using Cx.Compiler.Semantic;

namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed class RequirementLookup(RequirementMatcher matcher)
    {
        public RequirementMatch Match(string type, string requirementName) =>
            matcher.Match(type, requirementName);
    }
}
