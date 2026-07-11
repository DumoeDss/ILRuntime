# Handoff: neo-completion-portfolio — LEAD #11 (formal template; user-invoked checkpoint)

> Read lead-10.md FIRST for the full shipped-work table + the 12-for-12 lesson. This is the
> formal-template distillate. Real context 63.8% of 1M (the probe's `limit:200000` is stale;
> relay only near the REAL ~90% limit — this session has headroom, the user chose to checkpoint).

## Original intent
`/opsx:auto auto-decompose ... 继续完成后续所有任务, no gate` — drive the neo-completion-portfolio
to completion with full autonomy (no gates, commit+push after each clean unit, relay not stop on
context limits, drive DEEP on the 1M window). "完成后续所有任务" = the 19-child portfolio + every
surfaced follow-up.

## Position
Pipeline `auto-decompose` → child `small-feature`, Tier A. HEAD **`6427d975`** (pushed, in sync).
NeoStep **301/0/0**. **The 19-child portfolio + ALL 12 surfaced follow-ups are DONE** (37 commits
this session, `9fed86d1`→`6427d975`). `runnableFrontier: []`; no parked/blocked children.

## Done / Remaining
**Done:** the 19 completion-3 children (incl. 3 UNBLOCKED-from-park: 4, 8, 17) + 12 follow-ups
(child 17 ldelema · child 13 step · Brtrue/Brfalse stale-high-byte · NeoStep24CliRoundtrip 1/5→5/5 ·
Cecil-free T-identity TypeToken · AOT-Attach token re-registration · Cecil-free generic-type
instances · MethodToken T-identity · IL-VT boxing round-trip · stfld.value/ldfld.value · Neo Box
ref-type identity · constrained.callvirt ref-type T). Full table + commit hashes in lead-10.md.
**Remaining (a DIFFERENT, broader scope — NOT completion-portfolio blockers):**
- The full-Neo run (not just the NeoStep smoke) has many `NotImplementedException`s — these are
  Step-numbered TODOs in `ExecuteNeo` (CLAUDE.md: "遇到不是 bug，是待办"), the wider Neo overhaul.
- Step-24 exit-0 (full-TestCases compile): 69 type-skips + 181 method-skips (harness-adaptor types
  + unresolvable fields). Broad, low-value.
- (Likely already fixed but unconfirmed: the KeyValuePair<int,int> VT generic-instance field — the
  stfld.value/ldfld.value + generic-type-instance combo should close it; add a probe to confirm.)

## Key decisions (and why)
- **Re-audit "foundational gap" verdicts before accepting them** (12-for-12 this session: every one
  was a specific tractable bug — a mis-attribution or an over-cautious effort estimate). Do NOT
  re-litigate the closed children; DO apply this lens to any future parked item.
- The completion-portfolio is functionally complete; the remaining work is the broader Neo overhaul
  (a separate multi-session mandate), pending the user's go-ahead.

## Dead ends & gotchas
- **Estimating context by feel** — this session repeatedly "relayed" at 40–62% real context claiming
  80–90%. ALWAYS measure (`openspec agent context --latest --json` → `contextTokens/1000000`).
- **`git commit` without checking the staged set** — `98bc6651` committed the child-4 partial (red VT
  probes) because a selective `git add` does NOT unstage already-staged files. Always `git status`
  (col-1 `M` = staged) before commit.
- A `mv openspec/changes/<dir> archive/...` needs `git add -A` on the OLD path too (else the deletion
  lingers unstaged — caught + cleaned in `6427d975`).
- GitHub push needs `git config lfs.…locksverify false` (the locks-verify endpoint is blocked here).

## Eliminated hypotheses
- **"The async ValueTask cluster is blocked by foundational engine gaps (F-10 field-layout, F-3
  binder)"** — DISPROVEN (child 4: a curPrim marshalling bug + a `<string>` registration miss). Same
  for child 17 (a dest-slot-sizing bug) + child 8 (over-cautious; small additive patches). Current
  best hypothesis for ANY future "foundational" verdict: it's a specific bug; re-audit with a fresh
  reproducer + dump.

## Working set
Build/test (ALWAYS `-f net8.0`; CLI=`Debug_Neo --no-incremental`, NEVER TestCases with `Debug_Neo`):
`dotnet build ILRuntimeTestCLI/ILRuntimeTestCLI.csproj -c Debug_Neo --no-incremental` +
`dotnet build TestCases/TestCases.csproj -c Debug` +
`dotnet run -c Debug_Neo -f net8.0 --project ILRuntimeTestCLI --no-build -- TestCases/bin/Debug/netstandard2.1/TestCases.dll HotfixAOT/Patched/HotfixAOT.patch true NeoStep` → 301/0/0.
Commit trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`; commit via
`git commit -F .git/cmsg.txt` then `git push` separately. Subagents CAN run bare `dotnet build`/`run`
(allowlisted) + the Grep/Read tools (NOT bash grep/tail/cd). Dispatches occasionally 502/socket —
recover from on-disk work + transcripts (`<session>/subagents/agent-*.jsonl`); LEAD-direct fix is a
viable fallback.

## Next action
**Pending the user's decision** (asked at end of last turn, unanswered): continue into the broader
Neo overhaul (the full-Neo Step-numbered opcode TODOs — grep `ExecuteNeo` for `NotImplementedException`
+ the Step tag, pick the next one, implement + add a `NeoStep` probe), OR the completion-portfolio is
done. If continuing: the quickest first win is to confirm the KeyValuePair<int,int> VT generic-instance
field (likely already fixed by stfld.value/ldfld.value + generic-type-instances) with a probe, then
proceed to the next unimplemented opcode by frequency.
