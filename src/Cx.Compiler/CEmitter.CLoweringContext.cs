using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed record CLoweringContext(
        IReadOnlyDictionary<string, string> SymbolAliases,
        IReadOnlyList<string> ModuleQualifiers,
        IReadOnlyDictionary<string, string> MethodNames,
        IReadOnlyDictionary<string, string> MethodReceiverTypes,
        IReadOnlyDictionary<string, bool> MethodTakesPointerSelf,
        IReadOnlyList<GenericCallInfo> GenericCalls,
        IReadOnlySet<string> GenericMacroNames,
        IReadOnlySet<string> StaticMethodNames,
        IReadOnlyDictionary<string, StructNode> Structs,
        IReadOnlyDictionary<string, InterfaceNode> Interfaces,
        IReadOnlyDictionary<(string StructName, string InterfaceName), InterfaceImplementation> InterfaceImplementations,
        IReadOnlyDictionary<string, TaggedUnionNode> TaggedUnions,
        IReadOnlyDictionary<string, string> TagAliases,
        IReadOnlyDictionary<string, string> EnumMemberAliases,
        IReadOnlyDictionary<string, string> TypeAliases,
        IReadOnlyDictionary<string, AdapterExposeInfo> AdapterExposes,
        RequirementMatcher RequirementMatcher)
    {
        public bool IsGenericMacro(string name) =>
            GenericMacroNames.Contains(name);

        public GenericCallResolver CreateGenericCallResolver(
            Func<ExpressionNode, string?> resolveExpressionType,
            Func<string, string, bool> canAssign) =>
            new(GenericCalls, resolveExpressionType, canAssign);

        public RequirementLookup CreateRequirementLookup() =>
            new(RequirementMatcher);

        public bool TryGetMethod(string key, out CLoweringMethodInfo method)
        {
            if (!MethodNames.TryGetValue(key, out var cName))
            {
                method = null!;
                return false;
            }

            method = CreateMethodInfo(key, cName);
            return true;
        }

        public bool TryGetMethodTakesPointerSelf(string key, out bool takesPointerSelf)
        {
            if (MethodTakesPointerSelf.TryGetValue(key, out takesPointerSelf))
            {
                return true;
            }

            takesPointerSelf = false;
            return false;
        }

        public IEnumerable<CLoweringMethodInfo> GetInstanceMethodsForReceiver(string receiverType) =>
            MethodNames
                .Where(method =>
                    !StaticMethodNames.Contains(method.Key)
                    && (method.Key.StartsWith(receiverType + ".", StringComparison.Ordinal)
                        || MethodReceiverTypes.GetValueOrDefault(method.Key) == receiverType))
                .Select(method => CreateMethodInfo(method.Key, method.Value));

        public IEnumerable<CLoweringMethodInfo> GetMethods() =>
            MethodNames.Select(method => CreateMethodInfo(method.Key, method.Value));

        public bool IsTaggedUnion(string name) =>
            TaggedUnions.ContainsKey(name);

        public bool TryGetTaggedUnion(string name, out TaggedUnionNode taggedUnion) =>
            TaggedUnions.TryGetValue(name, out taggedUnion!);

        public IEnumerable<TaggedUnionNode> GetTaggedUnions() =>
            TaggedUnions.Values;

        public bool TryGetTaggedUnionTagAlias(string source, out string target) =>
            TagAliases.TryGetValue(source, out target!);

        public IEnumerable<(string Source, string Target)> GetTaggedUnionTagAliases() =>
            TagAliases.Select(alias => (alias.Key, alias.Value));

        public bool TryGetTaggedUnionVariant(
            string unionName,
            string variantName,
            out TaggedUnionNode taggedUnion,
            out TaggedUnionVariantNode variant)
        {
            if (!TaggedUnions.TryGetValue(unionName, out taggedUnion!)
                || taggedUnion.Variants.FirstOrDefault(candidate => candidate.Name == variantName) is not { } foundVariant)
            {
                variant = null!;
                return false;
            }

            variant = foundVariant;
            return true;
        }

        public bool IsInterface(string name) =>
            Interfaces.ContainsKey(name);

        public bool TryGetInterface(string name, out InterfaceNode interfaceNode) =>
            Interfaces.TryGetValue(name, out interfaceNode!);

        public IEnumerable<InterfaceNode> GetInterfaces() =>
            Interfaces.Values;

        public IEnumerable<string> GetInterfaceNames() =>
            Interfaces.Keys;

        public bool InterfaceHasMethod(string interfaceName, string methodName) =>
            Interfaces.TryGetValue(interfaceName, out var interfaceNode)
            && interfaceNode.Methods.Any(method => method.Name == methodName);

        public bool HasInterfaceImplementation(string structName, string interfaceName) =>
            InterfaceImplementations.ContainsKey((structName, interfaceName));

        public IReadOnlyDictionary<string, InterfaceImplementation> GetInterfaceImplementationsByStruct(string interfaceName) =>
            InterfaceImplementations.Values
                .Where(implementation => implementation.Interface.Name == interfaceName)
                .ToDictionary(implementation => implementation.Struct.Name, StringComparer.Ordinal);

        public bool TryGetAdapterExpose(string key, out AdapterExposeInfo expose) =>
            AdapterExposes.TryGetValue(key, out expose!);

        public IEnumerable<AdapterExposeInfo> GetInstanceAdapterExposes(string adapterName) =>
            AdapterExposes.Values.Where(expose =>
                !expose.IsStatic
                && string.Equals(expose.AdapterName, adapterName, StringComparison.Ordinal));

        public IEnumerable<AdapterExposeInfo> GetInstanceAdapterExposes() =>
            AdapterExposes.Values.Where(expose => !expose.IsStatic);

        public bool TryGetStruct(string name, out StructNode structNode) =>
            Structs.TryGetValue(name, out structNode!);

        public IEnumerable<StructNode> GetStructs() =>
            Structs.Values;

        public IEnumerable<string> GetStructNames() =>
            Structs.Keys;

        public bool TryResolveSymbolAlias(string name, out string original) =>
            SymbolAliases.TryGetValue(name, out original!);

        public IEnumerable<(string Alias, string Original)> GetSymbolAliases() =>
            SymbolAliases.Select(alias => (alias.Key, alias.Value));

        public bool IsModuleQualifierTarget(string target) =>
            ModuleQualifiers.Any(qualifier => string.Equals(qualifier, target + ".", StringComparison.Ordinal));

        public IEnumerable<string> GetModuleQualifiers() =>
            ModuleQualifiers;

        public bool TryGetEnumMemberAlias(string source, out string target) =>
            EnumMemberAliases.TryGetValue(source, out target!);

        public IEnumerable<(string Source, string Target)> GetEnumMemberAliases() =>
            EnumMemberAliases.Select(alias => (alias.Key, alias.Value));

        public string ResolveTypeAlias(string type)
        {
            var isPointer = type.EndsWith("*", StringComparison.Ordinal);
            var coreType = isPointer ? type.TrimEnd('*').TrimEnd() : type;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            while (TypeAliases.TryGetValue(coreType, out var targetType) && seen.Add(coreType))
            {
                coreType = targetType;
            }

            return isPointer ? coreType + "*" : coreType;
        }

        public static CLoweringContext Create(
            ProgramNode program,
            IReadOnlyList<StructNode> concreteStructs,
            RequirementMatcher requirementMatcher) =>
            new(
                program.SymbolImports
                    .SelectMany(import => import.Symbols)
                    .Where(symbol => symbol.Alias is not null)
                    .ToDictionary(symbol => symbol.Alias!, symbol => symbol.Name, StringComparer.Ordinal),
                program.Imports
                    .Select(import => import.Alias ?? import.ModuleName)
                    .OrderByDescending(name => name.Length)
                    .Select(name => name + ".")
                    .ToList(),
                program.Functions
                    .Where(function => function.OwnerType is not null && function.TypeArguments.Count == 0)
                    .ToDictionary(
                        function => $"{function.OwnerType}.{function.Name}",
                        GetCFunctionName,
                        StringComparer.Ordinal),
                program.Functions
                    .Where(function => function.OwnerType is not null)
                    .Where(function => function.TypeArguments.Count == 0)
                    .ToDictionary(
                        function => $"{function.OwnerType}.{function.Name}",
                        function => NormalizeType(SubstituteSelfType(function.Parameters.FirstOrDefault()?.Type ?? string.Empty, ResolveSelfType(function))),
                        StringComparer.Ordinal),
                program.Functions
                    .Where(function => function.OwnerType is not null)
                    .Where(function => function.TypeArguments.Count == 0)
                    .ToDictionary(
                        function => $"{function.OwnerType}.{function.Name}",
                        function => function.Parameters.FirstOrDefault()?.Type.EndsWith("*", StringComparison.Ordinal) == true,
                        StringComparer.Ordinal),
                program.Functions
                    .Where(function => function.TypeArguments.Count > 0)
                    .Select(function => new GenericCallInfo(
                        function.OwnerType,
                        function.Name,
                        function.TypeArguments,
                        function.Parameters.Where(parameter => !parameter.IsVariadic).Select(parameter => parameter.Type).ToList(),
                        function.ReturnType,
                        GetCFunctionName(function),
                        function.Parameters.FirstOrDefault()?.Type.EndsWith("*", StringComparison.Ordinal) == true,
                        function.IsStatic))
                    .ToList(),
                program.ExternFunctions
                    .Where(function => function.IsMacro)
                    .Select(function => function.Name)
                    .ToHashSet(StringComparer.Ordinal),
                program.Functions
                    .Where(function => function.OwnerType is not null && function.IsStatic)
                    .Select(function => $"{function.OwnerType}.{function.Name}")
                    .ToHashSet(StringComparer.Ordinal),
                concreteStructs.ToDictionary(structNode => structNode.Name, StringComparer.Ordinal),
                program.Interfaces.ToDictionary(interfaceNode => interfaceNode.Name, StringComparer.Ordinal),
                GetInterfaceImplementations(program, concreteStructs)
                    .ToDictionary(
                        implementation => (implementation.Struct.Name, implementation.Interface.Name),
                        implementation => implementation),
                program.TaggedUnions.ToDictionary(taggedUnion => taggedUnion.Name, StringComparer.Ordinal),
                program.TaggedUnions
                    .Where(union => !union.IsRaw)
                    .SelectMany(taggedUnion => taggedUnion.Variants.Select(variant => new
                    {
                        Source = $"{taggedUnion.Name}.{variant.Name}",
                        Target = $"{taggedUnion.Name}_Tag_{variant.Name}",
                    }))
                    .ToDictionary(item => item.Source, item => item.Target, StringComparer.Ordinal),
                program.Enums
                    .SelectMany(enumNode => enumNode.Members.Select(member => new
                    {
                        Source = $"{enumNode.Name}.{member.Name}",
                        Target = member.Name,
                    }))
                    .ToDictionary(item => item.Source, item => item.Target, StringComparer.Ordinal),
                program.TypeAliases
                    .GroupBy(typeAlias => typeAlias.Name, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Last().TargetType, StringComparer.Ordinal),
                program.TypeAdapters
                    .SelectMany(adapter => adapter.ExposedMethods.Select(expose => new AdapterExposeInfo(
                        adapter.Name,
                        adapter.TypeParameters,
                        adapter.BaseType,
                        expose.IsStatic,
                        expose.SourceName,
                        expose.ExposedName,
                        expose.ReturnType)))
                    .GroupBy(expose => $"{expose.AdapterName}.{expose.ExposedName}", StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal),
                requirementMatcher);

        private CLoweringMethodInfo CreateMethodInfo(string key, string cName) =>
            new(
                key,
                key[(key.LastIndexOf('.') + 1)..],
                cName,
                MethodReceiverTypes.GetValueOrDefault(key),
                MethodTakesPointerSelf.GetValueOrDefault(key),
                StaticMethodNames.Contains(key));
    }

    private sealed record CLoweringMethodInfo(
        string Key,
        string Name,
        string CName,
        string? ReceiverType,
        bool TakesPointerSelf,
        bool IsStatic);
}
