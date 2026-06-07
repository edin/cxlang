using Cx.Compiler.C;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler;

public sealed partial class CEmitter
{
    private sealed class StructValueBuilder(
        CLoweringContext context,
        Func<ExpressionNode, CExpression> lowerExpression,
        Func<string, string> lowerText,
        Func<string, string?> inferExpressionType,
        Func<string, string> lowerCxType)
    {
        public CExpression BuildPayloadExpression(
            string payloadType,
            IReadOnlyList<ExpressionNode> arguments,
            Func<string, IReadOnlyList<string>, string> buildPayloadText)
        {
            var normalizedPayloadType = NormalizeType(payloadType);
            if (context.TryGetStruct(normalizedPayloadType, out var structNode))
            {
                if (arguments.Count == 1
                    && inferExpressionType(arguments[0].SourceText) == normalizedPayloadType)
                {
                    return lowerExpression(arguments[0]);
                }

                if (TryBuildStructConstructorExpression(structNode, arguments, out var initializer))
                {
                    return initializer;
                }
            }

            return arguments.Count == 1
                ? lowerExpression(arguments[0])
                : new CRawExpression(buildPayloadText(payloadType, arguments.Select(argument => argument.SourceText).ToList()));
        }

        public CExpression BuildStructConstructorExpression(
            StructNode structNode,
            IReadOnlyList<ExpressionNode> arguments,
            Func<StructNode, IReadOnlyList<string>, string> buildStructConstructorText)
        {
            return TryBuildStructConstructorExpression(structNode, arguments, out var initializer)
                ? initializer
                : new CRawExpression(buildStructConstructorText(structNode, arguments.Select(argument => argument.SourceText).ToList()));
        }

        public string BuildPayloadText(
            string payloadType,
            IReadOnlyList<string> arguments,
            Func<StructNode, IReadOnlyList<string>, string> buildStructConstructorText)
        {
            var normalizedPayloadType = NormalizeType(payloadType);
            if (context.TryGetStruct(normalizedPayloadType, out var structNode))
            {
                if (arguments.Count == 1 && inferExpressionType(arguments[0]) == normalizedPayloadType)
                {
                    return lowerText(arguments[0]);
                }

                return buildStructConstructorText(structNode, arguments);
            }

            return arguments.Count == 1 ? lowerText(arguments[0]) : string.Join(", ", arguments.Select(lowerText));
        }

        public string BuildStructConstructorText(StructNode structNode, IReadOnlyList<string> arguments)
        {
            if (arguments.Count != structNode.Fields.Count)
            {
                return $"{structNode.Name}({string.Join(", ", arguments)})";
            }

            var fields = structNode.Fields
                .Zip(arguments, (field, argument) => $".{field.Name} = {lowerText(argument)}");
            return $"({structNode.Name}){{ {string.Join(", ", fields)} }}";
        }

        private bool TryBuildStructConstructorExpression(
            StructNode structNode,
            IReadOnlyList<ExpressionNode> arguments,
            out CExpression initializer)
        {
            if (arguments.Count != structNode.Fields.Count)
            {
                initializer = null!;
                return false;
            }

            initializer = new CInitializerExpression(
                lowerCxType(structNode.Name),
                structNode.Fields
                    .Zip(arguments, (field, argument) => new CInitializerField(field.Name, lowerExpression(argument)))
                    .ToList(),
                []);
            return true;
        }
    }
}
