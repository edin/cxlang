namespace Cx.Compiler.Semantic;

internal enum LocalMutability
{
    Mutable,
    Const,
    ConstGlobal,
    ForeachIndex,
    ForeachKey,
    ForeachConstItem,
}
