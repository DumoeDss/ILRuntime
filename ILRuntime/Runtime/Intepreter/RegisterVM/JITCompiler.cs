using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ILRuntime.Mono.Cecil;
using ILRuntime.Mono.Cecil.Cil;

using ILRuntime.CLR.TypeSystem;
using ILRuntime.CLR.Method;
using ILRuntime.Runtime.Intepreter.OpCodes;

namespace ILRuntime.Runtime.Intepreter.RegisterVM
{
#if ENABLE_NEO_MODE
    internal enum NeoPrimitiveTypeTag : int
    {
        I4 = 0,
        U4 = 1,
        I8 = 2,
        U8 = 3,
        R4 = 4,
        R8 = 5,
    }

    struct NeoCallParamMap
    {
        public ushort[] PrimitiveSrc;
        public ushort[] PrimitiveDst;
        public ushort[] PrimitiveSize;
        public ushort[] RefSrc;
        public ushort[] RefDst;
        // Step 13 Area 4b: per-prim-slot flag -- when true, the source slot holds
        // an 8-byte frame-native byref (a Ref Slot (-1, structFrameOff) produced
        // by ldloca) and CopyNeoCallArguments must DEREFERENCE it (read the byref,
        // copy PrimitiveSize[i] bytes from frameBase + the byref's offset half)
        // rather than copy the byref bytes verbatim. Set for a CLR value-type
        // instance `this` slot (the C# compiler lowers `local.VTMethod()` and
        // `new VT(args)` to `ldloca; call`, so the `this` source is always a
        // byref). The dest (callee param region) receives the struct's flat
        // bytes, so the readers read it exactly like a by-value struct param.
        //
        // Step 13 Area 4c: ALSO set for a CLR-method byref PARAM (ref/out/in).
        // A byref param source holds an 8-byte Ref Slot that may be frame-native
        // (-1, off) OR an mStack-object field (objIdx >= 0, fieldHash); the copy
        // derefs BOTH shapes (the mStack-object sub-case routes through the field
        // accessor). The dest (callee param region) is sized by the element type
        // (de-byref'd), so the reader reads flat bytes like a by-value param of
        // the element type.
        public bool[] PrimitiveByRefSrc;
        // Step 13 Area 4c: per-prim-slot write-back gate. When true, the post-
        // call reverse copy (CopyNeoCallWriteBack) writes the (possibly-mutated)
        // dest slot bytes BACK through the source byref. Set for a `ref`/`out`
        // param (gate on !IsIn || IsOut) and for a mutating VT `this` slot. NOT
        // set for an `in`-only param (CLR contract forbids mutation). Absent for
        // any by-value param (no byref to write through).
        public bool[] PrimitiveByRefWriteBack;
        // Step 13 Area 4c: per-prim-slot element CLR Type. For a byref PARAM
        // whose Ref Slot is mStack-object (a `ref obj.field` shape), the copy
        // helper must read/write the FIELD via the field accessor and flatten/
        // re-box per the element type. The element type is captured here (null
        // for a frame-native byref -- the byte width alone suffices there, and
        // null for a non-byref slot).
        public System.Type[] PrimitiveByRefElemType;
        // neo-il-struct-box-call-boundary: per-prim-slot boxing descriptor for
        // an IL value-type struct passed as a REFERENCE-typed param at a
        // Callvirt_CLR / Call / Call_Redirect boundary. When non-null, the
        // source slot holds the IL-struct's FLAT BYTES (+ its ref region) but
        // the resolved CLR param is ILTypeInstance / object (a reference) --
        // the canonical case is a CLR generic collection over an IL struct
        // (List<Anim>.Add -> List<ILTypeInstance>.Add; the C# compiler emits
        // NO CIL box because at source level the param is the value type T,
        // but ILRuntime resolves T to ILTypeInstance). Raw-copying the struct
        // bytes into the dest 4-byte ref slot puts the struct's first primitive
        // field bytes where the autogen reader expects an mStack index ->
        // garbage index -> OOB / NRE. CopyNeoCallArguments must instead BOX the
        // struct (ilType.Instantiate(false) + CopyFrameToIL) into a fresh mStack
        // slot and write THAT index. PrimitiveBoxIlType[i] is the struct's ILType
        // (null = no box for this slot); PrimitiveBoxSrcRefOff[i] is the struct's
        // caller-frame ref offset (the source of its ref-region fields). The
        // struct's prim source offset is map.PrimitiveSrc[i]; sizes come from the
        // ILType. Mirrors the Box arm (Instantiate + CopyFrameToIL) and the
        // neo-array-multidim-ilvt Set box (TryNeoIlVtElementArrayCall).
        public CLR.TypeSystem.ILType[] PrimitiveBoxIlType;
        public ushort[] PrimitiveBoxSrcRefOff;
    }
#endif
    struct StackSlotInfo
    {
        public int Offset;
        public int RefOffset;
        public int Size;
        public int RefCount;
    }
    struct CompiledFrame
    {
        public OpCodeR[] CodeBody;
        public int StackRegisterCount;
        public Dictionary<int, int[]> SwitchTargets;
        public Dictionary<int, RegisterVMSymbol> Symbols;
        public StackSlotInfo[] LocalInfos;
        public int TotalStructSize;
        public int TotalRefSize;
#if ENABLE_NEO_MODE
        public NeoCallParamMap[] NeoCallParams;
        // neo-il-struct-box-call-boundary: the per-register inferred type map
        // produced by TypeSpecializeNeoOpcodes (the eval-stack dataflow). Read by
        // LowerNeoOffsets' call-param map-build to decide whether a call arg
        // source is a flat-bytes IL value-type struct (needs boxing when the
        // resolved CLR param is a reference). Set once in RunNeoBackHalf right
        // after TypeSpecializeNeoOpcodes; null in any path that skips the back-
        // half (the map-build null-checks it).
        public IType[] NeoRegisterTypes;
        public StackSlotInfo[] ParamInfos;
        public int ParamPrimitiveSize;
        public int ParamReferenceCount;
        public int LocalsPrimitiveSize;
        public int LocalsReferenceCount;
        public int ReturnPrimitiveSize;
        public int ReturnRefCount;
        public bool[] LocalIsReference;
        // Body executed by ExecuteNeo. Same opcode shape as CodeBody but with
        // Register1/2/3 lowered to byte offsets via LowerNeoOffsets. CodeBody
        // itself stays in register-index form so inliner / debugger / future
        // AOT serialization can keep operating on a stable representation.
        public OpCodeR[] NeoExecuteBody;
        // Step 14: catch-handler exception variable slot. -1 when the method
        // has no catch handler. ExecuteNeo writes the caught exception object
        // into this ref slot on catch entry (mirroring Legacy's
        // AssignToRegister(exReg, ex)). The slot is reserved in
        // AllocateLocalStackSpaces at the catch exception register's
        // post-compaction index.
        public int NeoCatchExceptionRegIndex;
        public int NeoCatchExceptionByteOffset;
        public int NeoCatchExceptionRefOffset;
#endif
    }
    struct JITCompiler
    {
        public const int CallRegisterParamCount = 3;
        // F-6 / NEO-VT-FLDADDR: Operand4 flag bit stamped on a real `Ldflda`
        // whose source register is an in-frame IL value type (the same condition
        // that seeds the dest type in TypeSpecializeNeoOpcodes). Tells the runtime
        // Ldflda arm the operand slot may hold the struct's FLAT BYTES (e.g. a
        // constrained-boxed `this`), not a Ref Slot. See ILIntepreter.Neo.cs
        // `case Ldflda`. Standalone Operand4 (offset 20); collision-free (no other
        // Ldflda path writes Operand4).
        public const int NeoLdfldaInlineMarker = 0x1;

        // neo-array-multidim-ilvt (sub-gap 1): the IL value-type element type of
        // a multi-dimensional IL-VT-element array call, keyed by the Cecil method-
        // token hash (the same key stamped on op.Operand2 for Call/Callvirt/Newobj).
        // The token's declaring type is the IL array type (e.g. NeoStep16Vt[,]),
        // whose element type is the IL-VT; the resolved CLR ctor/Set/Get method
        // (on the shared CLR type ILTypeInstance[,]) has LOST the element type
        // (all IL-VTs share ILTypeInstance[,]). This map -- populated at JIT
        // InitializeFunctionParam (which has the Cecil token) -- lets the Neo
        // call arms recover the element ILType to box/unbox the IL-VT element.
        // Keyed per-token (per-IL-array-type), so it is unambiguous across
        // distinct IL-VT element types. Static + concurrent-safe (JIT is
        // multi-threaded via Prewarm); values are immutable once set.
        static readonly System.Collections.Concurrent.ConcurrentDictionary<int, ILType> s_neoIlVtArrayElementTypes =
            new System.Collections.Concurrent.ConcurrentDictionary<int, ILType>();

        public static ILType GetNeoIlVtArrayElementType(int methodTokenHash)
        {
            ILType t;
            return s_neoIlVtArrayElementTypes.TryGetValue(methodTokenHash, out t) ? t : null;
        }

        // F-10 / NEO-CLRSTRUCT-FIELD-OF-IL: a CLR-struct field of an IL instance
        // is laid out by the ILType field-layout pass as a REFERENCE slot (the
        // boxed struct lives at ManagedObjects[ReferenceOffset]; its flat bytes
        // do NOT live in Primitives). But the heap field-access opcodes
        // (Stfld_Ref / Ldfld_Ref / Ldflda) selected for a non-primitive field
        // unconditionally treat the offset as a Primitives byte offset (and the
        // stfld source as a ref-slot mStack index) -- wrong for a CLR-struct
        // field. The F-10 discriminator: when the declaring type is an ILType
        // AND the field's type is a CLR value type (not an ILType, IsValueType),
        // stamp the field's type hash into the opcode's standalone Operand4
        // (offset 20). At runtime:
        //   * Stfld_Ref / Ldfld_Ref: Operand4 != 0 -> F-10. Resolve the field's
        //     CLR type via AppDomain.GetType(Operand4), and box/unbox the struct
        //     between the flat-bytes source/dest and ManagedObjects[ReferenceOffset]
        //     via ReadNeoValueType / WriteNeoValueType.
        //   * Ldflda: the type-spec `case Ldflda` OR-stamps bit 0x2 (the F-10
        //     marker) alongside F-6's 0x1 (mutually exclusive shapes); the
        //     runtime Ldflda arm produces a byref carrying the ReferenceOffset
        //     + a high-bit flag so the byref consumers (Ldobj/Stobj/stind/ldind)
        //     route to ManagedObjects[refOff] (see ILIntepreter.Neo.cs).
        // Collision-free: Stfld_Ref/Ldfld_Ref never write Operand4 elsewhere;
        // Ldflda's F-6 marker is bit 0x1 (F-10 is bit 0x2; the shapes are
        // mutually exclusive -- F-6 source is an in-frame VT, F-10 source is a
        // heap IL ref). A field-type hash of exactly 0 is the only ambiguous
        // case (astronomically rare; falls back to the pre-F-10 path, does not
        // corrupt other fields).
        public const int NeoLdfldaClrStructFieldMarker = 0x2;
        // neo-byref-ldind-ref-heap: a HEAP IL REFERENCE-typed field (string /
        // IL-class / object field declared on an IL heap class) is laid out as a
        // ManagedObjects slot at ReferenceOffset (NOT a Primitives byte offset).
        // Stamped on Ldflda's standalone Operand4 (bit 0x4) when the declaring
        // type is an ILType AND the field is a non-value, non-primitive type (a
        // reference field). Mutually exclusive with F-6 (0x1, in-frame-VT source)
        // and F-10 (0x2, CLR-struct field) -- a reference field is neither an
        // in-frame VT nor a CLR value type. The runtime Ldflda arm produces a
        // byref carrying (objIdx, ReferenceOffset) (NO offset-flag); the
        // ldind_ref/stind_ref consumer arms dispatch on `mStack[objIdx] is
        // ILTypeInstance` (content-based) to route to ManagedObjects[refOff].
        // (A bit-flag in the offset half was REJECTED: CLR field-hash offset
        //  values are non-deterministic and can set high bits, colliding with any
        //  flag bit and causing intermittent mis-dispatch on the 4d CLR-object
        //  path. Content-based dispatch avoids the collision entirely.)
        public const int NeoLdfldaHeapIlRefFieldMarker = 0x4;
        // neo-ldind-stind-byref-clr-struct (child 15): a CLR value-type LOCAL
        // (inline flat managed bytes in the frame, e.g. `TestVector3 a;`) whose
        // field is addressed by `ldflda`. The body's `case Code.Ldflda` stamps
        // `op.Operand2 = offset.PrimitiveOffset`; for a CLR (non-IL) declaring
        // type, `AppDomain.GetFieldOffset` returns `PrimitiveOffset =
        // type.GetFieldIndex(token)` which is `FieldInfo.GetHashCode()` (a large
        // arbitrary 32-bit hash) -- NOT a byte offset. The runtime `ldflda`
        // frame-native branch (`objectIndex == -1`, from `ldloca <CLR-struct
        // local>`) would then produce `(-1, vtBase + <huge hash>)`, so the
        // following `ldind_*`/`stind_*` deref `frameBase + <huge hash>` -> AV.
        // This marker (bit 0x8, the last free bit in standalone Operand4) tells
        // the runtime arm to resolve the field's REAL managed byte offset (cached
        // `Marshal.OffsetOf`) and use it instead of the hash. Mutually exclusive
        // with F-6 (0x1) / F-10 (0x2) / heap-IL-ref (0x4): those require the
        // declaring `type is ILType`, whereas this requires `type is CLRType`. The
        // F-6 type-spec gate (`TypeSpecializeNeoOpcodes` `case Ldflda`) fires only
        // for an IL value-type source and clears only bits 0x2/0x4, so it never
        // touches bit 0x8 and never fires for a CLR-struct source. Sound because
        // only blittable CLR structs reach the flat-byte local path (a CLR VT
        // with reference fields throws the Step-13b NIE inside
        // ReadNeoValueType/WriteNeoValueType first).
        public const int NeoLdfldaClrStructLocalFieldMarker = 0x8;
        // neo-nested-ldflda-byref: marks an `Ldflda` whose operand is a BYREF
        // produced by a preceding address-of (Ldflda / Ldsflda) -- i.e. this
        // ldflda drills INTO a struct field's address. The canonical shape is
        // `outer.Struct.field += N` which lowers to `ldflda Struct(on outer);
        // ldflda field(on the struct byref); ldind.i4; add; stind.i4`. The
        // inner ldflda's operand slot holds the outer byref
        // (containingObjIdx, structFieldOff); without this marker the runtime
        // Ldflda arm treats the byref's objIdx half as a direct heap index and
        // re-stamps its own fieldPrimOff, dropping the struct-field
        // indirection -> the following ldind/stind mis-resolve the inner field
        // on the wrong (containing) object. Stamped on the inner Ldflda's
        // standalone Operand4 (bit 0x10, the next free bit after
        // 0x1/0x2/0x4/0x8). The CIL predecessor (Ldflda/Ldsflda) is the
        // reliable JIT-time signal that the operand is a byref (the untyped
        // Neo frame cannot distinguish a byref's objIdx half from a heap
        // object's mStack index at runtime -- same crux as child-24/29 raw-
        // Ldfld). Mutually exclusive with the in-frame-VT source shapes: the
        // frame-native nested chain (`ldloca; ldflda; ldflda`, objIdx == -1)
        // is already handled by the existing `objIdx == -1` branch (vtBase +
        // fieldOff), so the runtime nested branch is gated on objIdx >= 0.
        // Prefixes (readonly./constrained./unaligned.) precede the address-
        // producer and never sit between it and the inner ldflda; a prefix ON
        // the inner ldfld itself is a rare edge that skips the marker (fails
        // safe, same as child-24/29).
        public const int NeoLdfldaNestedByRefMarker = 0x10;
        // neo-raw-ldfld-array-element: marks a raw `Ldfld` (CLR-declaring-type
        // field, the typed-splitter's CLRType `else` branch) whose owner is a
        // CLR-struct ARRAY ELEMENT byref produced by `ldelema` (the CIL shape
        // `x = clrStructArray[i].field;` -> `ldelema T; ldfld field`). This is
        // on the raw `Ldfld` opcode's Operand4 -- a DIFFERENT opcode namespace
        // from the four Ldflda markers above (0x1/0x2/0x4/0x8), so bit 0x1 is
        // collision-free (raw Ldfld's CLRType branch sets only OperandLong,
        // leaving Operand4 == 0; child-21's TypeSpecializeNeoOpcodes raw-Ldfld
        // seeding case reads only OperandLong/Register1). Without this marker
        // the runtime value-type-owner branch cannot distinguish a flat-bytes
        // local-value owner (ldloc/ldsfld of a CLR struct by value) from an
        // array-element byref owner: the untyped Neo frame holds both in the
        // same SrcOffset slot, and ReadNeoValueType would reinterpret the
        // (arrIdx, elementIdx) byref ints as the struct's first two fields ->
        // silent wrong value. Stamped when `ins.Previous` is `Code.Ldelema`
        // (the ldelema's dest register IS the ldfld's owner register). The
        // marker survives LowerNeoOffsets (the raw-Ldfld case explicitly does
        // NOT touch Operand4). Direct `arr[i].field` shape only -- ref-local
        // indirection (`ref var p = ref arr[i]; p.field`) is a documented
        // follow-up, not a regression (HEAD behavior).
        public const int NeoRawLdfldArrayElementByRefMarker = 0x1;
        // neo-raw-ldfld-clr-object-vt-field: a raw Ldfld whose owner is a byref
        // produced by `ldflda <CLR-struct field of a CLR object>` (the READ
        // counterpart of child-27's Stfld fix). The owner slot holds the 8-byte
        // byref (objIdx, structFieldHash) -- NOT flat managed bytes. The untyped
        // Neo frame cannot distinguish a flat-bytes owner (ldloc structByValue;
        // ldfld) from this byref at runtime (a flat-bytes struct's first int
        // field can coincidentally index a real object in mStack -> a constructible
        // collision), so mark the shape at JIT time. Stamped when `ins.Previous`
        // is `Code.Ldflda` -- mutually exclusive with the 0x1 Ldelema marker above
        // (a CIL instruction has exactly one immediate predecessor: it is EITHER
        // Ldelema OR Ldflda, never both). Bit 0x2 of the raw-Ldfld Operand4
        // (disjoint namespace from the Ldflda-opcode markers 0x1/0x2/0x4/0x8).
        // The marker survives LowerNeoOffsets / TypeSpecializeNeoOpcodes (same
        // argument as 0x1: the raw-Ldfld case does not touch Operand4).
        public const int NeoRawLdfldClrObjectFieldByRefMarker = 0x2;
        // neo-raw-ldfld-boxed-ref-owner: a raw Ldfld whose owner is a CLR value
        // type stored as a BOXED REFERENCE -- the Neo calling convention lays out
        // a CLR value-type PARAMETER as Size=4/RefCount=1 (an mStack index of the
        // boxed struct; see AllocateSlotForType's `else` branch), NOT flat managed
        // bytes like a CLR value-type LOCAL (AllocateLocalStackSpaces). So
        // `ldarg <clrStructParam>; ldfld <field>` -- the canonical shape of a
        // delegate lambda `v => v.field` whose v is a CLR struct -- presents the
        // raw-Ldfld IsValueType arm with an owner slot holding an mStack index,
        // which the flat-bytes ReadNeoValueType path would reinterpret as the
        // struct's first field (silent corruption: `v.X` returns *(float*)&index).
        // The untyped Neo frame cannot distinguish a flat-bytes local owner from
        // this boxed-ref param owner at runtime (same constructible collision as
        // 0x1/0x2), so mark the shape at JIT time. Stamped when `ins.Previous` is
        // an `ldarg` (the param load) -- mutually exclusive with the 0x1 Ldelema
        // and 0x2 Ldflda markers (one CIL predecessor per instruction). Bit 0x4
        // of the raw-Ldfld Operand4 (disjoint from 0x1/0x2; survives all passes:
        // LowerNeoOffsets raw-Ldfld does not touch Operand4, the push-deletion
        // remap never decrements a 0x4, TypeSpecializeNeoOpcodes reads only
        // OperandLong/Register1).
        public const int NeoRawLdfldBoxedRefOwnerMarker = 0x4;
        // neo-initobj-ref-byref: marks an Initobj whose reference-type target
        // is reached through a BYREF -- the `result = default(T)` shape on a
        // `ref T result` byref parameter (CIL `ldarg <byref param>; initobj T`,
        // e.g. TestCases.RefOutTest.UnitTest_NestedGenericRefOutSub2<string>).
        // The initobj's DstOffset then points at the frame slot HOLDING the
        // 8-byte byref (objIdx, off), NOT at the target slot. The runtime
        // reference-type arm must DEREF through the byref and write the -1 null
        // sentinel at the TARGET (mirrors Stind_Ref with vIdx = -1), not
        // direct-write the byref temp (which silently loses the `default` and
        // leaves the reference local non-null). Stamped in `case Code.Initobj`
        // when T is a reference type AND the CIL predecessor is an `ldarg` (the
        // genuine-byref shape, never addr-alias-folded). This is the
        // CIL-producer-scan fix: the `ldarg` signal is stable CIL order and
        // sidesteps the unreliable runtime alias/localIsRef state that defeated
        // the 4 prior recluster-21 runtime approaches (all of which regressed
        // ActivatorCreateInstanceWithArgsTest + InheritanceTest20 because an
        // EqualityComparer `default(T)` FOLDED stack-temp reads the same
        // objIdx==-1 at runtime as a frame-native byref). Bit 0x1 of the
        // Initobj Operand4 -- a disjoint opcode namespace (Initobj's Operand4
        // is otherwise untouched; the only other Initobj operand stamp is
        // Operand3=RefOffset for the in-frame VT path). The marker survives
        // LowerNeoOffsets (Initobj case sets Operand3/DstOffset only) and the
        // Neo inliner (copies OpCodeR by value, register-remap only).
        public const int NeoInitobjByRefOperandMarker = 0x1;
        // The runtime byref offset-half flag for an F-10 byref (set by the
        // Ldflda arm): the offset half carries (ReferenceOffset | this flag) so
        // the consumer arms can distinguish "this offset is a ManagedObjects
        // ref-slot index" from a Primitives byte offset. Bit 30 (avoids the sign
        // bit so the offset stays a positive int); ReferenceOffsets are tiny.
        public const int NeoF10ByrefOffsetFlag = unchecked((int)0x40000000);
        // neo-stfld-value-inframe-owner: Stfld_Value / Ldfld_Value (Step 12b
        // whole-IL-VT field store/load) have NO _Inline opcode -- the typed
        // scalar arms get a distinct _Inline opcode via the rewrite above, but
        // the Value variants are deliberately excluded (whole-VT copy), so the
        // SAME plain opcode reaches ExecuteNeo for both a heap ILTypeInstance
        // owner (slot holds an mStack index) and an IN-FRAME IL-struct owner
        // (a value-type LOCAL or value-type `this`, whose flat bytes sit
        // directly in the frame at DstOffset/SrcOffset). The runtime arm must
        // distinguish these; the untyped Neo frame cannot (a small in-frame
        // struct's first int field can coincidentally be a valid mStack index
        // pointing to an ILTypeInstance -- the same constructible collision as
        // the raw-Ldfld markers above), so the owner representation is resolved
        // at JIT time. Operand (@8 = the declaring-type hash) is DEAD for the
        // Value variants (neither LowerNeoOffsets nor the ExecuteNeo arm reads
        // it), so it is repurposed as the discriminator. Stamped unconditionally
        // for every Value variant in TypeSpecializeNeoOpcodes from the owner
        // register's dataflow type (both arms -- a heap type-hash could
        // otherwise collide with the marker value).
        public const int NeoValueFieldInFrameOwnerMarker = 1;
        public const int NeoValueFieldHeapOwnerMarker = 0;
        Enviorment.AppDomain appdomain;
        ILType declaringType;
        ILMethod method;
        MethodDefinition def;
        bool hasReturn;
        Dictionary<Instruction, int> entryMapping;
        Dictionary<int, int[]> jumptables;
#if ENABLE_NEO_MODE
        // Step 22: template-capture hook. When non-null, Compile writes the T-
        // invariant front-half artifacts (register-index body after CleanupRegister
        // + metadata + symbols + addr + the auto-Initobj prefix registers) into
        // this bag, for GenericMethodTemplateOps.StoreFromCapture (the template is
        // captured from a concrete instantiation's front-half, NOT from a Compile
        // of the open definition). The back-half still runs normally afterward
        // (its output is the instance's own body).
        internal TemplateCapture templateCapture;
        internal sealed class TemplateCapture
        {
            public OpCodeR[] TemplateBody;
            public short LocVarRegStart;
            public int TotalRegCnt;
            public short NeoCatchExRegFinal;
            public int StackRegisterCount;
            public Dictionary<Instruction, int> Addr;
            public Dictionary<int, RegisterVMSymbol> Symbols;
            public Dictionary<int, int[]> SwitchTargets;
            public int[] InitObjPrefixRegisters;  // Register1 of each auto-Initobj prefix op
            public TypeReference[] VariableTypes;
            public int VarCnt;
            // The `constrained. T` Cecil prefix TypeReferences, in CIL order. The
            // Constrained op's recorded symbol does NOT reliably point at the
            // `constrained.` Cecil prefix (BLOCKER-1: the JIT emits Constrained+
            // callvirt as a pair and re-keys the symbol to the trailing callvirt;
            // CleanupRegister can scramble it further to an unrelated branch/ret).
            // So the T TypeReference is captured here directly from the CIL body
            // (CIL order == template-body order: each constrained.+callvirt pair
            // becomes exactly one Constrained op and is never inlined/reordered).
            // Consumed in body order by ExtractPatches.
            public TypeReference[] ConstrainedTypeTokens;
            // The trailing callvirt's Cecil MethodReference per `constrained.`
            // prefix, in CIL order (paired 1:1 with ConstrainedTypeTokens). A
            // T-QUALIFIED callvirt (e.g. IComparable<T>::CompareTo) has a method
            // token (Operand2) that is concrete-T-dependent; CloneAndPatch must
            // re-emit it (MAJOR-2) or the runtime Constrained arm calls the
            // capture-T method on the wrong type. Null entry when the trailing
            // callvirt carries no method token / is not T-qualified.
            public MethodReference[] ConstrainedMethodTokens;
        }
#endif

        public JITCompiler(Enviorment.AppDomain appDomain, ILType declaringType, ILMethod method)
        {
            this.appdomain = appDomain;
            this.declaringType = declaringType;
            this.method = method;
            def = method.Definition;
            hasReturn = method.ReturnType != appdomain.VoidType;
            entryMapping = null;
            jumptables = null;
#if ENABLE_NEO_MODE
            templateCapture = null;
#endif
        }

        bool CheckNeedInitObj(CodeBasicBlock block, short reg, bool hasReturn, HashSet<CodeBasicBlock> visited)
        {
            if (visited.Contains(block))
                return false;
            visited.Add(block);
            for (int i = 0; i < block.FinalInstructions.Count; i++)
            {
                var ins = block.FinalInstructions[i];
                short r1, r2, r3, rw;
                Optimizer.GetOpcodeDestRegister(ref ins, out rw);
                if (Optimizer.GetOpcodeSourceRegister(ref ins, hasReturn, out r1, out r2, out r3))
                {
                    if (r1 == reg || r2 == reg || r3 == reg)
                    {
                        if (ins.Code == OpCodeREnum.Ldloca || ins.Code == OpCodeREnum.Ldloca_S)
                        {
                            if (i < block.FinalInstructions.Count - 1)
                            {
                                var next = block.FinalInstructions[i + 1];
                                if (next.Code == OpCodeREnum.Initobj && next.Register1 == rw)
                                    return false;
                            }
                            return true;
                        }
                        else
                            return rw != reg;
                    }
                }
                if (rw == reg)
                    return false;
            }
            if (block.NextBlocks != null && block.NextBlocks.Count > 0)
            {
                foreach (var i in block.NextBlocks)
                {
                    if (CheckNeedInitObj(i, reg, hasReturn, visited))
                        return true;
                }
            }

            return false;
        }

        bool IsCatchHandler(CodeBasicBlock block, MethodBody body)
        {
            if (body.HasExceptionHandlers)
            {
                var firstIns = block.Instructions[0];
                foreach(var eh in body.ExceptionHandlers)
                {
                    if(eh.HandlerType == Mono.Cecil.Cil.ExceptionHandlerType.Catch)
                    {
                        if (eh.HandlerStart == firstIns)
                            return true;
                    }
                }
                return false;
            }
            else
                return false;
        }

#if ENABLE_NEO_MODE
        List<IType> GatherValueTypes(ref CompiledFrame frame)
        {
            List<IType> valueTypes = new List<IType>();
            var body = frame.CodeBody;
            var domain = method.AppDomain;
            for (int i = 0; i < body.Length; i++)
            {
                var code = body[i];
                IType type = null;
                switch (code.Code)
                {
                    case OpCodeREnum.Ldobj:
                    case OpCodeREnum.Stobj:
                    case OpCodeREnum.Box:
                    case OpCodeREnum.Unbox:
                    case OpCodeREnum.Unbox_Any:
                    case OpCodeREnum.Isinst:
                    case OpCodeREnum.Castclass:
                    case OpCodeREnum.Constrained:
                    case OpCodeREnum.Sizeof:
                    case OpCodeREnum.Newarr:
                    case OpCodeREnum.Ldfld_Ref:
                    case OpCodeREnum.Ldfld_Value:
                    case OpCodeREnum.Stfld_Ref:
                    case OpCodeREnum.Stfld_Value:
                        type = domain.GetType(code.Operand);
                        break;
                    case OpCodeREnum.Ldfld:
                    case OpCodeREnum.Stfld:
                    case OpCodeREnum.Ldsfld:
                    case OpCodeREnum.Stsfld:
                        type = domain.GetType((int)(code.OperandLong >> 32));
                        break;
                    // VT-THIS-ADDR: a Newobj of an IL value type produces a dest
                    // temp that must hold the FULL constructed VT (the runtime
                    // Newobj IL-VT branch constructs it in the dest's frame byte
                    // region). The temp-slot sizer (AllocateLocalStackSpaces)
                    // sizes every temp to `maxSize` gathered from this list, so
                    // the constructed VT MUST be in the gather set -- otherwise a
                    // VT larger than 8 bytes (the default) gets an undersized
                    // temp and a subsequent `Move` of the dest copies only the
                    // first 8 bytes (silent truncation). Resolve the ctor and
                    // gather its declaring type when it is an IL value type.
                    case OpCodeREnum.Newobj:
                        {
                            var ctor = domain.GetMethod(code.Operand2);
                            if (ctor != null && ctor.DeclearingType != null)
                                type = ctor.DeclearingType;
                        }
                        break;
                }
                if (type != null && type.IsValueType && !type.IsPrimitive)
                {
                    valueTypes.Add(type);
                }
            }
            return valueTypes;
        }
#endif

        public void Compile(Dictionary<Instruction, int> addr, ref CompiledFrame frame)
        {
#if DEBUG && !NO_PROFILER
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == method.AppDomain.UnityMainThreadID)

#if UNITY_5_5_OR_NEWER
                UnityEngine.Profiling.Profiler.BeginSample("JITCompiler.Compile");
#else
                UnityEngine.Profiler.BeginSample("JITCompiler.Compile");
#endif

#endif
            method.Compiling = true;
            Dictionary<int, RegisterVMSymbol> symbols = new Dictionary<int, RegisterVMSymbol>();

            var body = def.Body;
            short locVarRegStart = (short)def.Parameters.Count;
            if (!def.IsStatic)
                locVarRegStart++;
            short baseRegIdx = (short)(locVarRegStart + body.Variables.Count);
            short baseRegStart = baseRegIdx;

            var blocks = CodeBasicBlock.BuildBasicBlocks(body, out entryMapping);

            foreach (var i in blocks)
            {
                baseRegIdx = baseRegStart;
                if (IsCatchHandler(i, body))
                    baseRegIdx++;
                else
                {
                    if (i.PreviousBlocks.Count > 0)
                    {
                        foreach (var j in i.PreviousBlocks)
                        {
                            if (j.EndRegister >= 0)
                            {
                                baseRegIdx = j.EndRegister;
                                break;
                            }
                        }
                    }
                }
                foreach (var ins in i.Instructions)
                {
                    Translate(i, ins, locVarRegStart, ref baseRegIdx);
                }
                i.EndRegister = baseRegIdx;
            }

            //Append init local
            var first = blocks[0];
            int idx = 0;
            int appendIdx = 0;
            HashSet<CodeBasicBlock> visitedBlocks = body.Variables.Count > 0 ? new HashSet<CodeBasicBlock>() : null;
            for (short r = locVarRegStart; r < locVarRegStart + body.Variables.Count; r++)
            {
                visitedBlocks.Clear();
                foreach (var b in blocks)
                {
                    if (b.PreviousBlocks.Count == 0)
                    {
                        var lt = def.Body.Variables[r - locVarRegStart];
                        bool needInitOjb = false;
                        if (lt.VariableType.IsGenericParameter)
                        {
                            var gt = method.FindGenericArgument(lt.VariableType.Name);
                            needInitOjb = gt.IsValueType && !gt.IsPrimitive;
                        }
                        else
                            needInitOjb = lt.VariableType.IsValueType && !lt.VariableType.IsPrimitive;
                        if (needInitOjb || CheckNeedInitObj(b, r, method.ReturnType != method.AppDomain.VoidType, visitedBlocks))
                        {
                            OpCodeR code = new OpCodeR();
                            code.Code = OpCodeREnum.Initobj;
                            code.Register1 = r;
                            code.Operand = method.GetTypeTokenHashCode(body.Variables[idx].VariableType);
                            code.Operand2 = 1;
                            first.FinalInstructions.Insert(appendIdx++, code);
                            break;
                        }
                    }
                }

                idx++;
            }
            for (idx = first.FinalInstructions.Count - 1; idx >= 0; idx--)
            {
                if (idx >= appendIdx)
                {
                    RegisterVMSymbol symbol;

                    if (first.InstructionMapping.TryGetValue(idx - appendIdx, out symbol))
                    {
                        first.InstructionMapping[idx] = first.InstructionMapping[idx - appendIdx];
                    }
                }
                else
                    first.InstructionMapping.Remove(idx);
            }

#if OUTPUT_JIT_RESULT
            int cnt = 1;
            Console.WriteLine($"JIT Results for {method}:");
            foreach (var b in blocks)
            {
                Console.WriteLine($"Block {cnt++}, Instructions:{b.FinalInstructions.Count}");
                for (int i = 0; i < b.FinalInstructions.Count; i++)
                {
                    Console.WriteLine($"    {i}:{b.FinalInstructions[i].ToString(appdomain)}");
                }
            }
#endif

            Optimizer.ForwardCopyPropagation(blocks, hasReturn, baseRegStart);
            Optimizer.BackwardsCopyPropagation(blocks, hasReturn, baseRegStart);
            Optimizer.ForwardCopyPropagation(blocks, hasReturn, baseRegStart);
            Optimizer.EliminateConstantLoad(blocks, hasReturn);

#if OUTPUT_JIT_RESULT
            cnt = 1;
            Console.WriteLine($"Optimizer Results for {method}:");
            foreach (var b in blocks)
            {
                Console.WriteLine($"Block {cnt++}, Instructions:{b.FinalInstructions.Count}");
                for (int i = 0; i < b.FinalInstructions.Count; i++)
                {
                    string canRemove = b.CanRemove.Contains(i) ? "(x)" : "";
                    Console.WriteLine($"    {i}:{canRemove}{b.FinalInstructions[i].ToString(appdomain)}");
                }
            }
#endif

            List<OpCodeR> res = new List<OpCodeR>();
            Dictionary<int, int> jumpTargets = new Dictionary<int, int>();
            int bIdx = 0;
            HashSet<int> inlinedBranches = new HashSet<int>();
            int curIndex = 0;
            foreach (var b in blocks)
            {
                jumpTargets[bIdx++] = res.Count;
                bool isInline = false;
                int inlineOffset = 0;
                bool inlineAddressSet = false;
                for (idx = 0; idx < b.FinalInstructions.Count; idx++)
                {
                    RegisterVMSymbol oriIns;
                    bool hasOri = b.InstructionMapping.TryGetValue(idx, out oriIns);
                    if (hasOri)
                    {
                        if (isInline)
                        {
                            if (!inlineAddressSet)
                            {
                                while (oriIns.ParentSymbol != null)
                                    oriIns = oriIns.ParentSymbol.Value;
                                addr[oriIns.Instruction] = curIndex;
                                inlineAddressSet = true;
                            }
                        }
                        else
                            addr[oriIns.Instruction] = curIndex;
                    }
                    if (b.CanRemove.Contains(idx))
                    {
                        if (isInline)
                            inlineOffset--;
                        continue;
                    }
                    var ins = b.FinalInstructions[idx];
                    if (ins.Code == OpCodeREnum.InlineStart)
                    {
                        inlineAddressSet = false;
                        isInline = true;
                        inlineOffset = res.Count;
                    }
                    else if (ins.Code == OpCodeREnum.InlineEnd)
                    {
                        isInline = false;
                    }
                    else
                    {
                        if (isInline)
                        {
                            if (Optimizer.IsBranching(ins.Code))
                            {
                                ins.Operand += inlineOffset;
                                inlinedBranches.Add(res.Count);
                            }
                            else if (Optimizer.IsIntermediateBranching(ins.Code))
                            {
                                ins.Operand4 += inlineOffset;
                                inlinedBranches.Add(res.Count);
                            }
                            else if (ins.Code == OpCodeREnum.Switch)
                            {
                                int[] targets = jumptables[ins.Operand];
                                for (int j = 0; j < targets.Length; j++)
                                {
                                    targets[j] = targets[j] + inlineOffset;
                                }
                                inlinedBranches.Add(res.Count);
                            }
                        }
                        if (hasOri)
                            symbols.Add(res.Count, oriIns);
                        curIndex++;
                        res.Add(ins);
                    }
                }
            }
            for (int i = 0; i < res.Count; i++)
            {
                var op = res[i];
                if (Optimizer.IsBranching(op.Code) && !inlinedBranches.Contains(i))
                {
                    op.Operand = jumpTargets[op.Operand];
                    res[i] = op;
                }
                else if (Optimizer.IsIntermediateBranching(op.Code) && !inlinedBranches.Contains(i))
                {
                    op.Operand4 = jumpTargets[op.Operand4];
                    res[i] = op;
                }
                else if (op.Code == OpCodeREnum.Switch && !inlinedBranches.Contains(i))
                {
                    int[] targets = jumptables[op.Operand];
                    for (int j = 0; j < targets.Length; j++)
                    {
                        targets[j] = jumpTargets[targets[j]];
                    }
                }
                else if(op.Code == OpCodeREnum.Leave || op.Code == OpCodeREnum.Leave_S)
                {
                    var oriIns = symbols[i];
                    op.Operand = addr[(Instruction)oriIns.Instruction.Operand];
                    res[i] = op;
                }
            }
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
            //FixSymbol(symbols);
#else
            symbols = null;
#endif
            frame = new CompiledFrame();
            frame.SwitchTargets = jumptables;
            frame.Symbols = symbols;
            short neoCatchExReg = -1;
#if ENABLE_NEO_MODE
            // Neo exception handling (Step 14): if this method has any catch
            // handler, protect its exception-variable register (temp register 0,
            // baseRegStart) from compaction so AllocateLocalStackSpaces
            // reserves a StackSlotInfo (and thus an mStack ref slot) for it --
            // ExecuteNeo writes the caught object there on catch entry.
            if (body.HasExceptionHandlers)
            {
                bool hasCatch = false;
                foreach (var eh in body.ExceptionHandlers)
                {
                    if (eh.HandlerType == Mono.Cecil.Cil.ExceptionHandlerType.Catch)
                    {
                        hasCatch = true;
                        break;
                    }
                }
                if (hasCatch)
                    neoCatchExReg = baseRegStart;
            }
#endif
            var totalRegCnt = Optimizer.CleanupRegister(res, locVarRegStart, hasReturn, neoCatchExReg, out short neoCatchExRegFinal);
            frame.StackRegisterCount = Math.Max(totalRegCnt - baseRegStart, 0);
#if ENABLE_NEO_MODE
            // Step 22 template capture point: AFTER CleanupRegister, BEFORE the
            // T-dependent back-half (TypeSpecialize). The register-index body
            // here is the T-invariant template artifact. When templateCapture is
            // set (by InitCodeBody on a capture-eligible generic instance), it is
            // snapshotted into the bag for GenericMethodTemplateOps.StoreFromCapture.
            if (templateCapture != null)
                CaptureTemplate(res, locVarRegStart, totalRegCnt, neoCatchExRegFinal, ref frame, addr, symbols);
            // Step 22: the Neo T-dependent back-half (TypeSpecialize + Allocate +
            // Lower) is factored into RunNeoBackHalf so GenericMethodTemplate.
            // CloneAndPatch can re-run it on a cloned template body with a concrete
            // generic argument. Compile calls it inline here on its own `res`,
            // byte-identical to the previous inlined sequence. RunNeoBackHalf sets
            // frame.CodeBody (after TypeSpecialize mutates `res`), so the Neo arm
            // does not assign CodeBody here (the Legacy #else arm does).
            // rasen neo-overhaul-eh-table-remap: materialize the Neo EH table from
            // `addr` BEFORE the back-half so it is non-null during LowerNeoOffsets
            // (the Push-deletion pass). This lets FixBranchTargetsAfterRemove re-map
            // the four body-indexed EH fields (TryStart/TryEnd/HandlerStart/
            // HandlerEnd) in lockstep with the branch targets. InitCodeBody's :959
            // build is idempotent (skips, already populated here).
            method.BuildExceptionHandlerRegister(addr);
            RunNeoBackHalf(ref frame, res, locVarRegStart, totalRegCnt, neoCatchExRegFinal);
#else
            frame.CodeBody = res.ToArray();
#endif
#if OUTPUT_JIT_RESULT
            Console.WriteLine($"Final Results for {method}:");

            for (int i = 0; i < res.Count; i++)
            {
                Console.WriteLine($"    {i}:{res[i].ToString(appdomain)}");
            }

#endif
            method.Compiling = false;

#if DEBUG && !NO_PROFILER
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == method.AppDomain.UnityMainThreadID)
#if UNITY_5_5_OR_NEWER
                UnityEngine.Profiling.Profiler.EndSample();
#else
                UnityEngine.Profiler.EndSample();
#endif
#endif
        }

#if ENABLE_NEO_MODE
        // Step 22: the Neo T-dependent back-half, factored out of Compile so
        // GenericMethodTemplate.CloneAndPatch can re-run it on a cloned template
        // body with a concrete generic argument. Compile calls this inline on its
        // own `res` (byte-identical to the previous inlined sequence).
        //
        // Operates on a register-index body (Register1/2/3 are still indices;
        // LowerNeoOffsets is the last step and overwrites them with byte offsets).
        // `res` is the body AFTER CleanupRegister -- the T-invariant front-half
        // output, which is also the template capture point. The caller must have
        // already set frame.StackRegisterCount / SwitchTargets / Symbols (the
        // T-invariant front-half artifacts) before calling this.
        internal void RunNeoBackHalf(ref CompiledFrame frame, List<OpCodeR> res, short locVarRegStart, int totalRegCnt, short neoCatchExRegFinal)
        {
            // Record the catch exception register's post-compaction index;
            // AllocateLocalStackSpaces resolves its byte/ref offsets from the
            // localInfo at this index and stamps them onto the frame.
            frame.NeoCatchExceptionRegIndex = neoCatchExRegFinal;
            frame.NeoRegisterTypes = TypeSpecializeNeoOpcodes(res, locVarRegStart, totalRegCnt);
            frame.CodeBody = res.ToArray();
            AllocateLocalStackSpaces(ref frame);
            // Keep frame.CodeBody in register-index form (used by inliner,
            // debugger, optimization passes when this method is later inlined).
            // ExecuteNeo runs against a lowered copy where Register1/2/3 hold
            // byte offsets after LowerNeoOffsets.
            frame.NeoExecuteBody = (OpCodeR[])frame.CodeBody.Clone();
            // rasen neo-overhaul-eh-table-remap: pass the (pre-back-half-built) EH
            // table so the Push-deletion pass re-maps its four body-indexed fields.
            Optimizer.LowerNeoOffsets(ref frame, appdomain, method.ExceptionHandlerRegister);
        }

        // Step 22: snapshot the T-invariant front-half artifacts into the capture
        // bag (templateCapture). Called at the capture point (after CleanupRegister,
        // before the back-half) when templateCapture is non-null.
        void CaptureTemplate(List<OpCodeR> res, short locVarRegStart, int totalRegCnt, short neoCatchExRegFinal, ref CompiledFrame frame, Dictionary<Mono.Cecil.Cil.Instruction, int> addr, Dictionary<int, RegisterVMSymbol> symbols)
        {
            var body = res.ToArray();
            // The auto-Initobj prefix: leading ops with Code==Initobj && Operand2==1
            // (the front-half's auto-inserted local init -- line ~374 sets Operand2=1;
            // IL-source Initobj leaves Operand2=0). Contiguous at the start, in local-
            // register order. For the open definition these are all CheckNeedInitObj-
            // driven (non-generic) locals -- T-invariant.
            var prefixRegs = new List<int>();
            for (int i = 0; i < body.Length; i++)
            {
                if (body[i].Code == OpCodeREnum.Initobj && body[i].Operand2 == 1)
                    prefixRegs.Add(body[i].Register1);
                else
                    break;
            }
            int varCnt = def.Body.Variables.Count;
            var varTypes = new TypeReference[varCnt];
            for (int i = 0; i < varCnt; i++)
                varTypes[i] = def.Body.Variables[i].VariableType;
            // Capture the `constrained. T` prefix TypeReferences (BLOCKER-1). See
            // TemplateCapture.ConstrainedTypeTokens for why the symbol can't be used.
            // Also capture the trailing callvirt's MethodReference per pair (MAJOR-2:
            // a T-qualified callvirt method token is concrete-T-dependent).
            var constrainedTokens = new List<TypeReference>();
            var constrainedMethods = new List<MethodReference>();
            foreach (var ins in def.Body.Instructions)
            {
                if (ins.OpCode.Code == Code.Constrained)
                {
                    var tr = ins.Operand as TypeReference;
                    constrainedTokens.Add(tr);
                    var next = ins.Next;
                    MethodReference mref = null;
                    if (next != null && (next.OpCode.Code == Code.Callvirt || next.OpCode.Code == Code.Call))
                        mref = next.Operand as MethodReference;
                    constrainedMethods.Add(mref);
                }
            }
            templateCapture.TemplateBody = body;
            templateCapture.LocVarRegStart = locVarRegStart;
            templateCapture.TotalRegCnt = totalRegCnt;
            templateCapture.NeoCatchExRegFinal = neoCatchExRegFinal;
            templateCapture.StackRegisterCount = frame.StackRegisterCount;
            templateCapture.Addr = addr;
            templateCapture.Symbols = symbols;
            templateCapture.SwitchTargets = frame.SwitchTargets;
            templateCapture.InitObjPrefixRegisters = prefixRegs.ToArray();
            templateCapture.VariableTypes = varTypes;
            templateCapture.VarCnt = varCnt;
            templateCapture.ConstrainedTypeTokens = constrainedTokens.Count == 0 ? null : constrainedTokens.ToArray();
            templateCapture.ConstrainedMethodTokens = constrainedMethods.Count == 0 ? null : constrainedMethods.ToArray();
        }
#endif

#if ENABLE_NEO_MODE
        IType[] TypeSpecializeNeoOpcodes(List<OpCodeR> body, short locVarRegStart, int totalRegCnt)
        {
            IType[] registerTypes = BuildInitialRegisterTypes(locVarRegStart, totalRegCnt);
            for (int i = 0; i < body.Count; i++)
            {
                OpCodeR op = body[i];
                // ---- Step 12: select inline vs heap field-access opcode ----
                // The JIT's Translate step unconditionally emits the heap
                // Ldfld_* / Stfld_* variant. Here, where the per-register
                // static types are known, rewrite it to the _Inline variant
                // when the operand register holds an in-frame value type (a
                // value-type local/temp/parameter that is not boxed and not a
                // reference slot). The discriminator is the operand register's
                // value-category, NOT the field's declaring type (which is
                // identical whether the operand is a heap instance or an
                // in-frame VT). The field offset operands (Operand2/Operand3)
                // are unchanged; the Neo offset-lowering pass later resolves
                // SrcOffset/DstOffset to the VT slot's byte offset and stamps
                // the absolute frame-ref index for the Ref variants.
                OpCodeR rewritten = op;
                if (TryRewriteFieldAccessForInline(ref rewritten, registerTypes))
                {
                    op = rewritten;
                    // VT-THIS-ADDR: the dest-temp type seeding applies ONLY to an
                    // inline Ldfld, where Register1 is the LOAD DESTINATION temp
                    // (it now holds the field's primitive/ref value). For an inline
                    // Stfld, Register1 is the OWNING in-frame VT (the address being
                    // written to), NOT a destination -- stamping the field's type
                    // there would clobber the owner's VT type and make every
                    // SUBSEQUENT `this.field=` / `dest.field=` on the same owner
                    // register fall back to the non-inline heap arm (the root cause
                    // of the multi-field VT-ctor blocker). Seed Ldfld dest only.
                    bool isInlineLdfld = IsInlineLdfldDestSeedable(op.Code);
                    if (isInlineLdfld)
                    {
                        if (op.Code == OpCodeREnum.Ldfld_Ref_Inline)
                            SetRegisterType(registerTypes, op.Register1, appdomain.ObjectType);
                        else
                            SetRegisterType(registerTypes, op.Register1, FieldTypeForInlineLdfld(op.Code));
                    }
                }
                // Stfld_Value / Ldfld_Value in-frame-owner discriminator (Step
                // 12b). These whole-IL-VT field opcodes have no _Inline form,
                // so the same opcode reaches ExecuteNeo for both a heap
                // ILTypeInstance owner and an in-frame IL-struct owner. Stamp
                // the discriminator into Operand (@8 = dead declaring-type hash)
                // from the owner register's dataflow type -- the SAME
                // registerTypes signal the typed _Inline rewrite above keys on.
                if (op.Code == OpCodeREnum.Stfld_Value || op.Code == OpCodeREnum.Ldfld_Value)
                {
                    short svOwnerReg = op.Code == OpCodeREnum.Ldfld_Value ? op.Register2 : op.Register1;
                    IType svOwnerType = GetRegisterType(registerTypes, svOwnerReg);
                    bool svOwnerInFrame = svOwnerType is ILType svt && svt.IsValueType && !svt.IsEnum;
                    op.Operand = svOwnerInFrame ? NeoValueFieldInFrameOwnerMarker : NeoValueFieldHeapOwnerMarker;
                    // cluster-E: a Ldfld_Value dest ALWAYS holds an in-frame copy of
                    // the whole IL value-type field (flat managed bytes), for BOTH a
                    // heap and an in-frame owner (the runtime arm copies the field's
                    // primitive+ref region into the dest either way). Seed the dest
                    // (Register1) with the field's ILType (Operand4 = field-type hash,
                    // stamped at body emission) so a following typed Ldfld_*/Stfld_*
                    // on that dest (e.g. `a.C.x` where `a.C` is a Vector3 loaded via
                    // ldfld.value) is rewritten to its _Inline variant. Without this
                    // the followup stays the plain heap arm and reads the dest's first
                    // bytes as an mStack index -> GetNeoILInstance NRE. A non-IL /
                    // non-value-type field (should not occur for Ldfld_Value, but
                    // guarded) leaves the dest unseeded.
                    if (op.Code == OpCodeREnum.Ldfld_Value)
                    {
                        var fldType = appdomain.GetType(op.Operand4);
                        if (fldType is ILType ftIl && ftIl.IsValueType && !ftIl.IsEnum)
                            SetRegisterType(registerTypes, op.Register1, fldType);
                    }
                }
                switch (op.Code)
                {
                    case OpCodeREnum.Ldc_I4_M1:
                    case OpCodeREnum.Ldc_I4_0:
                    case OpCodeREnum.Ldc_I4_1:
                    case OpCodeREnum.Ldc_I4_2:
                    case OpCodeREnum.Ldc_I4_3:
                    case OpCodeREnum.Ldc_I4_4:
                    case OpCodeREnum.Ldc_I4_5:
                    case OpCodeREnum.Ldc_I4_6:
                    case OpCodeREnum.Ldc_I4_7:
                    case OpCodeREnum.Ldc_I4_8:
                    case OpCodeREnum.Ldc_I4:
                    case OpCodeREnum.Ldc_I4_S:
                        SetRegisterType(registerTypes, op.Register1, appdomain.IntType);
                        break;
                    case OpCodeREnum.Ldc_I8:
                        SetRegisterType(registerTypes, op.Register1, appdomain.LongType);
                        break;
                    case OpCodeREnum.Ldc_R4:
                        SetRegisterType(registerTypes, op.Register1, appdomain.FloatType);
                        break;
                    case OpCodeREnum.Ldc_R8:
                        SetRegisterType(registerTypes, op.Register1, appdomain.DoubleType);
                        break;
                    // neo-c10-list-index-residual: Initobj/Ldobj are IL value-type
                    // PRODUCERS (their Operand carries the type-token hash, stamped at
                    // JIT Translate). Without seeding, a struct TEMP produced by
                    // `initobj rT, Vector3` / `ldobj rT, srcAddr` stays untyped, and a
                    // following `move rLocal, rT` clobbers the local's declared in-frame-
                    // VT type with null -> a subsequent `ldfld.r4 rLocal.field` is NOT
                    // rewritten to the _Inline variant -> the runtime Ldfld_R4 arm
                    // misreads the struct's flat bytes (e.g. float bits 0x40400000) as an
                    // mStack index -> GetNeoILInstance -> List.get_Item OOB. Seed the dest
                    // so the inline rewrite + Move propagation keep the in-frame-VT type
                    // (same producer-seeding pattern as child-16/21/23). A non-ILType
                    // result (CLR struct / primitive) simply does not trigger the inline
                    // rewrite, so the seed is harmless for those cases.
                    case OpCodeREnum.Initobj:
                    case OpCodeREnum.Ldobj:
                        {
                            IType t = appdomain.GetType(op.Operand);
                            if (t != null)
                                SetRegisterType(registerTypes, op.Register1, t);
                        }
                        break;
                    case OpCodeREnum.Ldnull:
                        SetRegisterType(registerTypes, op.Register1, appdomain.ObjectType);
                        break;
                    case OpCodeREnum.Ldstr:
                        SetRegisterType(registerTypes, op.Register1, appdomain.ObjectType);
                        break;
                    case OpCodeREnum.Move:
                        {
                            IType srcType = GetRegisterType(registerTypes, op.Register2);
                            op.Operand = IsNeoReferenceSlot(srcType) ? 1 : 0;
                            SetRegisterType(registerTypes, op.Register1, srcType);

                            // Step 12b: LowerMove. Rewrite this Move to Move_Vt
                            // when the DESTINATION slot is a value type with one
                            // or more reference fields (TotalReferenceCount > 0).
                            // Plain Move already handles primitives, single
                            // reference slots, and pure-primitive value types
                            // (refCount == 0, where the byte CopyBlock is correct)
                            // so we leave those alone to minimize the regression
                            // surface. This runs INSIDE TypeSpecializeNeoOpcodes,
                            // BEFORE LowerNeoOffsets overwrites Register1/2 with
                            // byte offsets (the OpCodeR explicit-layout union
                            // aliases Register1/2/3 with DstOffset/SrcOffset/
                            // OperandOffset), so the dest register index is still
                            // valid for the type lookup. The authoritative slot
                            // Size/RefOffset/RefCount are stamped later from
                            // localInfos at LowerNeoOffsets time (see
                            // Optimizer.Neo.cs Move_Vt case).
                            IType dstType = GetRegisterType(registerTypes, op.Register1);
                            if (dstType is ILType dstIl && dstIl.IsValueType && !dstIl.IsEnum)
                            {
                                if (dstIl.TotalReferenceCount > 0)
                                {
                                    op.Code = OpCodeREnum.Move_Vt;
                                }
                            }
                        }
                        break;
                    case OpCodeREnum.Neg:
                    case OpCodeREnum.Not:
                        op.Code = GetTypedUnaryOpcode(op.Code, InferPrimTag(GetRegisterType(registerTypes, op.Register2), appdomain));
                        SetRegisterType(registerTypes, op.Register1, GetRegisterType(registerTypes, op.Register2));
                        break;
                    case OpCodeREnum.Add:
                    case OpCodeREnum.Sub:
                    case OpCodeREnum.Mul:
                    case OpCodeREnum.Div:
                    case OpCodeREnum.Div_Un:
                    case OpCodeREnum.Rem:
                    case OpCodeREnum.Rem_Un:
                    case OpCodeREnum.And:
                    case OpCodeREnum.Or:
                    case OpCodeREnum.Xor:
                    case OpCodeREnum.Shl:
                    case OpCodeREnum.Shr:
                    case OpCodeREnum.Shr_Un:
                        op.Code = GetTypedBinaryOpcode(op.Code, InferPrimTag(GetRegisterType(registerTypes, op.Register2), appdomain));
                        SetRegisterType(registerTypes, op.Register1, GetRegisterType(registerTypes, op.Register2));
                        break;
                    // neo-stfld-ref-generics: an ALREADY-specialized Ceq_Ref
                    // re-enters TypeSpecializeNeoOpcodes when its owning method
                    // is INLINED into a caller -- the inliner splices the
                    // callee's post-TypeSpecialize BodyRegister (which has
                    // Ceq_Ref baked in, NOT a plain Ceq), so the plain-Ceq case
                    // below (which seeds the dest IntType) never runs for it.
                    // The dest then keeps a STALE reference type from a preceding
                    // Ldsfeld/Box in the inlined body, and the Brtrue/Brfalse ->
                    // _Ref rewrite below mis-classifies the ceq's int32 0/1
                    // result as a reference mStack index: Brfalse_Ref reads the
                    // `1` (true) as "mStack[1]", and when that slot is null the
                    // initializer branch is WRONGLY taken (true -> falsey),
                    // skipping the lazy `if (x == null) { x = new(); }` body ->
                    // x stays null -> downstream NRE (e.g. the self-referential
                    // generic Singleton<T : Singleton<T>>.get_Inst inlined into
                    // the caller: `Inst.Test = "bar"` NREs because Inst returns
                    // null). Seed dest = IntType here so the ceq result is
                    // correctly typed and the following Brfalse stays plain
                    // (mirrors line 1094 for the freshly-specialized Ceq). The
                    // opcode itself is NOT re-touched (Ceq_Ref is terminal).
                    case OpCodeREnum.Ceq_Ref:
                        SetRegisterType(registerTypes, op.Register1, appdomain.IntType);
                        break;
                    case OpCodeREnum.Ceq:
                    case OpCodeREnum.Cgt:
                    case OpCodeREnum.Cgt_Un:
                    case OpCodeREnum.Clt:
                    case OpCodeREnum.Clt_Un:
                        op.Code = GetTypedCompareOpcode(op.Code, InferPrimTag(GetRegisterType(registerTypes, op.Register2), appdomain));
                        SetRegisterType(registerTypes, op.Register1, appdomain.IntType);
                        // neo-ceq-null-sentinel: a reference-typed Ceq compares the
                        // referenced objects' identity/nullness, not the raw mStack-
                        // index int32s (null = a non-zero index to a null entry, or
                        // the -1 sentinel). When EITHER operand register (Register2/
                        // Register3, the two compare sources) is a reference slot,
                        // upgrade the still-plain Ceq to Ceq_Ref. Runs AFTER the typed
                        // rewrite so I8/R4/R8 variants win for primitive operands --
                        // only a plain Ceq whose InferPrimTag fell back to I4 because
                        // it is a reference is upgraded. Dest stays IntType (a real
                        // 0/1 int32), so a following brtrue/brfalse on the result
                        // stays a plain branch (no Brtrue_Ref interaction). Only Ceq:
                        // Cgt/Clt reference ordering is invalid CIL, and Cgt_Un
                        // already has its Step-15 null-sentinel runtime arm. Sibling
                        // of the Brtrue/Brfalse -> _Ref rewrite below (closes the ceq
                        // form of the null-comparison gap). Legacy parity: Ceq
                        // (Register.cs:4557-4603).
                        if (op.Code == OpCodeREnum.Ceq
                            && (IsNeoReferenceSlot(GetRegisterType(registerTypes, op.Register2))
                                || IsNeoReferenceSlot(GetRegisterType(registerTypes, op.Register3))))
                        {
                            op.Code = OpCodeREnum.Ceq_Ref;
                        }
                        break;
                    case OpCodeREnum.Beq:
                    case OpCodeREnum.Beq_S:
                    case OpCodeREnum.Bne_Un:
                    case OpCodeREnum.Bne_Un_S:
                    case OpCodeREnum.Blt:
                    case OpCodeREnum.Blt_S:
                    case OpCodeREnum.Blt_Un:
                    case OpCodeREnum.Blt_Un_S:
                    case OpCodeREnum.Bgt:
                    case OpCodeREnum.Bgt_S:
                    case OpCodeREnum.Bgt_Un:
                    case OpCodeREnum.Bgt_Un_S:
                    case OpCodeREnum.Ble:
                    case OpCodeREnum.Ble_S:
                    case OpCodeREnum.Ble_Un:
                    case OpCodeREnum.Ble_Un_S:
                    case OpCodeREnum.Bge:
                    case OpCodeREnum.Bge_S:
                    case OpCodeREnum.Bge_Un:
                    case OpCodeREnum.Bge_Un_S:
                        op.Code = GetTypedBranchOpcode(NormalizeBranchOpcode(op.Code), InferPrimTag(GetRegisterType(registerTypes, op.Register1), appdomain));
                        // neo-ceq-null-sentinel: a reference-typed Beq/Bne_Un must
                        // compare the referenced objects' identity, not the raw
                        // mStack-index int32s (null = a non-zero index or -1). When
                        // EITHER operand register (Register1/Register2, the two
                        // compare sources) is a reference slot, upgrade the still-
                        // plain Beq/Bne_Un to its _Ref variant. Runs AFTER the typed
                        // rewrite so the I8/R4/R8 branch variants win for primitives
                        // -- only a plain Beq/Bne_Un whose InferPrimTag fell back to
                        // I4 because it is a reference is upgraded. Only Beq/Bne_Un:
                        // Blt/Bgt/... reference ordering is invalid CIL. Sibling of
                        // the Ceq -> Ceq_Ref rewrite above and the Brtrue/Brfalse ->
                        // _Ref rewrite below. Legacy parity: Beq (Register.cs:2090-
                        // 2136), Bne_Un (Register.cs:2172-2220).
                        if ((op.Code == OpCodeREnum.Beq || op.Code == OpCodeREnum.Bne_Un)
                            && (IsNeoReferenceSlot(GetRegisterType(registerTypes, op.Register1))
                                || IsNeoReferenceSlot(GetRegisterType(registerTypes, op.Register2))))
                        {
                            op.Code = op.Code == OpCodeREnum.Beq ? OpCodeREnum.Beq_Ref : OpCodeREnum.Bne_Un_Ref;
                        }
                        break;
                    case OpCodeREnum.Addi:
                    case OpCodeREnum.Subi:
                    case OpCodeREnum.Muli:
                    case OpCodeREnum.Divi:
                    case OpCodeREnum.Divi_Un:
                    case OpCodeREnum.Remi:
                    case OpCodeREnum.Remi_Un:
                    case OpCodeREnum.Andi:
                    case OpCodeREnum.Ori:
                    case OpCodeREnum.Xori:
                    case OpCodeREnum.Shli:
                    case OpCodeREnum.Shri:
                    case OpCodeREnum.Shri_Un:
                        op.Code = GetTypedImmediateBinaryOpcode(op.Code, InferPrimTag(GetRegisterType(registerTypes, op.Register2), appdomain));
                        SetRegisterType(registerTypes, op.Register1, GetRegisterType(registerTypes, op.Register2));
                        break;
                    case OpCodeREnum.Ceqi:
                    case OpCodeREnum.Cgti:
                    case OpCodeREnum.Cgti_Un:
                    case OpCodeREnum.Clti:
                    case OpCodeREnum.Clti_Un:
                        op.Code = GetTypedImmediateCompareOpcode(op.Code, InferPrimTag(GetRegisterType(registerTypes, op.Register2), appdomain));
                        SetRegisterType(registerTypes, op.Register1, appdomain.IntType);
                        break;
                    case OpCodeREnum.Beqi:
                    case OpCodeREnum.Bnei_Un:
                    case OpCodeREnum.Blti:
                    case OpCodeREnum.Blti_Un:
                    case OpCodeREnum.Bgti:
                    case OpCodeREnum.Bgti_Un:
                    case OpCodeREnum.Blei:
                    case OpCodeREnum.Blei_Un:
                    case OpCodeREnum.Bgei:
                    case OpCodeREnum.Bgei_Un:
                        op.Code = GetTypedImmediateBranchOpcode(op.Code, InferPrimTag(GetRegisterType(registerTypes, op.Register1), appdomain));
                        break;
                    case OpCodeREnum.Conv_I:
                    case OpCodeREnum.Conv_I1:
                    case OpCodeREnum.Conv_I2:
                    case OpCodeREnum.Conv_I4:
                    case OpCodeREnum.Conv_I8:
                    case OpCodeREnum.Conv_R4:
                    case OpCodeREnum.Conv_R8:
                    case OpCodeREnum.Conv_R_Un:
                    case OpCodeREnum.Conv_U:
                    case OpCodeREnum.Conv_U1:
                    case OpCodeREnum.Conv_U2:
                    case OpCodeREnum.Conv_U4:
                    case OpCodeREnum.Conv_U8:
                        op.Operand2 = (int)InferPrimTag(GetRegisterType(registerTypes, op.Register2), appdomain);
                        SetRegisterType(registerTypes, op.Register1, GetConvResultType(op.Code));
                        break;
                    case OpCodeREnum.Ldfld_I1:
                    case OpCodeREnum.Ldfld_I2:
                    case OpCodeREnum.Ldfld_I4:
                    case OpCodeREnum.Ldfld_U1:
                    case OpCodeREnum.Ldfld_U2:
                    case OpCodeREnum.Ldfld_U4:
                    case OpCodeREnum.Ldlen:
                        SetRegisterType(registerTypes, op.Register1, appdomain.IntType);
                        break;
                    case OpCodeREnum.Ldfld_I8:
                    case OpCodeREnum.Ldfld_U8:
                        SetRegisterType(registerTypes, op.Register1, appdomain.LongType);
                        break;
                    case OpCodeREnum.Ldfld_R4:
                        SetRegisterType(registerTypes, op.Register1, appdomain.FloatType);
                        break;
                    case OpCodeREnum.Ldfld_R8:
                        SetRegisterType(registerTypes, op.Register1, appdomain.DoubleType);
                        break;
                    // neo-raw-ldfld-stfld-clr-struct-seeding (shape 2): a raw
                    // Ldfld reaches here ONLY when the declaring type is a
                    // CLRType (child 4: the Neo typed-splitter emits
                    // Ldfld_R4/R8/I8 only for an ILType declaring type). The
                    // field identity is encoded in OperandLong =
                    // (typeHash<<32)|fieldHash -- identical to Legacy's raw
                    // encoding and to the ExecuteNeo raw-Ldfld runtime handler.
                    // Resolve the declaring CLRType + field and seed
                    // registerTypes[dest] with the field's primitive type so a
                    // subsequent typed arith (muli/addi/add keyed on
                    // registerTypes[Register2]) fires. Non-primitive fields
                    // (VT/ref) are left unseeded (they do not feed primitive
                    // arithmetic; seeding a VT would perturb the field-access
                    // discriminator). Same hash-lookup the runtime performs.
                    case OpCodeREnum.Ldfld:
                        {
                            int typeHash = (int)((ulong)op.OperandLong >> 32);
                            int fieldHash = (int)op.OperandLong;
                            var declType = appdomain.GetType(typeHash);
                            if (declType is CLRType ct)
                            {
                                var f = ct.GetField(fieldHash);
                                if (f != null)
                                {
                                    IType ft = NeoClrPrimitiveTypeToIType(f.FieldType, appdomain);
                                    if (ft != null)
                                        SetRegisterType(registerTypes, op.Register1, ft);
                                }
                            }
                        }
                        break;
                    // neo-ceq-null-instance-field: seed a heap instance
                    // REFERENCE-field load's dest as a reference so a direct
                    // Brtrue/Brfalse on the instance field (the Roslyn lowering
                    // of `(field == null) ? a : b` and bare-reference branch
                    // conditions, which emit `ldfld.ref; brfalse/brtrue` with NO
                    // ceq) -- and a two-reference Ceq/Beq/Bne_Un on two instance
                    // fields -- specializes to the _Ref variant. Without this the
                    // dest stays unseeded, IsNeoReferenceSlot is false at the
                    // Brtrue/Brfalse case below, the branch stays the plain int
                    // Brfalse_S/Brtrue_S, and a null reference field (a VALID
                    // mStack index N != 0) reads TRUTHY -> the null guard /
                    // ternary takes the wrong branch. Mirror child-11 Ldsfeld
                    // seeding; ObjectType is the canonical reference (same as
                    // Ldnull/Ldstr/Ldfld_Ref_Inline). EXCLUDE the F-10
                    // boxed-CLR-struct-field case: when IsClrStructFieldOfIL is
                    // true the body stamps Operand4 = fieldType.GetHashCode()
                    // (non-zero), and the runtime Ldfld_Ref arm then flattens the
                    // boxed struct into the dest flat-bytes region -- the dest is
                    // NOT a reference encoding, so Brtrue_Ref must NOT fire;
                    // genuine reference fields leave Operand4 == 0. Ldfld_Ref is
                    // emitted ONLY for non-primitive, non-IL-VT fields
                    // (GetLdfldCodeForType), so a reference seed can never collide
                    // with an int-branch path. (Ldfld_Ref_Inline -- the in-frame-
                    // VT-owner variant -- is already seeded at the pre-rewrite
                    // step above.)
                    case OpCodeREnum.Ldfld_Ref:
                        if (op.Operand4 == 0)
                            SetRegisterType(registerTypes, op.Register1, appdomain.ObjectType);
                        break;
                    // neo-addi-on-float: seed the indirect- and element-load
                    // producers so a float/double/long operand loaded via a
                    // byref/CLR-struct-field indirection (ldflda; ldind.r4) or an
                    // array element (ldelem.r4) is correctly typed. Without this,
                    // only Ldc_*/Ldfld_* seeded these types, so such an operand
                    // fell back to the default I4, the typed immediate/binary
                    // specialization (Addi->Addi_R4 etc., keyed on
                    // registerTypes[Register2]) silently no-oped, and a plain
                    // integer Addi integer-added the raw IEEE bits (e.g.
                    // a.X += 100 lowered to addi r,r,0x42C80000). Seeding a
                    // primitive is safe for the other type-spec decisions, which
                    // all key on IsNeoReferenceSlot/IsValueType (a primitive is
                    // neither). I4 is seeded for symmetry (it matches the prior
                    // default fallback, so today it is a no-op).
                    case OpCodeREnum.Ldind_R4:
                    case OpCodeREnum.Ldelem_R4:
                        SetRegisterType(registerTypes, op.Register1, appdomain.FloatType);
                        break;
                    case OpCodeREnum.Ldind_R8:
                    case OpCodeREnum.Ldelem_R8:
                        SetRegisterType(registerTypes, op.Register1, appdomain.DoubleType);
                        break;
                    case OpCodeREnum.Ldind_I8:
                    case OpCodeREnum.Ldelem_I8:
                        SetRegisterType(registerTypes, op.Register1, appdomain.LongType);
                        break;
                    case OpCodeREnum.Ldind_I4:
                    case OpCodeREnum.Ldelem_I4:
                        SetRegisterType(registerTypes, op.Register1, appdomain.IntType);
                        break;
                    // Step 12: ldloca/ldloca.s of a value-type local produces a
                    // managed pointer, but for the Neo in-frame-VT model the
                    // pointer aliases the local's byte range. Propagate the
                    // source local's value-type to the dest temp so the
                    // field-access discriminator (TryRewriteFieldAccessForInline)
                    // recognizes the operand as an in-frame value type and emits
                    // the _Inline variant. The offset-lowering pass resolves the
                    // ldloca dest back to the source local's offset.
                    case OpCodeREnum.Ldloca:
                    case OpCodeREnum.Ldloca_S:
                        {
                            IType srcType = GetRegisterType(registerTypes, op.Register2);
                            if (srcType is ILType srcIl && srcIl.IsValueType && !srcIl.IsEnum)
                                SetRegisterType(registerTypes, op.Register1, srcType);
                        }
                        break;
                    // cluster-E: ldarga of a struct PARAMETER (the byref dest
                    // aliases the param's in-frame flat bytes). Same rule as
                    // Ldloca above -- propagate the source param's value-type so
                    // a following typed Ldfld_*/Stfld_* on this byref is rewritten
                    // to its _Inline variant (without this, e.g. `void F(Struc a){
                    // a.a = 3; }` lowers to `ldarga;stfld.i4` and the plain heap
                    // arm reads the struct's first field as an mStack index ->
                    // GetNeoILInstance NRE). A primitive/ref param is not an ILType
                    // -> no seed -> no change (a `ref int` byref stays untyped).
                    case OpCodeREnum.Ldarga:
                    case OpCodeREnum.Ldarga_S:
                        {
                            IType srcType = GetRegisterType(registerTypes, op.Register2);
                            if (srcType is ILType srcIl && srcIl.IsValueType && !srcIl.IsEnum)
                                SetRegisterType(registerTypes, op.Register1, srcType);
                        }
                        break;
                    // Step 12 (Minor 3): ldflda of a nested in-frame value-type
                    // field. The dest temp aliases the owning VT's byte range
                    // (plus the field's nested offset, folded later by the
                    // offset-lowering pass). For a chain `ldloca V -> ldflda f
                    // -> stfld x`, the stfld operand is this ldflda dest; the
                    // field-access discriminator (TryRewriteFieldAccessForInline)
                    // keys on that operand's value-category, so the dest MUST
                    // carry an in-frame VT type. Propagate the source register's
                    // VT type (the same rule the Ldloca case uses) when the
                    // source is an in-frame VT. This makes opcode selection
                    // self-contained instead of depending on the JIT reusing
                    // the same temp for the ldloca dest and the ldflda dest.
                    case OpCodeREnum.Ldflda:
                        {
                            IType srcType = GetRegisterType(registerTypes, op.Register2);
                            if (srcType is ILType srcIl && srcIl.IsValueType && !srcIl.IsEnum)
                            {
                                SetRegisterType(registerTypes, op.Register1, srcType);
                                // F-6 / NEO-VT-FLDADDR: stamp a marker so the
                                // runtime Ldflda arm can distinguish an in-frame-VT
                                // operand (its slot may hold FLAT BYTES from a
                                // constrained-boxed `this`, NOT a Ref Slot) from a
                                // heap-IL / CLR-object operand. Stamped pre-lowering
                                // (Register2 still a register index); Operand4 is
                                // standalone (offset 20) and otherwise unused for
                                // Ldflda, so bit 0x1 is collision-free (mirrors the
                                // Constrained-callvirt 0x1 flag convention).
                                op.Operand4 |= NeoLdfldaInlineMarker;
                                // F-10-R1: the F-6 (in-frame-VT) and F-10 (CLR-
                                // struct-field-of-IL) markers are NOT mutually-
                                // exclusive at the main-JIT body emission -- the
                                // body's `case Code.Ldflda` (below) stamps F-10
                                // whenever IsClrStructFieldOfIL is true, and that
                                // predicate returns true for an IL VALUE-type
                                // declaring type too (it checks `declaringType is
                                // ILType`, not `!IsValueType`). So an in-frame-VT
                                // source whose field is a CLR struct gets BOTH
                                // stamps (Operand4 = 0x3). The runtime Ldflda arm
                                // then checks F-10 FIRST (clrStructFieldMarker &&
                                // objIdx >= 0); for the constrained.callvirt
                                // direct-call shape, slot-0 holds the struct's FLAT
                                // PRIMITIVE bytes (e.g. an int `prefix`), which the
                                // body's `ldflda this.field` reads as the byref
                                // objectIndex -> the F-10 branch fires on a garbage
                                // index -> NRE at NeoMarshalByrefFieldToSlot.
                                //
                                // The gate: an in-frame-VT source MUST route through
                                // the F-6 runtime branch (shape 1/2/3), NEVER the
                                // F-10 heap-ManagedObjects branch. This type-spec
                                // pass runs AFTER body emission, so the body's F-10
                                // stamp is already on Operand4 -- clear it here. The
                                // gate keys on the OPERAND's value-category (in-frame
                                // VT vs heap/boxed), exactly the F-6 condition, so it
                                // is correct for ALL three operand shapes: in-frame
                                // VT (F-10 cleared -> F-6 shape 1/2/3), heap IL class
                                // (F-6 not stamped -> F-10 stays), and boxed IL VT
                                // (operand is a heap mStack object, not an in-frame VT
                                // -> F-6 not stamped -> F-10 stays). A naive
                                // `!declaringType.IsValueType` gate was REJECTED: it
                                // would suppress F-10 for the boxed-IL-VT-with-CLR-
                                // struct-field case (declaring type is a value type,
                                // but the operand is a heap boxed object that
                                // correctly needs F-10).
                                op.Operand4 &= ~NeoLdfldaClrStructFieldMarker;
                                // neo-byref-ldind-ref-heap: same gate for the
                                // heap-IL-ref-field marker. An in-frame-VT source
                                // (e.g. an IL struct `struct S { public string s; }`
                                // with `ldflda this.s`) must route through the F-6
                                // frame-native branch (the ref field sits in the
                                // frame ref region at a frame-relative offset), NOT
                                // the heap-ManagedObjects branch. The body stamps
                                // bit 0x4 for any IL-declared reference field; clear
                                // it here for the in-frame-VT operand (same condition
                                // as the F-10 clear above; boxed-IL-VT-with-ref-field
                                // keeps the marker -- the operand is a heap object).
                                op.Operand4 &= ~NeoLdfldaHeapIlRefFieldMarker;
                            }
                        }
                        break;
                    // neo-array-multidim-ilvt (sub-gap 3): a Call/Callvirt/etc.
                    // result is NEVER an in-frame value type -- it is a reference
                    // (heap object / mStack index), a primitive, or a managed
                    // pointer (byref). When the call's dest register was REUSED
                    // from an earlier in-frame-VT operand (e.g. an `ldloca` of a
                    // VT local seeded it with a VT type), that STALE VT type would
                    // otherwise make a subsequent (possibly inlined) `stfld`/
                    // `ldflda` on the call result mis-lower to the _Inline variant.
                    // For the multi-dim `ref a[i,j]` case the `Address` call returns
                    // a byref into a register previously seeded as an in-frame VT by
                    // the source struct's `ldloca`; the inlined `ref T` callee's
                    // `stfld` then wrongly wrote into the frame instead of through
                    // the byref to the array cell's box. A call dest that carries a
                    // stale VT type must be cleared so the field-access discriminator
                    // (TryRewriteFieldAccessForInline) does not rewrite it to inline.
                    // (Newobj of an IL VT is the exception -- handled in its own
                    // case below -- its dest genuinely IS an in-frame VT.)
                    case OpCodeREnum.Call:
                    case OpCodeREnum.Callvirt:
                    case OpCodeREnum.Callvirt_IL:
                    case OpCodeREnum.Callvirt_CLR:
                    case OpCodeREnum.Call_Redirect:
                        {
                            // Only clear a STALE in-frame-VT type when the call's
                            // return is NOT itself a by-value IL value type. A
                            // byref return (the multi-dim IL-VT array `Address`
                            // case), a reference, or a primitive return is never
                            // an in-frame VT, so a reused dest register must drop
                            // its stale VT type (else a subsequent stfld/ldfld on
                            // the byref/reference result mis-lowers to _Inline and
                            // writes into the frame instead of through the byref).
                            // A by-value IL-VT return IS materialized into the dest
                            // frame region (the runtime CopyILToFrame path), so its
                            // in-frame-VT type is correct and is KEPT. The resolved
                            // method's ReturnType is the shared CLR `ILTypeInstance`
                            // for IL-VT-element array calls (the element IL-VT is
                            // lost post-resolution); recover it via the token-keyed
                            // element map so the `Get` case keeps its VT type.
                            if (op.Register1 >= 0)
                            {
                                IType cur = GetRegisterType(registerTypes, op.Register1);
                                if (cur is ILType cil && cil.IsValueType && !cil.IsEnum)
                                {
                                    var cm = appdomain.GetMethod(op.Operand2);
                                    IType rt = cm != null ? cm.ReturnType : null;
                                    if (rt is ILType rtil)
                                        rt = rtil.IsByRef ? null : rtil;
                                    else if (rt == null || (!rt.IsValueType && !rt.IsPrimitive))
                                        rt = null;
                                    if (rt == null)
                                    {
                                        ILType elemIl = JITCompiler.GetNeoIlVtArrayElementType(op.Operand2);
                                        if (elemIl != null && cm != null && cm.Name == "Get")
                                            rt = elemIl;
                                    }
                                    bool retIsInFrameVt = rt is ILType rtil2
                                        && rtil2.IsValueType && !rtil2.IsEnum && !rtil2.IsByRef;
                                    if (!retIsInFrameVt)
                                        SetRegisterType(registerTypes, op.Register1, null);
                                }
                                else if (IsNeoReferenceSlot(cur))
                                {
                                    // neo-brtrue-on-reference: a reused dest that held a
                                    // REFERENCE, now overwritten by a call result. If the
                                    // call does NOT also return a reference (a bool/int
                                    // compare like op_Inequality, a byref, or void), the
                                    // stale reference type MUST be dropped -- otherwise a
                                    // following brtrue mis-specializes to brtrue.ref and
                                    // dereferences a non-index bool/int. (The `||`-chain
                                    // regression: a register reused from a string-ref
                                    // ldfeld.ref.inline for the bool compare result.) A
                                    // reference-RETURNING call leaves the reference type in
                                    // place (correct: brtrue on it SHOULD test nullness).
                                    var cm2 = appdomain.GetMethod(op.Operand2);
                                    IType rt2 = cm2 != null ? cm2.ReturnType : null;
                                    if (rt2 is ILType rtil3) rt2 = rtil3.IsByRef ? null : rtil3;
                                    bool retIsRef = rt2 != null && !rt2.IsValueType && !rt2.IsPrimitive;
                                    if (!retIsRef)
                                        SetRegisterType(registerTypes, op.Register1, rt2 != null && rt2.IsPrimitive ? rt2 : null);
                                }
                                // neo-raw-ldfld-stfld-clr-struct-seeding (shape
                                // 1): seed a primitive return type so typed arith
                                // on the call result fires (Addi->Addi_R4 etc.
                                // key on registerTypes[Register2]). The stale-VT/
                                // stale-ref branches above only CLEAR a reused
                                // dest; they never SEED a fresh-temp dest, so
                                // `x = GetF() + 100f` left the dest null -> I4
                                // -> plain integer op on float bits -> garbage.
                                // Safe: a primitive return is excluded from the
                                // keep-VT / keep-ref logic (a primitive is
                                // neither a VT nor a reference), so re-seeding
                                // after a clear is a harmless no-op; for a fresh
                                // temp it is the missing seed. ByRef returns are
                                // nulled so they do not overwrite.
                                var cmr = appdomain.GetMethod(op.Operand2);
                                IType rtr = cmr != null ? cmr.ReturnType : null;
                                if (rtr is ILType rtrIl) rtr = rtrIl.IsByRef ? null : rtr;
                                if (rtr != null && rtr.IsPrimitive)
                                    SetRegisterType(registerTypes, op.Register1, rtr);
                            }
                        }
                        break;
                    // VT-THIS-ADDR (D1): type the dest of a Newobj of an IL value
                    // type as the constructed VT. The dest register holds the in-
                    // frame VT (the runtime Newobj IL-VT branch constructs it in
                    // the dest's frame byte/ref region), so the caller's
                    // subsequent ldfld/stfld on the result MUST be recognized as
                    // in-frame and rewritten to _Inline. Without this, the dest
                    // stays untyped and the caller's field reads fall back to the
                    // heap Ldfld_* arm (treating the dest as an mStack index ->
                    // NullReferenceException / corruption). This mirrors the
                    // Ldloca / Ldflda dest-typing rules above (the Newobj dest is
                    // the third in-frame-VT address case). Reference-type and
                    // delegate newobj are left untyped (the existing IL-ref /
                    // CLR-newobj paths own those).
                    case OpCodeREnum.Newobj:
                        {
                            var ctor = appdomain.GetMethod(op.Operand2);
                            if (ctor != null && ctor.DeclearingType is ILType nt
                                && nt.IsValueType && !nt.IsEnum && !nt.IsPrimitive)
                            {
                                SetRegisterType(registerTypes, op.Register1, nt);
                            }
                        }
                        break;
                    // neo-ilvt-boxing-roundtrip: type the dest of an Unbox /
                    // Unbox_Any of an IL value type as the unboxed VT. The dest
                    // register holds the in-frame VT (the runtime CopyILToFrame
                    // arm writes the instance's bytes + refs into the dest's frame
                    // region), so the caller's subsequent ldfld/stfld on the result
                    // MUST be recognized as in-frame and rewritten to _Inline.
                    // Without this, a `Move` that reads the unbox dest (e.g. the
                    // inlined `T BoxUnbox<T>(T v){ object o = v; return (T)o; }`
                    // body, where T is an IL struct) OVERWRITES the dest local's
                    // initial VT type with null -- and the following ldfld then
                    // falls back to the heap Ldfld_* arm, reading the dest's first 4
                    // frame bytes (the int field) as an mStack index -> OOB /
                    // NullReferenceException / corruption. This mirrors the Newobj
                    // IL-VT dest-typing rule above (Unbox is the fourth in-frame-VT
                    // address case). Box / Unbox of a reference or primitive type
                    // is left untyped (the result is an mStack index / a primitive).
                    case OpCodeREnum.Unbox:
                    case OpCodeREnum.Unbox_Any:
                        {
                            var ut = appdomain.GetType(op.Operand);
                            if (ut is ILType utIl
                                && utIl.IsValueType && !utIl.IsEnum && !utIl.IsPrimitive)
                            {
                                SetRegisterType(registerTypes, op.Register1, utIl);
                            }
                        }
                        break;
                    // neo-brtrue-on-reference (D2): seed the Ldsfeld dest register
                    // type from the static field's type. Ldsfeld is the dominant
                    // feeder for the canonical C# lazy-init / delegate-cache pattern
                    // (`if(x==null){init}` -> Roslyn emits `ldsfld x; brtrue skipInit`
                    // with NO ceq). Without seeding, the per-register type map has no
                    // entry for the ldsfeld dest, so the Brtrue/Brfalse -> _Ref rewrite
                    // below would never fire for this chain and null would stay truthy.
                    // IL: StaticFieldTypes[sIdx] is the field's IType directly; CLR:
                    // resolve FieldInfo.FieldType (System.Type) via the domain (same
                    // IL/CLR split + OperandLong=(typeHash<<32)|fieldIdx encoding the
                    // runtime Ldsfeld arm uses). Stsfld consumes its operand (no dest),
                    // so it needs no seeding.
                    case OpCodeREnum.Ldsfld:
                        {
                            var declType = appdomain.GetType((int)(op.OperandLong >> 32));
                            int sIdx = (int)op.OperandLong;
                            IType ft = null;
                            if (declType is ILType ilt && sIdx >= 0 && sIdx < ilt.StaticFieldTypes.Length)
                                ft = ilt.StaticFieldTypes[sIdx];
                            else if (declType is CLRType ct)
                            {
                                var f = ct.GetField(sIdx);
                                if (f != null)
                                    ft = appdomain.GetType(f.FieldType);
                            }
                            SetRegisterType(registerTypes, op.Register1, ft);
                        }
                        break;
                    // neo-brtrue-on-reference (D1/D3): type-specialize the branch
                    // condition. Under the Neo flat frame a reference is an mStack
                    // index in the slot's primitive bytes, and null is a NON-ZERO
                    // index (IL-static Ldsfeld does mStack.Add(null)+index) or the -1
                    // sentinel (CLR-static Ldsfeld / Ldnull). The plain Brtrue/Brfalse
                    // int32 (!=0 / ==0) test therefore misreads null as TRUTHY, which
                    // silently skips the `if(x==null){init}` initializer (x stays null
                    // -> downstream NRE). When the condition register's tracked type is
                    // a reference slot, rewrite to Brtrue_Ref/Brfalse_Ref, whose runtime
                    // arm tests mStack[idx] != null (Legacy mStack[v]!=null parity). A
                    // ceq-normalized condition stays plain: ceq seeds its dest IntType
                    // (a real 0/1 int32), so IsNeoReferenceSlot is false here.
                    case OpCodeREnum.Brtrue:
                    case OpCodeREnum.Brtrue_S:
                    case OpCodeREnum.Brfalse:
                    case OpCodeREnum.Brfalse_S:
                        {
                            if (IsNeoReferenceSlot(GetRegisterType(registerTypes, op.Register1)))
                            {
                                bool isBrtrue = op.Code == OpCodeREnum.Brtrue || op.Code == OpCodeREnum.Brtrue_S;
                                op.Code = isBrtrue ? OpCodeREnum.Brtrue_Ref : OpCodeREnum.Brfalse_Ref;
                            }
                        }
                        break;
                }
                body[i] = op;
            }
            return registerTypes;
        }

        // Step 12: returns true and rewrites `op` from its heap Ldfld_*/Stfld_*
        // form to the matching _Inline variant when the field-access operand
        // register holds an in-frame value type. Returns false (no change) when
        // the operand is a reference slot (heap ILTypeInstance / CLR object /
        // boxed VT) or when the opcode is not a field-access primitive/Ref
        // variant (e.g. Ldfld_Value / Stfld_Value = whole-VT-field copy = 12b).
        // For Ldfld the operand is Register2; for Stfld the operand is Register1.
        bool TryRewriteFieldAccessForInline(ref OpCodeR op, IType[] registerTypes)
        {
            OpCodeREnum code = op.Code;
            bool isLdfld = false;
            bool isStfld = false;
            // Primitive + Ref variants only (Value variants are 12b whole-copy).
            switch (code)
            {
                case OpCodeREnum.Ldfld_I1:
                case OpCodeREnum.Ldfld_I2:
                case OpCodeREnum.Ldfld_I4:
                case OpCodeREnum.Ldfld_I8:
                case OpCodeREnum.Ldfld_U1:
                case OpCodeREnum.Ldfld_U2:
                case OpCodeREnum.Ldfld_U4:
                case OpCodeREnum.Ldfld_U8:
                case OpCodeREnum.Ldfld_R4:
                case OpCodeREnum.Ldfld_R8:
                case OpCodeREnum.Ldfld_Ref:
                    isLdfld = true;
                    break;
                case OpCodeREnum.Stfld_I1:
                case OpCodeREnum.Stfld_I2:
                case OpCodeREnum.Stfld_I4:
                case OpCodeREnum.Stfld_I8:
                case OpCodeREnum.Stfld_U1:
                case OpCodeREnum.Stfld_U2:
                case OpCodeREnum.Stfld_U4:
                case OpCodeREnum.Stfld_U8:
                case OpCodeREnum.Stfld_R4:
                case OpCodeREnum.Stfld_R8:
                case OpCodeREnum.Stfld_Ref:
                    isStfld = true;
                    break;
                default:
                    return false;
            }

            short operandReg = isLdfld ? op.Register2 : op.Register1;
            IType operandType = GetRegisterType(registerTypes, operandReg);
            // In-frame value type = an IL value type that is not boxed (i.e. the
            // register holds the VT's flat bytes, not an mStack index). Enums are
            // addressed as their underlying primitive, not as VTs, so exclude them.
            bool operandIsInFrameVt =
                operandType is ILType ot && ot.IsValueType && !ot.IsEnum;
            if (!operandIsInFrameVt)
                return false;

            op.Code = ToInlineOpcode(code);
            return true;
        }

        // Step 12: map a heap Ldfld_*/Stfld_* opcode to its _Inline counterpart.
        static OpCodeREnum ToInlineOpcode(OpCodeREnum code)
        {
            switch (code)
            {
                case OpCodeREnum.Ldfld_I1: return OpCodeREnum.Ldfld_I1_Inline;
                case OpCodeREnum.Ldfld_I2: return OpCodeREnum.Ldfld_I2_Inline;
                case OpCodeREnum.Ldfld_I4: return OpCodeREnum.Ldfld_I4_Inline;
                case OpCodeREnum.Ldfld_I8: return OpCodeREnum.Ldfld_I8_Inline;
                case OpCodeREnum.Ldfld_U1: return OpCodeREnum.Ldfld_U1_Inline;
                case OpCodeREnum.Ldfld_U2: return OpCodeREnum.Ldfld_U2_Inline;
                case OpCodeREnum.Ldfld_U4: return OpCodeREnum.Ldfld_U4_Inline;
                case OpCodeREnum.Ldfld_U8: return OpCodeREnum.Ldfld_U8_Inline;
                case OpCodeREnum.Ldfld_R4: return OpCodeREnum.Ldfld_R4_Inline;
                case OpCodeREnum.Ldfld_R8: return OpCodeREnum.Ldfld_R8_Inline;
                case OpCodeREnum.Ldfld_Ref: return OpCodeREnum.Ldfld_Ref_Inline;
                case OpCodeREnum.Stfld_I1: return OpCodeREnum.Stfld_I1_Inline;
                case OpCodeREnum.Stfld_I2: return OpCodeREnum.Stfld_I2_Inline;
                case OpCodeREnum.Stfld_I4: return OpCodeREnum.Stfld_I4_Inline;
                case OpCodeREnum.Stfld_I8: return OpCodeREnum.Stfld_I8_Inline;
                case OpCodeREnum.Stfld_U1: return OpCodeREnum.Stfld_U1_Inline;
                case OpCodeREnum.Stfld_U2: return OpCodeREnum.Stfld_U2_Inline;
                case OpCodeREnum.Stfld_U4: return OpCodeREnum.Stfld_U4_Inline;
                case OpCodeREnum.Stfld_U8: return OpCodeREnum.Stfld_U8_Inline;
                case OpCodeREnum.Stfld_R4: return OpCodeREnum.Stfld_R4_Inline;
                case OpCodeREnum.Stfld_R8: return OpCodeREnum.Stfld_R8_Inline;
                case OpCodeREnum.Stfld_Ref: return OpCodeREnum.Stfld_Ref_Inline;
                default: return code;
            }
        }

        // VT-THIS-ADDR: true only for an inline Ldfld dest (Register1 = the
        // loaded-value temp that should carry the field type). Inline Stfld
        // opcodes are NOT seedable -- their Register1 is the OWNING in-frame VT,
        // not a destination, and stamping the field type there would clobber the
        // owner type for subsequent same-owner field accesses.
        static bool IsInlineLdfldDestSeedable(OpCodeREnum code)
        {
            switch (code)
            {
                case OpCodeREnum.Ldfld_I1_Inline:
                case OpCodeREnum.Ldfld_I2_Inline:
                case OpCodeREnum.Ldfld_I4_Inline:
                case OpCodeREnum.Ldfld_I8_Inline:
                case OpCodeREnum.Ldfld_U1_Inline:
                case OpCodeREnum.Ldfld_U2_Inline:
                case OpCodeREnum.Ldfld_U4_Inline:
                case OpCodeREnum.Ldfld_U8_Inline:
                case OpCodeREnum.Ldfld_R4_Inline:
                case OpCodeREnum.Ldfld_R8_Inline:
                case OpCodeREnum.Ldfld_Ref_Inline:
                    return true;
                default:
                    return false;
            }
        }

        // neo-raw-ldfld-boxed-ref-owner: true for any CIL `ldarg` variant (the
        // param load). Used to detect `ldarg <clrStructParam>; ldfld <field>`
        // (a delegate lambda reading a CLR-struct param's field), where the owner
        // is a boxed reference, not flat bytes.
        static bool IsLdargCode(Mono.Cecil.Cil.Code code)
        {
            switch (code)
            {
                case Mono.Cecil.Cil.Code.Ldarg:
                case Mono.Cecil.Cil.Code.Ldarg_0:
                case Mono.Cecil.Cil.Code.Ldarg_1:
                case Mono.Cecil.Cil.Code.Ldarg_2:
                case Mono.Cecil.Cil.Code.Ldarg_3:
                case Mono.Cecil.Cil.Code.Ldarg_S:
                    return true;
                default:
                    return false;
            }
        }

        // Step 12: dest temp type for an inline Ldfld primitive opcode. Mirrors
        // the heap Ldfld type-tracking cases above.
        IType FieldTypeForInlineLdfld(OpCodeREnum code)
        {
            switch (code)
            {
                case OpCodeREnum.Ldfld_I1_Inline:
                case OpCodeREnum.Ldfld_I2_Inline:
                case OpCodeREnum.Ldfld_I4_Inline:
                case OpCodeREnum.Ldfld_U1_Inline:
                case OpCodeREnum.Ldfld_U2_Inline:
                case OpCodeREnum.Ldfld_U4_Inline:
                    return appdomain.IntType;
                case OpCodeREnum.Ldfld_I8_Inline:
                case OpCodeREnum.Ldfld_U8_Inline:
                    return appdomain.LongType;
                case OpCodeREnum.Ldfld_R4_Inline:
                    return appdomain.FloatType;
                case OpCodeREnum.Ldfld_R8_Inline:
                    return appdomain.DoubleType;
                default:
                    return appdomain.IntType;
            }
        }

        // neo-raw-ldfld-stfld-clr-struct-seeding: map a CLR System.Type to the
        // Neo primitive IType used to seed registerTypes[dest] for a raw Ldfld
        // of a CLR-struct field. float->FloatType, double->DoubleType,
        // long/ulong->LongType, any OTHER primitive->IntType (matches today's
        // null->I4 fallback so non-float/double/long fields specialize
        // identically to before -- only the float/double/long fix is live), a
        // NON-primitive (VT/ref) -> null (left unseeded; does not feed primitive
        // arith, and seeding a VT would perturb the field-access discriminator).
        static IType NeoClrPrimitiveTypeToIType(Type clrType, Enviorment.AppDomain appdomain)
        {
            if (clrType == null || !clrType.IsPrimitive)
                return null;
            if (clrType == typeof(float))
                return appdomain.FloatType;
            if (clrType == typeof(double))
                return appdomain.DoubleType;
            if (clrType == typeof(long) || clrType == typeof(ulong))
                return appdomain.LongType;
            return appdomain.IntType;
        }

        IType[] BuildInitialRegisterTypes(short locVarRegStart, int totalRegCnt)
        {
            IType[] registerTypes = new IType[totalRegCnt];
            int idx = 0;
            if (method.HasThis)
                registerTypes[idx++] = declaringType;
#if ENABLE_NEO_MODE
            // V5 (neo-aot-generic-cecilfree): a Cecil-free generic-instance shell
            // (def == null) has no Cecil Parameters/Body.Variables. The concrete
            // param types are on the shell (method.Parameters, already T-
            // substituted at MakeGenericMethodShell); the open def's local types
            // are on the cached template's VariableTypes (Cecil TypeReference[],
            // re-resolved Cecil-free at S2 bind). Both flow through
            // appdomain.GetType(token, declaringType, method), which resolves a
            // generic-param TypeReference via the instance's FindGenericArgument
            // (the concrete T). Never touches def.
            TypeReference[] neoVarTypes = null;
            if (def == null)
            {
                var mp = method.Parameters;
                for (int i = 0; i < method.ParameterCount && idx < registerTypes.Length; i++, idx++)
                {
                    registerTypes[idx] = mp != null && i < mp.Count ? mp[i] : null;
                }
                neoVarTypes = GetTemplateVariableTypes();
                int neoVarCnt = neoVarTypes != null ? neoVarTypes.Length : 0;
                for (int i = 0; i < neoVarCnt; i++)
                {
                    int reg = locVarRegStart + i;
                    if (reg < registerTypes.Length)
                        registerTypes[reg] = appdomain.GetType(neoVarTypes[i], declaringType, method);
                }
                return registerTypes;
            }
#endif
            for (int i = 0; i < method.ParameterCount && idx < registerTypes.Length; i++, idx++)
            {
                registerTypes[idx] = appdomain.GetType(def.Parameters[i].ParameterType, declaringType, method);
            }
            for (int i = 0; i < def.Body.Variables.Count; i++)
            {
                int reg = locVarRegStart + i;
                if (reg < registerTypes.Length)
                    registerTypes[reg] = appdomain.GetType(def.Body.Variables[i].VariableType, declaringType, method);
            }
            return registerTypes;
        }

#if ENABLE_NEO_MODE
        // V5 (neo-aot-generic-cecilfree): recover the cached Step-22 template's
        // VariableTypes (the open def's local types, Cecil TypeReference[]) for a
        // Cecil-free generic-instance shell. Null if the instance has no cached
        // template (the caller guards def==null with a non-null template). Neo-only.
        TypeReference[] GetTemplateVariableTypes()
        {
            var gd = method.GenericDefinition as ILMethod;
            if (gd == null) return null;
            var tpl = gd.GenericMethodTemplateCache;
            return tpl != null ? tpl.VariableTypes : null;
        }

        // V5: the local count for a Cecil-free generic-instance shell = the cached
        // template's VariableTypes length (the open def's declared-local count).
        int GetLocalCount()
        {
            var vts = GetTemplateVariableTypes();
            return vts != null ? vts.Length : 0;
        }

        // V5: local i's open-def Cecil type for a Cecil-free shell = the cached
        // template's VariableTypes[i]. appdomain.GetType resolves it to the concrete
        // T via the instance's FindGenericArgument. Bounds-safe (null -> the caller
        // falls through; should not happen for a well-formed template).
        TypeReference GetLocalType(int i)
        {
            var vts = GetTemplateVariableTypes();
            return (vts != null && (uint)i < (uint)vts.Length) ? vts[i] : null;
        }
#endif

        static IType GetRegisterType(IType[] registerTypes, short reg)
        {
            if (reg >= 0 && reg < registerTypes.Length)
                return registerTypes[reg];
            return null;
        }

        static void SetRegisterType(IType[] registerTypes, short reg, IType type)
        {
            if (reg >= 0 && reg < registerTypes.Length)
                registerTypes[reg] = type;
        }

        static bool IsNeoReferenceSlot(IType type)
        {
            return type != null && !type.IsPrimitive && !type.IsValueType;
        }

        IType GetConvResultType(OpCodeREnum code)
        {
            switch (code)
            {
                case OpCodeREnum.Conv_I8:
                    return appdomain.LongType;
                case OpCodeREnum.Conv_U8:
                    return appdomain.ULongType;
                case OpCodeREnum.Conv_R4:
                    return appdomain.FloatType;
                case OpCodeREnum.Conv_R8:
                case OpCodeREnum.Conv_R_Un:
                    return appdomain.DoubleType;
                case OpCodeREnum.Conv_U4:
                case OpCodeREnum.Conv_U:
                    return appdomain.UIntType;
                default:
                    return appdomain.IntType;
            }
        }

        internal static NeoPrimitiveTypeTag InferPrimTag(IType t, Enviorment.AppDomain appdomain)
        {
            if (t == null)
                return NeoPrimitiveTypeTag.I4;

            // 解 enum 到 underlying type
            if (t is ILType ilt && ilt.IsEnum)
            {
                var fts = ilt.FieldTypes;
                if (fts != null && fts.Length > 0)
                    t = fts[0];
            }

            // 通过 TypeForCLR 比对 CLR 基本类型
            var clr = t.TypeForCLR;
            if (clr == typeof(bool) || clr == typeof(byte) || clr == typeof(sbyte)
                || clr == typeof(short) || clr == typeof(ushort) || clr == typeof(char)
                || clr == typeof(int))
                return NeoPrimitiveTypeTag.I4;
            if (clr == typeof(uint))
                return NeoPrimitiveTypeTag.U4;
            if (clr == typeof(long))
                return NeoPrimitiveTypeTag.I8;
            if (clr == typeof(ulong))
                return NeoPrimitiveTypeTag.U8;
            if (clr == typeof(float))
                return NeoPrimitiveTypeTag.R4;
            if (clr == typeof(double))
                return NeoPrimitiveTypeTag.R8;
            if (clr == typeof(IntPtr) || clr == typeof(UIntPtr))
                return IntPtr.Size == 8 ? NeoPrimitiveTypeTag.I8 : NeoPrimitiveTypeTag.I4;

            // fallback
            return NeoPrimitiveTypeTag.I4;
        }

        static OpCodeREnum NormalizeBranchOpcode(OpCodeREnum code)
        {
            switch (code)
            {
                case OpCodeREnum.Beq_S:
                    return OpCodeREnum.Beq;
                case OpCodeREnum.Bne_Un_S:
                    return OpCodeREnum.Bne_Un;
                case OpCodeREnum.Blt_S:
                    return OpCodeREnum.Blt;
                case OpCodeREnum.Blt_Un_S:
                    return OpCodeREnum.Blt_Un;
                case OpCodeREnum.Bgt_S:
                    return OpCodeREnum.Bgt;
                case OpCodeREnum.Bgt_Un_S:
                    return OpCodeREnum.Bgt_Un;
                case OpCodeREnum.Ble_S:
                    return OpCodeREnum.Ble;
                case OpCodeREnum.Ble_Un_S:
                    return OpCodeREnum.Ble_Un;
                case OpCodeREnum.Bge_S:
                    return OpCodeREnum.Bge;
                case OpCodeREnum.Bge_Un_S:
                    return OpCodeREnum.Bge_Un;
                default:
                    return code;
            }
        }

        static OpCodeREnum GetTypedBinaryOpcode(OpCodeREnum code, NeoPrimitiveTypeTag tag)
        {
            switch (tag)
            {
                case NeoPrimitiveTypeTag.I8:
                case NeoPrimitiveTypeTag.U8:
                    switch (code)
                    {
                        case OpCodeREnum.Add: return OpCodeREnum.Add_I8;
                        case OpCodeREnum.Sub: return OpCodeREnum.Sub_I8;
                        case OpCodeREnum.Mul: return OpCodeREnum.Mul_I8;
                        case OpCodeREnum.Div: return OpCodeREnum.Div_I8;
                        case OpCodeREnum.Div_Un: return OpCodeREnum.Div_Un_I8;
                        case OpCodeREnum.Rem: return OpCodeREnum.Rem_I8;
                        case OpCodeREnum.Rem_Un: return OpCodeREnum.Rem_Un_I8;
                        case OpCodeREnum.And: return OpCodeREnum.And_I8;
                        case OpCodeREnum.Or: return OpCodeREnum.Or_I8;
                        case OpCodeREnum.Xor: return OpCodeREnum.Xor_I8;
                        case OpCodeREnum.Shl: return OpCodeREnum.Shl_I8;
                        case OpCodeREnum.Shr: return OpCodeREnum.Shr_I8;
                        case OpCodeREnum.Shr_Un: return OpCodeREnum.Shr_Un_I8;
                    }
                    break;
                case NeoPrimitiveTypeTag.R4:
                    switch (code)
                    {
                        case OpCodeREnum.Add: return OpCodeREnum.Add_R4;
                        case OpCodeREnum.Sub: return OpCodeREnum.Sub_R4;
                        case OpCodeREnum.Mul: return OpCodeREnum.Mul_R4;
                        case OpCodeREnum.Div: return OpCodeREnum.Div_R4;
                        case OpCodeREnum.Rem: return OpCodeREnum.Rem_R4;
                    }
                    break;
                case NeoPrimitiveTypeTag.R8:
                    switch (code)
                    {
                        case OpCodeREnum.Add: return OpCodeREnum.Add_R8;
                        case OpCodeREnum.Sub: return OpCodeREnum.Sub_R8;
                        case OpCodeREnum.Mul: return OpCodeREnum.Mul_R8;
                        case OpCodeREnum.Div: return OpCodeREnum.Div_R8;
                        case OpCodeREnum.Rem: return OpCodeREnum.Rem_R8;
                    }
                    break;
            }
            return code;
        }

        static OpCodeREnum GetTypedUnaryOpcode(OpCodeREnum code, NeoPrimitiveTypeTag tag)
        {
            if (code == OpCodeREnum.Neg)
            {
                if (tag == NeoPrimitiveTypeTag.I8 || tag == NeoPrimitiveTypeTag.U8)
                    return OpCodeREnum.Neg_I8;
                if (tag == NeoPrimitiveTypeTag.R4)
                    return OpCodeREnum.Neg_R4;
                if (tag == NeoPrimitiveTypeTag.R8)
                    return OpCodeREnum.Neg_R8;
            }
            else if (code == OpCodeREnum.Not && (tag == NeoPrimitiveTypeTag.I8 || tag == NeoPrimitiveTypeTag.U8))
            {
                return OpCodeREnum.Not_I8;
            }
            return code;
        }

        static OpCodeREnum GetTypedCompareOpcode(OpCodeREnum code, NeoPrimitiveTypeTag tag)
        {
            switch (tag)
            {
                case NeoPrimitiveTypeTag.I8:
                case NeoPrimitiveTypeTag.U8:
                    switch (code)
                    {
                        case OpCodeREnum.Ceq: return OpCodeREnum.Ceq_I8;
                        case OpCodeREnum.Cgt: return OpCodeREnum.Cgt_I8;
                        case OpCodeREnum.Cgt_Un: return OpCodeREnum.Cgt_Un_I8;
                        case OpCodeREnum.Clt: return OpCodeREnum.Clt_I8;
                        case OpCodeREnum.Clt_Un: return OpCodeREnum.Clt_Un_I8;
                    }
                    break;
                case NeoPrimitiveTypeTag.R4:
                    switch (code)
                    {
                        case OpCodeREnum.Ceq: return OpCodeREnum.Ceq_R4;
                        case OpCodeREnum.Cgt: return OpCodeREnum.Cgt_R4;
                        case OpCodeREnum.Cgt_Un: return OpCodeREnum.Cgt_Un_R4;
                        case OpCodeREnum.Clt: return OpCodeREnum.Clt_R4;
                        case OpCodeREnum.Clt_Un: return OpCodeREnum.Clt_Un_R4;
                    }
                    break;
                case NeoPrimitiveTypeTag.R8:
                    switch (code)
                    {
                        case OpCodeREnum.Ceq: return OpCodeREnum.Ceq_R8;
                        case OpCodeREnum.Cgt: return OpCodeREnum.Cgt_R8;
                        case OpCodeREnum.Cgt_Un: return OpCodeREnum.Cgt_Un_R8;
                        case OpCodeREnum.Clt: return OpCodeREnum.Clt_R8;
                        case OpCodeREnum.Clt_Un: return OpCodeREnum.Clt_Un_R8;
                    }
                    break;
            }
            return code;
        }

        static OpCodeREnum GetTypedBranchOpcode(OpCodeREnum code, NeoPrimitiveTypeTag tag)
        {
            if (tag == NeoPrimitiveTypeTag.I8 || tag == NeoPrimitiveTypeTag.U8)
            {
                switch (code)
                {
                    case OpCodeREnum.Beq: return OpCodeREnum.Beq_I8;
                    case OpCodeREnum.Bne_Un: return OpCodeREnum.Bne_Un_I8;
                    case OpCodeREnum.Blt: return OpCodeREnum.Blt_I8;
                    case OpCodeREnum.Blt_Un: return OpCodeREnum.Blt_Un_I8;
                    case OpCodeREnum.Bgt: return OpCodeREnum.Bgt_I8;
                    case OpCodeREnum.Bgt_Un: return OpCodeREnum.Bgt_Un_I8;
                    case OpCodeREnum.Ble: return OpCodeREnum.Ble_I8;
                    case OpCodeREnum.Ble_Un: return OpCodeREnum.Ble_Un_I8;
                    case OpCodeREnum.Bge: return OpCodeREnum.Bge_I8;
                    case OpCodeREnum.Bge_Un: return OpCodeREnum.Bge_Un_I8;
                }
            }
            if (tag == NeoPrimitiveTypeTag.R4)
            {
                switch (code)
                {
                    case OpCodeREnum.Beq: return OpCodeREnum.Beq_R4;
                    case OpCodeREnum.Bne_Un: return OpCodeREnum.Bne_Un_R4;
                    case OpCodeREnum.Blt: return OpCodeREnum.Blt_R4;
                    case OpCodeREnum.Blt_Un: return OpCodeREnum.Blt_Un_R4;
                    case OpCodeREnum.Bgt: return OpCodeREnum.Bgt_R4;
                    case OpCodeREnum.Bgt_Un: return OpCodeREnum.Bgt_Un_R4;
                    case OpCodeREnum.Ble: return OpCodeREnum.Ble_R4;
                    case OpCodeREnum.Ble_Un: return OpCodeREnum.Ble_Un_R4;
                    case OpCodeREnum.Bge: return OpCodeREnum.Bge_R4;
                    case OpCodeREnum.Bge_Un: return OpCodeREnum.Bge_Un_R4;
                }
            }
            if (tag == NeoPrimitiveTypeTag.R8)
            {
                switch (code)
                {
                    case OpCodeREnum.Beq: return OpCodeREnum.Beq_R8;
                    case OpCodeREnum.Bne_Un: return OpCodeREnum.Bne_Un_R8;
                    case OpCodeREnum.Blt: return OpCodeREnum.Blt_R8;
                    case OpCodeREnum.Blt_Un: return OpCodeREnum.Blt_Un_R8;
                    case OpCodeREnum.Bgt: return OpCodeREnum.Bgt_R8;
                    case OpCodeREnum.Bgt_Un: return OpCodeREnum.Bgt_Un_R8;
                    case OpCodeREnum.Ble: return OpCodeREnum.Ble_R8;
                    case OpCodeREnum.Ble_Un: return OpCodeREnum.Ble_Un_R8;
                    case OpCodeREnum.Bge: return OpCodeREnum.Bge_R8;
                    case OpCodeREnum.Bge_Un: return OpCodeREnum.Bge_Un_R8;
                }
            }
            return code;
        }

        static OpCodeREnum GetTypedImmediateBinaryOpcode(OpCodeREnum code, NeoPrimitiveTypeTag tag)
        {
            switch (tag)
            {
                case NeoPrimitiveTypeTag.I8:
                case NeoPrimitiveTypeTag.U8:
                    switch (code)
                    {
                        case OpCodeREnum.Addi: return OpCodeREnum.Addi_I8;
                        case OpCodeREnum.Subi: return OpCodeREnum.Subi_I8;
                        case OpCodeREnum.Muli: return OpCodeREnum.Muli_I8;
                        case OpCodeREnum.Divi: return OpCodeREnum.Divi_I8;
                        case OpCodeREnum.Divi_Un: return OpCodeREnum.Divi_Un_I8;
                        case OpCodeREnum.Remi: return OpCodeREnum.Remi_I8;
                        case OpCodeREnum.Remi_Un: return OpCodeREnum.Remi_Un_I8;
                        case OpCodeREnum.Andi: return OpCodeREnum.Andi_I8;
                        case OpCodeREnum.Ori: return OpCodeREnum.Ori_I8;
                        case OpCodeREnum.Xori: return OpCodeREnum.Xori_I8;
                        case OpCodeREnum.Shli: return OpCodeREnum.Shli_I8;
                        case OpCodeREnum.Shri: return OpCodeREnum.Shri_I8;
                        case OpCodeREnum.Shri_Un: return OpCodeREnum.Shri_Un_I8;
                    }
                    break;
                case NeoPrimitiveTypeTag.R4:
                    switch (code)
                    {
                        case OpCodeREnum.Addi: return OpCodeREnum.Addi_R4;
                        case OpCodeREnum.Subi: return OpCodeREnum.Subi_R4;
                        case OpCodeREnum.Muli: return OpCodeREnum.Muli_R4;
                        case OpCodeREnum.Divi: return OpCodeREnum.Divi_R4;
                        case OpCodeREnum.Remi: return OpCodeREnum.Remi_R4;
                    }
                    break;
                case NeoPrimitiveTypeTag.R8:
                    switch (code)
                    {
                        case OpCodeREnum.Addi: return OpCodeREnum.Addi_R8;
                        case OpCodeREnum.Subi: return OpCodeREnum.Subi_R8;
                        case OpCodeREnum.Muli: return OpCodeREnum.Muli_R8;
                        case OpCodeREnum.Divi: return OpCodeREnum.Divi_R8;
                        case OpCodeREnum.Remi: return OpCodeREnum.Remi_R8;
                    }
                    break;
            }
            return code;
        }

        static OpCodeREnum GetTypedImmediateCompareOpcode(OpCodeREnum code, NeoPrimitiveTypeTag tag)
        {
            switch (tag)
            {
                case NeoPrimitiveTypeTag.I8:
                case NeoPrimitiveTypeTag.U8:
                    switch (code)
                    {
                        case OpCodeREnum.Ceqi: return OpCodeREnum.Ceqi_I8;
                        case OpCodeREnum.Cgti: return OpCodeREnum.Cgti_I8;
                        case OpCodeREnum.Cgti_Un: return OpCodeREnum.Cgti_Un_I8;
                        case OpCodeREnum.Clti: return OpCodeREnum.Clti_I8;
                        case OpCodeREnum.Clti_Un: return OpCodeREnum.Clti_Un_I8;
                    }
                    break;
                case NeoPrimitiveTypeTag.R4:
                    switch (code)
                    {
                        case OpCodeREnum.Ceqi: return OpCodeREnum.Ceqi_R4;
                        case OpCodeREnum.Cgti: return OpCodeREnum.Cgti_R4;
                        case OpCodeREnum.Cgti_Un: return OpCodeREnum.Cgti_Un_R4;
                        case OpCodeREnum.Clti: return OpCodeREnum.Clti_R4;
                        case OpCodeREnum.Clti_Un: return OpCodeREnum.Clti_Un_R4;
                    }
                    break;
                case NeoPrimitiveTypeTag.R8:
                    switch (code)
                    {
                        case OpCodeREnum.Ceqi: return OpCodeREnum.Ceqi_R8;
                        case OpCodeREnum.Cgti: return OpCodeREnum.Cgti_R8;
                        case OpCodeREnum.Cgti_Un: return OpCodeREnum.Cgti_Un_R8;
                        case OpCodeREnum.Clti: return OpCodeREnum.Clti_R8;
                        case OpCodeREnum.Clti_Un: return OpCodeREnum.Clti_Un_R8;
                    }
                    break;
            }
            return code;
        }

        static OpCodeREnum GetTypedImmediateBranchOpcode(OpCodeREnum code, NeoPrimitiveTypeTag tag)
        {
            if (tag == NeoPrimitiveTypeTag.I8 || tag == NeoPrimitiveTypeTag.U8)
            {
                switch (code)
                {
                    case OpCodeREnum.Beqi: return OpCodeREnum.Beqi_I8;
                    case OpCodeREnum.Bnei_Un: return OpCodeREnum.Bnei_Un_I8;
                    case OpCodeREnum.Blti: return OpCodeREnum.Blti_I8;
                    case OpCodeREnum.Blti_Un: return OpCodeREnum.Blti_Un_I8;
                    case OpCodeREnum.Bgti: return OpCodeREnum.Bgti_I8;
                    case OpCodeREnum.Bgti_Un: return OpCodeREnum.Bgti_Un_I8;
                    case OpCodeREnum.Blei: return OpCodeREnum.Blei_I8;
                    case OpCodeREnum.Blei_Un: return OpCodeREnum.Blei_Un_I8;
                    case OpCodeREnum.Bgei: return OpCodeREnum.Bgei_I8;
                    case OpCodeREnum.Bgei_Un: return OpCodeREnum.Bgei_Un_I8;
                }
            }
            if (tag == NeoPrimitiveTypeTag.R4)
            {
                switch (code)
                {
                    case OpCodeREnum.Beqi: return OpCodeREnum.Beqi_R4;
                    case OpCodeREnum.Bnei_Un: return OpCodeREnum.Bnei_Un_R4;
                    case OpCodeREnum.Blti: return OpCodeREnum.Blti_R4;
                    case OpCodeREnum.Blti_Un: return OpCodeREnum.Blti_Un_R4;
                    case OpCodeREnum.Bgti: return OpCodeREnum.Bgti_R4;
                    case OpCodeREnum.Bgti_Un: return OpCodeREnum.Bgti_Un_R4;
                    case OpCodeREnum.Blei: return OpCodeREnum.Blei_R4;
                    case OpCodeREnum.Blei_Un: return OpCodeREnum.Blei_Un_R4;
                    case OpCodeREnum.Bgei: return OpCodeREnum.Bgei_R4;
                    case OpCodeREnum.Bgei_Un: return OpCodeREnum.Bgei_Un_R4;
                }
            }
            if (tag == NeoPrimitiveTypeTag.R8)
            {
                switch (code)
                {
                    case OpCodeREnum.Beqi: return OpCodeREnum.Beqi_R8;
                    case OpCodeREnum.Bnei_Un: return OpCodeREnum.Bnei_Un_R8;
                    case OpCodeREnum.Blti: return OpCodeREnum.Blti_R8;
                    case OpCodeREnum.Blti_Un: return OpCodeREnum.Blti_Un_R8;
                    case OpCodeREnum.Bgti: return OpCodeREnum.Bgti_R8;
                    case OpCodeREnum.Bgti_Un: return OpCodeREnum.Bgti_Un_R8;
                    case OpCodeREnum.Blei: return OpCodeREnum.Blei_R8;
                    case OpCodeREnum.Blei_Un: return OpCodeREnum.Blei_Un_R8;
                    case OpCodeREnum.Bgei: return OpCodeREnum.Bgei_R8;
                    case OpCodeREnum.Bgei_Un: return OpCodeREnum.Bgei_Un_R8;
                }
            }
            return code;
        }

        // Step 12: round `offset` up to the next multiple of `alignment`.
        // `alignment` MUST be a power of two (caller guarantees: primitive
        // sizes 1/2/4/8 or a VT's NaturalAlignment which is itself a max of
        // such sizes). Used wherever a slot's byte offset is assigned so that
        // the typed pointer casts in the Ldfld_*_Inline / Stfld_*_Inline
        // opcodes are naturally aligned.
        static int AlignUp(int offset, int alignment)
        {
            return (offset + alignment - 1) & ~(alignment - 1);
        }

        void AllocateLocalStackSpaces(ref CompiledFrame frame)
        {
#if ENABLE_NEO_MODE
            // V5 (neo-aot-generic-cecilfree): a Cecil-free generic-instance shell
            // (def == null) -- resolve varCnt + the per-local VariableType from the
            // cached template's VariableTypes (Cecil TypeReference[] re-resolved
            // Cecil-free at S2 bind), + the params from the shell's already-T-
            // substituted Parameters. Never touches def.Body / def.Parameters.
            // The shared body below reads types via the GetLocalCount/GetParamType/
            // GetLocalType helpers, which branch on def==null.
            Mono.Cecil.Cil.MethodBody body = def != null ? def.Body : null;
            int varCnt = def != null ? body.Variables.Count : GetLocalCount();
#else
            var body = def.Body;
            int varCnt = body.Variables.Count;
#endif

            // 1) Parameter slots

            // 1) Parameter slots
            int paramCnt = method.ParameterCount + (method.HasThis ? 1 : 0);
            StackSlotInfo[] paramInfo = new StackSlotInfo[paramCnt];
            int offset = 0;
            int refOffset = 0;
            int paramIdx = 0;
            if (method.HasThis)
            {
                StackSlotInfo slot = default;
                if (declaringType.IsValueType)
                {
                    int size = declaringType.TotalPrimitiveSize;
                    int refSize = declaringType.TotalReferenceCount;
                    // Step 12: align the `this` value-type slot.
                    offset = AlignUp(offset, declaringType.NaturalAlignment);
                    slot.Offset = offset;
                    slot.RefOffset = refOffset;
                    slot.Size = size;
                    slot.RefCount = refSize;
                    offset += size;
                    refOffset += refSize;
                }
                else
                {
                    slot.Offset = offset;
                    slot.RefOffset = refOffset;
                    slot.Size = 4;
                    slot.RefCount = 1;
                    offset += 4;
                    refOffset++;
                }
                paramInfo[paramIdx++] = slot;
            }
            for (int i = 0; i < method.ParameterCount; i++)
            {
#if ENABLE_NEO_MODE
                var pt = def != null
                    ? appdomain.GetType(def.Parameters[i].ParameterType, declaringType, method)
                    : (method.Parameters != null && i < method.Parameters.Count ? method.Parameters[i] : null);
#else
                var pDef = def.Parameters[i];
                var pt = appdomain.GetType(pDef.ParameterType, declaringType, method);
#endif
                StackSlotInfo slot = AllocateSlotForType(pt, ref offset, ref refOffset);
                paramInfo[paramIdx++] = slot;
            }
            frame.ParamInfos = paramInfo;
            frame.ParamPrimitiveSize = offset;
            frame.ParamReferenceCount = refOffset;

            int localsPrimStart = offset;
            int localsRefStart = refOffset;

            // 2) Local slots
            int baseRegStart = paramCnt + varCnt;
            int locVarRegStart = paramCnt;
            StackSlotInfo[] localInfo = new StackSlotInfo[paramCnt + varCnt + frame.StackRegisterCount];
            bool[] localIsRef = new bool[localInfo.Length];
            for (int i = 0; i < paramCnt; i++)
            {
                localInfo[i] = paramInfo[i];
            }
            for (int i = 0; i < varCnt; i++)
            {
#if ENABLE_NEO_MODE
                // V5: a Cecil-free shell reads the local's open-def type from the
                // cached template's VariableTypes (a Cecil TypeReference, e.g. a
                // GenericParameter "T"); appdomain.GetType resolves it to the
                // concrete T via the instance's FindGenericArgument.
                var vt = def != null ? body.Variables[i].VariableType : GetLocalType(i);
#else
                var vt = body.Variables[i].VariableType;
#endif
                StackSlotInfo slot = default;
                if (vt.IsValueType && !vt.IsPrimitive)
                {
                    var ivt = appdomain.GetType(vt, declaringType, method);
                    if (ivt is ILType il)
                    {
                        int size = il.TotalPrimitiveSize;
                        int refSize = il.TotalReferenceCount;
                        // Step 12: align the VT local slot.
                        offset = AlignUp(offset, il.NaturalAlignment);
                        slot.Offset = offset;
                        slot.Size = size;
                        slot.RefOffset = refOffset;
                        slot.RefCount = refSize;
                        offset += size;
                        refOffset += refSize;
                    }
                    else
                    {
#if ENABLE_NEO_MODE
                        // F-MAJ-1 (neo-opt-harden-2): in Neo mode a CLR value-type
                        // LOCAL is stored as FLAT MANAGED BYTES, NOT a boxed-ref.
                        // The D6 CLR-struct return-write path (InvokeNeoClrMethod)
                        // writes the struct's flat bytes (GetNeoValueTypeManagedSize,
                        // e.g. 12 for Vector3) via WriteNeoValueType into this slot,
                        // and the D2 by-value-param read byte-copies the same flat
                        // bytes out. Declaring the slot as a 4-byte boxed-ref (the
                        // Legacy shape) UNDER-SIZED it: a 12-byte flat write
                        // overflowed 8 bytes into the neighbouring local, silently
                        // corrupting any method with 2+ simultaneously-live CLR
                        // struct locals (each Sum() read then resolved a corrupted
                        // mStack index). The fix makes the declaration AGREE with
                        // the actual flat-bytes runtime representation. Mirrors the
                        // callee param layout (AllocateNeoCallParamSlot) so a local
                        // passed by value and a param agree. A CLR struct WITH a
                        // registered ValueTypeBinder (managedCount > 0) is not
                        // supported by the reflection-fallback D6 return path (it
                        // NIEs upstream); RefCount stays 0 -- the binder path is
                        // owned by the autogen redirects.
                        int clrVtSize = Optimizer.GetNeoValueTypeManagedSize(ivt.TypeForCLR);
                        offset = AlignUp(offset, clrVtSize >= 8 ? 4 : clrVtSize);
                        slot.Offset = offset;
                        slot.RefOffset = refOffset;
                        slot.Size = clrVtSize;
                        slot.RefCount = 0;
                        offset += clrVtSize;
                        // localIsRef[locVarRegStart + i] stays false (default).
#else
                        // Legacy (ExecuteR): a CLR value-type local is a boxed-ref
                        // (mStack index). Legacy never reaches the Neo D6/D2 flat-
                        // bytes arms, so the under-sizing is harmless there.
                        offset = AlignUp(offset, 4);
                        slot.Offset = offset;
                        slot.RefOffset = refOffset;
                        slot.Size = 4;
                        slot.RefCount = 1;
                        offset += 4;
                        refOffset++;
                        localIsRef[locVarRegStart + i] = true;
#endif
                    }
                }
                else if (!vt.IsValueType)
                {
                    offset = AlignUp(offset, 4);
                    slot.Offset = offset;
                    slot.RefOffset = refOffset;
                    slot.Size = 4;
                    slot.RefCount = 1;
                    offset += 4;
                    refOffset++;
                    localIsRef[locVarRegStart + i] = true;
                }
                else
                {
                    // primitive
                    var ivt = appdomain.GetType(vt, declaringType, method);
                    int size = appdomain.GetPrimitiveSize(ivt);
                    if (size < 1) size = 1;
#if ENABLE_NEO_MODE
                    // REGTC (neo-register-transition-frame-clobber / neo-async-movenext-frame-stacking):
                    // size sub-int primitive LOCALS (bool/byte/sbyte/short/ushort/char) to int32.
                    // Every Neo primitive write path writes *(int*)retDst (4 bytes, sign/zero-extended);
                    // a 1/2-byte local slot is overrun, clobbering the neighbour (ReflectionTest14).
                    // Matches the temp file's >=8-byte slots + Legacy's 12-byte StackObject. Legacy is
                    // unaffected (Legacy locals occupy full 12-byte StackObject slots).
                    if (size < 4) size = 4;
#endif
                    // Step 12: align the primitive local to its natural size.
                    offset = AlignUp(offset, size);
                    slot.Offset = offset;
                    slot.RefOffset = refOffset;
                    slot.Size = size;
                    slot.RefCount = 0;
                    offset += size;
                }
                localInfo[locVarRegStart + i] = slot;
            }
            var valueTypes = GatherValueTypes(ref frame);
            int maxSize = 8, maxRefCount = 1;
            // Step 12: track the max natural alignment across the value types
            // that can flow through the temp register file, so each temp slot
            // is aligned for the largest possible VT it may hold.
            int maxAlignment = 4;
            foreach (var i in valueTypes)
            {
                if (i is ILType il)
                {
                    int size = il.TotalPrimitiveSize;
                    int refSize = il.TotalReferenceCount;
                    if (size > maxSize)
                        maxSize = size;
                    if (refSize > maxRefCount)
                        maxRefCount = refSize;
                    int align = il.NaturalAlignment;
                    if (align > maxAlignment)
                        maxAlignment = align;
                }
                else if (i is CLR.TypeSystem.CLRType ct)
                {
                    // Neo (neo-clr-static-vt-slot-overflow): a gathered CLR value type can flow
                    // through an eval TEMP (e.g. the dest of ldsfld on a CLR-VT static field
                    // such as TestVector3.One). The temp file is sized to maxSize (default 8),
                    // which previously grew only for ILType -- a CLR struct > 8 bytes got an
                    // undersized temp and the slot-overflow guard (NeoClrVtStaticFieldIsUnsafe)
                    // rejected the write as an OOB. Grow maxSize (and maxAlignment, mirroring
                    // the CLR-VT LOCAL declaration). maxRefCount is left alone: the reachable
                    // set is blittable (a ref-field CLR struct is refused upstream by
                    // NeoClrStructHasRefFields), so its ref count is 0.
                    int size = Optimizer.GetNeoValueTypeManagedSize(ct.TypeForCLR);
                    if (size > maxSize)
                        maxSize = size;
                    int align = size >= 8 ? 4 : size;
                    if (align > maxAlignment)
                        maxAlignment = align;
                }
            }
            for (int i = 0; i < frame.StackRegisterCount; i++)
            {
                StackSlotInfo slot = default;
                // Step 12: align each temp slot to the max VT alignment.
                offset = AlignUp(offset, maxAlignment);
                slot.Offset = offset;
                slot.RefOffset = refOffset;
                slot.Size = maxSize;
                slot.RefCount = maxRefCount;
                offset += maxSize;
                refOffset += maxRefCount;
                localInfo[baseRegStart + i] = slot;
            }
            frame.LocalInfos = localInfo;
            frame.LocalIsReference = localIsRef;
            frame.TotalStructSize = offset;
            frame.TotalRefSize = refOffset;
            frame.LocalsPrimitiveSize = offset - localsPrimStart;
            frame.LocalsReferenceCount = refOffset - localsRefStart;

            // Step 14: resolve the catch-handler exception variable's frame slot
            // from its post-compaction register index (stamped by Compile). The
            // slot was reserved by the temp-register allocation loop above
            // (baseRegStart is protected from compaction in CleanupRegister, so
            // StackRegisterCount >= 1 and localInfo[NeoCatchExceptionRegIndex]
            // is valid). ExecuteNeo reads these offsets to install the caught
            // exception on catch entry. -1 when the method has no catch handler.
            int catchRegIdx = frame.NeoCatchExceptionRegIndex;
            if (catchRegIdx >= 0 && catchRegIdx < localInfo.Length)
            {
                frame.NeoCatchExceptionByteOffset = localInfo[catchRegIdx].Offset;
                frame.NeoCatchExceptionRefOffset = localInfo[catchRegIdx].RefOffset;
            }
            else
            {
                frame.NeoCatchExceptionByteOffset = -1;
                frame.NeoCatchExceptionRefOffset = -1;
            }

            // 3) Return value
            int retPrim = 0, retRef = 0;
            var retType = method.ReturnType;
            if (retType != null && retType != appdomain.VoidType)
            {
                int dummyOffset = 0, dummyRef = 0;
                AllocateSlotForType(retType, ref dummyOffset, ref dummyRef);
                retPrim = dummyOffset;
                retRef = dummyRef;
            }
            frame.ReturnPrimitiveSize = retPrim;
            frame.ReturnRefCount = retRef;
        }

        StackSlotInfo AllocateSlotForType(IType t, ref int offset, ref int refOffset)
        {
            // Step 17: a byref-typed slot (ref/out parameter, byref local/temp)
            // is a managed address = the 8-byte Ref Slot (objectIndex:int,
            // offset:int). It owns no independent mStack reference of its own
            // (RefCount==0) and is 4-aligned. This branch runs FIRST so the
            // byref type is not mis-sized by the IsPrimitive/IsValueType
            // branches below (TypeForCLR strips the byref modifier).
            if (t != null && t.IsByRef)
            {
                offset = AlignUp(offset, 4);
                StackSlotInfo byrefSlot = default;
                byrefSlot.Offset = offset;
                byrefSlot.RefOffset = refOffset;
                byrefSlot.Size = 8;
                byrefSlot.RefCount = 0;
                offset += 8;
                return byrefSlot;
            }
            // Step 12: naturally align every slot. The alignment of a slot is
            // the max natural alignment among its fields (recursively, for
            // nested value types); a primitive uses its own size; a reference
            // slot uses pointer size 4. Alignment is only applied to the byte
            // region (offset) — the mStack ref region holds indices, not bytes.
            int align = 4;
            if (t.IsPrimitive)
            {
                align = appdomain.GetPrimitiveSize(t);
                if (align < 1) align = 1;
            }
            else if (t.IsValueType && t is ILType ilt)
            {
                align = ilt.NaturalAlignment;
                if (align < 1) align = 1;
            }
            offset = AlignUp(offset, align);

            StackSlotInfo slot = default;
            if (t.IsPrimitive)
            {
                int size = appdomain.GetPrimitiveSize(t);
                slot.Offset = offset;
                slot.RefOffset = refOffset;
                slot.Size = size;
                slot.RefCount = 0;
                offset += size;
            }
            else if (t.IsValueType && t is ILType il)
            {
                int size = il.TotalPrimitiveSize;
                int refSize = il.TotalReferenceCount;
                slot.Offset = offset;
                slot.RefOffset = refOffset;
                slot.Size = size;
                slot.RefCount = refSize;
                offset += size;
                refOffset += refSize;
            }
            else if (t.IsValueType && !(t is ILType) && t.TypeForCLR != null && t.TypeForCLR.IsEnum)
            {
                // A CLR enum is stored as its underlying primitive (flat bytes),
                // matching the JIT's ldc.i4 / initobj emission for enum locals and
                // returns, AND matching IL enums (the ILType branch above sizes them
                // TotalReferenceCount==0). Without this branch a CLR enum would fall
                // through to the `else` (boxed reference, RefCount=1), which both
                // leaves an unused ref slot AND mis-routes a CLR-enum RETURN through
                // the Ret handler's vt-with-ref-fields branch (OOB reading a non-
                // existent ref slot -- DelegateTest19). Size it as the underlying
                // primitive instead (4 for an int32 enum).
                int size = appdomain.GetPrimitiveSize(t);
                if (size < 1) size = 4;
                slot.Offset = offset;
                slot.RefOffset = refOffset;
                slot.Size = size;
                slot.RefCount = 0;
                offset += size;
            }
            else if (t.IsValueType)
            {
                // neo-vt-return-move-size: a CLR value type (not ILType, not enum --
                // both caught above) param/return slot is FLAT MANAGED BYTES, mirroring
                // the CLR-struct LOCAL declaration (the varCnt loop's CLRType else
                // branch, F-MAJ-1) AND the call-frame layout (AllocateNeoCallParamSlot,
                // Step 13b D1) so the own-frame param/return layout AGREES with the
                // caller's CopyNeoCallArguments write layout. Previously this fell
                // through to the boxed-reference branch (Size=4, RefCount=1), which
                // truncated a CLR value type RETURN: the Ret handler copies
                // `returnPrimitiveSize` bytes to the caller's dest, so a 12-byte struct
                // (e.g. TestVector3NoBinding = 3 floats) returned only its leading 4
                // bytes (x), dropping y/z -- UnitTest_TestFCP's ToColor returned (1,0,0)
                // instead of (1,1,0). (The handoff's "Move copies 4 bytes" pinning was
                // STALE: the Move correctly copies min(src,dst) bytes; the loss is in
                // the Ret, sized by this return-slot computation.) RefCount stays 0: a
                // binder struct is owned by the autogen redirects (which use
                // AllocateNeoCallParamSlot for both caller+callee), so this own-frame
                // reflection-fallback path only sees no-binder structs (RefCount 0).
                int clrVtSize = Optimizer.GetNeoValueTypeManagedSize(t.TypeForCLR);
                slot.Offset = offset;
                slot.RefOffset = refOffset;
                slot.Size = clrVtSize;
                slot.RefCount = 0;
                offset += clrVtSize;
            }
            else
            {
                // Reference type -> stored as reference (mStack index)
                slot.Offset = offset;
                slot.RefOffset = refOffset;
                slot.Size = 4; // Need 4 bytes to store the mStack index in the primitive frame
                slot.RefCount = 1;
                offset += 4;
                refOffset++;
            }
            return slot;
        }
#endif
        void PrepareJumpTable(object token)
        {
            int hashCode = token.GetHashCode();

            if (jumptables == null)
                jumptables = new Dictionary<int, int[]>();
            if (jumptables.ContainsKey(hashCode))
                return;
            Mono.Cecil.Cil.Instruction[] e = token as Mono.Cecil.Cil.Instruction[];
            int[] addrs = new int[e.Length];
            for (int i = 0; i < e.Length; i++)
            {
                addrs[i] = entryMapping[e[i]];
            }

            jumptables[hashCode] = addrs;
        }

        public static void FixSymbol(Dictionary<int, RegisterVMSymbol> symbol)
        {
            HashSet<Instruction> includedIns = new HashSet<Instruction>();
            foreach(var i in symbol.ToArray())
            {
                RegisterVMSymbol cur = i.Value;
                RegisterVMSymbolLink link = null;
                while (cur.ParentSymbol != null)
                {
                    link = cur.ParentSymbol;
                    cur = cur.ParentSymbol.Value;
                }
                var sm = cur.Method.Definition.DebugInformation.GetSequencePointMapping();
                var sq = FindSequencePoint(cur.Instruction, sm);
                if(sq != null && !includedIns.Contains(sq))
                {
                    includedIns.Add(sq);
                    cur.Instruction = sq;
                    if (link != null)
                        link.Value = cur;
                    else
                    {
                        symbol[i.Key] = cur;
                    }
                }
            }
        }

        static Instruction FindSequencePoint(Instruction ins, IDictionary<Instruction, SequencePoint> seqMapping)
        {
            Mono.Cecil.Cil.Instruction cur = ins;
            Mono.Cecil.Cil.SequencePoint sp;
            while (!seqMapping.TryGetValue(cur, out sp) && cur.Previous != null)
                cur = cur.Previous;

            return cur;
        }
        void Translate(CodeBasicBlock block, Instruction ins, short locVarRegStart, ref short baseRegIdx)
        {
            List<OpCodeR> lst = block.FinalInstructions;
            OpCodeR op = new OpCodeR();
            var code = ins.OpCode;
            var token = ins.Operand;
            op.Code = (OpCodeREnum)code.Code;
            bool hasRet;
            switch (code.Code)
            {
                case Code.Br_S:
                case Code.Br:
                    op.Operand = entryMapping[(Mono.Cecil.Cil.Instruction)token];
                    break;
                case Code.Brtrue:
                case Code.Brtrue_S:
                case Code.Brfalse:
                case Code.Brfalse_S:
                    op.Register1 = --baseRegIdx;
                    op.Operand = entryMapping[(Mono.Cecil.Cil.Instruction)token];
                    break;
                case Code.Switch:
                    op.Register1 = --baseRegIdx;
                    PrepareJumpTable(token);
                    op.Operand = token.GetHashCode();
                    break;
                case Code.Blt:
                case Code.Blt_S:
                case Code.Blt_Un:
                case Code.Blt_Un_S:
                case Code.Ble:
                case Code.Ble_S:
                case Code.Ble_Un:
                case Code.Ble_Un_S:
                case Code.Bgt:
                case Code.Bgt_S:
                case Code.Bgt_Un:
                case Code.Bgt_Un_S:
                case Code.Bge:
                case Code.Bge_S:
                case Code.Bge_Un:
                case Code.Bge_Un_S:
                case Code.Beq:
                case Code.Beq_S:
                case Code.Bne_Un:
                case Code.Bne_Un_S:
                    op.Register1 = (short)(baseRegIdx - 2);
                    op.Register2 = (short)(baseRegIdx - 1);
                    baseRegIdx -= 2;
                    op.Operand = entryMapping[(Mono.Cecil.Cil.Instruction)token];
                    break;
                case Code.Ldc_I4_0:
                case Code.Ldc_I4_1:
                case Code.Ldc_I4_2:
                case Code.Ldc_I4_3:
                case Code.Ldc_I4_4:
                case Code.Ldc_I4_5:
                case Code.Ldc_I4_6:
                case Code.Ldc_I4_7:
                case Code.Ldc_I4_8:
                case Code.Ldc_I4_M1:
                case Code.Ldnull:
                    op.Register1 = baseRegIdx++;
                    break;
                case Code.Ldc_I4:
                    op.Register1 = baseRegIdx++;
                    op.Operand = (int)token;
                    break;
                case Code.Ldc_I4_S:
                    op.Register1 = baseRegIdx++;
                    op.Operand = (sbyte)token;
                    break;
                case Code.Ldc_I8:
                    op.Register1 = baseRegIdx++;
                    op.OperandLong = (long)token;
                    break;
                case Code.Ldc_R4:
                    op.Register1 = baseRegIdx++;
                    op.OperandFloat = (float)token;
                    break;
                case Code.Ldc_R8:
                    op.Register1 = baseRegIdx++;
                    op.OperandDouble = (double)token;
                    break;
                case Code.Ldstr:
                    op.Register1 = baseRegIdx++;
                    op.OperandLong = appdomain.CacheString(token);
                    break;
                case Code.Newobj:
                    {
                        bool canInline, isILMethod;
                        ILMethod toInline;
                        IMethod m;
                        var pCnt = InitializeFunctionParam(ref op, token, out hasRet, out canInline, out m, out toInline, out isILMethod);
                        int pushCnt = Math.Max(pCnt - CallRegisterParamCount, 0);
                        for (int i = pCnt; i > pCnt - pushCnt; i--)
                        {
                            OpCodes.OpCodeR op2 = new OpCodes.OpCodeR();
                            op2.Code = OpCodes.OpCodeREnum.Push;
                            op2.Register1 = (short)(baseRegIdx - i);
                            lst.Add(op2);
                        }
                        if (pushCnt < pCnt)
                        {
                            switch (pCnt - pushCnt)
                            {
                                case 1:
                                    op.Register2 = (short)(baseRegIdx - 1);
                                    break;
                                case 2:
                                    op.Register3 = (short)(baseRegIdx - 1);
                                    op.Register2 = (short)(baseRegIdx - 2);
                                    break;
                                case 3:
                                    op.Register4 = (short)(baseRegIdx - 1);
                                    op.Register3 = (short)(baseRegIdx - 2);
                                    op.Register2 = (short)(baseRegIdx - 3);
                                    break;
                            }
                        }
                        baseRegIdx -= (short)pCnt;
                        op.Register1 = baseRegIdx++;
                        if (m is CLRMethod cm && cm.Redirection != null)
                        {
                            if (!cm.DeclearingType.IsDelegate)
                            {
                                op.Code = OpCodeREnum.Call_Redirect;
                                op.Operand4 = 0x2;
                                var rCnt = cm.ParameterCount;
                                rCnt = rCnt - Math.Max((rCnt - CallRegisterParamCount), 0);

                                op.Operand4 |= (short)rCnt << 16;
                            }
                        }
                    }
                    break;
                case Code.Call:
                case Code.Callvirt:
                    {
                        bool canInline, isILMethod;
                        ILMethod toInline;
                        IMethod m;
                        var pCnt = InitializeFunctionParam(ref op, token, out hasRet, out canInline, out m, out toInline, out isILMethod);
                        bool hasConstrained = false;
                        int constrainIdx = -1;
                        if (lst.Count > 0)
                        {
                            constrainIdx = lst.Count - 1;
                            hasConstrained = lst[constrainIdx].Code == OpCodeREnum.Constrained;
                        }
                        bool needInline = canInline && !hasConstrained;
                        if (needInline)
                        {
                            if (toInline.BodyRegister.Length > Optimizer.MaximalInlineInstructionCount / 2)
                                needInline = false;
                        }
                        if (!needInline)
                        {
                            if (code.Code == Code.Callvirt && m is ILMethod)
                            {
                                ILMethod ilm = (ILMethod)m;
                                if (!ilm.Definition.IsAbstract && !ilm.Definition.IsVirtual && !ilm.DeclearingType.IsInterface)
                                    op.Code = OpCodeREnum.Call;
                            }
#if ENABLE_NEO_MODE
                            if (code.Code == Code.Callvirt && op.Code == OpCodeREnum.Callvirt && !hasConstrained)
                            {
                                InitializeCallvirtDispatch(ref op, m);
                            }
#endif
                            int pushCnt = hasConstrained ? pCnt : Math.Max(pCnt - CallRegisterParamCount, 0);
                            for (int i = pCnt; i > pCnt - pushCnt; i--)
                            {
                                OpCodes.OpCodeR op2 = new OpCodes.OpCodeR();
                                op2.Code = OpCodes.OpCodeREnum.Push;
                                op2.Operand = isILMethod ? 1 : 0;
                                op2.Register1 = (short)(baseRegIdx - i);
                                lst.Add(op2);
                            }
                            if (pushCnt < pCnt)
                            {
                                switch(pCnt - pushCnt)
                                {
                                    case 1:
                                        op.Register2 = (short)(baseRegIdx - 1);
                                        break;
                                    case 2:
                                        op.Register3 = (short)(baseRegIdx - 1);
                                        op.Register2 = (short)(baseRegIdx - 2);
                                        break;
                                    case 3:
                                        op.Register4 = (short)(baseRegIdx - 1);
                                        op.Register3 = (short)(baseRegIdx - 2);
                                        op.Register2 = (short)(baseRegIdx - 3);
                                        break;
                                }
                            }
                            if (hasConstrained)
                            {
                                op.Operand4 = 1;
                                var old = lst[constrainIdx];
                                lst.RemoveAt(constrainIdx);
                                old.Operand2 = op.Operand2;
                                var symbol = block.InstructionMapping[constrainIdx];
                                block.InstructionMapping.Remove(constrainIdx);
                                block.InstructionMapping.Add(lst.Count, symbol);
                                lst.Add(old);
                            }
                            baseRegIdx -= (short)pCnt;

                            if (hasRet)
                                op.Register1 = baseRegIdx++;
                            else
                                op.Register1 = -1;
                            if (m is CLRMethod cm && cm.Redirection != null &&
                                op.Code != OpCodeREnum.Callvirt &&
                                op.Code != OpCodeREnum.Callvirt_IL &&
                                op.Code != OpCodeREnum.Callvirt_CLR)
                            {
                                if (!cm.IsDelegateInvoke && !cm.IsDelegateDynamicInvoke)
                                {
                                    op.Code = OpCodeREnum.Call_Redirect;
                                    op.Operand4 = 0;
                                    if (hasConstrained)
                                        op.Operand4 |= 0x1;
                                    if (cm.ReturnType != appdomain.VoidType && !cm.IsConstructor)
                                        op.Operand4 |= 0x4;

                                    var rCnt = cm.HasThis ? cm.ParameterCount + 1 : cm.ParameterCount;
                                    rCnt = rCnt - Math.Max((rCnt - CallRegisterParamCount), 0);

                                    op.Operand4 |= (short)rCnt << 16;
                                }
                            }
                        }
                        else
                        {
                            baseRegIdx -= (short)pCnt;
                            RegisterVMSymbolLink link = null;
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
                            link = new RegisterVMSymbolLink();
                            link.BaseRegisterIndex = baseRegIdx;
                            link.Value.Instruction = ins;
                            link.Value.Method = method;
#else
                            RegisterVMSymbol vmS = new RegisterVMSymbol()
                            {
                                Instruction = ins,
                                Method = method
                            };
                            block.InstructionMapping.Add(lst.Count,vmS);
#endif
#if DEBUG && !NO_PROFILER
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == method.AppDomain.UnityMainThreadID)

#if UNITY_5_5_OR_NEWER
                UnityEngine.Profiling.Profiler.BeginSample("JITCompiler.InlineMethod");
#else
                UnityEngine.Profiler.BeginSample("JITCompiler.InlineMethod");
#endif

#endif
                            Optimizer.InlineMethod(block, toInline, link, ref jumptables, baseRegIdx, hasRet);
#if DEBUG && !NO_PROFILER
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == method.AppDomain.UnityMainThreadID)
#if UNITY_5_5_OR_NEWER
                UnityEngine.Profiling.Profiler.EndSample();
#else
                UnityEngine.Profiler.EndSample();
#endif
#endif
                            if (hasRet)
                                baseRegIdx++;
                            return;
                        }
                    }
                    break;
                case Code.Ldsfld:
                case Code.Ldsflda:
                    op.Register1 = baseRegIdx++;
                    op.OperandLong = appdomain.GetStaticFieldIndex(token, declaringType, method);
                    break;
                case Code.Stsfld:
                    op.Register1 = --baseRegIdx;
                    op.OperandLong = appdomain.GetStaticFieldIndex(token, declaringType, method);
                    break;
                case Code.Initobj:
                    op.Register1 = --baseRegIdx;
                    op.Operand = method.GetTypeTokenHashCode(token);
#if ENABLE_NEO_MODE
                    // neo-initobj-ref-byref: stamp the byref-operand marker when
                    // the initobj target T is a reference type and the CIL
                    // predecessor is an `ldarg` (the `result = default(T)` shape
                    // on a `ref T result` byref parameter: CIL `ldarg <byref
                    // param>; initobj T`). By CIL typing, `ldarg; initobj T`
                    // implies the ldarg loaded a `T&` byref -- a genuine managed
                    // pointer that is NEVER addr-alias-folded (unlike an
                    // ldloca-of-a-stack-temp, which the optimizer folds so the
                    // initobj's DstOffset resolves to the temp slot itself and
                    // must keep the direct write). The runtime reference-type arm
                    // must DEREF through the byref and write the -1 null sentinel
                    // at the TARGET, not direct-write the byref temp. Gating on
                    // the CIL `ldarg` predecessor (stable IL order, not the
                    // unreliable register-VM alias/localIsRef state) sidesteps
                    // the discrimination problem that defeated the prior
                    // recluster-21 runtime approaches (they could not distinguish
                    // the genuine byref from the folded temp and regressed
                    // ActivatorCreateInstanceWithArgsTest + InheritanceTest20).
                    // The Neo inliner (Optimizer.InlineMethod) copies OpCodeR by
                    // value and only remaps registers, so the marker propagates
                    // to the inlined copy (the `ref T` helper is typically
                    // inlined into the caller). LowerNeoOffsets' Initobj case
                    // does not touch Operand4. See NeoInitobjByRefOperandMarker.
                    // neo-recluster-10: the marker ALSO fires for a `Code.Ldflda`
                    // predecessor -- Roslyn lowers `refField = null` / `= default(T)`
                    // (T : class) on a HEAP reference field to `ldflda <refField>;
                    // initobj T`, producing a genuine managed byref (objIdx,
                    // ReferenceOffset) that is NOT addr-alias-folded. Without the
                    // marker the runtime initobj direct-wrote -1 to the byref TEMP
                    // and the field stayed non-null (UnitTest_1013's SetNull:
                    // `tValue = null` on a generic T=object field). A value-type
                    // field's ldflda+initobj never reaches here (`!initT.IsValueType`
                    // gate), so the fold-vs-genuine discrimination is unchanged.
                    {
                        var initT = appdomain.GetType(token, declaringType, method);
                        if (initT != null && !initT.IsValueType
                            && ins.Previous != null
                            && (IsLdargCode(ins.Previous.OpCode.Code)
                                || ins.Previous.OpCode.Code == Code.Ldflda))
                            op.Operand4 |= NeoInitobjByRefOperandMarker;
                    }
#endif
                    break;
                case Code.Ret:
                    if (hasReturn)
                        op.Register1 = --baseRegIdx;
                    break;
                case Code.Throw:
                    op.Register1 = --baseRegIdx;
                    break;
                case Code.Add:
                case Code.Add_Ovf:
                case Code.Add_Ovf_Un:
                case Code.Sub:
                case Code.Sub_Ovf:
                case Code.Sub_Ovf_Un:
                case Code.Mul:
                case Code.Mul_Ovf:
                case Code.Mul_Ovf_Un:
                case Code.Div:
                case Code.Div_Un:
                case Code.Rem:
                case Code.Rem_Un:
                case Code.Shr:
                case Code.Shr_Un:
                case Code.Shl:
                case Code.Xor:
                case Code.Or:
                case Code.And:
                case Code.Clt:
                case Code.Clt_Un:
                case Code.Cgt:
                case Code.Cgt_Un:
                case Code.Ceq:
                case Code.Ldelema:
                case Code.Ldelem_I1:
                case Code.Ldelem_U1:
                case Code.Ldelem_I2:
                case Code.Ldelem_U2:
                case Code.Ldelem_I4:
                case Code.Ldelem_U4:
                case Code.Ldelem_I8:
                case Code.Ldelem_R4:
                case Code.Ldelem_R8:
                case Code.Ldelem_Any:
                case Code.Ldelem_Ref:
                    op.Register1 = (short)(baseRegIdx - 2); //explicit use dest register for optimization
                    op.Register2 = (short)(baseRegIdx - 2);
                    op.Register3 = (short)(baseRegIdx - 1);
                    baseRegIdx--;
                    break;
                // ---- D-ARR (rank-1 completion): native-int element load.
                // Code.Ldelem_I casts directly to OpCodeREnum.Ldelem_I (same enum
                // ordering); the runtime has a dedicated Ldelem_I arm (native-int
                // array kinds IntPtr[]/UIntPtr[] plus int[]/uint[]). Additive:
                // previously hit the JIT `default` NIE.
                //
                // NOTE on the proposal's other 3 codes: this Mono.Cecil fork's
                // `Code` enum has NO `Code.Ldelem`, `Code.Stelem`, or
                // `Code.Ldelem_U8` -- the generic-with-token form is
                // `Code.Ldelem_Any`/`Code.Stelem_Any` (0xa3/0xa4), already
                // enumerated above; `Ldelem_U8` is not a real ECMA opcode (an
                // 8-byte unsigned load is just Ldelem_I8). So those 3 cases do
                // not exist to add. Only Ldelem_I is real.
                case Code.Ldelem_I:
                    op.Register1 = (short)(baseRegIdx - 2);
                    op.Register2 = (short)(baseRegIdx - 2);
                    op.Register3 = (short)(baseRegIdx - 1);
                    baseRegIdx--;
                    break;
                case Code.Nop:
                case Code.Readonly:
                case Code.Volatile:
                case Code.Endfinally:
                case Code.Rethrow:
                    break;
                case Code.Leave:
                case Code.Leave_S:
                    break;
                case Code.Stloc_0:
                    op.Code = OpCodes.OpCodeREnum.Move;
                    op.Register1 = locVarRegStart;
                    op.Register2 = --baseRegIdx;
                    break;
                case Code.Stloc_1:
                    op.Code = OpCodes.OpCodeREnum.Move;
                    op.Register1 = (short)(locVarRegStart + 1);
                    op.Register2 = --baseRegIdx;
                    break;
                case Code.Stloc_2:
                    op.Code = OpCodes.OpCodeREnum.Move;
                    op.Register1 = (short)(locVarRegStart + 2);
                    op.Register2 = --baseRegIdx;
                    break;
                case Code.Stloc_3:
                    op.Code = OpCodes.OpCodeREnum.Move;
                    op.Register1 = (short)(locVarRegStart + 3);
                    op.Register2 = --baseRegIdx;
                    break;
                case Code.Stloc_S:
                    op.Code = OpCodes.OpCodeREnum.Move;
                    op.Register1 = (short)(locVarRegStart + ((VariableDefinition)ins.Operand).Index);
                    op.Register2 = --baseRegIdx;
                    break;
                case Code.Ldloc_0:
                    op.Code = OpCodes.OpCodeREnum.Move;
                    op.Register1 = baseRegIdx++;
                    op.Register2 = locVarRegStart;
                    break;
                case Code.Ldloc_1:
                    op.Code = OpCodes.OpCodeREnum.Move;
                    op.Register1 = baseRegIdx++;
                    op.Register2 = (short)(locVarRegStart + 1);
                    break;
                case Code.Ldloc_2:
                    op.Code = OpCodes.OpCodeREnum.Move;
                    op.Register1 = baseRegIdx++;
                    op.Register2 = (short)(locVarRegStart + 2);
                    break;
                case Code.Ldloc_3:
                    op.Code = OpCodes.OpCodeREnum.Move;
                    op.Register1 = baseRegIdx++;
                    op.Register2 = (short)(locVarRegStart + 3);
                    break;
                case Code.Ldloc:
                case Code.Ldloc_S:
                    op.Code = OpCodes.OpCodeREnum.Move;
                    op.Register1 = baseRegIdx++;
                    op.Register2 = (short)(locVarRegStart + ((VariableDefinition)ins.Operand).Index);
                    break;
                case Code.Ldloca:
                case Code.Ldloca_S:
                    op.Register1 = baseRegIdx++;
                    op.Register2 = (short)(locVarRegStart + ((VariableDefinition)ins.Operand).Index);
                    break;
                case Code.Ldarg_0:
                case Code.Ldarg_1:
                case Code.Ldarg_2:
                case Code.Ldarg_3:
                    op.Code = OpCodes.OpCodeREnum.Move;
                    op.Register1 = baseRegIdx++;
                    op.Register2 = (short)(code.Code - (Code.Ldarg_0));
                    break;
                case Code.Ldarg_S:
                    op.Code = OpCodes.OpCodeREnum.Move;
                    op.Register1 = baseRegIdx++;
                    op.Register2 = (short)((ParameterDefinition)ins.Operand).Index;
                    if (def.HasThis)
                    {
                        op.Register2++;
                    }
                    break;
                case Code.Ldarga:
                case Code.Ldarga_S:
                    op.Register1 = baseRegIdx++;
                    op.Register2 = (short)((ParameterDefinition)ins.Operand).Index;
                    if (def.HasThis)
                    {
                        op.Register2++;
                    }
                    break;
                case Code.Starg:
                case Code.Starg_S:
                    op.Code = OpCodes.OpCodeREnum.Move;
                    op.Register2 = --baseRegIdx;
                    op.Register1 = (short)((ParameterDefinition)ins.Operand).Index;
                    if (def.HasThis)
                    {
                        op.Register1++;
                    }
                    break;
                case Code.Newarr:
                    op.Register1 = (short)(baseRegIdx - 1);
                    op.Register2 = (short)(baseRegIdx - 1);
                    op.Operand = method.GetTypeTokenHashCode(token);
                    break;
                case Code.Dup:
                    op.Code = OpCodes.OpCodeREnum.Move;
                    op.Register2 = (short)(baseRegIdx - 1);
                    op.Register1 = baseRegIdx++;
                    break;
                case Code.Stelem_I:
                case Code.Stelem_I1:
                case Code.Stelem_I2:
                case Code.Stelem_I4:
                case Code.Stelem_I8:
                case Code.Stelem_R4:
                case Code.Stelem_R8:
                case Code.Stelem_Ref:
                case Code.Stelem_Any:
                    op.Register1 = (short)(baseRegIdx - 3);
                    op.Register2 = (short)(baseRegIdx - 2);
                    op.Register3 = (short)(baseRegIdx - 1);
                    baseRegIdx -= 3;
                    break;
                case Code.Stind_I:
                case Code.Stind_I1:
                case Code.Stind_I2:
                case Code.Stind_I4:
                case Code.Stind_I8:
                case Code.Stind_R4:
                case Code.Stind_R8:
                case Code.Stind_Ref:
                    op.Register1 = (short)(baseRegIdx - 2);
                    op.Register2 = (short)(baseRegIdx - 1);
                    baseRegIdx -= 2;
                    break;
                case Code.Stobj:
                    op.Register1 = (short)(baseRegIdx - 2);
                    op.Register2 = (short)(baseRegIdx - 1);
                    op.Operand = method.GetTypeTokenHashCode(token);
                    baseRegIdx -= 2;
                    break;
                case Code.Conv_I:
                case Code.Conv_I1:
                case Code.Conv_I2:
                case Code.Conv_I4:
                case Code.Conv_I8:
                case Code.Conv_Ovf_I:
                case Code.Conv_Ovf_I1:
                case Code.Conv_Ovf_I1_Un:
                case Code.Conv_Ovf_I2:
                case Code.Conv_Ovf_I2_Un:
                case Code.Conv_Ovf_I4:
                case Code.Conv_Ovf_I4_Un:
                case Code.Conv_Ovf_I8:
                case Code.Conv_Ovf_I8_Un:
                case Code.Conv_Ovf_I_Un:
                case Code.Conv_Ovf_U:
                case Code.Conv_Ovf_U1:
                case Code.Conv_Ovf_U1_Un:
                case Code.Conv_Ovf_U2:
                case Code.Conv_Ovf_U2_Un:
                case Code.Conv_Ovf_U4:
                case Code.Conv_Ovf_U4_Un:
                case Code.Conv_Ovf_U8:
                case Code.Conv_Ovf_U8_Un:
                case Code.Conv_Ovf_U_Un:
                case Code.Conv_R4:
                case Code.Conv_R8:
                case Code.Conv_R_Un:
                case Code.Conv_U:
                case Code.Conv_U1:
                case Code.Conv_U2:
                case Code.Conv_U4:
                case Code.Conv_U8:
                case Code.Ldlen:
                case Code.Ldind_I:
                case Code.Ldind_I2:
                case Code.Ldind_I4:
                case Code.Ldind_I8:
                case Code.Ldind_R4:
                case Code.Ldind_R8:
                case Code.Ldind_U1:
                case Code.Ldind_U2:
                case Code.Ldind_U4:
                case Code.Ldind_Ref:
                case Code.Neg:
                case Code.Not:
                    op.Register1 = (short)(baseRegIdx - 1);
                    op.Register2 = (short)(baseRegIdx - 1);
                    break;
                case Code.Ldobj:
                    op.Register1 = (short)(baseRegIdx - 1);
                    op.Register2 = (short)(baseRegIdx - 1);
                    op.Operand = method.GetTypeTokenHashCode(token);
                    break;
                case Code.Ldfld:
#if ENABLE_NEO_MODE
                    op.Register1 = (short)(baseRegIdx - 1);
                    op.Register2 = (short)(baseRegIdx - 1);
                    {
                        var offset = appdomain.GetFieldOffset(token, declaringType, method, out IType type, out IType fieldType);
                        if (type is ILType)
                        {
                            op.Code = GetLdfldCodeForType(fieldType);
                            op.Operand = type.GetHashCode();
                            op.Operand2 = offset.PrimitiveOffset;
                            op.Operand3 = offset.ReferenceOffset;
                            // Step 12b (stfld.value/ldfld.value): a whole-IL-VT field
                            // load/store. Stamp the FIELD's ILType hash into Operand4 so
                            // the runtime ExecuteNeo arm resolves TotalPrimitiveSize /
                            // TotalReferenceCount (the declaring-type hash in Operand is
                            // NOT the field type). No-op for the typed Ldfld_* (Operand4
                            // is left 0; the runtime arm only reads it for Ldfld_Value).
                            if (op.Code == OpCodeREnum.Ldfld_Value)
                                op.Operand4 = fieldType.GetHashCode();
                            // F-10: a CLR-struct field of an IL instance is a
                            // reference slot holding the boxed struct. Stamp the
                            // field's type hash into Operand4 so the runtime
                            // Ldfld_Ref arm flattens the boxed struct into the
                            // dest flat-bytes region (instead of materializing a
                            // ref). No-op for non-F-10 fields (Operand4 stays 0).
                            if (IsClrStructFieldOfIL(type, fieldType))
                                op.Operand4 = fieldType.GetHashCode();
                        }
                        else
                        {
                            op.OperandLong = ((long)type.GetHashCode() << 32) | (uint)offset.PrimitiveOffset;
                            // neo-raw-ldfld-array-element: the owner is a CLR-struct
                            // ARRAY ELEMENT byref (ldelema-produced) rather than a
                            // flat-bytes local value. The untyped Neo frame cannot
                            // distinguish these at runtime (a flat-bytes struct's first
                            // int field can coincidentally index an Array in mStack ->
                            // a plain `mStack[objIdx] is Array` check has a constructible
                            // collision, unlike raw Stfld whose value-type owner is ALWAYS
                            // a byref), so mark the shape at JIT time. The `ldelema`
                            // immediately precedes the `ldfld` in `arr[i].field` and its
                            // dest register (baseRegIdx-2 then baseRegIdx--) IS the ldfld
                            // owner register (Register2 = baseRegIdx-1 after the
                            // decrement) -> the CIL Previous link is the reliable signal.
                            // `readonly.`/`constrained.` are prefixes (precede ldelema,
                            // never sit between ldelema and ldfld). The marker is a static
                            // bit on the ldfld op -> survives LowerNeoOffsets (raw-Ldfld
                            // case does not touch Operand4) and TypeSpecializeNeoOpcodes
                            // (raw-Ldfld seeding case reads only OperandLong/Register1).
                            if (ins.Previous != null && ins.Previous.OpCode.Code == Code.Ldelema)
                                op.Operand4 |= NeoRawLdfldArrayElementByRefMarker;
                            // neo-raw-ldfld-clr-object-vt-field: the owner is a
                            // byref produced by `ldflda <CLR-struct field of a CLR
                            // object>` (the READ counterpart of child-27's Stfld).
                            // `obj.Struct.field` lowers to `ldflda Struct(on obj);
                            // ldfld field(on the struct address)` -- the ldflda is
                            // the immediate CIL predecessor and its dest register
                            // IS the ldfld's owner register. Mutually exclusive
                            // with the Ldelema stamp above (one predecessor per CIL
                            // instruction). Prefixes (readonly./constrained.)
                            // precede ldflda, never sit between ldflda and ldfld;
                            // a volatile./unaligned. prefix ON the ldfld itself is
                            // a rare edge that skips the marker (fails safe).
                            else if (ins.Previous != null && ins.Previous.OpCode.Code == Code.Ldflda)
                                op.Operand4 |= NeoRawLdfldClrObjectFieldByRefMarker;
                            // neo-raw-ldfld-boxed-ref-owner: the owner is a CLR
                            // value-type PARAMETER loaded by `ldarg` -- stored as
                            // a boxed reference (mStack index), NOT flat bytes.
                            // Mutually exclusive with 0x1/0x2 (one CIL predecessor).
                            else if (ins.Previous != null && IsLdargCode(ins.Previous.OpCode.Code))
                                op.Operand4 |= NeoRawLdfldBoxedRefOwnerMarker;
                        }
                    }
                    break;
#endif
                case Code.Ldflda:
                    op.Register1 = (short)(baseRegIdx - 1);
                    op.Register2 = (short)(baseRegIdx - 1);
#if ENABLE_NEO_MODE
                    {
                        // Step 12: capture the field's PrimitiveOffset / ReferenceOffset
                        // so the Neo offset-lowering pass can fold a nested value-type
                        // field address (ldloca + ldflda chain) into the leaf field
                        // access's absolute frame offset. Legacy keeps the static-field
                        // index in OperandLong.
                        var offset = appdomain.GetFieldOffset(token, declaringType, method, out IType type, out IType fieldType);
                        op.Operand = type.GetHashCode();
                        op.Operand2 = offset.PrimitiveOffset;
                        op.Operand3 = offset.ReferenceOffset;
                        // F-10: stamp the CLR-struct-field-of-IL marker (bit 0x2,
                        // mutually exclusive with F-6's in-frame-VT marker 0x1).
                        // The runtime Ldflda arm produces a byref carrying the
                        // field's ReferenceOffset (NOT the stale PrimitiveOffset)
                        // + a high-bit flag so the byref consumers route to
                        // ManagedObjects[refOff].
                        if (IsClrStructFieldOfIL(type, fieldType))
                            op.Operand4 |= NeoLdfldaClrStructFieldMarker;
                        // neo-byref-ldind-ref-heap: a reference-typed field of a
                        // heap IL class (string / IL-class / object) is stored in
                        // ManagedObjects[ReferenceOffset], NOT Primitives. Stamp
                        // bit 0x4 so the runtime Ldflda arm produces a byref
                        // carrying ReferenceOffset (unflagged; ldind_ref/stind_ref
                        // dispatch on `mStack[objIdx] is ILTypeInstance`).
                        // Mutually exclusive with F-6/F-10 (a ref field is neither
                        // an in-frame VT nor a CLR value type).
                        else if (type is ILType && !fieldType.IsValueType && !fieldType.IsPrimitive)
                            op.Operand4 |= NeoLdfldaHeapIlRefFieldMarker;
                        // neo-ldind-stind-byref-clr-struct (child 15): the declaring
                        // type is a CLRType -> `offset.PrimitiveOffset` stamped into
                        // Operand2 above is `FieldInfo.GetHashCode()`, NOT a byte
                        // offset. Stamp bit 0x8 so the runtime Ldflda frame-native
                        // branch (`objectIndex == -1`, from `ldloca <CLR-struct
                        // local>`) resolves the field's REAL managed byte offset via
                        // a cached `Marshal.OffsetOf` instead of dereferencing the
                        // hash (which would AV). Mutually exclusive with F-6/F-10/
                        // heap-IL-ref (all require `type is ILType`); this branch
                        // requires `type is CLRType`. (A CLR struct field ON an IL
                        // heap instance is F-10 above, handled first.)
                        else if (type is CLRType)
                        {
                            op.Operand4 |= NeoLdfldaClrStructLocalFieldMarker;
                            // neo-nested-ldflda-byref: the operand is a byref
                            // produced by a preceding address-of (Ldflda/Ldsflda)
                            // -- this ldflda drills INTO a struct field's address
                            // (`outer.Struct.field += N` -> `ldflda Struct;
                            // ldflda field; ldind; add; stind`). The CIL
                            // predecessor is the immediate address-producer and
                            // its dest register IS this ldflda's owner register
                            // (same dataflow link as child-24/29 raw-Ldfld on a
                            // byref owner). Mutually exclusive with the direct-
                            // heap-object case (predecessor is a load, not an
                            // address-of). Gated on `type is CLRType` so an
                            // IL-struct inner field takes its existing path.
                            if (ins.Previous != null && (ins.Previous.OpCode.Code == Code.Ldflda || ins.Previous.OpCode.Code == Code.Ldsflda))
                                op.Operand4 |= NeoLdfldaNestedByRefMarker;
                        }
                    }
#else
                    op.OperandLong = appdomain.GetStaticFieldIndex(token, declaringType, method);
#endif
                    break;
                case Code.Stfld:
                    op.Register1 = (short)(baseRegIdx - 2);
                    op.Register2 = (short)(baseRegIdx - 1);
#if ENABLE_NEO_MODE
                    {
                        var offset = appdomain.GetFieldOffset(token, declaringType, method, out IType type, out IType fieldType);
                        if(type is ILType)
                        {
                            op.Code = GetStfldCodeForType(fieldType);
                            op.Operand = type.GetHashCode();
                            op.Operand2 = offset.PrimitiveOffset;
                            op.Operand3 = offset.ReferenceOffset;
                            // Step 12b (stfld.value/ldfld.value): a whole-IL-VT field
                            // store. Stamp the FIELD's ILType hash into Operand4 so the
                            // runtime ExecuteNeo arm resolves TotalPrimitiveSize /
                            // TotalReferenceCount. No-op for the typed Stfld_* (Operand4
                            // is left 0; the runtime arm only reads it for Stfld_Value).
                            if (op.Code == OpCodeREnum.Stfld_Value)
                                op.Operand4 = fieldType.GetHashCode();
                            // F-10: stamp the field's type hash into Operand4 so
                            // the runtime Stfld_Ref arm boxes the source flat
                            // bytes into the field's CLR type and stores the
                            // boxed struct at ManagedObjects[ReferenceOffset]
                            // (instead of reading the source's first 4 bytes as a
                            // ref-slot mStack index). No-op for non-F-10 fields.
                            if (IsClrStructFieldOfIL(type, fieldType))
                                op.Operand4 = fieldType.GetHashCode();
                        }
                        else
                            op.OperandLong = ((long)type.GetHashCode() << 32) | (uint)offset.PrimitiveOffset;
                    }
#else
                    op.OperandLong = appdomain.GetStaticFieldIndex(token, declaringType, method);
#endif
                    baseRegIdx -= 2;
                    break;
                case Code.Box:
                case Code.Unbox:
                case Code.Unbox_Any:
                case Code.Isinst:
                case Code.Castclass:
                    op.Register1 = (short)(baseRegIdx - 1);
                    op.Register2 = (short)(baseRegIdx - 1);
                    op.Operand = method.GetTypeTokenHashCode(token);
                    break;
                case Code.Constrained:
                    op.Operand = method.GetTypeTokenHashCode(token);
                    break;
                case Code.Ldtoken:
                    op.Register1 = baseRegIdx++;
                    if (token is FieldReference)
                    {
                        op.Operand = 0;
                        op.OperandLong = appdomain.GetStaticFieldIndex(token, declaringType, method);
                    }
                    else if (token is TypeReference)
                    {
                        op.Operand = 1;
                        op.OperandLong = method.GetTypeTokenHashCode(token);
                    }
                    else
                        throw new NotImplementedException(
                            $"Neo Ldtoken: unhandled token shape {token?.GetType().Name} (expected FieldReference/TypeReference) [neo-bare-nie]");
                    break;
                case Code.Ldftn:
                    {
                        op.Register1 = baseRegIdx++;
                        bool hasReturn, canInline, isILMethod;
                        ILMethod toInline;
                        IMethod m;
                        InitializeFunctionParam(ref op, token, out hasReturn, out canInline, out m, out toInline, out isILMethod);
                    }
                    break;

                case Code.Ldvirtftn:
                    {
                        bool hasReturn, canInline, isILMethod;
                        ILMethod toInline;
                        IMethod m;
                        InitializeFunctionParam(ref op, token, out hasReturn, out canInline, out m, out toInline, out isILMethod);
                        op.Register1 = (short)(baseRegIdx - 1);
                        op.Register2 = (short)(baseRegIdx - 1);
                    }
                    break;
                case Code.Pop:
                    baseRegIdx--;
                    op.Code = OpCodeREnum.Nop;
                    break;
                default:
                    throw new NotImplementedException(string.Format("Unknown Opcode:{0}", code.Code));
            }
            RegisterVMSymbol s = new RegisterVMSymbol()
            {
                Instruction = ins,
                Method = method
            };
            block.InstructionMapping.Add(lst.Count, s);
            lst.Add(op);
            if (!block.NeedLoadConstantElimination)
                block.NeedLoadConstantElimination = Optimizer.IsLoadConstant(op.Code);
        }
#if ENABLE_NEO_MODE
        OpCodeREnum GetLdfldCodeForType(IType fieldType)
        {
            OpCodeREnum res = OpCodeREnum.Ldfld_Ref;
            if (fieldType.IsPrimitive)
            {
                if (fieldType == appdomain.IntType)
                {
                    res = OpCodeREnum.Ldfld_I4;
                }
                else if (fieldType == appdomain.LongType)
                {
                    res = OpCodeREnum.Ldfld_I8;
                }
                else if (fieldType == appdomain.ShortType)
                {
                    res = OpCodeREnum.Ldfld_I2;
                }
                else if (fieldType == appdomain.ByteType)
                {
                    res = OpCodeREnum.Ldfld_U1;
                }
                else if (fieldType == appdomain.BoolType)
                {
                    res = OpCodeREnum.Ldfld_I1;
                }
                else if (fieldType == appdomain.FloatType)
                {
                    res = OpCodeREnum.Ldfld_R4;
                }
                else if (fieldType == appdomain.DoubleType)
                {
                    res = OpCodeREnum.Ldfld_R8;
                }
                else if (fieldType == appdomain.SByteType)
                {
                    res = OpCodeREnum.Ldfld_I1;
                }
                else if (fieldType == appdomain.UShortType)
                {
                    res = OpCodeREnum.Ldfld_U2;
                }
                else if (fieldType == appdomain.UIntType)
                {
                    res = OpCodeREnum.Ldfld_U4;
                }
                else if (fieldType == appdomain.ULongType)
                {
                    res = OpCodeREnum.Ldfld_U8;
                }
                else if (fieldType == appdomain.CharType)
                {
                    res = OpCodeREnum.Ldfld_U4;
                }
                else if (fieldType == appdomain.IntPtrType)
                {
                    res = OpCodeREnum.Ldfld_U8;
                }
                else
                    throw new NotImplementedException(
                        $"Neo GetLdfldCodeForType: IL field '{fieldType?.FullName}' has no typed Ldfld opcode [neo-bare-nie]");
            }
            else
            {
                if (fieldType is ILType && fieldType.IsValueType)
                {
                    res = OpCodeREnum.Ldfld_Value;
                }
                else
                    res = OpCodeREnum.Ldfld_Ref;
            }
            return res;
        }
        // F-10 / NEO-CLRSTRUCT-FIELD-OF-IL: returns true when `declaringType`
        // is an ILType AND `fieldType` is a CLR value type (the field is laid
        // out as a reference slot holding the boxed struct). The caller stamps
        // the field's type hash into the opcode's Operand4 so the runtime heap
        // field-access arms can box/unbox the struct between flat bytes and
        // ManagedObjects[ReferenceOffset]. Excludes ILType fields (the IL-VT
        // field path, Stfld_Value) and reference types (genuine ref slots).
        bool IsClrStructFieldOfIL(IType declaringType, IType fieldType)
        {
            return declaringType is ILType
                && !(fieldType is ILType)
                && fieldType.IsValueType
                && !fieldType.IsPrimitive;
        }
        OpCodeREnum GetStfldCodeForType(IType fieldType)
        {
            return GetNeoStfldCodeForType(fieldType, appdomain);
        }

        // neo-typeof-generic-param: static form exposed so GenericMethodTemplateOps
        // can derive the typed Stfld opcode a concrete generic-arg would select,
        // WITHOUT a JITCompiler instance. Used by the template category-fall-back
        // (a concrete T whose typed field-opcode category differs from the capture
        // T -> the cloned template body's pre-specialized typed arms (Stfld_I4 etc.)
        // would be wrong -> fall back to per-occurrence JIT). Authoritative: the
        // instance method delegates here, so the two cannot diverge.
        internal static OpCodeREnum GetNeoStfldCodeForType(IType fieldType, Enviorment.AppDomain appdomain)
        {
            OpCodeREnum res = OpCodeREnum.Stfld_Ref;
            if (fieldType.IsPrimitive)
            {
                if (fieldType == appdomain.IntType)
                {
                    res = OpCodeREnum.Stfld_I4;
                }
                else if (fieldType == appdomain.LongType)
                {
                    res = OpCodeREnum.Stfld_I8;
                }
                else if (fieldType == appdomain.ShortType)
                {
                    res = OpCodeREnum.Stfld_I2;
                }
                else if (fieldType == appdomain.ByteType)
                {
                    res = OpCodeREnum.Stfld_U1;
                }
                else if (fieldType == appdomain.BoolType)
                {
                    res = OpCodeREnum.Stfld_I1;
                }
                else if (fieldType == appdomain.FloatType)
                {
                    res = OpCodeREnum.Stfld_R4;
                }
                else if (fieldType == appdomain.DoubleType)
                {
                    res = OpCodeREnum.Stfld_R8;
                }
                else if (fieldType == appdomain.SByteType)
                {
                    res = OpCodeREnum.Stfld_I1;
                }
                else if (fieldType == appdomain.UShortType)
                {
                    res = OpCodeREnum.Stfld_U2;
                }
                else if (fieldType == appdomain.UIntType)
                {
                    res = OpCodeREnum.Stfld_U4;
                }
                else if (fieldType == appdomain.ULongType)
                {
                    res = OpCodeREnum.Stfld_U8;
                }
                else if (fieldType == appdomain.CharType)
                {
                    res = OpCodeREnum.Stfld_U4;
                }
                else if (fieldType == appdomain.IntPtrType)
                {
                    res = OpCodeREnum.Stfld_U8;
                }
                else
                    throw new NotImplementedException(
                        $"Neo GetStfldCodeForType: IL field '{fieldType?.FullName}' has no typed Stfld opcode [neo-bare-nie]");
            }
            else
            {
                if (fieldType is ILType && fieldType.IsValueType)
                {
                    res = OpCodeREnum.Stfld_Value;
                }
                else
                    res = OpCodeREnum.Stfld_Ref;
            }
            return res;
        }
#endif

#if ENABLE_NEO_MODE
        void InitializeCallvirtDispatch(ref OpCodes.OpCodeR op, IMethod targetMethod)
        {
            int slot = -1;
            if (targetMethod is ILMethod ilMethod)
            {
                ILType declaringILType = ilMethod.DeclearingType as ILType;
                // Step 11: interface dispatch MUST be handled FIRST. An interface
                // method is an ILMethod whose declaring ILType.IsInterface is true;
                // the Step 10 IL branch below guards on !IsInterface, so without
                // this earlier branch the interface call would fall through to the
                // generic Callvirt arm and never reach the interface offset map.
                if (declaringILType != null && declaringILType.IsInterface)
                {
                    // Encode the interface-local 0-based slot of the declared
                    // method. The implementing type's interface offset map is
                    // built lazily at runtime in the handler (TryGetInterface-
                    // VTableOffset), not here -- the interface type itself has
                    // no class VTable to map into.
                    int ifaceMethodSlot = declaringILType.GetInterfaceMethodSlotSelf(ilMethod);
                    op.Code = OpCodeREnum.Callvirt_Interface;
                    op.Operand4 = EncodeCallvirtInterface(ifaceMethodSlot, 0);
                    return;
                }
                if (declaringILType != null && !declaringILType.IsInterface && declaringILType.TryGetNeoVTableSlot(ilMethod, out slot))
                {
                    op.Code = OpCodeREnum.Callvirt_IL;
                    op.Operand4 = EncodeCallvirtDispatch(slot, 0);
                }
                else
                {
                    op.Code = OpCodeREnum.Callvirt;
                    op.Operand4 = EncodeCallvirtDispatch(slot, 0);
                }
            }
            else if (targetMethod is CLRMethod clrMethod)
            {
                if (MayCallvirtTargetILObject(clrMethod))
                    op.Code = OpCodeREnum.Callvirt;
                else
                    op.Code = OpCodeREnum.Callvirt_CLR;
                op.Operand4 = EncodeCallvirtDispatch(slot, 0);
            }
        }

        static int EncodeCallvirtDispatch(int slot, int thisArgOffset)
        {
            return ((thisArgOffset & 0xffff) << 16) | (slot & 0xffff);
        }

        // Step 11: same bit layout as EncodeCallvirtDispatch; separate name so a
        // future .neo AOT format change to interface-type-index encoding is localized.
        static int EncodeCallvirtInterface(int interfaceMethodSlot, int thisArgOffset)
        {
            return ((thisArgOffset & 0xffff) << 16) | (interfaceMethodSlot & 0xffff);
        }

        static bool MayCallvirtTargetILObject(CLRMethod method)
        {
            IType declaringType = method.DeclearingType;
            if (declaringType == null)
                return true;

            if (declaringType.IsInterface)
                return true;

            Type clrType = declaringType.TypeForCLR;
            return clrType == typeof(object);
        }
#endif

        int InitializeFunctionParam(ref OpCodes.OpCodeR op, object token, out bool hasReturn, out bool canInline, out IMethod m, out ILMethod toInline, out bool isILMethod)
        {
            bool invalidToken;
            int pCnt = 0;
            m = appdomain.GetMethod(token, declaringType, method, out invalidToken);
            toInline = null;
            canInline = false;
            op.Register2 = -1;
            op.Register3 = -1;
            op.Register4 = -1;
            if (m != null)
            {
                if (invalidToken)
                    op.Operand2 = m.GetHashCode();
                else
                    op.Operand2 = token.GetHashCode();
                // neo-array-multidim-ilvt (sub-gap 1): if this is a Call/Callvirt/
                // Newobj on an IL value-type-element multi-dim array (the resolved
                // method's declaring type is the shared CLR ILTypeInstance[,...]
                // but the token's declaring type is the IL array type carrying the
                // element IL-VT), record the element ILType keyed by the token hash
                // so the Neo call arms can recover it to box/unbox the element.
                if (!invalidToken && token is MethodReference mrArr && mrArr.DeclaringType is ArrayType)
                {
                    ILType ilArrType = appdomain.GetType(mrArr.DeclaringType, declaringType, method) as ILType;
                    if (ilArrType != null && ilArrType.IsArray && ilArrType.ElementType is ILType elemIl
                        && elemIl.IsValueType && !elemIl.IsPrimitive && !elemIl.IsEnum)
                    {
                        s_neoIlVtArrayElementTypes[op.Operand2] = elemIl;
                    }
                }
                pCnt = m.ParameterCount;
                if (!m.IsStatic && op.Code != OpCodeREnum.Newobj)
                    pCnt++;
                hasReturn = m.ReturnType != appdomain.VoidType && !(m.IsConstructor && op.Code == OpCodeREnum.Call);
                if (m is ILMethod)
                {
                    isILMethod = !m.IsDelegateInvoke;
                    var ilm = (ILMethod)m;
                    bool noJIT = (ilm.JITFlags & ILRuntimeJITFlags.NoJIT) != ILRuntimeJITFlags.None;
                    bool forceInline = (ilm.JITFlags & ILRuntimeJITFlags.ForceInline) != ILRuntimeJITFlags.None;
                    bool hasExceptionHandler = ilm.Definition.HasBody && ilm.Definition.Body.HasExceptionHandlers;
                    if (!ilm.IsDelegateInvoke && !ilm.IsVirtual && !noJIT && !hasExceptionHandler && !ilm.Compiling && !ilm.IsEventAdd && !ilm.IsEventRemove)
                    {
                        var def = ilm.Definition;
                        if (!def.HasBody || forceInline)
                        {
                            canInline = true;
                            toInline = ilm;
                        }
                        else
                        {
                            bool codeSizeOK = ilm.IsRegisterBodyReady ? ilm.BodyRegister.Length <= Optimizer.MaximalInlineInstructionCount / 2 : def.Body.Instructions.Count <= Optimizer.MaximalInlineInstructionCount;
                            if(codeSizeOK)
                            {
                                canInline = true;
                                toInline = ilm;
                            }
                        }
                    }
                }
                else
                    isILMethod = false;
            }
            else
            {
                isILMethod = false;
                //Cannot find method or the method is dummy
                MethodReference _ref = (MethodReference)token;
                pCnt = _ref.HasParameters ? _ref.Parameters.Count : 0;
                if (_ref.HasThis && op.Code != OpCodeREnum.Newobj)
                    pCnt++;
                op.Operand3 = pCnt;
                hasReturn = false;
            }
            return pCnt;
        }
    }
}
