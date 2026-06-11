using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Semantic;

internal abstract record TypeSymbol(string Name)
{
    public sealed record Builtin(string Name) : TypeSymbol(Name);

    public sealed record GenericParameter(string Name) : TypeSymbol(Name);

    public sealed record Alias(string Name, TypeAliasNode Declaration) : TypeSymbol(Name);

    public sealed record Struct(string Name, StructNode Declaration) : TypeSymbol(Name);

    public sealed record Adapter(string Name, TypeAdapterNode Declaration) : TypeSymbol(Name);

    public sealed record Interface(string Name, InterfaceNode Declaration) : TypeSymbol(Name);

    public sealed record Enum(string Name, EnumNode Declaration) : TypeSymbol(Name);

    public sealed record TaggedUnion(string Name, TaggedUnionNode Declaration) : TypeSymbol(Name);
}

internal sealed record ResolvedType(
    TypeRef Type,
    TypeSymbol? Symbol,
    IReadOnlyDictionary<string, TypeRef> Substitutions)
{
    public string DisplayName => TypeRefFormatter.ToCxString(Type);

    public bool IsUnknown => Type is TypeRef.Unknown;
}

internal sealed record ResolvedField(
    string Name,
    TypeRef Type,
    StructFieldNode Declaration);

internal enum ResolvedMethodKind
{
    Direct,
    Exposed,
}

internal abstract record ResolvedMethodTarget
{
    public abstract FunctionNode Function { get; }

    public sealed record Direct(FunctionNode DirectFunction) : ResolvedMethodTarget
    {
        public override FunctionNode Function => DirectFunction;
    }

    public sealed record Exposed(
        TypeAdapterNode Adapter,
        ExposeMethodNode Expose,
        ResolvedMethod InnerMethod) : ResolvedMethodTarget
    {
        public override FunctionNode Function => InnerMethod.Declaration;
    }
}

internal sealed record ResolvedMethod(
    string Name,
    TypeRef OwnerType,
    TypeRef ReturnType,
    IReadOnlyList<TypeRef> ParameterTypes,
    ResolvedMethodTarget Target)
{
    public FunctionNode Declaration => Target.Function;

    public ResolvedMethod DirectMethod =>
        Target is ResolvedMethodTarget.Exposed exposed
            ? exposed.InnerMethod.DirectMethod
            : this;

    public ResolvedMethodKind Kind =>
        Target is ResolvedMethodTarget.Exposed
            ? ResolvedMethodKind.Exposed
            : ResolvedMethodKind.Direct;
}

internal sealed class ResolvedTypeMemberResolver(ProgramNode program)
{
    private readonly TypeRefParser _parser = new(program);
    private readonly Lazy<RequirementMatcher> _requirementMatcher = new(() => new RequirementMatcher(program));

    public IReadOnlyList<ResolvedField> GetFields(ResolvedType type) =>
        type.Symbol switch
        {
            TypeSymbol.Struct structSymbol => ResolveFields(structSymbol.Declaration, type.Substitutions),
            _ => [],
        };

    public IReadOnlyList<ResolvedMethod> GetMethods(ResolvedType type) =>
        type.Symbol switch
        {
            TypeSymbol.Struct structSymbol => ResolveStructMethods(structSymbol.Declaration, type),
            TypeSymbol.Adapter adapterSymbol => ResolveAdapterMethods(adapterSymbol.Declaration, type),
            TypeSymbol.Builtin or TypeSymbol.Enum or TypeSymbol.Interface or TypeSymbol.TaggedUnion or null => ResolveOwnerFunctions(type),
            _ => [],
        };

    private IReadOnlyList<ResolvedField> ResolveFields(
        StructNode declaration,
        IReadOnlyDictionary<string, TypeRef> substitutions) =>
        declaration.Fields
            .Select(field =>
            {
                var fieldType = field.TypeNode.ToTypeRef(_parser);
                return new ResolvedField(
                    field.Name,
                    TypeRefRewriter.Substitute(fieldType, substitutions),
                    field);
            })
            .ToList();

    private IReadOnlyList<ResolvedMethod> ResolveStructMethods(
        StructNode declaration,
        ResolvedType type)
    {
        var methods = declaration.Methods
            .Concat(program.Extensions
                .Where(extension =>
                    string.Equals(extension.TargetTypeNode.ToTypeName(), declaration.Name, StringComparison.Ordinal)
                    && ExtensionConstraintsSatisfied(extension, type))
                .SelectMany(extension => extension.Methods))
            .Concat(program.Functions.Where(function =>
                function.OwnerTypeNode.ToTypeNameOrNull() is not null
                && string.Equals(function.OwnerTypeNode.ToTypeName(), declaration.Name, StringComparison.Ordinal)));
        return methods
            .Select(method => ResolveMethod(method, type.Type, type.Substitutions))
            .ToList();
    }

    private IReadOnlyList<ResolvedMethod> ResolveOwnerFunctions(ResolvedType type)
    {
        var ownerName = GetOwnerName(type.Type);
        if (ownerName is null)
        {
            return [];
        }

        return program.Functions
            .Where(function =>
                function.OwnerTypeNode.ToTypeNameOrNull() is not null
                && string.Equals(function.OwnerTypeNode.ToTypeName(), ownerName, StringComparison.Ordinal))
            .Select(method => ResolveMethod(method, type.Type, type.Substitutions))
            .ToList();
    }

    private IReadOnlyList<ResolvedMethod> ResolveAdapterMethods(
        TypeAdapterNode declaration,
        ResolvedType type)
    {
        var ownMethods = declaration.Methods
            .Select(method => ResolveMethod(method, type.Type, type.Substitutions))
            .ToList();
        var exposedMethods = ResolveAdapterExposedMethods(declaration, type);
        return ownMethods.Concat(exposedMethods).ToList();
    }

    private IReadOnlyList<ResolvedMethod> ResolveAdapterExposedMethods(
        TypeAdapterNode declaration,
        ResolvedType type)
    {
        var baseType = ResolveAdapterBaseType(declaration, type.Substitutions);
        var baseResolvedType = new TypeResolver(program).ResolveDefinition(baseType);
        var baseMethods = GetMethods(baseResolvedType);
        var selfType = type.Type;
        var exposed = new List<ResolvedMethod>();

        foreach (var expose in declaration.ExposedMethods)
        {
            var baseMethod = baseMethods.FirstOrDefault(method =>
                method.Declaration.IsStatic == expose.IsStatic
                && string.Equals(method.Name, expose.SourceName, StringComparison.Ordinal));
            if (baseMethod is null)
            {
                continue;
            }

            var returnType = expose.ReturnTypeNode.ToTypeNameOrNull() is null
                ? baseMethod.ReturnType
                : expose.ReturnTypeNode.ToTypeRef(_parser);
            returnType = TypeRefRewriter.Substitute(returnType, type.Substitutions);
            returnType = TypeRefRewriter.SubstituteSelf(returnType, selfType);

            var parameterTypes = baseMethod.ParameterTypes.ToList();
            if (!expose.IsStatic && parameterTypes.Count > 0)
            {
                parameterTypes[0] = new TypeRef.Pointer(selfType);
            }

            exposed.Add(new ResolvedMethod(
                expose.ExposedName,
                type.Type,
                returnType,
                parameterTypes,
                new ResolvedMethodTarget.Exposed(declaration, expose, baseMethod)));
        }

        return exposed;
    }

    private ResolvedMethod ResolveMethod(
        FunctionNode method,
        TypeRef ownerType,
        IReadOnlyDictionary<string, TypeRef> substitutions)
    {
        var returnType = method.ReturnTypeNode.ToTypeRef(_parser);
        var parameterTypes = method.Parameters
            .Where(parameter => !parameter.IsVariadic)
            .Select(parameter => parameter.TypeNode.ToTypeRef(_parser))
            .Select(parameterType => TypeRefRewriter.Substitute(parameterType, substitutions))
            .ToList();
        return new ResolvedMethod(
            method.Name,
            ownerType,
            TypeRefRewriter.Substitute(returnType, substitutions),
            parameterTypes,
            new ResolvedMethodTarget.Direct(method));
    }

    private bool ExtensionConstraintsSatisfied(
        ExtensionNode extension,
        ResolvedType type)
    {
        if (extension.TypeParameters.Count != type.Substitutions.Count)
        {
            return extension.GenericConstraints.Count == 0;
        }

        var substitutions = type.Substitutions
            .ToDictionary(
                pair => pair.Key,
                pair => TypeRefFormatter.ToCxString(pair.Value),
                StringComparer.Ordinal);
        foreach (var constraint in extension.GenericConstraints)
        {
            if (!substitutions.TryGetValue(constraint.TypeParameter, out var concreteType))
            {
                return false;
            }

            foreach (var requirement in constraint.Requirements)
            {
                var arguments = requirement.TypeArgumentNodes
                    .Select(argument => GenericTypeStringRewriter.Substitute(argument.ToTypeName(), substitutions))
                    .ToList();
                if (!_requirementMatcher.Value.Match(concreteType, requirement.Name, arguments).Success)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private TypeRef ResolveAdapterBaseType(
        TypeAdapterNode declaration,
        IReadOnlyDictionary<string, TypeRef> substitutions)
    {
        var baseType = declaration.Semantic.Type ?? declaration.BaseTypeNode.ToTypeRef(_parser);
        return TypeRefRewriter.Substitute(baseType, substitutions);
    }

    private static string? GetOwnerName(TypeRef type) =>
        type switch
        {
            TypeRef.Alias alias => GetOwnerName(alias.Target),
            TypeRef.Named named => named.Name,
            _ => null,
        };
}

internal sealed class TypeResolver(
    ProgramNode program,
    IReadOnlyList<string>? genericParameters = null)
{
    private readonly IReadOnlySet<string> _genericParameters = (genericParameters ?? [])
        .ToHashSet(StringComparer.Ordinal);

    public ResolvedType Resolve(string? type)
    {
        var parser = new TypeRefParser(program);
        return Resolve(parser.Parse(type));
    }

    public ResolvedType Resolve(TypeRef type) =>
        type switch
        {
            TypeRef.Alias alias => ResolveAlias(alias),
            TypeRef.Named named => ResolveNamed(named),
            TypeRef.Pointer pointer => ResolveContainer(pointer),
            TypeRef.FixedArray fixedArray => ResolveContainer(fixedArray),
            TypeRef.Function function => ResolveContainer(function),
            TypeRef.Null or TypeRef.Unknown => new ResolvedType(type, Symbol: null, Substitutions: EmptySubstitutions()),
            _ => new ResolvedType(type, Symbol: null, Substitutions: EmptySubstitutions()),
        };

    public ResolvedType ResolveDefinition(TypeRef type)
    {
        var resolved = Resolve(type);
        return resolved.Symbol is TypeSymbol.Alias && type is TypeRef.Alias alias
            ? ResolveDefinition(alias.Target)
            : resolved;
    }

    private ResolvedType ResolveAlias(TypeRef.Alias alias)
    {
        var declaration = FindTypeAlias(alias.Name);
        var symbol = declaration is null
            ? null
            : new TypeSymbol.Alias(alias.Name, declaration);
        return new ResolvedType(alias, symbol, EmptySubstitutions());
    }

    private ResolvedType ResolveNamed(TypeRef.Named named)
    {
        if (_genericParameters.Contains(named.Name))
        {
            return new ResolvedType(named, new TypeSymbol.GenericParameter(named.Name), EmptySubstitutions());
        }

        if (FindStruct(named.Name) is { } structNode)
        {
            return new ResolvedType(
                named,
                new TypeSymbol.Struct(named.Name, structNode),
                BuildSubstitutions(structNode.TypeParameters, named.Arguments));
        }

        if (FindTypeAdapter(named.Name) is { } adapter)
        {
            return new ResolvedType(
                named,
                new TypeSymbol.Adapter(named.Name, adapter),
                BuildSubstitutions(adapter.TypeParameters, named.Arguments));
        }

        if (FindInterface(named.Name) is { } interfaceNode)
        {
            return new ResolvedType(named, new TypeSymbol.Interface(named.Name, interfaceNode), EmptySubstitutions());
        }

        if (FindTaggedUnion(named.Name) is { } taggedUnion)
        {
            return new ResolvedType(named, new TypeSymbol.TaggedUnion(named.Name, taggedUnion), EmptySubstitutions());
        }

        if (FindEnum(named.Name) is { } enumNode)
        {
            return new ResolvedType(named, new TypeSymbol.Enum(named.Name, enumNode), EmptySubstitutions());
        }

        if (FindTypeAlias(named.Name) is { } alias)
        {
            return new ResolvedType(named, new TypeSymbol.Alias(named.Name, alias), EmptySubstitutions());
        }

        return new ResolvedType(
            named,
            BuiltinTypes.IsBuiltin(named.Name) ? new TypeSymbol.Builtin(named.Name) : null,
            EmptySubstitutions());
    }

    private static ResolvedType ResolveContainer(TypeRef type) =>
        new(type, Symbol: null, Substitutions: EmptySubstitutions());

    private TypeAliasNode? FindTypeAlias(string name) =>
        program.TypeAliases.FirstOrDefault(alias => string.Equals(alias.Name, name, StringComparison.Ordinal))
        ?? program.CDeclarations
            .SelectMany(declaration => declaration.TypeAliases)
            .FirstOrDefault(alias => string.Equals(alias.Name, name, StringComparison.Ordinal));

    private StructNode? FindStruct(string name) =>
        program.Structs.FirstOrDefault(structNode => string.Equals(structNode.Name, name, StringComparison.Ordinal))
        ?? program.CDeclarations
            .SelectMany(declaration => declaration.Structs)
            .FirstOrDefault(structNode => string.Equals(structNode.Name, name, StringComparison.Ordinal));

    private TypeAdapterNode? FindTypeAdapter(string name) =>
        program.TypeAdapters.FirstOrDefault(adapter => string.Equals(adapter.Name, name, StringComparison.Ordinal));

    private InterfaceNode? FindInterface(string name) =>
        program.Interfaces.FirstOrDefault(interfaceNode => string.Equals(interfaceNode.Name, name, StringComparison.Ordinal));

    private TaggedUnionNode? FindTaggedUnion(string name) =>
        program.TaggedUnions.FirstOrDefault(union => string.Equals(union.Name, name, StringComparison.Ordinal))
        ?? program.CDeclarations
            .SelectMany(declaration => declaration.Unions)
            .FirstOrDefault(union => string.Equals(union.Name, name, StringComparison.Ordinal));

    private EnumNode? FindEnum(string name) =>
        program.Enums.FirstOrDefault(enumNode => string.Equals(enumNode.Name, name, StringComparison.Ordinal))
        ?? program.CDeclarations
            .SelectMany(declaration => declaration.Enums)
            .FirstOrDefault(enumNode => string.Equals(enumNode.Name, name, StringComparison.Ordinal));

    private static IReadOnlyDictionary<string, TypeRef> BuildSubstitutions(
        IReadOnlyList<string> parameters,
        IReadOnlyList<TypeRef> arguments)
    {
        if (parameters.Count == 0 || parameters.Count != arguments.Count)
        {
            return EmptySubstitutions();
        }

        return parameters
            .Zip(arguments)
            .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, TypeRef> EmptySubstitutions() =>
        new Dictionary<string, TypeRef>(StringComparer.Ordinal);
}
