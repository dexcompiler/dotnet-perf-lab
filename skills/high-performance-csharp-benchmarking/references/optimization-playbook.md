# Optimization Playbook

## Preferred Order

Optimize in this order unless evidence says otherwise:

1. Algorithmic complexity and data structure choice.
2. Allocation, copy and conversion removal; object lifetime reduction.
3. Runtime/library primitives that already solve the problem.
4. Data layout and cache locality.
5. Concurrency model and contention.
6. Distribution-specific fast paths and branch predictability.
7. Safe span/vector loops when bulk library operations cannot express the kernel.
8. Unsafe access, ISA-specific intrinsics or branchless transformations when measurements justify their extra constraints.

Earlier steps are more portable, easier to review and usually larger. Later steps need stronger evidence and correctness coverage.

## Modern .NET Techniques

Consider:

- `Span<T>`/`ReadOnlySpan<T>` for slicing without allocation; `Memory<T>` when data crosses `await`.
- `stackalloc` for small, bounded, synchronous buffers (with a size threshold and a pooled or heap fallback); `[SkipLocalsInit]` only when every byte is written before it is read.
- `ArrayPool<T>`/`MemoryPool<T>` only with explicit ownership, clearing and return-on-failure rules, and no references after return.
- `SearchValues<T>` (bytes, chars, strings) for repeated membership or `IndexOfAny` searches.
- `FrozenDictionary`/`FrozenSet` for build-once, read-heavy lookups.
- `string.Create`, `IUtf8SpanFormattable`/`Utf8.TryWrite` and `ISpanFormattable` to format without intermediate strings.
- `System.Text.Json` source generation (and UTF-8 readers/writers) instead of reflection-based serialization.
- `BinaryPrimitives` for endian-aware reads/writes.
- `TensorPrimitives` and cross-platform `Vector128<T>`/`Vector256<T>`/`Vector<T>` before ISA-specific intrinsics.
- `CollectionsMarshal` (`AsSpan`, `GetValueRefOrAddDefault`) only when mutation/versioning risks are understood.
- `System.IO.Pipelines` for streaming parse/write with backpressure; bounded channels for producer/consumer with backpressure.
- Generic math and static abstract interface members for reusable numeric kernels without boxing or virtual dispatch.
- `ValueTask` for frequently synchronous async paths, respecting its single-consumption rules.
- `params ReadOnlySpan<T>` overloads to avoid array allocation at call sites.

## Text and lookup costs

- Keep UTF-8 input in bytes through search, numeric parsing and formatting when the contract permits it. Preserve invalid-input behavior, culture and comparison rules. Use ordinal comparison for protocol identifiers only when their specification calls for it.
- For a string-keyed dictionary and a character span, try `TryGetAlternateLookup<ReadOnlySpan<char>>` on a supporting TFM and comparer before materializing a temporary key. Preserve equality semantics on the fallback path.
- Cache fixed search sets across repeated operations. For a frozen collection or precomputed table, compare the additional build cost with the expected lookup count times the saving per lookup. Include startup and retained memory in the decision.
- For fixed regex patterns, consider `GeneratedRegex` with equivalent options, timeout and culture. For source-generated logging, guard expensive argument construction with `IsEnabled` because arguments are evaluated before the logging call.

## Zero-Cost Abstraction Claims

Before benchmarking a custom abstraction, compare it with the available library implementation at the same call site.

A claim that an abstraction (generic value-type policy, static abstract members, wrapper struct, interface port) costs nothing needs evidence:

- Benchmark the abstraction against equivalent direct concrete code at the same call shape.
- Show zero incremental bytes allocated and no boxing in the allocation columns.
- Show in disassembly that calls are inlined or devirtualized and that no extra indirection or copying remains. Shared generic code for reference-type arguments is not specialized per type; value-type instantiations are.
- Large structs can lose to references through copying; measure `in`/`ref readonly` rather than applying them everywhere.
- Count generic code growth and compile/JIT time as costs.
- If the evidence does not support "zero", narrow the claim or keep the simpler code.

## SIMD and Intrinsics

Benchmark the matching bulk operation first, such as `SequenceEqual`, `IndexOf`, `CopyTo`, `Clear` or `Fill`. For a custom kernel, try a safe loop that guards the remaining span length against one vector width, loads the vector, advances the span by that width and processes the scalar tail. Inspect the generated loop on the pinned runtime and target CPU before claiming the JIT eliminated bounds checks.

Require:

- A scalar fallback, exercised by tests with acceleration disabled.
- Feature checks: `Vector128.IsHardwareAccelerated` (and 256/512), or ISA checks such as `Avx2.IsSupported` / `AdvSimd.IsSupported` when ISA-specific code is justified.
- Benchmarks on supported and unsupported hardware, x64 and ARM64 when both are targets.
- Disassembly proving the expected vector instructions are emitted.
- Tests for tails, empty and small inputs, alignment assumptions and overflow.
- A stated floating-point rule (bit-exact or tolerance); vector reductions reorder operations.

Watch for:

- `Vector512.IsHardwareAccelerated` is deliberately false on some AVX-512 CPUs where 512-bit use throttles clocks (see `DOTNET_PreferredVectorBitWidth`); do not force 512-bit paths without measuring on the target hardware.
- Extra copies into temporary arrays.
- Dependency chains from horizontal reductions (use several accumulators).
- Bounds checks that survive vectorization.
- Code size and instruction-cache pressure from heavy unrolling.

## Branchless Code

Use branchless techniques when unpredictable branches dominate and the replacement does not lengthen the critical path.

Require:

- A readable reference implementation.
- A table or bit-math derivation and a boundary-mapping proof.
- Tests around every classification threshold.
- Benchmarks across all-small, all-large, mixed and adversarial distributions.
- Counter or disassembly evidence when claiming a branch-prediction improvement.

Branchless code can lose when one branch is overwhelmingly likely, the predictor is accurate, or the branchless form adds an expensive dependency chain. The JIT may already emit conditional moves; check before hand-writing tricks.

## Serialization Hot Paths

For binary and text protocols:

- Preserve canonical encoding and exact byte layout.
- Use `BinaryPrimitives` for endian operations unless evidence justifies lower-level writes.
- Test every length boundary and destination size; never write past the provided span.
- Keep partial-write behavior deterministic.
- Round-trip against a known-good library.
- Never retain a span into a buffer that has been returned to a pool; durable data must own its bytes.

For variable-length integer encoders (MessagePack-style and similar), benchmark small/fixint, medium, large, negative and realistic mixed streams separately. A mixed-stream win does not prove an all-small win.

## GC and Allocation Behavior

Identify:

- Per-operation allocations.
- Boxing through interfaces, non-generic collections, `object` parameters, string interpolation into `object`, or enum/struct `Equals`/`GetHashCode` without overrides.
- LINQ, iterator and async state-machine allocations.
- Captured closures and delegate allocations (use static lambdas and state arguments).
- Logging that formats or allocates when the level is disabled (use source-generated logging).
- Pooling that trades allocation for contention, retention or latency variance.

Prefer zero-allocation hot paths only when they do not make correctness fragile. Consider GC mode (workstation/server, concurrent, `DOTNET_GCgen0size`) as a deployment variable and record it.

## Concurrency

Measure contention explicitly:

- Single-thread throughput hides multi-thread bottlenecks; benchmark at production concurrency.
- Locks can be fine under low contention and terrible under bursty load. `System.Threading.Lock` is the modern dedicated lock type.
- Lock-free code can still bottleneck on cache-line bouncing and false sharing; pad hot per-thread counters.
- Channels and pipelines add coordination cost but provide backpressure and structure; never silently drop required messages.
- Avoid synchronous blocking on async work and unbounded task fan-out.

When many writers increment one counter, aggregate in partition-local variables and combine at completion if delayed visibility meets the contract. For live telemetry, padded stripes can reduce cache-line traffic. Writers sharing a stripe still need atomic updates. With P stripes, memory and total-read cost are O(P), and a concurrent sum gives no atomic snapshot guarantee. Measure both update throughput and the actual read frequency. Padding increases footprint and depends on target cache-line size. Thread-local slots instead scale with participating threads and need a bounded lifetime. Use a synchronized decision for quotas or admission that require an exact check and update.

For deterministic systems, define ordering guarantees, idempotency and time abstractions before parallelizing.
