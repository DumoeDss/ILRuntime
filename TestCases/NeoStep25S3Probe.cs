using System;

namespace TestCases
{
    // ===== Step 25 S3: the ILType layout + VTable rebuild probe (OQ1) =====
    //
    // The existing NeoStep25LoadProbe is a STATIC-only container (no instance
    // fields, no virtuals, no interface) -> its instance layout is empty and its
    // Neo VTable is the trivial inherited-Object set, so it CANNOT make the S3
    // ILType layout / VTable rebuild comparison meaningful. This dedicated probe
    // declares the shape the S3 self-check needs: 2+ instance fields of
    // DIFFERING primitive widths (int 4 + long 8) + a reference field (string)
    // + a base-class virtual the probe OVERRIDES + an interface implementation.
    //
    // The S3 self-check (NeoStep25LoadExecCheck, host-side DEBUG+Neo) compiles
    // this probe -> a .neo MemoryStream -> reads it back -> rebuilds the ILType
    // instance layout + Neo VTable + interface map from the deserialized
    // NeoTypeDefRecord (WITHOUT replaying the Cecil init) and asserts the
    // rebuild EQUALS the Cecil-computed values field-by-field / slot-by-slot;
    // then mutates a field offset (and swaps a VTable slot) in an INDEPENDENT
    // model2 BEFORE rebuild and asserts DIVERGENCE (the load-bearing mutation
    // cell -- a rebuild that ignored the record would still equal Cecil).
    //
    // Top-level (NON-NESTED) + NON-GENERIC + BCL-refs-only: keeps the TypeRef
    // full name == the LoadedTypes key (nested uses "/" vs "+") and stays within
    // the S1/S2 reference boundary. No field INITIALIZERS -> the implicit .ctor
    // is just base..ctor()+ret (zero-init is runtime), so it force-compiles
    // cleanly under Neo (no Stfld-in-ctor shape). The methods are pure
    // arithmetic so they compile without exercising any unimplemented op.

    // A minimal interface the probe implements, so its interface offset map is
    // non-empty and the rebuild can compare the carried VTableOffset /
    // MethodSlotKeys / ClassSlotRemap against the Cecil-built map.
    public interface INeoStep25S3Iface
    {
        int IfaceMethod(int x);
    }

    // A minimal ILType base class declaring a virtual method the probe overrides
    // + one instance field, so the probe's Neo VTable includes an INHERITED IL
    // base slot (exercising the inherited-IL-VTable rebuild path, not just the
    // inherited-CLR-Object slots) and the probe's FieldStartIndex > 0 (so the
    // own-field slicing in the rebuild is observable).
    public class NeoStep25S3Base
    {
        public int BaseField;
        public virtual int BaseVirtual(int k) { return k + 10; }
    }

    // The S3 probe: 3 own instance fields of differing widths (int 4 / long 8 /
    // ref 4) + an override of the base virtual + the interface impl. The own
    // primitive region is FInt(0..3) + FLong(4..11) = 12 bytes; the own
    // reference region is FRef = 1 slot. naturalAlignment = max(4, 8, 4) = 8.
    public class NeoStep25S3Probe : NeoStep25S3Base, INeoStep25S3Iface
    {
        public int FInt;     // 4-byte primitive (own primitive offset 0)
        public long FLong;   // 8-byte primitive (own primitive offset 4) -- differing width
        public string FRef;  // reference field (own reference offset 0)

        public override int BaseVirtual(int k) { return k + 20; }  // override base virtual
        public int IfaceMethod(int x) { return x * 3; }           // interface impl

        // The S3-2 capstone entry: a PARAMETERLESS method the Cecil-free load
        // invokes via domainB.Invoke (the Run host entry). Exercises field WRITE
        // (Stfld FInt/FLong/FRef), field READ (Ldfld), virtual dispatch
        // (BaseVirtual override via Callvirt on `this`), and interface dispatch
        // (IfaceMethod via the interface map) -- all on a Cecil-free ILType. The
        // arithmetic result is a known constant so the capstone asserts exactly.
        public int Compute()
        {
            FInt = 7;
            FLong = 100L;
            FRef = "hi";
            // Virtual dispatch: BaseVirtual(7) override -> 7 + 20 = 27.
            int v = BaseVirtual(FInt);
            // Interface dispatch via this (cast to the interface). IfaceMethod(7)
            // -> 7 * 3 = 21.
            int iv = ((INeoStep25S3Iface)this).IfaceMethod(FInt);
            // Field read: FLong is 100; FInt is 7. Combine so a field-layout bug
            // (wrong offset) yields a different sum.
            return v + iv + (int)FLong + FInt;
            // 27 + 21 + 100 + 7 = 155.
        }
    }
}
