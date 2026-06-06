namespace Cx.Compiler.Semantic;

internal static class TypeRefFormatter
{
    public static string ToCxString(TypeRef type) =>
        type switch
        {
            TypeRef.Unknown => "unknown",
            TypeRef.Null => "null",
            TypeRef.Alias alias => alias.Name,
            TypeRef.Named named when named.Arguments.Count == 0 => named.Name,
            TypeRef.Named named => $"{named.Name}<{string.Join(",", named.Arguments.Select(ToCxString))}>",
            TypeRef.Pointer pointer => ToCxString(pointer.Element) + "*",
            TypeRef.FixedArray array => $"{ToCxString(array.Element)}[{array.Length}]",
            TypeRef.Function function => $"fn({string.Join(",", function.Parameters.Select(ToCxString))})->{ToCxString(function.ReturnType)}",
            _ => type.ToString() ?? "unknown",
        };
}
