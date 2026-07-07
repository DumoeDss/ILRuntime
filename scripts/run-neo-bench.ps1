# scripts/run-neo-bench.ps1 - Step 26 Neo-vs-Legacy benchmark runner (Windows).
#
# Runs the 5 bench workloads (TestCases/NeoStep26BenchProbe.cs) under BOTH
# engines on the SAME TestCases.dll and prints a per-bench
#   name | neo_ms | legacy_ms | ratio
# table.
#
# HOW IT WORKS
#   The bench probe methods are untagged public-static-parameterless methods, so
#   the generic test loop (ILRuntimeTestBase/TestBase/BaseTestUnit.Invoke) runs
#   them under BOTH engines. Each probe method self-emits one
#   `BENCH:<name>:<iters>:<ticks>` line (via per-primitive Console.Write, which
#   avoids the Neo interpreter string-concat gap). This script runs the CLI
#   twice (Debug_Neo for ExecuteNeo, plain Debug + useRegister=true for
#   ExecuteR) with the `NeoBenchProbe` Contains-filter, parses the BENCH: lines,
#   and joins them by name.
#
#   The `NeoStep26Bench` host-side self-check
#   (NeoStep26BenchCheck.Run, gated #if ENABLE_NEO_MODE && DEBUG) is a SEPARATE
#   verification artifact: it host-times each bench via appdomain.Invoke, asserts
#   the returned primitive via the divide-assert correctness-of-measurement gate,
#   and emits its own BENCH: lines. Run it separately with the `NeoStep26Bench`
#   filter; THIS script uses the `NeoBenchProbe` filter so the probe self-emit is
#   the single timing source under both engines (symmetric, apples-to-apples).
#
# CAVEAT (spec design.md section 3.3): the development host is NOT the perf
#   baseline host. The ABSOLUTE timings are noise across machines/load; the RATIO
#   is the signal, and only on a dedicated baseline host. A green run here proves
#   measurement works + the benches computed their expected values -- NOT that
#   Neo is fast. The ticks->ms conversion uses Stopwatch.Frequency = 10^7 (the
#   value BENCHFREQ: emits on Windows; the ratio itself is unit-free).

param(
    [string]$Dll = "TestCases/bin/Debug/netstandard2.1/TestCases.dll",
    [string]$Patch = "HotfixAOT/Patched/HotfixAOT.patch",
    [double]$Freq = 10000000.0
)

$ErrorActionPreference = "Continue"
$repoRoot = Split-Path -Parent $PSScriptRoot
$dllPath = Join-Path $repoRoot $Dll
$patchPath = Join-Path $repoRoot $Patch

if (-not (Test-Path $dllPath)) {
    Write-Error "TestCases.dll not found at $dllPath. Build it first: dotnet build TestCases/TestCases.csproj -c Debug"
    exit 1
}

function Run-Bench {
    param([string]$Config)
    # useRegister=true is arg[2]; the NeoBenchProbe filter selects the 5 probes.
    $runArgs = @("run", "-c", $Config, "-f", "net8.0", "--project", "ILRuntimeTestCLI", "--no-build", "--",
              $dllPath, $patchPath, "true", "NeoBenchProbe")
    # BENCH: lines are on stdout; discard stderr (2>$null, NOT 2>&1 -- the latter
    # wraps native-command stderr in ErrorRecords under Windows PowerShell 5.1).
    $out = & dotnet @runArgs 2>$null | Out-String
    $map = @{}
    foreach ($line in $out -split "`r?`n") {
        if ($line -match "^BENCH:(\w+):(\d+):(\d+)\s*$") {
            $name = $Matches[1]
            $ticks = [long]$Matches[3]
            # Last-wins: if a bench emits multiple BENCH: lines, the final one
            # is the completed measurement.
            $map[$name] = $ticks
        }
    }
    return $map
}

Write-Host "Building CLI (Debug_Neo)..." -ForegroundColor Cyan
& dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo 2>$null | Out-Null
Write-Host "Building CLI (Debug)..." -ForegroundColor Cyan
& dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug 2>$null | Out-Null

Write-Host "Running Neo (Debug_Neo, ExecuteNeo) bench..." -ForegroundColor Cyan
$neo = Run-Bench -Config "Debug_Neo"
Write-Host "Running Legacy (Debug + useRegister=true, ExecuteR) bench..." -ForegroundColor Cyan
$legacy = Run-Bench -Config "Debug"

$names = @("FieldAccess", "MethodCall", "ValueType", "VirtualDispatch", "Array")

Write-Host ""
Write-Host "Step 26 Neo-vs-Legacy benchmark (freq=$Freq ticks/sec; ratio = neo_ms/legacy_ms)" -ForegroundColor Yellow
$header = "{0,-18} {1,12} {2,12} {3,10}" -f "name", "neo_ms", "legacy_ms", "ratio"
Write-Host $header
Write-Host ("-" * $header.Length)
$missing = $false
foreach ($n in $names) {
    $hasNeo = $neo.ContainsKey($n)
    $hasLeg = $legacy.ContainsKey($n)
    if (-not $hasNeo -or -not $hasLeg) {
        $missing = $true
        $neoCell = if ($hasNeo) { ($neo[$n] / ($Freq / 1000)).ToString("F2") } else { "MISSING" }
        $legCell = if ($hasLeg) { ($legacy[$n] / ($Freq / 1000)).ToString("F2") } else { "MISSING" }
        $ratioCell = "N/A"
    } else {
        $neoCell = ($neo[$n] / ($Freq / 1000)).ToString("F2")
        $legCell = ($legacy[$n] / ($Freq / 1000)).ToString("F2")
        $ratioCell = ($neo[$n] / [double]$legacy[$n]).ToString("F3")
    }
    Write-Host ("{0,-18} {1,12} {2,12} {3,10}" -f $n, $neoCell, $legCell, $ratioCell)
}
Write-Host ""
Write-Host "NOTE: this host is NOT the perf baseline host. The ratio is the" -ForegroundColor DarkGray
Write-Host "signal (only meaningful on a dedicated baseline host); absolute ms" -ForegroundColor DarkGray
Write-Host "are noise across machines/load." -ForegroundColor DarkGray

if ($missing) { Write-Warning "One or more benches were MISSING from a config (see table)."; exit 1 }
exit 0
