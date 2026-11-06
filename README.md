# .NET Perf Lab

An agent skill for deciding whether a C# performance change should ship. Start with a representative workload and a correctness check. Use BenchmarkDotNet for an isolated hot path, or counters and traces for a running application. Define how large an improvement matters before comparing results.

## Quickstart

Clone the repository and try the worked example. The first command after cloning rechecks the published measurements using Python 3.8+. The second publishes the workload with .NET SDK **10.0.401** and runs twelve fresh processes, taking about a minute plus build time. Run on an idle host with .NET 10 and record the runtime patch reported in your results.

```sh
git clone https://github.com/dexcompiler/dotnet-perf-lab.git
cd dotnet-perf-lab
python examples/gc-replay/replay.py --summarize examples/gc-replay/reference/decision.json
python examples/gc-replay/replay.py
```

Each replay writes its launch measurements to a new directory under `artifacts/gc-replay/`. To see how the original experiment was judged, read the [workload, decision rule and results](examples/gc-replay/README.md).

## Use the skill in Codex

Copy the [skill directory](skills/high-performance-csharp-benchmarking) into your target repository's `.agents/skills/` directory. From this checkout, with the destination directory already created:

```powershell
Copy-Item -Recurse ./skills/high-performance-csharp-benchmarking /path/to/your-repo/.agents/skills/
```

Start a new Codex session in the target repository and try:

> Use $high-performance-csharp-benchmarking to investigate our JSON ingestion path. Establish the allocation rate and the caller responsible, then test one change against a 5% throughput target and a 5% p99 latency regression limit. Preserve parsing behavior. Show the evidence for keeping or reverting the change.

For a known kernel, the skill includes a [.NET 10 file-based scaffold](skills/high-performance-csharp-benchmarking/scripts/scaffold-bdn-project.cs) for adding a BenchmarkDotNet project. The references cover [benchmark design](skills/high-performance-csharp-benchmarking/references/correctness-and-benchmark-design.md), [application profiling](skills/high-performance-csharp-benchmarking/references/application-profiling.md) and [runtime experiments](skills/high-performance-csharp-benchmarking/references/runtime-experiments.md).

## Credits and license

Thanks to [EgorBo](https://github.com/egorbo) and [neuecc](https://github.com/neuecc) for their contributions to the .NET performance community. The profiling guidance draws on EgorBo's [BlazingFastDotnet](https://github.com/EgorBo/BlazingFastDotnet). [MIT license](LICENSE) · [Upstream MIT notice](licenses/BlazingFastDotnet-MIT.txt)
