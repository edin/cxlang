using System.ComponentModel;
using System.Diagnostics;
using Cx.Compiler;
using Cx.Compiler.Diagnostics;
using Cx.Compiler.Syntax;
using Spectre.Console;
using Spectre.Console.Cli;
using Tomlyn;
using Tomlyn.Model;

var app = new CommandApp<TranspileCommand>();
app.Configure(config =>
{
    config.SetApplicationName("cx");
    config.SetApplicationVersion("0.1.0");

    config.AddCommand<TranspileCommand>("transpile")
        .WithDescription("Transpile a CX source file, directory, or configured project to C.");

    config.AddCommand<BuildCommand>("build")
        .WithDescription("Transpile and compile a configured project or input to a native executable.");

    config.AddCommand<RunCommand>("run")
        .WithDescription("Transpile, compile with a C compiler, and run the program.");

    config.AddCommand<CheckCommand>("check")
        .WithDescription("Parse and analyze a project or input without writing generated C.");

    config.AddCommand<TestCommand>("test")
        .WithDescription("Collect CX test blocks, compile a test runner, and run it.");

    config.AddCommand<InitCommand>("init")
        .WithDescription("Create a cx.toml project in the current directory.");

    config.AddCommand<NewCommand>("new")
        .WithDescription("Create a new CX project directory.");

    config.SetExceptionHandler((exception, _) =>
    {
        AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {exception.Message}");
        return 1;
    });
});

return app.Run(RewriteRunProgramArgs(args));

static string[] RewriteRunProgramArgs(string[] args)
{
    if (args.Length == 0 || args[0] != "run")
    {
        return args;
    }

    var separator = Array.IndexOf(args, "--");
    if (separator < 0)
    {
        return args;
    }

    var rewritten = args[..separator].ToList();
    foreach (var programArg in args[(separator + 1)..])
    {
        rewritten.Add("--program-arg");
        rewritten.Add(programArg);
    }

    return rewritten.ToArray();
}

internal sealed class TranspileCommand : Command<TranspileCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[input]")]
        [Description("Input .cx/.cplus file or directory. If omitted, cx.toml is used.")]
        public string? InputPath { get; init; }

        [CommandOption("-o|--output <path>")]
        [Description("Output C file path.")]
        public string? OutputPath { get; init; }

        [CommandOption("--config <path>")]
        [Description("Project config path. Defaults to cx.toml in the current directory.")]
        public string? ConfigPath { get; init; }

    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var plan = CliServices.ResolveBuildPlan(new BuildPlanRequest(
            settings.InputPath,
            settings.ConfigPath,
            settings.OutputPath,
            NativeOutputPath: null,
            Compiler: null,
            CompilerArgs: []));
        if (!plan.Success)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {plan.Error}");
            return 2;
        }

        var result = CliServices.Compile(plan.Value.SourceFiles);
        if (!result.Success)
        {
            CliServices.PrintDiagnostics(result);
            return 1;
        }

        CliServices.PrintDiagnostics(result);
        CliServices.EnsureParentDirectory(plan.Value.COutputPath);
        File.WriteAllText(plan.Value.COutputPath, result.Output);
        AnsiConsole.MarkupLineInterpolated($"[green]wrote[/] {plan.Value.COutputPath}");
        return 0;
    }
}

internal sealed class BuildCommand : Command<BuildCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[input]")]
        [Description("Input .cx/.cplus file or directory. If omitted, cx.toml is used.")]
        public string? InputPath { get; init; }

        [CommandOption("-o|--output <path>")]
        [Description("Executable output path.")]
        public string? OutputPath { get; init; }

        [CommandOption("--c-output <path>")]
        [Description("Generated C output path.")]
        public string? COutputPath { get; init; }

        [CommandOption("--cc <compiler>")]
        [Description("C compiler executable.")]
        public string? Compiler { get; init; }

        [CommandOption("--cc-arg <arg>")]
        [Description("Additional argument passed to the C compiler. Can be repeated.")]
        public string[] CompilerArgs { get; init; } = [];

        [CommandOption("--config <path>")]
        [Description("Project config path. Defaults to cx.toml in the current directory.")]
        public string? ConfigPath { get; init; }

    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var plan = CliServices.ResolveBuildPlan(new BuildPlanRequest(
            settings.InputPath,
            settings.ConfigPath,
            settings.COutputPath,
            settings.OutputPath,
            settings.Compiler,
            settings.CompilerArgs));
        if (!plan.Success)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {plan.Error}");
            return 2;
        }

        return CliServices.BuildNative(plan.Value, runAfterBuild: false, programArgs: []);
    }
}

internal sealed class RunCommand : Command<RunCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[input]")]
        [Description("Input .cx/.cplus file or directory. If omitted, cx.toml is used.")]
        public string? InputPath { get; init; }

        [CommandOption("--cc <compiler>")]
        [Description("C compiler executable.")]
        public string? Compiler { get; init; }

        [CommandOption("--cc-arg <arg>")]
        [Description("Additional argument passed to the C compiler. Can be repeated.")]
        public string[] CompilerArgs { get; init; } = [];

        [CommandOption("--program-arg <arg>")]
        [Description("Argument passed to the compiled program. Usually provided after '--'.")]
        public string[] ProgramArgs { get; init; } = [];

        [CommandOption("-o|--output <path>")]
        [Description("Executable output path.")]
        public string? OutputPath { get; init; }

        [CommandOption("--c-output <path>")]
        [Description("Generated C output path.")]
        public string? COutputPath { get; init; }

        [CommandOption("--config <path>")]
        [Description("Project config path. Defaults to cx.toml in the current directory.")]
        public string? ConfigPath { get; init; }

    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var plan = CliServices.ResolveBuildPlan(new BuildPlanRequest(
            settings.InputPath,
            settings.ConfigPath,
            settings.COutputPath,
            settings.OutputPath,
            settings.Compiler,
            settings.CompilerArgs));
        if (!plan.Success)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {plan.Error}");
            return 2;
        }

        return CliServices.BuildNative(plan.Value, runAfterBuild: true, settings.ProgramArgs);
    }
}

internal sealed class CheckCommand : Command<CheckCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[input]")]
        [Description("Input .cx/.cplus file or directory. If omitted, cx.toml is used.")]
        public string? InputPath { get; init; }

        [CommandOption("--config <path>")]
        [Description("Project config path. Defaults to cx.toml in the current directory.")]
        public string? ConfigPath { get; init; }

        [CommandOption("--ast-audit")]
        [Description("Fail if the parser falls back to raw expression nodes.")]
        public bool AstAudit { get; init; }

        [CommandOption("--c-raw-audit")]
        [Description("Report raw C escapes remaining in the lowered C AST.")]
        public bool CRawAudit { get; init; }

        [CommandOption("--include-std")]
        [Description("Include embedded standard library files in --ast-audit.")]
        public bool IncludeStandardLibrary { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var plan = CliServices.ResolveBuildPlan(new BuildPlanRequest(
            settings.InputPath,
            settings.ConfigPath,
            COutputPath: null,
            NativeOutputPath: null,
            Compiler: null,
            CompilerArgs: []));
        if (!plan.Success)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {Markup.Escape(plan.Error)}");
            return 2;
        }

        var result = settings.CRawAudit
            ? CliServices.AuditRawC(plan.Value.SourceFiles)
            : settings.AstAudit
                ? CliServices.AuditAst(plan.Value.SourceFiles, settings.IncludeStandardLibrary)
                : CliServices.Compile(plan.Value.SourceFiles);
        if (!result.Success)
        {
            CliServices.PrintDiagnostics(result);
            return 1;
        }

        CliServices.PrintDiagnostics(result);
        if (settings.CRawAudit)
        {
            AnsiConsole.WriteLine(result.Output ?? string.Empty);
            return 0;
        }

        var verb = settings.AstAudit ? "audited AST for" : "checked";
        AnsiConsole.MarkupLineInterpolated($"[green]{verb}[/] {Markup.Escape(plan.Value.Name)} ({plan.Value.SourceFiles.Count} source file(s))");
        return 0;
    }
}

internal sealed class TestCommand : Command<TestCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[input]")]
        [Description("Input .cx/.cplus file or directory. If omitted, cx.toml sources plus tests/ are used.")]
        public string? InputPath { get; init; }

        [CommandOption("--cc <compiler>")]
        [Description("C compiler executable.")]
        public string? Compiler { get; init; }

        [CommandOption("--cc-arg <arg>")]
        [Description("Additional argument passed to the C compiler. Can be repeated.")]
        public string[] CompilerArgs { get; init; } = [];

        [CommandOption("-o|--output <path>")]
        [Description("Test executable output path.")]
        public string? OutputPath { get; init; }

        [CommandOption("--c-output <path>")]
        [Description("Generated test C output path.")]
        public string? COutputPath { get; init; }

        [CommandOption("--config <path>")]
        [Description("Project config path. Defaults to cx.toml in the current directory.")]
        public string? ConfigPath { get; init; }

        [CommandOption("--module <name>")]
        [Description("Run tests from one module. Use this to opt into std modules, for example --module std.core.")]
        public string? ModuleName { get; init; }

        [CommandOption("--std")]
        [Description("Run embedded std.core tests without requiring an input file or project.")]
        public bool StandardLibrary { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var plan = settings.StandardLibrary
            ? CliServices.ResolveStandardTestPlan(new StandardTestPlanRequest(
                settings.COutputPath,
                settings.OutputPath,
                settings.Compiler,
                settings.CompilerArgs))
            : CliServices.ResolveTestPlan(new BuildPlanRequest(
                settings.InputPath,
                settings.ConfigPath,
                settings.COutputPath,
                settings.OutputPath,
                settings.Compiler,
                settings.CompilerArgs));
        if (!plan.Success)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {plan.Error}");
            return 2;
        }

        var testModuleName = settings.StandardLibrary
            ? settings.ModuleName ?? "std.core"
            : settings.ModuleName;

        var testPlan = plan.Value with
        {
            COutputPath = settings.COutputPath is null
                ? Path.Combine(Path.GetDirectoryName(plan.Value.COutputPath) ?? string.Empty, plan.Value.Name + ".tests.c")
                : plan.Value.COutputPath,
            NativeOutputPath = settings.OutputPath is null
                ? Path.Combine(
                    Path.GetDirectoryName(plan.Value.NativeOutputPath) ?? string.Empty,
                    plan.Value.Name + ".tests" + (OperatingSystem.IsWindows() ? ".exe" : ""))
                : plan.Value.NativeOutputPath,
        };

        return CliServices.BuildNative(
            testPlan,
            runAfterBuild: true,
            programArgs: [],
            buildTests: true,
            testModuleName: testModuleName);
    }
}

internal sealed class InitCommand : Command<InitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-n|--name <name>")]
        [Description("Project name. Defaults to the current directory name.")]
        public string? Name { get; init; }

        [CommandOption("-f|--force")]
        [Description("Overwrite cx.toml if it already exists.")]
        public bool Force { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var directory = Environment.CurrentDirectory;
        var name = settings.Name ?? new DirectoryInfo(directory).Name;
        return CliServices.CreateProjectScaffold(directory, name, settings.Force);
    }
}

internal sealed class NewCommand : Command<NewCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Project directory/name.")]
        public string Name { get; init; } = string.Empty;

        [CommandOption("-f|--force")]
        [Description("Overwrite cx.toml if it already exists.")]
        public bool Force { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Name))
        {
            AnsiConsole.MarkupLine("[red]error:[/] Project name is required.");
            return 2;
        }

        var directory = Path.GetFullPath(settings.Name, Environment.CurrentDirectory);
        return CliServices.CreateProjectScaffold(directory, Path.GetFileName(directory), settings.Force);
    }
}

internal static class CliServices
{
    public static ResolvedBuildPlanResult ResolveStandardTestPlan(StandardTestPlanRequest request)
    {
        var baseDirectory = Environment.CurrentDirectory;
        var cOutputPath = ResolveOutputPath(
            request.COutputPath,
            configValue: null,
            Path.Combine("build", "c", "std.tests.c"),
            baseDirectory);
        var nativeOutputPath = ResolveOutputPath(
            request.NativeOutputPath,
            configValue: null,
            Path.Combine("build", "bin", "std.tests" + (OperatingSystem.IsWindows() ? ".exe" : "")),
            baseDirectory);
        var compiler = request.Compiler ?? "gcc";

        return ResolvedBuildPlanResult.Succeeded(new ResolvedBuildPlan(
            "std",
            SourceFiles: [],
            cOutputPath,
            nativeOutputPath,
            compiler,
            request.CompilerArgs,
            EnvPath: []));
    }

    public static ResolvedBuildPlanResult ResolveTestPlan(BuildPlanRequest request)
    {
        var config = ProjectConfig.Load(
            request.ConfigPath,
            useDefaultConfig: string.IsNullOrWhiteSpace(request.InputPath));
        if (config is { Success: false })
        {
            return ResolvedBuildPlanResult.Failed(config.Error);
        }

        var project = config.Value;
        var baseDirectory = project?.BaseDirectory ?? Environment.CurrentDirectory;
        var sourceEntries = !string.IsNullOrWhiteSpace(request.InputPath)
            ? [request.InputPath]
            : GetTestSourceEntries(project, baseDirectory);
        if (sourceEntries.Count == 0)
        {
            return ResolvedBuildPlanResult.Failed("Provide an input path, create cx.toml with sources, or add tests under a tests directory.");
        }

        var sources = ResolveSourceFiles(sourceEntries, baseDirectory);
        if (sources.Count == 0)
        {
            return ResolvedBuildPlanResult.Failed("No .cx or .cplus source files were found.");
        }

        var name = project?.Name
            ?? Path.GetFileNameWithoutExtension(sources[0].Path)
            ?? "tests";
        var cOutputPath = ResolveOutputPath(
            request.COutputPath,
            configValue: null,
            Path.Combine("build", "c", name + ".tests.c"),
            baseDirectory);
        var nativeOutputPath = ResolveOutputPath(
            request.NativeOutputPath,
            configValue: null,
            Path.Combine("build", "bin", name + ".tests" + (OperatingSystem.IsWindows() ? ".exe" : "")),
            baseDirectory);
        var compiler = request.Compiler ?? project?.Compiler ?? "gcc";
        var compilerArgs = new List<string>();
        compilerArgs.AddRange(project?.CompilerArgs ?? []);
        compilerArgs.AddRange(request.CompilerArgs);
        var envPath = project?.EnvPath ?? [];

        return ResolvedBuildPlanResult.Succeeded(new ResolvedBuildPlan(
            name,
            sources,
            cOutputPath,
            nativeOutputPath,
            compiler,
            compilerArgs,
            envPath));
    }

    public static ResolvedBuildPlanResult ResolveBuildPlan(BuildPlanRequest request)
    {
        var config = ProjectConfig.Load(
            request.ConfigPath,
            useDefaultConfig: string.IsNullOrWhiteSpace(request.InputPath));
        if (config is { Success: false })
        {
            return ResolvedBuildPlanResult.Failed(config.Error);
        }

        var project = config.Value;
        var baseDirectory = project?.BaseDirectory ?? Environment.CurrentDirectory;
        var sourceEntries = !string.IsNullOrWhiteSpace(request.InputPath)
            ? [request.InputPath]
            : project?.Sources ?? [];
        if (sourceEntries.Count == 0)
        {
            return ResolvedBuildPlanResult.Failed("Provide an input path or create cx.toml with a sources array.");
        }

        var sources = ResolveSourceFiles(sourceEntries, baseDirectory);
        if (sources.Count == 0)
        {
            return ResolvedBuildPlanResult.Failed("No .cx or .cplus source files were found.");
        }

        var name = project?.Name
            ?? Path.GetFileNameWithoutExtension(sources[0].Path)
            ?? "program";
        var defaultCOutput = Path.Combine("build", "c", name + ".c");
        var cOutputPath = ResolveOutputPath(
            request.COutputPath,
            project?.COutput,
            defaultCOutput,
            baseDirectory);
        var nativeOutputPath = ResolveOutputPath(
            request.NativeOutputPath,
            project?.Output,
            Path.Combine("build", "bin", name + (OperatingSystem.IsWindows() ? ".exe" : "")),
            baseDirectory);
        var compiler = request.Compiler ?? project?.Compiler ?? "gcc";
        var compilerArgs = new List<string>();
        compilerArgs.AddRange(project?.CompilerArgs ?? []);
        compilerArgs.AddRange(request.CompilerArgs);
        var envPath = project?.EnvPath ?? [];

        return ResolvedBuildPlanResult.Succeeded(new ResolvedBuildPlan(
            name,
            sources,
            cOutputPath,
            nativeOutputPath,
            compiler,
            compilerArgs,
            envPath));
    }

    public static CompilationResult Compile(IReadOnlyList<SourceFile> sourceFiles) =>
        new CxCompiler().CompileToC(sourceFiles);

    public static CompilationResult CompileTests(IReadOnlyList<SourceFile> sourceFiles, string? moduleName = null) =>
        new CxCompiler().CompileTestsToC(sourceFiles, moduleName);

    public static CompilationResult AuditAst(IReadOnlyList<SourceFile> sourceFiles, bool includeStandardLibrary) =>
        new CxCompiler().AuditAstCompleteness(sourceFiles, includeStandardLibrary);

    public static CompilationResult AuditRawC(IReadOnlyList<SourceFile> sourceFiles) =>
        new CxCompiler().AuditRawC(sourceFiles);

    public static int BuildNative(
        ResolvedBuildPlan plan,
        bool runAfterBuild,
        IReadOnlyList<string> programArgs,
        bool buildTests = false,
        string? testModuleName = null)
    {
        var result = buildTests ? CompileTests(plan.SourceFiles, testModuleName) : Compile(plan.SourceFiles);
        if (!result.Success)
        {
            PrintDiagnostics(result);
            return 1;
        }

        PrintDiagnostics(result);
        EnsureParentDirectory(plan.COutputPath);
        EnsureParentDirectory(plan.NativeOutputPath);
        File.WriteAllText(plan.COutputPath, result.Output);

        var compileArgs = new List<string> { plan.COutputPath, "-o", plan.NativeOutputPath };
        compileArgs.AddRange(plan.CompilerArgs);
        compileArgs.AddRange(result.LinkerArguments);
        var compileExitCode = RunProcess(plan.Compiler, compileArgs, plan.EnvPath);
        if (compileExitCode != 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{plan.Compiler} failed[/] with exit code {compileExitCode}");
            AnsiConsole.MarkupLineInterpolated($"generated C kept at {plan.COutputPath}");
            return compileExitCode;
        }

        AnsiConsole.MarkupLineInterpolated($"[green]built[/] {plan.NativeOutputPath}");
        return runAfterBuild
            ? RunProcess(plan.NativeOutputPath, programArgs, plan.EnvPath)
            : 0;
    }

    public static int RunProcess(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyList<string>? prependPath = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (prependPath is { Count: > 0 })
        {
            var path = startInfo.Environment.TryGetValue("PATH", out var currentPath)
                ? currentPath
                : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, prependPath) + Path.PathSeparator + path;
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]failed to start[/] '{fileName}'");
                return 1;
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]failed to start[/] '{fileName}': {ex.Message}");
            return 1;
        }
    }

    public static void PrintDiagnostics(CompilationResult result)
    {
        foreach (var diagnostic in result.Diagnostics)
        {
            var color = diagnostic.Severity == DiagnosticSeverity.Warning ? "yellow" : "red";
            AnsiConsole.MarkupLineInterpolated($"[{color}]{Markup.Escape(diagnostic.ToString())}[/]");
        }
    }

    public static int CreateProjectScaffold(string directory, string name, bool force)
    {
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        Directory.CreateDirectory(Path.Combine(directory, "tests"));
        Directory.CreateDirectory(Path.Combine(directory, "build", "c"));
        Directory.CreateDirectory(Path.Combine(directory, "build", "bin"));

        var configPath = Path.Combine(directory, "cx.toml");
        if (File.Exists(configPath) && !force)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {Markup.Escape(configPath)} already exists. Use --force to overwrite it.");
            return 2;
        }

        File.WriteAllText(configPath, CreateProjectConfig(name));

        var mainPath = Path.Combine(directory, "src", "main.cx");
        if (!File.Exists(mainPath))
        {
            File.WriteAllText(mainPath, CreateMainSource());
        }

        AnsiConsole.MarkupLineInterpolated($"[green]created[/] {Markup.Escape(configPath)}");
        AnsiConsole.MarkupLineInterpolated($"[green]created[/] {Markup.Escape(mainPath)}");
        AnsiConsole.MarkupLine("outputs: build/c/<name>.c and build/bin/<name>.exe");
        return 0;
    }

    private static string CreateProjectConfig(string name)
    {
        var executable = name + (OperatingSystem.IsWindows() ? ".exe" : "");
        return $$"""
        name = "{{name}}"
        kind = "exe"
        sources = ["src"]

        c_output = "build/c/{{name}}.c"
        output = "build/bin/{{executable}}"

        cc = "gcc"
        cc_args = ["-O2"]
        """;
    }

    private static string CreateMainSource() =>
        """
        import c.stdio;

        fn main() -> int {
            printf("hello from cx\n");
            return 0;
        }
        """;

    private static IReadOnlyList<string> GetTestSourceEntries(ProjectConfig? project, string baseDirectory)
    {
        var entries = new List<string>();
        entries.AddRange(project?.Sources ?? []);

        if (Directory.Exists(Path.Combine(baseDirectory, "tests")))
        {
            entries.Add("tests");
        }

        return entries
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static IReadOnlyList<SourceFile> ResolveSourceFiles(
        IReadOnlyList<string> entries,
        string baseDirectory)
    {
        var files = new List<string>();
        foreach (var entry in entries)
        {
            var path = Path.GetFullPath(entry, baseDirectory);
            if (Directory.Exists(path))
            {
                files.AddRange(Directory.EnumerateFiles(path, "*.cx", SearchOption.AllDirectories));
                files.AddRange(Directory.EnumerateFiles(path, "*.cplus", SearchOption.AllDirectories));
                continue;
            }

            if (File.Exists(path))
            {
                files.Add(path);
            }
        }

        return files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new SourceFile(path, File.ReadAllText(path)))
            .ToList();
    }

    private static string ResolveOutputPath(
        string? commandValue,
        string? configValue,
        string fallback,
        string baseDirectory)
    {
        var value = commandValue ?? configValue ?? fallback;
        return Path.IsPathRooted(value)
            ? value
            : Path.GetFullPath(value, baseDirectory);
    }
}

internal sealed record BuildPlanRequest(
    string? InputPath,
    string? ConfigPath,
    string? COutputPath,
    string? NativeOutputPath,
    string? Compiler,
    IReadOnlyList<string> CompilerArgs);

internal sealed record StandardTestPlanRequest(
    string? COutputPath,
    string? NativeOutputPath,
    string? Compiler,
    IReadOnlyList<string> CompilerArgs);

internal sealed record ResolvedBuildPlan(
    string Name,
    IReadOnlyList<SourceFile> SourceFiles,
    string COutputPath,
    string NativeOutputPath,
    string Compiler,
    IReadOnlyList<string> CompilerArgs,
    IReadOnlyList<string> EnvPath);

internal sealed record ResolvedBuildPlanResult(bool Success, ResolvedBuildPlan Value, string Error)
{
    public static ResolvedBuildPlanResult Succeeded(ResolvedBuildPlan value) =>
        new(true, value, string.Empty);

    public static ResolvedBuildPlanResult Failed(string error) =>
        new(false, null!, error);
}

internal sealed record ProjectConfig(
    string Path,
    string BaseDirectory,
    string? Name,
    string? Kind,
    IReadOnlyList<string> Sources,
    string? Output,
    string? COutput,
    string? Compiler,
    IReadOnlyList<string> CompilerArgs,
    IReadOnlyList<string> EnvPath)
{
    public static ProjectConfigResult Load(string? requestedPath, bool useDefaultConfig)
    {
        if (string.IsNullOrWhiteSpace(requestedPath) && !useDefaultConfig)
        {
            return ProjectConfigResult.Succeeded(null);
        }

        var path = string.IsNullOrWhiteSpace(requestedPath)
            ? System.IO.Path.Combine(Environment.CurrentDirectory, "cx.toml")
            : System.IO.Path.GetFullPath(requestedPath);

        if (!File.Exists(path))
        {
            return string.IsNullOrWhiteSpace(requestedPath)
                ? ProjectConfigResult.Succeeded(null)
                : ProjectConfigResult.Failed($"Config file '{path}' does not exist.");
        }

        TomlTable? model;
        try
        {
            model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path));
            if (model is null)
            {
                return ProjectConfigResult.Failed($"Config file '{path}' is empty.");
            }
        }
        catch (Exception ex)
        {
            return ProjectConfigResult.Failed($"Failed to parse '{path}': {ex.Message}");
        }

        var baseDirectory = System.IO.Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        return ProjectConfigResult.Succeeded(new ProjectConfig(
            path,
            baseDirectory,
            GetString(model, "name"),
            GetString(model, "kind"),
            GetStringArray(model, "sources"),
            GetString(model, "output"),
            GetString(model, "c_output"),
            GetString(model, "cc") ?? GetString(model, "compiler"),
            GetStringArray(model, "cc_args").Count > 0
                ? GetStringArray(model, "cc_args")
                : GetStringArray(model, "compiler_args"),
            GetStringArray(model, "env_path")));
    }

    private static string? GetString(TomlTable model, string name) =>
        model.TryGetValue(name, out var value) && value is string text
            ? text
            : null;

    private static IReadOnlyList<string> GetStringArray(TomlTable model, string name)
    {
        if (!model.TryGetValue(name, out var value) || value is not TomlArray array)
        {
            return [];
        }

        return array.OfType<string>().ToList();
    }
}

internal sealed record ProjectConfigResult(bool Success, ProjectConfig? Value, string Error)
{
    public static ProjectConfigResult Succeeded(ProjectConfig? value) =>
        new(true, value, string.Empty);

    public static ProjectConfigResult Failed(string error) =>
        new(false, null, error);
}
