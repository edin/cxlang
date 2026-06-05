namespace Cx.Compiler.Lexer;

public enum TokenType
{
    [Token(TokenClass.Identifier)]
    Identifier,
    [Token(TokenClass.Literal)]
    Number,
    [Token(TokenClass.Literal)]
    String,
    [Token(TokenClass.Literal)]
    Character,

    [Token("fn", TokenClass.Keyword)]
    Fn,
    [Token("static", TokenClass.Keyword)]
    Static,
    [Token("let", TokenClass.Keyword)]
    Let,
    [Token("const", TokenClass.Keyword)]
    Const,
    [Token("return", TokenClass.Keyword)]
    Return,
    [Token("module", TokenClass.Keyword)]
    Module,
    [Token("import", TokenClass.Keyword)]
    Import,
    [Token("from", TokenClass.Keyword)]
    From,
    [Token("as", TokenClass.Keyword)]
    As,
    [Token("include", TokenClass.Keyword)]
    Include,
    [Token("declare", TokenClass.Keyword)]
    Declare,
    [Token("link", TokenClass.Keyword)]
    Link,
    [Token("macro", TokenClass.Keyword)]
    Macro,
    [Token("extern", TokenClass.Keyword)]
    Extern,
    [Token("raw", TokenClass.Keyword)]
    Raw,
    [Token("struct", TokenClass.Keyword)]
    Struct,
    [Token("extension", TokenClass.Keyword)]
    Extension,
    [Token("interface", TokenClass.Keyword)]
    Interface,
    [Token("enum", TokenClass.Keyword)]
    Enum,
    [Token("type", TokenClass.Keyword)]
    Type,
    [Token("using", TokenClass.Keyword)]
    Using,
    [Token("over", TokenClass.Keyword)]
    Over,
    [Token("expose", TokenClass.Keyword)]
    Expose,
    [Token("opaque", TokenClass.Keyword)]
    Opaque,
    [Token("union", TokenClass.Keyword)]
    Union,
    [Token("if", TokenClass.Keyword)]
    If,
    [Token("else", TokenClass.Keyword)]
    Else,
    [Token("switch", TokenClass.Keyword)]
    Switch,
    [Token("case", TokenClass.Keyword)]
    Case,
    [Token("default", TokenClass.Keyword)]
    Default,
    [Token("break", TokenClass.Keyword)]
    Break,
    [Token("continue", TokenClass.Keyword)]
    Continue,
    [Token("while", TokenClass.Keyword)]
    While,
    [Token("for", TokenClass.Keyword)]
    For,
    [Token("foreach", TokenClass.Keyword)]
    Foreach,
    [Token("in", TokenClass.Keyword)]
    In,
    [Token("requires", TokenClass.Keyword)]
    Requires,
    [Token("where", TokenClass.Keyword)]
    Where,
    [Token("match", TokenClass.Keyword)]
    Match,
    [Token("true", TokenClass.Keyword)]
    True,
    [Token("false", TokenClass.Keyword)]
    False,
    [Token("null", TokenClass.Keyword)]
    Null,
    [Token("attribute", TokenClass.Keyword)]
    Attribute,
    [Token("on", TokenClass.Keyword)]
    On,

    [Token("->", TokenClass.Symbol)]
    Arrow,
    [Token("=>", TokenClass.Symbol)]
    FatArrow,
    [Token("{", TokenClass.Symbol)]
    LBrace,
    [Token("}", TokenClass.Symbol)]
    RBrace,
    [Token("(", TokenClass.Symbol)]
    LParen,
    [Token(")", TokenClass.Symbol)]
    RParen,
    [Token("[", TokenClass.Symbol)]
    LBracket,
    [Token("]", TokenClass.Symbol)]
    RBracket,
    [Token("*", TokenClass.Symbol)]
    Star,
    [Token("=", TokenClass.Symbol)]
    Equals,
    [Token(":", TokenClass.Symbol)]
    Colon,
    [Token(";", TokenClass.Symbol)]
    Semicolon,
    [Token(",", TokenClass.Symbol)]
    Comma,
    [Token("...", TokenClass.Symbol)]
    Ellipsis,
    [Token("..", TokenClass.Symbol)]
    DotDot,
    [Token(".", TokenClass.Symbol)]
    Dot,
    [Token("==", TokenClass.Symbol)]
    EqualEqual,
    [Token("!=", TokenClass.Symbol)]
    BangEqual,
    [Token("<=>", TokenClass.Symbol)]
    Spaceship,
    [Token("<=", TokenClass.Symbol)]
    LessThanOrEqual,
    [Token(">=", TokenClass.Symbol)]
    GreaterThanOrEqual,
    [Token("&&", TokenClass.Symbol)]
    AmpersandAmpersand,
    [Token("||", TokenClass.Symbol)]
    PipePipe,
    [Token("++", TokenClass.Symbol)]
    PlusPlus,
    [Token("--", TokenClass.Symbol)]
    MinusMinus,
    [Token("+=", TokenClass.Symbol)]
    PlusEquals,
    [Token("-=", TokenClass.Symbol)]
    MinusEquals,
    [Token("*=", TokenClass.Symbol)]
    StarEquals,
    [Token("/=", TokenClass.Symbol)]
    SlashEquals,
    [Token("%=", TokenClass.Symbol)]
    PercentEquals,
    [Token("<<", TokenClass.Symbol)]
    LessThanLessThan,
    [Token(">>", TokenClass.Symbol)]
    GreaterThanGreaterThan,
    [Token("+", TokenClass.Symbol)]
    Plus,
    [Token("-", TokenClass.Symbol)]
    Minus,
    [Token("/", TokenClass.Symbol)]
    Slash,
    [Token("%", TokenClass.Symbol)]
    Percent,
    [Token("!", TokenClass.Symbol)]
    Bang,
    [Token("&", TokenClass.Symbol)]
    Ampersand,
    [Token("|", TokenClass.Symbol)]
    Pipe,
    [Token("^", TokenClass.Symbol)]
    Caret,
    [Token("~", TokenClass.Symbol)]
    Tilde,
    [Token("<", TokenClass.Symbol)]
    LessThan,
    [Token(">", TokenClass.Symbol)]
    GreaterThan,
    [Token("?", TokenClass.Symbol)]
    QuestionMark,
    [Token("@", TokenClass.Symbol)]
    At,

    [Token(TokenClass.Trivia)]
    Comment,
    [Token(TokenClass.Trivia)]
    MultilineComment,
    [Token(TokenClass.EndOfFile)]
    Eof
}
