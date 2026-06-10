namespace Cx.Compiler.C;

internal sealed record CTranslationUnit(IReadOnlyList<CTranslationUnitItem> Items);

internal abstract record CTranslationUnitItem;

internal sealed record CComment(string Text) : CTranslationUnitItem;

internal sealed record CBlankLine : CTranslationUnitItem;

internal sealed record CInclude(string Path, bool IsSystem) : CTranslationUnitItem;

internal sealed record CEnumDeclaration(
    string Name,
    IReadOnlyList<CEnumMember> Members) : CTranslationUnitItem;

internal sealed record CEnumMember(string Name, string? Value);

internal sealed record CStructDeclaration(
    string Name,
    IReadOnlyList<string> FieldDeclarations) : CTranslationUnitItem;

internal sealed record CTaggedUnionDeclaration(
    string Name,
    bool IsRaw,
    IReadOnlyList<CTaggedUnionVariantDeclaration> Variants) : CTranslationUnitItem;

internal sealed record CTaggedUnionVariantDeclaration(
    string Name,
    string TypeName,
    string FieldDeclaration);

internal sealed record CTypeAliasDeclaration(
    string Name,
    string TargetType,
    IReadOnlyList<string>? FunctionParameterTypes = null) : CTranslationUnitItem;

internal sealed record CFunctionDeclaration(
    string ReturnType,
    string Name,
    IReadOnlyList<string> ParameterDeclarations) : CTranslationUnitItem;

internal sealed record CFunctionDefinition(
    string ReturnType,
    string Name,
    IReadOnlyList<string> ParameterDeclarations,
    IReadOnlyList<CStatementNode> Body) : CTranslationUnitItem;

internal sealed record CGlobalDeclaration(
    string Declaration,
    CExpression? Initializer) : CTranslationUnitItem;

internal abstract record CStatementNode;

internal sealed record CBlockStatement(IReadOnlyList<CStatementNode> Body) : CStatementNode;

internal sealed record CLocalDeclarationStatement(
    string Declaration,
    CExpression? Initializer) : CStatementNode;

internal sealed record CReturnStatement(CExpression? Expression) : CStatementNode;

internal sealed record CBreakStatement : CStatementNode;

internal sealed record CContinueStatement : CStatementNode;

internal sealed record CExpressionStatement(CExpression Expression) : CStatementNode;

internal sealed record CIfStatement(
    CExpression Condition,
    IReadOnlyList<CStatementNode> ThenBody,
    CElseClause? ElseClause) : CStatementNode;

internal sealed record CWhileStatement(
    CExpression Condition,
    IReadOnlyList<CStatementNode> Body) : CStatementNode;

internal sealed record CForStatement(
    CForInitializerNode Initializer,
    CExpression Condition,
    CExpression Increment,
    IReadOnlyList<CStatementNode> Body) : CStatementNode;

internal abstract record CForInitializerNode;

internal sealed record CEmptyForInitializer : CForInitializerNode;

internal sealed record CDeclarationForInitializer(
    string Declaration,
    CExpression? Initializer) : CForInitializerNode;

internal sealed record CExpressionForInitializer(
    CExpression Expression) : CForInitializerNode;

internal sealed record CSwitchStatement(
    CExpression Expression,
    IReadOnlyList<CSwitchCase> Cases,
    IReadOnlyList<CStatementNode> DefaultBody) : CStatementNode;

internal sealed record CSwitchCase(
    string Pattern,
    IReadOnlyList<CStatementNode> Body);

internal abstract record CElseClause;

internal sealed record CElseIfClause(CIfStatement IfStatement) : CElseClause;

internal sealed record CElseBlockClause(IReadOnlyList<CStatementNode> Body) : CElseClause;

internal sealed record CRawStatement(string Text) : CStatementNode;

internal sealed record CRawTopLevel(string Text) : CTranslationUnitItem;
