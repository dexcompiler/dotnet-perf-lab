#!/usr/bin/env dotnet
// Scaffold an evidence-rich BenchmarkDotNet project.
// Usage: dotnet run <skill-dir>/scripts/scaffold-bdn-project.cs -- <output-dir> [options]
// Requires a .NET 10+ SDK (file-based apps). Run with --help for options.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

return Scaffold.Run(args);

static class Scaffold
{
    // Newest stable BenchmarkDotNet and the newest runtime major it can launch. 0.15.8 throws
    // "GetRuntimeVersion not implemented for NotRecognized" on .NET 11 hosts; 0.16 previews run there.
    // Update these when BenchmarkDotNet ships a stable release for a newer runtime.
    const string StableBdnVersion = "0.15.8";
    const int StableBdnMaxRuntimeMajor = 10;
    const string NewRuntimeBdnVersion = "0.16.0-preview.2";
    const string BdnPackage = "BenchmarkDotNet";
    const string BdnWindowsPackage = "BenchmarkDotNet.Diagnostics.Windows";

    public static int Run(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }

        if (options.Help)
        {
            Console.WriteLine(Options.Usage);
            return 0;
        }

        try
        {
            return Execute(options);
        }
        catch (ScaffoldException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    static int Execute(Options options)
    {
        var root = Path.GetFullPath(options.Output!);
        var projectName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(projectName))
            throw new ScaffoldException("Output must name a project directory.");

        var ns = options.Namespace ?? ToNamespace(projectName);
        var existingAncestor = NearestExistingDirectory(root);

        // Inherit repository build policy instead of overriding it.
        var buildProps = FindUpwards(existingAncestor, "Directory.Build.props");
        var inherited = buildProps is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : DeclaredProperties(buildProps);

        var tfm = options.TargetFramework;
        if (tfm is null && !inherited.ContainsKey("TargetFramework") && !inherited.ContainsKey("TargetFrameworks"))
            tfm = DetectTargetFramework(existingAncestor);

        var effectiveTfm = tfm ?? inherited.GetValueOrDefault("TargetFramework") ?? inherited.GetValueOrDefault("TargetFrameworks") ?? "";
        var runtimeMajor = Regex.Matches(effectiveTfm, @"net(\d+)\.\d")
            .Select(m => int.Parse(m.Groups[1].Value))
            .DefaultIfEmpty(0)
            .Max();
        var bdnVersion = options.BdnVersion
            ?? (runtimeMajor > StableBdnMaxRuntimeMajor ? NewRuntimeBdnVersion : StableBdnVersion);

        var packagesProps = FindUpwards(existingAncestor, "Directory.Packages.props");
        var centralPackages = packagesProps is not null && UsesCentralPackageManagement(packagesProps);

        var packages = new List<string> { BdnPackage };
        if (options.IncludeWindowsDiagnostics)
            packages.Add(BdnWindowsPackage);

        var references = options.References.Select(r =>
        {
            var full = Path.GetFullPath(r);
            if (!File.Exists(full))
                throw new ScaffoldException($"Referenced project not found: {full}");
            return Path.GetRelativePath(root, full).Replace('\\', '/');
        }).ToList();

        var files = new Dictionary<string, string>
        {
            [$"{projectName}.csproj"] = ProjectFile(tfm, inherited, packages, centralPackages ? null : bdnVersion, references),
            ["Program.cs"] = ProgramFile(ns),
            ["BenchmarkConfig.cs"] = ConfigFile(ns),
            ["ExampleBenchmarks.cs"] = ExampleFile(ns),
            [".gitignore"] = "BenchmarkDotNet.Artifacts/\n",
        };

        // All-or-nothing: refuse before writing anything if a file would be overwritten.
        var clashes = files.Keys.Select(f => Path.Combine(root, f)).Where(File.Exists).ToList();
        if (clashes.Count > 0 && !options.Force)
            throw new ScaffoldException($"Refusing to overwrite (pass --force): {string.Join(", ", clashes)}");

        Directory.CreateDirectory(root);
        foreach (var (name, content) in files)
            File.WriteAllText(Path.Combine(root, name), content.ReplaceLineEndings("\n"), new UTF8Encoding(false));

        var projectPath = Path.Combine(root, $"{projectName}.csproj");
        Console.WriteLine($"Created {projectPath}");
        Console.WriteLine(tfm is null
            ? $"  TargetFramework: inherited from {buildProps}"
            : $"  TargetFramework: {tfm}");
        if (buildProps is not null && inherited.Count > 0)
            Console.WriteLine($"  Inherits build policy from {buildProps}");

        var effectiveBdnVersion = bdnVersion;
        if (centralPackages)
        {
            foreach (var package in packages)
            {
                var pinned = EnsurePackageVersion(packagesProps!, package, bdnVersion);
                Console.WriteLine(pinned is null
                    ? $"  Central package management: added {package} {bdnVersion} to {packagesProps}"
                    : $"  Central package management: {package} {pinned} already pinned in {packagesProps}");
                if (package == BdnPackage && pinned is not null)
                    effectiveBdnVersion = pinned;
            }
        }
        else
        {
            Console.WriteLine($"  BenchmarkDotNet: {bdnVersion}");
        }

        if (runtimeMajor > StableBdnMaxRuntimeMajor && effectiveBdnVersion.StartsWith("0.15.", StringComparison.Ordinal))
        {
            Console.WriteLine($"  warning: BenchmarkDotNet {effectiveBdnVersion} cannot launch .NET {runtimeMajor} benchmarks by default.");
            Console.WriteLine($"           Use {NewRuntimeBdnVersion}+ or a CsProjCoreToolchain job (see references/benchmarkdotnet-workflow.md).");
        }

        var solution = options.NoSolution ? null : options.Solution ?? FindSolution(existingAncestor);
        if (solution is not null)
        {
            var solutionPath = Path.GetFullPath(solution);
            var solutionDir = Path.GetDirectoryName(solutionPath)!;
            // Referenced projects are normally in the solution already; re-adding them fails the whole command.
            var (code, output) = RunDotnet(solutionDir, "sln", solutionPath, "add", projectPath, "--include-references", "false");
            if (code != 0 && output.Contains("include-references", StringComparison.OrdinalIgnoreCase))
                (code, output) = RunDotnet(solutionDir, "sln", solutionPath, "add", projectPath); // older SDKs
            Console.WriteLine(code == 0
                ? $"  Added to solution {solution}"
                : $"  warning: could not add to solution {solution}: {output.Trim()}");
        }

        Console.WriteLine();
        Console.WriteLine("Next:");
        Console.WriteLine($"  dotnet run -c Release --project \"{projectPath}\" -- --filter \"*ExampleBenchmarks*\" --job short");
        Console.WriteLine("Replace ExampleBenchmarks with domain benchmarks backed by correctness tests.");
        return 0;
    }

    static string ProjectFile(string? tfm, Dictionary<string, string> inherited, List<string> packages, string? version, List<string> references)
    {
        var props = new StringBuilder();
        void Prop(string name, string value)
        {
            if (!inherited.ContainsKey(name))
                props.AppendLine($"    <{name}>{value}</{name}>");
        }

        props.AppendLine("    <OutputType>Exe</OutputType>");
        if (tfm is not null)
            props.AppendLine($"    <TargetFramework>{tfm}</TargetFramework>");
        Prop("Nullable", "enable");
        Prop("ImplicitUsings", "enable");
        Prop("IsPackable", "false");
        // Symbols let DisassemblyDiagnoser interleave source; BenchmarkDotNet rejects non-optimized builds.
        Prop("DebugType", "portable");
        Prop("Optimize", "true");

        var items = new StringBuilder();
        foreach (var package in packages)
        {
            items.AppendLine(version is null
                ? $"    <PackageReference Include=\"{package}\" />"
                : $"    <PackageReference Include=\"{package}\" Version=\"{version}\" />");
        }
        foreach (var reference in references)
            items.AppendLine($"    <ProjectReference Include=\"{reference}\" />");

        return $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
            {props.ToString().TrimEnd()}
              </PropertyGroup>

              <ItemGroup>
            {items.ToString().TrimEnd()}
              </ItemGroup>

            </Project>

            """;
    }

    static string ProgramFile(string ns) => $$"""
        using BenchmarkDotNet.Running;

        namespace {{ns}};

        public static class Program
        {
            // Pass BenchmarkDotNet CLI options through, e.g. --filter, --job short, --statisticalTest 3%.
            public static void Main(string[] args) =>
                BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        }

        """;

    static string ConfigFile(string ns) => $$"""
        using BenchmarkDotNet.Configs;
        using BenchmarkDotNet.Diagnosers;
        using BenchmarkDotNet.Exporters.Json;

        namespace {{ns}};

        /// <summary>
        /// Evidence bundle for each run: allocations, disassembly (with baseline diff) and full JSON.
        /// No job is fixed here: choose it per run with --job short (feedback) or the default job (decisions).
        /// </summary>
        public sealed class BenchmarkConfig : ManualConfig
        {
            public BenchmarkConfig()
            {
                AddDiagnoser(MemoryDiagnoser.Default);
                AddDiagnoser(new DisassemblyDiagnoser(new DisassemblyDiagnoserConfig(
                    maxDepth: 3,
                    printSource: true,
                    exportGithubMarkdown: true,
                    exportCombinedDisassemblyReport: true,
                    exportDiff: true)));
                AddExporter(JsonExporter.Full);
            }
        }

        """;

    static string ExampleFile(string ns) => $$"""
        using BenchmarkDotNet.Attributes;

        namespace {{ns}};

        public enum InputShape
        {
            Uniform,
            MostlySmall,
        }

        // Report results per input shape: distributions can reverse the winner.
        [Config(typeof(BenchmarkConfig))]
        public class ExampleBenchmarks
        {
            private int[] _values = [];

            [Params(1_024, 65_536)]
            public int Count { get; set; }

            [Params(InputShape.Uniform, InputShape.MostlySmall)]
            public InputShape Shape { get; set; }

            [GlobalSetup]
            public void Setup()
            {
                var random = new Random(42);
                _values = new int[Count];
                for (int i = 0; i < _values.Length; i++)
                {
                    _values[i] = Shape switch
                    {
                        InputShape.Uniform => random.Next(0, 1_000_000),
                        _ => random.Next(100) < 95 ? random.Next(0, 128) : random.Next(0, 1_000_000),
                    };
                }
            }

            [Benchmark(Baseline = true)]
            public long ForLoop()
            {
                int[] values = _values;
                long sum = 0;
                for (int i = 0; i < values.Length; i++)
                {
                    sum += values[i];
                }

                return sum;
            }

            [Benchmark]
            public long SpanForeach()
            {
                long sum = 0;
                foreach (int value in _values.AsSpan())
                {
                    sum += value;
                }

                return sum;
            }
        }

        """;

    static string DetectTargetFramework(string workingDirectory)
    {
        if (FindUpwards(workingDirectory, "global.json") is null)
        {
            // No pin: prefer the newest stable SDK over an installed preview.
            var (listCode, list) = RunDotnet(workingDirectory, "--list-sdks");
            var stable = Regex.Matches(list, @"^(\d+)\.\d+\.\d+ ", RegexOptions.Multiline)
                .Select(m => int.Parse(m.Groups[1].Value))
                .DefaultIfEmpty(0)
                .Max();
            if (listCode == 0 && stable > 0)
                return $"net{stable}.0";
        }

        // `dotnet --version` honours global.json, so this matches the SDK the repo pins.
        var (code, output) = RunDotnet(workingDirectory, "--version");
        var match = Regex.Match(output, @"^(\d+)\.");
        if (code != 0 || !match.Success)
            throw new ScaffoldException($"Could not detect the SDK version (pass --target-framework): {output.Trim()}");
        return $"net{match.Groups[1].Value}.0";
    }

    static Dictionary<string, string> DeclaredProperties(string propsFile)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in XDocument.Load(propsFile).Descendants().Where(e => e.Parent?.Name.LocalName == "PropertyGroup"))
            properties[e.Name.LocalName] = e.Value.Trim();
        return properties;
    }

    static bool UsesCentralPackageManagement(string packagesProps)
    {
        var doc = XDocument.Load(packagesProps);
        return doc.Descendants().Any(e =>
            e.Name.LocalName == "ManagePackageVersionsCentrally" &&
            string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Adds a PackageVersion entry by text insertion so the file's formatting survives.
    /// Returns the already pinned version, or null when the entry was added.
    /// </summary>
    static string? EnsurePackageVersion(string packagesProps, string package, string version)
    {
        var hasBom = File.ReadAllBytes(packagesProps) is [0xEF, 0xBB, 0xBF, ..];
        var text = File.ReadAllText(packagesProps);
        var entry = new Regex(@"^(?<indent>[ \t]*)<PackageVersion\s+Include=""(?<id>[^""]+)""[^\n]*$", RegexOptions.Multiline);
        var entries = entry.Matches(text);
        var existing = entries.FirstOrDefault(m => string.Equals(m.Groups["id"].Value, package, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var pinned = Regex.Match(existing.Value, @"Version=""(?<v>[^""]+)""");
            return pinned.Success ? pinned.Groups["v"].Value : "(version set elsewhere)";
        }

        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        if (entries.Count > 0)
        {
            // Keep alphabetical order when the file already has it; otherwise append after the last entry.
            var next = entries.FirstOrDefault(m => string.Compare(m.Groups["id"].Value, package, StringComparison.OrdinalIgnoreCase) > 0);
            var indent = (next ?? entries[^1]).Groups["indent"].Value;
            var line = $"{indent}<PackageVersion Include=\"{package}\" Version=\"{version}\" />";
            text = next is not null
                ? text.Insert(next.Index, line + newline)
                : text.Insert(entries[^1].Index + entries[^1].Length, newline + line);
        }
        else
        {
            var close = text.LastIndexOf("</Project>", StringComparison.Ordinal);
            if (close < 0)
                throw new ScaffoldException($"Cannot find </Project> in {packagesProps}");
            var group = $"  <ItemGroup>{newline}    <PackageVersion Include=\"{package}\" Version=\"{version}\" />{newline}  </ItemGroup>{newline}";
            text = text.Insert(close, group);
        }

        File.WriteAllText(packagesProps, text, new UTF8Encoding(hasBom));
        return null;
    }

    static string? FindSolution(string start)
    {
        var root = new DirectoryInfo(start);
        while (root is not null && !Path.Exists(Path.Combine(root.FullName, ".git")))
        {
            root = root.Parent;
        }

        if (root is null)
        {
            return null; // Outside Git, only an explicit --solution can establish ownership.
        }

        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var solutions = dir.GetFiles("*.sln").Concat(dir.GetFiles("*.slnx")).ToList();
            if (solutions.Count == 1)
            {
                return solutions[0].FullName;
            }
            if (solutions.Count > 1)
            {
                Console.WriteLine($"  Several solutions in {dir.FullName}; pass --solution to choose one.");
                return null;
            }
            if (dir.FullName == root.FullName)
            {
                break;
            }
        }

        return null;
    }

    static string? FindUpwards(string start, string fileName)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    static string NearestExistingDirectory(string path)
    {
        var dir = new DirectoryInfo(path);
        while (!dir.Exists)
            dir = dir.Parent ?? throw new ScaffoldException($"No existing ancestor for {path}");
        return dir.FullName;
    }

    static string ToNamespace(string name)
    {
        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var cleaned = Regex.Replace(part, @"[^\p{L}\p{Nd}_]", "_");
            return cleaned.Length > 0 && char.IsDigit(cleaned[0]) ? "_" + cleaned : cleaned;
        });
        var ns = string.Join('.', parts);
        return ns.Length == 0 ? "Benchmarks" : ns;
    }

    static (int Code, string Output) RunDotnet(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new ScaffoldException("Could not start dotnet");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr.Result);
    }
}

sealed class Options
{
    public const string Usage = """
        Usage: dotnet run scaffold-bdn-project.cs -- <output-dir> [options]

          <output-dir>                   Project directory; its name becomes the project name.
          --namespace <ns>               Root namespace (default: derived from the directory name).
          --target-framework <tfm>       Target framework (default: inherited from Directory.Build.props,
                                         else net<major>.0 of the SDK pinned by global.json, else of the
                                         newest stable SDK installed).
          --bdn-version <version>        BenchmarkDotNet version (default: newest stable that can run the
                                         target runtime). Under central package management it is only used
                                         when no PackageVersion exists yet.
          --reference <csproj>           Add a ProjectReference to the code under test (repeatable).
          --solution <sln|slnx>          Add the project to this solution.
          --no-solution                  Do not add the project to any solution. By default the nearest
                                         single .sln/.slnx up to the repository root is used.
          --include-windows-diagnostics  Add BenchmarkDotNet.Diagnostics.Windows (hardware counters, ETW).
          --force                        Overwrite generated files.
        """;

    public string? Output { get; private set; }
    public string? Namespace { get; private set; }
    public string? TargetFramework { get; private set; }
    public string? BdnVersion { get; private set; }
    public List<string> References { get; } = [];
    public string? Solution { get; private set; }
    public bool NoSolution { get; private set; }
    public bool IncludeWindowsDiagnostics { get; private set; }
    public bool Force { get; private set; }
    public bool Help { get; private set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "-h" or "--help": options.Help = true; break;
                case "--namespace": options.Namespace = Value(); break;
                case "--target-framework": options.TargetFramework = Value(); break;
                case "--bdn-version": options.BdnVersion = Value(); break;
                case "--reference": options.References.Add(Value()); break;
                case "--solution": options.Solution = Value(); break;
                case "--no-solution": options.NoSolution = true; break;
                case "--include-windows-diagnostics": options.IncludeWindowsDiagnostics = true; break;
                case "--force": options.Force = true; break;
                case var a when a.StartsWith('-'): throw new ArgumentException($"Unknown option {a}");
                case var a when options.Output is null: options.Output = a; break;
                default: throw new ArgumentException($"Unexpected argument {args[i]}");
            }
        }

        if (options.Output is null && !options.Help)
            throw new ArgumentException("Missing <output-dir>");
        if (options.Solution is not null && options.NoSolution)
            throw new ArgumentException("--solution and --no-solution are mutually exclusive");
        return options;
    }
}

sealed class ScaffoldException(string message) : Exception(message);
