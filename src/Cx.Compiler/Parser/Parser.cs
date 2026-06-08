using System.Text;
using Cx.Compiler.Diagnostics;
using Cx.Compiler.Lexer;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Parser;

public sealed class Parser
{
    private readonly DiagnosticBag _diagnostics;
    private IReadOnlyList<Token> _tokens = [];
    private int _position;
    private int _pendingTypeCloseAngles;

    public Parser(DiagnosticBag? diagnostics = null)
    {
        _diagnostics = diagnostics ?? new DiagnosticBag();
    }

    public DiagnosticBag Diagnostics => _diagnostics;

    public ProgramNode Parse(SourceFile sourceFile)
    {
        _tokens = new Lexer.Lexer(sourceFile, _diagnostics)
            .Tokenize()
            .Where(token => token.Type is not TokenType.Comment and not TokenType.MultilineComment)
            .ToList();
        _position = 0;
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

        var targetTypeNode = CreateTypeNode(nameToken?.Location ?? typeToken?.Location ?? Current.Location, "opaque");
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
            initializer = ParseExpressionText(keywordToken.Location, ReadUntil(TokenType.Semicolon));
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
        if (function?.OwnerType is null)
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
            var selfTypeNode = CreateTypeNode(fnLocation, "Self*");
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

        var type = parameter.Type.Trim();
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
                value = ReadUntilAny(TokenType.Comma, TokenType.RBrace);
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

    private StatementNode? ParseStatement()
    {
        if (Match(TokenType.Let) is { } letToken)
        {
            return ParseVariableStatement(letToken, isConst: false);
        }

        if (Match(TokenType.Const) is { } constToken)
        {
            return ParseVariableStatement(constToken, isConst: true);
        }

        if (Match(TokenType.Return) is { } returnToken)
        {
            var returnExpression = Check(TokenType.Semicolon)
                ? new RawExpressionNode(returnToken.Location, string.Empty)
                : ReadExpressionUntil(returnToken.Location, TokenType.Semicolon);
            Expect(TokenType.Semicolon, "Expected ';' after return statement.");
            return new ReturnStatement(returnToken.Location, returnExpression);
        }

        if (Check(TokenType.If))
        {
            return ParseIfStatement();
        }

        if (Check(TokenType.While))
        {
            return ParseWhileStatement();
        }

        if (Check(TokenType.For))
        {
            return ParseForStatement();
        }

        if (Check(TokenType.Foreach))
        {
            return ParseForeachStatement();
        }

        if (Check(TokenType.Switch))
        {
            return ParseSwitchStatement();
        }

        if (Check(TokenType.Match))
        {
            return ParseMatchStatement();
        }

        if (Match(TokenType.Break) is { } breakToken)
        {
            Expect(TokenType.Semicolon, "Expected ';' after break statement.");
            return new BreakStatement(breakToken.Location);
        }

        if (Match(TokenType.Continue) is { } continueToken)
        {
            Expect(TokenType.Semicolon, "Expected ';' after continue statement.");
            return new ContinueStatement(continueToken.Location);
        }

        var location = Current.Location;
        var expression = ReadExpressionUntil(location, TokenType.Semicolon);
        Expect(TokenType.Semicolon, "Expected ';' after expression statement.");
        return new CStatement(location, expression);
    }

    private StatementNode ParseVariableStatement(Token keywordToken, bool isConst)
    {
        var nameToken = Expect(TokenType.Identifier, "Expected variable name.");
        var typeNode = ParseOptionalVariableTypeNode("variable", keywordToken.Location);
        ExpressionNode? initializer = null;

        if (ConsumeOptional(TokenType.Equals))
        {
            initializer = ReadExpressionUntil(keywordToken.Location, TokenType.Semicolon);
        }

        if (typeNode is null && initializer is null)
        {
            _diagnostics.Report(keywordToken.Location, "Expected ':' or '=' after variable name.");
        }

        if (isConst && initializer is null)
        {
            _diagnostics.Report(keywordToken.Location, "Const variables require an initializer.");
        }

        Expect(TokenType.Semicolon, "Expected ';' after variable declaration.");
        return new LetStatement(keywordToken.Location, isConst, nameToken?.Value ?? string.Empty, initializer, typeNode);
    }

    private IfStatement? ParseIfStatement()
    {
        var ifToken = Expect(TokenType.If, "Expected 'if'.");
        var condition = ParseParenthesizedExpression("if condition");
        var thenBody = ParseBlock();
        StatementNode? elseBranch = null;

        if (ConsumeOptional(TokenType.Else))
        {
            if (Check(TokenType.If))
            {
                elseBranch = ParseIfStatement();
            }
            else
            {
                var elseLocation = Current.Location;
                elseBranch = new ElseBlockStatement(elseLocation, ParseBlock());
            }
        }

        return ifToken is null ? null : new IfStatement(ifToken.Location, condition, thenBody, elseBranch);
    }

    private WhileStatement? ParseWhileStatement()
    {
        var whileToken = Expect(TokenType.While, "Expected 'while'.");
        var condition = ParseParenthesizedExpression("while condition");
        var body = ParseBlock();

        return whileToken is null ? null : new WhileStatement(whileToken.Location, condition, body);
    }

    private ForStatement? ParseForStatement()
    {
        var forToken = Expect(TokenType.For, "Expected 'for'.");
        Expect(TokenType.LParen, "Expected '(' after 'for'.");
        var initializer = ParseForInitializer(forToken?.Location ?? Current.Location);
        Expect(TokenType.Semicolon, "Expected ';' after for initializer.");
        var condition = ReadExpressionUntil(forToken?.Location ?? Current.Location, TokenType.Semicolon);
        Expect(TokenType.Semicolon, "Expected ';' after for condition.");
        var increment = ReadExpressionUntil(forToken?.Location ?? Current.Location, TokenType.RParen);
        Expect(TokenType.RParen, "Expected ')' after for increment.");
        var body = ParseBlock();

        return forToken is null ? null : new ForStatement(forToken.Location, initializer, condition, increment, body);
    }

    private ForInitializerNode ParseForInitializer(Location location)
    {
        if (Match(TokenType.Let) is { } letToken)
        {
            return ParseForDeclarationInitializer(letToken.Location, isConst: false);
        }

        if (Match(TokenType.Const) is { } constToken)
        {
            return ParseForDeclarationInitializer(constToken.Location, isConst: true);
        }

        return new ForExpressionInitializerNode(
            location,
            ReadExpressionUntil(location, TokenType.Semicolon));
    }

    private ForDeclarationInitializerNode ParseForDeclarationInitializer(Location location, bool isConst)
    {
        var nameToken = Expect(TokenType.Identifier, "Expected for initializer variable name.");
        var typeNode = ParseOptionalVariableTypeNode("for initializer variable", location);
        ExpressionNode? initializer = null;

        if (ConsumeOptional(TokenType.Equals))
        {
            initializer = ReadExpressionUntil(location, TokenType.Semicolon);
        }

        if (typeNode is null && initializer is null)
        {
            _diagnostics.Report(location, "Expected ':' or '=' after for initializer variable name.");
        }

        return new ForDeclarationInitializerNode(
            location,
            isConst,
            nameToken?.Value ?? string.Empty,
            initializer,
            typeNode);
    }

    private string ParseOptionalVariableType(string subject, Location location)
    {
        return ParseOptionalVariableTypeNode(subject, location)?.TypeName ?? string.Empty;
    }

    private TypeNode? ParseOptionalVariableTypeNode(string subject, Location location)
    {
        if (!ConsumeOptional(TokenType.Colon))
        {
            return null;
        }

        var type = ParseTypeNode();
        if (string.IsNullOrWhiteSpace(type.TypeName))
        {
            _diagnostics.Report(location, $"Expected type after ':' in {subject} declaration.");
        }

        return type;
    }

    private ForeachStatement? ParseForeachStatement()
    {
        var foreachToken = Expect(TokenType.Foreach, "Expected 'foreach'.");
        var firstBinding = ParseForeachBinding("Expected foreach binding name.");
        ForeachBinding? indexBinding = null;
        ForeachBinding? keyBinding = null;
        ForeachBinding? valueBinding = null;

        if (ConsumeOptional(TokenType.Comma))
        {
            var secondBinding = ParseForeachBinding("Expected foreach binding name after ','.");
            if (ConsumeOptional(TokenType.FatArrow))
            {
                indexBinding = firstBinding;
                keyBinding = secondBinding;
                valueBinding = ParseForeachBinding("Expected foreach value binding after '=>'.");
            }
            else
            {
                indexBinding = firstBinding;
                valueBinding = secondBinding;
            }
        }
        else if (ConsumeOptional(TokenType.FatArrow))
        {
            keyBinding = firstBinding;
            valueBinding = ParseForeachBinding("Expected foreach value binding after '=>'.");
        }
        else
        {
            valueBinding = firstBinding;
        }

        Expect(TokenType.In, "Expected 'in' after foreach binding.");
        var iterableExpression = ReadExpressionUntil(foreachToken?.Location ?? Current.Location, TokenType.LBrace);
        var body = ParseBlock();

        return foreachToken is null
            ? null
            : new ForeachStatement(
                foreachToken.Location,
                indexBinding,
                keyBinding,
                valueBinding ?? new ForeachBinding(foreachToken.Location, string.Empty, IsReference: true, IsConst: false),
                iterableExpression,
                body);
    }

    private ForeachBinding ParseForeachBinding(string message)
    {
        var isConst = ConsumeOptional(TokenType.Const);
        var isReference = ConsumeOptional(TokenType.Ampersand);
        var nameToken = Expect(TokenType.Identifier, message);
        var typeNode = ParseOptionalVariableTypeNode("foreach binding", nameToken?.Location ?? Current.Location);
        return new ForeachBinding(
            nameToken?.Location ?? Current.Location,
            nameToken?.Value ?? string.Empty,
            isReference,
            isConst,
            typeNode);
    }

    private SwitchStatement? ParseSwitchStatement()
    {
        var switchToken = Expect(TokenType.Switch, "Expected 'switch'.");
        var expression = ParseParenthesizedExpression("switch expression");
        Expect(TokenType.LBrace, "Expected '{' before switch body.");

        var cases = new List<SwitchCaseNode>();
        var defaultBody = new List<StatementNode>();
        while (!IsAtEnd && !Check(TokenType.RBrace))
        {
            if (Match(TokenType.Case) is { } caseToken)
            {
                var pattern = ReadExpressionUntil(caseToken.Location, TokenType.Colon);
                Expect(TokenType.Colon, "Expected ':' after case pattern.");
                cases.Add(new SwitchCaseNode(caseToken.Location, pattern, ParseSwitchArmBody()));
                continue;
            }

            if (Match(TokenType.Default) is { })
            {
                Expect(TokenType.Colon, "Expected ':' after default.");
                defaultBody.AddRange(ParseSwitchArmBody());
                continue;
            }

            _diagnostics.Report(Current.Location, $"Unexpected token '{Current.Value}' in switch body.");
            SynchronizeStatement();
        }

        Expect(TokenType.RBrace, "Expected '}' after switch body.");
        return switchToken is null
            ? null
            : new SwitchStatement(switchToken.Location, expression, cases, defaultBody);
    }

    private IReadOnlyList<StatementNode> ParseSwitchArmBody()
    {
        if (Check(TokenType.LBrace))
        {
            var body = ParseBlock().ToList();
            if (Match(TokenType.Break) is { } breakToken)
            {
                Expect(TokenType.Semicolon, "Expected ';' after break statement.");
                body.Add(new BreakStatement(breakToken.Location));
            }

            return body;
        }

        var statements = new List<StatementNode>();
        while (!IsAtEnd
            && !Check(TokenType.Case)
            && !Check(TokenType.Default)
            && !Check(TokenType.RBrace))
        {
            if (ParseStatement() is { } statement)
            {
                statements.Add(statement);
            }
            else
            {
                SynchronizeStatement();
            }
        }

        return statements;
    }

    private MatchStatement? ParseMatchStatement()
    {
        var matchToken = Expect(TokenType.Match, "Expected 'match'.");
        var expression = ReadExpressionUntil(matchToken?.Location ?? Current.Location, TokenType.LBrace);
        Expect(TokenType.LBrace, "Expected '{' before match arms.");

        var arms = new List<MatchArmNode>();
        while (!IsAtEnd && !Check(TokenType.RBrace))
        {
            var arm = ParseMatchArm();
            if (arm is not null)
            {
                arms.Add(arm);
            }
            else
            {
                SynchronizeMatchArm();
            }
        }

        Expect(TokenType.RBrace, "Expected '}' after match arms.");
        return matchToken is null ? null : new MatchStatement(matchToken.Location, expression, arms);
    }

    private MatchArmNode? ParseMatchArm()
    {
        var patternToken = Expect(TokenType.Identifier, "Expected match pattern.");
        string? bindingName = null;

        if (ConsumeOptional(TokenType.Colon))
        {
            bindingName = Expect(TokenType.Identifier, "Expected binding name after ':'.")?.Value;
        }

        Expect(TokenType.FatArrow, "Expected '=>' after match pattern.");

        IReadOnlyList<StatementNode> body;
        if (Check(TokenType.LBrace))
        {
            body = ParseBlock();
        }
        else if (ParseStatement() is { } statement)
        {
            body = [statement];
        }
        else
        {
            body = [];
        }

        return patternToken is null
            ? null
            : new MatchArmNode(patternToken.Location, patternToken.Value, bindingName, body);
    }

    private IReadOnlyList<StatementNode> ParseBlock()
    {
        Expect(TokenType.LBrace, "Expected '{' before block.");
        var statements = new List<StatementNode>();

        while (!IsAtEnd && !Check(TokenType.RBrace))
        {
            var statement = ParseStatement();
            if (statement is not null)
            {
                statements.Add(statement);
            }
            else
            {
                SynchronizeStatement();
            }
        }

        Expect(TokenType.RBrace, "Expected '}' after block.");
        return statements;
    }

    private ExpressionNode ParseParenthesizedExpression(string label)
    {
        var location = Current.Location;
        Expect(TokenType.LParen, $"Expected '(' before {label}.");
        var expression = ReadExpressionUntil(location, TokenType.RParen);
        Expect(TokenType.RParen, $"Expected ')' after {label}.");
        return expression;
    }

    private ExpressionNode ReadExpressionUntil(Location location, TokenType type) =>
        ParseExpressionText(location, ReadUntil(type));

    private ExpressionNode ParseExpressionText(Location location, string text)
    {
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            _diagnostics.Report(location, "Expected expression.");
            return new RawExpressionNode(location, text);
        }

        if (TryParseFunctionExpression(location, text, out var functionExpression))
        {
            return functionExpression;
        }

        if (IsWholeQuotedLiteral(text)
            || text is "true" or "false" or "null"
            || double.TryParse(text, System.Globalization.NumberStyles.Integer | System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return new LiteralExpressionNode(location, text);
        }

        if (text.All(ch => char.IsLetterOrDigit(ch) || ch == '_') && (char.IsLetter(text[0]) || text[0] == '_'))
        {
            return new NameExpressionNode(location, text);
        }

        if (TryParseAssignmentExpression(location, text, out var assignmentExpression))
        {
            return assignmentExpression;
        }

        if (TryParseConditionalExpression(location, text, out var conditionalExpression))
        {
            return conditionalExpression;
        }

        if (TryParseScalarRangeExpression(location, text, out var scalarRangeExpression))
        {
            return scalarRangeExpression;
        }

        if (TryParseInitializerExpression(location, text, out var initializerExpression))
        {
            return initializerExpression;
        }

        if (TryParseSizeOfExpression(location, text, out var sizeOfExpression))
        {
            return sizeOfExpression;
        }

        if (TryParseCastExpression(location, text, out var castExpression))
        {
            return castExpression;
        }

        if (TryParseCallExpression(location, text, out var earlyCallExpression))
        {
            return earlyCallExpression;
        }

        if (TryParseBinaryExpression(location, text, out var binaryExpression))
        {
            return binaryExpression;
        }

        if (TryParsePostfixExpression(location, text, out var postfixExpression))
        {
            return postfixExpression;
        }

        if (TryParseParenthesizedExpression(location, text, out var parenthesizedExpression))
        {
            return parenthesizedExpression;
        }

        if (TryParseUnaryExpression(location, text, out var unaryExpression))
        {
            return unaryExpression;
        }

        if (TryParseMemberExpression(location, text, out var memberExpression))
        {
            return memberExpression;
        }

        if (TryParseIndexExpression(location, text, out var indexExpression))
        {
            return indexExpression;
        }

        _diagnostics.Report(location, $"Could not parse expression '{TrimForDiagnostic(text)}'.");
        return new RawExpressionNode(location, text);
    }

    private static string TrimForDiagnostic(string text)
    {
        text = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 120 ? text : text[..117] + "...";
    }

    private bool TryParseFunctionExpression(Location location, string text, out ExpressionNode expression)
    {
        expression = null!;
        if (!text.StartsWith("fn", StringComparison.Ordinal))
        {
            return false;
        }

        var scan = 2;
        if (scan < text.Length && IsIdentifierPart(text[scan]))
        {
            return false;
        }

        SkipWhitespace(text, ref scan);
        if (scan >= text.Length || text[scan] != '(')
        {
            return false;
        }

        var closeParameters = FindMatchingClose(text, scan, '(', ')');
        if (closeParameters < 0)
        {
            return false;
        }

        var parameters = ParseFunctionExpressionParameters(location, text[(scan + 1)..closeParameters]);
        scan = closeParameters + 1;
        SkipWhitespace(text, ref scan);

        TypeNode? returnTypeNode = null;
        if (scan + 1 < text.Length && text[scan] == '-' && text[scan + 1] == '>')
        {
            scan += 2;
            var returnTypeStart = scan;
            var fatArrow = FindTopLevelFatArrow(text, scan);
            var blockOpen = FindTopLevelOpeningBrace(text, scan);
            if (fatArrow < 0 && blockOpen < 0)
            {
                return false;
            }

            if (blockOpen >= 0 && (fatArrow < 0 || blockOpen < fatArrow))
            {
                returnTypeNode = CreateInlineTypeNode(location, text[returnTypeStart..blockOpen]);
                scan = blockOpen;
            }
            else
            {
                returnTypeNode = CreateInlineTypeNode(location, text[returnTypeStart..fatArrow]);
                scan = fatArrow;
            }
        }

        SkipWhitespace(text, ref scan);
        if (scan < text.Length && text[scan] == '{')
        {
            var closeBody = FindMatchingClose(text, scan, '{', '}');
            if (closeBody != text.Length - 1)
            {
                return false;
            }

            expression = new FunctionExpressionNode(
                location,
                text,
                parameters,
                ExpressionBody: null,
                BlockBody: ParseFunctionExpressionBlock(
                    location,
                    parameters,
                    returnTypeNode?.TypeName ?? "int",
                    text[(scan + 1)..closeBody]),
                ReturnTypeNode: returnTypeNode);
            return true;
        }

        if (scan + 1 >= text.Length || text[scan] != '=' || text[scan + 1] != '>')
        {
            return false;
        }

        var body = text[(scan + 2)..].Trim();
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        expression = new FunctionExpressionNode(
            location,
            text,
            parameters,
            ParseExpressionText(location, body),
            BlockBody: null,
            ReturnTypeNode: returnTypeNode);
        return true;
    }

    private static IReadOnlyList<StatementNode> ParseFunctionExpressionBlock(
        Location location,
        IReadOnlyList<ParameterNode> parameters,
        string returnType,
        string body)
    {
        var parameterText = string.Join(", ", parameters.Select(parameter => $"{parameter.Name}: {parameter.Type}"));
        var source = new SourceFile(
            "<lambda-block>",
            $"fn __lambda_block({parameterText}) -> {returnType} {{{body}}}");
        var parser = new Parser();
        var program = parser.Parse(source);
        return program.Functions.FirstOrDefault()?.Body ?? [];
    }

    private static IReadOnlyList<ParameterNode> ParseFunctionExpressionParameters(Location location, string parameterText)
    {
        if (string.IsNullOrWhiteSpace(parameterText))
        {
            return [];
        }

        var parameters = new List<ParameterNode>();
        foreach (var parameter in SplitTopLevel(parameterText, ','))
        {
            var colon = FindTopLevelColon(parameter);
            if (colon <= 0)
            {
                continue;
            }

            var name = parameter[..colon].Trim();
            var type = parameter[(colon + 1)..].Trim();
            if (name.Length == 0 || type.Length == 0)
            {
                continue;
            }

            var typeNode = CreateInlineTypeNode(location, type);
            parameters.Add(new ParameterNode(location, name, [], TypeNode: typeNode));
        }

        return parameters;
    }

    private static string NormalizeFunctionExpressionType(string type) =>
        NormalizeTypePunctuationSpacing(type.Trim());

    private static string NormalizeTypePunctuationSpacing(string type)
    {
        string previous;
        do
        {
            previous = type;
            type = type
                .Replace("\t", " ", StringComparison.Ordinal)
                .Replace("  ", " ", StringComparison.Ordinal)
                .Replace(" <", "<", StringComparison.Ordinal)
                .Replace("< ", "<", StringComparison.Ordinal)
                .Replace(" >", ">", StringComparison.Ordinal)
                .Replace("> ", ">", StringComparison.Ordinal)
                .Replace(" ,", ",", StringComparison.Ordinal)
                .Replace(", ", ",", StringComparison.Ordinal)
                .Replace(" *", "*", StringComparison.Ordinal)
                .Replace("* ", "*", StringComparison.Ordinal)
                .Replace(" [", "[", StringComparison.Ordinal)
                .Replace("[ ", "[", StringComparison.Ordinal)
                .Replace(" ]", "]", StringComparison.Ordinal)
                .Replace("] ", "]", StringComparison.Ordinal)
                .Trim();
        }
        while (!string.Equals(previous, type, StringComparison.Ordinal));

        return type;
    }

    private static TypeNode CreateInlineTypeNode(Location location, string type) =>
        CreateTypeNode(location, NormalizeFunctionExpressionType(type.Trim()));

    private static IReadOnlyList<TypeNode> CreateInlineTypeNodes(Location location, IReadOnlyList<string> types) =>
        types.Select(type => CreateInlineTypeNode(location, type)).ToList();

    private bool TryParseInitializerExpression(Location location, string text, out ExpressionNode expression)
    {
        expression = null!;
        if (!text.EndsWith("}", StringComparison.Ordinal))
        {
            return false;
        }

        var openBrace = FindMatchingOpen(text, text.Length - 1, '{', '}');
        if (openBrace < 0)
        {
            return false;
        }

        var typeName = openBrace == 0
            ? null
            : text[..openBrace].Trim();
        if (typeName is { Length: 0 })
        {
            return false;
        }

        if (typeName is not null && !IsLikelyInitializerType(typeName))
        {
            return false;
        }

        var typeNameNode = typeName is null ? null : CreateInlineTypeNode(location, typeName);
        var body = text[(openBrace + 1)..^1].Trim();
        if (body.Length == 0)
        {
            expression = new InitializerExpressionNode(location, text, [], [], typeNameNode);
            return true;
        }

        var fields = new List<InitializerFieldNode>();
        var values = new List<ExpressionNode>();
        foreach (var item in SplitTopLevel(body, ','))
        {
            var colon = FindTopLevelColon(item);
            if (colon > 0 && IsIdentifier(item[..colon].Trim()))
            {
                fields.Add(new InitializerFieldNode(
                    item[..colon].Trim(),
                    ParseExpressionText(location, item[(colon + 1)..])));
                continue;
            }

            values.Add(ParseExpressionText(location, item));
        }

        expression = new InitializerExpressionNode(location, text, fields, values, typeNameNode);
        return true;
    }

    private static int FindTopLevelFatArrow(string text, int start)
    {
        var paren = 0;
        var bracket = 0;
        var brace = 0;
        var angle = 0;
        for (var i = start; i < text.Length - 1; i++)
        {
            if (SkipQuotedLiteralForward(text, ref i))
            {
                continue;
            }

            switch (text[i])
            {
                case '(':
                    paren++;
                    break;
                case ')':
                    paren--;
                    break;
                case '[':
                    bracket++;
                    break;
                case ']':
                    bracket--;
                    break;
                case '{':
                    brace++;
                    break;
                case '}':
                    brace--;
                    break;
                case '<':
                    angle++;
                    break;
                case '>':
                    angle--;
                    break;
                case '=' when paren == 0 && bracket == 0 && brace == 0 && angle == 0 && text[i + 1] == '>':
                    return i;
            }
        }

        return -1;
    }

    private static int FindTopLevelOpeningBrace(string text, int start)
    {
        var paren = 0;
        var bracket = 0;
        var angle = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (SkipQuotedLiteralForward(text, ref i))
            {
                continue;
            }

            switch (text[i])
            {
                case '(':
                    paren++;
                    break;
                case ')':
                    paren--;
                    break;
                case '[':
                    bracket++;
                    break;
                case ']':
                    bracket--;
                    break;
                case '<':
                    angle++;
                    break;
                case '>':
                    angle--;
                    break;
                case '{' when paren == 0 && bracket == 0 && angle == 0:
                    return i;
            }
        }

        return -1;
    }

    private static void SkipWhitespace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }
    }

    private static bool IsWholeQuotedLiteral(string text)
    {
        if (text.Length < 2 || text[0] is not ('"' or '\''))
        {
            return false;
        }

        var quote = text[0];
        var escaped = false;
        for (var i = 1; i < text.Length; i++)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (text[i] == '\\')
            {
                escaped = true;
                continue;
            }

            if (text[i] != quote)
            {
                continue;
            }

            return i == text.Length - 1;
        }

        return false;
    }

    private static bool IsIdentifierPart(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

    private bool TryParseParenthesizedExpression(Location location, string text, out ExpressionNode expression)
    {
        expression = null!;
        if (!text.StartsWith("(", StringComparison.Ordinal) || !text.EndsWith(")", StringComparison.Ordinal))
        {
            return false;
        }

        var closeParen = FindMatchingClose(text, 0, '(', ')');
        if (closeParen != text.Length - 1)
        {
            return false;
        }

        var inner = text[1..^1].Trim();
        if (string.IsNullOrWhiteSpace(inner))
        {
            return false;
        }

        expression = new ParenthesizedExpressionNode(
            location,
            text,
            ParseExpressionText(location, inner));
        return true;
    }

    private bool TryParseCastExpression(Location location, string text, out ExpressionNode expression)
    {
        expression = null!;
        if (!text.StartsWith("(", StringComparison.Ordinal))
        {
            return false;
        }

        var closeParen = FindMatchingClose(text, 0, '(', ')');
        if (closeParen <= 1 || closeParen >= text.Length - 1)
        {
            return false;
        }

        var typeText = text[1..closeParen].Trim();
        if (!IsLikelyTypeText(typeText))
        {
            return false;
        }

        var operandText = text[(closeParen + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(operandText) || operandText[0] is '.' or ')' or ']' or '}' or ',' or ';')
        {
            return false;
        }

        var typeNode = CreateInlineTypeNode(location, typeText);
        expression = new CastExpressionNode(
            location,
            text,
            ParseExpressionText(location, operandText),
            typeNode);
        return true;
    }

    private bool TryParseSizeOfExpression(Location location, string text, out ExpressionNode expression)
    {
        expression = null!;
        if (!text.StartsWith("sizeof", StringComparison.Ordinal))
        {
            return false;
        }

        var scan = "sizeof".Length;
        if (scan < text.Length && IsIdentifierPart(text[scan]))
        {
            return false;
        }

        SkipWhitespace(text, ref scan);
        if (scan >= text.Length || text[scan] != '(')
        {
            return false;
        }

        var close = FindMatchingClose(text, scan, '(', ')');
        if (close != text.Length - 1)
        {
            return false;
        }

        var operandText = text[(scan + 1)..close].Trim();
        if (string.IsNullOrWhiteSpace(operandText))
        {
            return false;
        }

        var operandTypeNode = IsLikelySizeOfType(operandText)
            ? CreateInlineTypeNode(location, operandText)
            : null;
        expression = operandTypeNode is not null
            ? new SizeOfExpressionNode(location, text, ExpressionOperand: null, operandTypeNode)
            : new SizeOfExpressionNode(location, text, ParseExpressionText(location, operandText));
        return true;
    }

    private static bool IsLikelySizeOfType(string text) =>
        IsLikelyTypeText(text)
        || IsLikelyInitializerType(text);

    private bool TryParsePostfixExpression(Location location, string text, out ExpressionNode expression)
    {
        expression = null!;
        foreach (var op in new[] { "++", "--" })
        {
            if (!text.EndsWith(op, StringComparison.Ordinal) || text.Length == op.Length)
            {
                continue;
            }

            var operandText = text[..^op.Length].Trim();
            if (string.IsNullOrWhiteSpace(operandText) || HasTopLevelCallBlockingOperator(operandText))
            {
                continue;
            }

            expression = new PostfixExpressionNode(
                location,
                text,
                ParseExpressionText(location, operandText),
                op);
            return true;
        }

        return false;
    }

    private bool TryParseCallExpression(Location location, string text, out ExpressionNode expression)
    {
        expression = null!;
        if (!text.EndsWith(")", StringComparison.Ordinal))
        {
            return false;
        }

        var openParen = FindMatchingOpen(text, text.Length - 1, '(', ')');
        if (openParen <= 0)
        {
            return false;
        }

        var calleeText = text[..openParen].Trim();
        if (string.IsNullOrWhiteSpace(calleeText) || !IsPotentialCalleeText(calleeText))
        {
            return false;
        }

        var argumentsText = text[(openParen + 1)..^1];
        var arguments = string.IsNullOrWhiteSpace(argumentsText)
            ? []
            : SplitTopLevel(argumentsText, ',')
                .Select(argument => ParseExpressionText(location, argument))
                .ToList();

        if (TrySplitGenericOwnerMemberCallee(calleeText, out var genericOwnerTargetText, out var ownerTypeArguments))
        {
            var ownerTypeArgumentNodes = CreateInlineTypeNodes(location, ownerTypeArguments);
            expression = new GenericCallExpressionNode(
                location,
                text,
                ParseExpressionText(location, genericOwnerTargetText),
                arguments,
                ownerTypeArgumentNodes);
            return true;
        }

        if (TrySplitGenericCallee(calleeText, out var genericTargetText, out var typeArguments))
        {
            var typeArgumentNodes = CreateInlineTypeNodes(location, typeArguments);
            expression = new GenericCallExpressionNode(
                location,
                text,
                ParseExpressionText(location, genericTargetText),
                arguments,
                typeArgumentNodes);
            return true;
        }

        if (HasTopLevelCallBlockingOperator(calleeText))
        {
            return false;
        }

        expression = new CallExpressionNode(
            location,
            text,
            ParseExpressionText(location, calleeText),
            arguments);
        return true;
    }

    private static bool IsPotentialCalleeText(string text)
    {
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return char.IsLetter(text[0])
            || text[0] == '_'
            || text[0] == '('
            || text[0] == '*'
            || text.EndsWith("]", StringComparison.Ordinal);
    }

    private static bool TrySplitGenericCallee(
        string calleeText,
        out string targetText,
        out IReadOnlyList<string> typeArguments)
    {
        targetText = string.Empty;
        typeArguments = [];

        if (!calleeText.EndsWith(">", StringComparison.Ordinal))
        {
            return false;
        }

        var genericStart = FindMatchingOpen(calleeText, calleeText.Length - 1, '<', '>');
        if (genericStart <= 0)
        {
            return false;
        }

        targetText = calleeText[..genericStart].Trim();
        if (string.IsNullOrWhiteSpace(targetText))
        {
            return false;
        }

        var typeArgumentText = calleeText[(genericStart + 1)..^1].Trim();
        if (string.IsNullOrWhiteSpace(typeArgumentText))
        {
            return false;
        }

        typeArguments = SplitTopLevel(typeArgumentText, ',');
        return typeArguments.Count > 0;
    }

    private static bool TrySplitGenericOwnerMemberCallee(
        string calleeText,
        out string targetText,
        out IReadOnlyList<string> typeArguments)
    {
        targetText = string.Empty;
        typeArguments = [];

        var dot = FindLastTopLevelOutsideGeneric(calleeText, ".");
        if (dot <= 0 || dot >= calleeText.Length - 1)
        {
            return false;
        }

        var ownerText = calleeText[..dot].Trim();
        var memberName = calleeText[(dot + 1)..].Trim();
        if (!IsIdentifier(memberName)
            || !ownerText.EndsWith(">", StringComparison.Ordinal))
        {
            return false;
        }

        var genericStart = FindMatchingOpen(ownerText, ownerText.Length - 1, '<', '>');
        if (genericStart <= 0)
        {
            return false;
        }

        var ownerName = ownerText[..genericStart].Trim();
        if (string.IsNullOrWhiteSpace(ownerName))
        {
            return false;
        }

        var typeArgumentText = ownerText[(genericStart + 1)..^1].Trim();
        if (string.IsNullOrWhiteSpace(typeArgumentText))
        {
            return false;
        }

        typeArguments = SplitTopLevel(typeArgumentText, ',');
        if (typeArguments.Count == 0)
        {
            return false;
        }

        targetText = ownerName + "." + memberName;
        return true;
    }

    private static bool HasTopLevelCallBlockingOperator(string text)
    {
        if (FindTopLevelQuestion(text) >= 0)
        {
            return true;
        }

        foreach (var op in new[] { "||", "&&", "|", "^", "&", "==", "!=", "<=>", "<=", ">=", "<<", ">>", "+", "-", "*", "/", "%", "=" })
        {
            var index = FindLastTopLevel(text, op);
            if (index <= 0)
            {
                continue;
            }

            if (op == "-" && IsLikelyUnaryMinus(text, index))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private bool TryParseIndexExpression(Location location, string text, out ExpressionNode expression)
    {
        expression = null!;
        if (!text.EndsWith("]", StringComparison.Ordinal))
        {
            return false;
        }

        var openBracket = FindMatchingOpen(text, text.Length - 1, '[', ']');
        if (openBracket <= 0)
        {
            return false;
        }

        var targetText = text[..openBracket].Trim();
        var indexText = text[(openBracket + 1)..^1].Trim();
        if (string.IsNullOrWhiteSpace(targetText) || string.IsNullOrWhiteSpace(indexText))
        {
            return false;
        }

        expression = new IndexExpressionNode(
            location,
            text,
            ParseExpressionText(location, targetText),
            ParseExpressionText(location, indexText));
        return true;
    }

    private bool TryParseMemberExpression(Location location, string text, out ExpressionNode expression)
    {
        expression = null!;
        var dot = FindLastTopLevel(text, ".");
        if (dot <= 0 || dot >= text.Length - 1)
        {
            return false;
        }

        var memberName = text[(dot + 1)..].Trim();
        if (!IsIdentifier(memberName))
        {
            return false;
        }

        expression = new MemberExpressionNode(
            location,
            text,
            ParseExpressionText(location, text[..dot]),
            memberName);
        return true;
    }

    private bool TryParseUnaryExpression(Location location, string text, out ExpressionNode expression)
    {
        expression = null!;
        foreach (var op in new[] { "!", "+", "-", "~", "&", "*" })
        {
            if (!text.StartsWith(op, StringComparison.Ordinal) || text.Length == op.Length)
            {
                continue;
            }

            expression = new UnaryExpressionNode(
                location,
                text,
                op,
                ParseExpressionText(location, text[op.Length..]));
            return true;
        }

        return false;
    }

    private bool TryParseBinaryExpression(Location location, string text, out ExpressionNode expression)
    {
        expression = null!;
        foreach (var operators in new[]
        {
            new[] { "||" },
            new[] { "&&" },
            new[] { "|" },
            new[] { "^" },
            new[] { "&" },
            new[] { "==", "!=", "<=>", "<=", ">=", "<", ">" },
            new[] { "<<", ">>" },
            new[] { "+", "-" },
            new[] { "*", "/", "%" },
        })
        {
            foreach (var op in operators)
            {
                var index = FindLastTopLevel(text, op);
                if (index <= 0 || index >= text.Length - op.Length)
                {
                    continue;
                }

                if (op == "-" && IsLikelyUnaryMinus(text, index))
                {
                    continue;
                }

                if (op is "&" or "|" && IsPartOfRepeatedOperator(text, index))
                {
                    continue;
                }

                if (op is "<" or ">" && IsPartOfGenericCallTypeArguments(text, index))
                {
                    continue;
                }

                if (op is "<" or ">" && IsPartOfShiftOperator(text, index))
                {
                    continue;
                }

                expression = new BinaryExpressionNode(
                    location,
                    text,
                    ParseExpressionText(location, text[..index]),
                    op,
                    ParseExpressionText(location, text[(index + op.Length)..]));
                return true;
            }
        }

        return false;
    }

    private static bool IsPartOfRepeatedOperator(string text, int index)
    {
        var current = text[index];
        return (index > 0 && text[index - 1] == current)
            || (index + 1 < text.Length && text[index + 1] == current);
    }

    private static bool IsPartOfGenericCallTypeArguments(string text, int index)
    {
        if (text[index] == '<')
        {
            var close = FindMatchingClose(text, index, '<', '>');
            return close > index && NextNonWhitespace(text, close + 1) is { } next && text[next] == '(';
        }

        if (text[index] == '>')
        {
            var open = FindMatchingOpen(text, index, '<', '>');
            return open >= 0 && NextNonWhitespace(text, index + 1) is { } next && text[next] == '(';
        }

        return false;
    }

    private static int? NextNonWhitespace(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return i;
            }
        }

        return null;
    }

    private static bool IsPartOfShiftOperator(string text, int index)
    {
        var current = text[index];
        return (index > 0 && text[index - 1] == current)
            || (index + 1 < text.Length && text[index + 1] == current);
    }

    private bool TryParseConditionalExpression(Location location, string text, out ExpressionNode expression)
    {
        expression = null!;
        var questionIndex = FindTopLevelQuestion(text);
        if (questionIndex <= 0 || questionIndex >= text.Length - 1)
        {
            return false;
        }

        var colonIndex = FindMatchingConditionalColon(text, questionIndex);
        if (colonIndex <= questionIndex + 1 || colonIndex >= text.Length - 1)
        {
            return false;
        }

        expression = new ConditionalExpressionNode(
            location,
            text,
            ParseExpressionText(location, text[..questionIndex]),
            ParseExpressionText(location, text[(questionIndex + 1)..colonIndex]),
            ParseExpressionText(location, text[(colonIndex + 1)..]));
        return true;
    }

    private bool TryParseScalarRangeExpression(Location location, string text, out ExpressionNode expression)
    {
        expression = null!;
        if (FindTopLevelRangeOperator(text) is not { } range)
        {
            return false;
        }

        var left = text[..range.Index].Trim();
        var right = text[(range.Index + range.Length)..].Trim();
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        expression = new ScalarRangeExpressionNode(
            location,
            text,
            ParseExpressionText(location, left),
            ParseExpressionText(location, right),
            IsInclusive: range.Length == 3);
        return true;
    }

    private static (int Index, int Length)? FindTopLevelRangeOperator(string text)
    {
        var paren = 0;
        var bracket = 0;
        var brace = 0;
        var angle = 0;
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (SkipQuotedLiteralForward(text, ref i))
            {
                continue;
            }

            switch (text[i])
            {
                case '(':
                    paren++;
                    break;
                case ')':
                    paren--;
                    break;
                case '[':
                    bracket++;
                    break;
                case ']':
                    bracket--;
                    break;
                case '{':
                    brace++;
                    break;
                case '}':
                    brace--;
                    break;
                case '<':
                    angle++;
                    break;
                case '>':
                    angle--;
                    break;
                case '.' when paren == 0 && bracket == 0 && brace == 0 && angle == 0 && text[i + 1] == '.':
                    return i + 2 < text.Length && text[i + 2] == '.'
                        ? (i, 3)
                        : (i, 2);
            }
        }

        return null;
    }

    private static int FindTopLevelQuestion(string text)
    {
        var paren = 0;
        var bracket = 0;
        var brace = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (SkipQuotedLiteralForward(text, ref i))
            {
                continue;
            }

            switch (text[i])
            {
                case '(':
                    paren++;
                    break;
                case ')':
                    paren--;
                    break;
                case '[':
                    bracket++;
                    break;
                case ']':
                    bracket--;
                    break;
                case '{':
                    brace++;
                    break;
                case '}':
                    brace--;
                    break;
                case '?' when paren == 0 && bracket == 0 && brace == 0:
                    return i;
            }
        }

        return -1;
    }

    private static int FindMatchingConditionalColon(string text, int questionIndex)
    {
        var paren = 0;
        var bracket = 0;
        var brace = 0;
        var nestedConditional = 0;
        for (var i = questionIndex + 1; i < text.Length; i++)
        {
            if (SkipQuotedLiteralForward(text, ref i))
            {
                continue;
            }

            switch (text[i])
            {
                case '(':
                    paren++;
                    break;
                case ')':
                    paren--;
                    break;
                case '[':
                    bracket++;
                    break;
                case ']':
                    bracket--;
                    break;
                case '{':
                    brace++;
                    break;
                case '}':
                    brace--;
                    break;
                case '?' when paren == 0 && bracket == 0 && brace == 0:
                    nestedConditional++;
                    break;
                case ':' when paren == 0 && bracket == 0 && brace == 0:
                    if (nestedConditional == 0)
                    {
                        return i;
                    }

                    nestedConditional--;
                    break;
            }
        }

        return -1;
    }

    private bool TryParseAssignmentExpression(Location location, string text, out ExpressionNode expression)
    {
        expression = null!;
        foreach (var op in new[] { "+=", "-=", "*=", "/=", "%=", "=" })
        {
            var index = FindLastTopLevel(text, op);
            if (index <= 0 || index >= text.Length - op.Length)
            {
                continue;
            }

            if (op == "=" && IsPartOfNonAssignmentOperator(text, index))
            {
                continue;
            }

            expression = new AssignmentExpressionNode(
                location,
                text,
                ParseExpressionText(location, text[..index]),
                op,
                ParseExpressionText(location, text[(index + op.Length)..]));
            return true;
        }

        return false;
    }

    private static bool IsPartOfNonAssignmentOperator(string text, int index)
    {
        var before = index > 0 ? text[index - 1] : '\0';
        var after = index + 1 < text.Length ? text[index + 1] : '\0';
        return before is '=' or '!' or '<' or '>' || after is '=' or '>';
    }

    private static int FindMatchingOpen(string text, int closeIndex, char open, char close)
    {
        var depth = 0;
        for (var i = closeIndex; i >= 0; i--)
        {
            if (SkipQuotedLiteralBackward(text, ref i))
            {
                continue;
            }

            if (text[i] == close)
            {
                depth++;
                continue;
            }

            if (text[i] != open)
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

    private static int FindMatchingClose(string text, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (SkipQuotedLiteralForward(text, ref i))
            {
                continue;
            }

            if (text[i] == open)
            {
                depth++;
                continue;
            }

            if (text[i] != close)
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

    private static int FindLastTopLevel(string text, string value)
    {
        var paren = 0;
        var bracket = 0;
        var brace = 0;
        var last = -1;
        for (var i = 0; i <= text.Length - value.Length; i++)
        {
            if (SkipQuotedLiteralForward(text, ref i))
            {
                continue;
            }

            if (paren == 0
                && bracket == 0
                && brace == 0
                && string.Equals(text.Substring(i, value.Length), value, StringComparison.Ordinal))
            {
                last = i;
            }

            var ch = text[i];
            switch (ch)
            {
                case '(': paren++; break;
                case ')': paren--; break;
                case '[': bracket++; break;
                case ']': bracket--; break;
                case '{': brace++; break;
                case '}': brace--; break;
            }
        }

        return last;
    }

    private static int FindLastTopLevelOutsideGeneric(string text, string value)
    {
        var paren = 0;
        var bracket = 0;
        var brace = 0;
        var angle = 0;
        var last = -1;
        for (var i = 0; i <= text.Length - value.Length; i++)
        {
            if (SkipQuotedLiteralForward(text, ref i))
            {
                continue;
            }

            if (paren == 0
                && bracket == 0
                && brace == 0
                && angle == 0
                && string.Equals(text.Substring(i, value.Length), value, StringComparison.Ordinal))
            {
                last = i;
            }

            var ch = text[i];
            switch (ch)
            {
                case '(': paren++; break;
                case ')': paren--; break;
                case '[': bracket++; break;
                case ']': bracket--; break;
                case '{': brace++; break;
                case '}': brace--; break;
                case '<': angle++; break;
                case '>': angle--; break;
            }
        }

        return last;
    }

    private static bool IsLikelyUnaryMinus(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                continue;
            }

            return text[i] is '(' or '[' or '{' or ',' or ':' or '=' or '+' or '-' or '*' or '/' or '%';
        }

        return true;
    }

    private static bool IsIdentifier(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && (char.IsLetter(text[0]) || text[0] == '_')
        && text.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    private static bool IsLikelyTypeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || text.Any(ch => ch is '+' or '-' or '/' or '%' or '=' or '&' or '|' or '!' or '?' or ':' or '.' or '[' or ']'))
        {
            return false;
        }

        var firstStar = text.IndexOf('*');
        if (firstStar >= 0 && text[(firstStar + 1)..].Any(ch => ch != '*' && !char.IsWhiteSpace(ch)))
        {
            return false;
        }

        var genericDepth = 0;
        foreach (var ch in text)
        {
            if (ch == '<')
            {
                genericDepth++;
                continue;
            }

            if (ch == '>')
            {
                genericDepth--;
                if (genericDepth < 0)
                {
                    return false;
                }

                continue;
            }

            if (char.IsLetterOrDigit(ch) || ch is '_' or '*' or ',' || char.IsWhiteSpace(ch))
            {
                continue;
            }

            return false;
        }

        return genericDepth == 0
            && text.Any(char.IsLetter)
            && (char.IsLetter(text.TrimStart()[0]) || text.TrimStart()[0] == '_');
    }

    private static bool IsLikelyInitializerType(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var depth = 0;
        foreach (var ch in text)
        {
            if (ch == '<')
            {
                depth++;
                continue;
            }

            if (ch == '>')
            {
                depth--;
                if (depth < 0)
                {
                    return false;
                }

                continue;
            }

            if (char.IsLetterOrDigit(ch) || ch is '_' or '.' or '*' or ',' || char.IsWhiteSpace(ch))
            {
                continue;
            }

            return false;
        }

        return depth == 0
            && text.Any(char.IsLetter)
            && (char.IsLetter(text.TrimStart()[0]) || text.TrimStart()[0] == '_');
    }

    private static bool ContainsLambdaFatArrow(string text)
    {
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (SkipQuotedLiteralForward(text, ref i))
            {
                continue;
            }

            if (text[i] != '=' || text[i + 1] != '>')
            {
                continue;
            }

            if (i > 0 && text[i - 1] == '<')
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool SkipQuotedLiteralForward(string text, ref int index)
    {
        if (index >= text.Length || text[index] is not ('"' or '\''))
        {
            return false;
        }

        var quote = text[index];
        index++;
        while (index < text.Length)
        {
            if (text[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (text[index] == quote)
            {
                return true;
            }

            index++;
        }

        index = text.Length - 1;
        return true;
    }

    private static bool SkipQuotedLiteralBackward(string text, ref int index)
    {
        if (index < 0 || text[index] is not ('"' or '\''))
        {
            return false;
        }

        var quote = text[index];
        index--;
        while (index >= 0)
        {
            if (text[index] == quote && !IsEscaped(text, index))
            {
                return true;
            }

            index--;
        }

        index = 0;
        return true;
    }

    private static bool IsEscaped(string text, int index)
    {
        var slashCount = 0;
        for (var i = index - 1; i >= 0 && text[i] == '\\'; i--)
        {
            slashCount++;
        }

        return slashCount % 2 == 1;
    }

    private static IReadOnlyList<string> SplitTopLevel(string text, char separator)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var parts = new List<string>();
        var start = 0;
        var paren = 0;
        var bracket = 0;
        var brace = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (SkipQuotedLiteralForward(text, ref i))
            {
                continue;
            }

            switch (text[i])
            {
                case '(': paren++; break;
                case ')': paren--; break;
                case '[': bracket++; break;
                case ']': bracket--; break;
                case '{': brace++; break;
                case '}': brace--; break;
            }

            if (text[i] != separator || paren != 0 || bracket != 0 || brace != 0)
            {
                continue;
            }

            parts.Add(text[start..i].Trim());
            start = i + 1;
        }

        parts.Add(text[start..].Trim());
        return parts;
    }

    private string ParseType() => ParseTypeNode().TypeName;

    private TypeNode ParseTypeNode()
    {
        var location = Current.Location;
        if (Check(TokenType.Fn))
        {
            return ParseFunctionTypeNode();
        }

        var typeNames = ParseTypeName();
        if (typeNames.Count == 0)
        {
            _diagnostics.Report(Current.Location, "Expected type name.");
            return CreateTypeNode(location, string.Empty);
        }

        var parts = new List<string> { string.Join(" ", typeNames) };

        if (ConsumeOptional(TokenType.LessThan))
        {
            var typeArguments = new List<string>();

            if (!CheckTypeCloseAngle())
            {
                do
                {
                    typeArguments.Add(ParseTypeNode().TypeName);
                }
                while (ConsumeOptional(TokenType.Comma));
            }

            ExpectTypeCloseAngle("Expected '>' after generic type arguments.");
            parts.Add("<");
            parts.Add(string.Join(",", typeArguments));
            parts.Add(">");
        }

        if (_pendingTypeCloseAngles == 0)
        {
            while (ConsumeOptional(TokenType.Star))
            {
                parts.Add("*");
            }

            while (ConsumeOptional(TokenType.LBracket))
            {
                var length = ReadUntil(TokenType.RBracket);
                Expect(TokenType.RBracket, "Expected ']' after array length.");
                parts.Add("[");
                parts.Add(length);
                parts.Add("]");
            }
        }

        return CreateTypeNode(location, string.Join("", parts));
    }

    private List<string> ParseTypeName()
    {
        var names = new List<string>();

        if (Current.Type is TokenType.Struct or TokenType.Enum or TokenType.Union)
        {
            names.Add(Advance().Value);
            if (Expect(TokenType.Identifier, "Expected C tag type name.") is { } tagName)
            {
                names.Add(tagName.Value);
            }

            return names;
        }

        var isConst = Current.Type == TokenType.Const;
        var first = isConst
            ? Advance()
            : Expect(TokenType.Identifier, "Expected type name.");

        if (first is null)
        {
            return names;
        }

        names.Add(first.Value);
        if (isConst && Current.Type == TokenType.Identifier)
        {
            names.Add(Advance().Value);
        }

        while (ConsumeOptional(TokenType.Dot))
        {
            if (Expect(TokenType.Identifier, "Expected type name after '.'.") is { } part)
            {
                names[^1] = names[^1] + "." + part.Value;
            }
        }

        while (Current.Type == TokenType.Identifier && IsTypeNameContinuation(Current.Value))
        {
            names.Add(Advance().Value);
        }

        return names;
    }

    private static bool IsTypeNameContinuation(string value) =>
        value is "void" or "char" or "short" or "int" or "long" or "float" or "double";

    private TypeNode ParseFunctionTypeNode()
    {
        var location = Current.Location;
        Expect(TokenType.Fn, "Expected 'fn'.");
        Expect(TokenType.LParen, "Expected '(' after 'fn' in function type.");

        var parameterTypes = new List<string>();
        if (!Check(TokenType.RParen))
        {
            do
            {
                if (Current.Type == TokenType.Identifier && PeekType() == TokenType.Colon)
                {
                    Advance();
                    Expect(TokenType.Colon, "Expected ':' after function type parameter name.");
                }

                if (Match(TokenType.Ellipsis) is not null)
                {
                    parameterTypes.Add("...");
                }
                else
                {
                    parameterTypes.Add(ParseTypeNode().TypeName);
                }
            }
            while (ConsumeOptional(TokenType.Comma));
        }

        ValidateVariadicFunctionType(parameterTypes);
        Expect(TokenType.RParen, "Expected ')' after function type parameters.");
        Expect(TokenType.Arrow, "Expected '->' before function type return type.");
        var returnType = ParseTypeNode().TypeName;

        return CreateTypeNode(location, $"fn({string.Join(",", parameterTypes)})->{returnType}");
    }

    private static TypeNode CreateTypeNode(Location location, string type) =>
        new(location, type, TypeSyntaxParser.Parse(type));

    private bool CheckTypeCloseAngle() =>
        _pendingTypeCloseAngles > 0
        || Check(TokenType.GreaterThan)
        || Check(TokenType.GreaterThanGreaterThan);

    private void ExpectTypeCloseAngle(string message)
    {
        if (ConsumeTypeCloseAngle())
        {
            return;
        }

        _diagnostics.Report(Current.Location, message);
    }

    private bool ConsumeTypeCloseAngle()
    {
        if (_pendingTypeCloseAngles > 0)
        {
            _pendingTypeCloseAngles--;
            return true;
        }

        if (ConsumeOptional(TokenType.GreaterThan))
        {
            return true;
        }

        if (ConsumeOptional(TokenType.GreaterThanGreaterThan))
        {
            _pendingTypeCloseAngles++;
            return true;
        }

        return false;
    }

    private void ValidateVariadicFunctionType(IReadOnlyList<string> parameterTypes)
    {
        for (var i = 0; i < parameterTypes.Count; i++)
        {
            if (parameterTypes[i] != "...")
            {
                continue;
            }

            if (i != parameterTypes.Count - 1)
            {
                _diagnostics.Report(Current.Location, "Variadic marker '...' must be the last function type parameter.");
            }

            if (i == 0)
            {
                _diagnostics.Report(Current.Location, "Variadic function types require at least one fixed parameter before '...'.");
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
                        var argumentText = ReadUntilAny(TokenType.Comma, TokenType.RParen);
                        var colon = FindTopLevelColon(argumentText);
                        string? argumentName = null;
                        var value = argumentText.Trim();

                        if (colon > 0)
                        {
                            argumentName = argumentText[..colon].Trim();
                            value = argumentText[(colon + 1)..].Trim();
                        }

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

    private string ReadUntil(TokenType type)
    {
        var parts = new List<string>();
        var depth = 0;

        while (!IsAtEnd)
        {
            if (depth <= 0 && Check(type))
            {
                break;
            }

            if (Check(TokenType.LParen) || Check(TokenType.LBracket) || Check(TokenType.LBrace))
            {
                depth++;
            }
            else if (Check(TokenType.RParen) || Check(TokenType.RBracket) || Check(TokenType.RBrace))
            {
                depth--;
            }

            parts.Add(Advance().Value);
        }

        return NormalizeTokens(parts);
    }

    private static int FindTopLevelColon(string text)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (SkipQuotedLiteralForward(text, ref i))
            {
                continue;
            }

            depth += text[i] switch
            {
                '(' or '[' or '{' => 1,
                ')' or ']' or '}' => -1,
                _ => 0
            };

            if (text[i] == ':' && depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private string ReadUntilAny(params TokenType[] types)
    {
        var parts = new List<string>();
        var depth = 0;

        while (!IsAtEnd)
        {
            if (depth <= 0 && types.Any(Check))
            {
                break;
            }

            if (Check(TokenType.LParen) || Check(TokenType.LBracket) || Check(TokenType.LBrace))
            {
                depth++;
            }
            else if (Check(TokenType.RParen) || Check(TokenType.RBracket) || Check(TokenType.RBrace))
            {
                depth--;
            }

            parts.Add(Advance().Value);
        }

        return NormalizeTokens(parts);
    }

    private static string NormalizeTokens(IEnumerable<string> parts)
    {
        var builder = new StringBuilder();
        string? previous = null;

        foreach (var part in parts)
        {
            if (previous is not null && NeedsSpace(previous, part))
            {
                builder.Append(' ');
            }

            builder.Append(part);
            previous = part;
        }

        return builder.ToString();
    }

    private static bool NeedsSpace(string left, string right)
    {
        if (left is "(" or "[" or "." or "->" || right is ")" or "]" or "," or ";" or "." or "->")
        {
            return false;
        }

        if (right is "(" or "[")
        {
            return false;
        }

        return true;
    }

    private Token? Expect(TokenType type, string message)
    {
        if (Current.Type == type)
        {
            return Advance();
        }

        _diagnostics.Report(Current.Location, message);
        return null;
    }

    private Token? ExpectIdentifierLike(string message)
    {
        if (Current.Type is TokenType.Identifier or TokenType.Type or TokenType.Default)
        {
            return Advance();
        }

        _diagnostics.Report(Current.Location, message);
        return null;
    }

    private Token? Match(TokenType type)
    {
        if (!Check(type))
        {
            return null;
        }

        return Advance();
    }

    private bool ConsumeOptional(TokenType type)
    {
        if (!Check(type))
        {
            return false;
        }

        Advance();
        return true;
    }

    private bool Check(TokenType type) => Current.Type == type;

    private bool IsContextualKeyword(string value) =>
        Current.Type == TokenType.Identifier
        && string.Equals(Current.Value, value, StringComparison.Ordinal);

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

    private Token Advance()
    {
        var current = Current;
        if (!IsAtEnd)
        {
            _position++;
        }

        return current;
    }

    private bool IsAtEnd => Current.Type == TokenType.Eof;

    private Token Current => _tokens[_position];

    private TokenType PeekType(int offset = 1)
    {
        var index = _position + offset;
        return index < _tokens.Count ? _tokens[index].Type : TokenType.Eof;
    }
}
