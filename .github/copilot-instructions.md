# Copilot Instructions

## General Guidelines
- Run terminal commands in the foreground (background=false) rather than as background tasks; waiting on background terminals wastes time.

## Project Guidelines
- When building packages in the Typewriter repo, output should go to `artifacts/packages` (the canonical location used by CI, release.yml, README, and docs/packing_plan.md), not the `artifacts` default that the Build-*.ps1 scripts use. Pass `-OutputDirectory artifacts/packages` explicitly when running the packaging scripts.

## Building BenchmarkDotNet Suites in This Repo

Getting a BenchmarkDotNet project to run against the Typewriter engine takes four non-obvious fixes. All four are required; each was found the hard way. Copy the template below rather than starting from the `create_benchmark` default.

### Why it is awkward

`Directory.Build.props` applies repo-wide and does two things that break benchmark projects:

1. Enables the full analyzer stack with `TreatWarningsAsErrors`, so a throwaway perf harness fails to build on style rules (SA1516, MA0003, VSTHRD200, S1075, CC0061, ...).
2. References `Microsoft.CodeAnalysis.*` as *analyzers* with `ExcludeAssets="runtime"` and `PrivateAssets="all"`, so Roslyn runtime assemblies are neither copied to output nor flowed to consumers. The engine needs them at run time and fails with:
   `FileNotFoundException: Could not load file or assembly 'Microsoft.CodeAnalysis, Version=5.6.0.0'`

### The four required fixes

1. **Disable the analyzer stack.** Blank out `CodeAnalysisRuleset` and `ErrorLog` too, not just `RunAnalyzers` — `ErrorLog` writes a SARIF file per project and will otherwise collide.
2. **Set `CopyLocalLockFileAssemblies=true`.** Matches `Typewriter.Cli`; without it the transitive Roslyn graph is not fully copied.
3. **Re-reference Roslyn with `Remove` then `Include`, pinned to the same version the engine uses (currently 5.6.0), with `PrivateAssets="none"`.** `Remove` alone does not clear the inherited `ExcludeAssets`/`PrivateAssets` metadata. Mirror the pattern already used by `Typewriter.Cli` and `Typewriter.Roslyn`. Do NOT `Include` a different version (e.g. 4.14.0) — it produces NU1504 duplicate warnings and risks running against a different Roslyn than the engine was compiled against.
4. **Add an `AssemblyResolve` handler in `[GlobalSetup]`.** This is the one that is impossible to guess. Fixes 1-3 are necessary but *not sufficient*.

### Why fix 4 is needed even after fixes 1-3

BenchmarkDotNet compiles each job into a **separate generated project** (e.g. `BenchmarkSuite1-Job-APMJEU-1`) that sets `ImportDirectoryBuildProps=false` and references your benchmark project only via `ProjectReference`. That generated project therefore never sees the repo's package references. Verified symptom: the Roslyn DLLs *are* physically present in the job's output directory, but the job's `deps.json` `runtime` section lists only `Microsoft.CodeAnalysis.VisualBasic.dll`. `Microsoft.CodeAnalysis.dll` and `Microsoft.CodeAnalysis.CSharp.dll` exist as files but are unknown to the host, so the load still fails. Probing the executable's own directory sidesteps the `deps.json` gap without changing what is measured.

### Diagnosing this class of failure

The failure surfaces inside the spawned child process, where the exception is hidden. Useful commands, in order:
# 1. Surface the real exception (BDN hides child-process errors)
dotnet run -c Release --no-build -- --filter '*MyBench*' --job dry --inProcess

# 2. Confirm what MSBuild actually resolved in YOUR project (expect PrivateAssets: none)
dotnet msbuild MyBench.csproj -p:Configuration=Release -getItem:PackageReference

# 3. Confirm what the GENERATED JOB actually registered - this is the real check
$g = Get-ChildItem 'bin\Release\net10.0' -Directory -Filter '*-Job-*' | Select-Object -First 1
$dj = Get-ChildItem "$($g.FullName)\bin\Release\net10.0\*.deps.json" | Select-Object -First 1
(Get-Content $dj.FullName -Raw | ConvertFrom-Json).targets.PSObject.Properties.Value.PSObject.Properties.Name |
    Where-Object { $_ -like '*CodeAnalysis*' } | Select-Object -Unique
Note: BenchmarkDotNet **reuses** the generated job directory when the job hash is unchanged, so a stale `deps.json` can make a working fix look broken. Delete `bin` and `obj` before re-testing.

### Copyable csproj template
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFrameworks>net10.0</TargetFrameworks>
    <OutputType>Exe</OutputType>
    <Nullable>enable</Nullable>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <!-- Fix 1: the repo-wide analyzer stack and warnings-as-errors only obstruct a perf harness. -->
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <RunAnalyzers>false</RunAnalyzers>
    <RunAnalyzersDuringBuild>false</RunAnalyzersDuringBuild>
    <EnableNETAnalyzers>false</EnableNETAnalyzers>
    <CodeAnalysisRuleset></CodeAnalysisRuleset>
    <ErrorLog></ErrorLog>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
    <!-- Fix 2: matches Typewriter.Cli; without it the Roslyn graph is not fully copied. -->
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="BenchmarkDotNet" Version="0.15.*" />
    <PackageReference Include="Microsoft.VisualStudio.DiagnosticsHub.BenchmarkDotNetDiagnosers" Version="18.7.37220.1" />
    <!-- Fix 3: restore Roslyn as real runtime references, pinned to the engine's version. -->
    <PackageReference Remove="Microsoft.CodeAnalysis" />
    <PackageReference Remove="Microsoft.CodeAnalysis.Common" />
    <PackageReference Remove="Microsoft.CodeAnalysis.CSharp" />
    <PackageReference Remove="Microsoft.CodeAnalysis.CSharp.Workspaces" />
    <PackageReference Remove="Microsoft.CodeAnalysis.Workspaces.Common" />
    <PackageReference Include="Microsoft.CodeAnalysis.Common" Version="5.6.0" PrivateAssets="none" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="5.6.0" PrivateAssets="none" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="5.6.0" PrivateAssets="none" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\src\Typewriter.Engine\Typewriter.Engine.csproj" />
    <ProjectReference Include="..\src\Typewriter.Abstractions\Typewriter.Abstractions.csproj" />
    <ProjectReference Include="..\src\Typewriter.Roslyn\Typewriter.Roslyn.csproj" />
  </ItemGroup>
</Project>
### Copyable AssemblyResolve handler (fix 4)
[GlobalSetup]
public void Setup()
{
    // BenchmarkDotNet compiles each job into a separate generated project that sets
    // ImportDirectoryBuildProps=false, so it never sees the repo's Microsoft.CodeAnalysis
    // package references. The Roslyn DLLs are copied next to the job executable, but the job's
    // deps.json only registers Microsoft.CodeAnalysis.VisualBasic in its runtime section, so
    // the engine fails with FileNotFoundException for 5.6.0 at run time. Resolving them from
    // the executable's own directory sidesteps the gap without altering what is measured.
    AppDomain.CurrentDomain.AssemblyResolve += static (_, args) =>
    {
        var simpleName = new AssemblyName(args.Name).Name;
        if (simpleName is null || !simpleName.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal))
        {
            return null;
        }

        var probeDirectory = Path.GetDirectoryName(typeof(MyBenchmarks).Assembly.Location);
        if (probeDirectory is null)
        {
            return null;
        }

        var candidate = Path.Combine(probeDirectory, simpleName + ".dll");
        return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
    };

    // ... rest of setup
}
### Interpreting results: beware static caches

`CSharpProjectMetadataProvider` holds **static** caches (`MetadataCache`, `SyntaxTreeCache`). Once one iteration loads a workspace, later iterations reuse it, so BenchmarkDotNet measures the warm path only. Observed on a workload that takes ~53s from the CLI:
WorkloadWarmup 1:  5.9191 s/op   <- still warmer than a real cold run
WorkloadResult 1:  1.3252 s/op
WorkloadResult 2:  1.1344 s/op
WorkloadResult 3:  1.2759 s/op
The reported 1.245 s is ~2% of real wall-clock. Workspace loading is a **cold-start** cost, and BenchmarkDotNet's repeat-until-stable model structurally cannot measure it. For cold-start work, time the CLI process directly (e.g. `Measure-Command` per phase, or a no-op template to isolate load cost from render cost) and use BenchmarkDotNet only for the warm render path. Always sanity-check a benchmark number against a real end-to-end measurement before optimizing against it.

### Worked example: a 7x warm-path win that was worth nothing end to end

This is not hypothetical. Memoising the code-model adapter factories (`TemplateCodeModelAdapterFactory`) produced a clean, reproducible BenchmarkDotNet result:
before caching:  2.368 s  (+/- 0.057 s, 15 iterations)
after caching:   0.318 s  (+/- 0.010 s, 15 iterations)
End-to-end wall clock for `RegenAnavasi.ps1` went from **~54 s to ~53 s**. Essentially nothing.

Phase split of a cold CLI run (~52 s), measured by timing the process directly:

| Phase | Time | How it was isolated |
| --- | ---: | --- |
| Workspace/metadata load | ~28 s | template that matches nothing (no-op) |
| Per-template work | ~23 s | real template minus the no-op baseline |
| Template compilation | <1 s | tiny template containing a code block |
| Process startup | ~0.1 s | `--help` |

The ~23 s of cold per-template work is the *same logical operation* the benchmark reports as 318 ms — a ~70x gap. The warm measurement simply never touches whatever dominates the cold path.

Takeaways:
- A large, statistically clean warm-path improvement can be worth **zero** end to end.
- Do not extrapolate a benchmark delta into a predicted script-level number. Measure the script.
- Isolate phases with purpose-built inputs (no-op template, trivial-but-compiling template) rather than reasoning about which phase "should" dominate.
- Keep such an optimization only on its own merits (correctness, avoided work), and say so plainly in the code comments so a future reader does not over-credit it.

### Worked example: a 26% CPU hotspot that was worth 0.6% of wall clock

**This section previously taught the opposite of the truth. It is kept as a cautionary tale about
a broken measurement, not as guidance about source generators.**

A CPU trace of the Anavasi regen attributed ~26% of total CPU to
`CSharpProjectMetadataProvider.RunSourceGenerators(...)`, with
`GeneratorDriver.RunGeneratorsAndUpdateCompilation(...)` at ~25% *self* CPU. A
`Generation.RunSourceGenerators` opt-out was added, measured end to end, and appeared to save only
~170 ms out of ~30.6 s (0.6%). The documented conclusion was that the profiler had misled us and
that source generation was irrelevant to wall clock.

**That conclusion was wrong.** `GenerationConfigurationFile` in `TypewriterConfigurationLoader`
never declared a `RunSourceGenerators` property, so `System.Text.Json` silently dropped the value
from `typewriter.json`. The flag, the schema entry, and all plumbing down to the metadata provider
were correct — the value simply never survived deserialization. **Generators ran in both arms of
the comparison.** The ~170 ms was measurement noise between two identical runs.

After fixing the binding, the same experiment (post-parallel-prefetch baseline):

| Metric | Generators ON | Generators OFF | Delta |
| --- | ---: | ---: | ---: |
| Total generation | 19,255 ms | 15,047 ms | **-4,208 ms (-22%)** |
| Metadata load | 16,491 ms | 11,921 ms | -4,570 ms |
| `roslyn-metadata` | 8,232 ms | 3,764 ms | -4,468 ms |
| `generators-only` | 5,011 ms | 0.9 ms | -5,010 ms |
| `msbuild-load` | 8,065 ms | 7,960 ms | ~0 |

The CPU profiler was right the whole time. Source generation really is roughly a quarter of
end-to-end wall clock on this workload.

Takeaways (revised):
- **Validate that your opt-out flag actually takes effect before trusting a negative result.** A
  no-op toggle produces a perfectly clean, perfectly meaningless A/B comparison. Assert the flag
  reached the code path — log it, break on it, or unit-test the binding.
- A *negative* performance result deserves the same scepticism as a positive one. "Changing X did
  nothing" is most often explained by "X never changed".
- Silently-ignored configuration is a recurring bug class with `System.Text.Json` DTO mirrors: the
  strongly-typed config, the JSON schema, and the loader DTO must all be updated together. There is
  now a regression test (`LoadAsyncBindsRunSourceGenerators`) guarding this specific property.
- CPU share still is not wall-clock share in general, and `TYPEWRITER_TRACE_PERF=1` remains the
  right instrument — but this particular example never demonstrated that.

Note on semantics: disabling generators produced byte-identical output for Anavasi only because
`GetSourceSymbols` enumerates just the syntax trees parsed from disk, so generated types are never
template entities anyway. Generators still affect semantic resolution of hand-written code that
references generated symbols, so the default must remain `true`; the speedup is opt-in.

### Useful measurement switch

`TYPEWRITER_TRACE_PERF=1` makes the CLI emit per-phase timings (`TWPERF: ...`), including workspace resolution, per-project metadata load, template parse/compile, and total generation. This is the fastest way to attribute end-to-end time and should generally be the *first* step for any performance question about the CLI, before reaching for a profiler or BenchmarkDotNet.
