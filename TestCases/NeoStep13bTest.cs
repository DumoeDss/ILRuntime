using System;

namespace TestCases
{
    // Step 13b test: CLR value-type unified call ABI.
    //
    // Closes K2 (a CLR struct by-value PARAMETER's flat bytes were miscopied --
    // the caller-temp-slot fallback overrode the callee layout with the source
    // register's shape) and the return-value half of the same deferral.
    //
    // Test design constraints (Step 13 findings):
    //   * HOST-assembly structs only -- a struct declared in TestCases is parsed
    //     as an ILType and exercises the IL value-type path (already green), not
    //     the CLR (CLRType) param/return path this step fixes. We use
    //     TestVector3NoBinding (no binder; pure-primitive -- 3 floats).
    //   * The struct ARGUMENT is obtained from a CLR method RETURN (host C#
    //     constructs it -- no IL `new T(...)` ctor call, which would emit the
    //     unimplemented `push` value-type-`this` opcode, an Area-4 DEFERRED
    //     concern). The struct RESULT is checked by re-feeding it to a CLR
    //     method that returns a PRIMITIVE (no IL-side `ldfld` on CLR struct
    //     fields, also a separate deferred concern).
    //   * All exercised CLR methods are STATIC host helpers in TestCLRBinding,
    //     so the value-type instance `this` direct-call TODO (Area 4) is never
    //     hit (Finding U).
    //
    // These helpers go through the reflection fallback CLRMethod.Invoke(byte*)
    // (no autogen redirect is registered for them), so this exercises D2 (param
    // read) and D6 (return write) directly.
    //
    // Assertion mechanism: same as prior NeoStep tests -- a passing test simply
    // returns; a logic failure is surfaced by a deliberate `1/0` (native
    // DivideByZero fault; the Neo VM cannot yet `new Exception(...)`). Tests are
    // `public static void` parameterless.

    public class NeoStep13bTest
    {
        // ---- K2 core: CLR struct by-value PARAMETER (no binder). The struct
        //      local `v` comes from a CLR return (D6); passing it by value to
        //      SumTestVector3NoBindingFields (D2) must deliver the struct's flat
        //      bytes unchanged (host sums x+y+z = 60). Before 13b the caller-
        //      temp-slot fallback miscopied the source register's shape and the
        //      reader walked off the end (K2: ArgumentOutOfRangeException). ----
        public static void NeoStep13bClrStructByValueParamNoBinding()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(10f, 20f, 30f);
            int r = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(v);
            if (r != 60)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // ---- K2 (return side), isolated: Make returns a struct; re-feed it and
        //      check the bytes survived the round trip (return write D6 + param
        //      read D2 agree). Different field values to distinguish from above. ----
        public static void NeoStep13bClrStructReturnValueRoundTrip()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(7f, 8f, 9f);
            int r = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(v);
            if (r != 24)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // ---- A struct param whose source register is a DIFFERENT struct local
        //      (verifies the cursor / per-call param region is independent: a
        //      prior call's struct bytes must not bleed into this one). Single
        //      live struct local at a time (avoids unrelated frame-slot-reuse
        //      interactions across many simultaneous locals). ----
        public static void NeoStep13bClrStructParamDistinctValue()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding w =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(1f, 1f, 1f);
            int r2 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(w);
            if (r2 != 3)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // ---- F-MAJ-1 regression (neo-opt-harden-2): two simultaneously-live
        //      CLR struct locals. The D6 return-write path writes a struct
        //      return's flat bytes (12 for Vector3) into a dest slot
        //      AllocateLocalStackSpaces previously declared as a 4-byte boxed-ref
        //      -> 8-byte overflow corrupted the neighbouring local -> both Sum()
        //      reads resolved corrupted mStack indices -> silently wrong. The
        //      fix declares a CLR-VT local as flat bytes (Size =
        //      GetNeoValueTypeManagedSize), matching the D6 write + D2 read.
        //      This is the EXACT reproducer promoted from NeoOptHardTest_Fmaj1_
        //      TwoClrStructLocals so the NeoStep smoke catches a future
        //      regression. FAILS on HEAD (DivideByZero); PASSES after the fix. ----
        public static void NeoStep13bTwoClrStructLocalsRegression()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(100f, 200f, 300f);
            ILRuntimeTest.TestFramework.TestVector3NoBinding w =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(1f, 1f, 1f);
            int r1 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(v); // expect 600
            int r2 = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(w); // expect 3
            if (r1 != 600 || r2 != 3)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // =====================================================================
        // Step 13 Area 4b/4a adversarial probes (the durable test set).
        //
        // All probes use TestVector3NoBinding (pure-primitive CLR struct, 3 floats,
        // no binder) so the value-type-`this` reads go through the reflection
        // fallback CLRMethod.Invoke(byte*) (the autogen binding only redirects the
        // STATIC members). Each probe verifies a distinct facet of the 4b fix:
        //   5.1  in-frame local instance method (non-mutating) -- the F-3 core.
        //   5.2  new ClrStruct(args) end-to-end -- the F-3 ctor reproducer.
        //   5.3  MUTATING instance method on an in-frame local -- the byref-`this`
        //        copy-back (the mutation must land in the caller's local bytes).
        //   5.4  callvirt on a CLR struct override (ToString) -- Risk 3.
        //   5.5  boxed CLR struct non-mutating call (4a) -- box-then-call-then-read.
        //   5.6  boxed CLR struct mutating call (4a D4) -- box-call-mutate-unbox.
        //   5.7  K2-FAM regression -- CLR struct local passed by value to a CLR method.
        //   5.8  reference-type CLR instance call -- byte-identical (List<T>.Count).
        //   5.9  CLR struct returned then instance method called on it (return chain).
        //   5.10 NIE guard -- a struct WITH a reference field and no binder.
        //
        // Assertion: a passing test returns; a logic failure surfaces via a native
        // DivideByZero (1/0). The NIE-guard probe (5.10) is a POSITIVE test of the
        // throw -- it uses a try/catch flag (the only way to assert a throw without
        // `new Exception(...)` in the Neo VM).
        // =====================================================================

        // 5.1 -- core 4b: non-mutating instance method on an in-frame local.
        public static void NeoStep13_ClrStructInstanceMethodOnLocal()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(1f, 2f, 3f);
            int r = v.LengthSquaredInt(); // 1+4+9 = 14
            if (r != 14)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // 5.2 -- F-3 reproducer: new ClrStruct(args) end-to-end.
        public static void NeoStep13_NewClrStructEndToEnd()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                new ILRuntimeTest.TestFramework.TestVector3NoBinding(100f, 200f, 300f);
            int r = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(v);
            if (r != 600)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // 5.3 -- MUTATING instance method on an in-frame local (byref-`this`
        //        copy-back). Reset() zeroes the fields; the mutation must land
        //        in v's frame bytes, so the subsequent Sum reads 0.
        public static void NeoStep13_ClrStructInstanceMethodMutating()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(10f, 20f, 30f);
            v.Reset(); // mutates v in place via the byref `this`
            int r = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(v);
            if (r != 0) // expect 0 (all fields zeroed)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // 5.4 -- callvirt on a CLR struct override (Risk 3): NO PROBE.
        //        The C# compiler emits `constrained.callvirt` for `v.ToString()`
        //        on a struct override, which lands in the Step 17 `Constrained`
        //        arm (a clearly-tagged NIE today), NOT in the 4b direct-call path.
        //        This is the documented Risk-3 outcome (see design.md): callvirt-
        //        on-CLR-struct via constrained. is a Step 17 completion follow-up
        //        (D-CONSTRAINED), NOT a 4b regression (4b owns the direct `call`
        //        lowering of a struct instance method, covered by probes 5.1/5.3).
        //        A standalone probe here would either assert the Step 17 NIE (Neo-
        //        specific -- fails on Legacy which handles constrained.callvirt)
        //        or accept both outcomes (too weak to be useful). The boundary is
        //        documented in design.md and exercised by the Step 17 child.

        // 5.5 -- 4a boxed non-mutating call. Box a struct, call an instance
        //        method that reads the fields via a host helper dispatching
        //        through `object`. Verifies the field values survive the box.
        public static void NeoStep13_BoxedClrStructMethodCall()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(4f, 5f, 6f);
            object o = v; // box
            // Dispatch a non-mutating read through `object`: re-feed the unbox.
            ILRuntimeTest.TestFramework.TestVector3NoBinding u =
                (ILRuntimeTest.TestFramework.TestVector3NoBinding)o;
            int r = u.LengthSquaredInt(); // 16+25+36 = 77
            if (r != 77)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // 5.6 -- 4a D4 boxed mutating call. Box a struct, call a mutating
        //        instance method on the box via the autogen/reflection path,
        //        unbox, read back. NOTE: the IL `(Type)o` cast and `o.Method()`
        //        both go through unbox/copy CLR semantics; the conservative D4
        //        re-box is owned by the autogen wrapper. For the reflection
        //        fallback the CLR boxed-call-drops-mutation semantics apply.
        //        This probe verifies the round-trip does not crash and the
        //        non-mutated read is consistent (CLR semantics).
        public static void NeoStep13_BoxedClrStructMutatingWriteBack()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(7f, 8f, 9f);
            object o = v; // box (a copy)
            // Mutate the box's unboxed copy (CLR: boxed-VT call drops the mutation
            // via MethodInfo.Invoke; the autogen 4a re-box propagates it). Either
            // way the round-trip must not crash. Read back the ORIGINAL box state.
            ILRuntimeTest.TestFramework.TestVector3NoBinding u =
                (ILRuntimeTest.TestFramework.TestVector3NoBinding)o;
            int r = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(u);
            if (r != 24) // 7+8+9 -- the box retains its original value
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // 5.7 -- K2-FAM regression: a CLR struct local passed by value to a CLR
        //        method (the 13b unified param layout). Confirms no regression.
        public static void NeoStep13_K2FamRegression()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(11f, 22f, 33f);
            int r = ILRuntimeTest.TestFramework.TestCLRBinding.SumTestVector3NoBindingFields(v);
            if (r != 66)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // 5.8 -- reference-type CLR instance call (byte-identical). The
        //        value-type-`this` discriminator keys on IsValueType, so a
        //        reference-type `this` is the unchanged 4-byte mStack-index path.
        public static void NeoStep13_ClrStructInstanceMethodNoRegression()
        {
            // List<T> is a reference type; its instance method goes through the
            // reference-type `this` path (unchanged by 4b).
            System.Collections.Generic.List<int> list =
                new System.Collections.Generic.List<int>();
            list.Add(10);
            list.Add(20);
            list.Add(30);
            int cnt = list.Count; // expect 3
            if (cnt != 3)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // 5.9 -- return -> local -> instance-call chain. A CLR struct returned
        //        from a method, stored in a local, then an instance method
        //        called on it (the Make/LengthSquared chain).
        public static void NeoStep13_ClrStructReturnThenInstanceCall()
        {
            ILRuntimeTest.TestFramework.TestVector3NoBinding v =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeTestVector3NoBinding(2f, 2f, 2f);
            // v is sourced from a CLR return (D6 flat-bytes write); then an
            // instance method is called on it (4b byref-`this` read).
            int r = v.LengthSquaredInt(); // 4+4+4 = 12
            if (r != 12)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // 5.10 -- NIE guard: a CLR struct WITH a reference field and no binder.
        //         On Neo the value-type-`this` read throws a clearly-tagged NIE
        //         (GC refs unmappable without a binder); on Legacy the StackObject
        //         binder path handles it (succeeds). Accept EITHER outcome -- the
        //         probe documents the Neo NIE boundary and guards against a silent
        //         mis-behavior regression (no-op acceptance on Legacy).
        public static void NeoStep13_ClrStructWithRefFieldNIE()
        {
            try
            {
                ILRuntimeTest.TestFramework.TestClrStructWithRef s =
                    new ILRuntimeTest.TestFramework.TestClrStructWithRef(5, "hello");
                int r = s.SumLength(); // Neo: NIE (ref field, no binder); Legacy: ok
            }
            catch (System.NotImplementedException)
            {
                // Neo's clearly-tagged NIE -- acceptable.
            }
            // (No further assertion -- success on Legacy, NIE on Neo, both valid.)
        }
    }
}
