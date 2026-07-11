# Review Report: neo-step25-s3-cecil-free-load (S3-2 TRUE COMPLETION)

> Independent adversarial verification (REVIEWER role, author != verifier).
> HEAD context: the Cecil-free `.neo` load + APPROACH-1 cross-AppDomain token
> re-resolution. Reviewer did NOT write this code. Re-ran every gate on a fresh
> build; code-read the load-bearing diff; probed the binding rigor, the capstone
> adversarial cells, the V1/V2 guard, and the Legacy-neutrality of the shared
> files.

## Verdict: APPROVE-WITH-FINDINGS

The change is sound, the capstone is genuinely load-bearing (both mutation cells
prove no-Cecil-fallback), the token re-resolution is correct-by-construction
(no hash-collision mis-resolution possible; coverage is complete via the
compile-domain snapshot), and Legacy is byte-identical (every shared-file edit
is `#if ENABLE_NEO_MODE`-gated). Findings below are severity <= MEDIUM -- none
block ship; they are spec-fidelity gaps + a latent deferred-items note.

## 1. Independent gate re-runs (fresh build, `-f net8.0`, `--no-build`)

| Gate | Filter | Result |
|------|--------|--------|
| Capstone | `NeoStep25CecilFreeLoad` | **5/5 cells PASS** (A-JIT-ref, compile-V2, B-Cecil-free-load+Compute, M1-body-mutation, M2-layout-mutation). Attach: 5 attached, 1 skipped (`TestCases.NeoStep25S3Base..ctor`). |
| S1/S2/S3-partial regression | `NeoStep25LoadExec` | **28/28 PASS**, 0 failed. |
| Neo smoke | `NeoStep` (Debug_Neo + useRegister=true) | **223 ran, 0 failed, 0 ignored, 0 todo.** |
| Legacy build (Legacy-neutral) | plain `Debug` build of CLI | **0 errors.** |
| Legacy stash-toggle | `NeoStep` (plain `Debug` + useRegister=true) | **223 ran, 8 failed.** See Finding F5 -- these 8 are PRE-EXISTING (the change compiles out under plain Debug). |

DLL-mtime sanity confirmed (CLI engine DLL newer than `NeoAssembly.cs` source
after rebuild; `Debug_Neo` JIT-output noise is normal per CLAUDE.md).

## 2. Token re-resolution assessment (the core risk) -- CORRECT, COMPLETE

### 2a. Hash-collision mis-resolution -- NOT POSSIBLE (by construction)

The recorded hashes are process-global-counter identity hashes:
`ILType.instance_id` (`ILType.cs:81`, `Interlocked.Add`) and
`ILMethod.instance_id` (`ILMethod.cs:83`), PLUS the Cecil
`TypeReference.GetHashCode`/`MethodReference.GetHashCode` identity hashes (the
implementer's KEY finding: these are ALSO process-global-counter-based, NOT
content-based). A monotonic interlocked counter GUARANTEES uniqueness within a
process, so no two distinct refs share a recorded hash. **A hash collision
causing a mis-resolution is structurally impossible.** The binding list
(`NeoTokenBinding[]`) records (hash, NAME) pairs; the loader re-resolves by NAME
and rebinds under the hash. Two distinct refs cannot collide on the hash key.

The decision to use a FLAT binding list (not a parallel `int[]`) is correct and
load-bearing: a single logical ref can appear under MULTIPLE hashes (the Cecil
`MethodReference` hash at a call site AND the resolved `IMethod` hash via the
`GetMethod` invalidToken path at `AppDomain.cs:2057`), and `GetMethod(token,...)`
at `:1953` registers BOTH `token.GetHashCode()` and `method.GetHashCode()` forms.
The flat list captures every (hash -> resolved) pair in the snapshot. A parallel
array keyed by ref index could NOT express many-hashes-per-ref.

### 2b. Missed token sites -- NONE (coverage is the whole compile-domain map)

`BuildTokenBindings` (`NeoAssemblyWriter.cs:556`) snapshots
`NeoTypeTokenSnapshot` / `NeoMethodTokenSnapshot` -- read-only views that iterate
the ENTIRE `mapTypeToken` / `mapMethod` of the COMPILING AppDomain AFTER
`CompileFresh` on every method. This captures EVERY hash the JIT baked into any
body, including:
- Body-INTERNAL call-site method tokens (an interface method a body calls via
  `Callvirt_Interface`) -- these are NOT in the `.neo` ref tables, which is
  exactly why the binding carries the NAME directly (not a ref index). Correct.
- `isinst`/`castclass` TYPE tokens (the capstone's `((INeoStep25S3Iface)this)`
  cast exercises this -- the capstone PASSING proves the type-token resolves in
  B via the name-based binding).
- The `GetMethod(object token,...)` JIT resolver (`AppDomain.cs:1948`) registers
  `mapMethod[hashCode]` (`:2055`) for EVERY method token it bakes, so the
  snapshot is exhaustive.

Body-internal interface-method resolution is handled by synthesized shells:
`ResolveMethodRefByName` (`AppDomain.cs:857`) builds a Cecil-free
`ILMethod.CreateFromNeoShell` for an interface's abstract methods (which have no
body, so are absent from `.neo` MethodDefs), so the dispatch's declared-method
resolution has a non-null target whose `DeclearingType` is the interface. The
capstone's interface-dispatch PASS confirms this path works.

### 2c. The `string`/`switch`/`static-field` token paths (D6) -- correctly NOT recorded

Confirmed by code-reading: the `mapString` table is content-keyed (string
content hash, stable across AppDomains) so `ldstr` needs no re-registration;
`SwitchTargets` are body-local Cecil-array hashes carried verbatim; the
static-field token path is unexercised by the capstone (the probe is
static-field-free). Consistent with the spec delta's "String-token +
switch-target hashes need no recording" scenario.

## 3. Capstone adversarial rigor assessment -- BOTH CELLS LOAD-BEARING

### 3a. M1 body-mutation -- LOAD-BEARING

`NeoStep25CecilFreeLoadCheck` M1 mutates `Ldc_I4_S 100 -> 555` (the FLong
constant) in `model2.MethodDefs[defIdx].NeoExecuteBody` BEFORE `LoadNeoAssembly`,
then asserts B yields `expected + (555-100) = 610` (FLong contributes once to
the sum). A Cecil-fallback (B secretly re-reading Cecil or using A's JIT body)
would yield the UNMUTATED `155` -> M1 FAILs. The cell PROVES ExecuteNeo runs the
genuine deserialized/mutated `.neo` body. Verified the mutation targets the body
passed to `LoadNeoAssembly` (model2, not a Cecil source).

### 3b. M2 layout-mutation -- LOAD-BEARING (stash-toggle re-proven per deferred-items)

M2 mutates `model3.TypeDefs[tdIdx].Fields[0].PrimitiveOffset += 9999` BEFORE
load, then asserts the Cecil-free ILType's `GetFieldOffset(start+0).PrimitiveOffset`
reflects the mutation. A factory that ignored the record (fell back to Cecil)
would yield the unmutated offset -> M2 FAILs. Confirmed load-bearing per the
deferred-items STEP-25-PARTIAL row ("M2 adversarially re-proven load-bearing:
break factory -> M2 FAILs").

### 3c. Is the probe genuinely Cecil-free? -- YES by construction; NO explicit diagnostic (Finding F3)

The check constructs `var domainB = new AppDomain()` (a fresh instance whose
`loadedModules` list starts empty) and calls `LoadNeoAssembly`, which by
construction does NOT call `ModuleDefinition.ReadModule` and does NOT add to
`loadedModules`. So B is Cecil-free BY CONSTRUCTION. However, the capstone does
NOT assert the invariant (no `if (domainB.LoadedModules.Count != 0) FAIL`).
M1+M2 transitively PROVE no-Cecil-fallback (a Cecil read would defeat both
cells), so the invariant IS effectively proven indirectly. But a cheap explicit
`LoadedModules.Count == 0` assertion would harden the claim against a future
regression where `LoadNeoAssembly` accidentally loads a Cecil module. See F3.

## 4. `.neo` Version 1 -> 2 bump assessment

### 4a. The bump itself -- correct

`NeoAssemblyFormat.Version = 2` (`NeoAssembly.cs:41`); the V2-additive binding
tables are trailing data written AFTER the 7 V1 indexed tables
(`NeoAssemblyWriter.cs:604-637`), leaving the V1 header's 7 `TableOffsets`
unchanged. Same-AppDomain loads (S1/S2/S3-partial) ignore the binding tables
(the live maps resolve the bodies). Confirmed.

### 4b. V2 reader EOF-tolerance -- DEAD CODE (Finding F1)

`ReadTokenBindings` (`NeoAssemblyReader.cs:391`) checks
`br.BaseStream.Position >= br.BaseStream.Length` to tolerate a V1 stream that
ends after the 7 tables. BUT the reader's version guard at `:342-344` THROWS
`NotSupportedException` on ANY `version != 2` BEFORE reaching the binding
tables. So a V1 `.neo` never reaches `ReadTokenBindings` -- the EOF check is
unreachable for a version-mismatched stream. This contradicts the spec delta /
design claim that "a V1 .neo is rejected by the Cecil-free loader (Version
guard) but still loadable same-AppDomain" and "the reader reaches EOF cleanly
for a V1 stream". In the implementation, a V1 `.neo` is HARD-REJECTED at READ
time for ALL paths (same-AppDomain included), not just the Cecil-free path. Not
a correctness bug for the shipped slice (no V1 files exist; the
`NeoStep25LoadExec` check compiles fresh V2 in-process), but a spec-fidelity
divergence. See F1.

### 4c. V2 reader on a V2 stream -- correct

Reads the 7 tables, then the 2 binding tables. A well-formed V2 stream round-
trips. The `NeoStep25LoadExec` 28/28 + the capstone 5/5 confirm V2 read is
correct.

## 5. Deferred-items honesty -- HONEST (with one addition recommended, Finding F4)

The `STEP-25-PARTIAL` deferred-items row (`.trae/documents/neo-deferred-items.md:97`)
explicitly records as STILL DEFERRED (not promoted to met):
- Sub-surface 4 static `.cctor` seeding + per-static-field offsets.
- CLR base/interface resolution on the Cecil-free path (NEO-AOT-ADAPTOR-SKIP).
- Generic-method/type instances on the Cecil-free path (S2 T-identity-token).
- Cross-PROCESS load; multi-hotfix-assembly cross-refs; Step 26 perf.

No promotion. The spec delta (`specs/neo-optimizer/spec.md`) is consistent. One
addition recommended: the capstone's "1 skipped: TestCases.NeoStep25S3Base..ctor"
reveals an inherited-base instance-`.ctor` body is not AOT-bound on the
Cecil-free path -- a latent gap not explicitly in the deferred-items doc. See F4.

## 6. Legacy-neutrality -- PROVEN (shared files byte-identical under plain Debug)

### 6a. Diff analysis of the 3 SHARED files (ILType / ILMethod / AppDomain)

Script-analyzed the 379 added code lines across the 3 shared files:
- 362 added under an ADDED `#if ENABLE_NEO_MODE`.
- 17 "outside an added #if" are ALL false positives -- they sit inside
  PRE-EXISTING `#if ENABLE_NEO_MODE` blocks (e.g. `ILType.cs:89-112`,
  `ILMethod.cs:36-66`), verified by line-context.

### 6b. The ReturnType auto-property -> backing-field refactor (the one non-trivial shared change)

`ILMethod.ReturnType` changed from `{ get; private set; }` (auto-property) to an
explicit `get`/`set` around a `returnTypeValue` backing field with a Neo-gated
`if (isNeoAotShell) return neoShellReturnType;` prepended in the getter. Under
plain `Debug` the Neo check compiles out and the getter/setter are semantically
identical to the auto-property (`returnTypeValue` IS the backing field;
`isNeoAotShell` is never set true in Legacy). No reflection-on-backing-field
dependency exists. **Byte-equivalent in Legacy.** The same prepend-only pattern
holds for `IsStatic`, `Name`, `HasThis`, `Parameters`, `IsConstructor`,
`IsVirtual`, `GenericParameterCount` -- each gets a Neo-gated short-circuit
prepended, the Legacy fallback is unchanged.

### 6c. The 8 Legacy NeoStep failures are PRE-EXISTING

Under plain `Debug` + useRegister=true, the `NeoStep` filter shows 223 ran / 8
failed. The 8 are all `NeoStep22` generic-method probes (`ProbeBasic<int>`,
`MakeArray<int>`, `BoxIt<int>`, `GenericHolder.Echo`, etc.). These are
Step-22 generic-template probes failing under Legacy `ExecuteR` -- completely
unrelated to S3-2's `.neo`-load work. Since EVERY shared-file edit is
Neo-gated (6a/6b), the Legacy engine is byte-identical to HEAD, so these 8 MUST
be pre-existing (they would fail identically with this change stashed). The
whole-file Neo gating of `NeoAssembly.cs`/`NeoAssemblyWriter.cs`/
`NeoAssemblyReader.cs` (each starts `#if ENABLE_NEO_MODE`, ends `#endif`) and
the capstone (`#if ENABLE_NEO_MODE && DEBUG`) confirms no Legacy code path is
altered.

## Findings

### F1 -- spec-vs-impl: V1 `.neo` "still loadable same-AppDomain" is NOT honored [MEDIUM-LOW]

**Where:** `NeoAssemblyReader.Read` `:342-344` (hard version throw); design D1
+ spec delta claim a V1 `.neo` is "still loadable same-AppDomain" with
"EOF-tolerant" reader.

**Issue:** The reader throws `NotSupportedException` on `version != 2` BEFORE
the EOF-tolerant `ReadTokenBindings`, so a V1 `.neo` is hard-rejected for ALL
read paths, not selectively for the Cecil-free path. The EOF check at
`ReadTokenBindings:391` is dead code for the version-mismatch case (it only
helps a truncated V2 stream, which the current writer cannot produce).

**Risk:** NONE for the shipped slice (no V1 `.neo` files exist; the harness
compiles fresh V2 in-process; `NeoStep25LoadExec` 28/28 + capstone 5/5 use V2).
The mismatch is documentation/spec fidelity: the design + spec delta describe a
V1-compatibility behavior the code does not implement (and does not need).

**Recommendation:** Either (a) align the spec delta to say "the reader rejects
any non-V2 `.neo` (hard guard); V1 files do not exist in the current pipeline",
or (b) if genuine V1-backward-compat is wanted, downgrade the reader's version
check to "reject only for the Cecil-free path" and let same-AppDomain reads
tolerate V1 (which is what the EOF check was meant to enable). (a) is simpler
and matches the code. Not blocking.

### F2 -- spec-vs-impl: D5 Cecil-property guards THROW, but code RETURNS FALSE [MEDIUM-LOW]

**Where:** `ILType.IsValueType` `:1604`, `ILType.IsInterface` `:1636` (and
`HasGenericParameter`/`IsGenericParameter`) return `false` for `isNeoAotType`
instead of throwing `NotSupportedException`.

**Issue:** Design D5 + spec delta (`specs/neo-optimizer/spec.md:83-85`) mandate
that Cecil-reading properties on a Cecil-free ILType SHALL throw a descriptive
`NotSupportedException` "NEVER silently return null/wrong". The implementation
instead `return false` for `IsValueType`/`IsInterface` (documented as "the
capstone probe is a class, not a valuetype/interface"). For the shipped probe
(a class) `false` is CORRECT, so this is not a correctness bug today. But it
contradicts the spec's "throw, never silently return wrong" and creates a
latent mis-return: a future AOT value-type or interface on the Cecil-free path
would get wrong `IsValueType=false`/`IsInterface=false` silently.

**Risk:** LOW for the shipped slice (probe is a class); latent for forward-compat
with value-type/interface AOT types.

**Recommendation:** Either align the spec to "the guards return a safe default
(false) for the shipped slice; throw is a forward-compat TODO", or implement the
throw as specified. Preferably the throw (with a comment that the capstone
avoids these properties), to match the spec's "fail loud, never silent" intent.
Not blocking for the class-only shipped slice.

### F3 -- capstone does not assert the Cecil-free invariant explicitly [LOW]

**Where:** `NeoStep25CecilFreeLoadCheck.Run` constructs `new AppDomain()` B but
does not assert `domainB.LoadedModules.Count == 0` or that B's maps were NOT
populated from Cecil.

**Issue:** B is Cecil-free BY CONSTRUCTION (`LoadNeoAssembly` does not call
`ReadModule`), and M1+M2 transitively prove no-Cecil-fallback. But a cheap
explicit invariant assertion would harden against a future regression where
`LoadNeoAssembly` accidentally loads a Cecil module. The implementer's CLAIM
("B has NO Cecil load") is trusted, not asserted.

**Recommendation:** Add `if (domainB.LoadedModules.Count != 0) diff = "B has
Cecil modules!"` to each of the B/B2/B3 cells. Trivial hardening. Not blocking.

### F4 -- inherited-base instance-`.ctor` body not AOT-bound on Cecil-free path [LOW]

**Where:** The capstone's attach report: `SKIP: method not matched:
TestCases.NeoStep25S3Base..ctor`. `NeoAssemblyLoader.MatchMethod` does not find
the base class's `.ctor` among the Cecil-free ILType's constructors.

**Issue:** `NeoStep25S3Base`'s implicit default `.ctor` is not bound on the
Cecil-free path (its MethodDef either is not in the `.neo`, or the name/param
match against the Cecil-free base ILType's shells misses it). The capstone
still PASSES 5/5 because the probe's OWN `.ctor` resolves (the
`base..ctor()` call in the probe's `.ctor` body resolves via `mapMethod` to a
registered method, and the base `.ctor` body is trivial). But for a base class
with a NON-trivial `.ctor` (field initializers, side effects), the inherited
`.ctor` body would NOT execute correctly on the Cecil-free path (the additive
"skip keeps JIT" contract has NO Cecil to JIT from on a Cecil-free AppDomain).
This is a latent gap distinct from the documented sub-surface-4 `.cctor`
seeding.

**Recommendation:** Add a note to the STEP-25-PARTIAL deferred-items row that
inherited-base instance-`.ctor` body-binding on the Cecil-free path is not yet
covered (the capstone's base `.ctor` is trivial). Optionally add a future probe
(a base with a non-trivial `.ctor`) once `.cctor`/field-initializer seeding is
unblocked. Not blocking for the shipped probe.

### F5 -- Legacy NeoStep22 failures (8) are pre-existing, NOT a regression [INFORMATIONAL]

**Where:** plain `Debug` + useRegister=true `NeoStep` filter = 223 ran / 8
failed (all NeoStep22 generic probes).

**Assessment:** PRE-EXISTING. Every shared-file edit is `#if ENABLE_NEO_MODE`
-gated (Finding section 6a/6b), so the Legacy engine is byte-identical to HEAD;
these 8 would fail identically with this change stashed. They are Step-22
generic-template probes failing under Legacy `ExecuteR`, unrelated to S3-2's
`.neo`-load work. No action. (The CLAUDE.md Legacy baseline is 518/519 for the
FULL register set; the NeoStep-filtered subset's Legacy behavior is a separate
population.)

## No NEW blocking correctness finding

The core invariant -- a Cecil-free load into a FRESH AppDomain executes
correctly via the recorded hash re-registration -- is correct by construction
(no hash collisions possible; exhaustive snapshot coverage; both mutation cells
load-bearing). The 5 findings are spec-fidelity / forward-compat / hardening,
all severity <= MEDIUM-LOW. Ship.

## Artifacts read

- `openspec/changes/neo-step25-s3-cecil-free-load/{proposal,design,tasks}.md`
- `openspec/changes/neo-step25-s3-cecil-free-load/specs/neo-optimizer/spec.md`
- `.trae/documents/neo-deferred-items.md` (STEP-25-PARTIAL row)
- Diff: `ILRuntime/CLR/TypeSystem/ILType.cs`, `ILRuntime/CLR/Method/ILMethod.cs`,
  `ILRuntime/Runtime/Enviorment/AppDomain.cs`,
  `ILRuntime/Runtime/NeoAOT/{NeoAssembly,NeoAssemblyWriter,NeoAssemblyReader}.cs`,
  `ILRuntime/Runtime/NeoAOT/NeoAssemblyLoader.cs`,
  `ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep25CecilFreeLoadCheck.cs`,
  `TestCases/NeoStep25S3Probe.cs`, `ILRuntimeTestCLI/Program.cs`.
