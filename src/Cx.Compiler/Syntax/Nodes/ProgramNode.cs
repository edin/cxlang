namespace Cx.Compiler.Syntax.Nodes;

public abstract record TopLevelNode(Location Location) : SyntaxNode(Location);

public sealed record ProgramNode(
    Location Location,
    IReadOnlyList<TopLevelNode> Declarations) : SyntaxNode(Location)
{
    public ProgramNode(
        Location Location,
        ModuleDeclarationNode? Module,
        IReadOnlyList<ImportNode> Imports,
        IReadOnlyList<SymbolImportNode> SymbolImports,
        IReadOnlyList<IncludeNode> Includes,
        IReadOnlyList<CDeclareNode> CDeclarations,
        IReadOnlyList<ExternFunctionNode> ExternFunctions,
        IReadOnlyList<AttributeDeclarationNode> AttributeDeclarations,
        IReadOnlyList<TypeAliasNode> TypeAliases,
        IReadOnlyList<RequirementNode> Requirements,
        IReadOnlyList<EnumNode> Enums,
        IReadOnlyList<InterfaceNode> Interfaces,
        IReadOnlyList<StructNode> Structs,
        IReadOnlyList<TypeAdapterNode> TypeAdapters,
        IReadOnlyList<ExtensionNode> Extensions,
        IReadOnlyList<TaggedUnionNode> TaggedUnions,
        IReadOnlyList<GlobalVariableNode> GlobalVariables,
        IReadOnlyList<FunctionNode> Functions)
        : this(
            Location,
            BuildDeclarations(
                Module,
                Imports,
                SymbolImports,
                Includes,
                CDeclarations,
                ExternFunctions,
                AttributeDeclarations,
                TypeAliases,
                Requirements,
                Enums,
                Interfaces,
                Structs,
                TypeAdapters,
                Extensions,
                TaggedUnions,
                GlobalVariables,
                Functions))
    {
    }

    public ModuleDeclarationNode? Module
    {
        get => Declarations.OfType<ModuleDeclarationNode>().FirstOrDefault();
        init => Declarations = ReplaceSingle(Declarations, value);
    }

    public IReadOnlyList<ImportNode> Imports
    {
        get => Declarations.OfType<ImportNode>().ToList();
        init => Declarations = ReplaceAll(Declarations, value);
    }

    public IReadOnlyList<SymbolImportNode> SymbolImports
    {
        get => Declarations.OfType<SymbolImportNode>().ToList();
        init => Declarations = ReplaceAll(Declarations, value);
    }

    public IReadOnlyList<IncludeNode> Includes
    {
        get => Declarations.OfType<IncludeNode>().ToList();
        init => Declarations = ReplaceAll(Declarations, value);
    }

    public IReadOnlyList<CDeclareNode> CDeclarations
    {
        get => Declarations.OfType<CDeclareNode>().ToList();
        init => Declarations = ReplaceAll(Declarations, value);
    }

    public IReadOnlyList<ExternFunctionNode> ExternFunctions
    {
        get => Declarations.OfType<ExternFunctionNode>().ToList();
        init => Declarations = ReplaceAll(Declarations, value);
    }

    public IReadOnlyList<AttributeDeclarationNode> AttributeDeclarations
    {
        get => Declarations.OfType<AttributeDeclarationNode>().ToList();
        init => Declarations = ReplaceAll(Declarations, value);
    }

    public IReadOnlyList<TypeAliasNode> TypeAliases
    {
        get => Declarations.OfType<TypeAliasNode>().ToList();
        init => Declarations = ReplaceAll(Declarations, value);
    }

    public IReadOnlyList<RequirementNode> Requirements
    {
        get => Declarations.OfType<RequirementNode>().ToList();
        init => Declarations = ReplaceAll(Declarations, value);
    }

    public IReadOnlyList<EnumNode> Enums
    {
        get => Declarations.OfType<EnumNode>().ToList();
        init => Declarations = ReplaceAll(Declarations, value);
    }

    public IReadOnlyList<InterfaceNode> Interfaces
    {
        get => Declarations.OfType<InterfaceNode>().ToList();
        init => Declarations = ReplaceAll(Declarations, value);
    }

    public IReadOnlyList<StructNode> Structs
    {
        get => Declarations.OfType<StructNode>().ToList();
        init => Declarations = ReplaceAll(Declarations, value);
    }

    public IReadOnlyList<TypeAdapterNode> TypeAdapters
    {
        get => Declarations.OfType<TypeAdapterNode>().ToList();
        init => Declarations = ReplaceAll(Declarations, value);
    }

    public IReadOnlyList<ExtensionNode> Extensions
    {
        get => Declarations.OfType<ExtensionNode>().ToList();
        init => Declarations = ReplaceAll(Declarations, value);
    }

    public IReadOnlyList<TaggedUnionNode> TaggedUnions
    {
        get => Declarations.OfType<TaggedUnionNode>().ToList();
        init => Declarations = ReplaceAll(Declarations, value);
    }

    public IReadOnlyList<GlobalVariableNode> GlobalVariables
    {
        get => Declarations.OfType<GlobalVariableNode>().ToList();
        init => Declarations = ReplaceAll(Declarations, value);
    }

    public IReadOnlyList<FunctionNode> Functions
    {
        get => Declarations.OfType<FunctionNode>().ToList();
        init => Declarations = ReplaceAll(Declarations, value);
    }

    public IReadOnlyList<TestNode> Tests
    {
        get => Declarations.OfType<TestNode>().ToList();
        init => Declarations = ReplaceAll(Declarations, value);
    }

    public static IReadOnlyList<TopLevelNode> BuildDeclarations(
        ModuleDeclarationNode? module,
        IReadOnlyList<ImportNode> imports,
        IReadOnlyList<SymbolImportNode> symbolImports,
        IReadOnlyList<IncludeNode> includes,
        IReadOnlyList<CDeclareNode> cDeclarations,
        IReadOnlyList<ExternFunctionNode> externFunctions,
        IReadOnlyList<AttributeDeclarationNode> attributeDeclarations,
        IReadOnlyList<TypeAliasNode> typeAliases,
        IReadOnlyList<RequirementNode> requirements,
        IReadOnlyList<EnumNode> enums,
        IReadOnlyList<InterfaceNode> interfaces,
        IReadOnlyList<StructNode> structs,
        IReadOnlyList<TypeAdapterNode> typeAdapters,
        IReadOnlyList<ExtensionNode> extensions,
        IReadOnlyList<TaggedUnionNode> taggedUnions,
        IReadOnlyList<GlobalVariableNode> globalVariables,
        IReadOnlyList<FunctionNode> functions)
    {
        var declarations = new List<TopLevelNode>();
        if (module is not null)
        {
            declarations.Add(module);
        }

        declarations.AddRange(imports);
        declarations.AddRange(symbolImports);
        declarations.AddRange(includes);
        declarations.AddRange(cDeclarations);
        declarations.AddRange(externFunctions);
        declarations.AddRange(attributeDeclarations);
        declarations.AddRange(typeAliases);
        declarations.AddRange(requirements);
        declarations.AddRange(enums);
        declarations.AddRange(interfaces);
        declarations.AddRange(structs);
        declarations.AddRange(typeAdapters);
        declarations.AddRange(extensions);
        declarations.AddRange(taggedUnions);
        declarations.AddRange(globalVariables);
        declarations.AddRange(functions);
        return declarations;
    }

    private static IReadOnlyList<TopLevelNode> ReplaceSingle<T>(
        IReadOnlyList<TopLevelNode> declarations,
        T? replacement)
        where T : TopLevelNode
    {
        var replacements = replacement is null ? [] : new[] { replacement };
        return ReplaceAll(declarations, replacements);
    }

    private static IReadOnlyList<TopLevelNode> ReplaceAll<T>(
        IReadOnlyList<TopLevelNode> declarations,
        IEnumerable<T> replacements)
        where T : TopLevelNode
    {
        var replacementList = replacements.Cast<TopLevelNode>().ToList();
        var result = new List<TopLevelNode>();
        var inserted = false;

        foreach (var declaration in declarations)
        {
            if (declaration is T)
            {
                if (!inserted)
                {
                    result.AddRange(replacementList);
                    inserted = true;
                }

                continue;
            }

            result.Add(declaration);
        }

        if (!inserted)
        {
            result.AddRange(replacementList);
        }

        return result;
    }
}

public sealed record GlobalVariableNode(
    Location Location,
    bool IsConst,
    string Name,
    ExpressionNode? Initializer,
    IReadOnlyList<AttributeApplicationNode> Attributes,
    bool IsHeaderDeclaration = false,
    bool IsMacro = false,
    TypeNode? TypeNode = null) : TopLevelNode(Location)
{
    [Obsolete("Use TypeNode instead of the string compatibility property.")]
    public string Type => TypeNode?.TypeName ?? string.Empty;
}

public sealed record ExtensionNode(
    Location Location,
    IReadOnlyList<string> TypeParameters,
    IReadOnlyList<GenericConstraintNode> GenericConstraints,
    IReadOnlyList<FunctionNode> Methods,
    IReadOnlyList<AttributeApplicationNode> Attributes,
    TypeNode? TargetTypeNode = null) : TopLevelNode(Location)
{
    [Obsolete("Use TargetTypeNode instead of the string compatibility property.")]
    public string TargetType => TargetTypeNode?.TypeName ?? string.Empty;
}

public sealed record TypeAdapterNode(
    Location Location,
    string Name,
    IReadOnlyList<string> TypeParameters,
    IReadOnlyList<ExposeMethodNode> ExposedMethods,
    IReadOnlyList<FunctionNode> Methods,
    IReadOnlyList<AttributeApplicationNode> Attributes,
    TypeNode? BaseTypeNode = null) : TopLevelNode(Location)
{
    [Obsolete("Use BaseTypeNode instead of the string compatibility property.")]
    public string BaseType => BaseTypeNode?.TypeName ?? string.Empty;
}

public sealed record ExposeMethodNode(
    Location Location,
    bool IsStatic,
    string SourceName,
    string ExposedName,
    TypeNode? ReturnTypeNode = null) : SyntaxNode(Location)
{
    [Obsolete("Use ReturnTypeNode instead of the string compatibility property.")]
    public string? ReturnType => ReturnTypeNode?.TypeName;
}

public sealed record TestNode(
    Location Location,
    string Name,
    IReadOnlyList<StatementNode> Body,
    IReadOnlyList<AttributeApplicationNode> Attributes) : TopLevelNode(Location);
