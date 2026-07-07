#!/usr/bin/env bash
# scripts/run-neo-bench.sh - Step 26 Neo-vs-Legacy benchmark runner (bash).
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
#   value BENCHFREQ: emits; the ratio itself is unit-free).

set -euo pipefail

DLL="${DLL:-TestCases/bin/Debug/netstandard2.1/TestCases.dll}"
PATCH="${PATCH:-HotfixAOT/Patched/HotfixAOT.patch}"
FREQ="${FREQ:-10000000}"

# Resolve repo root (parent of this script's dir).
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DLL_PATH="$REPO_ROOT/$DLL"
PATCH_PATH="$REPO_ROOT/$PATCH"

if [ ! -f "$DLL_PATH" ]; then
    echo "ERROR: TestCases.dll not found at $DLL_PATH." >&2
    echo "Build it first: dotnet build TestCases/TestCases.csproj -c Debug" >&2
    exit 1
fi

# run_bench <config> -> echoes "name ticks" lines on stdout.
run_bench() {
    local config="$1"
    dotnet run -c "$config" -f net8.0 --project ILRuntimeTestCLI --no-build -- \
        "$DLL_PATH" "$PATCH_PATH" true NeoBenchProbe 2>&1 \
        | grep -Eo "^BENCH:[A-Za-z]+:[0-9]+:[0-9]+" \
        | sed -E "s/^BENCH:([A-Za-z]+):[0-9]+:([0-9]+)/\1 \2/" \
        | awk '{ last[$1]=$2 } END { for (k in last) print k, last[k] }'
}

echo "Building CLI (Debug_Neo)..." >&2
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo >&2 2>&1 || true
echo "Building CLI (Debug)..." >&2
dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug >&2 2>&1 || true

echo "Running Neo (Debug_Neo, ExecuteNeo) bench..." >&2
NEO_OUT="$(run_bench Debug_Neo)"
echo "Running Legacy (Debug + useRegister=true, ExecuteR) bench..." >&2
LEG_OUT="$(run_bench Debug)"

# Load into associative arrays.
declare -A NEO LEG
while read -r name ticks; do [ -n "$name" ] && NEO[$name]=$ticks; done <<< "$NEO_OUT"
while read -r name ticks; do [ -n "$name" ] && LEG[$name]=$ticks; done <<< "$LEG_OUT"

echo ""
echo "Step 26 Neo-vs-Legacy benchmark (freq=$FREQ ticks/sec; ratio = neo_ms/legacy_ms)"
printf "%-18s %12s %12s %10s\n" "name" "neo_ms" "legacy_ms" "ratio"
printf '%*s\n' 55 "" | tr ' ' '-'
ms_div=$(( FREQ / 1000 ))
missing=0
for n in FieldAccess MethodCall ValueType VirtualDispatch Array; do
    neo_ticks="${NEO[$n]:-}"
    leg_ticks="${LEG[$n]:-}"
    if [ -z "$neo_ticks" ] || [ -z "$leg_ticks" ]; then
        missing=1
        neo_ms="MISSING"; leg_ms="MISSING"; ratio="N/A"
        [ -n "$neo_ticks" ] && neo_ms="$(awk "BEGIN{printf \"%.2f\", $neo_ticks/$ms_div}")"
        [ -n "$leg_ticks" ] && leg_ms="$(awk "BEGIN{printf \"%.2f\", $leg_ticks/$ms_div}")"
    else
        neo_ms="$(awk "BEGIN{printf \"%.2f\", $neo_ticks/$ms_div}")"
        leg_ms="$(awk "BEGIN{printf \"%.2f\", $leg_ticks/$ms_div}")"
        ratio="$(awk "BEGIN{printf \"%.3f\", $neo_ticks/$leg_ticks}")"
    fi
    printf "%-18s %12s %12s %10s\n" "$n" "$neo_ms" "$leg_ms" "$ratio"
done
echo ""
echo "NOTE: this host is NOT the perf baseline host. The ratio is the" >&2
echo "signal (only meaningful on a dedicated baseline host); absolute ms" >&2
echo "are noise across machines/load." >&2

exit $missing
