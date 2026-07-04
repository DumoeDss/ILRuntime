using System;

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
    }
}
