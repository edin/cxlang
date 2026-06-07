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
        private readonly ReceiverExpressionBuilder _receiverExpressionBuilder;
        private readonly InterfaceMemberCallLowerer _interfaceMemberCallLowerer;
        private readonly ResolvedCallLowerer _resolvedCallLowerer;
        public string? SelfType { get; }
        private string? SelfApiType { get; }

        public ImportedNameLowerer(
            ProgramNode program,
            IReadOnlyList<StructNode> concreteStructs,
            RequirementMatcher requirementMatcher)
            : this(
                CLoweringContext.Create(program, concreteStructs, requirementMatcher),
                CLoweringScope.Create(
                    program.GlobalVariables
                        .Where(global => global.Type.EndsWith("*", StringComparison.Ordinal))
                        .Select(global => global.Name)
                        .ToHashSet(StringComparer.Ordinal),
                    program.GlobalVariables
                        .Select(global => (global.Name, global.Type))
                        .GroupBy(item => item.Name, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.Last().Type, StringComparer.Ordinal),
                    program.GlobalVariables
                        .Where(global => global.TypeNode?.Semantic.Type is not null)
                        .Select(global => (global.Name, Type: global.TypeNode!.Semantic.Type!))
                        .GroupBy(item => item.Name, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.Last().Type, StringComparer.Ordinal)))
        {
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
                ReplaceBracedExpressions,
                SplitArguments,
                Lower,
                type => LowerType(type, SelfType));
            _adapterExposeResolver = new AdapterExposeResolver(_context);
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
        }

        public ImportedNameLowerer ForFunction(FunctionNode function)
        {
            var selfType = ResolveSelfType(function);
            var selfApiType = ResolveSelfApiType(function);
            var scope = _scope.ForFunction(function, selfType);

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
            if (!string.IsNullOrWhiteSpace(sizeOf.TypeOperand))
            {
                return new CSizeOfTypeExpression(LowerType(sizeOf.TypeOperand, SelfType));
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
            var closeParen = FindMatchingParen(expression, openParen);
            if (openParen < 0 || closeParen != expression.Length - 1)
            {
                return null;
            }

            var argumentsText = expression[(openParen + 1)..closeParen];
            var arguments = SplitArguments(argumentsText)
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

        string ICExpressionLoweringContext.LowerType(TypeNode? typeNode, string fallbackType) =>
            CEmitter.LowerType(typeNode, fallbackType, SelfType);

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

        private string? LowerCall(CallExpressionNode call)
        {
            if (_resolvedCallLowerer.TryLowerStatic(call.Semantic.ResolvedCall, call.Arguments) is { } resolvedCall)
            {
                return _expressionEmitter.Emit(resolvedCall);
            }

            if (call.Callee is MemberExpressionNode member)
            {
                return LowerMemberCall(member, call.Arguments);
            }

            if (call.Callee is NameExpressionNode name)
            {
                if (_context.TryGetStruct(name.SourceText, out var structNode))
                {
                    return LowerStructConstructor(structNode, call.Arguments.Select(argument => argument.SourceText).ToList());
                }

                if (_context.IsTaggedUnion(name.SourceText))
                {
                    return null;
                }

            var genericCall = _genericCallResolver.FindInferredCall(null, name.SourceText, call.Arguments, skipSelf: false);
                if (genericCall is not null)
                {
                    return EmitCallExpression(
                        new CResolvedFunction(ModuleName: GetFunctionModule(genericCall.OwnerType, genericCall.Name), genericCall.CName),
                        call.Arguments);
                }

                return EmitCallExpression(new CFunctionName(LowerFunctionReferenceName(name)), call.Arguments);
            }

            return null;
        }

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
            if (!string.IsNullOrWhiteSpace(sizeOf.TypeOperand))
            {
                return $"sizeof({LowerType(sizeOf.TypeOperand, SelfType)})";
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

        private CExpression? LowerGenericCallExpression(GenericCallExpressionNode call)
        {
            if (_resolvedCallLowerer.TryLowerStatic(call.Semantic.ResolvedCall, call.Arguments) is { } resolvedCall)
            {
                return resolvedCall;
            }

            if (call.Callee is MemberExpressionNode member)
            {
                var memberCall = LowerGenericMemberCallExpression(member, call.TypeArguments, call.Arguments);
                if (memberCall is not null)
                {
                    return memberCall;
                }
            }

            var calleeName = GetQualifiedName(call.Callee);
            if (calleeName is null)
            {
                return null;
            }

            if (_context.IsGenericMacro(calleeName))
            {
                return new CRawExpression($"{LowerName(calleeName)}({string.Join(", ", call.Arguments.Select(Lower))})");
            }

            var match = _genericCallResolver.FindFreeExact(calleeName, call.TypeArguments);
            if (match is not null)
            {
                return new CCallExpression(
                    new CResolvedFunction(ModuleName: GetFunctionModule(match.OwnerType, match.Name), match.CName),
                    call.Arguments.Select(LowerExpression).ToList());
            }

            var staticMatch = _genericCallResolver.FindStaticExact(calleeName, call.TypeArguments);
            if (staticMatch is null
                && TrySplitQualifiedMember(calleeName, out var ownerName, out var memberName)
                && _context.TryGetAdapterExpose($"{ownerName}.{memberName}", out var staticExpose)
                && staticExpose.IsStatic)
            {
                var resolvedExpose = _adapterExposeResolver.Resolve(staticExpose, call.TypeArguments);
                staticMatch = _genericCallResolver.FindStaticExact(
                    resolvedExpose.BaseOwner,
                    resolvedExpose.SourceName,
                    resolvedExpose.TypeArguments);
            }

            return staticMatch is null
                ? null
                : new CCallExpression(
                    new CResolvedFunction(ModuleName: GetFunctionModule(staticMatch.OwnerType, staticMatch.Name), staticMatch.CName),
                    call.Arguments.Select(LowerExpression).ToList());
        }

        private string? LowerGenericMemberCall(
            MemberExpressionNode member,
            IReadOnlyList<string> typeArguments,
            IReadOnlyList<ExpressionNode> arguments)
        {
            var expression = LowerGenericMemberCallExpression(member, typeArguments, arguments);
            return expression is null ? null : _expressionEmitter.Emit(expression);
        }

        private CExpression? LowerGenericMemberCallExpression(
            MemberExpressionNode member,
            IReadOnlyList<string> typeArguments,
            IReadOnlyList<ExpressionNode> arguments)
        {
            if (_resolvedCallLowerer.TryLowerInstance(member.Semantic.ResolvedCall, member, arguments) is { } resolvedInstanceCall)
            {
                return resolvedInstanceCall;
            }

            if (member.Target is not NameExpressionNode targetName
                || !_scope.TryGetVariableType(targetName.SourceText, out var targetType))
            {
                return null;
            }

            var target = targetName.SourceText;
            var concreteReceiverType = RemovePointer(targetType);
            if (_genericCallResolver.FindGenericMemberExact(
                GetGenericBaseName(targetType),
                concreteReceiverType,
                member.MemberName,
                typeArguments) is { } genericCall)
            {
                var loweredArguments = arguments.Select(LowerExpression).ToList();
                loweredArguments.Insert(0, _receiverExpressionBuilder.Build(target, targetType.EndsWith("*", StringComparison.Ordinal), genericCall.TakesPointerSelf));
                return new CCallExpression(
                    new CResolvedFunction(ModuleName: GetFunctionModule(genericCall.OwnerType, genericCall.Name), genericCall.CName),
                    loweredArguments);
            }

            return null;
        }

        private string? ResolveExpressionType(ExpressionNode expression) => expression switch
        {
            LiteralExpressionNode literal => ResolveLiteralType(literal.SourceText),
            NameExpressionNode name => _scope.GetVariableTypeOrDefault(name.SourceText),
            ParenthesizedExpressionNode parenthesized => ResolveExpressionType(parenthesized.Expression),
            CastExpressionNode cast => cast.TargetType,
            UnaryExpressionNode { Operator: "&" } unary when ResolveExpressionType(unary.Operand) is { } operandType => operandType + "*",
            UnaryExpressionNode { Operator: "*" } unary when ResolveExpressionType(unary.Operand) is { } operandType => UnwrapPointer(operandType),
            UnaryExpressionNode unary => ResolveExpressionType(unary.Operand),
            BinaryExpressionNode binary => ResolveBinaryType(binary),
            ScalarRangeExpressionNode range => ResolveExpressionType(range.Start) ?? ResolveExpressionType(range.End),
            ConditionalExpressionNode conditional => ResolveExpressionType(conditional.WhenTrue) ?? ResolveExpressionType(conditional.WhenFalse),
            CallExpressionNode call => ResolveCallType(call),
            GenericCallExpressionNode call => ResolveGenericCallType(call),
            MemberExpressionNode member => ResolveMemberType(member),
            IndexExpressionNode index => ResolveIndexType(index),
            _ => null,
        };

        public string? ResolveExpressionTypeForLowering(ExpressionNode expression) =>
            ResolveExpressionType(expression);

        private string? ResolveCallType(CallExpressionNode call)
        {
            if (call.Callee is NameExpressionNode name)
            {
                return _genericCallResolver.FindInferredCall(null, name.SourceText, call.Arguments, skipSelf: false)?.ReturnType;
            }

            if (call.Callee is MemberExpressionNode member)
            {
                return ResolveMemberCallType(member, call.Arguments);
            }

            return null;
        }

        private string? ResolveGenericCallType(GenericCallExpressionNode call)
        {
            var calleeName = GetQualifiedName(call.Callee);
            if (calleeName is not null)
            {
                var freeCall = _genericCallResolver.FindFreeExact(calleeName, call.TypeArguments);
                if (freeCall is not null)
                {
                    return freeCall.ReturnType;
                }

                var staticCall = _genericCallResolver.FindStaticExact(calleeName, call.TypeArguments);
                if (staticCall is not null)
                {
                    return staticCall.ReturnType;
                }
            }

            if (call.Callee is MemberExpressionNode member)
            {
                var targetType = ResolveExpressionType(member.Target);
                var owner = targetType is null ? GetQualifiedName(member.Target) : GetGenericBaseName(RemovePointer(NormalizeType(targetType)));
                var match = _genericCallResolver.FindExact(owner, member.MemberName, call.TypeArguments);
                return match?.ReturnType;
            }

            return null;
        }

        private string? ResolveMemberCallType(MemberExpressionNode member, IReadOnlyList<ExpressionNode> arguments)
        {
            if (member.Target is not NameExpressionNode targetName
                || !_scope.TryGetVariableType(targetName.SourceText, out var targetType))
            {
                return null;
            }

            var owner = GetGenericBaseName(targetType);
            return _genericCallResolver.FindInferredCall(owner, member.MemberName, arguments, skipSelf: true)?.ReturnType;
        }

        private string? ResolveMemberType(MemberExpressionNode member)
        {
            var targetType = ResolveExpressionType(member.Target);
            if (targetType is null)
            {
                return null;
            }

            var normalizedType = RemovePointer(NormalizeType(targetType));
            if (_context.TryGetStruct(normalizedType, out var structNode)
                || _context.TryGetStruct(LowerType(normalizedType, SelfType), out structNode))
            {
                return structNode.Fields.FirstOrDefault(field => field.Name == member.MemberName)?.Type;
            }

            return null;
        }

        private string? ResolveIndexType(IndexExpressionNode index)
        {
            var targetType = ResolveExpressionType(index.Target);
            if (targetType is null)
            {
                return null;
            }

            if (TryParseFixedArrayType(targetType, out var elementType, out _))
            {
                return elementType;
            }

            return UnwrapPointer(targetType);
        }

        private string? ResolveBinaryType(BinaryExpressionNode binary)
        {
            if (binary.Operator is "==" or "!=" or "<" or "<=" or ">" or ">=" or "&&" or "||")
            {
                return "bool";
            }

            if (binary.Operator == "<=>")
            {
                return "int";
            }

            return ResolveExpressionType(binary.Left) ?? ResolveExpressionType(binary.Right);
        }

        private static string? ResolveLiteralType(string text)
        {
            text = text.Trim();
            if (text is "true" or "false")
            {
                return "bool";
            }

            if (text.StartsWith("\"", StringComparison.Ordinal))
            {
                return "char*";
            }

            if (text.StartsWith("'", StringComparison.Ordinal))
            {
                return "char";
            }

            if (Regex.IsMatch(text, @"^-?\d+$"))
            {
                return "int";
            }

            if (Regex.IsMatch(text, @"^-?(\d+\.\d*|\d*\.\d+)([eE][+-]?\d+)?$"))
            {
                return "double";
            }

            return null;
        }

        private static bool CanAssign(string targetType, string sourceType) =>
            string.Equals(targetType.Trim(), sourceType.Trim(), StringComparison.Ordinal)
            || (targetType.Trim() == "double" && sourceType.Trim() is "int" or "float")
            || (targetType.Trim() == "float" && sourceType.Trim() == "int")
            || (IsNumericType(targetType) && IsNumericType(sourceType));

        private static bool IsNumericType(string type)
        {
            type = type.Trim();
            return type is
                "char" or
                "signed char" or
                "unsigned char" or
                "short" or
                "unsigned short" or
                "int" or
                "unsigned int" or
                "long" or
                "unsigned long" or
                "long long" or
                "unsigned long long" or
                "usize" or
                "i8" or
                "i16" or
                "i32" or
                "i64" or
                "u8" or
                "u16" or
                "u32" or
                "u64" or
                "float" or
                "double" or
                "long double";
        }

        private static string? UnwrapPointer(string type)
        {
            type = type.Trim();
            return type.EndsWith("*", StringComparison.Ordinal)
                ? type[..^1].TrimEnd()
                : null;
        }

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

        private string LowerMember(MemberExpressionNode member)
        {
            if (TryLowerFunctionReferenceMember(member) is { } functionReference)
            {
                return functionReference;
            }

            var qualifiedMember = $"{GetQualifiedName(member.Target)}.{member.MemberName}";
            if (_context.TryGetEnumMemberAlias(qualifiedMember, out var enumMemberName))
            {
                return enumMemberName;
            }

            var staticMethodKey = $"{GetQualifiedName(member.Target)}.{member.MemberName}";
            if (_context.TryGetMethod(staticMethodKey, out var staticMethod))
            {
                return staticMethod.CName;
            }

            if (GetQualifiedName(member.Target) is { } moduleTarget && IsModuleQualifierTarget(moduleTarget))
            {
                return member.MemberName;
            }

            if (member.Target is NameExpressionNode targetName
                && _scope.TryGetVariableType(targetName.SourceText, out var targetType))
            {
                var targetIsImplicitReference = _scope.IsImplicitReferenceLocal(targetName.SourceText);
                var normalizedType = NormalizeType(targetType);
                if (_context.TryGetTaggedUnion(normalizedType, out var taggedUnion)
                    && taggedUnion.Variants.Any(variant => variant.Name == member.MemberName))
                {
                    if (taggedUnion.IsRaw)
                    {
                        var rawAccess = targetIsImplicitReference || targetType.EndsWith("*", StringComparison.Ordinal)
                            ? "->"
                            : ".";
                        return targetName.SourceText + rawAccess + member.MemberName;
                    }

                    var access = targetIsImplicitReference || targetType.EndsWith("*", StringComparison.Ordinal)
                        ? "->as."
                        : ".as.";
                    return targetName.SourceText + access + member.MemberName;
                }

                if (targetIsImplicitReference || targetType.EndsWith("*", StringComparison.Ordinal))
                {
                    return targetName.SourceText + "->" + member.MemberName;
                }
            }

            return $"{Lower(member.Target)}.{member.MemberName}";
        }

        private CExpression LowerMemberExpression(MemberExpressionNode member)
        {
            if (TryLowerFunctionReferenceMember(member) is { } functionReference)
            {
                return new CNameExpression(functionReference);
            }

            var qualifiedMember = $"{GetQualifiedName(member.Target)}.{member.MemberName}";
            if (_context.TryGetEnumMemberAlias(qualifiedMember, out var enumMemberName))
            {
                return new CNameExpression(enumMemberName);
            }

            var staticMethodKey = $"{GetQualifiedName(member.Target)}.{member.MemberName}";
            if (_context.TryGetMethod(staticMethodKey, out var staticMethod))
            {
                return new CNameExpression(staticMethod.CName);
            }

            if (GetQualifiedName(member.Target) is { } moduleTarget && IsModuleQualifierTarget(moduleTarget))
            {
                return new CNameExpression(member.MemberName);
            }

            if (member.Target is NameExpressionNode targetName
                && _scope.TryGetVariableType(targetName.SourceText, out var targetType))
            {
                var targetIsImplicitReference = _scope.IsImplicitReferenceLocal(targetName.SourceText);
                var normalizedType = NormalizeType(targetType);
                if (_context.TryGetTaggedUnion(normalizedType, out var taggedUnion)
                    && taggedUnion.Variants.Any(variant => variant.Name == member.MemberName))
                {
                    var access = taggedUnion.IsRaw
                        ? targetIsImplicitReference || targetType.EndsWith("*", StringComparison.Ordinal) ? "->" : "."
                        : targetIsImplicitReference || targetType.EndsWith("*", StringComparison.Ordinal) ? "->as." : ".as.";
                    return new CMemberExpression(new CNameExpression(targetName.SourceText), access, member.MemberName);
                }

                if (targetIsImplicitReference || targetType.EndsWith("*", StringComparison.Ordinal))
                {
                    return new CMemberExpression(new CNameExpression(targetName.SourceText), "->", member.MemberName);
                }
            }

            return new CMemberExpression(LowerExpression(member.Target), ".", member.MemberName);
        }

        private string? TryLowerFunctionReferenceMember(MemberExpressionNode member) =>
            member.Semantic is { Symbol: { Kind: SymbolKind.Function } symbol, ResolvedCall.IsInstance: false }
                ? s_nameMangler.SymbolName(symbol)
                : null;

        private CExpression? LowerCallExpression(CallExpressionNode call)
        {
            if (_resolvedCallLowerer.TryLowerStatic(call.Semantic.ResolvedCall, call.Arguments) is { } resolvedCall)
            {
                return resolvedCall;
            }

            if (call.Callee is MemberExpressionNode member)
            {
                if (TryLowerTaggedUnionConstructorExpression(member, call.Arguments) is { } taggedUnionConstructor)
                {
                    return taggedUnionConstructor;
                }

                return LowerMemberCallExpression(member, call.Arguments);
            }

            if (call.Callee is NameExpressionNode name)
            {
                if (_context.TryGetStruct(name.SourceText, out var structNode))
                {
                    return LowerStructConstructorExpression(structNode, call.Arguments);
                }

                if (_context.IsTaggedUnion(name.SourceText))
                {
                    return null;
                }

                var genericCall = _genericCallResolver.FindInferredCall(null, name.SourceText, call.Arguments, skipSelf: false);
                if (genericCall is not null)
                {
                    return new CCallExpression(
                        new CResolvedFunction(ModuleName: GetFunctionModule(genericCall.OwnerType, genericCall.Name), genericCall.CName),
                        call.Arguments.Select(LowerExpression).ToList());
                }

                return new CCallExpression(
                    new CFunctionName(LowerFunctionReferenceName(name)),
                    call.Arguments.Select(LowerExpression).ToList());
            }

            return null;
        }

        private CExpression? TryLowerTaggedUnionConstructorExpression(
            MemberExpressionNode member,
            IReadOnlyList<ExpressionNode> arguments)
        {
            if (GetQualifiedName(member.Target) is not { } targetName)
            {
                return null;
            }

            return _taggedUnionValueBuilder.TryBuildConstructorExpression(
                targetName,
                member.MemberName,
                arguments,
                LowerPayloadConstructorExpression);
        }

        private CExpression LowerPayloadConstructorExpression(
            string payloadType,
            IReadOnlyList<ExpressionNode> arguments) =>
            _structValueBuilder.BuildPayloadExpression(payloadType, arguments, LowerPayloadConstructor);

        private CExpression LowerStructConstructorExpression(
            StructNode structNode,
            IReadOnlyList<ExpressionNode> arguments) =>
            _structValueBuilder.BuildStructConstructorExpression(structNode, arguments, LowerStructConstructor);

        private string? LowerMemberCall(
            MemberExpressionNode member,
            IReadOnlyList<ExpressionNode> arguments)
        {
            var expression = LowerMemberCallExpression(member, arguments);
            return expression is null ? null : _expressionEmitter.Emit(expression);
        }

        private CExpression? LowerMemberCallExpression(
            MemberExpressionNode member,
            IReadOnlyList<ExpressionNode> arguments)
        {
            if (_interfaceMemberCallLowerer.TryLower(member, arguments) is { } interfaceCall)
            {
                return interfaceCall;
            }

            if (TryLowerKnownMemberCallExpression(member, arguments) is { } knownMemberCall)
            {
                return knownMemberCall;
            }

            if (member.Target is not NameExpressionNode targetName)
            {
                return null;
            }

            var target = targetName.SourceText;
            if (!_scope.TryGetVariableType(target, out var targetType))
            {
                var staticGenericCall = _genericCallResolver.FindInferredCall(target, member.MemberName, arguments, skipSelf: false);
                if (staticGenericCall is not null)
                {
                    return new CCallExpression(
                        new CResolvedFunction(ModuleName: GetFunctionModule(staticGenericCall.OwnerType, staticGenericCall.Name), staticGenericCall.CName),
                        arguments.Select(LowerExpression).ToList());
                }

                var staticMethodKey = $"{target}.{member.MemberName}";
                if (_context.TryGetMethod(staticMethodKey, out var staticMethod))
                {
                    return new CCallExpression(
                        new CResolvedFunction(target, staticMethod.CName),
                        arguments.Select(LowerExpression).ToList());
                }

                return IsModuleQualifierTarget(target)
                    ? new CCallExpression(
                        new CFunctionName(member.MemberName),
                        arguments.Select(LowerExpression).ToList())
                    : null;
            }

            if (_resolvedCallLowerer.TryLowerInstance(member.Semantic.ResolvedCall, member, arguments) is { } resolvedInstanceCall)
            {
                return resolvedInstanceCall;
            }

            var normalizedType = NormalizeType(targetType);
            var isPointer = targetType.EndsWith("*", StringComparison.Ordinal);
            var receiverArguments = TryParseGenericUse(RemovePointer(normalizedType), out _, out var parsedReceiverArguments)
                ? parsedReceiverArguments
                : [];
            var genericMemberCall = _genericCallResolver.FindInferredMemberCall(
                GetGenericBaseName(RemovePointer(normalizedType)),
                RemovePointer(normalizedType),
                member.MemberName,
                arguments,
                skipSelf: true,
                preferredTypeArguments: receiverArguments);
            if (genericMemberCall is not null)
            {
                var loweredArguments = arguments.Select(LowerExpression).ToList();
                loweredArguments.Insert(0, _receiverExpressionBuilder.Build(target, isPointer, genericMemberCall.TakesPointerSelf));
                return new CCallExpression(
                    new CResolvedFunction(ModuleName: GetFunctionModule(genericMemberCall.OwnerType, genericMemberCall.Name), genericMemberCall.CName),
                    loweredArguments);
            }

            foreach (var methodInfo in _context.GetInstanceMethodsForReceiver(normalizedType))
            {
                if (methodInfo.Name != member.MemberName)
                {
                    continue;
                }

                var loweredArguments = arguments.Select(LowerExpression).ToList();
                loweredArguments.Insert(0, _receiverExpressionBuilder.Build(target, isPointer, methodInfo.TakesPointerSelf));
                return new CCallExpression(
                    new CResolvedFunction(NormalizeType(targetType), methodInfo.CName),
                    loweredArguments);
            }

            return null;
        }

        private CExpression? TryLowerKnownMemberCallExpression(
            MemberExpressionNode member,
            IReadOnlyList<ExpressionNode> arguments)
        {
            if (member.Target is NameExpressionNode)
            {
                return null;
            }

            var targetType = ResolveExpressionType(member.Target);
            if (targetType is null)
            {
                return null;
            }

            var normalizedType = NormalizeType(targetType);
            var isPointer = targetType.EndsWith("*", StringComparison.Ordinal);
            var receiverType = RemovePointer(normalizedType);
            var receiverArguments = TryParseGenericUse(receiverType, out _, out var parsedReceiverArguments)
                ? parsedReceiverArguments
                : [];
            var ownerType = GetGenericBaseName(receiverType) ?? receiverType;

            var genericMemberCall = _genericCallResolver.FindInferredCall(
                ownerType,
                member.MemberName,
                arguments,
                skipSelf: true,
                preferredTypeArguments: receiverArguments);
            if (genericMemberCall is not null)
            {
                var targetExpression = LowerExpression(member.Target);
                var loweredArguments = arguments.Select(LowerExpression).ToList();
                loweredArguments.Insert(0, ReceiverExpressionBuilder.Build(targetExpression, isPointer, genericMemberCall.TakesPointerSelf));
                return new CCallExpression(
                    new CResolvedFunction(ModuleName: GetFunctionModule(genericMemberCall.OwnerType, genericMemberCall.Name), genericMemberCall.CName),
                    loweredArguments);
            }

            foreach (var methodInfo in _context.GetInstanceMethodsForReceiver(receiverType))
            {
                if (methodInfo.Name != member.MemberName)
                {
                    continue;
                }

                var targetExpression = LowerExpression(member.Target);
                var loweredArguments = arguments.Select(LowerExpression).ToList();
                loweredArguments.Insert(0, ReceiverExpressionBuilder.Build(targetExpression, isPointer, methodInfo.TakesPointerSelf));
                return new CCallExpression(
                    new CResolvedFunction(receiverType, methodInfo.CName),
                    loweredArguments);
            }

            return null;
        }

        private CExpression? LowerAdapterExposeCall(
            AdapterExposeInfo adapterExpose,
            IReadOnlyList<string> receiverArguments,
            IReadOnlyList<ExpressionNode> arguments,
            string target,
            bool isPointer)
        {
            var resolvedExpose = _adapterExposeResolver.Resolve(adapterExpose, receiverArguments);
            var genericBaseCall = _genericCallResolver.FindInferredCall(
                resolvedExpose.BaseOwner,
                resolvedExpose.SourceName,
                arguments,
                skipSelf: true,
                preferredTypeArguments: resolvedExpose.TypeArguments)
                ?? _genericCallResolver.FindExact(
                    resolvedExpose.BaseOwner,
                    resolvedExpose.SourceName,
                    resolvedExpose.TypeArguments);
            if (genericBaseCall is not null)
            {
                var loweredArguments = arguments.Select(LowerExpression).ToList();
                loweredArguments.Insert(0, _receiverExpressionBuilder.Build(target, isPointer, takesPointerSelf: true));
                return new CCallExpression(
                    new CResolvedFunction(ModuleName: GetFunctionModule(genericBaseCall.OwnerType, genericBaseCall.Name), genericBaseCall.CName),
                    loweredArguments);
            }

            var baseMethodKey = $"{resolvedExpose.BaseOwner}.{resolvedExpose.SourceName}";
            if (_context.TryGetMethod(baseMethodKey, out var baseMethod))
            {
                var loweredArguments = arguments.Select(LowerExpression).ToList();
                loweredArguments.Insert(0, _receiverExpressionBuilder.Build(target, isPointer, takesPointerSelf: true));
                return new CCallExpression(
                    new CResolvedFunction(GetFunctionModule(resolvedExpose.BaseOwner, resolvedExpose.SourceName) ?? resolvedExpose.BaseOwner, baseMethod.CName),
                    loweredArguments);
            }

            return null;
        }

        private string EmitCallExpression(CFunctionReference function, IReadOnlyList<ExpressionNode> arguments) =>
            _expressionEmitter.Emit(new CCallExpression(
                function,
                arguments.Select(argument => new CRawExpression(Lower(argument))).ToList()));

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
            expression = LowerExplicitGenericStaticCalls(expression);

            expression = LowerTaggedUnionConstructors(expression);
            expression = LowerNamedStructInitializers(expression);
            expression = LowerNamedInterfaceInitializers(expression);
            expression = LowerStructConstructors(expression);

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
                expression = LowerAdapterExposeCalls(expression, variable, argument, receiverApiName, receiverApiArguments);

                var adapterName = GetGenericBaseName(receiverType) ?? receiverType;
                expression = LowerAdapterExposeCalls(expression, variable, argument, adapterName, receiverArguments);
                foreach (var expose in _context.GetInstanceAdapterExposes())
                {
                    var baseType = _adapterExposeResolver.SubstituteBaseType(expose, receiverArguments);
                    if (LowerType(baseType) == LowerType(receiverType))
                    {
                        expression = LowerAdapterExposeCall(expression, variable, argument, expose, receiverArguments);
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

        private string LowerExplicitGenericStaticCalls(string expression)
        {
            foreach (var call in _genericCallResolver.GetStaticOrFreeCalls())
            {
                var sourceName = call.OwnerType is null ? call.Name : $"{call.OwnerType}.{call.Name}";
                var source = Regex.Escape(sourceName).Replace("\\.", @"\s*\.\s*")
                    + @"\s*<\s*"
                    + string.Join(@"\s*,\s*", call.TypeArguments.Select(Regex.Escape))
                    + @"\s*>\s*\(";
                expression = Regex.Replace(expression, source, call.CName + "(");
            }

            return expression;
        }

        private string LowerTaggedUnionConstructors(string expression)
        {
            foreach (var taggedUnion in _context.GetTaggedUnions())
            {
                if (taggedUnion.IsRaw)
                {
                    continue;
                }

                foreach (var variant in taggedUnion.Variants)
                {
                    expression = ReplaceCallExpressions(
                        expression,
                        $"{taggedUnion.Name}.{variant.Name}",
                        arguments => LowerTaggedUnionConstructor(taggedUnion, variant, arguments));
                }
            }

            return expression;
        }

        private string LowerTaggedUnionConstructor(
            TaggedUnionNode taggedUnion,
            TaggedUnionVariantNode variant,
            IReadOnlyList<string> arguments) =>
            _taggedUnionValueBuilder.BuildConstructorText(
                taggedUnion,
                variant,
                arguments,
                LowerPayloadConstructor);

        private string LowerStructConstructors(string expression)
        {
            foreach (var structNode in _context.GetStructs())
            {
                expression = ReplaceCallExpressions(
                    expression,
                    structNode.Name,
                    arguments => LowerStructConstructor(structNode, arguments));
            }

            return expression;
        }

        private string LowerAdapterExposeCalls(
            string expression,
            string variable,
            string receiver,
            string adapterName,
            IReadOnlyList<string> receiverArguments)
        {
            foreach (var expose in _context.GetInstanceAdapterExposes(adapterName))
            {
                expression = LowerAdapterExposeCall(expression, variable, receiver, expose, receiverArguments);
            }

            return expression;
        }

        private string LowerAdapterExposeCall(
            string expression,
            string variable,
            string receiver,
            AdapterExposeInfo expose,
            IReadOnlyList<string> receiverArguments)
        {
            var resolvedExpose = _adapterExposeResolver.Resolve(expose, receiverArguments);
            var genericBaseCall = _genericCallResolver.FindExact(
                resolvedExpose.BaseOwner,
                resolvedExpose.SourceName,
                resolvedExpose.TypeArguments);

            var cName = genericBaseCall?.CName;
            if (cName is null)
            {
                if (!_context.TryGetMethod($"{resolvedExpose.BaseOwner}.{resolvedExpose.SourceName}", out var baseMethod))
                {
                    return expression;
                }

                cName = baseMethod.CName;
            }

            expression = Regex.Replace(
                expression,
                $@"\b{Regex.Escape(variable)}\.{Regex.Escape(expose.ExposedName)}\(\s*\)",
                $"{cName}({receiver})");
            return Regex.Replace(
                expression,
                $@"\b{Regex.Escape(variable)}\.{Regex.Escape(expose.ExposedName)}\(",
                $"{cName}({receiver}, ");
        }

        private string LowerNamedStructInitializers(string expression) =>
            _namedInitializerTextLowerer.LowerStructInitializers(expression);

        private string LowerNamedInterfaceInitializers(string expression) =>
            _namedInitializerTextLowerer.LowerInterfaceInitializers(expression);

        private string LowerPayloadConstructor(string payloadType, IReadOnlyList<string> arguments)
            => _structValueBuilder.BuildPayloadText(payloadType, arguments, LowerStructConstructor);

        private string LowerStructConstructor(StructNode structNode, IReadOnlyList<string> arguments)
            => _structValueBuilder.BuildStructConstructorText(structNode, arguments);

        private static string ReplaceBracedExpressions(
            string expression,
            string callee,
            Func<string, string> replacementFactory)
        {
            var searchStart = 0;
            while (searchStart < expression.Length)
            {
                var index = expression.IndexOf(callee, searchStart, StringComparison.Ordinal);
                if (index < 0)
                {
                    break;
                }

                if (index > 0 && IsIdentifierPart(expression[index - 1]))
                {
                    searchStart = index + callee.Length;
                    continue;
                }

                var scan = index + callee.Length;
                while (scan < expression.Length && char.IsWhiteSpace(expression[scan]))
                {
                    scan++;
                }

                if (scan >= expression.Length || expression[scan] != '{')
                {
                    searchStart = index + callee.Length;
                    continue;
                }

                var closeBrace = FindMatchingBrace(expression, scan);
                if (closeBrace < 0)
                {
                    break;
                }

                var initializerText = expression[(scan + 1)..closeBrace];
                var replacement = replacementFactory(initializerText);
                expression = expression[..index] + replacement + expression[(closeBrace + 1)..];
                searchStart = index + replacement.Length;
            }

            return expression;
        }

        private static string ReplaceCallExpressions(
            string expression,
            string callee,
            Func<IReadOnlyList<string>, string> replacementFactory)
        {
            var searchStart = 0;
            while (searchStart < expression.Length)
            {
                var index = expression.IndexOf(callee + "(", searchStart, StringComparison.Ordinal);
                if (index < 0)
                {
                    break;
                }

                if (index > 0 && IsIdentifierPart(expression[index - 1]))
                {
                    searchStart = index + callee.Length;
                    continue;
                }

                var openParen = index + callee.Length;
                var closeParen = FindMatchingParen(expression, openParen);
                if (closeParen < 0)
                {
                    break;
                }

                var argumentsText = expression[(openParen + 1)..closeParen];
                var arguments = SplitArguments(argumentsText);
                var replacement = replacementFactory(arguments);
                expression = expression[..index] + replacement + expression[(closeParen + 1)..];
                searchStart = index + replacement.Length;
            }

            return expression;
        }

        private static int FindMatchingBrace(string text, int openBrace)
        {
            var depth = 0;
            for (var i = openBrace; i < text.Length; i++)
            {
                if (text[i] == '{')
                {
                    depth++;
                    continue;
                }

                if (text[i] != '}')
                {
                    continue;
                }

                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }

            return -1;
        }

        private static int FindMatchingParen(string text, int openParen)
        {
            var depth = 0;
            for (var i = openParen; i < text.Length; i++)
            {
                if (text[i] == '(')
                {
                    depth++;
                    continue;
                }

                if (text[i] != ')')
                {
                    continue;
                }

                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }

            return -1;
        }

        private static IReadOnlyList<string> SplitArguments(string argumentsText)
        {
            if (string.IsNullOrWhiteSpace(argumentsText))
            {
                return [];
            }

            var arguments = new List<string>();
            var start = 0;
            var depth = 0;

            for (var i = 0; i < argumentsText.Length; i++)
            {
                depth += argumentsText[i] switch
                {
                    '(' or '[' or '{' => 1,
                    ')' or ']' or '}' => -1,
                    _ => 0
                };

                if (argumentsText[i] != ',' || depth != 0)
                {
                    continue;
                }

                arguments.Add(argumentsText[start..i].Trim());
                start = i + 1;
            }

            arguments.Add(argumentsText[start..].Trim());
            return arguments;
        }

        private static bool IsIdentifierPart(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

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
