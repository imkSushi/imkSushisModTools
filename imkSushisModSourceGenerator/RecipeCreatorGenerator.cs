using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace imkSushisModSourceGenerator;

[Generator]
public class RecipeCreatorGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var recipeMethods = context.SyntaxProvider
            .CreateSyntaxProvider(Predicate, Transform).Where(node => node is not null);

        var allMethods = recipeMethods.Collect();
        
        context.RegisterSourceOutput(allMethods, Execute!);
    }

    public static bool Predicate(SyntaxNode node, CancellationToken cancellationToken)
    {
        return node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax
                {
                    Identifier.ValueText: "recipe"
                }
            } maes
        } && maes.Name.Identifier.ValueText.StartsWith("New");
    }
    
    public static InvocationExpressionSyntax? Transform(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        var classNode = GetParentClass(context);
        if (classNode is null)
            return null;

        if (GetAllAttributes(classNode).Any(attribute => CheckIfHasGeneratesRecipeAttribute(context, attribute)))
            return (InvocationExpressionSyntax)context.Node;

        return null;
    }

    private static ClassDeclarationSyntax? GetParentClass(GeneratorSyntaxContext context)
    {
        return context.Node.AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault();
    }

    private static IEnumerable<AttributeSyntax> GetAllAttributes(ClassDeclarationSyntax classNode)
    {
        return classNode.AttributeLists.SelectMany(list => list.Attributes);
    }

    private static bool CheckIfHasGeneratesRecipeAttribute(GeneratorSyntaxContext context,
        AttributeSyntax attribute)
    {
        if (context.SemanticModel.GetSymbolInfo(attribute).Symbol is not IMethodSymbol attributeSymbol)
            return false;

        var attributeContainingTypeSymbol = attributeSymbol.ContainingType;
        var fullName = attributeContainingTypeSymbol.ToDisplayString();

        return fullName == "imkSushisMod.GenerateRecipesAttribute";
    }

    public static void Execute(SourceProductionContext spc, ImmutableArray<InvocationExpressionSyntax> nodes)
    {
        var methodsToCreate = CalculateWhichMethodsToCreate(nodes);

        var output = new StringBuilder("""
                                       using System.Collections.Generic;
                                       using System.IO;
                                       using System.Linq;
                                       using Terraria;
                                       using Terraria.Map;
                                       using Terraria.ModLoader;

                                       namespace imkSushisMod;

                                       public partial class RecipeCreator
                                       {

                                       """);
        
        foreach (var (name, singleStack) in methodsToCreate) 
            GenerateMethod(name, output, singleStack);

        output.AppendLine("}");
        
        spc.AddSource("RecipeCreator.g.cs", output.ToString());
    }

    private static void GenerateMethod(string name, StringBuilder output, bool[] singleStack)
    {
        var singleStackAndGroup = GenerateSingleStackAndGroupArray(name, singleStack);

        output.Append($"""
                       
                           public void {name}(
                       """);
        for (var i = 0; i < singleStack.Length - 2; i++)
        {
            output.Append(singleStackAndGroup[i] switch
            {
                (true, true)   => $"int group{i + 1}, ",
                (true, false)  => $"int ingredient{i + 1}, ",
                (false, true)  => $"(int ingredient, int stack) group{i + 1}, ",
                (false, false) => $"(int ingredient, int stack) ingredient{i + 1}, ",
            });
        }

        var outputItemParameterType = (singleStack.Last() ? "int" : "(int item, int stack)");
        output.Append($$"""
                        int tile, {{outputItemParameterType}} result, bool format = FORMATRECIPES)
                            {
                                New(new (int ingredient, int stack, bool group)[] {
                        """);

        for (var i = 0; i < singleStack.Length - 2; i++)
        {
            output.Append(singleStackAndGroup[i] switch
            {
                (true, true)   => $"(group{i + 1}, 1, true), ",
                (true, false)  => $"(ingredient{i + 1}, 1, false), ",
                (false, true)  => $"(group{i + 1}.ingredient, group{i + 1}.stack, true), ",
                (false, false) => $"(ingredient{i + 1}.ingredient, ingredient{i + 1}.stack, false), "
            });
        }

        output.AppendLine($$"""
                            }, new[] {tile}, {{(singleStack[^1] ? "(result, 1)" : "result")}}, format);
                                }
                            """);
    }

    private static (bool singleStack, bool group)[] GenerateSingleStackAndGroupArray(string name, bool[] singleStack)
    {
        var group = WhichArgumentsAreOfGroups(name);
        var singleStackAndGroup = new (bool singleStack, bool group)[singleStack.Length - 2];
        for (var i = 0; i < group.Length; i++)
            singleStackAndGroup[i] = (singleStack[i], group[i]);
        for (var i = group.Length; i < singleStack.Length - 2; i++)
            singleStackAndGroup[i] = (singleStack[i], false);
        
        return singleStackAndGroup;
    }

    private static bool[] WhichArgumentsAreOfGroups(string name)
    {
        return name[3..].Select(c => c is 'g' or 'G').ToArray();
    }

    private static HashSet<(string name, bool[] singleStack)> CalculateWhichMethodsToCreate(ImmutableArray<InvocationExpressionSyntax> nodes)
    {
        var methodsToCreate = new HashSet<(string name, bool[] singleStack)>();

        foreach (var ies in nodes) 
            RegisterMethodForCreation(ies, methodsToCreate);

        return methodsToCreate;
    }

    private static void RegisterMethodForCreation(InvocationExpressionSyntax ies, HashSet<(string name, bool[] singleStack)> methodsToCreate)
    {
        var maes = (MemberAccessExpressionSyntax)ies.Expression;

        var name = maes.Name.Identifier.ValueText;
        var ending = name[3..];
        if (!ending.All(c => c is 'g' or 'G' or 'n' or 'N'))
            return;

        var args = ies.ArgumentList.Arguments;
        var singleStack = WhichArgumentsAreSingleStack(args);
        
        if (IsMethodAlreadyRegistered(methodsToCreate, name, singleStack))
            return;

        methodsToCreate.Add((maes.Name.Identifier.ValueText, singleStack));
    }

    private static bool[] WhichArgumentsAreSingleStack(SeparatedSyntaxList<ArgumentSyntax> args)
    {
        return args.Select(arg => arg.Expression is not TupleExpressionSyntax).ToArray();
    }

    private static bool IsMethodAlreadyRegistered(HashSet<(string name, bool[] singleStack)> methodsToCreate, string name, bool[] singleStack)
    {
        return methodsToCreate.Any(method => method.name == name && singleStack.SequenceEqual(method.singleStack));
    }
}