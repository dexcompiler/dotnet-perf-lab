# Workstation versus server GC for JSON ingestion

A single worker parses four in-memory UTF-8 documents containing 1, 4, 16 and 64 items. Every cycle must sum their quantities to 4,454. One timed batch repeats that cycle 64 times. The p99 measures a batch of 256 documents. The input stays hot in cache.

## Decision rule

Use one framework-dependent Release build. Expose four processors to .NET and compare workstation GC with server GC. The processor override controls the runtime's view of capacity. The OS schedules the process normally. Each launch reports its effective GC mode, processor count, runtime and checksum failures.

Run six pairs, reversing order in successive pairs. Each fresh process warms up for two seconds and measures for three. Require a throughput gain above 5% in all six pairs (a one-sided sign test at 5% with six pairs requires six wins). Use the median of the six server/workstation ratios for each guardrail. Accept at most 5% higher batch p99, 10% higher lifetime peak working set and 10% higher allocated bytes per batch. Require every checksum to pass.

## Reference run

The [saved launch records](reference/decision.json) were collected on Windows x64, Intel Core Ultra 9 285K, .NET SDK 10.0.401 and CoreCLR 10.0.14. All 107,944 batches passed. Server GC cleared the throughput threshold in zero pairs.

| Server GC relative to workstation GC | Median paired change |
| --- | ---: |
| Batches per second | −2.15% |
| Batch p99 | +7.11% |
| Peak working set | −26.57% |
| Allocated bytes per batch | +0.00% |

Keep workstation GC for this workload and objective. The smaller peak working set could matter for a different objective. The short windows characterize this replay only. A deployment decision should use its actual concurrency, arrivals, resource limits and sustained collection behavior.

## Reproduce and profile

Use the [root quickstart](../../README.md#quickstart) to run the comparison. The runner keeps diagnostics out of decision timings. For a separate diagnostic replica, take the published DLL path under the printed run directory and run `dotnet <path-to-gc-replay.dll> 60 3` with the same GC and processor overrides. The arguments are measured seconds and warmup seconds. Attach after the workload prints `MEASURE`. Follow the [profiling recipes](../../skills/high-performance-csharp-benchmarking/references/application-profiling.md) for counters, sampled stacks and GC allocation events.

The original Windows assessment exercised those three captures. Linux perf, contention events and NativeAOT are documented techniques awaiting separate validation.
