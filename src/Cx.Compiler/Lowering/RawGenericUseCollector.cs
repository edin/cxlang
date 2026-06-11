using System.Text.RegularExpressions;
using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Lowering;

internal sealed class RawGenericUseCollector(IReadOnlyList<FunctionNode> genericFunctions)
{
    private readonly List<RawGenericUseAuditEntry> _auditEntries = [];
    private readonly TypeRefParser _typeRefParser = new(new ProgramNode(
        Location.Synthetic("<raw-generic-use>"),
        genericFunctions.Cast<TopLevelNode>().ToList()));

    public IReadOnlyList<RawGenericUseAuditEntry> AuditEntries => _auditEntries;

    public IEnumerable<GenericFunctionUse> Collect(
        string expression,
        IReadOnlyDictionary<string, string> variables,
        string context = "",
        IReadOnlySet<GenericFunctionUseKey>? knownUses = null)
    {
        foreach (var function in genericFunctions)
        {
            var ownerType = OwnerType(function);
            var staticCallee = ownerType is null
                ? function.Name
                : $"{ownerType}.{function.Name}";
            foreach (var arguments in FindExplicitTypeArgumentCalls(expression, staticCallee))
            {
                if (arguments.Count == function.TypeParameters.Count)
                {
                    if (ShouldSkip(function, arguments, knownUses))
                    {
                        continue;
                    }

                    AddAuditEntry(context, expression, function, arguments, "explicit type argument call");
                    yield return new GenericFunctionUse(function, arguments);
                }
            }
        }

        foreach (var (variable, variableType) in variables)
        {
            var owner = TypeSyntaxFacts.GetGenericBaseName(variableType);
            if (owner is null)
            {
                continue;
            }

            foreach (var function in genericFunctions.Where(function => OwnerType(function) == owner && !function.IsStatic))
            {
                var inferredCallPattern = $@"\b{Regex.Escape(variable)}\s*\.\s*{Regex.Escape(function.Name)}\s*\(";
                if (function.TypeParameters.Count == (TypeSyntaxFacts.TryParseGenericUse(variableType, out _, out var receiverArguments) ? receiverArguments.Count : 0)
                    && Regex.IsMatch(expression, inferredCallPattern))
                {
                    if (ShouldSkip(function, receiverArguments, knownUses))
                    {
                        continue;
                    }

                    AddAuditEntry(context, expression, function, receiverArguments, "receiver type argument inference");
                    yield return new GenericFunctionUse(function, receiverArguments);
                }

                foreach (var arguments in FindExplicitTypeArgumentCalls(expression, $"{variable}.{function.Name}"))
                {
                    if (arguments.Count == function.TypeParameters.Count)
                    {
                        if (ShouldSkip(function, arguments, knownUses))
                        {
                            continue;
                        }

                        AddAuditEntry(context, expression, function, arguments, "explicit receiver type argument call");
                        yield return new GenericFunctionUse(function, arguments);
                    }
                }
            }
        }
    }

    private static bool ShouldSkip(
        FunctionNode function,
        IReadOnlyList<string> typeArguments,
        IReadOnlySet<GenericFunctionUseKey>? knownUses) =>
        knownUses is not null && knownUses.Contains(GenericFunctionUseKey.Create(function, typeArguments));

    private void AddAuditEntry(
        string context,
        string expression,
        FunctionNode function,
        IReadOnlyList<string> typeArguments,
        string reason)
    {
        _auditEntries.Add(new RawGenericUseAuditEntry(
            string.IsNullOrWhiteSpace(context) ? "<unknown>" : context,
            TrimForAudit(expression),
            FormatFunctionName(function),
            typeArguments.ToList(),
            reason));
    }

    private string FormatFunctionName(FunctionNode function)
    {
        var ownerType = OwnerType(function);
        return ownerType is null
            ? function.Name
            : $"{ownerType}.{function.Name}";
    }

    private string? OwnerType(FunctionNode function)
    {
        var type = TypeText(function.OwnerTypeNode);
        return string.IsNullOrWhiteSpace(type) ? null : type;
    }

    private string TypeText(TypeNode? typeNode)
    {
        if (typeNode is null)
        {
            return string.Empty;
        }

        var type = typeNode.ToTypeRef(_typeRefParser);
        return type is TypeRef.Unknown ? string.Empty : TypeRefFormatter.ToCxString(type);
    }

    private static string TrimForAudit(string expression)
    {
        expression = expression.Trim();
        return expression.Length <= 120
            ? expression
            : expression[..117] + "...";
    }

    private static IReadOnlyList<IReadOnlyList<string>> FindExplicitTypeArgumentCalls(string expression, string callee)
    {
        var uses = new List<IReadOnlyList<string>>();
        var pattern = Regex.Escape(callee).Replace("\\.", @"\s*\.\s*") + @"\s*<";

        foreach (Match match in Regex.Matches(expression, pattern))
        {
            var openIndex = expression.IndexOf('<', match.Index + match.Length - 1);
            if (openIndex < 0)
            {
                continue;
            }

            var closeIndex = TypeSyntaxFacts.FindMatchingGenericClose(expression, openIndex);
            if (closeIndex < 0)
            {
                continue;
            }

            var after = expression[(closeIndex + 1)..];
            if (!Regex.IsMatch(after, @"^\s*\("))
            {
                continue;
            }

            uses.Add(TypeSyntaxFacts.SplitGenericArguments(expression[(openIndex + 1)..closeIndex]));
        }

        return uses;
    }
}

internal sealed record RawGenericUseAuditEntry(
    string Context,
    string Expression,
    string FunctionName,
    IReadOnlyList<string> TypeArguments,
    string Reason);
