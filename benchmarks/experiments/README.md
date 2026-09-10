# Reproducing the reader experiments

These are experimental patches and probes, not active production optimizations.
The recorded prework baseline (`a0b1aab`) keeps binary group membership and the original
temporal reader parsers. The temporal fast path has since been integrated by #30; apply
the archived temporal patch only to a worktree at that prework baseline, not on top of
the integrated implementation. Results, caveats and BenchmarkDotNet commands are recorded in
`../FixSourceGenerator.Benchmarks/README.md`.

Use separate worktrees from the same baseline commit for every variant. Do not apply a
prototype patch to a dirty implementation branch or combine variants when measuring an
individual effect. Preserve a clean baseline worktree for comparison.

## Temporal reader (#30)

From an experimental worktree rooted at prework commit `a0b1aab`:

```bash
git apply --check benchmarks/experiments/temporal-reader/fast-path.patch
git apply benchmarks/experiments/temporal-reader/fast-path.patch
dotnet run -c Release --project benchmarks/experiments/temporal-reader/TemporalReaderProbe.csproj
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- --reader-process-check
```

The probe compares the parser contract on 75,293 inputs and exercises the X/W decode and
projection matrices. It also works against the unchanged baseline as a reference check;
passing it alone does not prove that the patch has been applied. It is .NET 10 evidence,
not a substitute for permanent tests on all supported consumer runtimes.

Run the process benchmarks from each worktree's own root so BenchmarkDotNet rebuilds the
correct generator. Use `--buildTimeout 300` for the full dictionary.

## Group membership (#33)

For the post-projection decision, use the integrated binary baseline `835941c` and bounded
bitmap policy `7db739c` (or `9249509`, which also updates the order/process fixtures). Build each benchmark project
in its own worktree, then run:

```bash
dotnet run -c Release --project benchmarks/experiments/paired-load/PairedLoad.csproj -- \
  --residual /absolute/binary/benchmark.dll /absolute/bitmap/benchmark.dll
```

This mode initializes and reports group metadata separately, then alternates full/projected
reader, slicing and combined codec workloads. It accepts both generated representations.
Only one membership array is retained per group by the bounded candidate; tags above the
8 KiB bitmap budget or too sparse for a memory win fall back to the sorted array.

The patches below are **archived pre-projection prototypes**, not the bounded implementation.
Do not apply them on top of the current integrated runtime:

Apply either `group-membership/hashset.patch` or `group-membership/bitmap.patch` to its own
clean worktree, then run:

```bash
dotnet run -c Release --project benchmarks/experiments/group-membership/MembershipProbe.csproj
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- \
  --filter '*MarketDataReaderBenchmarks*' --buildTimeout 300
```

The membership probe requires one of the experimental metadata representations. Both
prototypes retain the original integer arrays as well; they do not represent a final
retained-memory policy. In particular, the bitmap prototype is for the bounded fixture
tag ranges, not an unbounded production policy for arbitrary custom tags.

The packaged membership variants also retain the internal sorted-constructor path used by
the newer process-decomposition benchmarks. That explicit path remains binary search;
use `MarketDataReaderBenchmarks` for the generated HashSet/bitmap comparison. This
compatibility addition changes the experimental runtime shape from the original measurement
snapshot, so remeasure these packaged variants rather than treating historical timings as
measurements of their exact code layout.

The baseline's source-shape assertion for the generated binary-constructor call intentionally
does not match the HashSet/bitmap constructor calls. Running the entire baseline test suite
unchanged on these variants therefore needs that assertion adapted; the membership probe
checks their runtime behavior and is not a claim that every unchanged baseline test passes.

## Interleaved load

After building the baseline and temporal variant benchmark projects in Release:

```bash
dotnet run -c Release --project benchmarks/experiments/paired-load/PairedLoad.csproj -- \
  /absolute/baseline/benchmarks/FixSourceGenerator.Benchmarks/bin/Release/net10.0/FixSourceGenerator.Benchmarks.dll \
  /absolute/variant/benchmarks/FixSourceGenerator.Benchmarks/bin/Release/net10.0/FixSourceGenerator.Benchmarks.dll
```

The runner loads the assemblies in separate load contexts, warms their workloads, and
alternates eight 250 ms blocks per variant/case on one thread. It reports samples,
medians and measured thread allocations. This supplements, not replaces, BenchmarkDotNet.

Probes are intentionally outside the main solution. Generated binaries, session logs and
machine-specific NuGet-cache paths must not be committed.
