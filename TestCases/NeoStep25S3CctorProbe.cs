using System;

namespace TestCases
{
    // ===== Step 25 S3-4: the .cctor-seeding probe =====
    //
    // The S3-2 probe (NeoStep25S3Probe) deliberately declares NO static fields +
    // NO .cctor (the S3-2 capstone is independent of sub-surface 4). This
    // DEDICATED probe closes sub-surface 4: it declares a static int field + a
    // .cctor that writes a KNOWN non-zero constant to it + a ReadStatic() reader.
    //
    // The S3-4 capstone (NeoStep25CecilFreeLoadCheck, .cctor cell) compiles this
    // probe -> a .neo -> Cecil-free-loads it into a FRESH AppDomain B ->
    // domainB.Invoke("ReadStatic") -> asserts the result EQUALS the .cctor-set
    // constant (NOT the default zero). This proves the .cctor RAN at Cecil-free
    // load (the deserialized .cctor body executes via ExecuteNeo after Attach
    // binds its CompiledFrame).
    //
    // Adversarial body-mutation cell: mutate the .cctor body's Ldc_I4 constant in
    // an INDEPENDENT model2 BEFORE LoadNeoAssembly -> load into B2 -> invoke
    // ReadStatic() -> assert the MUTATED constant (proves the deserialized .cctor
    // body genuinely ran; a Cecil-fallback or a default-zero read fails).
    //
    // Top-level (NON-NESTED) + NON-GENERIC + BCL-refs-only: keeps the TypeRef
    // full name == the LoadedTypes key. The .cctor is trivial arithmetic (a
    // single Stsfld of a constant) so it compiles cleanly under Neo + does not
    // throw. ReadStatic is a parameterless static method so the Cecil-free load
    // invokes it via domainB.Invoke without instantiation.

    public class NeoStep25S3CctorProbe
    {
        // The .cctor sets this to .cctorValue (777). The default (zero) is what
        // a load that NEVER ran the .cctor would read -> the capstone's
        // non-zero assertion catches that.
        public static int SVal;

        // The .cctor-set constant. ReadStatic returns SVal (== this after the
        // .cctor runs). Mirrored as CctorValue in the host-side self-check so a
        // probe-value edit propagates (the adversarial cell mutates a DIFFERENT
        // value in model2 to prove the .cctor body ran).
        const int CctorValue = 777;

        static NeoStep25S3CctorProbe()
        {
            SVal = CctorValue;
        }

        // The capstone entry: a parameterless static reader the Cecil-free load
        // invokes via domainB.Invoke (no instantiation needed). Returns SVal --
        // the .cctor-set value (777), NOT the default zero.
        public static int ReadStatic()
        {
            return SVal;
        }
    }
}
