# neo-f4-surfaced-gaps

TRUE COMPLETION: the 2 small gaps surfaced by F-4, BOTH FIXED.
- (A) op_Equality null-operand autogen-binding AoRE: `ReadNeoReference` now applies the `(idx >= 0) ? mStack[idx] : null` null-sentinel guard (closes the whole autogen-binding defect class: Type/String.op_Equality + every autogen Neo binding reading a reference operand).
- (B) RE-CHARACTERIZED at implement time: the F-4 hypothesis ("plain `new MyEx(msg)` stores `this` into Msg") was STALE (the plain ctor already worked post-`de0ef01c`); the REAL bug was a DERIVED IL type's flat `ILTypeInstance` missing inherited fields (`ILType.InitializeFields` did not accumulate the IL base's `TotalPrimitiveSize`/`TotalReferenceCount`, so e.g. `new DerivedEx(msg):base(msg)` allocated a 0-slot `ManagedObjects` and the base ctor's `stfld` NREd). Fixed by prepending the IL base's flat totals in the `#if ENABLE_NEO_MODE` arm (mirrors Legacy's flat `TotalFieldCount`).
Both probes FAIL-on-HEAD (A: `-96`; B: throws) -> PASS-after (`Return:9`). NeoStep14 23/0/0 (incl. 2 new probes); NeoStep 226/0/0; Legacy-neutral.
