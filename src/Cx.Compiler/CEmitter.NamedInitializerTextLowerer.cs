using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed class NamedInitializerTextLowerer(
        CLoweringContext context,
        Func<string, string, Func<string, string>, string> replaceBracedExpressions,
        Func<string, IReadOnlyList<string>> splitArguments,
        Func<string, string> lowerText,
        Func<string, string> lowerType)
    {
        public string LowerStructInitializers(string expression)
        {
            foreach (var structNode in context.GetStructs())
            {
                expression = replaceBracedExpressions(
                    expression,
                    structNode.Name,
                    initializerText => LowerStructInitializer(structNode, initializerText));
            }

            return expression;
        }

        public string LowerInterfaceInitializers(string expression)
        {
            foreach (var interfaceNode in context.GetInterfaces())
            {
                expression = replaceBracedExpressions(
                    expression,
                    interfaceNode.Name,
                    initializerText => LowerInterfaceInitializer(interfaceNode, initializerText));
            }

            return expression;
        }

        private string LowerStructInitializer(StructNode structNode, string initializerText)
        {
            var fields = SplitNamedInitializerFields(initializerText);
            if (fields.Count == 0)
            {
                return $"({lowerType(structNode.Name)}){{0}}";
            }

            var knownFields = structNode.Fields
                .Select(field => field.Name)
                .ToHashSet(StringComparer.Ordinal);

            var initializers = fields.Select(field =>
            {
                var prefix = knownFields.Contains(field.Name)
                    ? "." + field.Name
                    : "." + field.Name;
                return $"{prefix} = {lowerText(field.Value)}";
            });

            return $"({lowerType(structNode.Name)}){{ {string.Join(", ", initializers)} }}";
        }

        private string LowerInterfaceInitializer(InterfaceNode interfaceNode, string initializerText)
        {
            var fields = SplitNamedInitializerFields(initializerText);
            if (fields.Count == 0)
            {
                return $"({lowerType(interfaceNode.Name)}){{0}}";
            }

            var knownFields = interfaceNode.Methods
                .Select(method => method.Name)
                .Append("vtable")
                .Prepend("state")
                .ToHashSet(StringComparer.Ordinal);

            var initializers = fields.Select(field =>
            {
                var prefix = knownFields.Contains(field.Name)
                    ? "." + field.Name
                    : "." + field.Name;
                return $"{prefix} = {lowerText(field.Value)}";
            });

            return $"({lowerType(interfaceNode.Name)}){{ {string.Join(", ", initializers)} }}";
        }

        private IReadOnlyList<NamedInitializerField> SplitNamedInitializerFields(string initializerText)
        {
            var arguments = splitArguments(initializerText);
            var fields = new List<NamedInitializerField>();

            foreach (var argument in arguments)
            {
                var colon = FindTopLevelColon(argument);
                if (colon <= 0)
                {
                    continue;
                }

                var name = argument[..colon].Trim();
                var value = argument[(colon + 1)..].Trim();
                if (name.Length == 0 || value.Length == 0)
                {
                    continue;
                }

                fields.Add(new NamedInitializerField(name, value));
            }

            return fields;
        }

        private static int FindTopLevelColon(string text)
        {
            var depth = 0;
            for (var i = 0; i < text.Length; i++)
            {
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

        private sealed record NamedInitializerField(string Name, string Value);
    }
}
