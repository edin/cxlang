using System.Text;
using Cx.Compiler.Syntax.Nodes;

namespace Cx.Compiler.Derive;

internal static class DebugDeriveGenerator
{
    public static string Generate(ProgramNode program)
    {
        var builder = new StringBuilder();
        var debugStructNames = program.Structs
            .Where(HasDebugDerive)
            .Where(structNode => structNode.TypeParameters.Count == 0)
            .Select(structNode => structNode.Name)
            .ToHashSet(StringComparer.Ordinal);

        var typeAliases = program.TypeAliases
            .GroupBy(typeAlias => typeAlias.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().TargetType, StringComparer.Ordinal);

        foreach (var structNode in program.Structs.Where(HasDebugDerive).Where(structNode => structNode.TypeParameters.Count == 0))
        {
            GenerateStructDebug(builder, structNode, debugStructNames, typeAliases);
            builder.AppendLine();
        }

        if (builder.Length == 0)
        {
            return string.Empty;
        }

        return "include <stdio.h>;\n\n" + builder;
    }

    private static void GenerateStructDebug(
        StringBuilder builder,
        StructNode structNode,
        IReadOnlySet<string> debugStructNames,
        IReadOnlyDictionary<string, string> typeAliases)
    {
        builder.AppendLine($"fn {structNode.Name}.debug(self: {structNode.Name}*) -> void {{");
        builder.AppendLine($"    printf(\"{EscapeCString(structNode.Name)} {{ \");");

        var printedFieldCount = 0;
        foreach (var field in structNode.Fields)
        {
            if (field.Attributes.Any(attribute => attribute.Name == "debug_skip"))
            {
                continue;
            }

            if (printedFieldCount > 0)
            {
                builder.AppendLine("    printf(\", \");");
            }

            builder.AppendLine($"    printf(\"{EscapeCString(field.Name)}: \");");
            AppendFieldPrinter(builder, structNode, field, debugStructNames, typeAliases);
            printedFieldCount++;
        }

        builder.AppendLine("    printf(\" }\");");
        builder.AppendLine("}");
    }

    private static void AppendFieldPrinter(
        StringBuilder builder,
        StructNode owner,
        StructFieldNode field,
        IReadOnlySet<string> debugStructNames,
        IReadOnlyDictionary<string, string> typeAliases)
    {
        var type = field.Type.Trim();
        var expression = $"self.{field.Name}";
        var resolvedType = ResolveAlias(type, typeAliases);

        if (resolvedType.EndsWith("*", StringComparison.Ordinal))
        {
            AppendPointerPrinter(builder, resolvedType, expression);
            return;
        }

        if (IsCharPointer(resolvedType))
        {
            AppendStringPrinter(builder, expression);
            return;
        }

        if (resolvedType == "bool")
        {
            builder.AppendLine($"    printf(\"%s\", {expression} ? \"true\" : \"false\");");
            return;
        }

        if (TryGetPrintfFormat(resolvedType, out var format, out var castType))
        {
            var value = castType is null ? expression : $"({castType}){expression}";
            builder.AppendLine($"    printf(\"{format}\", {value});");
            return;
        }

        if (debugStructNames.Contains(resolvedType) && resolvedType != owner.Name)
        {
            builder.AppendLine($"    {resolvedType}.debug(&{expression});");
            return;
        }

        builder.AppendLine($"    printf(\"<{EscapeCString(type)}>\");");
    }

    private static void AppendPointerPrinter(StringBuilder builder, string type, string expression)
    {
        if (IsCharPointer(type))
        {
            AppendStringPrinter(builder, expression);
            return;
        }

        var pointedType = type.TrimEnd('*').TrimEnd();
        builder.AppendLine($"    if ({expression} == null) {{");
        builder.AppendLine("        printf(\"null\");");
        builder.AppendLine("    }");
        builder.AppendLine("    else {");
        builder.AppendLine($"        printf(\"{EscapeCString(pointedType)}*(%p)\", (void*){expression});");
        builder.AppendLine("    }");
    }

    private static void AppendStringPrinter(StringBuilder builder, string expression)
    {
        builder.AppendLine($"    if ({expression} == null) {{");
        builder.AppendLine("        printf(\"null\");");
        builder.AppendLine("    }");
        builder.AppendLine("    else {");
        builder.AppendLine($"        printf(\"\\\"%s\\\"\", {expression});");
        builder.AppendLine("    }");
    }

    private static bool HasDebugDerive(StructNode structNode) =>
        structNode.Attributes.Any(attribute =>
            attribute.Name == "derive"
            && attribute.Arguments.Any(argument => NormalizeDeriveName(argument.Value) == "Debug"));

    private static string NormalizeDeriveName(string value) =>
        value.Trim().Trim('"');

    private static bool IsCharPointer(string type) =>
        type is "char*" or "const char*";

    private static bool TryGetPrintfFormat(string type, out string format, out string? castType)
    {
        castType = null;
        switch (type)
        {
            case "char":
                format = "%c";
                return true;
            case "float":
            case "double":
                format = "%f";
                return true;
            case "int":
            case "short":
            case "i8":
            case "i16":
            case "i32":
                format = "%d";
                return true;
            case "long":
            case "i64":
                format = "%lld";
                castType = "long long";
                return true;
            case "unsigned char":
            case "u8":
            case "uint8_t":
                format = "%u";
                castType = "unsigned int";
                return true;
            case "unsigned short":
            case "u16":
            case "uint16_t":
                format = "%u";
                castType = "unsigned int";
                return true;
            case "unsigned int":
            case "u32":
            case "uint32_t":
                format = "%u";
                return true;
            case "unsigned long":
            case "unsigned long long":
            case "u64":
            case "usize":
            case "uint64_t":
                format = "%llu";
                castType = "unsigned long long";
                return true;
            default:
                format = string.Empty;
                return false;
        }
    }

    private static string ResolveAlias(string type, IReadOnlyDictionary<string, string> typeAliases)
    {
        var pointerSuffix = "";
        while (type.EndsWith("*", StringComparison.Ordinal))
        {
            pointerSuffix += "*";
            type = type[..^1].TrimEnd();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (typeAliases.TryGetValue(type, out var target) && seen.Add(type))
        {
            type = target;
        }

        return type + pointerSuffix;
    }

    private static string EscapeCString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}
