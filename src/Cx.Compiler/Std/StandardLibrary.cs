using System.Reflection;
using Cx.Compiler.Syntax;

namespace Cx.Compiler.Std;

internal static class StandardLibrary
{
    private const string ResourcePrefix = "Cx.Compiler.Std.";

    public static IReadOnlyList<SourceFile> LoadCoreFiles()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly
            .GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                && name.EndsWith(".cx", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        if (resourceNames.Count == 0)
        {
            throw new InvalidOperationException($"No embedded std resources were found with prefix '{ResourcePrefix}'.");
        }

        return resourceNames
            .Select(name => new SourceFile(GetPath(name), ReadResource(assembly, name)))
            .ToList();
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string GetPath(string resourceName) =>
        "std/" + resourceName[ResourcePrefix.Length..].Replace('.', '/');
}
