# Application profiling

Record the symptom and reproduce it with a bounded replay. Capture the offered load, concurrency, input mix, resource limits, success/error counts and latency distribution. Separate startup from steady state. For a steady-state claim, begin the capture after throughput and queues stabilize. Keep the workload and capture windows aligned.

## Choose a capture

Replace PID with the application process ID. Record tool versions and check `dotnet-trace list-profiles` for the installed sampling profile. Have the target signal the end of warmup and keep it running through attachment and the capture window. Run each collector on a separate equivalent replay when attribution does not require simultaneous events. Check local help for duration support or stop the collector cleanly at the window boundary.

```text
dotnet-counters collect -p PID --counters System.Runtime --duration 00:00:00:20 --format csv -o counters.csv
dotnet-trace collect -p PID --duration 00:00:00:20 -o stacks.nettrace
dotnet-trace report stacks.nettrace topN -n 20
dotnet-trace report stacks.nettrace topN -n 20 --inclusive
```

| Observation under load | Next capture and interpretation |
| --- | --- |
| CPU tracks load and throughput plateaus | Inspect sampled stacks. Exclusive samples point to work in a method, inclusive samples include its callees. Walk the expensive call chain and corroborate CPU use with process metrics. |
| Allocation rate and GC pauses rise together | Collect `dotnet-trace collect -p PID --profile gc-verbose --duration 00:00:00:20 -o gc.nettrace`. Inspect sampled allocation types and stacks in PerfView. Compare bytes per completed operation and pause time. Use a heap snapshot if the question concerns retained objects. |
| Lock contention rises | Collect `dotnet-trace collect -p PID --clrevents contention --duration 00:00:00:20 -o locks.nettrace`. Inspect contention events, wait durations and stacks. Identify the protected operation before changing synchronization. |
| Queue age grows with spare CPU | Correlate worker counts and blocked stacks with downstream latency and concurrency limits. A quiet CPU profile can accompany a saturated connection pool. |

Before interpreting a capture, reopen the file and verify its duration, provider, expected event or metric rows, and resolved workload frames where attribution depends on them. Check lost events when the reader exposes them. A successful exit alone cannot establish that useful samples were collected. Use the stack report for sampled stacks. Managed sampling can overrepresent locations where threads can be sampled. If a GC polling helper dominates exclusive samples, inspect its callers and corroborate with GC events and pause duration before attributing latency to collection. Use an OS CPU profile if the conclusion depends on exact CPU attribution. For GC or contention, inspect the corresponding typed events with PerfView or a compatible TraceEvent reader.

Read the CSV units and dimensions. Allocation bytes per second divided by completions per second gives bytes per operation only over matching intervals. CPU seconds per wall second measure cores consumed. GC allocation ticks sample allocation traffic, so type breakdowns estimate its sources. Measure per-operation totals independently (for example, process allocation deltas over a bounded replay). A high allocation rate alone does not show GC limits throughput. Check pause time and GC CPU against elapsed time and the target metric.

To extract allocation callers offline with TraceEvent, convert the original `.nettrace` using `TraceLog.CreateFromEventPipeDataFile`, open the resulting `.etlx` and obtain `log.Events.GetSource()`. Subscribe to `source.Clr.GCAllocationTick` before calling `source.Process()`. In the callback, group by `TypeName`, count ticks and accumulate `AllocationAmount64` as a sampled weight. For the type under investigation, use `log.GetCallStackForEvent(e)` and walk `Caller`, recording each `CodeAddress.FullMethodName`. Keep missing-stack counts visible. Report the leading resolved caller alongside the type and capture duration.

For startup, launch the published app through `dotnet-trace collect -- <executable> <arguments>` so collection includes work before an attach would succeed. [Trace commands](https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-trace) · [Runtime counters](https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-counters)

## Inspect generated code when the hypothesis depends on it

For a CoreCLR steady-state path, apply `DOTNET_JitDisasm=Type:Method`, `DOTNET_JitDisasmDiffable=1` and `DOTNET_JitStdOutFile=jit.txt` to the application process. Where supported, `DOTNET_JitDisasmOnlyOptimized=1` filters early tiers. Check the header for Tier1, Tier1-OSR or FullOpts. If the method was inlined, inspect its caller. Record any temporary NoInlining or frame-generation change.

For native stacks on a Linux JIT workload, launch a diagnostic replica with `DOTNET_PerfMapEnabled=1` before warmup. With perf sampling access, collect a bounded window and resolve the JIT dump:

```sh
perf record -k 1 -F 99 -g -p "$app_pid" -o perf.data -- sleep 15
perf inject --jit -i perf.data -o perf.jit.data
perf report --stdio -i perf.jit.data
perf annotate --stdio -i perf.jit.data
```

Set `app_pid` to the replica PID. Verify stacks resolve and load matching native debug symbols where needed. Samples can land after the instruction that stalled. Inspect neighboring instructions before assigning cost to a load or branch. Keep the profiles for attribution and repeat the decision run with normal deployment settings. [Upstream profiling examples](https://github.com/EgorBo/BlazingFastDotnet/blob/main/SKILL.md#profiling-and-performance-analysis)
