using System.Text;
using Cx.Compiler.Semantic;
using Cx.Compiler.Syntax;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Diagnostics;

internal sealed class CxAstDebugPrinter
{
    private readonly StringBuilder _builder = new();

    public string Print(ProgramNode program)
    {
        _builder.Clear();
        WriteLine(0, program.Module is null
            ? "Program"
            : $"Program module={program.Module.Name}");

        foreach (var typeAlias in program.TypeAliases)
        {
            PrintTypeAlias(typeAlias, 1);
        }

        foreach (var structNode in program.Structs)
        {
            PrintStruct(structNode, 1);
        }

        foreach (var function in program.Functions)
        {
            PrintFunction(function, 1);
        }

        foreach (var test in program.Tests)
        {
            PrintTest(test, 1);
        }

        return _builder.ToString();
    }

    private void PrintTypeAlias(TypeAliasNode alias, int indent)
    {
        WriteLine(indent, $"TypeAlias {alias.Name}");
        PrintTypeNode("Target", alias.TargetTypeNode, alias.TargetType, indent + 1);
        PrintSemantic(alias, indent + 1);
    }

    private void PrintStruct(StructNode structNode, int indent)
    {
        var typeParameters = structNode.TypeParameters.Count == 0
            ? string.Empty
            : $"<{string.Join(",", structNode.TypeParameters)}>";
        WriteLine(indent, $"Struct {structNode.Name}{typeParameters}");
        PrintSemantic(structNode, indent + 1);

        foreach (var requirement in structNode.Requirements)
        {
            WriteLine(indent + 1, $"Requirement {requirement.Name}{FormatTypeArgumentList(requirement.TypeArguments)}");
            PrintSemantic(requirement, indent + 2);
            for (var i = 0; i < requirement.TypeArgumentNodes.Count; i++)
            {
                PrintTypeNode($"Argument[{i}]", requirement.TypeArgumentNodes[i], requirement.TypeArguments.ElementAtOrDefault(i), indent + 2);
            }
        }

        foreach (var field in structNode.Fields)
        {
            WriteLine(indent + 1, $"Field {field.Name}: {field.Type}");
            PrintTypeNode("Type", field.TypeNode, field.Type, indent + 2);
            PrintSemantic(field, indent + 2);
        }

        foreach (var method in structNode.Methods)
        {
            PrintFunction(method, indent + 1);
        }
    }

    private void PrintFunction(FunctionNode function, int indent)
    {
        var owner = function.OwnerType is null ? string.Empty : function.OwnerType + ".";
        var typeParameters = function.TypeParameters.Count == 0
            ? string.Empty
            : $"<{string.Join(",", function.TypeParameters)}>";
        WriteLine(indent, $"{(function.IsStatic ? "Static " : string.Empty)}Function {owner}{function.Name}{typeParameters} -> {function.ReturnType}");
        PrintTypeNode("Return", function.ReturnTypeNode, function.ReturnType, indent + 1);
        PrintSemantic(function, indent + 1);

        foreach (var parameter in function.Parameters)
        {
            if (parameter.IsVariadic)
            {
                WriteLine(indent + 1, "Parameter ...");
                continue;
            }

            WriteLine(indent + 1, $"Parameter {parameter.Name}: {parameter.Type}");
            PrintTypeNode("Type", parameter.TypeNode, parameter.Type, indent + 2);
            PrintSemantic(parameter, indent + 2);
        }

        PrintStatements(function.Body, indent + 1);
    }

    private void PrintTest(TestNode test, int indent)
    {
        WriteLine(indent, $"Test \"{test.Name}\"");
        PrintSemantic(test, indent + 1);
        PrintStatements(test.Body, indent + 1);
    }

    private void PrintStatements(IEnumerable<StatementNode> statements, int indent)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case LetStatement let:
                    WriteLine(indent, $"Let {let.Name}: {let.Type}");
                    PrintTypeNode("Type", let.TypeNode, let.Type, indent + 1);
                    PrintSemantic(let, indent + 1);
                    if (let.Initializer is not null)
                    {
                        PrintExpression(let.Initializer, indent + 1);
                    }
                    break;
                case ReturnStatement ret:
                    WriteLine(indent, "Return");
                    PrintSemantic(ret, indent + 1);
                    PrintExpression(ret.Expression, indent + 1);
                    break;
                case CStatement c:
                    WriteLine(indent, "ExpressionStatement");
                    PrintSemantic(c, indent + 1);
                    PrintExpression(c.Expression, indent + 1);
                    break;
                case IfStatement ifStatement:
                    WriteLine(indent, "If");
                    PrintSemantic(ifStatement, indent + 1);
                    PrintExpression(ifStatement.Condition, indent + 1);
                    PrintStatements(ifStatement.ThenBody, indent + 1);
                    if (ifStatement.ElseBranch is not null)
                    {
                        PrintStatements([ifStatement.ElseBranch], indent + 1);
                    }
                    break;
                case WhileStatement whileStatement:
                    WriteLine(indent, "While");
                    PrintSemantic(whileStatement, indent + 1);
                    PrintExpression(whileStatement.Condition, indent + 1);
                    PrintStatements(whileStatement.Body, indent + 1);
                    break;
                case ForStatement forStatement:
                    WriteLine(indent, "For");
                    PrintSemantic(forStatement, indent + 1);
                    PrintForInitializer(forStatement.Initializer, indent + 1);
                    PrintExpression(forStatement.Condition, indent + 1);
                    PrintExpression(forStatement.Increment, indent + 1);
                    PrintStatements(forStatement.Body, indent + 1);
                    break;
                case ForeachStatement foreachStatement:
                    WriteLine(indent, "Foreach");
                    PrintSemantic(foreachStatement, indent + 1);
                    PrintForeachBinding("Index", foreachStatement.IndexBinding, indent + 1);
                    PrintForeachBinding("Key", foreachStatement.KeyBinding, indent + 1);
                    PrintForeachBinding("Value", foreachStatement.ValueBinding, indent + 1);
                    PrintExpression(foreachStatement.IterableExpression, indent + 1);
                    PrintStatements(foreachStatement.Body, indent + 1);
                    break;
                default:
                    WriteLine(indent, statement.GetType().Name);
                    PrintSemantic(statement, indent + 1);
                    break;
            }
        }
    }

    private void PrintForInitializer(ForInitializerNode initializer, int indent)
    {
        switch (initializer)
        {
            case ForDeclarationInitializerNode declaration:
                WriteLine(indent, $"ForLet {declaration.Name}: {declaration.Type}");
                PrintTypeNode("Type", declaration.TypeNode, declaration.Type, indent + 1);
                PrintSemantic(declaration, indent + 1);
                if (declaration.Initializer is not null)
                {
                    PrintExpression(declaration.Initializer, indent + 1);
                }
                break;
            case ForExpressionInitializerNode expression:
                WriteLine(indent, "ForExpression");
                PrintSemantic(expression, indent + 1);
                PrintExpression(expression.Expression, indent + 1);
                break;
        }
    }

    private void PrintForeachBinding(string label, ForeachBinding? binding, int indent)
    {
        if (binding is null)
        {
            return;
        }

        WriteLine(indent, $"{label}Binding {binding.Name}: {binding.Type}");
        PrintTypeNode("Type", binding.TypeNode, binding.Type, indent + 1);
        PrintSemantic(binding, indent + 1);
    }

    private void PrintExpression(ExpressionNode expression, int indent)
    {
        WriteLine(indent, $"{expression.GetType().Name} {Quote(expression.SourceText)}");
        PrintSemantic(expression, indent + 1);

        switch (expression)
        {
            case CastExpressionNode cast:
                PrintTypeNode("Target", cast.TargetTypeNode, cast.TargetType, indent + 1);
                PrintExpression(cast.Expression, indent + 1);
                break;
            case SizeOfExpressionNode sizeOf:
                if (sizeOf.TypeOperand is not null)
                {
                    PrintTypeNode("TypeOperand", sizeOf.TypeOperandNode, sizeOf.TypeOperand, indent + 1);
                }
                if (sizeOf.ExpressionOperand is not null)
                {
                    PrintExpression(sizeOf.ExpressionOperand, indent + 1);
                }
                break;
            case InitializerExpressionNode initializer:
                if (initializer.TypeName is not null)
                {
                    PrintTypeNode("TypeName", initializer.TypeNameNode, initializer.TypeName, indent + 1);
                }
                foreach (var field in initializer.Fields)
                {
                    WriteLine(indent + 1, $"InitializerField {field.Name}");
                    PrintExpression(field.Value, indent + 2);
                }
                foreach (var value in initializer.Values)
                {
                    PrintExpression(value, indent + 1);
                }
                break;
            case FunctionExpressionNode function:
                PrintTypeNode("Return", function.ReturnTypeNode, function.ReturnType, indent + 1);
                foreach (var parameter in function.Parameters.Where(parameter => !parameter.IsVariadic))
                {
                    WriteLine(indent + 1, $"Parameter {parameter.Name}: {parameter.Type}");
                    PrintTypeNode("Type", parameter.TypeNode, parameter.Type, indent + 2);
                    PrintSemantic(parameter, indent + 2);
                }
                if (function.ExpressionBody is not null)
                {
                    PrintExpression(function.ExpressionBody, indent + 1);
                }
                if (function.BlockBody is not null)
                {
                    PrintStatements(function.BlockBody, indent + 1);
                }
                break;
            case GenericCallExpressionNode genericCall:
                PrintExpression(genericCall.Callee, indent + 1);
                for (var i = 0; i < genericCall.TypeArgumentNodes.Count; i++)
                {
                    PrintTypeNode($"TypeArgument[{i}]", genericCall.TypeArgumentNodes[i], genericCall.TypeArguments.ElementAtOrDefault(i), indent + 1);
                }
                foreach (var argument in genericCall.Arguments)
                {
                    PrintExpression(argument, indent + 1);
                }
                break;
            case CallExpressionNode call:
                PrintExpression(call.Callee, indent + 1);
                foreach (var argument in call.Arguments)
                {
                    PrintExpression(argument, indent + 1);
                }
                break;
        }
    }

    private void PrintTypeNode(string label, TypeNode? typeNode, string? fallbackType, int indent)
    {
        if (typeNode is null)
        {
            if (!string.IsNullOrWhiteSpace(fallbackType))
            {
                WriteLine(indent, $"{label} TypeNode=<missing> text={fallbackType}");
            }
            return;
        }

        WriteLine(indent, $"{label} TypeNode={typeNode.TypeName}");
        PrintSemantic(typeNode, indent + 1);
    }

    private void PrintSemantic(SyntaxNode node, int indent)
    {
        if (node.Semantic.Type is { } type)
        {
            WriteLine(indent, $"Semantic.Type={FormatType(type)}");
        }

        if (node.Semantic.Symbol is { } symbol)
        {
            WriteLine(indent, $"Semantic.Symbol={symbol.Kind}:{symbol.Name}");
        }

        if (node.Semantic.ResolvedCall is { } call)
        {
            WriteLine(indent, $"Semantic.ResolvedCall={call.Function.Name}{FormatTypeArgumentList(call.TypeArguments)}");
        }
    }

    private static string FormatType(TypeRef type) =>
        type switch
        {
            TypeRef.Alias alias => $"Alias({alias.Name} -> {FormatType(alias.Target)})",
            TypeRef.Named named when named.Arguments.Count == 0 => named.Name,
            TypeRef.Named named => $"{named.Name}<{string.Join(",", named.Arguments.Select(FormatType))}>",
            TypeRef.Pointer pointer => FormatType(pointer.Element) + "*",
            TypeRef.FixedArray array => $"{FormatType(array.Element)}[{array.Length}]",
            TypeRef.Function function => $"fn({string.Join(",", function.Parameters.Select(FormatType))})->{FormatType(function.ReturnType)}",
            _ => TypeRefFormatter.ToCxString(type),
        };

    private static string FormatTypeArgumentList(IReadOnlyList<string> arguments) =>
        arguments.Count == 0 ? string.Empty : $"<{string.Join(",", arguments)}>";

    private void WriteLine(int indent, string text)
    {
        _builder.Append(' ', indent * 2);
        _builder.AppendLine(text);
    }

    private static string Quote(string text) =>
        "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
