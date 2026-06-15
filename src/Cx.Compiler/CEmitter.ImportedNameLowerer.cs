using System.Text.RegularExpressions;
using Cx.Compiler.C;
using Cx.Compiler.Lowering;
using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed class ImportedNameLowerer : ICExpressionLoweringContext
    {
        private readonly CLoweringContext _context;
        private readonly CLoweringScope _scope;
        private readonly CExpressionEmitter _expressionEmitter = new();
        private readonly CExpressionLoweringPipeline _expressionLoweringPipeline;
        private readonly GenericCallResolver _genericCallResolver;
        private readonly InterfaceValueBuilder _interfaceValueBuilder;
        private readonly TaggedUnionValueBuilder _taggedUnionValueBuilder;
        private readonly StructValueBuilder _structValueBuilder;
        private readonly AdapterExposeResolver _adapterExposeResolver;
        private readonly ReceiverExpressionBuilder _receiverExpressionBuilder;
        private readonly MemberAccessLowerer _memberAccessLowerer;
        private readonly MemberCallLowerer _memberCallLowerer;
        private readonly ExpressionTypeLoweringResolver _expressionTypeResolver;
        private readonly NameExpressionLowerer _nameExpressionLowerer;
        public string? SelfType { get; }
        private string? SelfApiType { get; }

        public ImportedNameLowerer(
            ProgramNode program,
            IReadOnlyList<StructNode> concreteStructs)
            : this(
                CLoweringContext.Create(program, concreteStructs),
                CreateInitialScope(program))
        {
        }

        private static CLoweringScope CreateInitialScope(ProgramNode program)
        {
            var typeRefParser = new TypeRefParser(program);
            var globals = program.GlobalVariables
                .Select(global => (global.Name, Type: global.TypeNode.ToTypeRef(typeRefParser)))
                .Where(global => global.Type is not TypeRef.Unknown)
                .GroupBy(global => global.Name, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToList();

            return CLoweringScope.Create(
                typeRefParser,
                globals
                    .Where(global => global.Type is TypeRef.Pointer)
                    .Select(global => global.Name)
                    .ToHashSet(StringComparer.Ordinal),
                globals.ToDictionary(
                    global => global.Name,
                    global => TypeRefFormatter.ToCxString(global.Type),
                    StringComparer.Ordinal),
                globals.ToDictionary(
                    global => global.Name,
                    global => global.Type,
                    StringComparer.Ordinal));
        }

        private ImportedNameLowerer(
            CLoweringContext context,
            CLoweringScope scope,
            string? selfType = null,
            string? selfApiType = null)
        {
            _context = context;
            _scope = scope;
            SelfType = selfType;
            SelfApiType = selfApiType;
            _genericCallResolver = _context.CreateGenericCallResolver(ResolveExpressionType, CanAssign);
            _expressionTypeResolver = CreateExpressionTypeResolver();
            _interfaceValueBuilder = new InterfaceValueBuilder(
                _context,
                _scope,
                s_abiNames,
                type => LowerType(type, SelfType),
                LowerTypeRef);
            _taggedUnionValueBuilder = new TaggedUnionValueBuilder(
                _context,
                InferExpressionType,
                InferExpressionTypeRef,
                type => LowerType(type, SelfType),
                LowerTypeRef);
            _structValueBuilder = new StructValueBuilder(
                _context,
                LowerExpression,
                InferExpressionType,
                type => LowerType(type, SelfType));
            _adapterExposeResolver = new AdapterExposeResolver(_context);
            _receiverExpressionBuilder = new ReceiverExpressionBuilder(_scope);
            _nameExpressionLowerer = new NameExpressionLowerer(
                _context,
                _scope,
                s_nameMangler,
                LowerExpression);
            var expressionLoweringServices = CreateExpressionLoweringServices();
            _expressionLoweringPipeline = expressionLoweringServices.Pipeline;
            _memberAccessLowerer = expressionLoweringServices.MemberAccessLowerer;
            _memberCallLowerer = expressionLoweringServices.MemberCallLowerer;
        }

        private ExpressionTypeLoweringResolver CreateExpressionTypeResolver() =>
            new(
                _context,
                _scope,
                _genericCallResolver,
                type => LowerType(type, SelfType));

        private ExpressionLoweringServices CreateExpressionLoweringServices()
        {
            var interfaceMemberCallLowerer = new InterfaceMemberCallLowerer(
                _context,
                ResolveExpressionType,
                LowerExpression);
            var functionReferences = new CFunctionReferenceResolver();
            var resolvedCallLowerer = new ResolvedCallLowerer(
                _context,
                _scope,
                _genericCallResolver,
                functionReferences,
                _receiverExpressionBuilder,
                LowerExpression);
            var memberAccessLowerer = new MemberAccessLowerer(
                _context,
                _scope,
                Lower,
                LowerExpression);
            var memberCallLowerer = new MemberCallLowerer(
                _context,
                _scope,
                _genericCallResolver,
                resolvedCallLowerer,
                functionReferences,
                interfaceMemberCallLowerer,
                _adapterExposeResolver,
                _receiverExpressionBuilder,
                ResolveExpressionType,
                LowerExpression);
            var genericCallLowerer = new GenericCallLowerer(
                _context,
                _scope,
                _genericCallResolver,
                resolvedCallLowerer,
                functionReferences,
                memberCallLowerer,
                _structValueBuilder,
                _adapterExposeResolver,
                _nameExpressionLowerer.LowerName,
                type => LowerType(type, SelfType),
                LowerExpression);
            var callLowerer = new CallLowerer(
                _context,
                _genericCallResolver,
                resolvedCallLowerer,
                functionReferences,
                memberCallLowerer,
                _structValueBuilder,
                _taggedUnionValueBuilder,
                _nameExpressionLowerer.LowerFunctionReferenceName,
                LowerExpression);
            var callExpressionLowerer = new CallExpressionLowerer(callLowerer, genericCallLowerer);
            return new ExpressionLoweringServices(
                new CExpressionLoweringPipeline(this, callExpressionLowerer),
                memberAccessLowerer,
                memberCallLowerer);
        }

        private sealed record ExpressionLoweringServices(
            CExpressionLoweringPipeline Pipeline,
            MemberAccessLowerer MemberAccessLowerer,
            MemberCallLowerer MemberCallLowerer);

        public ImportedNameLowerer ForFunction(FunctionNode function)
        {
            var selfType = ResolveSelfType(function);
            var selfApiType = ResolveSelfApiType(function);
            var scope = _scope.ForFunction(function, selfType, selfApiType);

            return new(
                _context,
                scope,
                selfType,
                selfApiType);
        }

        public ImportedNameLowerer WithLocal(string name, string type)
        {
            var scope = _scope.WithLocal(name, type);

            return new ImportedNameLowerer(
                _context,
                scope,
                SelfType,
                SelfApiType);
        }

        public ImportedNameLowerer WithImplicitReferenceLocal(
            string name,
            string valueType,
            string storageType,
            bool isConst)
        {
            var scope = _scope.WithImplicitReferenceLocal(name, valueType, storageType, isConst);

            return new ImportedNameLowerer(
                _context,
                scope,
                SelfType,
                SelfApiType);
        }

        public string LowerInitializer(string targetType, ExpressionNode expression)
        {
            var lowered = expression is InitializerExpressionNode initializer
                ? _expressionLoweringPipeline.LowerInitializer(initializer, targetType)
                : LowerExpression(expression);
            if (TryBuildInterfaceValue(targetType, expression.SourceText, out var interfaceInitializer))
            {
                return interfaceInitializer;
            }

            return _expressionEmitter.Emit(
                TryWrapTaggedUnionValueExpression(targetType, expression.SourceText, lowered) ?? lowered);
        }

        public string LowerInitializer(TypeRef? targetType, string fallbackTargetType, ExpressionNode expression) =>
            targetType is null
                ? LowerInitializer(fallbackTargetType, expression)
                : LowerInitializer(targetType, expression);

        public string LowerInitializer(TypeRef targetType, ExpressionNode expression)
        {
            var lowered = expression is InitializerExpressionNode initializer
                ? _expressionLoweringPipeline.LowerInitializer(initializer, TypeRefFormatter.ToCxString(targetType))
                : LowerExpression(expression);
            if (TryBuildInterfaceValue(targetType, expression.SourceText, out var interfaceInitializer))
            {
                return interfaceInitializer;
            }

            return _expressionEmitter.Emit(
                TryWrapTaggedUnionValueExpression(targetType, expression.SourceText, lowered) ?? lowered);
        }

        public CExpression LowerInitializerExpression(string targetType, ExpressionNode expression)
        {
            var direct = expression is InitializerExpressionNode initializer
                ? _expressionLoweringPipeline.LowerInitializer(initializer, targetType)
                : LowerExpression(expression);
            if (TryBuildInterfaceValueExpression(targetType, expression.SourceText) is { } interfaceInitializer)
            {
                return interfaceInitializer;
            }

            if (TryWrapTaggedUnionValueExpression(targetType, expression.SourceText, direct) is { } wrapped)
            {
                return wrapped;
            }

            var lowered = LowerInitializer(targetType, expression);
            return string.Equals(lowered, _expressionEmitter.Emit(direct), StringComparison.Ordinal)
                ? direct
                : UnsupportedInitializerTextFallback(expression, lowered);
        }

        public CExpression LowerInitializerExpression(TypeRef? targetType, string fallbackTargetType, ExpressionNode expression) =>
            targetType is null
                ? LowerInitializerExpression(fallbackTargetType, expression)
                : LowerInitializerExpression(targetType, expression);

        public CExpression LowerInitializerExpression(TypeNode? targetTypeNode, string fallbackTargetType, ExpressionNode expression) =>
            LowerInitializerExpression(_scope.ResolveType(targetTypeNode), fallbackTargetType, expression);

        public CExpression LowerInitializerExpression(TypeRef targetType, ExpressionNode expression)
        {
            var direct = expression is InitializerExpressionNode initializer
                ? _expressionLoweringPipeline.LowerInitializer(initializer, TypeRefFormatter.ToCxString(targetType))
                : LowerExpression(expression);
            if (TryBuildInterfaceValueExpression(targetType, expression.SourceText) is { } interfaceInitializer)
            {
                return interfaceInitializer;
            }

            if (TryWrapTaggedUnionValueExpression(targetType, expression.SourceText, direct) is { } wrapped)
            {
                return wrapped;
            }

            return direct;
        }

        private bool TryBuildInterfaceValue(string targetType, string sourceExpression, out string initializer)
        {
            initializer = string.Empty;
            if (TryBuildInterfaceValueExpression(targetType, sourceExpression) is not { } expression)
            {
                return false;
            }

            initializer = _expressionEmitter.Emit(expression);
            return true;
        }

        private bool TryBuildInterfaceValue(TypeRef targetType, string sourceExpression, out string initializer)
        {
            initializer = string.Empty;
            if (TryBuildInterfaceValueExpression(targetType, sourceExpression) is not { } expression)
            {
                return false;
            }

            initializer = _expressionEmitter.Emit(expression);
            return true;
        }

        private CExpression? TryBuildInterfaceValueExpression(string targetType, string sourceExpression)
            => _interfaceValueBuilder.TryBuild(targetType, sourceExpression);

        private CExpression? TryBuildInterfaceValueExpression(TypeRef targetType, string sourceExpression)
            => _interfaceValueBuilder.TryBuild(targetType, sourceExpression);

        public CExpression LowerExpression(ExpressionNode expression) =>
            _expressionLoweringPipeline.Lower(expression);

        public string Lower(ExpressionNode expression) => expression switch
        {
            LiteralExpressionNode
                or NameExpressionNode
                or ParenthesizedExpressionNode
                or CastExpressionNode
                or UnaryExpressionNode
                or PostfixExpressionNode
                or SizeOfExpressionNode
                or BinaryExpressionNode
                or ConditionalExpressionNode
                or InitializerExpressionNode
                or AssignmentExpressionNode
                or MemberExpressionNode
                or ScalarRangeExpressionNode
                or IndexExpressionNode
                or CallExpressionNode
                or GenericCallExpressionNode => _expressionEmitter.Emit(LowerExpression(expression)),
            FunctionExpressionNode functionExpression => UnsupportedExpressionTextLowering(functionExpression),
            RawExpressionNode raw => UnexpectedRawExpressionText(raw),
            _ => UnsupportedExpressionTextLowering(expression),
        };

        private static string UnexpectedRawExpressionText(RawExpressionNode raw) =>
            throw new InvalidOperationException(
                $"Raw expression reached C emission after lowering: '{TrimForDiagnostic(raw.SourceText)}'.");

        private static string TrimForDiagnostic(string text)
        {
            text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return text.Length <= 120 ? text : text[..117] + "...";
        }

        private static string UnsupportedExpressionTextLowering(ExpressionNode expression) =>
            throw new InvalidOperationException(
                $"Internal C emission error: expression requires unsupported legacy text lowering: '{TrimForDiagnostic(expression.SourceText)}'.");

        private static CExpression UnsupportedInitializerTextFallback(ExpressionNode expression, string loweredText) =>
            throw new InvalidOperationException(
                "Internal C emission error: initializer lowered differently through legacy text path and cannot be represented as C AST: "
                + $"'{TrimForDiagnostic(expression.SourceText)}' -> '{TrimForDiagnostic(loweredText)}'.");

        CExpression ICExpressionLoweringContext.LowerNameExpression(NameExpressionNode name) =>
            _nameExpressionLowerer.LowerNameExpression(name);

        CExpression ICExpressionLoweringContext.LowerAddressOfExpression(ExpressionNode operand) =>
            _nameExpressionLowerer.LowerAddressOfExpression(operand);

        string ICExpressionLoweringContext.LowerType(TypeRef type) =>
            LowerTypeRef(type);

        string ICExpressionLoweringContext.LowerType(TypeNode? typeNode) =>
            _scope.ResolveType(typeNode) is { } type
                ? LowerTypeRef(type)
                : string.Empty;

        string ICExpressionLoweringContext.LowerType(TypeNode? typeNode, string fallbackType) =>
            CEmitter.LowerType(typeNode, fallbackType, SelfType);

        private string LowerType(TypeNode? typeNode) =>
            _scope.ResolveType(typeNode) is { } type
                ? LowerTypeRef(type)
                : string.Empty;

        private string LowerTypeRef(TypeRef type) =>
            s_abiNames.LowerType(type, GenericTypeSubstitutionBuilder.ParseType(SelfType));

        private static string LowerType(string type, string? selfType = null) =>
            CEmitter.LowerType(type, selfType);

        CExpression? ICExpressionLoweringContext.TryWrapAssignmentValue(
            AssignmentExpressionNode assignment,
            CExpression value)
        {
            return assignment.Operator == "="
                && assignment.Target is NameExpressionNode targetName
                && _scope.TryGetVariableTypeRef(targetName.SourceText, out var targetTypeRef)
                ? TryWrapTaggedUnionValueExpression(targetTypeRef, assignment.Value.SourceText, value)
                : null;
        }

        CExpression? ICExpressionLoweringContext.TryLowerMemberExpression(MemberExpressionNode member) =>
            LowerMemberExpression(member);

        private string LowerInitializerExpression(InitializerExpressionNode initializer, string? targetType = null)
            => _expressionEmitter.Emit(_expressionLoweringPipeline.LowerInitializer(initializer, targetType));

        private CExpression? LowerGenericMemberCallExpression(
            MemberExpressionNode member,
            IReadOnlyList<string> typeArguments,
            IReadOnlyList<ExpressionNode> arguments) =>
            _memberCallLowerer.TryLowerGenericMember(member, typeArguments, arguments);

        private string? ResolveExpressionType(ExpressionNode expression) =>
            _expressionTypeResolver.Resolve(expression);

        public string? ResolveExpressionTypeForLowering(ExpressionNode expression) =>
            ResolveExpressionType(expression);

        private static bool CanAssign(string targetType, string sourceType) =>
            string.Equals(targetType.Trim(), sourceType.Trim(), StringComparison.Ordinal)
            || (targetType.Trim() == "double" && sourceType.Trim() is "int" or "float")
            || (targetType.Trim() == "float" && sourceType.Trim() == "int")
            || (BuiltinTypes.IsNumeric(targetType) && BuiltinTypes.IsNumeric(sourceType));

        private static string RemovePointer(string type)
        {
            while (type.TrimEnd().EndsWith("*", StringComparison.Ordinal))
            {
                type = type.TrimEnd()[..^1];
            }

            return type.TrimEnd();
        }

        private CExpression LowerMemberExpression(MemberExpressionNode member) =>
            _memberAccessLowerer.LowerExpression(member);

        private CExpression? LowerAdapterExposeCall(
            AdapterExposeInfo adapterExpose,
            IReadOnlyList<string> receiverArguments,
            IReadOnlyList<ExpressionNode> arguments,
            string target,
            bool isPointer) =>
            _memberCallLowerer.TryLowerAdapterExposeCall(adapterExpose, receiverArguments, arguments, target, isPointer);

        private bool IsModuleQualifierTarget(string target) =>
            _context.IsModuleQualifierTarget(target);

        private CExpression? TryWrapTaggedUnionValueExpression(
            string targetType,
            string sourceExpression,
            CExpression loweredExpression)
            => _taggedUnionValueBuilder.TryWrapExpression(targetType, sourceExpression, loweredExpression);

        private CExpression? TryWrapTaggedUnionValueExpression(
            TypeRef targetType,
            string sourceExpression,
            CExpression loweredExpression)
            => _taggedUnionValueBuilder.TryWrapExpression(targetType, sourceExpression, loweredExpression);

        private string? InferExpressionType(string expression)
        {
            expression = expression.Trim();

            if (Regex.IsMatch(expression, @"^-?\s*\d+$"))
            {
                return "int";
            }

            if (expression.StartsWith('"'))
            {
                return "char*";
            }

            if (expression.StartsWith("&", StringComparison.Ordinal)
                && _scope.TryGetVariableType(expression[1..].Trim(), out var referencedType))
            {
                return referencedType + "*";
            }

            foreach (var structName in _context.GetStructNames())
            {
                if (expression.StartsWith(structName + "(", StringComparison.Ordinal))
                {
                    return structName;
                }

                if (expression.StartsWith(structName + " {", StringComparison.Ordinal))
                {
                    return structName;
                }
            }

            foreach (var interfaceName in _context.GetInterfaceNames())
            {
                if (expression.StartsWith(interfaceName + " {", StringComparison.Ordinal))
                {
                    return interfaceName;
                }
            }

            return _scope.TryGetVariableType(expression, out var variableType)
                ? variableType
                : null;
        }

        private TypeRef? InferExpressionTypeRef(string expression)
        {
            expression = expression.Trim();
            if (expression.StartsWith("&", StringComparison.Ordinal)
                && _scope.TryGetVariableTypeRef(expression[1..].Trim(), out var referencedType))
            {
                return new TypeRef.Pointer(referencedType);
            }

            if (_scope.TryGetVariableTypeRef(expression, out var variableType))
            {
                return variableType;
            }

            if (InferExpressionType(expression) is not { } type)
            {
                return null;
            }

            return GenericTypeSubstitutionBuilder.ParseType(type);
        }

        private string ResolveAlias(string type) =>
            _context.ResolveTypeAlias(type);

    }
}
