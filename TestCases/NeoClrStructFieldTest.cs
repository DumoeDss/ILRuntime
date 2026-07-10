using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // F-10 / NEO-CLRSTRUCT-FIELD-OF-IL: a CLR-struct field of an IL instance.
    //
    // The ILType field-layout pass lays out a CLR-struct field as a REFERENCE
    // slot (the boxed struct lives at ManagedObjects[ReferenceOffset]; its flat
    // bytes do NOT live in Primitives). But the JIT `ldflda` of that field
    // stamped PrimitiveOffset (a stale offset past Primitives.Length) -> the
    // byref consumers (NeoMarshalByrefFieldToSlot, Ldobj/Stobj/stind/ldind) read
    // ili.Primitives[off] -> IndexOutOfRange / silent corruption.
    //
    // This change makes ldflda emit a recoverable byref encoding (carrying the
    // field's ReferenceOffset + a runtime-detectable flag) so the consumers read
    // ili.ManagedObjects[refOffset] (the boxed struct) via ReadNeoValueType /
    // WriteNeoValueType.
    //
    // Probe design constraints (the same family as Step 13b / 13-area4):
    //   * The CLR-struct field's value is sourced from a host CLR method RETURN
    //     (Make*) and checked by re-feeding to a host helper (Sum*) -- NO IL-side
    //     `ldfld` on a CLR struct field (the separate [NEO-IL-VT-INSTANCE-
    //     COVERAGE] Step-6 gap).
    //   * `ldflda` is exercised by the C# compiler's `c.f` read-then-pass-by-
    //     value lowering (`ldflda; ldobj; call`) and by a `ref`-param helper.
    //
    // Assertion mechanism: a passing test returns; a logic failure surfaces as a
    // deliberate `1/0` (DivideByZero). Tests are `public static void`.

    public class NeoClrStructFieldTest
    {
        // ---- IL classes with CLR-struct fields ----
        public class HolderOne
        {
            public int state;                          // IL-primitive field
            public TestVector3NoBinding field;         // CLR-struct field (F-10)
        }

        public class HolderTwo
        {
            public TestVector3NoBinding a;             // CLR-struct field (F-10)
            public TestVector3NoBinding b;             // second CLR-struct field
        }

        // ---- CLR-struct-WITH-reference field of an IL class + a sibling IL-
        //      primitive field (the F-10 family; modeled on the
        //      AsyncValueTaskMethodBuilder<T> shape { int n; string s; }). These
        //      holders back the storage-disjointness regression guards below
        //      (see the probes' header for the full history: the alleged "B1
        //      field-layout collision" was DISPROVED -- the shared
        //      PrimitiveOffset is BENIGN because the struct field's storage is
        //      ManagedObjects[ReferenceOffset] (disjoint from the primitive
        //      field's Primitives[PrimitiveOffset])).
        public class HolderClrStructWithRefThenPrim
        {
            public TestClrStructWithRef builder;        // CLR-struct-WITH-ref field
            public int v;                               // sibling IL-primitive field (the hoisted local)
        }

        // The reverse order (primitive FIRST, then CLR-struct-with-ref). A
        // regression guard; must stay green.
        public class HolderPrimThenClrStructWithRef
        {
            public int v;                               // IL-primitive field FIRST
            public TestClrStructWithRef builder;        // CLR-struct-WITH-ref field
        }

        // ---- probes ----

        // (4.1) minimal F-10 reproducer: set the CLR-struct field via stfld,
        // then pass it BY REF to a host helper (ldflda;call -> the F-10 byref
        // through CopyNeoCallArguments -> NeoMarshalByrefFieldToSlot).
        public static void NeoClrStructField_LdfldaByValDeref()
        {
            HolderOne c = new HolderOne();
            c.state = 1;
            c.field = TestCLRBinding.MakeTestVector3NoBinding(10f, 20f, 30f);
            int r = TestCLRBinding.SumTestVector3NoBindingByRef(ref c.field);
            if (r != 60) { int x = 1; int y = 0; int _ = x / y; }
        }

        // (4.2) multiple CLR-struct fields: ldflda each, no cross-clobber.
        public static void NeoClrStructField_MultipleFields()
        {
            HolderTwo c = new HolderTwo();
            c.a = TestCLRBinding.MakeTestVector3NoBinding(1f, 2f, 3f);
            c.b = TestCLRBinding.MakeTestVector3NoBinding(10f, 20f, 30f);
            int ra = TestCLRBinding.SumTestVector3NoBindingByRef(ref c.a);
            int rb = TestCLRBinding.SumTestVector3NoBindingByRef(ref c.b);
            if (ra != 6 || rb != 60) { int x = 1; int y = 0; int _ = x / y; }
        }

        // (4.3) a CLR-struct field round-tripped through a SECOND IL instance +
        // re-stored via stfld after the first read (the deep round-trip: box ->
        // ManagedObjects -> byref read -> host -> re-stfld -> byref read again).
        //
        // NOTE: the original intent (a CLR struct WITH a reference-type field,
        // the TaskAwaiter shape) is blocked UPSTREAM by the Step-13b binder NIE
        // ("CLR value type with reference fields and no ValueTypeBinder"). That
        // is a pre-existing limitation of ReadNeoValueType/WriteNeoValueType
        // (loud NIE, not silent corruption) and is OUT OF SCOPE for F-10 -- the
        // real TaskAwaiter<T> ships with a framework binder. This probe uses a
        // pure-primitive struct to verify the F-10 round-trip is stable across
        // re-store + cross-instance. The ref-field-struct case stays an
        // accepted-known edge (binder NIE).
        public static void NeoClrStructField_StructWithRefField()
        {
            HolderOne c1 = new HolderOne();
            c1.field = TestCLRBinding.MakeTestVector3NoBinding(1f, 2f, 3f);
            HolderOne c2 = new HolderOne();
            c2.field = TestCLRBinding.MakeTestVector3NoBinding(10f, 20f, 30f);
            // Re-store c1's field from c2's (ldfld c2.field -> stfld c1.field).
            c1.field = c2.field;
            int r1 = TestCLRBinding.SumTestVector3NoBindingByRef(ref c1.field);
            int r2 = TestCLRBinding.SumTestVector3NoBindingByRef(ref c2.field);
            if (r1 != 60 || r2 != 60) { int x = 1; int y = 0; int _ = x / y; }
        }

        // (4.4) regression: stfld/ldfld of a CLR-struct field via plain field
        // access (NO ldflda) -- the Stfld_Ref/Ldfld_Ref F-10 box/unbox path.
        // Read the field into a local (ldfld), then pass the local by ref.
        public static void NeoClrStructField_StfldLdfld_Regression()
        {
            HolderOne c = new HolderOne();
            c.field = TestCLRBinding.MakeTestVector3NoBinding(1f, 2f, 3f);
            TestVector3NoBinding copy = c.field;  // ldfld (flatten boxed -> bytes)
            int r = TestCLRBinding.SumTestVector3NoBindingByRef(ref copy);
            if (r != 6) { int x = 1; int y = 0; int _ = x / y; }
        }

        // (4.5) byref write-back: a mutating byref helper writes through the
        // F-10 byref; the mutation must land in the IL instance's ManagedObjects
        // slot (the Area-4a write-back shape, the Step-20 SetResult path).
        public static void NeoClrStructField_ByValAfterLdfldaDeref()
        {
            HolderOne c = new HolderOne();
            c.field = TestCLRBinding.MakeTestVector3NoBinding(100f, 200f, 300f);
            TestCLRBinding.MutateTestVector3NoBindingByRef(ref c.field, 1f);
            int r = TestCLRBinding.SumTestVector3NoBindingByRef(ref c.field);
            if (r != 603) { int x = 1; int y = 0; int _ = x / y; }
        }

        // (4.6) regression: IL classes with other (non-CLR-struct) field types
        // stay byte-identical. IL-primitive field, CLR-ref field, CLR-object
        // field. (IL-VT fields are the separate Step-6 Stfld_Value gap and are
        // NOT exercised here -- the F-10 marker fires ONLY for CLR-struct
        // fields, so these non-F-10 paths take the unchanged existing arms.)
        public class ILFieldHolder
        {
            public int prim;                  // IL-primitive field
            public string refField;           // CLR-ref field
        }

        public static void NeoClrStructField_OtherFieldTypes_Regression()
        {
            ILFieldHolder c = new ILFieldHolder();
            c.prim = 42;                             // IL-primitive field (Stfld_I4/Ldfld_I4)
            c.refField = "abc";                      // CLR-ref field (Stfld_Ref/Ldfld_Ref, non-F-10)
            int p = c.prim;
            int rl = c.refField.Length;
            if (p != 42 || rl != 3)
            { int x = 1; int y = 0; int _ = x / y; }
        }

        // (4.7) register-reuse escape: an ldflda-produced byref whose dest
        // register is reused by an intervening foldable, then the byref is read.
        // The F-10 marker must NOT perturb the addrAlias COEXIST gate.
        public static void NeoClrStructField_RegisterReuseEscape()
        {
            HolderOne c = new HolderOne();
            c.field = TestCLRBinding.MakeTestVector3NoBinding(5f, 6f, 7f);
            // Intervening foldable work between the field-address and its use.
            int dummy = 0;
            for (int i = 0; i < 3; i++) dummy += i;
            int r = TestCLRBinding.SumTestVector3NoBindingByRef(ref c.field);
            if (r != 18 || dummy != 3) { int x = 1; int y = 0; int _ = x / y; }
        }

        // (4.8) F-10-R1 latent-shape probe: an IL VALUE TYPE with a CLR-struct
        // field, accessed via `ldflda this.field` INSIDE a VT instance method.
        // The reviewer's F-10-R1 finding predicted this BOTH-STAMP shape (F-6
        // inlineMarker = in-frame VT source; F-10 clrStructFieldMarker = CLR-
        // struct field) would mis-dispatch under the runtime Ldflda arm's F-10-
        // first order, and recommended a 1-line runtime reorder (F-6 first).
        //
        // Fixer investigation (runtime diagnostic on this exact probe) FOUND:
        //  * Both markers DO stamp (Operand4 = 0x3) -- the discriminator-level
        //    non-mutual-exclusivity the reviewer flagged is REAL.
        //  * BUT the recommended runtime REORDER (F-6 first) is INCORRECT: it
        //    routes the objIdx == -1 case (which is EVERY reachable VT `this`
        //    and by-value VT arg today -- they arrive as managed pointers) to
        //    F-6 shape 3 (operandSlotOff + fieldPrimOff) instead of shape 1/2
        //    frame-native (vtBase + fieldPrimOff). Those produce DIFFERENT
        //    byrefs. Applying the reorder broke 6 NeoStep17 F-6-only probes
        //    (NeoStep17_TC6_RefInFrameVtField, _LdfldaInline_*). Reverted.
        //  * The F-10-first mis-dispatch the reviewer feared requires objIdx
        //    >= 0 with flat bytes -- the constrained-boxed-VT sub-case -- which
        //    is gated behind the DEFERRED constrained.callvirt-on-VT (Step 13
        //    Area 3 / Step 17 follow-up, confirmed DEFERRED in NeoStep13Test).
        //    So the F-10-first order is CORRECT for every reachable shape today;
        //    the defect is fully latent, not just unexercised.
        //  * The correct FUTURE fix (when constrained-VT lands) is the JIT
        //    discriminator gate (reviewer option (a): only stamp F-10 when the
        //    source is NOT an in-frame VT), NOT a runtime reorder.
        //
        // This probe exercises the both-stamp shape end-to-end (the byref flows
        // to a CLR host helper through CopyNeoCallArguments -> the real `call`
        // defeats the IL inliner). It PASSES under the current (HEAD, F-10-
        // first) order because objIdx == -1 routes it to shape 1/2 frame-native,
        // which handles the round-trip correctly. It is a regression guard for
        // the both-stamp shape (if a future change breaks the objIdx == -1 ->
        // shape 1/2 routing, this fails).
        public struct IlVtWithClrStructField
        {
            public int prefix;                  // IL-primitive field
            public TestVector3NoBinding field;  // CLR-struct field (F-10 marker)

            // `ldflda this.field; call` -- BOTH markers stamp (Operand4 = 0x3).
            public int SetAndSumField(float x, float y, float z)
            {
                TestCLRBinding.SetTestVector3NoBindingByRef(ref this.field, x, y, z);
                return TestCLRBinding.SumTestVector3NoBindingByRef(ref this.field);
            }
        }

        public static void NeoClrStructField_IlVtMethodLdfldaThisClrStructField()
        {
            IlVtWithClrStructField v = default(IlVtWithClrStructField);
            v.prefix = 7;
            // Seed the field THROUGH the ldflda-produced byref, then read it back.
            // Both `ldflda this.field` sites stamp Operand4 = 0x3 (F-6 | F-10);
            // the current Ldflda arm routes via objIdx == -1 -> shape 1/2 frame-
            // native and the round-trip is correct.
            int r = v.SetAndSumField(10f, 20f, 30f);
            if (r != 60) { int x = 1; int y = 0; int _ = x / y; }
        }

        // =====================================================================
        // STORAGE-DISJOINTNESS guards for a CLR-struct-WITH-reference field of
        // an IL class followed by a sibling IL-primitive field (the F-10 family).
        //
        // HISTORY / WHY THESE EXIST: the parked child `neo-async-valuetask-
        // asyncvoid` (child 4) hypothesized a "B1 field-offset collision": that
        // an async ValueTask<T> SM's <>t__builder field (a CLR struct WITH a
        // Task ref) and its hoisted int local `v` BOTH get Neo primitiveOffset 4
        // -> the builder "clobbers" v (v reads 1 not 11). This change
        // (neo-clrstruct-sm-field-layout) was scoped to fix that alleged layout
        // collision. INVESTIGATION DISPROVED THE HYPOTHESIS:
        //   * The ILType field-layout DOES assign both the CLR-struct-with-ref
        //     field and the sibling primitive field the SAME PrimitiveOffset
        //     (the struct field is a reference slot: referenceOffset++ with NO
        //     primitiveOffset advance, so the next primitive field reuses the
        //     cursor). BUT this is BENIGN: a CLR-struct-with-ref field's storage
        //     is the BOXED struct at ManagedObjects[ReferenceOffset] (Stfld_Ref
        //     / Ldfld_Ref F-10 arms), while the sibling primitive field's storage
        //     is Primitives[PrimitiveOffset] (Stfld_I4 / Ldfld_I4). Primitives[]
        //     and ManagedObjects[] are DISJOINT arrays, so the shared offset
        //     value does NOT cause corruption.
        //   * Runtime diagnostic on VT1 (stfld.i4/ldfld.i4 with primOff printing)
        //     confirmed `v` STORES 11 AND LOADS 11 -- the field-layout is NOT the
        //     corruption site.
        //   * Applying the proposed B1 fix (advance primitiveOffset for branch-3
        //     fields) did NOT change VT1's outcome (still reads resultObj=4).
        //   * The REAL VT1 root cause is in the async redirect: the builder
        //     byref-`this` passed to SetResult occupies 16 call-frame bytes
        //     (8-byte F-10 byref + 8-byte struct flat-bytes), but
        //     AsyncValueTaskMethodBuilder_T_SetResult_Neo skips only 8
        //     (curPrim += 8) -> ReadResultParam reads the int result from
        //     frameBase[8] (stale) instead of frameBase[16] (the actual 14).
        //     That is a call-argument-marshalling bug in child-4's async
        //     redirect scope, NOT a field-layout bug.
        //
        // These probes are kept as DURABLE REGRESSION GUARDS: they pin the
        // invariant that a CLR-struct-with-ref field + a sibling primitive field
        // (in either declaration order, and with two surrounding primitives) do
        // NOT corrupt each other via the shared PrimitiveOffset. They PASS on
        // HEAD (the disjoint storage) and must stay green. They are NOT
        // FAIL-on-HEAD reproducers (the alleged B1 bug does not reproduce).
        // =====================================================================

        // (B1.1) CLR-struct-with-ref field FIRST, then a sibling int. Asserts the
        // sibling int is NOT clobbered by the struct field's stfld (disjoint
        // Primitives vs ManagedObjects storage). PASS on HEAD; regression guard.
        public static void NeoClrStructField_ClrStructWithRefThenPrim_NoClobber()
        {
            HolderClrStructWithRefThenPrim c = new HolderClrStructWithRefThenPrim();
            c.builder = default(TestClrStructWithRef);   // stfld the CLR-struct-with-ref field
            c.v = 11;                                    // stfld the sibling int
            int readBack = c.v;                          // ldfld the sibling int
            if (readBack != 11) { int x = 1; int y = 0; int _ = x / y; }
        }

        // (B1.2) write order independence: set v FIRST, then the builder, read v.
        // The shared PrimitiveOffset must not corrupt v regardless of write
        // order. PASS on HEAD; regression guard.
        public static void NeoClrStructField_ClrStructWithRefThenPrim_WriteOrder()
        {
            HolderClrStructWithRefThenPrim c = new HolderClrStructWithRefThenPrim();
            c.v = 11;                                    // stfld the sibling int FIRST
            c.builder = default(TestClrStructWithRef);   // stfld the CLR-struct-with-ref field
            int readBack = c.v;                          // ldfld the sibling int
            if (readBack != 11) { int x = 1; int y = 0; int _ = x / y; }
        }

        // (B1.3) the reverse-order regression guard: primitive field FIRST, then
        // the CLR-struct-with-ref. Must stay green (regression guard).
        public static void NeoClrStructField_PrimThenClrStructWithRef_Regression()
        {
            HolderPrimThenClrStructWithRef c = new HolderPrimThenClrStructWithRef();
            c.v = 11;
            c.builder = default(TestClrStructWithRef);
            int readBack = c.v;
            if (readBack != 11) { int x = 1; int y = 0; int _ = x / y; }
        }

        // (B1.4) two sibling primitive fields after a CLR-struct-with-ref field:
        // { CLRStructWithRef builder; int a; int b; }. Both a and b must be
        // unclobbered AND distinct (no cross-clobber between the two ints or with
        // the builder). PASS on HEAD; regression guard.
        public class HolderClrStructWithRefBetweenTwoPrims
        {
            public TestClrStructWithRef builder;         // CLR-struct-WITH-ref field
            public int a;                                // sibling int 1
            public int b;                                // sibling int 2
        }

        public static void NeoClrStructField_ClrStructWithRefBetweenTwoPrims()
        {
            HolderClrStructWithRefBetweenTwoPrims c = new HolderClrStructWithRefBetweenTwoPrims();
            c.builder = default(TestClrStructWithRef);
            c.a = 11;
            c.b = 22;
            int ra = c.a;
            int rb = c.b;
            if (ra != 11 || rb != 22) { int x = 1; int y = 0; int _ = x / y; }
        }
    }
}
