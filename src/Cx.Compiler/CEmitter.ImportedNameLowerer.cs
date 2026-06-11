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
        private readonly CExpressionLowerer _expressionLowerer;
        private readonly GenericCallResolver _genericCallResolver;
        private readonly RequirementLookup _requirementLookup;
        private readonly ForeachIterableResolver _foreachIterableResolver;
        private readonly MatchResolver _matchResolver;
        private readonly InterfaceValueBuilder _interfaceValueBuilder;
        private readonly TaggedUnionValueBuilder _taggedUnionValueBuilder;
        private readonly StructValueBuilder _structValueBuilder;
        private readonly NamedInitializerTextLowerer _namedInitializerTextLowerer;
        private readonly AdapterExposeResolver _adapterExposeResolver;
        private readonly TextConstructorLowerer _textConstructorLowerer;
        private readonly TextAdapterExposeLowerer _textAdapterExposeLowerer;
        private readonly ReceiverExpressionBuilder _receiverExpressionBuilder;
        private readonly InterfaceMemberCallLowerer _interfaceMemberCallLowerer;
        private readonly ResolvedCallLowerer _resolvedCallLowerer;
        private readonly MemberAccessLowerer _memberAccessLowerer;
        private readonly MemberCallLowerer _memberCallLowerer;
        private readonly GenericCallLowerer _genericCallLowerer;
        private readonly CallLowerer _callLowerer;
        private readonly ExpressionTypeLoweringResolver _expressionTypeResolver;
        public string? SelfType { get; }
        private string? SelfApiType { get; }

        public ImportedNameLowerer(
            ProgramNode program,
            IReadOnlyList<StructNode> concreteStructs,
            RequirementMatcher requirementMatcher)
            : this(
                CLoweringContext.Create(program, concreteStructs, requirementMatcher),
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
            _expressionLowerer = new CExpressionLowerer(this);
            _genericCallResolver = _context.CreateGenericCallResolver(ResolveExpressionType, CanAssign);
            _expressionTypeResolver = new ExpressionTypeLoweringResolver(
                _context,
                _scope,
                _genericCallResolver,
                type => LowerType(type, SelfType));
            _requirementLookup = _context.CreateRequirementLookup();
            _foreachIterableResolver = new ForeachIterableResolver(
                _scope,
                _genericCallResolver,
                _requirementLookup,
                ResolveExpressionType,
                LowerExpression);
            _matchResolver = new MatchResolver(_scope, _context);
            _interfaceValueBuilder = new InterfaceValueBuilder(
                _context,
                _scope,
                type => LowerType(type, SelfType),
                type => CTypeLowerer.LowerType(type, s_typeAdapters, GenericTypeSubstitutionBuilder.ParseType(SelfType)));
            _taggedUnionValueBuilder = new TaggedUnionValueBuilder(
                _context,
                InferExpressionType,
                InferExpressionTypeRef,
                type => LowerType(type, SelfType),
                type => CTypeLowerer.LowerType(type, s_typeAdapters, GenericTypeSubstitutionBuilder.ParseType(SelfType)),
                () => GenericTypeSubstitutionBuilder.ParseType(SelfType));
            _structValueBuilder = new StructValueBuilder(
                _context,
                LowerExpression,
                Lower,
                InferExpressionType,
                type => LowerType(type, SelfType));
            _namedInitializerTextLowerer = new NamedInitializerTextLowerer(
                _context,
                TextExpressionRewriter.ReplaceBracedExpressions,
                TextExpressionRewriter.SplitArguments,
                Lower,
                type => LowerType(type, SelfType));
            _adapterExposeResolver = new AdapterExposeResolver(_context);
            _textConstructorLowerer = new TextConstructorLowerer(
                _context,
                _genericCallResolver,
                _taggedUnionValueBuilder,
                _structValueBuilder);
            _textAdapterExposeLowerer = new TextAdapterExposeLowerer(
                _context,
                _genericCallResolver,
                _adapterExposeResolver);
            _receiverExpressionBuilder = new ReceiverExpressionBuilder(_scope);
            _interfaceMemberCallLowerer = new InterfaceMemberCallLowerer(
                _context,
                ResolveExpressionType,
                LowerExpression);
            _resolvedCallLowerer = new ResolvedCallLowerer(
                _context,
                _scope,
                _genericCallResolver,
                _receiverExpressionBuilder,
                LowerExpression);
            _memberAccessLowerer = new MemberAccessLowerer(
                _context,
                _scope,
                Lower,
                LowerExpression);
            _memberCallLowerer = new MemberCallLowerer(
                _context,
                _scope,
                _genericCallResolver,
                _resolvedCallLowerer,
                _interfaceMemberCallLowerer,
                _adapterExposeResolver,
                _receiverExpressionBuilder,
                ResolveExpressionType,
                LowerExpression);
            _genericCallLowerer = new GenericCallLowerer(
                _context,
                _scope,
                _genericCallResolver,
                _resolvedCallLowerer,
                _memberCallLowerer,
                _adapterExposeResolver,
                LowerName,
                Lower,
                LowerExpression);
            _callLowerer = new CallLowerer(
                _context,
                _genericCallResolver,
                _resolvedCallLowerer,
                _memberCallLowerer,
                _structValueBuilder,
                _taggedUnionValueBuilder,
                LowerFunctionReferenceName,
                Lower,
                LowerExpression,
                _textConstructorLowerer.LowerPayloadConstructor,
                _textConstructorLowerer.LowerStructConstructor);
        }

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

        public ContiguousIterableInfo? ResolveContiguousIterable(string expression) =>
            _foreachIterableResolver.ResolveContiguousIterable(expression);

        public IteratorIterableInfo? ResolveIteratorIterable(ExpressionNode expression, bool keyValue) =>
            _foreachIterableResolver.ResolveIteratorIterable(expression, keyValue);

        public MatchInfo? ResolveMatch(string expression) =>
            _matchResolver.ResolveMatch(expression);

        public string LowerInitializer(string targetType, string expression)
        {
            var lowered = Lower(expression);
            if (TryBuildInterfaceValue(targetType, expression, out var interfaceInitializer))
            {
                return interfaceInitializer;
            }

            return TryWrapTaggedUnionValue(targetType, expression, lowered) ?? lowered;
        }

        public string LowerInitializer(TypeRef? targetType, string fallbackTargetType, string expression) =>
            targetType is null
                ? LowerInitializer(fallbackTargetType, expression)
                : LowerInitializer(targetType, expression);

        public string LowerInitializer(TypeRef targetType, string expression)
        {
            var lowered = Lower(expression);
            if (TryBuildInterfaceValue(targetType, expression, out var interfaceInitializer))
            {
                return interfaceInitializer;
            }

            return TryWrapTaggedUnionValue(targetType, expression, lowered) ?? lowered;
        }

        public string LowerInitializer(string targetType, ExpressionNode expression)
        {
            var lowered = expression is InitializerExpressionNode initializer
                ? LowerInitializerExpression(initializer, targetType)
                : Lower(expression);
            if (TryBuildInterfaceValue(targetType, expression.SourceText, out var interfaceInitializer))
            {
                return interfaceInitializer;
            }

            return TryWrapTaggedUnionValue(targetType, expression.SourceText, lowered) ?? lowered;
        }

        public string LowerInitializer(TypeRef? targetType, string fallbackTargetType, ExpressionNode expression) =>
            targetType is null
                ? LowerInitializer(fallbackTargetType, expression)
                : LowerInitializer(targetType, expression);

        public string LowerInitializer(TypeRef targetType, ExpressionNode expression)
        {
            var lowered = expression is InitializerExpressionNode initializer
                ? _expressionEmitter.Emit(LowerInitializerExpression(targetType, initializer))
                : Lower(expression);
            if (TryBuildInterfaceValue(targetType, expression.SourceText, out var interfaceInitializer))
            {
                return interfaceInitializer;
            }

            return TryWrapTaggedUnionValue(targetType, expression.SourceText, lowered) ?? lowered;
        }

        public CExpression LowerInitializerExpression(string targetType, ExpressionNode expression)
        {
            var direct = expression is InitializerExpressionNode initializer
                ? _expressionLowerer.LowerInitializer(initializer, targetType)
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
                : new CRawExpression(lowered);
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
                ? _expressionLowerer.LowerInitializer(initializer, TypeRefFormatter.ToCxString(targetType))
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

        public CExpression LowerExpression(ExpressionNode expression) => expression switch
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
                or IndexExpressionNode => _expressionLowerer.LowerSimple(expression),
            FunctionExpressionNode functionExpression => new CRawExpression(Lower(functionExpression.SourceText)),
            CallExpressionNode call => LowerCallExpression(call) is { } loweredCall
                ? loweredCall
                : new CRawExpression(Lower(call.SourceText)),
            GenericCallExpressionNode call => LowerGenericCallExpression(call) is { } loweredGenericCall
                ? loweredGenericCall
                : new CRawExpression(Lower(call.SourceText)),
            RawExpressionNode raw => TryLowerRawExpression(raw.SourceText) ?? new CRawExpression(Lower(raw.SourceText)),
            _ => new CRawExpression(Lower(expression.SourceText)),
        };

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
                or IndexExpressionNode => _expressionLowerer.LowerSimpleText(expression, _expressionEmitter),
            FunctionExpressionNode functionExpression => Lower(functionExpression.SourceText),
            CallExpressionNode call => LowerCall(call) ?? Lower(call.SourceText),
            GenericCallExpressionNode call => LowerGenericCall(call) ?? Lower(call.SourceText),
            RawExpressionNode raw => Lower(raw.SourceText),
            _ => Lower(expression.SourceText),
        };

        private CExpression LowerBinaryExpression(BinaryExpressionNode binary)
        {
            if (ShouldUseRawLowering(binary.SourceText))
            {
                return new CRawExpression(Lower(binary.SourceText));
            }

            return binary.Operator == "<=>"
                ? new CCallExpression(new CFunctionName("compare"), [LowerExpression(binary.Left), LowerExpression(binary.Right)])
                : new CBinaryExpression(LowerExpression(binary.Left), binary.Operator, LowerExpression(binary.Right));
        }

        private CExpression LowerSizeOfExpression(SizeOfExpressionNode sizeOf)
        {
            if (sizeOf.TypeOperandNode is not null)
            {
                return new CSizeOfTypeExpression(LowerType(sizeOf.TypeOperandNode));
            }

            return sizeOf.ExpressionOperand is null
                ? new CSizeOfTypeExpression("void")
                : new CSizeOfExpression(LowerExpression(sizeOf.ExpressionOperand));
        }

        private CExpression LowerConditionalExpression(ConditionalExpressionNode conditional)
        {
            if (ShouldUseRawLowering(conditional.SourceText))
            {
                return new CRawExpression(Lower(conditional.SourceText));
            }

            return new CConditionalExpression(
                LowerExpression(conditional.Condition),
                LowerExpression(conditional.WhenTrue),
                LowerExpression(conditional.WhenFalse));
        }

        private CExpression LowerInitializerExpressionNode(InitializerExpressionNode initializer, string? targetType = null) =>
            _expressionLowerer.LowerInitializer(initializer, targetType);

        private CExpression? TryLowerRawExpression(string expression)
        {
            expression = expression.Trim();
            var dereferenceCall = Regex.Match(expression, @"^\*\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(");
            if (!dereferenceCall.Success)
            {
                return null;
            }

            var functionName = dereferenceCall.Groups["name"].Value;
            var openParen = expression.IndexOf('(', dereferenceCall.Groups["name"].Index + functionName.Length);
            var closeParen = TextExpressionRewriter.FindMatchingParen(expression, openParen);
            if (openParen < 0 || closeParen != expression.Length - 1)
            {
                return null;
            }

            var argumentsText = expression[(openParen + 1)..closeParen];
            var arguments = TextExpressionRewriter.SplitArguments(argumentsText)
                .Select(LowerSimpleRawArgument)
                .ToList();
            return new CUnaryExpression(
                "*",
                new CCallExpression(new CFunctionName(functionName), arguments));
        }

        private CExpression LowerSimpleRawArgument(string argument)
        {
            argument = argument.Trim();
            if (argument.StartsWith("&", StringComparison.Ordinal))
            {
                return new CUnaryExpression("&", ToCSimpleAccessExpression(argument[1..]));
            }

            if (Regex.IsMatch(argument, @"^\d+(?:\.\d+)?$"))
            {
                return new CLiteralExpression(argument);
            }

            return ToCSimpleAccessExpression(Lower(argument));
        }

        private string LowerLiteral(string text) => text switch
        {
            "true" => "1",
            "false" => "0",
            "null" => "NULL",
            _ => text,
        };

        private string LowerName(string name)
        {
            if (_context.TryResolveSymbolAlias(name, out var original))
            {
                return original;
            }

            return name;
        }

        private string LowerFunctionReferenceName(NameExpressionNode name) =>
            name.Semantic.Symbol is { Kind: SymbolKind.Function } symbol
                ? s_nameMangler.SymbolName(symbol)
                : LowerName(name.SourceText);

        private CExpression LowerNameExpression(NameExpressionNode name)
        {
            var loweredName = LowerFunctionReferenceName(name);
            return _scope.IsImplicitReferenceLocal(name.SourceText)
                ? new CUnaryExpression("*", new CNameExpression(loweredName))
                : new CNameExpression(loweredName);
        }

        private string LowerNameForValue(NameExpressionNode name)
        {
            var loweredName = LowerFunctionReferenceName(name);
            return _scope.IsImplicitReferenceLocal(name.SourceText)
                ? "*" + loweredName
                : loweredName;
        }

        private CExpression LowerAddressOfExpression(ExpressionNode operand)
        {
            if (operand is NameExpressionNode name
                && _scope.IsImplicitReferenceLocal(name.SourceText))
            {
                return new CNameExpression(LowerName(name.SourceText));
            }

            return new CUnaryExpression("&", LowerExpression(operand));
        }

        CExpression ICExpressionLoweringContext.LowerNameExpression(NameExpressionNode name) =>
            LowerNameExpression(name);

        CExpression ICExpressionLoweringContext.LowerAddressOfExpression(ExpressionNode operand) =>
            LowerAddressOfExpression(operand);

        string ICExpressionLoweringContext.LowerRawText(string text) =>
            Lower(text);

        string ICExpressionLoweringContext.LowerType(string type) =>
            LowerType(type, SelfType);

        string ICExpressionLoweringContext.LowerType(TypeRef type) =>
            CTypeLowerer.LowerType(type, s_typeAdapters, GenericTypeSubstitutionBuilder.ParseType(SelfType));

        string ICExpressionLoweringContext.LowerType(TypeNode? typeNode) =>
            _scope.ResolveType(typeNode) is { } type
                ? CTypeLowerer.LowerType(type, s_typeAdapters, GenericTypeSubstitutionBuilder.ParseType(SelfType))
                : string.Empty;

        string ICExpressionLoweringContext.LowerType(TypeNode? typeNode, string fallbackType) =>
            CEmitter.LowerType(typeNode, fallbackType, SelfType);

        private string LowerType(TypeNode? typeNode) =>
            _scope.ResolveType(typeNode) is { } type
                ? CTypeLowerer.LowerType(type, s_typeAdapters, GenericTypeSubstitutionBuilder.ParseType(SelfType))
                : string.Empty;

        private static string LowerType(string type, string? selfType = null) =>
            CEmitter.LowerType(type, selfType);

        bool ICExpressionLoweringContext.ShouldUseRawLowering(string text) =>
            ShouldUseRawLowering(text);

        bool ICExpressionLoweringContext.ShouldUseRawAssignmentLowering(string text) =>
            ShouldUseRawLowering(text, allowAssignment: true);

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

        string? ICExpressionLoweringContext.TryWrapAssignmentValueText(
            AssignmentExpressionNode assignment,
            string loweredValue)
        {
            return assignment.Operator == "="
                && assignment.Target is NameExpressionNode targetName
                && _scope.TryGetVariableTypeRef(targetName.SourceText, out var targetTypeRef)
                ? TryWrapTaggedUnionValue(targetTypeRef, assignment.Value.SourceText, loweredValue)
                : null;
        }

        CExpression? ICExpressionLoweringContext.TryRepairAssignmentTarget(CExpression target) =>
            target is CRawExpression rawTarget && TryLowerRawExpression(rawTarget.Text) is { } loweredTarget
                ? loweredTarget
                : null;

        CExpression? ICExpressionLoweringContext.TryLowerMemberExpression(MemberExpressionNode member) =>
            LowerMemberExpression(member);

        string? ICExpressionLoweringContext.TryLowerMemberText(MemberExpressionNode member) =>
            LowerMember(member);

        private string LowerAddressOf(ExpressionNode operand)
        {
            if (operand is NameExpressionNode name
                && _scope.IsImplicitReferenceLocal(name.SourceText))
            {
                return LowerName(name.SourceText);
            }

            return "&" + Lower(operand);
        }

        private string? LowerCall(CallExpressionNode call) =>
            _callLowerer.TryLowerText(call);

        private string LowerBinary(BinaryExpressionNode binary)
        {
            if (ShouldUseRawLowering(binary.SourceText))
            {
                return Lower(binary.SourceText);
            }

            if (binary.Operator == "<=>")
            {
                return $"compare({Lower(binary.Left)}, {Lower(binary.Right)})";
            }

            return $"{Lower(binary.Left)} {binary.Operator} {Lower(binary.Right)}";
        }

        private string LowerSizeOf(SizeOfExpressionNode sizeOf)
        {
            if (sizeOf.TypeOperandNode is not null)
            {
                return $"sizeof({LowerType(sizeOf.TypeOperandNode)})";
            }

            return sizeOf.ExpressionOperand is null
                ? "sizeof(void)"
                : $"sizeof({Lower(sizeOf.ExpressionOperand)})";
        }

        private string LowerConditional(ConditionalExpressionNode conditional)
        {
            if (ShouldUseRawLowering(conditional.SourceText))
            {
                return Lower(conditional.SourceText);
            }

            return $"{Lower(conditional.Condition)} ? {Lower(conditional.WhenTrue)} : {Lower(conditional.WhenFalse)}";
        }

        private string LowerInitializerExpression(InitializerExpressionNode initializer, string? targetType = null)
            => _expressionLowerer.LowerInitializerText(initializer, _expressionEmitter, targetType);

        private string? LowerGenericCall(GenericCallExpressionNode call)
        {
            var expression = LowerGenericCallExpression(call);
            return expression is null ? null : _expressionEmitter.Emit(expression);
        }

        private CExpression? LowerGenericCallExpression(GenericCallExpressionNode call) =>
            _genericCallLowerer.TryLower(call);

        private string? LowerGenericMemberCall(
            MemberExpressionNode member,
            IReadOnlyList<string> typeArguments,
            IReadOnlyList<ExpressionNode> arguments)
        {
            var expression = _memberCallLowerer.TryLowerGenericMember(member, typeArguments, arguments);
            return expression is null ? null : _expressionEmitter.Emit(expression);
        }

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

        private static string? GetQualifiedName(ExpressionNode expression) => expression switch
        {
            NameExpressionNode name => name.SourceText,
            MemberExpressionNode member when GetQualifiedName(member.Target) is { } target => $"{target}.{member.MemberName}",
            _ => null,
        };

        private string LowerAssignment(AssignmentExpressionNode assignment)
        {
            if (ShouldUseRawLowering(assignment.SourceText, allowAssignment: true))
            {
                return Lower(assignment.SourceText);
            }

            var loweredTarget = Lower(assignment.Target);
            var loweredValue = Lower(assignment.Value);
            if (assignment.Operator == "="
                && assignment.Target is NameExpressionNode targetName
                && _scope.TryGetVariableType(targetName.SourceText, out var targetType)
                && TryWrapTaggedUnionValue(targetType, assignment.Value.SourceText, loweredValue) is { } wrappedValue)
            {
                loweredValue = wrappedValue;
            }

            return $"{loweredTarget} {assignment.Operator} {loweredValue}";
        }

        private CExpression LowerAssignmentExpression(AssignmentExpressionNode assignment)
        {
            if (ShouldUseRawLowering(assignment.SourceText, allowAssignment: true))
            {
                return new CRawExpression(Lower(assignment.SourceText));
            }

            var value = LowerExpression(assignment.Value);
            if (assignment.Operator == "="
                && assignment.Target is NameExpressionNode targetName
                && _scope.TryGetVariableType(targetName.SourceText, out var targetType)
                && TryWrapTaggedUnionValueExpression(targetType, assignment.Value.SourceText, value) is { } wrappedValue)
            {
                value = wrappedValue;
            }

            var target = LowerExpression(assignment.Target);
            if (target is CRawExpression rawTarget
                && TryLowerRawExpression(rawTarget.Text) is { } loweredTarget)
            {
                target = loweredTarget;
            }

            return new CAssignmentExpression(
                target,
                assignment.Operator,
                value);
        }

        private static bool ShouldUseRawLowering(string expression, bool allowAssignment = false) =>
            Regex.IsMatch(expression, @"(?<!<)=>")
            || expression.Contains("->", StringComparison.Ordinal)
            || (!allowAssignment && Regex.IsMatch(expression, @"[\+\-\*/%]\s*="))
            || (!allowAssignment && Regex.IsMatch(expression, @"(?<![=!<>])=(?![=>])"));

        private string LowerMember(MemberExpressionNode member) =>
            _memberAccessLowerer.LowerText(member);

        private CExpression LowerMemberExpression(MemberExpressionNode member) =>
            _memberAccessLowerer.LowerExpression(member);

        private CExpression? LowerCallExpression(CallExpressionNode call) =>
            _callLowerer.TryLowerExpression(call);

        private CExpression? LowerAdapterExposeCall(
            AdapterExposeInfo adapterExpose,
            IReadOnlyList<string> receiverArguments,
            IReadOnlyList<ExpressionNode> arguments,
            string target,
            bool isPointer) =>
            _memberCallLowerer.TryLowerAdapterExposeCall(adapterExpose, receiverArguments, arguments, target, isPointer);

        private static string GetFunctionModule(string? ownerType, string name) =>
            ownerType is null ? name : ownerType;

        private bool IsModuleQualifierTarget(string target) =>
            _context.IsModuleQualifierTarget(target);

        public string Lower(string expression)
        {
            if (TrySplitTopLevelSpaceship(expression, out var left, out var right))
            {
                return $"compare({Lower(left)}, {Lower(right)})";
            }

            expression = Regex.Replace(expression, @"\btrue\b", "1");
            expression = Regex.Replace(expression, @"\bfalse\b", "0");
            expression = Regex.Replace(expression, @"\bnull\b", "NULL");
            expression = _textConstructorLowerer.LowerExplicitGenericStaticCalls(expression);

            expression = _textConstructorLowerer.LowerTaggedUnionConstructors(expression);
            expression = LowerNamedStructInitializers(expression);
            expression = LowerNamedInterfaceInitializers(expression);
            expression = _textConstructorLowerer.LowerStructConstructors(expression);

            if (SelfApiType is not null)
            {
                var selfApiName = GetGenericBaseName(SelfApiType) ?? SelfApiType;
                var selfApiArguments = TryParseGenericUse(SelfApiType, out _, out var parsedSelfApiArguments)
                    ? parsedSelfApiArguments
                    : [];
                expression = _textAdapterExposeLowerer.LowerCalls(expression, "self", "self", selfApiName, selfApiArguments);
            }

            foreach (var (variable, type) in _scope.GetVariables())
            {
                var normalizedType = NormalizeType(type);
                var argument = !type.EndsWith("*", StringComparison.Ordinal)
                    ? "&" + variable
                    : variable;

                if (_context.TryGetTaggedUnion(normalizedType, out var taggedUnion))
                {
                    foreach (var variant in taggedUnion.Variants)
                    {
                        var access = type.EndsWith("*", StringComparison.Ordinal)
                            ? $"{variable}->as.{variant.Name}"
                            : $"{variable}.as.{variant.Name}";
                        expression = Regex.Replace(
                            expression,
                            $@"\b{Regex.Escape(variable)}\.{Regex.Escape(variant.Name)}\b",
                            access);
                    }
                }

                if (_context.TryGetInterface(normalizedType, out var interfaceNode))
                {
                    var isPointer = type.EndsWith("*", StringComparison.Ordinal);
                    var access = isPointer ? "->" : ".";
                    var stateAccess = $"{variable}{access}state";
                    foreach (var method in interfaceNode.Methods)
                    {
                        var vMethod = $"{variable}{access}vtable->{method.Name}";
                        expression = Regex.Replace(
                            expression,
                            $@"\b{Regex.Escape(variable)}\.{Regex.Escape(method.Name)}\(\s*\)",
                            $"{vMethod}({stateAccess})");
                        expression = Regex.Replace(
                            expression,
                            $@"\b{Regex.Escape(variable)}\.{Regex.Escape(method.Name)}\(",
                            $"{vMethod}({stateAccess}, ");
                    }
                }

                var receiverType = RemovePointer(normalizedType);
                var receiverArguments = TryParseGenericUse(receiverType, out _, out var parsedReceiverArguments)
                    ? parsedReceiverArguments
                    : [];
                var receiverApiType = variable == "self" && SelfApiType is not null
                    ? SelfApiType
                    : receiverType;
                var receiverApiArguments = TryParseGenericUse(receiverApiType, out _, out var parsedReceiverApiArguments)
                    ? parsedReceiverApiArguments
                    : [];
                var receiverApiName = GetGenericBaseName(receiverApiType) ?? receiverApiType;
                expression = _textAdapterExposeLowerer.LowerCalls(expression, variable, argument, receiverApiName, receiverApiArguments);

                var adapterName = GetGenericBaseName(receiverType) ?? receiverType;
                expression = _textAdapterExposeLowerer.LowerCalls(expression, variable, argument, adapterName, receiverArguments);
                foreach (var expose in _context.GetInstanceAdapterExposes())
                {
                    var baseType = _adapterExposeResolver.SubstituteBaseType(expose, receiverArguments);
                    if (LowerType(baseType) == LowerType(receiverType))
                    {
                        expression = _textAdapterExposeLowerer.LowerCall(expression, variable, argument, expose, receiverArguments);
                    }
                }

                foreach (var methodInfo in _context.GetInstanceMethodsForReceiver(normalizedType))
                {
                    var receiver = methodInfo.TakesPointerSelf ? argument : variable;
                    expression = Regex.Replace(
                        expression,
                        $@"\b{Regex.Escape(variable)}\.{Regex.Escape(methodInfo.Name)}\(\s*\)",
                        $"{methodInfo.CName}({receiver})");
                    expression = Regex.Replace(
                        expression,
                        $@"\b{Regex.Escape(variable)}\.{Regex.Escape(methodInfo.Name)}\(",
                        $"{methodInfo.CName}({receiver}, ");
                }

                foreach (var genericCall in _genericCallResolver.GetInstanceCallsForOwner(GetGenericBaseName(type)))
                {
                    var receiver = genericCall.TakesPointerSelf ? argument : variable;
                    if (GenericCallResolver.SameTypeArguments(genericCall.TypeArguments, receiverArguments))
                    {
                        var inferredCallPrefix = $@"\b{Regex.Escape(variable)}\s*\.\s*{Regex.Escape(genericCall.Name)}\s*";
                        expression = Regex.Replace(expression, inferredCallPrefix + @"\(\s*\)", $"{genericCall.CName}({receiver})");
                        expression = Regex.Replace(expression, inferredCallPrefix + @"\(", $"{genericCall.CName}({receiver}, ");
                    }

                    var callPrefix = $@"\b{Regex.Escape(variable)}\s*\.\s*{Regex.Escape(genericCall.Name)}\s*<\s*{string.Join(@"\s*,\s*", genericCall.TypeArguments.Select(Regex.Escape))}\s*>\s*";
                    expression = Regex.Replace(expression, callPrefix + @"\(\s*\)", $"{genericCall.CName}({receiver})");
                    expression = Regex.Replace(expression, callPrefix + @"\(", $"{genericCall.CName}({receiver}, ");
                }
            }

            foreach (var methodInfo in _context.GetMethods())
            {
                expression = expression.Replace(methodInfo.Key, methodInfo.CName, StringComparison.Ordinal);
            }

            foreach (var (source, target) in _context.GetTaggedUnionTagAliases())
            {
                expression = expression.Replace(source, target, StringComparison.Ordinal);
            }

            foreach (var (source, target) in _context.GetEnumMemberAliases())
            {
                expression = expression.Replace(source, target, StringComparison.Ordinal);
            }

            foreach (var qualifier in _context.GetModuleQualifiers())
            {
                expression = expression.Replace(qualifier, string.Empty, StringComparison.Ordinal);
            }

            foreach (var (alias, original) in _context.GetSymbolAliases())
            {
                expression = Regex.Replace(
                    expression,
                    $@"\b{Regex.Escape(alias)}\b",
                    original);
            }

            foreach (var parameter in _scope.GetPointerParametersByDescendingLength())
            {
                expression = Regex.Replace(
                    expression,
                    $@"\b{Regex.Escape(parameter)}\.",
                    parameter + "->");
            }

            expression = LowerTaggedUnionAssignment(expression);

            return expression;
        }

        private static bool TrySplitTopLevelSpaceship(string expression, out string left, out string right)
        {
            var depth = 0;
            for (var i = 0; i <= expression.Length - 3; i++)
            {
                depth += expression[i] switch
                {
                    '(' or '[' or '{' => 1,
                    ')' or ']' or '}' => -1,
                    _ => 0,
                };

                if (depth != 0 || !expression.AsSpan(i, 3).SequenceEqual("<=>"))
                {
                    continue;
                }

                left = expression[..i].Trim();
                right = expression[(i + 3)..].Trim();
                return left.Length > 0 && right.Length > 0;
            }

            left = string.Empty;
            right = string.Empty;
            return false;
        }

        private string LowerNamedStructInitializers(string expression) =>
            _namedInitializerTextLowerer.LowerStructInitializers(expression);

        private string LowerNamedInterfaceInitializers(string expression) =>
            _namedInitializerTextLowerer.LowerInterfaceInitializers(expression);

        private string LowerTaggedUnionAssignment(string expression)
        {
            var match = Regex.Match(expression, @"^(?<target>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>.+)$");
            if (!match.Success)
            {
                return expression;
            }

            var targetName = match.Groups["target"].Value;
            if (!_scope.TryGetVariableTypeRef(targetName, out var targetType))
            {
                return expression;
            }

            var value = match.Groups["value"].Value.Trim();
            var wrappedValue = TryWrapTaggedUnionValue(targetType, value, value);
            return wrappedValue is null ? expression : $"{targetName} = {wrappedValue}";
        }

        private string? TryWrapTaggedUnionValue(string targetType, string sourceExpression, string loweredExpression)
            => _taggedUnionValueBuilder.TryWrap(targetType, sourceExpression, loweredExpression);

        private string? TryWrapTaggedUnionValue(TypeRef targetType, string sourceExpression, string loweredExpression)
            => _taggedUnionValueBuilder.TryWrap(targetType, sourceExpression, loweredExpression);

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
