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
    }
}
