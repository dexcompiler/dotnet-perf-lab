# BenchmarkDotNet Workflow

## Harness Requirements

Use BenchmarkDotNet as a reproducible evidence generator:

- Deterministic input: fixed seeds or checked-in traces. Build inputs in `[GlobalSetup]`; feed realistic cases with `[Params]`/`[ParamsSource]`/`[ArgumentsSource]`.
- Keep setup, parsing, data allocation and verification out of measured methods unless they are the work being measured.
- Return a value or write to a consumed buffer so the JIT cannot remove the work.
- Set `OperationsPerInvoke` when a benchmark method loops over N logical operations.
- Benchmark classes must be `public` and not `sealed`; benchmark methods must be `public`.
- Do not use `[IterationSetup]`/`[IterationCleanup]` for microbenchmarks: they force one invocation per iteration, so timer resolution and harness overhead dominate. Use them only for operations of roughly 100 ms or more; otherwise restructure (for example, pre-build N inputs and consume one per invocation).

Evidence config (what the scaffold generates):

```csharp
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;

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
            exportDiff: true)));   // baseline-vs-candidate asm diff
        AddExporter(JsonExporter.Full);
    }
}
```

Keep the job out of the config and choose it per run, so feedback and decision runs use the same code:

```bash
dotnet run -c Release -- --filter "*Parser*" --job short                    # feedback loop
dotnet run -c Release -- --filter "*Parser*" --statisticalTest 3%           # decision run (default job)
dotnet run -c Release -- --filter "*Parser*" --job dry                      # smoke test: does it run?
```

When a repository defines named profiles (for example quick/decisive), use those instead.

## Runtime Selection

- Without `--runtimes`, BenchmarkDotNet benchmarks the host runtime. `--runtimes net10.0 net11.0` compares runtimes (the first is the baseline); `nativeaot10.0` (etc.) builds a NativeAOT job. Report JIT and NativeAOT results separately: they are different deployments.
- **BenchmarkDotNet may not recognize a runtime newer than its release.** For example, 0.15.8 on a .NET 11 host fails with `NotImplementedException: GetRuntimeVersion not implemented for NotRecognized` before any benchmark runs. Preferred fix: a BenchmarkDotNet version that supports the runtime (0.16 previews run .NET 11). If the repository pins an older version, add a job with an explicit toolchain, and record in the results that the runtime moniker is only a compatibility token:

```csharp
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.DotNetCli;

AddJob(Job.Default
    .WithRuntime(CoreRuntime.Core10_0)   // recognized token only; the toolchain decides the TFM
    .WithToolchain(CsProjCoreToolchain.From(new NetCoreAppSettings(
        targetFrameworkMoniker: "net11.0", runtimeFrameworkVersion: null, name: ".NET 11"))));
```

  Verify the `// Runtime=` line in the output shows the intended runtime.

## JIT Modes: Warm, Cold, PGO

- Default jobs measure **warm, steady-state** code after tiering and Dynamic PGO. That is the right question for long-running hot loops, not for startup or rarely called paths.
- Measure **cold/first-call** cost separately with `RunStrategy.ColdStart` (several launches, zero warmup). Keep tiering/ReadyToRun settings matching production and record them.
- **Dynamic PGO** (default since .NET 8) specializes for observed types and branch biases. A benchmark that shows one implementation at an interface call site gets guarded devirtualization that production code seeing several implementations may not. Model production polymorphism in the benchmark, and when the claim depends on PGO, compare `--envVars DOTNET_TieredPGO:0`.
- Other environment knobs worth recording or varying: `DOTNET_TieredCompilation`, `DOTNET_ReadyToRun`, `DOTNET_gcServer`, `DOTNET_GCgen0size`. Use `--envVars KEY:value` or `Job.WithEnvironmentVariable`.

## Layout and Noise Controls

- `--memoryRandomization` randomizes allocation alignment between iterations; use it when results look bimodal or sensitive to alignment.
- `--launchCount` above 1 exposes between-process variance (code placement, ASLR).
- `--affinity` pins the benchmark process for deeper investigation; first make the benchmark valid.
- BenchmarkDotNet switches Windows to the High Performance power plan by default; still avoid decisive runs on battery, thermally throttled hosts or noisy VMs.

## Hardware Counters and Profilers

- BenchmarkDotNet's `[HardwareCounters(...)]` and `--counters` work **only on Windows**, through ETW, with `BenchmarkDotNet.Diagnostics.Windows` referenced and an elevated process. They usually fail or lie under Hyper-V guests, WSL, containers, CI runners and cloud VMs.
- On Linux, use `perf stat -e branches,branch-misses,cache-misses,instructions` against the benchmark process, or BenchmarkDotNet's `[PerfCollectProfiler]` attribute (config-only; not a `--profiler` value).
- Cross-platform tracing: `EventPipeProfiler` (`--profiler EP`) produces a `.nettrace` for PerfView/Visual Studio/speedscope. Windows: `--profiler ETW`.
- When counters are unavailable, rely on distribution-sensitive timings, disassembly and profiler output, and say which evidence is missing.

## Quick Checks Without BenchmarkDotNet

- **JIT disassembly** on a release runtime (.NET 7+): `DOTNET_JitDisasm="Type:Method"` (wildcards allowed), `DOTNET_JitDisasmSummary=1` to list every jitted method and its tier, `DOTNET_JitStdOutFile=path` to capture output. Read the Tier1 / FullOpts version, not Tier0.
- **Runtime counters:** `dotnet-counters monitor -p <pid>` for allocation rate, GC counts, thread-pool queue length, lock contention.
- **Traces and heaps:** `dotnet-trace collect`, `dotnet-gcdump`.

Use these checks to form a hypothesis. For a kernel claim, confirm it with the benchmark evidence config. For application behavior under load, use a controlled replay as described in [application profiling](application-profiling.md).

## Comparing Runs

- In-run: mark the baseline with `[Benchmark(Baseline = true)]` and add `--statisticalTest <threshold>` (Mann–Whitney U) so the table says whether each difference is significant at the threshold you declared beforehand.
- Across commits or machines: keep both `*-report-full.json` sets and compare them with a tool such as the `ResultsComparer` in the dotnet/performance repository (`--threshold`), rather than eyeballing means.
- Use the asm diff export to confirm the change you intended is the change the JIT made.

## Artifact Bundle

For each decision (and for AI-assisted loops), keep together:

- Benchmark source, commit and exact command.
- Full result table (not only the mean), allocation and GC columns.
- `*-report-full.json`, disassembly markdown/diff.
- Counter or profiler output when available, or a note that it was unavailable and why.
- CPU model, core count, OS, runtime, TFM, JIT/PGO/tiering mode, process architecture, power mode, virtualization/container status.
- Correctness tests and their output.
- Constraints: portability, no-unsafe policy, target ISA, NativeAOT, ARM64, cloud variance.

## CI Model

Do not fail ordinary CI on small timing deltas. Prefer:

- Correctness tests as hard gates.
- `--job dry` runs so benchmarks cannot silently rot.
- Allocation regressions as hard or near-hard gates when an allocation budget exists (allocation counts are far less noisy than time).
- Benchmark artifacts uploaded for review.
- Scheduled or manual performance jobs on stable, dedicated runners.
- Threshold alerts only after enough baseline history exists to know the noise floor.
