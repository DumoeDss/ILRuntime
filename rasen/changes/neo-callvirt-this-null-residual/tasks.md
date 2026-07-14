# Tasks -- neo-callvirt-this-null-residual

## 1. Re-audit the 4 tests at the 86-baseline -- DONE
- Built CLI (Debug_Neo, --no-incremental) + TestCases (Debug). 0 errors.
- Ran the full smoke at HEAD: `922 ran / 86 failed`. Confirmed the 4 tests fail
  with `Neo callvirt this is null` at `ResolveNeoCallvirtCLRTarget:1432`.
- Confirmed all 4 PASS on Legacy (plain Debug + useRegister=true).
- Pinned the upstream null producer per test (see design.md). The 4 CONVERGE at
  :1432 but have DISTINCT upstream roots.

## 2. Fix A -- GetTypeNeo redirect -- DONE
- `CLRRedirections.cs`: added `GetTypeNeo` (mirrors Legacy `GetType`).
- `AppDomain.cs`: registered on `RedirectMapNeo` in the static `Type.GetType` loop.
- Fixes ReflectionTest04 + ReflectionTest19.

## 3. Fix B -- route byref-param CLR calls to reflection -- DONE
- `CLRMethod.cs`: added `HasByRefParameter` + `ReflectionCannotHandleThis`.
- `ILIntepreter.Neo.cs` `InvokeNeoClrMethod`: guard becomes
  `redirectNeo != null && (!HasByRefParameter || ReflectionCannotHandleThis)`.
- Fixes StaticTest03 (+ 3 additional stale-stub byref flips in the full smoke).
- Async exemption (`ReflectionCannotHandleThis`) verified -- without it, 22
  NeoStep20 async tests regress with the Area-4b ref-field-struct NIE.

## 4. Verify -- DONE
- Stash-toggle: 3 targets FAIL at HEAD (`this is null`) -> PASS with fix.
- FULL SMOKE diff vs real HEAD run: **86 -> 80 (-6), 6 flipped, 0 regressions**.
- NeoStep: 388/0 (no regression).
- Legacy-neutral: plain Debug build 0 errors.

## 5. NOT fixed (reported)
- DelegateTest43: `ldsflda` IL-static-field byref write-back gap in
  `CopyNeoCallThisBack` (CompareExchange/CAS pattern; child-7 Ldsflda lineage).
- Stale autogen stubs broadly: durable fix = regenerate (generator already correct).
