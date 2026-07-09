using System;

namespace TestCases
{
    // ===== neo-debugger-neo-frame capstone probe =====
    //
    // A SMALL dedicated probe for the Neo debugger frame-inspection self-check
    // (ILRuntime/Runtime/Debugger/NeoDebuggerFrameCheck.cs). The exercise point
    // is the UNHANDLED-EXCEPTION path: an IL method running under ExecuteNeo
    // throws an exception no handler catches -> ExecuteNeo's unwind constructs an
    // ILRuntimeException (ILIntepreter.Neo.cs:4543) whose ctor calls
    // DebugService.GetThisInfo / GetLocalVariableInfo (ILRuntimeException.cs:
    // 36-40), stashing .ThisInfo / .LocalInfo. The self-check catches that
    // exception and asserts the stashed strings carry the CORRECT live values.
    //
    // INSTANCE methods (HasThis) so GetThisInfo is exercised (it only runs when
    // method.HasThis). PARAMETERLESS wrappers so the harness invokes them via
    // appdomain.Invoke(m, null) (a new instance is created by Invoke). Each probe
    // assigns a KNOWN value to a primitive local (int) and a reference local
    // (string) -- the two shapes GetLocalVariableInfo recovers -- then throws
    // unhandled. The adversarial probe MUTATES the local before the throw to
    // prove the inspection reads the LIVE frame (not a stale/default).
    //
    // The probe type carries non-static IL fields so GetThisInfo has something to
    // enumerate (the self-check asserts the field values appear in .ThisInfo).
    //
    // NON-GENERIC + NON-NESTED + top-level: keeps the TypeRef full name == the
    // LoadedTypes key and avoids generic-param resolution noise in the inspector.

    // neo-debugger-ilvt-local probe: an IL value type (struct) with a primitive
    // field + a reference field. An in-frame local of this type spans BOTH the
    // frame's primitive sub-region (the `int x` bytes) AND the reference sub-
    // region (the `string s` slot) -- the shape neo-debugger-neo-frame left as a
    // placeholder for this child to reconstruct.
    public struct VtLocal
    {
        public int X;
        public string S;
    }

    public class NeoDebuggerFrameProbe
    {
        // IL-declared instance fields (exercised by GetThisInfo via the F-4
        // indexer). A primitive + a reference field cover the two common shapes.
        public int FieldInt = 7777;
        public string FieldRef = "field-value-base";

        // (a) primitive + reference locals, then throw unhandled. The known
        //     values PRIM_A / REF_A are pinned by the self-check. The throw must
        //     be UNHANDLED (no try/catch in this method) so ExecuteNeo's unwind
        //     builds the ILRuntimeException from THIS frame.
        public int ProbeThrow()
        {
            int prim = 12345;
            string msg = "local-string-A";
            throw new Exception("ProbeThrow fired");
        }

        // (b) ADVERSARIAL: assign one value, MUTATE the primitive local to a
        //     distinct value, then throw. The self-check asserts .LocalInfo
        //     carries the MUTATED value (PRIM_MUT), NOT the original -- proving
        //     the inspection reads the live frame slot, not a default/stale read.
        public int ProbeMutateThenThrow()
        {
            int prim = 11111;
            prim = 99999;                 // MUTATE -> the value the inspection must see
            string msg = "mutated-string-B";
            throw new Exception("ProbeMutateThenThrow fired");
        }

        // (c) reference-only local + a primitive of a DIFFERENT width (long) so
        //     the self-check also exercises the 8-byte primitive read path. The
        //     known values are pinned. Throw unhandled.
        public int ProbeMixedWidths()
        {
            long big = 0x123456789ABCDEF0L;
            string tag = "wide-probe-C";
            throw new Exception("ProbeMixedWidths fired");
        }

        // (d) IL-value-type LOCAL. `VtLocal vt` is an in-frame IL struct spanning
        //     the primitive sub-region (X = VT_X = 4242) + the reference sub-
        //     region (S = VT_S = "vt-field-A"). The debugger reconstructs the
        //     struct's fields from the split storage. Throw unhandled so the
        //     ILRuntimeException ctor inspects THIS frame.
        public int ProbeVtLocal()
        {
            VtLocal vt;
            vt.X = 4242;
            vt.S = "vt-field-A";
            throw new Exception("ProbeVtLocal fired");
        }

        // (e) ADVERSARIAL IL-value-type local: assign then MUTATE the struct's
        //     primitive field, proving the reconstruction reads the LIVE frame
        //     bytes (the MUTATED value), not a stale/default.
        public int ProbeVtLocalMutate()
        {
            VtLocal vt;
            vt.X = 1111;
            vt.X = 8888;            // MUTATE -> the value reconstruction must see
            vt.S = "vt-field-B";
            throw new Exception("ProbeVtLocalMutate fired");
        }
    }
}
