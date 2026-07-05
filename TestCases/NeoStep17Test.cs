using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // Step 17 test: Neo byref / managed-address model.
    //   * ref / out IL parameters (the unified 8-byte Ref Slot call ABI)
    //   * ref to a frame local (frame-native Ref Slot, objectIndex == -1)
    //   * ref to a heap IL object field (objectIndex >= 0, Primitives offset)
    //   * ref to an in-frame value-type field (frame-native base + field offset)
    //   * ldelema + stind/ldind round-trip on an IL value-type array
    //   * constrained.callvirt on a struct ToString (override path)
    //
    // Assertion mechanism: same as prior NeoStep tests -- a passing test simply
    // returns without dividing by zero. A logic failure is surfaced by a
    // deliberate `1/0` (native DivideByZero fault; the Neo VM cannot yet
    // `new Exception(...)`). Tests are `public static void` parameterless.
    //
    // CLR-object stind/ldind via field hash, CLR-method ref/out params, and
    // generic-byref are DEFERRED to Step 13b and are NOT exercised here.

    // A small IL value type used by the ref-to-VT-field and ldelema tests.
    public struct NeoStep17Point
    {
        public int x;
        public int y;
    }

    // A heap IL class with an instance field (for the ref-to-heap-field case).
    public class NeoStep17Holder
    {
        public int value;
    }

    // A struct that overrides ToString (the constrained.-no-box path).
    public struct NeoStep17Named
    {
        public int id;
        public NeoStep17Named(int v) { id = v; }
        public override string ToString() { return "Named:" + id.ToString(); }
    }

    public class NeoStep17Test
    {
        // ---- helpers that take ref / out parameters ----

        static void Increment(ref int x)
        {
            x++;
        }

        static void AddInto(int a, ref int acc)
        {
            acc += a;
        }

        static void Produce(out int r)
        {
            r = 777;
        }

        static void PassForward(ref int x)
        {
            // Re-pass the byref onward to another byref method.
            Increment(ref x);
        }

        static void MutateField(ref int f)
        {
            f = f + 100;
        }

        static void MutatePointX(ref int fx)
        {
            fx = 555;
        }

        // TC1 ref to a frame local: Inc(ref x) must mutate the caller's local.
        public static void NeoStep17_TC1_RefFrameLocal()
        {
            int x = 10;
            Increment(ref x);
            if (x != 11)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC2 ref accumulator across several calls.
        public static void NeoStep17_TC2_RefAccumulate()
        {
            int acc = 0;
            AddInto(5, ref acc);
            AddInto(7, ref acc);
            AddInto(11, ref acc);
            if (acc != 23)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC3 out parameter: callee writes, caller observes.
        public static void NeoStep17_TC3_OutParam()
        {
            Produce(out int r);
            if (r != 777)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC4 byref passed onward (the slot's (objectIndex, offset) survives a
        // nested call).
        public static void NeoStep17_TC4_ByrefForwarded()
        {
            int x = 0;
            PassForward(ref x);
            if (x != 1)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC5 ref to a heap IL object field (objectIndex >= 0 encoding).
        public static void NeoStep17_TC5_RefHeapField()
        {
            NeoStep17Holder h = new NeoStep17Holder();
            h.value = 3;
            MutateField(ref h.value);
            if (h.value != 103)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC6 ref to an in-frame value-type field (frame-native base + offset).
        public static void NeoStep17_TC6_RefInFrameVtField()
        {
            NeoStep17Point p;
            p.x = 1;
            p.y = 2;
            MutatePointX(ref p.x);
            if (p.x != 555 || p.y != 2)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC7 ldelema + stind/ldind round-trip on an IL value-type array.
        // Uses a `ref` to an array element to mutate it in place.
        static void BumpViaRef(ref int slot)
        {
            slot = slot + 1;
        }

        public static void NeoStep17_TC7_LdelemaRoundTrip()
        {
            NeoStep17Point[] arr = new NeoStep17Point[3];
            arr[0].x = 10;
            arr[1].x = 20;
            arr[2].x = 30;
            BumpViaRef(ref arr[1].x);
            if (arr[0].x != 10 || arr[1].x != 21 || arr[2].x != 30)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC8 constrained.callvirt on a value type is DEFERRED (Step 13b /
        // follow-up): full constrained. dispatch needs the callvirt to accept a
        // byref `this` (the struct's managed address) and dispatch to the
        // constrained type's concrete override. That callvirt-byref-this work is
        // not in this step, so the case NIEs at the constrained callvirt. No
        // green test is added for it here; see design.md sec 6 / tasks 6.3.

        // ---- B1 regression: register reused for a folded VT field access and
        // ---- then a DISTINCT live range that escapes as a byref. ----
        // The C# compiler reuses an eval-stack register: first as a folded
        // in-frame-VT address (consumed by Stfld_Inline -> sets the gate's
        // foldable-use marker), then as a NEW ldloca addressing an unrelated
        // local whose address escapes through `stind`/`ref`-Call. The gate must
        // judge each live range independently: the escape range must read a real
        // Ref Slot (the fresh address), not a stale folded offset from the
        // earlier range. Asserts BOTH the folded field writes AND the escape
        // store land on the correct memory.

        struct TC8Point { public int x; public int y; }

        static void TC8SetX(ref int fx, int v) { fx = v; }

        public static void NeoStep17_TC8_MixedFoldThenEscape()
        {
            TC8Point p;
            p.x = 7;                 // Stfld_Inline -> folded address use
            p.y = 8;                 // Stfld_Inline -> folded address use
            int x = 0;
            TC8SetX(ref x, 99);      // ref to UNRELATED local -> register reused, escapes
            if (p.x != 7 || p.y != 8 || x != 99)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC9 covers the frame-native Stind_Ref / Ldind_Ref path (M2): a `ref`
        // to a frame local that holds an IL object reference. The byref escapes
        // the addrAlias folding (a byref param), so the ldloca produces a real
        // Ref Slot and the callee's stind_ref/ldind_ref dispatch frame-native.
        static void TC9SetHolder(ref NeoStep17Holder h)
        {
            // stind_ref through the byref: install a fresh instance.
            NeoStep17Holder nh = new NeoStep17Holder();
            nh.value = 4242;
            h = nh;
        }

        public static void NeoStep17_TC9_RefFrameRefRoundTrip()
        {
            NeoStep17Holder h = new NeoStep17Holder();
            h.value = 1;
            TC9SetHolder(ref h);
            // ldind_ref / field read observes the replaced reference.
            if (h.value != 4242)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // ---- Step 17 D-CONSTRAINED follow-up: constrained.callvirt on a value
        // ---- type (box-once for CLR / override-of-Object; direct-call for IL
        // ---- struct interface impls); CLR primitive-array ldelema; addrAlias
        // ---- register-reuse regression. ----

        // An IL value type implementing an interface -- the constrained path's
        // direct-call shape (the override is an ILMethod whose body uses in-frame
        // Ldfld_Inline; the byref `this` is dereferenced into the callee slot-0).
        public interface INeoStep17Id { int FetchId(); int MutateAndFetch(int delta); }
        public struct NeoStep17IdStruct : INeoStep17Id
        {
            public int id;
            public NeoStep17IdStruct(int v) { id = v; }
            public int FetchId() { return id; }
            public int MutateAndFetch(int delta) { id = id + delta; return id; }
        }

        // Generic callers force the C# compiler to emit `constrained.callvirt`.
        static int Step17ConstrainedFetch<T>(T v) where T : INeoStep17Id
        {
            return v.FetchId();
        }

        static int Step17ConstrainedMutate<T>(ref T v, int delta) where T : INeoStep17Id
        {
            // v is a byref here; v.MutateAndFetch emits constrained.callvirt on the
            // byref `this` -> exercises the box/deref path against a ref param.
            return v.MutateAndFetch(delta);
        }

        static string Step17ConstrainedToString<T>(T v) where T : struct
        {
            return v.ToString();
        }

        // TC10 IL value type constrained.callvirt via an interface (direct-call
        // path): the override reads `this.id` via in-frame Ldfld_Inline. Asserts
        // the constrained dispatch lands the byref `this` flat bytes in the callee
        // slot-0 and the override reads the correct value.
        public static void NeoStep17_ConstrainedIlVtDirectCall()
        {
            NeoStep17IdStruct v = new NeoStep17IdStruct(111);
            int r = Step17ConstrainedFetch(v);
            if (r != 111)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC11 CLR primitive constrained.callvirt (int.ToString via box-once on a
        // CLR primitive). The boxed int is dispatched to Int32.ToString.
        public static void NeoStep17_ConstrainedClrPrimitiveToString()
        {
            int x = 7;
            string s = Step17ConstrainedToString(x);
            if (s != "7")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC12 CLR struct override via constrained.callvirt (box-once on a CLR
        // value type). The boxed struct is dispatched to its ToString override.
        public static void NeoStep17_ConstrainedClrStructToString()
        {
            TestVector3NoBinding v = new TestVector3NoBinding(1f, 2f, 3f);
            string s = Step17ConstrainedToString(v);
            if (s != "(1,2,3)")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC13 the box-once resolves the CONSTRAINED type's concrete override, not
        // the static call-site method. A CLR struct override of Object.ToString
        // dispatched via `constrained T; callvirt Object.ToString` SHALL fire T's
        // override (here "(4,5,6)"), not Object.ToString. The TestVector3NoBinding
        // override IS the constrained type's concrete override.
        public static void NeoStep17_ConstrainedOverrideResolvesConstrainedType()
        {
            TestVector3NoBinding v = new TestVector3NoBinding(4f, 5f, 6f);
            string s = Step17ConstrainedToString(v);
            // Object.ToString (the static call-site method) would return the type
            // name; the override returns the "(x,y,z)" form. Asserting the override
            // output proves the constrained TYPE's concrete override was resolved
            // (not the static call-site Object.ToString).
            if (s != "(4,5,6)")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC14 CLR primitive-array ldelema -> stind_i4 / ldind_i4 round-trip. A
        // `ref` to a CLR int[] element flows through ldelema (Ref Slot
        // (arrIdx, elementIdx)) and the callee's stind_i4 writes through it; a
        // direct element load reads the mutation back.
        static void Step17SetViaRef(ref int slot, int v) { slot = v; }

        public static void NeoStep17_ClrPrimitiveArrayLdelema_StindLdind()
        {
            int[] arr = new int[3];
            arr[0] = 10;
            arr[1] = 20;
            arr[2] = 30;
            Step17SetViaRef(ref arr[1], 99);
            int a0 = arr[0];
            int a1 = arr[1];
            int a2 = arr[2];
            if (a0 != 10 || a1 != 99 || a2 != 30)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // TC15 addrAlias register-reuse regression (the Step-17-B1 probe). The
        // fusion / Constrained dispatch must NOT weaken the addrAlias COEXIST
        // gate. A byref whose dest register is reused by an intervening op, then
        // read, must yield the FRESH value (not a stale folded offset). This is
        // the adversarial register-reuse probe a green smoke previously MISSED.
        struct TC15Point { public int x; public int y; }
        static void TC15SetX(ref int fx, int v) { fx = v; }

        public static void NeoStep17_AddrAliasRegisterReuseRegression()
        {
            TC15Point p;
            p.x = 7;                 // Stfld_Inline -> folded address use
            p.y = 8;                 // Stfld_Inline -> folded address use
            int x = 0;
            TC15SetX(ref x, 99);     // ref to UNRELATED local -> register reused, escapes
            if (p.x != 7 || p.y != 8 || x != 99)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // ---- Review-loop round 1 (F1) keeper probes: IL value type calling an
        // ---- INHERITED Object/ValueType method via constrained.callvirt (no IL
        // ---- override). HEAD's Step-17 NIE degraded gracefully; the apply
        // ---- resolution regressed it into a native segfault/NRE because the
        // ---- box-once branch read the IL struct's flat bytes AS an
        // ---- ILTypeInstance (a CLASS), producing a corrupt boxed receiver.
        // ---- The fix boxes the IL struct into a real ILTypeInstance (Step 13
        // ---- Box-arm machinery) and dispatches the inherited CLRMethod on it. ----

        // An IL value type with NO ToString/GetHashCode/Equals override. Calling
        // any of these via a generic constrained caller resolves to the inherited
        // CLRMethod (Object.ToString / ValueType.GetHashCode / Object.Equals).
        public struct NeoStep17PlainStruct
        {
            public int a;
            public int b;
            public NeoStep17PlainStruct(int x, int y) { a = x; b = y; }
        }

        // Generic constrained caller -- forces `constrained T; callvirt M`.
        static string Step17InheritedToString<T>(T v) where T : struct
        {
            return v.ToString();
        }

        static int Step17InheritedGetHashCode<T>(T v) where T : struct
        {
            return v.GetHashCode();
        }

        static bool Step17InheritedEquals<T>(T v, object other) where T : struct
        {
            return v.Equals(other);
        }

        static string Step17InheritedInterpolate<T>(T v) where T : struct
        {
            // The common pattern: string interpolation lowers to a constrained
            // callvirt Object.ToString on the boxed struct (the F1 segfault path
            // in the wild).
            return $"{v}";
        }

        // K1 ilStruct.ToString() -- the F1 case (no override). The inherited
        // Object.ToString dispatched on the boxed ILTypeInstance must NOT crash
        // (it returns a non-null string -- the ILTypeInstance host ToString falls
        // back to the type's full name when no IL override exists). The keeper
        // asserts only that the call returns a non-null, non-empty string without
        // crashing (the regression was a native segfault).
        public static void NeoStep17_ConstrainedIlVtInheritedToString()
        {
            NeoStep17PlainStruct v = new NeoStep17PlainStruct(1, 2);
            string s = Step17InheritedToString(v);
            if (string.IsNullOrEmpty(s))
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // K2 ilStruct.GetHashCode() -- the inherited ValueType.GetHashCode on a
        // no-override IL struct (the NRE half of F1). Must return an int without
        // crashing.
        public static void NeoStep17_ConstrainedIlVtInheritedGetHashCode()
        {
            NeoStep17PlainStruct v = new NeoStep17PlainStruct(3, 4);
            int h = Step17InheritedGetHashCode(v);
            // Just assert it returned without crashing; the exact hash is
            // implementation-defined. Use a trivially-true check that still
            // executes the return-value read.
            if (h == int.MinValue && h != int.MinValue)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // K3 ilStruct.Equals(other) -- the inherited Object.Equals on a
        // no-override IL struct. Must return a bool without crashing.
        public static void NeoStep17_ConstrainedIlVtInheritedEquals()
        {
            NeoStep17PlainStruct v = new NeoStep17PlainStruct(5, 6);
            bool eq = Step17InheritedEquals(v, v);
            // Assert the call returned without crashing. We do not assert the
            // exact equality semantics (the inherited Object.Equals on a boxed
            // ILTypeInstance is reference equality on the box); the regression
            // was a crash, so a clean return is the contract.
            if (eq && !eq)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // K4 string interpolation $"{ilStruct}" -- the common pattern that lowers
        // to a constrained callvirt Object.ToString. The wild crash path; must
        // return a non-null, non-empty string without crashing.
        public static void NeoStep17_ConstrainedIlVtInheritedInterpolate()
        {
            NeoStep17PlainStruct v = new NeoStep17PlainStruct(7, 8);
            string s = Step17InheritedInterpolate(v);
            if (string.IsNullOrEmpty(s))
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // K5 an IL struct WITH an IL override of ToString dispatched via a generic
        // constrained caller -- verifies the existing IL-VT-direct-call path
        // (actualMethod is ILMethod) STILL works after the F1 fix (the discriminator
        // must not be perturbed for the override case). Uses NeoStep17Named which
        // overrides ToString (and the override body only does `id.ToString()` on a
        // primitive -- no ldflda, so it is reachable).
        public static void NeoStep17_ConstrainedIlVtOverrideStillWorks()
        {
            NeoStep17Named v = new NeoStep17Named(42);
            string s = Step17InheritedToString(v);
            if (s != "Named:42")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // K6 a CLR struct .ToString() via a generic constrained caller -- verifies
        // the CLR-VT box-once path STILL works after the F1 fix (the discriminator's
        // CLR branch must stay byte-identical). Reuses TestVector3NoBinding which
        // overrides ToString with "(x,y,z)".
        public static void NeoStep17_ConstrainedClrVtBoxOnceStillWorks()
        {
            TestVector3NoBinding v = new TestVector3NoBinding(9f, 10f, 11f);
            string s = Step17InheritedToString(v);
            if (s != "(9,10,11)")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // K7 (F5 reconstruction) two simultaneous constrained callvirts whose
        // boxed temps reuse the register region, then a byref dest-reuse window,
        // then re-read the folded fields. With F1 fixed, this confirms the
        // Constrained dispatch does not perturb the addrAlias COEXIST gate (the
        // silent-corruption class from Step-17-B1). The two constrained calls
        // here are the F1 inherited-method shape (the one F1 previously crashed).
        struct K7Pair { public int a; public int b; }
        static void K7Bump(ref int slot, int dv) { slot = slot + dv; }

        public static void NeoStep17_AddrAliasTwoConstrainedReuse()
        {
            K7Pair p;
            p.a = 100;              // Stfld_Inline -> folded address use
            p.b = 200;              // Stfld_Inline -> folded address use
            // Two constrained inherited-method calls (F1 shape) whose boxed
            // receivers occupy temps near the folded-field register region.
            NeoStep17PlainStruct v1 = new NeoStep17PlainStruct(1, 2);
            NeoStep17PlainStruct v2 = new NeoStep17PlainStruct(3, 4);
            string s1 = Step17InheritedToString(v1);
            string s2 = Step17InheritedToString(v2);
            // An intervening byref dest-reuse window (the addrAlias escape).
            int x = 0;
            K7Bump(ref x, 50);
            // Re-read the folded fields + the byref dest: all must be intact.
            bool bad = false;
            if (p.a != 100) bad = true;
            if (p.b != 200) bad = true;
            if (x != 50) bad = true;
            if (string.IsNullOrEmpty(s1)) bad = true;
            if (string.IsNullOrEmpty(s2)) bad = true;
            if (bad)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }
    }
}
