using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Derive;

internal static class DeriveGenerator
{
    public static string Generate(ProgramNode program) =>
        DebugDeriveGenerator.Generate(program);
}
