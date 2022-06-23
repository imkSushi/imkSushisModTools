using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace imkSushisModSourceGenerator;

[Generator]
public class RecipeCreatorGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context)
    {
        context.RegisterForSyntaxNotifications(() => new RecipeSyntaxReciever());
    }

    public void Execute(GeneratorExecutionContext context)
    {
        var reciever = (RecipeSyntaxReciever)context.SyntaxReceiver!;
        var output = new StringBuilder(@"using System.Collections.Generic;
using System.IO;
using System.Linq;
using Terraria;
using Terraria.Map;
using Terraria.ModLoader;

namespace imkSushisMod;


public partial class RecipeCreator
{");
        
        foreach (var (name, singleStack) in reciever.MethodsToCreate)
        {
            var group = name.Skip(3).Select(c => c is 'g' or 'G').ToArray();
            output.AppendLine();
            output.Append($"    public void {name}(");
            for (var i = 0; i < singleStack.Length - 2; i++)
            {
                var gp = group.Length > i && group[i];
                output.Append((singleStack[i], gp) switch
                {
                    (true, true)   => $"int group{i + 1}, ",
                    (true, false)  => $"int ingredient{i + 1}, ",
                    (false, true)  => $"(int ingredient, int stack) group{i + 1}, ",
                    (false, false) => $"(int ingredient, int stack) ingredient{i + 1}, ",
                });
            }

            output.Append("int tile, ");
            output.Append(singleStack.Last() ? "int result" : "(int item, int stack) result");
            output.AppendLine(", bool format = FORMATRECIPES)");
            output.AppendLine("    {");
            output.Append("        New(new(int ingredient, int stack, bool group)[]{");
            for (var i = 0; i < singleStack.Length - 2; i++)
            {
                var gp = group.Length > i && group[i];
                output.Append((singleStack[i], gp) switch
                {
                    (true, true)   => $"(group{i+1}, 1, true), ",
                    (true, false)  => $"(ingredient{i+1}, 1, false), ",
                    (false, true)  => $"(group{i+1}.ingredient, group{i+1}.stack, true), ",
                    (false, false) => $"(ingredient{i+1}.ingredient, ingredient{i+1}.stack, false), "
                });
            }

            output.Append("}, new[]{tile}, ");
            output.Append(singleStack.Last() ? "(result, 1)" : "result");
            output.AppendLine(", format);");
            output.AppendLine("    }");
            output.AppendLine();
        }

        output.Append("}");
        var sourceText = SourceText.From(output.ToString(), Encoding.UTF8);
        context.AddSource("RecipeCreatorGenerator.Generated.cs", sourceText);
    }
}

public class RecipeSyntaxReciever : ISyntaxReceiver
{
    public HashSet<(string name, bool[] singleStack)> MethodsToCreate = new();

    public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
    {
        if (syntaxNode is InvocationExpressionSyntax { 
                Expression: MemberAccessExpressionSyntax { 
                    Expression: IdentifierNameSyntax
                    {
                        Identifier.ValueText: "recipe"
                    }
                } maes 
            } ies && maes.Name.Identifier.ValueText.StartsWith("New"))
        {
            var classNode = syntaxNode;
            while (classNode is not ClassDeclarationSyntax)
            {
                classNode = classNode.Parent;
                if (classNode == null)
                    return;
            }

            var cds = (ClassDeclarationSyntax)classNode;
            var passed = false;

            foreach (var attributeList in cds.AttributeLists)
            {
                foreach (var attribute in attributeList.Attributes)
                {
                    if (attribute.Name.ToString() == "GenerateRecipes")
                        passed = true;
                }
            }
            if (!passed)
                return;
            var name = maes.Name.Identifier.ValueText;
            var ending = name.Skip(3);
            if (!ending.All(c => c is 'g' or 'G' or 'n' or 'N'))
                return;
            var args = ies.ArgumentList.Arguments;
            var singleStack = args.Select(arg => arg.Expression is not TupleExpressionSyntax).ToArray();
            if (MethodsToCreate.Any(method => method.name == name && singleStack.SequenceEqual(method.singleStack)))
            {
                return;
            }
            MethodsToCreate.Add((maes.Name.Identifier.ValueText, singleStack));
        }
    }
}