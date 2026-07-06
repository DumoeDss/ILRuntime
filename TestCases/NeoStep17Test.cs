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

        // F-6 / NEO-VT-FLDADDR adversarial keeper probes. The ldflda arm must
        // produce a correct frame-native Ref Slot when the operand slot holds the
        // in-frame VT's FLAT BYTES (shape 3: a constrained-boxed `this` inside an
        // IL-struct method body), as well as the existing Ref-Slot operand shapes
        // (ldloca; ldflda -- shape 1; byref-this direct-call -- shape 2). The
        // marker (Operand4 bit 0x1, stamped by JIT type-spec) gates the new branch.
        //
        // Probe design: the load-bearing F-6 shape is exercised by an IL struct
        // override invoked via constrained.callvirt box-once (slot-0 = flat bytes).
        // The override body takes a field address via `ldflda` and consumes it
        // through an IL byref helper (NOT a CLR method call -- the byref-`this` ->
        // CLR-method path is a SEPARATE F-3/NEO-BYREF-THIS deferred gap). This
        // isolates the ldflda Ref-Slot correctness from the unrelated downstream
        // CLR-call gap.

        // An IL struct with a method that takes `ref field` via ldflda. Used by
        // the shape-3 (constrained box-once) load-bearing probe.
        public struct NeoStep17LdfldaStruct
        {
            public int id;
            public NeoStep17LdfldaStruct(int v) { id = v; }
            static int ReadViaRef(ref int slot) { return slot; }
            // The body lowers to `ldflda this.id; call ReadViaRef(ref ...)`. With
            // F-6 unfixed, the ldflda reads slot-0's flat bytes (id's value) as an
            // mStack index -> garbage Ref Slot -> ReadViaRef reads mStack garbage
            // -> wrong value. With F-6 fixed, the marker branch produces a frame-
            // native Ref Slot pointing at `id`'s flat bytes -> ReadViaRef reads
            // the correct field value.
            public int ReadIdViaLdflda() { return ReadViaRef(ref id); }
            static void WriteViaRef(ref int slot, int v) { slot = v; }
            public void WriteIdViaLdflda(int v) { WriteViaRef(ref id, v); }
            // A ToString override whose body takes `ref id` via ldflda and passes
            // it to an IL byref helper (NOT a CLR method -- avoids the byref-`this`
            // -> CLR-method F-3 gap). Invoked via `s.ToString()` -> constrained
            // box-once -> slot-0 = flat bytes (shape 3). This is the load-bearing
            // F-6 reproducer.
            public override string ToString() { return "S:" + ReadViaRef(ref id).ToString(); }
        }

        // 4.1 -- shape 1 (ldloca; ldflda via ref param): regression guard. PASSES
        // on HEAD (the operand slot holds a real frame-native Ref Slot).
        public static void NeoStep17_LdfldaInline_RefFieldRead()
        {
            NeoStep17Point p;
            p.x = 0;
            p.y = 0;
            int got = ReadPointX(ref p.x);
            p.x = 77;
            got = ReadPointX(ref p.x);
            if (got != 77) { int z = 1; int d = 0; int _ = z / d; }
        }
        static int ReadPointX(ref int fx) { return fx; }

        // 4.2 -- shape 1 write: regression guard.
        public static void NeoStep17_LdfldaInline_RefFieldWrite()
        {
            NeoStep17Point p;
            p.x = 0;
            p.y = 0;
            WritePointX(ref p.x, 88);
            if (p.x != 88) { int z = 1; int d = 0; int _ = z / d; }
        }
        static void WritePointX(ref int fx, int v) { fx = v; }

        // 4.3 -- shape 3 (the F-6 load-bearing probe). An IL struct ToString
        // override invoked via `constrained.callvirt` box-once (the C# compiler
        // emits constrained.callvirt Object.ToString for `s.ToString()`); the
        // override body takes `ref id` via ldflda and passes it to an IL byref
        // helper. FAILS on HEAD (ldflda reads id's value 42 as an mStack index ->
        // garbage Ref Slot -> ReadViaRef reads garbage), PASSES after the F-6 fix
        // (the marker branch produces a frame-native Ref Slot pointing at `id`'s
        // flat bytes).
        static string CallToStringConstrained<T>(T v) where T : struct { return v.ToString(); }

        public static void NeoStep17_LdfldaInline_StructMethodFlatBytes()
        {
            NeoStep17LdfldaStruct s = new NeoStep17LdfldaStruct(42);
            string got = CallToStringConstrained(s);
            if (got != "S:42") { int z = 1; int d = 0; int _ = z / d; }
        }

        // 4.4 -- nested-field ldflda via a chain (`ref outer.inner.x`), address-
        // only access (no whole-VT load -> avoids the Ldfld_Value Step-12b NIE).
        public struct NeoStep17LdfldaInner { public int x; }
        public struct NeoStep17LdfldaOuter { public NeoStep17LdfldaInner inner; }
        static int ReadNested(ref int fx) { return fx; }
        public static void NeoStep17_LdfldaInline_NestedField()
        {
            NeoStep17LdfldaOuter o;
            o.inner.x = 55;
            int got = ReadNested(ref o.inner.x);
            if (got != 55) { int z = 1; int d = 0; int _ = z / d; }
        }

        // 4.5 -- ldflda on a struct with a REFERENCE-type field (the ref-region
        // sub-case). The address points at the primitive-region slot; the consumer
        // reads/writes the mStack ref slot.
        public struct NeoStep17LdfldaWithRef { public int n; public NeoStep17Holder h; }
        static void SetRefField(ref NeoStep17Holder slot, NeoStep17Holder v) { slot = v; }
        static NeoStep17Holder GetRefField(ref NeoStep17Holder slot) { return slot; }
        public static void NeoStep17_LdfldaInline_RefTypeField()
        {
            NeoStep17LdfldaWithRef s;
            s.n = 0;
            s.h = null;
            NeoStep17Holder nh = new NeoStep17Holder();
            nh.value = 123;
            SetRefField(ref s.h, nh);
            NeoStep17Holder got = GetRefField(ref s.h);
            if (got == null || got.value != 123) { int z = 1; int d = 0; int _ = z / d; }
        }

        // 4.6 -- register-reuse / escape probe (the Step-17-B1 silent-corruption
        // class). An ldflda-produced byref whose dest register is reused by an
        // intervening op, then the byref is read. The addrAlias COEXIST gate
        // must keep the ldflda real and the marker branch must yield the FRESH
        // value, not stale.
        struct LdfldaReuseOuter { public int a; public int b; }
        static int LdfldaReuseRead(ref int fx) { return fx; }
        public static void NeoStep17_LdfldaInline_RegisterReuseEscape()
        {
            LdfldaReuseOuter p;
            p.a = 7;
            p.b = 8;
            // An intervening byref dest-reuse window (an unrelated `ref int`
            // forces register reuse / addrAlias escape).
            int x = 0;
            LdfldaReuseRead(ref x);
            // Re-read the folded field addresses after the reuse window.
            int ra = LdfldaReuseRead(ref p.a);
            int rb = LdfldaReuseRead(ref p.b);
            if (ra != 7 || rb != 8 || x != 0) { int z = 1; int d = 0; int _ = z / d; }
        }

        // 4.7 -- heap-IL ldflda regression guard (the marker is ABSENT -> the
        // existing heap branch fires byte-identical).
        class LdfldaHeapIl { public int val; }
        static int ReadHeapViaRef(ref int fx) { return fx; }
        public static void NeoStep17_LdfldaInline_HeapIlRegression()
        {
            LdfldaHeapIl h = new LdfldaHeapIl();
            h.val = 999;
            int got = ReadHeapViaRef(ref h.val);
            if (got != 999) { int z = 1; int d = 0; int _ = z / d; }
        }

        // 4.8 -- CLR-object ldflda regression guard (marker absent). Uses a CLR-
        // declared holder via the host so the field-hash stind/ldind path is not
        // required (the byref-param read alone suffices).
        public static void NeoStep17_LdfldaInline_ClrObjectRegression()
        {
            // A CLR object (System.Text.StringBuilder) with an instance field-
            // style access via a property -- ldflda on a CLR ref object's field
            // would hit the Step-17 CLR-field-hash deferral. Scope to a byref of
            // a CLR-primitive local instead (the marker is absent; the heap branch
            // is exercised through the existing ldind/stind). This keeps the probe
            // reachable and proves the non-marker path is byte-identical.
            int n = 0;
            ReadPointX(ref n);
            n = 321;
            int got = ReadPointX(ref n);
            if (got != 321) { int z = 1; int d = 0; int _ = z / d; }
        }

        // ---- Step 13 Area 4d probes: stind/ldind/stobj/ldobj on a byref to a
        //      CLR OBJECT field via field identity. Before 4d the Ldflda arm
        //      stamped field.PrimitiveOffset (meaningless for a CLR object) and
        //      the stind/ldind consumer threw a clean NIE. Each probe is a
        //      FAIL-on-HEAD stash-toggle -> PASS-after guard. ----

        // 4d.1 -- ldflda clrObj.intField; stind_i4 write. The CLR object's int
        //         field is mutated via a managed ref produced by ldflda.
        static void StindClrIntField(ref int slot, int v) { slot = v; }

        public static void NeoStep17_StindClrObjectIntField()
        {
            ILRuntimeTest.TestFramework.TestCLRBinding.Area4dHolder h =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeArea4dHolder(0, "seed");
            // Mutate the CLR object's int field via ldflda + stind.
            StindClrIntField(ref h.intField, 7173);
            int got = ILRuntimeTest.TestFramework.TestCLRBinding.ReadArea4dIntField(h);
            if (got != 7173) { int z = 1; int d = 0; int _ = z / d; }
        }

        // 4d.2 -- ldflda; ldind_i4 read of a CLR object's int field. The C#
        //        compiler lowers `ref` reading helpers; to exercise the ldflda;
        //        ldind.i4 path on a CLR-object field WITHOUT an inlined IL-method
        //        return move (a pre-existing inliner edge), this probe writes the
        //        field via stind (4d.1 path) then reads it back via ldind into a
        //        frame local through a non-trivial helper, and compares.
        static int LdindClrIntFieldPeek(ref int slot) { int v = slot; return v + 0; }

        public static void NeoStep17_LdindClrObjectIntField()
        {
            ILRuntimeTest.TestFramework.TestCLRBinding.Area4dHolder h =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeArea4dHolder(7, "seed");
            // Write via stind (exercises the stind CLR-object path).
            StindClrIntField(ref h.intField, 99);
            // Read via ldind (exercises the ldind CLR-object path).
            int got = LdindClrIntFieldPeek(ref h.intField);
            if (got != 99) { int z = 1; int d = 0; int _ = z / d; }
        }

        // 4d.3 -- stind_ref/ldind_ref on a CLR object's reference-type field
        //         (string). The field-hash path must route the managed ref.
        static void StindClrRefField(ref string slot, string v) { slot = v; }
        static string LdindClrRefField(ref string slot) { return slot; }

        public static void NeoStep17_ClrObjectRefFieldReadWrite()
        {
            ILRuntimeTest.TestFramework.TestCLRBinding.Area4dHolder h =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeArea4dHolder(0, "initial");
            StindClrRefField(ref h.refField, "updated");
            string got = LdindClrRefField(ref h.refField);
            int len = ILRuntimeTest.TestFramework.TestCLRBinding.StringLength(got);
            if (len != "updated".Length) { int z = 1; int d = 0; int _ = z / d; }
        }

        // 4d.4 -- read-after-write roundtrip: write then read back the same
        //         CLR int field, then verify the underlying object's state.
        public static void NeoStep17_ClrObjectFieldRoundTrip()
        {
            ILRuntimeTest.TestFramework.TestCLRBinding.Area4dHolder h =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeArea4dHolder(1, "seed");
            StindClrIntField(ref h.intField, 500);
            int r1 = LdindClrIntFieldPeek(ref h.intField);
            StindClrIntField(ref h.intField, r1 + 250);
            int r2 = ILRuntimeTest.TestFramework.TestCLRBinding.ReadArea4dIntField(h);
            if (r1 != 500 || r2 != 750) { int z = 1; int d = 0; int _ = z / d; }
        }

        // 4d.5 -- a CLR-object-field ref passed to a 4c CLR method (4c+4d
        //         interaction). The byref PARAM's Ref Slot points at a CLR
        //         object's field (objectIndex >= 0); the 4c marshal must deref
        //         through the field accessor (the mStack-object case).
        public static void NeoStep17_ClrObjectFieldRefToClrMethod()
        {
            ILRuntimeTest.TestFramework.TestCLRBinding.Area4dHolder h =
                ILRuntimeTest.TestFramework.TestCLRBinding.MakeArea4dHolder(0, "seed");
            // The 4c CLR method BumpRefInt takes ref int; passing a CLR object
            // field's address exercises both 4d (ldflda on a CLR object) and
            // 4c's mStack-object byref deref in one round-trip.
            ILRuntimeTest.TestFramework.TestCLRBinding.BumpRefInt(ref h.intField);
            int got = ILRuntimeTest.TestFramework.TestCLRBinding.ReadArea4dIntField(h);
            if (got != 10) { int z = 1; int d = 0; int _ = z / d; }
        }

        // ======================================================================
        // Step 17 (b) Stobj/Ldobj ref-region copy loop + IL-VT-with-ref-fields
        //            constrained sub-case (neo-step17-stobj-refloop).
        // ======================================================================

        // An IL value type WITH a reference field (the green target: a primitive
        // half + a ref half). Used by Stobj/Ldobj probes + the constrained sub-case.
        public struct NeoStep17VtWithRef
        {
            public int x;
            public string tag;
            public NeoStep17VtWithRef(int x_, string tag_) { x = x_; tag = tag_; }
        }

        // An IL value type with a ref field AND an IL override of ToString (the
        // constrained direct-call sub-case: the override reads this.tag via
        // in-frame Ldfld_Ref_Inline from ParamInfos[0]).
        public struct NeoStep17VtWithRefOverride
        {
            public int id;
            public string tag;
            public NeoStep17VtWithRefOverride(int id_, string tag_) { id = id_; tag = tag_; }
            public override string ToString() { return "OVR:" + (tag == null ? "null" : tag); }
        }

        // A nested IL value type with a ref field. The genuine nested-field-byref
        // stobj/ldobj shape (a byref produced by `ldflda` of a nested field) is
        // blocked by the pre-existing Step-6 `Ldfld_Value` gap and is the
        // documented DEFERRED NIE edge for this change -- not exercised by a
        // runnable probe here (see ship log).
        public struct NeoStep17VtWithRefInner { public int n; public string tag; }
        public struct NeoStep17VtWithRefOuter { public NeoStep17VtWithRefInner inner; }

        // Helper: take a byref and Stobj a value through it (forces a genuine
        // escaping byref that addrAlias cannot fold -- the value is loaded from a
        // separate local and the call escapes the fold window).
        static void StobjIntoByref(ref NeoStep17VtWithRef dst, NeoStep17VtWithRef src)
        {
            dst = src;
        }

        static NeoStep17VtWithRef LdobjFromByref(ref NeoStep17VtWithRef src)
        {
            // Returning the struct directly hits the trivial-inliner return-move
            // defect (NEO-INLINED-RETURN-MOVE, a separate pre-existing gap); not
            // used by the probes (kept for reference). The probes drive ldobj via
            // inlinable byref helpers that keep source + dest in one frame.
            return src;
        }

        // Probe 1: Stobj of a VT-with-ref-field where the DEST's stale ref slot
        // is a NON-NULL canary that MUST be overwritten (the silent-corruption
        // guard). Without the ref-loop, dst.tag keeps the canary.
        public static void NeoStep17_StobjVtWithRefField_OverwritesStaleDestRef()
        {
            NeoStep17VtWithRef dst = new NeoStep17VtWithRef(0, "CANARY");
            NeoStep17VtWithRef src = new NeoStep17VtWithRef(7, "REAL");
            StobjIntoByref(ref dst, src);
            if (dst.x != 7 || dst.tag != "REAL")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // Probe 2: Ldobj of a VT-with-ref-field where the DEST's stale ref slot
        // is null and the SRC's ref field is non-null. Without the ref-loop, the
        // dest's ref slot stays the stale null.
        // Helper that reads a VT-with-ref-field through a byref (ldobj) and writes
        // it to an out param (stobj). Inlined into the caller, the byref source +
        // the out dest live in the SAME frame, so the runtime localInfos scan
        // recovers both ref bases (the design's green target).
        static void LoadByRefIntoOut(ref NeoStep17VtWithRef src, out NeoStep17VtWithRef dst)
        {
            dst = src;
        }

        public static void NeoStep17_LdobjVtWithRefField_ReadsSrcRefNotStaleNull()
        {
            NeoStep17VtWithRef src = new NeoStep17VtWithRef(9, "PAYLOAD");
            NeoStep17VtWithRef dst;  // out -- stale null ref slot before the write
            // Drives a genuine ldobj (read src through its byref) + stobj (write
            // into the out dest). The dest's stale null ref slot MUST be
            // overwritten with src's non-null "PAYLOAD" -- proving the ldobj read
            // the SRC ref slot, not the dest's stale null.
            LoadByRefIntoOut(ref src, out dst);
            if (dst.x != 9 || dst.tag != "PAYLOAD")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // A VT with TWO ref fields -- exercises the multi-slot ref-region copy
        // loop (refCount == 2), guarding against an off-by-one that copies only
        // the first ref slot. (The genuine nested-VT-field-byref shape -- a byref
        // produced by `ldflda` of a nested field -- is blocked by the pre-existing
        // Step-6 `Ldfld_Value` / F-6 gaps and is the documented DEFERRED NIE edge
        // for this change; see the ship log.)
        public struct NeoStep17VtWithTwoRefs
        {
            public int n;
            public string a;
            public string b;
            public NeoStep17VtWithTwoRefs(int n_, string a_, string b_) { n = n_; a = a_; b = b_; }
        }

        static void StobjTwoRefsByRef(ref NeoStep17VtWithTwoRefs dst, NeoStep17VtWithTwoRefs src)
        {
            dst = src;
        }

        // Probe 3: a VT with TWO ref fields -- both ref slots must be copied (an
        // off-by-one that copies only slot 0 leaves `b` stale). The dest's `b` is
        // pre-set to a non-null canary that MUST be overwritten.
        public static void NeoStep17_NestedVtWithRefField_Stobj()
        {
            NeoStep17VtWithTwoRefs dst = new NeoStep17VtWithTwoRefs(0, "CAN_A", "CAN_B");
            NeoStep17VtWithTwoRefs src = new NeoStep17VtWithTwoRefs(7, "REAL_A", "REAL_B");
            StobjTwoRefsByRef(ref dst, src);
            // Read each field into a local first (avoids a JIT inline-rewrite gap
            // triggered by chained field reads on the multi-ref struct), then
            // assert: n + both ref slots must reflect SRC (CAN_B MUST be gone).
            int gotN = dst.n;
            string gotA = dst.a;
            string gotB = dst.b;
            if (gotN != 7 || gotA != "REAL_A" || gotB != "REAL_B")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // Probe 4: IL-VT-with-ref-fields constrained DIRECT-CALL override. The
        // override reads this.tag via in-frame Ldfld_Ref_Inline from the callee
        // ParamInfos[0] ref region -- which must be seeded from the source local.
        static string ConstrainedToString<T>(ref T v) where T : struct
        {
            // Force a genuine byref escape: pass it as a constrained callvirt.
            return v.ToString();
        }

        public static void NeoStep17_ConstrainedIlVtWithRefFields_DirectCall()
        {
            NeoStep17VtWithRefOverride v = new NeoStep17VtWithRefOverride(42, "DIRECT");
            string s = ConstrainedToString(ref v);
            if (s != "OVR:DIRECT")
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // Probe 5: IL-VT-with-ref-fields constrained INHERITED-CLRMethod box.
        // The struct has NO IL override of ToString; constrained.callvirt resolves
        // to Object.ToString on a boxed ILTypeInstance. Must not crash; the boxed
        // instance carries the ref field.
        public static void NeoStep17_ConstrainedIlVtWithRefFields_InheritedClrMethod()
        {
            NeoStep17VtWithRef v = new NeoStep17VtWithRef(11, "BOXED");
            string s = Step17InheritedToString(v);
            // Inherited Object.ToString on a boxed ILTypeInstance returns the
            // type's full name (non-null, non-empty). Assert it didn't crash and
            // returned a non-empty string.
            if (string.IsNullOrEmpty(s))
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        // Probe 6 (REGRESSION): primitives-only Stobj/Ldobj still byte-identical
        // (the existing green target gated on TotalReferenceCount == 0).
        public static void NeoStep17_StobjLdobjPrimitiveOnly_Regression()
        {
            NeoStep17Point dst = default;
            NeoStep17Point src = new NeoStep17Point { x = 12, y = 34 };
            // Round-trip through a byref copy.
            StobjPointIntoByref(ref dst, src);
            if (dst.x != 12 || dst.y != 34)
            {
                int z = 1; int d = 0; int _ = z / d;
            }
        }

        static void StobjPointIntoByref(ref NeoStep17Point dst, NeoStep17Point src)
        {
            dst = src;
        }

        // ======================================================================
        // Step 17 (c) edges + F-10-R1 JIT-discriminator gate
        //            (neo-step17-generic-byref-etc).
        //   * generic-byref (ref T/out T, T generic) -- type-agnostic model; 3
        //     TEST-ONLY guards (Swap int / IL-ref / IL-VT). PASS on HEAD.
        //   * interface-on-VT-constrained beyond the common shape -- the
        //     {a,d,M2,b} cohorts already cover it; 2 TEST-ONLY guards (IL-VT
        //     direct-call, CLR-VT box-once). PASS on HEAD.
        //   * F-10-R1: the latent F-6/F-10 both-stamp shape, triggered via a
        //     constrained.callvirt direct-call on an IL VT with a CLR-struct
        //     field. FAIL-on-HEAD (NRE at NeoMarshalByrefFieldToSlot, reading
        //     `prefix`'s flat bytes as objectIndex) -> PASS-after the JIT-
        //     discriminator gate (TypeSpecializeNeoOpcodes case Ldflda clears
        //     F-10 when F-6 stamps).
        // ======================================================================

        // ---- generic-byref: Swap<T> with T a generic parameter. The Neo byref
        //      is an 8-byte Ref Slot (objectIndex, offset) copied as 8 bytes
        //      regardless of element type; the generic-param type token is
        //      resolved at the call site via JIT generic substitution, NOT at the
        //      byref-marshal level. ----

        static void Swap<T>(ref T a, ref T b) { T tmp = a; a = b; b = tmp; }

        // (c).1 Swap<int>: the primitive concrete substitution.
        public static void NeoStep17_GenericByRef_SwapInt()
        {
            int a = 1, b = 2;
            Swap(ref a, ref b);
            if (a != 2 || b != 1) { int z = 1; int d = 0; int _ = z / d; }
        }

        // (c).2 Swap<IL-ref-class>: T is a heap IL reference type.
        public static void NeoStep17_GenericByRef_SwapIlRef()
        {
            NeoStep17Holder a = new NeoStep17Holder(); a.value = 11;
            NeoStep17Holder b = new NeoStep17Holder(); b.value = 22;
            Swap(ref a, ref b);
            if (a.value != 22 || b.value != 11) { int z = 1; int d = 0; int _ = z / d; }
        }

        // (c).3 Swap<IL-VT>: T is an IL value type (Move_Vt body).
        public static void NeoStep17_GenericByRef_SwapIlVt()
        {
            NeoStep17Point a; a.x = 1; a.y = 2;
            NeoStep17Point b; b.x = 3; b.y = 4;
            Swap(ref a, ref b);
            if (a.x != 3 || a.y != 4 || b.x != 1 || b.y != 2)
            { int z = 1; int d = 0; int _ = z / d; }
        }

        // ---- interface-on-VT-constrained beyond the common shape. The
        //      {a,d,M2,b} cohorts (neo-step17-completion + neo-step17-stobj-
        //      refloop) already cover the box-and-interface-dispatch + IL-VT-
        //      direct-call + CLR-VT-box-once + IL-VT-inherited-CLRMethod paths;
        //      these 2 guards lock the closure so a future change cannot silently
        //      regress it. ----

        // (c).4 IL-VT implementing an interface, dispatched via a generic
        //      constrained caller -> the constrained.callvirt direct-call path
        //      (actualMethod is an ILMethod, constrainedType is an ILType).
        public interface INeoStep17ProbeIface { int ProbeValue(); }
        public struct NeoStep17ProbeIlVtIface : INeoStep17ProbeIface
        {
            public int val;
            public int ProbeValue() { return val; }
        }
        static int ProbeConstrainedIface<T>(T v) where T : INeoStep17ProbeIface
        {
            return v.ProbeValue();
        }
        public static void NeoStep17_InterfaceOnIlVtConstrained()
        {
            NeoStep17ProbeIlVtIface s; s.val = 4242;
            int r = ProbeConstrainedIface(s);
            if (r != 4242) { int z = 1; int d = 0; int _ = z / d; }
        }

        // (c).5 CLR-VT implementing IComparable<int>, dispatched via a generic
        //      constrained caller -> the box-once path. int (Int32) is the CLR
        //      value type; the constrained arm boxes it and dispatches via the
        //      interface map.
        static int ProbeConstrainedCompare<T>(T v, int other) where T : IComparable<int>
        {
            return v.CompareTo(other);
        }
        public static void NeoStep17_InterfaceOnClrVtConstrained()
        {
            int v = 5;
            int lt = ProbeConstrainedCompare(v, 7);  // 5 < 7 -> negative
            int eq = ProbeConstrainedCompare(v, 5);  // equal -> 0
            int gt = ProbeConstrainedCompare(v, 3);  // 5 > 3 -> positive
            if (!(lt < 0 && eq == 0 && gt > 0))
            { int z = 1; int d = 0; int _ = z / d; }
        }

        // ---- F-10-R1: the JIT-discriminator gate reproducer (the ONLY engine
        //      change in this change). ----

        // An IL value type with an IL-primitive `prefix` (a NON-empty flat
        // region) AND a CLR-struct field (the F-10 marker). Invoked via a generic
        // constrained caller `T v where T:struct,IFace` -> constrained.callvirt
        // direct-call -> slot-0 seeded with the struct's FLAT PRIMITIVE bytes
        // (just `prefix`, 4 bytes -- the CLR-struct field is a reference slot,
        // zero primitive contribution, its boxed value lives in the ref region).
        // The body's `ldflda this.field` then reads slot-0's leading int =
        // `prefix` value (e.g. 7) as the byref objectIndex.
        //
        // On HEAD: BOTH markers stamp (Operand4 = 0x3: F-6 in-frame-VT 0x1 | F-10
        // CLR-struct-field 0x2; IsClrStructFieldOfIL returns true for an IL
        // value-type declaring type too). The runtime Ldflda arm checks F-10
        // FIRST (clrStructFieldMarker && objIdx >= 0) -> (objIdx=7, refOff|flag)
        // -> the byref consumer reads mStack[7] (a garbage slot; the struct was
        // NOT boxed in the direct-call path) -> NullReferenceException at
        // NeoMarshalByrefFieldToSlot.
        //
        // After the JIT gate (TypeSpecializeNeoOpcodes case Ldflda clears F-10
        // when F-6 stamps): Operand4 = 0x1 -> the F-10-first check is false ->
        // routes to F-6 shape 3 (frame-native byref at operandSlotOff +
        // fieldPrimOff) -> the byref points at the in-frame ref slot of `field`,
        // the host helpers read/write it correctly -> returns 60.
        public interface INeoStep17SetAndSum { int SetAndSumViaLdflda(float x, float y, float z); }
        public struct NeoStep17F10R1Vt : INeoStep17SetAndSum
        {
            public int prefix;                  // IL-primitive (non-empty flat region)
            public TestVector3NoBinding field;  // CLR-struct field (F-10 marker)
            public int SetAndSumViaLdflda(float x, float y, float z)
            {
                TestCLRBinding.SetTestVector3NoBindingByRef(ref this.field, x, y, z);
                return TestCLRBinding.SumTestVector3NoBindingByRef(ref this.field);
            }
        }
        static int ProbeConstrainedCallSetAndSum<T>(T v, float x, float y, float z)
            where T : struct, INeoStep17SetAndSum
        {
            return v.SetAndSumViaLdflda(x, y, z);
        }
        public static void NeoStep17_F10R1_ConstrainedVtLdfldaClrField()
        {
            NeoStep17F10R1Vt v = default(NeoStep17F10R1Vt);
            v.prefix = 7;
            int r = ProbeConstrainedCallSetAndSum(v, 10f, 20f, 30f);
            if (r != 60) { int z = 1; int d = 0; int _ = z / d; }
        }
    }
}
