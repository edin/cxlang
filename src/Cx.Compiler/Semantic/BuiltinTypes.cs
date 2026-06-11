namespace Cx.Compiler.Semantic;

internal static class BuiltinTypes
{
    private static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal)
    {
        "void",
        "bool",
        "char",
        "signed char",
        "unsigned char",
        "short",
        "unsigned short",
        "int",
        "unsigned int",
        "long",
        "unsigned long",
        "long long",
        "unsigned long long",
        "float",
        "double",
        "long double",
        "size_t",
        "usize",
        "u8",
        "u16",
        "u32",
        "u64",
        "i8",
        "i16",
        "i32",
        "i64",
        "int8_t",
        "uint8_t",
        "int16_t",
        "uint16_t",
        "int32_t",
        "uint32_t",
        "int64_t",
        "uint64_t",
        "clock_t",
        "FILE",
    };

    private static readonly IReadOnlySet<string> NumericTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "char",
        "signed char",
        "unsigned char",
        "short",
        "unsigned short",
        "int",
        "unsigned int",
        "long",
        "unsigned long",
        "long long",
        "unsigned long long",
        "float",
        "double",
        "long double",
        "size_t",
        "usize",
        "u8",
        "u16",
        "u32",
        "u64",
        "i8",
        "i16",
        "i32",
        "i64",
        "int8_t",
        "uint8_t",
        "int16_t",
        "uint16_t",
        "int32_t",
        "uint32_t",
        "int64_t",
        "uint64_t",
        "clock_t",
    };

    public static bool IsBuiltin(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && Types.Contains(Normalize(name));

    public static bool IsNumeric(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && NumericTypes.Contains(Normalize(name));

    public static string Normalize(string name)
    {
        name = name.Trim();
        while (name.EndsWith("*", StringComparison.Ordinal))
        {
            name = name[..^1].TrimEnd();
        }

        if (name.StartsWith("const ", StringComparison.Ordinal))
        {
            name = name["const ".Length..].TrimStart();
        }

        return name;
    }
}
