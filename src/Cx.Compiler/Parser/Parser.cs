using Cx.Compiler.Diagnostics;
using Cx.Compiler.Lexer;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Parser;

public sealed partial class Parser
{
    private readonly DiagnosticBag _diagnostics;
    private TokenStream? _tokens;
    private int _pendingTypeCloseAngles;

    public Parser(DiagnosticBag? diagnostics = null)
    {
        _diagnostics = diagnostics ?? new DiagnosticBag();
    }

    public DiagnosticBag Diagnostics => _diagnostics;

    public ProgramNode Parse(SourceFile sourceFile)
    {
        _tokens = new TokenStream(new Lexer.Lexer(sourceFile, _diagnostics)
            .Tokenize()
            .Where(token => token.Type is not TokenType.Comment and not TokenType.MultilineComment)
            .ToList());
        _pendingTypeCloseAngles = 0;

        ModuleDeclarationNode? module = null;
        var imports = new List<ImportNode>();
        var symbolImports = new List<SymbolImportNode>();
        var includes = new List<IncludeNode>();
        var cDeclarations = new List<CDeclareNode>();
        var externFunctions = new List<ExternFunctionNode>();
        var attributeDeclarations = new List<AttributeDeclarationNode>();
        var typeAliases = new List<TypeAliasNode>();
        var requirements = new List<RequirementNode>();
        var enums = new List<EnumNode>();
        var interfaces = new List<InterfaceNode>();
        var structs = new List<StructNode>();
        var typeAdapters = new List<TypeAdapterNode>();
        var extensions = new List<ExtensionNode>();
        var taggedUnions = new List<TaggedUnionNode>();
        var globalVariables = new List<GlobalVariableNode>();
        var functions = new List<FunctionNode>();
        var declarations = new List<TopLevelNode>();
        var location = new Location(sourceFile, 0, 1, 1);

        while (!IsAtEnd)
        {
            var attributes = ParseAttributeApplications();

            if (Check(TokenType.Module))
            {
                ReportUnexpectedAttributes(attributes, "module declarations");
                module = ParseModuleDeclaration();
                if (module is not null)
                {
                    declarations.Add(module);
                }

                continue;
            }

            if (Check(TokenType.Import))
            {
                ReportUnexpectedAttributes(attributes, "imports");
                if (ParseImport() is { } import)
                {
                    imports.Add(import);
                    declarations.Add(import);
                }

                continue;
            }

            if (Check(TokenType.From))
            {
                ReportUnexpectedAttributes(attributes, "imports");
                if (ParseSymbolImport() is { } symbolImport)
                {
                    symbolImports.Add(symbolImport);
                    declarations.Add(symbolImport);
                }

                continue;
            }

            if (Check(TokenType.Include))
            {
                ReportUnexpectedAttributes(attributes, "includes");
                if (ParseInclude() is { } include)
                {
                    includes.Add(include);
                    declarations.Add(include);
                }

                continue;
            }

            if (Check(TokenType.Declare))
            {
                ReportUnexpectedAttributes(attributes, "C declarations");
                if (ParseCDeclare() is { } cDeclare)
                {
                    cDeclarations.Add(cDeclare);
                    declarations.Add(cDeclare);
                }

                continue;
            }

            if (Check(TokenType.Extern))
            {
                if (ParseExternFunction(attributes) is { } externFunction)
                {
                    externFunctions.Add(externFunction);
                    declarations.Add(externFunction);
                }

                continue;
            }

            if (Check(TokenType.Type))
            {
                if (ParseTypeDeclaration(attributes) is { } typeDeclaration)
                {
                    declarations.Add(typeDeclaration);
                    switch (typeDeclaration)
                    {
                        case TypeAliasNode typeAlias:
                            typeAliases.Add(typeAlias);
                            break;
                        case TypeAdapterNode typeAdapter:
                            typeAdapters.Add(typeAdapter);
                            break;
                    }
                }

                continue;
            }

            if (Check(TokenType.Fn))
            {
                if (ParseFunction(attributes) is { } function)
                {
                    functions.Add(function);
                    declarations.Add(function);
                }

                continue;
            }

            if (Match(TokenType.Let) is { } letToken)
            {
                if (ParseGlobalVariable(letToken, isConst: false, attributes) is { } global)
                {
                    globalVariables.Add(global);
                    declarations.Add(global);
                }

                continue;
            }

            if (Match(TokenType.Const) is { } constToken)
            {
                if (ParseGlobalVariable(constToken, isConst: true, attributes) is { } global)
                {
                    globalVariables.Add(global);
                    declarations.Add(global);
                }

                continue;
            }

            if (Check(TokenType.Static))
            {
                if (ParseStaticFunction(attributes) is { } function)
                {
                    functions.Add(function);
                    declarations.Add(function);
                }

                continue;
            }

            if (Check(TokenType.Requires))
            {
                ReportUnexpectedAttributes(attributes, "requirements");
                if (ParseRequirement() is { } requirement)
                {
                    requirements.Add(requirement);
                    declarations.Add(requirement);
                }

                continue;
            }

            if (Check(TokenType.Struct))
            {
                if (ParseStruct(attributes) is { } structNode)
                {
                    structs.Add(structNode);
                    declarations.Add(structNode);
                }

                continue;
            }

            if (Check(TokenType.Extension))
            {
                if (ParseExtension(attributes) is { } extension)
                {
                    extensions.Add(extension);
                    declarations.Add(extension);
                }

                continue;
            }

            if (Check(TokenType.Interface))
            {
                if (ParseInterface(attributes) is { } interfaceNode)
                {
                    interfaces.Add(interfaceNode);
                    declarations.Add(interfaceNode);
                }

                continue;
            }

            if (Check(TokenType.Enum))
            {
                if (ParseEnum(attributes) is { } enumNode)
                {
                    enums.Add(enumNode);
                    declarations.Add(enumNode);
                }

                continue;
            }

            if (Check(TokenType.Raw) && PeekType() == TokenType.Union)
            {
                var rawToken = Expect(TokenType.Raw, "Expected 'raw'.");
                if (ParseTaggedUnion(attributes, isRaw: true, rawLocation: rawToken?.Location) is { } rawUnion)
                {
                    taggedUnions.Add(rawUnion);
                    declarations.Add(rawUnion);
                }

                continue;
            }

            if (Check(TokenType.Union))
            {
                if (ParseTaggedUnion(attributes, isRaw: false) is { } taggedUnion)
                {
                    taggedUnions.Add(taggedUnion);
                    declarations.Add(taggedUnion);
                }

                continue;
            }

            if (Check(TokenType.Attribute))
            {
                ReportUnexpectedAttributes(attributes, "attribute declarations");
                if (ParseAttributeDeclaration() is { } attributeDeclaration)
                {
                    attributeDeclarations.Add(attributeDeclaration);
                    declarations.Add(attributeDeclaration);
                }

                continue;
            }

            if (IsContextualKeyword("test"))
            {
                if (ParseTest(attributes) is { } test)
                {
                    declarations.Add(test);
                }

                continue;
            }

            _diagnostics.Report(Current.Location, $"Unexpected token '{Current.Value}'.");
            SynchronizeTopLevel();
        }

        return new ProgramNode(location, declarations);
    }

    private TestNode? ParseTest(IReadOnlyList<AttributeApplicationNode> attributes)
    {
        var testToken = Expect(TokenType.Identifier, "Expected 'test'.");
        var nameToken = Expect(TokenType.String, "Expected test name.");
        var body = ParseBlock();

        return testToken is null
            ? null
            : new TestNode(testToken.Location, nameToken?.Value.Trim('"') ?? string.Empty, body, attributes);
    }

    private ModuleDeclarationNode? ParseModuleDeclaration()
    {
        var moduleToken = Expect(TokenType.Module, "Expected 'module'.");
        var name = ParseModulePath();
        Expect(TokenType.Semicolon, "Expected ';' after module declaration.");

        return moduleToken is null ? null : new ModuleDeclarationNode(moduleToken.Location, name);
    }

    private ImportNode? ParseImport()
    {
        var importToken = Expect(TokenType.Import, "Expected 'import'.");
        var moduleName = ParseModulePath();
        string? alias = null;

        if (ConsumeOptional(TokenType.As))
        {
            alias = Expect(TokenType.Identifier, "Expected alias after 'as'.")?.Value;
        }

        Expect(TokenType.Semicolon, "Expected ';' after import.");
        return importToken is null ? null : new ImportNode(importToken.Location, moduleName, alias);
    }

    private SymbolImportNode? ParseSymbolImport()
    {
        var fromToken = Expect(TokenType.From, "Expected 'from'.");
        var moduleName = ParseModulePath();
        Expect(TokenType.Import, "Expected 'import' after module path.");

        var symbols = new List<ImportedSymbolNode>();
        do
        {
            var symbolToken = Expect(TokenType.Identifier, "Expected imported symbol name.");
            string? alias = null;

            if (ConsumeOptional(TokenType.As))
            {
                alias = Expect(TokenType.Identifier, "Expected alias after 'as'.")?.Value;
            }

            if (symbolToken is not null)
            {
                symbols.Add(new ImportedSymbolNode(symbolToken.Location, symbolToken.Value, alias));
            }
        }
        while (ConsumeOptional(TokenType.Comma));

        Expect(TokenType.Semicolon, "Expected ';' after symbol import.");
        return fromToken is null ? null : new SymbolImportNode(fromToken.Location, moduleName, symbols);
    }

    private IncludeNode? ParseInclude()
    {
        var includeToken = Expect(TokenType.Include, "Expected 'include'.");
        string path;
        var isSystem = false;

        if (ConsumeOptional(TokenType.LessThan))
        {
            isSystem = true;
            path = ReadSystemIncludePath();
            Expect(TokenType.GreaterThan, "Expected '>' after include path.");
        }
        else if (Expect(TokenType.String, "Expected include path.") is { } pathToken)
        {
            path = pathToken.Value.Trim('"');
        }
        else
        {
            path = string.Empty;
        }

        Expect(TokenType.Semicolon, "Expected ';' after include.");
        return includeToken is null ? null : new IncludeNode(includeToken.Location, path, isSystem);
    }

    private string ReadSystemIncludePath()
    {
        var parts = new List<string>();
        while (!IsAtEnd && !Check(TokenType.GreaterThan))
        {
            parts.Add(Advance().Value);
        }

        return string.Concat(parts);
    }

    private CDeclareNode? ParseCDeclare()
    {
        var declareToken = Expect(TokenType.Declare, "Expected 'declare'.");
        string path;
        var isSystem = false;

        if (ConsumeOptional(TokenType.LessThan))
        {
            isSystem = true;
            path = ReadSystemIncludePath();
            Expect(TokenType.GreaterThan, "Expected '>' after declared header path.");
        }
        else if (Expect(TokenType.String, "Expected declared header path.") is { } pathToken)
        {
            path = pathToken.Value.Trim('"');
        }
        else
        {
            path = string.Empty;
        }

        Expect(TokenType.LBrace, "Expected '{' before C declaration block.");
        var links = new List<CLinkNode>();
        var types = new List<TypeAliasNode>();
        var enums = new List<EnumNode>();
        var structs = new List<StructNode>();
        var unions = new List<TaggedUnionNode>();
        var constants = new List<GlobalVariableNode>();
        var functions = new List<ExternFunctionNode>();

        while (!IsAtEnd && !Check(TokenType.RBrace))
        {
            if (Check(TokenType.Link))
            {
                if (ParseCDeclareLink() is { } link)
                {
                    links.Add(link);
                }

                continue;
            }

            if (Check(TokenType.Type))
            {
                if (ParseCDeclareType() is { } type)
                {
                    types.Add(type);
                }

                continue;
            }

            if (Check(TokenType.Const))
            {
                if (ParseCDeclareConstant(isMacro: false) is { } constant)
                {
                    constants.Add(constant);
                }

                continue;
            }

            if (Check(TokenType.Macro))
            {
                var macroToken = Expect(TokenType.Macro, "Expected 'macro'.");
                if (Check(TokenType.Const))
                {
                    if (ParseCDeclareConstant(isMacro: true) is { } constant)
                    {
                        constants.Add(constant);
                    }

                    continue;
                }

                if (Check(TokenType.Fn))
                {
                    if (ParseCDeclareFunction(isMacro: true) is { } function)
                    {
                        functions.Add(function);
                    }

                    continue;
                }

                _diagnostics.Report(macroToken?.Location ?? Current.Location, "Expected 'const' or 'fn' after 'macro'.");
                continue;
            }

            if (Check(TokenType.Struct))
            {
                if (ParseCDeclareStruct() is { } structNode)
                {
                    structs.Add(structNode);
                }

                continue;
            }

            if (Check(TokenType.Enum))
            {
                if (ParseEnum([], isHeaderDeclaration: true) is { } enumNode)
                {
                    enums.Add(enumNode);
                }

                continue;
            }

            if (Check(TokenType.Raw) && PeekType() == TokenType.Union)
            {
                var rawToken = Expect(TokenType.Raw, "Expected 'raw'.");
                if (ParseTaggedUnion([], isRaw: true, isHeaderDeclaration: true, rawLocation: rawToken?.Location) is { } rawUnion)
                {
                    unions.Add(rawUnion);
                }

                continue;
            }

            if (Check(TokenType.Fn))
            {
                if (ParseCDeclareFunction(isMacro: false) is { } function)
                {
                    functions.Add(function);
                }

                continue;
            }

            _diagnostics.Report(Current.Location, $"Unexpected token '{Current.Value}' in C declaration block.");
            Advance();
        }

        Expect(TokenType.RBrace, "Expected '}' after C declaration block.");
        ConsumeOptional(TokenType.Semicolon);

        return declareToken is null
            ? null
            : new CDeclareNode(declareToken.Location, path, isSystem, links, types, enums, structs, unions, constants, functions);
    }

    private CLinkNode? ParseCDeclareLink()
    {
        var linkToken = Expect(TokenType.Link, "Expected 'link'.");
        string? platform = null;
        if (Current.Type == TokenType.Identifier && PeekType() == TokenType.String)
        {
            platform = Advance().Value;
        }

        var libraryToken = Expect(TokenType.String, "Expected library name after 'link'.");
        Expect(TokenType.Semicolon, "Expected ';' after link declaration.");
        return linkToken is null || libraryToken is null
            ? null
            : new CLinkNode(linkToken.Location, platform, libraryToken.Value.Trim('"'));
    }

    private TypeAliasNode? ParseCDeclareType()
    {
        var typeToken = Expect(TokenType.Type, "Expected 'type'.");
        var nameToken = Expect(TokenType.Identifier, "Expected declared type name.");
        Expect(TokenType.Equals, "Expected '=' after declared type name.");

        var targetTypeNode = TypeNode.Named(nameToken?.Location ?? typeToken?.Location ?? Current.Location, "opaque");
        if (!ConsumeOptional(TokenType.Opaque))
        {
            targetTypeNode = ParseTypeNode();
        }

        Expect(TokenType.Semicolon, "Expected ';' after declared type.");
        return typeToken is null || nameToken is null
            ? null
            : new TypeAliasNode(typeToken.Location, nameToken.Value, [], IsHeaderDeclaration: true, TargetTypeNode: targetTypeNode);
    }

    private GlobalVariableNode? ParseCDeclareConstant(bool isMacro)
    {
        var constToken = Expect(TokenType.Const, "Expected 'const'.");
        var nameToken = Expect(TokenType.Identifier, "Expected declared constant name.");
        Expect(TokenType.Colon, "Expected ':' after declared constant name.");
        var typeNode = ParseTypeNode();
        Expect(TokenType.Semicolon, "Expected ';' after declared constant.");
        return constToken is null || nameToken is null
            ? null
            : new GlobalVariableNode(constToken.Location, IsConst: true, nameToken.Value, Initializer: null, [], IsHeaderDeclaration: true, IsMacro: isMacro, TypeNode: typeNode);
    }

    private StructNode? ParseCDeclareStruct()
    {
        var structToken = Expect(TokenType.Struct, "Expected 'struct'.");
        var nameToken = Expect(TokenType.Identifier, "Expected declared struct name.");
        Expect(TokenType.LBrace, "Expected '{' before declared struct fields.");

        var fields = new List<StructFieldNode>();
        while (!IsAtEnd && !Check(TokenType.RBrace))
        {
            var fieldAttributes = ParseAttributeApplications();
            var fieldToken = Expect(TokenType.Identifier, "Expected declared struct field name.");
            Expect(TokenType.Colon, "Expected ':' after declared struct field name.");
            var typeNode = ParseTypeNode();
            Expect(TokenType.Semicolon, "Expected ';' after declared struct field.");

            if (fieldToken is not null)
            {
                fields.Add(new StructFieldNode(fieldToken.Location, fieldToken.Value, fieldAttributes, typeNode));
            }
        }

        Expect(TokenType.RBrace, "Expected '}' after declared struct fields.");
        ConsumeOptional(TokenType.Semicolon);

        return structToken is null
            ? null
            : new StructNode(
                structToken.Location,
                nameToken?.Value ?? string.Empty,
                [],
                [],
                [],
                fields,
                [],
                [],
                IsHeaderDeclaration: true);
    }

    private ExternFunctionNode? ParseCDeclareFunction(bool isMacro)
    {
        var fnToken = Expect(TokenType.Fn, "Expected 'fn'.");
        var nameToken = Expect(TokenType.Identifier, "Expected declared function name.");
        var typeParameters = ParseOptionalTypeParameters();
        Expect(TokenType.LParen, "Expected '(' after declared function name.");

        var parameters = new List<ParameterNode>();
        if (!Check(TokenType.RParen))
        {
            do
            {
                var parameter = ParseParameter(allowVariadic: true);
                if (parameter is not null)
                {
                    parameters.Add(parameter);
                }
            }
            while (ConsumeOptional(TokenType.Comma));
        }

        ValidateVariadicParameter(parameters);
        Expect(TokenType.RParen, "Expected ')' after declared function parameters.");
        Expect(TokenType.Arrow, "Expected '->' before declared function return type.");
        var returnTypeNode = ParseTypeNode();
        Expect(TokenType.Semicolon, "Expected ';' after declared function.");

        return fnToken is null
            ? null
            : new ExternFunctionNode(
                fnToken.Location,
                nameToken?.Value ?? string.Empty,
                typeParameters,
                parameters,
                [],
                IsHeaderDeclaration: true,
                IsMacro: isMacro,
                ReturnTypeNode: returnTypeNode);
    }

    private ExternFunctionNode? ParseExternFunction(IReadOnlyList<AttributeApplicationNode> attributes)
    {
        var externToken = Expect(TokenType.Extern, "Expected 'extern'.");
        Expect(TokenType.Fn, "Expected 'fn' after 'extern'.");
        var nameToken = Expect(TokenType.Identifier, "Expected extern function name.");
        Expect(TokenType.LParen, "Expected '(' after extern function name.");

        var parameters = new List<ParameterNode>();
        if (!Check(TokenType.RParen))
        {
            do
            {
                var parameter = ParseParameter(allowVariadic: true);
                if (parameter is not null)
                {
                    parameters.Add(parameter);
                }
            }
            while (ConsumeOptional(TokenType.Comma));
        }

        ValidateVariadicParameter(parameters);
        Expect(TokenType.RParen, "Expected ')' after extern function parameters.");
        Expect(TokenType.Arrow, "Expected '->' before extern function return type.");
        var returnTypeNode = ParseTypeNode();
        Expect(TokenType.Semicolon, "Expected ';' after extern function declaration.");

        return externToken is null
            ? null
            : new ExternFunctionNode(
                externToken.Location,
                nameToken?.Value ?? string.Empty,
                [],
                parameters,
                attributes,
                ReturnTypeNode: returnTypeNode);
    }

    private AttributeDeclarationNode? ParseAttributeDeclaration()
    {
        var attributeToken = Expect(TokenType.Attribute, "Expected 'attribute'.");
        var nameToken = Expect(TokenType.Identifier, "Expected attribute name.");
        Expect(TokenType.On, "Expected 'on' after attribute name.");

        var targets = new List<string>();
        do
        {
            if (ReadAttributeTarget() is { Length: > 0 } target)
            {
                targets.Add(target);
            }
        }
        while (ConsumeOptional(TokenType.Comma));

        var fields = new List<AttributeFieldNode>();
        if (ConsumeOptional(TokenType.Semicolon))
        {
            return attributeToken is null
                ? null
                : new AttributeDeclarationNode(attributeToken.Location, nameToken?.Value ?? string.Empty, targets, fields);
        }

        Expect(TokenType.LBrace, "Expected '{' before attribute fields.");
        while (!IsAtEnd && !Check(TokenType.RBrace))
        {
            var fieldToken = Expect(TokenType.Identifier, "Expected attribute field name.");
            Expect(TokenType.Colon, "Expected ':' after attribute field name.");
            var typeNode = ParseTypeNode();
            Expect(TokenType.Semicolon, "Expected ';' after attribute field.");

            if (fieldToken is not null)
            {
                fields.Add(new AttributeFieldNode(fieldToken.Location, fieldToken.Value, typeNode));
            }
        }

        Expect(TokenType.RBrace, "Expected '}' after attribute declaration.");
        ConsumeOptional(TokenType.Semicolon);

        return attributeToken is null
            ? null
            : new AttributeDeclarationNode(attributeToken.Location, nameToken?.Value ?? string.Empty, targets, fields);
    }

    private TopLevelNode? ParseTypeDeclaration(IReadOnlyList<AttributeApplicationNode> attributes)
    {
        var typeToken = Expect(TokenType.Type, "Expected 'type'.");
        var nameToken = Expect(TokenType.Identifier, "Expected type alias name.");
        var typeParameters = ParseOptionalTypeParameters();

        if (ConsumeOptional(TokenType.Using) || ConsumeOptional(TokenType.Over))
        {
            var baseTypeNode = ParseTypeNode();
            Expect(TokenType.LBrace, "Expected '{' before type adapter body.");

            var exposedMethods = new List<ExposeMethodNode>();
            var methods = new List<FunctionNode>();
            while (!IsAtEnd && !Check(TokenType.RBrace))
            {
                var memberAttributes = ParseAttributeApplications();

                if (Check(TokenType.Expose))
                {
                    if (ParseExposeMethod() is { } expose)
                    {
                        exposedMethods.Add(expose);
                    }

                    continue;
                }

                if (Check(TokenType.Fn))
                {
                    if (ParseStructFunction(nameToken?.Value ?? string.Empty, typeParameters, isStatic: false, memberAttributes) is { } method)
                    {
                        methods.Add(method);
                    }

                    continue;
                }

                if (Check(TokenType.Static))
                {
                    if (ParseStructStaticFunction(nameToken?.Value ?? string.Empty, typeParameters, memberAttributes) is { } method)
                    {
                        methods.Add(method);
                    }

                    continue;
                }

                _diagnostics.Report(Current.Location, "Expected 'expose' or adapter method declaration.");
                SynchronizeStatement();
            }

            Expect(TokenType.RBrace, "Expected '}' after type adapter body.");
            ConsumeOptional(TokenType.Semicolon);
            return typeToken is null
                ? null
                : new TypeAdapterNode(
                    typeToken.Location,
                    nameToken?.Value ?? string.Empty,
                    typeParameters,
                    exposedMethods,
                    methods,
                    attributes,
                    BaseTypeNode: baseTypeNode);
        }

        if (typeParameters.Count > 0)
        {
            _diagnostics.Report(typeToken?.Location ?? Current.Location, "Generic type aliases are not supported yet; use 'type Name<T> over Base<T>' for adapters.");
        }

        Expect(TokenType.Equals, "Expected '=' after type alias name.");
        var targetTypeNode = ParseTypeNode();
        Expect(TokenType.Semicolon, "Expected ';' after type alias.");

        return typeToken is null
            ? null
            : new TypeAliasNode(typeToken.Location, nameToken?.Value ?? string.Empty, attributes, TargetTypeNode: targetTypeNode);
    }

    private ExposeMethodNode? ParseExposeMethod()
    {
        var exposeToken = Expect(TokenType.Expose, "Expected 'expose'.");
        var isStatic = ConsumeOptional(TokenType.Static);
        var sourceToken = Expect(TokenType.Identifier, "Expected method name after 'expose'.");
        var exposedName = sourceToken?.Value ?? string.Empty;
        if (ConsumeOptional(TokenType.As))
        {
            exposedName = Expect(TokenType.Identifier, "Expected exposed method name after 'as'.")?.Value ?? string.Empty;
        }

        TypeNode? returnTypeNode = null;
        if (ConsumeOptional(TokenType.Arrow))
        {
            returnTypeNode = ParseTypeNode();
        }

        Expect(TokenType.Semicolon, "Expected ';' after exposed method.");
        return exposeToken is null || sourceToken is null
            ? null
            : new ExposeMethodNode(exposeToken.Location, isStatic, sourceToken.Value, exposedName, returnTypeNode);
    }

    private GlobalVariableNode? ParseGlobalVariable(Token keywordToken, bool isConst, IReadOnlyList<AttributeApplicationNode> attributes)
    {
        var nameToken = Expect(TokenType.Identifier, "Expected global variable name.");
        var typeNode = ParseOptionalVariableTypeNode("global variable", keywordToken.Location);
        ExpressionNode? initializer = null;

        if (ConsumeOptional(TokenType.Equals))
        {
            initializer = ReadExpressionUntil(keywordToken.Location, TokenType.Semicolon);
        }

        if (typeNode is null && initializer is null)
        {
            _diagnostics.Report(keywordToken.Location, "Expected ':' or '=' after global variable name.");
        }

        if (isConst && initializer is null)
        {
            _diagnostics.Report(keywordToken.Location, "Const globals require an initializer.");
        }

        Expect(TokenType.Semicolon, "Expected ';' after global variable declaration.");
        return nameToken is null
            ? null
            : new GlobalVariableNode(keywordToken.Location, isConst, nameToken.Value, initializer, attributes, TypeNode: typeNode);
    }

    private FunctionNode? ParseFunction(IReadOnlyList<AttributeApplicationNode> attributes)
    {
        var fnToken = Expect(TokenType.Fn, "Expected 'fn' before function declaration.");
        if (fnToken is null)
        {
            return null;
        }

        return ParseFunctionAfterFn(fnToken.Location, isStatic: false, attributes: attributes);
    }

    private FunctionNode? ParseStaticFunction(IReadOnlyList<AttributeApplicationNode> attributes)
    {
        var staticToken = Expect(TokenType.Static, "Expected 'static'.");
        if (Expect(TokenType.Fn, "Expected 'fn' after 'static'.") is null)
        {
            return null;
        }

        var function = ParseFunctionAfterFn(staticToken?.Location ?? Current.Location, isStatic: true, attributes: attributes);
        if (function?.OwnerTypeNode is null)
        {
            _diagnostics.Report(staticToken?.Location ?? Current.Location, "Static functions must be declared with an owner type, for example 'static fn Vec.empty()'.");
        }

        if (function?.Parameters.FirstOrDefault()?.Name == "self")
        {
            _diagnostics.Report(function.Location, "Static functions should not take 'self'; use 'fn Type.name' for instance methods.");
        }

        return function;
    }

    private FunctionNode? ParseFunctionAfterFn(
        Location fnLocation,
        bool isStatic,
        string? implicitOwnerType = null,
        IReadOnlyList<AttributeApplicationNode>? attributes = null)
    {
        var firstNameToken = ExpectIdentifierLike("Expected function name.");
        string? ownerType = implicitOwnerType;
        var functionName = firstNameToken?.Value ?? string.Empty;

        if (ConsumeOptional(TokenType.Dot))
        {
            ownerType = functionName;
            functionName = ExpectIdentifierLike("Expected method name after '.'.")?.Value ?? string.Empty;
        }

        var typeParameters = ParseOptionalTypeParameters();
        Expect(TokenType.LParen, "Expected '(' after function name.");

        var parameters = new List<ParameterNode>();
        if (!Check(TokenType.RParen))
        {
            do
            {
                var parameter = ParseParameter(allowVariadic: false);
                if (parameter is not null)
                {
                    parameters.Add(parameter);
                }
            }
            while (ConsumeOptional(TokenType.Comma));
        }

        ValidateVariadicParameter(parameters);
        if (!isStatic
            && ownerType is not null
            && !HasExplicitReceiverParameter(ownerType, parameters.FirstOrDefault()))
        {
            var selfTypeNode = TypeNode.Pointer(fnLocation, new NamedTypeSyntaxNode("Self"));
            parameters.Insert(0, new ParameterNode(fnLocation, "self", [], IsVariadic: false, TypeNode: selfTypeNode));
        }

        Expect(TokenType.RParen, "Expected ')' after function parameters.");
        Expect(TokenType.Arrow, "Expected '->' before function return type.");
        var returnTypeNode = ParseTypeNode();
        var genericConstraints = ParseOptionalGenericConstraints(typeParameters);
        Expect(TokenType.LBrace, "Expected '{' before function body.");

        var body = new List<StatementNode>();
        while (!IsAtEnd && !Check(TokenType.RBrace))
        {
            var statement = ParseStatement();
            if (statement is not null)
            {
                body.Add(statement);
            }
            else
            {
                SynchronizeStatement();
            }
        }

        Expect(TokenType.RBrace, "Expected '}' after function body.");
        return new FunctionNode(
            fnLocation,
            isStatic,
            ownerType,
            functionName,
            typeParameters,
            [],
            genericConstraints,
            parameters,
            body,
            attributes ?? [],
            returnTypeNode);
    }

    private static bool HasExplicitReceiverParameter(string ownerType, ParameterNode? parameter)
    {
        if (parameter is null)
        {
            return false;
        }

        if (parameter.Name == "self")
        {
            return true;
        }

        var type = parameter.TypeNode.ToTypeName().Trim();
        return type == "Self*"
            || type == ownerType + "*"
            || (type.StartsWith(ownerType + "<", StringComparison.Ordinal)
                && type.EndsWith("*", StringComparison.Ordinal));
    }

    private StructNode? ParseStruct(IReadOnlyList<AttributeApplicationNode> attributes)
    {
        var structToken = Expect(TokenType.Struct, "Expected 'struct'.");
        var nameToken = Expect(TokenType.Identifier, "Expected struct name.");
        var typeParameters = ParseOptionalTypeParameters();
        var requirements = ParseOptionalStructRequirements();
        var genericConstraints = ParseOptionalGenericConstraints(typeParameters);
        Expect(TokenType.LBrace, "Expected '{' before struct body.");

        var fields = new List<StructFieldNode>();
        var methods = new List<FunctionNode>();
        while (!IsAtEnd && !Check(TokenType.RBrace))
        {
            var memberAttributes = ParseAttributeApplications();

            if (Check(TokenType.Fn))
            {
                if (ParseStructFunction(nameToken?.Value ?? string.Empty, typeParameters, isStatic: false, memberAttributes) is { } method)
                {
                    methods.Add(method);
                }

                continue;
            }

            if (Check(TokenType.Static))
            {
                if (ParseStructStaticFunction(nameToken?.Value ?? string.Empty, typeParameters, memberAttributes) is { } method)
                {
                    methods.Add(method);
                }

                continue;
            }

            var fieldToken = ExpectIdentifierLike("Expected struct field name.");
            Expect(TokenType.Colon, "Expected ':' after struct field name.");
            var typeNode = ParseTypeNode();
            Expect(TokenType.Semicolon, "Expected ';' after struct field.");

            if (fieldToken is not null)
            {
                fields.Add(new StructFieldNode(fieldToken.Location, fieldToken.Value, memberAttributes, typeNode));
            }
        }

        Expect(TokenType.RBrace, "Expected '}' after struct body.");
        ConsumeOptional(TokenType.Semicolon);

        return structToken is null
            ? null
            : new StructNode(structToken.Location, nameToken?.Value ?? string.Empty, typeParameters, genericConstraints, requirements, fields, methods, attributes);
    }

    private ExtensionNode? ParseExtension(IReadOnlyList<AttributeApplicationNode> attributes)
    {
        var extensionToken = Expect(TokenType.Extension, "Expected 'extension'.");
        var targetToken = Expect(TokenType.Identifier, "Expected extension target type.");
        var targetType = targetToken?.Value ?? string.Empty;
        var targetTypeNode = CreateTypeNode(targetToken?.Location ?? extensionToken?.Location ?? Current.Location, targetType);
        var typeParameters = ParseOptionalTypeParameters();
        var genericConstraints = ParseOptionalGenericConstraints(typeParameters);
        Expect(TokenType.LBrace, "Expected '{' before extension body.");

        var methods = new List<FunctionNode>();
        while (!IsAtEnd && !Check(TokenType.RBrace))
        {
            var memberAttributes = ParseAttributeApplications();

            if (Check(TokenType.Fn))
            {
                if (ParseStructFunction(targetType, typeParameters, isStatic: false, memberAttributes) is { } method)
                {
                    methods.Add(method with
                    {
                        GenericConstraints = genericConstraints.Concat(method.GenericConstraints).ToList(),
                    });
                }

                continue;
            }

            if (Check(TokenType.Static))
            {
                if (ParseStructStaticFunction(targetType, typeParameters, memberAttributes) is { } method)
                {
                    methods.Add(method with
                    {
                        GenericConstraints = genericConstraints.Concat(method.GenericConstraints).ToList(),
                    });
                }

                continue;
            }

            _diagnostics.Report(Current.Location, "Expected extension method declaration.");
            SynchronizeStatement();
        }

        Expect(TokenType.RBrace, "Expected '}' after extension body.");
        ConsumeOptional(TokenType.Semicolon);

        return extensionToken is null
            ? null
            : new ExtensionNode(
                extensionToken.Location,
                typeParameters,
                genericConstraints,
                methods,
                attributes,
                TargetTypeNode: targetTypeNode);
    }

    private FunctionNode? ParseStructFunction(
        string ownerType,
        IReadOnlyList<string> ownerTypeParameters,
        bool isStatic,
        IReadOnlyList<AttributeApplicationNode> attributes)
    {
        var fnToken = Expect(TokenType.Fn, "Expected 'fn' before struct function declaration.");
        if (fnToken is null)
        {
            return null;
        }

        return InheritOwnerTypeParameters(ParseFunctionAfterFn(fnToken.Location, isStatic, ownerType, attributes), ownerTypeParameters);
    }

    private FunctionNode? ParseStructStaticFunction(
        string ownerType,
        IReadOnlyList<string> ownerTypeParameters,
        IReadOnlyList<AttributeApplicationNode> attributes)
    {
        var staticToken = Expect(TokenType.Static, "Expected 'static'.");
        if (Expect(TokenType.Fn, "Expected 'fn' after 'static'.") is null)
        {
            return null;
        }

        var function = ParseFunctionAfterFn(staticToken?.Location ?? Current.Location, isStatic: true, ownerType, attributes);
        function = InheritOwnerTypeParameters(function, ownerTypeParameters);
        if (function?.Parameters.FirstOrDefault()?.Name == "self")
        {
            _diagnostics.Report(function.Location, "Static functions should not take 'self'; use 'fn Type.name' for instance methods.");
        }

        return function;
    }

    private static FunctionNode? InheritOwnerTypeParameters(
        FunctionNode? function,
        IReadOnlyList<string> ownerTypeParameters)
    {
        if (function is null || ownerTypeParameters.Count == 0)
        {
            return function;
        }

        var inherited = ownerTypeParameters
            .Where(parameter => !function.TypeParameters.Contains(parameter, StringComparer.Ordinal))
            .ToList();
        if (inherited.Count == 0)
        {
            return function;
        }

        return function with
        {
            TypeParameters = inherited.Concat(function.TypeParameters).ToList(),
        };
    }

    private InterfaceNode? ParseInterface(IReadOnlyList<AttributeApplicationNode> attributes)
    {
        var interfaceToken = Expect(TokenType.Interface, "Expected 'interface'.");
        var nameToken = Expect(TokenType.Identifier, "Expected interface name.");
        Expect(TokenType.LBrace, "Expected '{' before interface body.");

        var methods = new List<InterfaceMethodNode>();
        while (!IsAtEnd && !Check(TokenType.RBrace))
        {
            if (ParseInterfaceMethod() is { } method)
            {
                methods.Add(method);
            }
        }

        Expect(TokenType.RBrace, "Expected '}' after interface body.");
        ConsumeOptional(TokenType.Semicolon);

        return interfaceToken is null
            ? null
            : new InterfaceNode(interfaceToken.Location, nameToken?.Value ?? string.Empty, methods, attributes);
    }

    private InterfaceMethodNode? ParseInterfaceMethod()
    {
        var fnToken = Expect(TokenType.Fn, "Expected 'fn' before interface method.");
        var nameToken = ExpectIdentifierLike("Expected interface method name.");
        Expect(TokenType.LParen, "Expected '(' after interface method name.");

        var parameters = new List<ParameterNode>();
        if (!Check(TokenType.RParen))
        {
            do
            {
                var parameter = ParseParameter(allowVariadic: false);
                if (parameter is not null)
                {
                    parameters.Add(parameter);
                }
            }
            while (ConsumeOptional(TokenType.Comma));
        }

        ValidateVariadicParameter(parameters);
        Expect(TokenType.RParen, "Expected ')' after interface method parameters.");
        Expect(TokenType.Arrow, "Expected '->' before interface method return type.");
        var returnTypeNode = ParseTypeNode();
        Expect(TokenType.Semicolon, "Expected ';' after interface method.");

        return fnToken is null
            ? null
            : new InterfaceMethodNode(fnToken.Location, nameToken?.Value ?? string.Empty, parameters, returnTypeNode);
    }

    private EnumNode? ParseEnum(
        IReadOnlyList<AttributeApplicationNode> attributes,
        bool isHeaderDeclaration = false)
    {
        var enumToken = Expect(TokenType.Enum, "Expected 'enum'.");
        var nameToken = Expect(TokenType.Identifier, "Expected enum name.");
        Expect(TokenType.LBrace, "Expected '{' before enum body.");

        var members = new List<EnumMemberNode>();
        while (!IsAtEnd && !Check(TokenType.RBrace))
        {
            var memberAttributes = ParseAttributeApplications();
            var memberToken = Expect(TokenType.Identifier, "Expected enum member name.");
            string? value = null;
            if (ConsumeOptional(TokenType.Equals))
            {
                value = ReadBalancedSliceUntilAny(memberToken?.Location ?? Current.Location, TokenType.Comma, TokenType.RBrace)
                    .ToSourceText();
            }

            if (memberToken is not null)
            {
                members.Add(new EnumMemberNode(memberToken.Location, memberToken.Value, value, memberAttributes));
            }

            ConsumeOptional(TokenType.Comma);
        }

        Expect(TokenType.RBrace, "Expected '}' after enum body.");
        ConsumeOptional(TokenType.Semicolon);

        return enumToken is null
            ? null
            : new EnumNode(enumToken.Location, nameToken?.Value ?? string.Empty, members, attributes, IsHeaderDeclaration: isHeaderDeclaration);
    }

    private IReadOnlyList<StructRequirementNode> ParseOptionalStructRequirements()
    {
        if (!ConsumeOptional(TokenType.Colon))
        {
            return [];
        }

        var requirements = new List<StructRequirementNode>();
        do
        {
            if (ParseRequirementReference() is { } requirement)
            {
                requirements.Add(requirement);
            }
        }
        while (ConsumeOptional(TokenType.Comma));

        return requirements;
    }

    private IReadOnlyList<GenericConstraintNode> ParseOptionalGenericConstraints(IReadOnlyList<string> typeParameters)
    {
        if (!ConsumeOptional(TokenType.Where))
        {
            return [];
        }

        var constraints = new List<GenericConstraintNode>();
        do
        {
            var parameterToken = Expect(TokenType.Identifier, "Expected generic type parameter name in where clause.");
            Expect(TokenType.Colon, "Expected ':' after generic type parameter in where clause.");

            var requirements = new List<StructRequirementNode>();
            do
            {
                if (ParseRequirementReference() is { } requirement)
                {
                    requirements.Add(requirement);
                }
            }
            while (ConsumeOptional(TokenType.Plus));

            if (parameterToken is not null)
            {
                constraints.Add(new GenericConstraintNode(parameterToken.Location, parameterToken.Value, requirements));
            }
        }
        while (ConsumeOptional(TokenType.Comma));

        return constraints;
    }

    private StructRequirementNode? ParseRequirementReference()
    {
        var nameToken = Expect(TokenType.Identifier, "Expected requirement name.");
        var typeArgumentNodes = new List<TypeNode>();

        if (ConsumeOptional(TokenType.LessThan))
        {
            if (!CheckTypeCloseAngle())
            {
                do
                {
                    var typeArgumentNode = ParseTypeNode();
                    typeArgumentNodes.Add(typeArgumentNode);
                }
                while (ConsumeOptional(TokenType.Comma));
            }

            ExpectTypeCloseAngle("Expected '>' after requirement type arguments.");
        }

        return nameToken is null
            ? null
            : new StructRequirementNode(nameToken.Location, nameToken.Value, typeArgumentNodes);
    }

    private IReadOnlyList<string> ParseOptionalTypeParameters()
    {
        if (!ConsumeOptional(TokenType.LessThan))
        {
            return [];
        }

        var parameters = new List<string>();
        do
        {
            if (Expect(TokenType.Identifier, "Expected generic type parameter name.") is { } parameter)
            {
                parameters.Add(parameter.Value);
            }
        }
        while (ConsumeOptional(TokenType.Comma));

        Expect(TokenType.GreaterThan, "Expected '>' after generic type parameters.");
        return parameters;
    }

    private RequirementNode? ParseRequirement()
    {
        var requiresToken = Expect(TokenType.Requires, "Expected 'requires'.");
        var nameToken = Expect(TokenType.Identifier, "Expected requirement name.");
        var typeParameters = ParseOptionalTypeParameters();
        var genericConstraints = ParseOptionalGenericConstraints(typeParameters);
        Expect(TokenType.LBrace, "Expected '{' before requirement body.");

        var members = new List<RequirementMemberNode>();
        while (!IsAtEnd && !Check(TokenType.RBrace))
        {
            if (Check(TokenType.Fn) || Check(TokenType.Static))
            {
                if (ParseRequirementFunction() is { } function)
                {
                    members.Add(function);
                }

                continue;
            }

            if (ParseRequirementField() is { } field)
            {
                members.Add(field);
            }
        }

        Expect(TokenType.RBrace, "Expected '}' after requirement body.");
        ConsumeOptional(TokenType.Semicolon);

        return requiresToken is null
            ? null
            : new RequirementNode(
                requiresToken.Location,
                nameToken?.Value ?? string.Empty,
                typeParameters,
                genericConstraints,
                members);
    }

    private RequirementFunctionNode? ParseRequirementFunction()
    {
        var staticToken = Match(TokenType.Static);
        var fnToken = Expect(TokenType.Fn, staticToken is null
            ? "Expected 'fn' before requirement function."
            : "Expected 'fn' after 'static' in requirement function.");
        var nameToken = ExpectIdentifierLike("Expected requirement function name.");
        Expect(TokenType.LParen, "Expected '(' after requirement function name.");

        var parameters = new List<ParameterNode>();
        if (!Check(TokenType.RParen))
        {
            do
            {
                var parameter = ParseParameter(allowVariadic: false);
                if (parameter is not null)
                {
                    parameters.Add(parameter);
                }
            }
            while (ConsumeOptional(TokenType.Comma));
        }

        ValidateVariadicParameter(parameters);
        Expect(TokenType.RParen, "Expected ')' after requirement function parameters.");
        Expect(TokenType.Arrow, "Expected '->' before requirement function return type.");
        var returnTypeNode = ParseTypeNode();
        Expect(TokenType.Semicolon, "Expected ';' after requirement function.");

        return fnToken is null
            ? null
            : new RequirementFunctionNode(
                staticToken?.Location ?? fnToken.Location,
                staticToken is not null,
                nameToken?.Value ?? string.Empty,
                parameters,
                returnTypeNode);
    }

    private RequirementFieldNode? ParseRequirementField()
    {
        var fieldToken = Expect(TokenType.Identifier, "Expected requirement field name.");
        Expect(TokenType.Colon, "Expected ':' after requirement field name.");
        var typeNode = ParseTypeNode();
        Expect(TokenType.Semicolon, "Expected ';' after requirement field.");

        return fieldToken is null
            ? null
            : new RequirementFieldNode(fieldToken.Location, fieldToken.Value, typeNode);
    }

    private TaggedUnionNode? ParseTaggedUnion(
        IReadOnlyList<AttributeApplicationNode> attributes,
        bool isRaw,
        bool isHeaderDeclaration = false,
        Cx.Compiler.Syntax.Location? rawLocation = null)
    {
        var unionToken = Expect(TokenType.Union, "Expected 'union'.");
        var nameToken = Expect(TokenType.Identifier, "Expected union name.");
        Expect(TokenType.LBrace, "Expected '{' before union body.");

        var variants = new List<TaggedUnionVariantNode>();
        var methods = new List<FunctionNode>();
        while (!IsAtEnd && !Check(TokenType.RBrace))
        {
            var memberAttributes = ParseAttributeApplications();

            if (!isRaw && Check(TokenType.Fn))
            {
                if (ParseStructFunction(nameToken?.Value ?? string.Empty, [], isStatic: false, memberAttributes) is { } method)
                {
                    methods.Add(method);
                }

                continue;
            }

            if (!isRaw && Check(TokenType.Static))
            {
                if (ParseStructStaticFunction(nameToken?.Value ?? string.Empty, [], memberAttributes) is { } method)
                {
                    methods.Add(method);
                }

                continue;
            }

            var variantToken = Expect(TokenType.Identifier, "Expected union variant name.");
            Expect(TokenType.Colon, "Expected ':' after union variant name.");
            var typeNode = ParseTypeNode();
            Expect(TokenType.Semicolon, "Expected ';' after union variant.");

            if (variantToken is not null)
            {
                variants.Add(new TaggedUnionVariantNode(variantToken.Location, variantToken.Value, memberAttributes, typeNode));
            }
        }

        Expect(TokenType.RBrace, "Expected '}' after union body.");
        ConsumeOptional(TokenType.Semicolon);

        return unionToken is null
            ? null
            : new TaggedUnionNode(
                rawLocation ?? unionToken.Location,
                nameToken?.Value ?? string.Empty,
                variants,
                methods,
                attributes,
                IsRaw: isRaw,
                IsHeaderDeclaration: isHeaderDeclaration);
    }

    private ParameterNode? ParseParameter(bool allowVariadic)
    {
        var attributes = ParseAttributeApplications();
        if (Match(TokenType.Ellipsis) is { } ellipsis)
        {
            if (!allowVariadic)
            {
                _diagnostics.Report(ellipsis.Location, "Variadic parameter '...' is only supported for C declarations.");
            }

            return new ParameterNode(ellipsis.Location, string.Empty, attributes, IsVariadic: true, TypeNode: CreateTypeNode(ellipsis.Location, "..."));
        }

        var nameToken = Expect(TokenType.Identifier, "Expected parameter name.");
        if (nameToken is null)
        {
            return null;
        }

        Expect(TokenType.Colon, "Expected ':' after parameter name.");
        var typeNode = ParseTypeNode();
        return new ParameterNode(nameToken.Location, nameToken.Value, attributes, TypeNode: typeNode);
    }

    private void ValidateVariadicParameter(IReadOnlyList<ParameterNode> parameters)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            if (!parameters[i].IsVariadic)
            {
                continue;
            }

            if (i != parameters.Count - 1)
            {
                _diagnostics.Report(parameters[i].Location, "Variadic parameter '...' must be the last parameter.");
            }

            if (i == 0)
            {
                _diagnostics.Report(parameters[i].Location, "Variadic functions require at least one named parameter before '...'.");
            }
        }
    }

    private string ParseModulePath()
    {
        var parts = new List<string>();
        var first = Expect(TokenType.Identifier, "Expected module name.");
        if (first is not null)
        {
            parts.Add(first.Value);
        }

        while (ConsumeOptional(TokenType.Dot))
        {
            var part = Expect(TokenType.Identifier, "Expected module path segment after '.'.");
            if (part is not null)
            {
                parts.Add(part.Value);
            }
        }

        return string.Join(".", parts);
    }

    private IReadOnlyList<AttributeApplicationNode> ParseAttributeApplications()
    {
        var attributes = new List<AttributeApplicationNode>();
        while (Check(TokenType.At))
        {
            var atToken = Expect(TokenType.At, "Expected '@'.");
            var nameToken = Expect(TokenType.Identifier, "Expected attribute name after '@'.");
            var arguments = new List<AttributeArgumentNode>();

            if (ConsumeOptional(TokenType.LParen))
            {
                if (!Check(TokenType.RParen))
                {
                    do
                    {
                        var argumentLocation = Current.Location;
                        var argumentTokens = Tokens.ReadBalancedUntilAny(TokenType.Comma, TokenType.RParen);
                        string? argumentName = null;
                        var valueTokens = argumentTokens;

                        if (TrySplitNamedAttributeArgument(argumentTokens, out var name, out var namedValueTokens))
                        {
                            argumentName = name;
                            valueTokens = namedValueTokens;
                        }

                        var value = TokenText.ToSourceText(valueTokens).Trim();
                        if (value.Length > 0)
                        {
                            arguments.Add(new AttributeArgumentNode(argumentLocation, argumentName, value));
                        }
                    }
                    while (ConsumeOptional(TokenType.Comma));
                }

                Expect(TokenType.RParen, "Expected ')' after attribute arguments.");
            }

            if (atToken is not null)
            {
                attributes.Add(new AttributeApplicationNode(
                    atToken.Location,
                    nameToken?.Value ?? string.Empty,
                    arguments));
            }
        }

        return attributes;
    }

    private static bool TrySplitNamedAttributeArgument(
        IReadOnlyList<Token> tokens,
        out string? name,
        out IReadOnlyList<Token> valueTokens)
    {
        name = null;
        valueTokens = tokens;

        if (tokens.Count < 3 || tokens[0].Type != TokenType.Identifier)
        {
            return false;
        }

        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            depth += tokens[i].Type switch
            {
                TokenType.LParen or TokenType.LBracket or TokenType.LBrace or TokenType.LessThan => 1,
                TokenType.RParen or TokenType.RBracket or TokenType.RBrace or TokenType.GreaterThan => -1,
                _ => 0
            };

            if (tokens[i].Type != TokenType.Colon || depth != 0)
            {
                continue;
            }

            if (i != 1)
            {
                return false;
            }

            name = tokens[0].Value;
            valueTokens = tokens.Skip(i + 1).ToList();
            return valueTokens.Count > 0;
        }

        return false;
    }

    private string? ReadAttributeTarget()
    {
        if (Current.Type is TokenType.Identifier
            or TokenType.Struct
            or TokenType.Union
            or TokenType.Enum
            or TokenType.Fn
            or TokenType.Type
            or TokenType.Const
            or TokenType.Macro
            or TokenType.Extern
            or TokenType.Module
            or TokenType.Requires)
        {
            return Advance().Value;
        }

        _diagnostics.Report(Current.Location, "Expected attribute target.");
        return null;
    }

    private void ReportUnexpectedAttributes(IReadOnlyList<AttributeApplicationNode> attributes, string targetDescription)
    {
        foreach (var attribute in attributes)
        {
            _diagnostics.Report(attribute.Location, $"Attributes cannot be applied to {targetDescription}.");
        }
    }

    private TokenSlice ReadBalancedSliceUntilAny(Location location, params TokenType[] types) =>
        new(location, Tokens.ReadBalancedUntilAny(types));

    private void SynchronizeFunction()
    {
        while (!IsAtEnd && Current.Type != TokenType.Fn)
        {
            Advance();
        }
    }

    private void SynchronizeTopLevel()
    {
        while (!IsAtEnd && !Check(TokenType.Semicolon) && !Check(TokenType.RBrace))
        {
            Advance();
        }

        ConsumeOptional(TokenType.Semicolon);
    }

    private void SynchronizeStatement()
    {
        while (!IsAtEnd && !Check(TokenType.Semicolon) && !Check(TokenType.RBrace))
        {
            Advance();
        }

        ConsumeOptional(TokenType.Semicolon);
    }

    private void SynchronizeMatchArm()
    {
        while (!IsAtEnd && !Check(TokenType.FatArrow) && !Check(TokenType.RBrace))
        {
            Advance();
        }

        if (ConsumeOptional(TokenType.FatArrow) && !Check(TokenType.RBrace))
        {
            _ = ParseStatement();
        }
    }
}
