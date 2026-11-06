# Runtime and deployment experiments

Start from the deployed configuration. Record the SDK/runtime version, publish command, RID, CPU features, container CPU/memory limits and effective settings. If production overrides a runtime default, distinguish the deployed baseline from an experiment restoring that default. Verify each setting against the documentation or runtime source for the pinned release.

Choose one change and declare the primary metric, minimum improvement, guardrail limits and decision rule before collecting timings. Publish once for runtime-only settings. In every fresh process, record the effective setting (for GC, `GCSettings.IsServerGC`), runtime and visible processor count. An environment-variable value only records intent.

Pair launches under comparable conditions and balance which variant runs first. Keep the input mix, load model, concurrency, warmup and window fixed. With closed-loop clients, completed throughput varies even at fixed concurrency. With an open-loop generator, also record offered rate, backlog and dropped work. Include bursts and recovery for adaptive settings.

Retain one row per launch with its pair/order, elapsed time, completed work, correctness failures, primary metric and guardrails. Compare paired effects against the declared threshold using launches as replicates. Report the effect size, uncertainty and each guardrail separately. Label the latency unit (batch or request), memory scope (process-lifetime peak or window) and CPU/resource limits. A runtime processor-count override changes what .NET sees without enforcing OS affinity or a CPU quota.

| Evidence | Experiment | Cost to measure |
| --- | --- | --- |
| GC pauses or collection CPU limit throughput | Compare the deployed GC mode with a supported alternative under the actual memory limit. For server GC, examine dynamic adaptation separately. | Pause distribution, working set, peak memory and completed throughput |
| Worker queue grows under bursts | Locate blocking first. If worker injection is implicated, compare one minimum-worker setting under the same burst and downstream capacity. | Queue age, CPU, thread count, tail latency and recovery |
| Small socket operations spend substantial time dispatching completions | On a runtime that supports inline socket completions, compare that setting alone. Exercise slow or blocking continuations as well as the common case. | I/O progress across connections, errors, throughput and tail latency |
| Cold starts and steady-state performance diverge | Compare tiering or PGO as separate hypotheses in fresh JIT processes. Record the code tier and time to readiness. | Startup CPU/time, sustained throughput and code size |

Keep request semantics, middleware, logging and serialization settings equivalent across runs. Revalidate retained settings after runtime or hardware upgrades. Record the original value or artifact and restore it when the primary improvement misses its threshold or a guardrail regresses. [Runtime configuration](https://learn.microsoft.com/dotnet/core/runtime-config/)

For a steady-state claim, use a pilot to record completions and queue depth over successive equal windows. Choose a warmup and run length that cover stabilization for both variants and repeated cycles of the behavior under test, such as GC collections. Apply that schedule to fresh decision runs. If a short replay ends before stabilization is demonstrated, report the result for that replay duration.

## NativeAOT

Republish each candidate from the same source and SDK. For example, compare these two publish outputs on a supported Linux x64 target, assuming the project uses the blended optimization default:

```sh
dotnet publish App.csproj -c Release -r linux-x64 -p:PublishAot=true -o out/baseline
dotnet publish App.csproj -c Release -r linux-x64 -p:PublishAot=true -p:OptimizationPreference=Speed -o out/speed
```

Run the actual binaries. Record their hashes and sizes, startup time, peak memory and sustained throughput. For an instruction-set experiment, select an explicit minimum ISA supported across deployment hosts and exercise the binary on the oldest supported CPU. A build-host-native target can require instructions absent elsewhere. NativeAOT generates code at publish time, so JIT tiering experiments apply only to JIT deployments. [NativeAOT optimization](https://learn.microsoft.com/dotnet/core/deploying/native-aot/optimizing)
