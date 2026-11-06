# Correctness and Benchmark Design

## Correctness Comes First

Before optimizing, define what must not change:

- Functional equivalence to a readable reference implementation.
- Boundary behavior at min/max values and representation changes.
- Error behavior, exceptions, return codes and partial-write semantics.
- Thread-safety and cancellation behavior.
- Encoding, ordering and deterministic serialization rules.
- Overflow, checked/unchecked arithmetic, endianness and culture invariance.
- Floating-point results: SIMD, parallel and reassociated reductions change summation order and therefore results. State whether the optimized path must be bit-exact (then it must reproduce the reference order) or within a stated ULP/relative tolerance, and test to that rule. Exact decimal or money semantics are not a candidate for reassociation.

For serialization/protocol code, test against an independent known-good implementation.

## Test Matrix

Use the smallest matrix that can falsify the optimization:

- Boundary cases around every encoding/classification threshold.
- Random cases with fixed seeds, logged so failures reproduce.
- Adversarial cases that defeat branch predictors, caches or pooling assumptions.
- Buffer sizes: empty, exact, one short, one extra, maximum expected; destination too small.
- SIMD tails: lengths 0, 1, vector width ± 1, several widths plus a remainder.
- Aliasing/slicing cases for `Span<T>` and `Memory<T>`.
- Architecture/runtime cases when code depends on intrinsics or JIT behavior: run tests with the accelerated path disabled (for example `DOTNET_EnableAVX2=0`, `DOTNET_EnableAVX512F=0`, `DOTNET_EnableHWIntrinsic=0`) so fallbacks are exercised, and on ARM64 where supported.
- Concurrency cases when shared state, pooling, static caches, locks, channels or pipelines are involved.

For complex arithmetic or bit classification, require a proof sketch plus property tests.

## Distribution Design

Benchmark distributions must match the question:

- Uniform random data is rarely production-representative.
- All-small, all-large and mixed distributions can produce opposite winners.
- Short repeating patterns train the branch predictor and hide mispredictions; use inputs longer than the predictor's history.
- Tiny inputs benchmark call overhead; very large inputs benchmark memory bandwidth. Include the sizes production uses.
- Call-site shape matters: with Dynamic PGO, one concrete type at an interface or delegate call site is optimized differently from the mix production sees.
- Captured production traces are valuable; anonymize them and make replay deterministic.

Report results per distribution. One global mean is not enough when workload shape changes the winner.

## Validity Checks

Signs of an invalid benchmark:

- The optimized method returns a different value or skips work.
- Setup cost is included in only one of the compared benchmarks.
- The benchmark allocates inside the measured method unintentionally.
- The method is too small and dominated by harness overhead, or uses `[IterationSetup]` for sub-100 ms work.
- Constant or `static readonly` inputs let the JIT fold or specialize work that production cannot.
- `OperationsPerInvoke` does not match the inner loop.
- Buffer reuse gives cache locality production will not have (or the reverse).
- A scalar reference is compared against a vector path on hardware that does not accelerate it, or vice versa.
- A monomorphic benchmark stands in for a polymorphic production call site.
- The benchmark ran on a different runtime than intended (check the `// Runtime=` line).

## Interpretation

Analyze more than `Mean`:

- `Error`, `StdDev`, percentiles and `Ratio` show magnitude and noise; bimodal results suggest alignment or tiering effects.
- Use a statistical test against a threshold declared before measuring. A statistically indistinguishable result is "no demonstrated difference", not "zero overhead".
- Allocation columns reveal hidden heap pressure and GC cost.
- Disassembly explains bounds checks, calls, vector instructions and branch layout; code size matters for the instruction cache.
- Counters can confirm or refute branch-misprediction and cache hypotheses.

State Big-O time and memory even for microbenchmarks. A nanosecond win in one size regime may lose when asymptotics or locality change.

## Microbenchmarks vs System Evidence

Microbenchmarks answer "is this kernel cheaper?". They do not establish:

- Tail latency (p99/p99.9), queueing delay or throughput under load.
- GC pause behavior, allocation rate at scale or retained memory.
- Lock contention and thread-pool starvation under bursty concurrency.
- Network, disk or database latency.

Measure those with end-to-end replay or bounded load tests that report latency distributions, allocation rate, GC pauses and queue age. Never derive a service p99 from a microbenchmark mean.

## Production Acceptance

Accept an optimization when:

- The benchmark measures a real hot path.
- The speedup is material against service-level objectives or cost, and clears the pre-declared threshold.
- Correctness coverage is stronger than the implementation's cleverness.
- The code remains maintainable or is isolated behind a stable API.
- Fallback behavior is deterministic and tested.
- Observability can detect a production regression after release.
