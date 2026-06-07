using Cx.Compiler.C;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed record MatchInfo(
        string TagAccess,
        TaggedUnionNode? Union,
        string? PayloadAccess,
        InterfaceNode? Interface,
        IReadOnlyDictionary<string, InterfaceImplementation> InterfaceImplementations,
        string? StateAccess)
    {
        public static MatchInfo ForTaggedUnion(
            TaggedUnionNode union,
            string tagAccess,
            string payloadAccess) =>
            new(tagAccess, union, payloadAccess, null, new Dictionary<string, InterfaceImplementation>(), null);

        public static MatchInfo ForInterface(
            InterfaceNode interfaceNode,
            string tagAccess,
            string stateAccess,
            IReadOnlyDictionary<string, InterfaceImplementation> implementations) =>
            new(tagAccess, null, null, interfaceNode, implementations, stateAccess);

        public string CaseLabel(string pattern) =>
            Union is not null
                ? $"{Union.Name}_Tag_{pattern}"
                : GetTypeIdName(pattern);

        public string? BindingType(string pattern)
        {
            if (Union is not null)
            {
                return Union.Variants.FirstOrDefault(variant => variant.Name == pattern)?.Type;
            }

            return InterfaceImplementations.ContainsKey(pattern)
                ? pattern + "*"
                : null;
        }

        public CExpression? BindingExpression(string pattern)
        {
            if (Union is not null && PayloadAccess is not null)
            {
                return ToCSimpleAccessExpression($"{PayloadAccess}.{pattern}");
            }

            if (Interface is not null
                && StateAccess is not null
                && InterfaceImplementations.ContainsKey(pattern))
            {
                return new CCastExpression(LowerType(pattern) + "*", ToCSimpleAccessExpression(StateAccess));
            }

            return null;
        }
    }
}
