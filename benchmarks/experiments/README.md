# Reproducing the reader experiments

These are experimental patches and probes, not active production optimizations.
The baseline keeps the binary group-membership implementation and the original temporal
reader parsers. Results, caveats and BenchmarkDotNet commands are recorded in
`../FixSourceGenerator.Benchmarks/README.md`.

Use separate worktrees from the same baseline commit for every variant. Do not apply a
prototype patch to a dirty implementation branch or combine variants when measuring an
individual effect. Preserve a clean baseline worktree for comparison.

## Temporal reader (#30)

From the experimental worktree root:

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
