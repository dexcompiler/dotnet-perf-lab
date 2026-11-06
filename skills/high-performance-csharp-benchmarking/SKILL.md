---
name: high-performance-csharp-benchmarking
description: Diagnose slow .NET applications, design or repair BenchmarkDotNet benchmarks, and validate C# hot-path optimizations or runtime tuning with correctness tests and representative measurements.
---

# High Performance C# Benchmarking

Follow repository instructions and extend its existing performance harness. Use the pinned SDK, TFM and deployment model. Where unpinned, prefer the newest installed stable SDK.

## Workflow

1. Define the claim, workload distribution, supported OS/CPU/runtime, allocation budget and minimum material improvement before comparing results. Identify the correctness contract and the relevant complexity, locality and concurrency costs.
2. Choose the measurement that can answer the question.
   - **Known kernel.** Establish a correctness oracle, then reproduce production inputs and call-site polymorphism in [BenchmarkDotNet](references/benchmarkdotnet-workflow.md). Use deterministic setup, consume results, and verify operation counts. For decision runs use a full job with allocation data, full JSON and disassembly diff.
   - **Slow application.** Reproduce the symptom under representative load. Use [application profiling](references/application-profiling.md) to identify the limiting resource and responsible path. Extract a kernel if isolation will test the hypothesis. For GC, contention, scheduling or I/O, a controlled application replay can supply the decision evidence.
3. Change one cause at a time. Follow the [optimization playbook](references/optimization-playbook.md) for code and the [runtime experiments guide](references/runtime-experiments.md) for deployment settings. Capture the baseline and candidate commands, commits, environment, workload and diagnostic artifacts.
4. Apply the [correctness and benchmark design checks](references/correctness-and-benchmark-design.md). Test boundaries, fixed-seed adversarial inputs and concurrency assumptions as applicable. Require an independent oracle for protocol changes and an explicit numerical tolerance or reduction order for floating point. Document invariants, overflow and tested fallbacks for unsafe, intrinsic or branchless paths.
5. Compare distributions against the declared threshold with an appropriate statistical comparison. Confirm the mechanism using allocations, traces, counters or assembly. For service claims, repeat the end-to-end workload under deployment settings and report throughput, tail latency and errors. Record portability and maintenance costs with the decision.

## New benchmark project

For a repository that needs a new harness, run the [scaffold](scripts/scaffold-bdn-project.cs) with a .NET 10+ SDK.

```bash
dotnet run <skill-dir>/scripts/scaffold-bdn-project.cs -- ./perf/MyLib.Benchmarks --reference ./src/MyLib/MyLib.csproj
```

It accounts for repository build settings, central package management and solution membership. See `--help` for overrides. Replace its example with benchmarks backed by domain correctness tests.
