## 1. Current-state assessment (probe BEFORE fixing)

- [x] 1.1 Build the CLI `Debug_Neo` + TestCases `Debug --no-incremental`; verify
  NeoStep smoke 175/175 green at HEAD (baseline confirmation).
- [x] 1.2 Construct a Stobj-of-VT-with-ref-field probe (`struct S { int x; string
  s; }`, `ldloca dst; ldloc src; stobj S`), pre-set the dest ref slot to a
  non-null canary, run it on HEAD, record the failure mode (silent stale-canary
  kept -- the dest ref slot NOT overwritten).
- [x] 1.3 Construct a Ldobj-of-VT-with-ref-field probe, run on HEAD, record the
  failure (dest ref slot reads its stale null, not the src ref field).
- [x] 1.4 Construct an IL-VT-with-ref-fields constrained direct-call probe (a
  struct `{int id; string tag;}` overriding `ToString()` that reads `this.tag`;
  invoke via `constrained.callvirt`), run on HEAD, record the NIE text (the
  `:3818` tag).
- [x] 1.5 Construct an IL-VT-with-ref-fields constrained inherited-CLRMethod box
  probe (same struct, no override; `$"{s}"`), run on HEAD, record the NIE (the
  `:3876` tag).

## 2. Stobj/Ldobj ref-region copy (the green target)

- [x] 2.1 Dump-gate the Stobj/Ldobj operands: add a temporary
  `Console.WriteLine` inside each arm for the probe method, confirm
  post-lowering `DstOffset`/`SrcOffset` mapping (Stobj: `DstOffset`=address,
  `SrcOffset`=value; Ldobj: reverse). Remove the diagnostic before finalizing.
- [x] 2.2 Implement the frame-native-direct-local ref-region recovery: scan
  `localInfos` for the local whose `Offset == thisByteOff` to recover the
  source/dest `RefOffset`. Confirm `localInfos` is in scope at the arm
  (it is a local in `ExecuteNeo`).
- [x] 2.3 Stobj arm (`ILIntepreter.Neo.cs:3514`): for `ilType.TotalReferenceCount
  > 0`, after the existing primitive `CopyBlock`, add the mStack-to-mStack copy
  of `TotalReferenceCount` ref slots (mirror `Move_Vt`). Frame-native-direct-
  local: recover dst + src ref bases via the scan. IL-instance (`objectIndex >=
  0`): route through `CopyFrameToIL` with the real ref base + refCount. CLR-
  object: unchanged. Nested-field (scan miss): tagged NIE.
- [x] 2.4 Ldobj arm (`:3544`): the mirror of 2.3 (frame-native-direct-local
  recovers src ref base; IL-instance routes through `CopyILToFrame`).
- [x] 2.5 Verify the 1.2 + 1.3 probes now PASS (stash-toggle: FAIL on HEAD, PASS
  after). Run each probe name separately (CLI filter is a `Contains` substring,
  no regex/`|`).

## 3. Constrained arm -- remove the two ref-fields NIEs

- [x] 3.1 Dump-gate the Constrained IL-VT-direct-call callee frame: confirm the
  seed target is `calleeFrame.ParamInfos[0].RefOffset` + the source local's
  `RefOffset` recovers via the same `localInfos` scan (R2).
- [x] 3.2 IL-VT-direct-call path (`:3807-3833`): remove the
  `ilConstrained.TotalReferenceCount > 0` NIE (`:3818`); after the primitive
  `CopyBlock` into slot-0, seed the callee slot-0 ref slots from the recovered
  source-local ref base (`mStack[targetFrameRefBase + thisSlotInfo.RefOffset + i]
  = mStack[frameRefBase + srcLocalRefOffset + i]` for `i in [0,
  TotalReferenceCount)`).
- [x] 3.3 IL-VT-inherited-CLRMethod box path (`:3842-3890`): remove the
  `ilBoxType.TotalReferenceCount > 0` NIE (`:3876`); extend the `CopyFrameToIL`
  call (`:3884-3886`) to pass the real recovered `refOffset` (R2) and
  `refCount = ilBoxType.TotalReferenceCount` (currently `refOffset=0,
  refCount=0`).
- [x] 3.4 Verify the 1.4 + 1.5 probes now PASS (stash-toggle: NIE on HEAD,
  correct result after).

## 4. Adversarial probes (MANDATORY) + regression

- [x] 4.1 Add `NeoStep17_StobjVtWithRefField_OverwritesStaleDestRef` (dest ref
  slot pre-set to a non-null canary; assert the dest ref slot is the NEW value
  after stobj, not the canary).
- [x] 4.2 Add `NeoStep17_LdobjVtWithRefField_ReadsSrcRefNotStaleNull` (dest ref
  slot null; assert the dest ref slot is the src's non-null object after
  ldobj).
- [x] 4.3 Add `NeoStep17_NestedVtWithRefField_Stobj` (a nested-field-byref
  shape -- dump-gate whether it hits the NIE or accidentally resolves; tighten
  the scan to match only local bases if needed; document in the ship log).
- [x] 4.4 Add `NeoStep17_ConstrainedIlVtWithRefFields_DirectCall` (struct
  `{int id; string tag;}` overriding `ToString()` reading `this.tag`; assert
  the override sees the correct `tag`).
- [x] 4.5 Add `NeoStep17_ConstrainedIlVtWithRefFields_InheritedClrMethod`
  (same struct, no override; `$"{s}"` / `GetHashCode()` does not crash and
  reflects the ref field where observable).
- [x] 4.6 Regression: primitives-only Stobj/Ldobj probes still pass; the
  step17-completion probes (K1-K7 + the implementer's 6) still green; full
  NeoStep smoke green.
- [x] 4.7 Legacy-neutral: build plain `Debug` CLI + `useRegister=true`, run the
  `NeoStep17_` filter on Legacy; confirm the new probes pass on Legacy too
  (Legacy Stobj/Ldobj/Constrained is the REFERENCE) and the 175/175 NeoStep-
  filter baseline holds.

## 5. Build / verify discipline (earned gotchas)

- [x] 5.1 After EVERY TestCases edit, rebuild with `--no-incremental` and
  verify the DLL mtime > source mtime (stale-DLL false-failure gotcha).
- [x] 5.2 After adding any host type (a new struct/class the test references),
  rebuild the CLI `Debug_Neo` `--no-incremental` (the earned host-type gotcha).
- [x] 5.3 ALWAYS run the CLI with `-f net8.0`; NEVER build TestCases with
  `Debug_Neo`; kill + investigate any probe taking >10s (interpreter infinite
  loop).
- [x] 5.4 Run `openspec validate neo-step17-stobj-refloop --strict` before
  handoff; resolve any delta/spec format errors (scenario hashtags = exactly
  4).

## 6. Ship + archive prep

- [ ] 6.1 Write `review-report.md` (verify stage) + `ship-log.md` (ship stage):
  scope shipped, probes FAIL-on-HEAD -> PASS-after, stash-toggle proofs,
  accepted-known edges (nested-field NIE; CLR-VT-with-refs-no-binder), Legacy-
  neutral confirmation.
- [ ] 6.2 Update `.trae/documents/neo-deferred-items.md`: D-CONSTRAINED (b) +
  IL-VT-with-ref-fields constrained sub-case -> RESOLVED (this change); (c)
  edges stay deferred (the tagged NIEs remain).
- [ ] 6.3 LEAD commits + pushes (`Neo step 17 stobj-refloop:` prefix) with the
  Co-Authored-By trailer; archive moves the delta into
  `openspec/specs/neo-byref/spec.md` and relocates the change directory to
  `openspec/changes/archive/`.
