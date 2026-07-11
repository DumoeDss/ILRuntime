#if ENABLE_NEO_MODE
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using ILRuntime.CLR.TypeSystem;
using ILRuntime.CLR.Method;
using ILRuntime.Runtime.Stack;
using ILRuntime.Runtime.Intepreter.OpCodes;
using ILRuntime.Runtime.Enviorment;
using ILRuntime.CLR.Utils;
using ILRuntime.Runtime.Intepreter.RegisterVM;

#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
using AutoList = System.Collections.Generic.List<object>;
#else
using AutoList = ILRuntime.Other.UncheckedList<object>;
#endif
namespace ILRuntime.Runtime.Intepreter
{
    public unsafe partial class ILIntepreter
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadNeoInt32(byte* frameBase, ref int curPrim)
        {
            int res = *(int*)(frameBase + curPrim);
            curPrim += 4;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReadNeoUInt32(byte* frameBase, ref int curPrim)
        {
            uint res = *(uint*)(frameBase + curPrim);
            curPrim += 4;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static short ReadNeoInt16(byte* frameBase, ref int curPrim)
        {
            short res = *(short*)(frameBase + curPrim);
            curPrim += 2;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort ReadNeoUInt16(byte* frameBase, ref int curPrim)
        {
            ushort res = *(ushort*)(frameBase + curPrim);
            curPrim += 2;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte ReadNeoUInt8(byte* frameBase, ref int curPrim)
        {
            byte res = *(byte*)(frameBase + curPrim);
            curPrim += 1;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static sbyte ReadNeoInt8(byte* frameBase, ref int curPrim)
        {
            sbyte res = *(sbyte*)(frameBase + curPrim);
            curPrim += 1;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ReadNeoBoolean(byte* frameBase, ref int curPrim)
        {
            bool res = *(byte*)(frameBase + curPrim) != 0;
            curPrim += 1;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ReadNeoInt64(byte* frameBase, ref int curPrim)
        {
            long res = *(long*)(frameBase + curPrim);
            curPrim += 8;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ReadNeoUInt64(byte* frameBase, ref int curPrim)
        {
            ulong res = *(ulong*)(frameBase + curPrim);
            curPrim += 8;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ReadNeoFloat(byte* frameBase, ref int curPrim)
        {
            float res = *(float*)(frameBase + curPrim);
            curPrim += 4;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double ReadNeoDouble(byte* frameBase, ref int curPrim)
        {
            double res = *(double*)(frameBase + curPrim);
            curPrim += 8;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static char ReadNeoChar(byte* frameBase, ref int curPrim)
        {
            char res = (char)*(int*)(frameBase + curPrim);
            curPrim += 4;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static object ReadNeoReference(byte* frameBase, ref int curPrim, AutoList mStack)
        {
            int idx = *(int*)(frameBase + curPrim);
            curPrim += 4;
            // neo-f4-surfaced-gaps Gap A: the autogen Neo CLR bindings (e.g.
            // System_Type_Binding.op_Equality_1_Neo) read BOTH operands via
            // this helper. A NULL operand is the Neo null sentinel (-1); the
            // unguarded `mStack[idx]` indexed mStack[-1] -> ArgumentOutOfRangeException.
            // Apply the established null-sentinel convention (the `(idx >= 0) ?
            // mStack[idx] : null` form used at CLRMethod.Invoke's Neo arg read
            // and Ldelem_Ref's null encoding) so a null operand yields null
            // before indexing. Neo-only helper; Legacy byte-identical.
            return idx >= 0 ? mStack[idx] : null;
        }

        // ---- Step 13b (D4): CLR value-type read/write helpers ----
        // These are the by-type generalization of the ReadNeo* primitive family
        // for a CLR struct param/return slot, which is stored as FLAT BYTES in
        // the callee param region (sized by the managed value-type byte size --
        // see Optimizer.GetNeoValueTypeManagedSize). Both CLR-param readers --
        // the reflection fallback CLRMethod.Invoke(byte*) and the autogen
        // AppendArgumentCodeNeo / GetReturnValueCodeNeo delegate body -- call
        // THESE so the reader layout and the callee layout stay byte-consistent
        // BY CONSTRUCTION (single code path, single size source). For a struct
        // WITH ref fields and NO registered ValueTypeBinder there is no way to
        // map the GC references, so the caller checks for that case and throws a
        // clearly-tagged Step-13b NIE before reaching here; these helpers assume
        // a blittable-or-binder-mapped struct.
        //
        // The typed read/write is emitted once per Type via DynamicMethod (IL
        // calling Unsafe.ReadUnaligned<T>/WriteUnaligned<T>, box/unbox) and
        // cached, so the hot path is a delegate invoke with no reflection. The
        // size used to advance the cursor is Optimizer.GetNeoValueTypeManagedSize.
        static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, NeoVtReaderDelegate> s_neoVtReaders
            = new System.Collections.Concurrent.ConcurrentDictionary<Type, NeoVtReaderDelegate>();
        static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, NeoVtWriterDelegate> s_neoVtWriters
            = new System.Collections.Concurrent.ConcurrentDictionary<Type, NeoVtWriterDelegate>();

        // rasen neo-jit-bogus-opcode: the named OpCodeREnum range is implicit
        // 0..<count> (zero explicit-value members). Cached once so the ExecuteNeo
        // dispatch guard can detect a garbage/out-of-range Code with a single int
        // compare and self-maintains as Neo opcodes are appended.
        // INVARIANT: OpCodeREnum must stay a CONTIGUOUS implicit range (no explicit
        // values, no gaps) for `raw < NeoOpCodeCount` to be a valid named-range test.
        // If explicit values/gaps are ever introduced, switch this guard to a name-set
        // lookup (Enum.IsDefined / a HashSet) instead of the int-range compare.
        static readonly int NeoOpCodeCount = Enum.GetValues(typeof(OpCodeREnum)).Length;

        // Custom delegate types: pointer types cannot be generic type arguments
        // (CS0306), so Func<byte*,object>/Action<byte*,object> are illegal. These
        // custom delegates accept the frame byte pointer directly.
        unsafe delegate object NeoVtReaderDelegate(byte* src);
        unsafe delegate void NeoVtWriterDelegate(byte* dst, object value);

        static NeoVtReaderDelegate CreateNeoVtReader(Type t)
        {
            // T ReadUnaligned<T>(void* source) -- the native-pointer overload
            // (matches the byte* arg; the ref-byte overload would need a managed
            // pointer and fail IL verification here).
            MethodInfo readOpen = null;
            foreach (var m in typeof(Unsafe).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var ps = m.GetParameters();
                if (m.Name == "ReadUnaligned" && m.IsGenericMethod && ps.Length == 1
                    && ps[0].ParameterType == typeof(void*))
                { readOpen = m; break; }
            }
            var readMi = readOpen.MakeGenericMethod(t);
            var dm = new System.Reflection.Emit.DynamicMethod("NeoVtReader_" + t.FullName, typeof(object), new[] { typeof(byte*) }, restrictedSkipVisibility: true);
            var il = dm.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.EmitCall(System.Reflection.Emit.OpCodes.Call, readMi, null);
            il.Emit(System.Reflection.Emit.OpCodes.Box, t);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            return (NeoVtReaderDelegate)dm.CreateDelegate(typeof(NeoVtReaderDelegate));
        }

        static NeoVtWriterDelegate CreateNeoVtWriter(Type t)
        {
            // void WriteUnaligned<T>(void* destination, T value) -- native-pointer.
            MethodInfo writeOpen = null;
            foreach (var m in typeof(Unsafe).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var ps = m.GetParameters();
                if (m.Name == "WriteUnaligned" && m.IsGenericMethod && ps.Length == 2
                    && ps[0].ParameterType == typeof(void*))
                { writeOpen = m; break; }
            }
            var writeMi = writeOpen.MakeGenericMethod(t);
            var dm = new System.Reflection.Emit.DynamicMethod("NeoVtWriter_" + t.FullName, null, new[] { typeof(byte*), typeof(object) }, restrictedSkipVisibility: true);
            var il = dm.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
            il.Emit(System.Reflection.Emit.OpCodes.Unbox_Any, t);
            il.EmitCall(System.Reflection.Emit.OpCodes.Call, writeMi, null);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            return (NeoVtWriterDelegate)dm.CreateDelegate(typeof(NeoVtWriterDelegate));
        }

        // Read sz managed bytes at frameBase+curPrim into a BOXED object of the
        // given CLR Type, advancing curPrim by sz (sz MUST equal
        // Optimizer.GetNeoValueTypeManagedSize(clr) for the param/return slot).
        // public: the autogen CLR binding redirect delegate body calls this so the
        // generated reader and the reflection reader share one code path.
        public static unsafe object ReadNeoValueType(Type clr, byte* frameBase, ref int curPrim, int sz)
        {
            if (clr == null || sz <= 0)
            {
                curPrim += sz;
                return null;
            }
            var reader = s_neoVtReaders.GetOrAdd(clr, CreateNeoVtReader);
            object result = reader(frameBase + curPrim);
            curPrim += sz;
            return result;
        }

        // Write a boxed CLR struct's managed bytes into dst (sz bytes). The
        // inverse of ReadNeoValueType, used by the return-value path. public: the
        // autogen GetReturnValueCodeNeo delegate body calls this.
        public static unsafe void WriteNeoValueType(object value, byte* dst, int sz)
        {
            if (value == null || dst == null || sz <= 0)
                return;
            Type clr = value.GetType();
            var writer = s_neoVtWriters.GetOrAdd(clr, CreateNeoVtWriter);
            writer(dst, value);
        }

        // neo-clr-static-fields / neo-clr-static-vt-field: a CLR value-type STATIC
        // field is marshaled through the flat frame slot via the box-roundtrip
        // (ReadNeoValueType/WriteNeoValueType), which are PURE FLAT-BYTE copies
        // (Unsafe.ReadUnaligned/WriteUnaligned) that do NOT consult the registered
        // ValueTypeBinder -- there is no Neo byte* binder API (the binder only
        // exposes Legacy StackObject* marshalling). So a registered binder on the
        // field's type is IRRELEVANT to this path. The Stsfld/Ldsfld CLR-static VT
        // branches must refuse the field with a tagged NIE (NOT crash) ONLY when
        //   (a) it has reference fields -- a Neo frame VT slot stores GC refs as
        //       mStack indices, but FieldInfo.GetValue returns real GC pointers,
        //       so the per-type writer/reader would store/load managed pointers as
        //       raw bytes into the primitive region (a missed GC root / later
        //       corruption); or
        //   (b) its flat managed size overflows the register's eval-slot size
        //       (the JIT sizes an eval temp to the method's MAX VT, which may
        //       not include this CLR static struct -> an OOB write/read and
        //       AccessViolation-exits the process) -- this is the REAL AV guard.
        // A blittable binder struct that fits the slot (e.g. TestVector3.One --
        // 3 floats, 12 bytes, with a registered binder) passes. Recursive so a
        // struct with a nested ref-fielded struct is also caught.
        static bool NeoClrVtStaticFieldIsUnsafe(Type ft, int slotSize)
        {
            if (ft == null || !ft.IsValueType || ft.IsPrimitive)
                return false;
            if (NeoClrStructHasRefFields(ft))
                return true;
            if (slotSize > 0 && Optimizer.GetNeoValueTypeManagedSize(ft) > slotSize)
                return true;
            return false;
        }

        static bool NeoClrStructHasRefFields(Type t)
        {
            if (t == null || !t.IsValueType || t.IsPrimitive)
                return false;
            foreach (var fi in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var fft = fi.FieldType;
                if (!fft.IsValueType) return true;
                if (!fft.IsPrimitive && NeoClrStructHasRefFields(fft)) return true;
            }
            return false;
        }

        // Step 19: read the explicit args of an IL-delegate Invoke callvirt from
        // the callee param region (slot 0 = the adapter `this`, slots [1..] = the
        // Invoke params). Used by the Callvirt_IL delegate-invoke branch to feed
        // adapter.NeoInvokePublic. Reads each param by its declared CLR type.
        static unsafe object[] ReadNeoDelegateInvokeArgs(IMethod invokeMethod, byte* targetBase, AutoList mStack)
        {
            int pCnt = invokeMethod.ParameterCount;
            object[] args = new object[pCnt];
            // Slot 0 is the `this` (the adapter); the explicit params start after
            // it. The Invoke method's parameters are laid out contiguously. Use
            // the Invoke method's own frame ParamInfos if available (an ILMethod);
            // otherwise fall back to a 4-byte-mStack-index read per param (the
            // common by-ref/object + primitive-int mix). For correctness across
            // primitive widths, walk the parameter types.
            int cur = 4; // skip the `this` slot (4-byte mStack index)
            for (int i = 0; i < pCnt; i++)
            {
                var pt = invokeMethod.Parameters[i];
                Type clrT = pt.TypeForCLR;
                if (clrT == typeof(int)) { args[i] = *(int*)(targetBase + cur); cur += 4; }
                else if (clrT == typeof(long)) { args[i] = *(long*)(targetBase + cur); cur += 8; }
                else if (clrT == typeof(float)) { args[i] = *(float*)(targetBase + cur); cur += 4; }
                else if (clrT == typeof(double)) { args[i] = *(double*)(targetBase + cur); cur += 8; }
                else if (clrT == typeof(bool)) { args[i] = *(byte*)(targetBase + cur) != 0; cur += 1; }
                else if (clrT == typeof(byte)) { args[i] = *(byte*)(targetBase + cur); cur += 1; }
                else if (clrT == typeof(sbyte)) { args[i] = *(sbyte*)(targetBase + cur); cur += 1; }
                else if (clrT == typeof(short)) { args[i] = *(short*)(targetBase + cur); cur += 2; }
                else if (clrT == typeof(ushort)) { args[i] = *(ushort*)(targetBase + cur); cur += 2; }
                else if (clrT == typeof(uint)) { args[i] = *(uint*)(targetBase + cur); cur += 4; }
                else if (clrT == typeof(ulong)) { args[i] = *(ulong*)(targetBase + cur); cur += 8; }
                else if (clrT == typeof(char)) { args[i] = *(char*)(targetBase + cur); cur += 2; }
                else if (clrT.IsValueType && !clrT.IsPrimitive && !clrT.IsEnum)
                {
                    int sz = Optimizer.GetNeoValueTypeManagedSize(clrT);
                    args[i] = ReadNeoValueType(clrT, targetBase, ref cur, sz);
                }
                else
                {
                    // reference / enum (read as int) / byref-as-int: 4-byte mStack index.
                    int idx = *(int*)(targetBase + cur);
                    args[i] = (idx >= 0) ? mStack[idx] : null;
                    cur += 4;
                }
            }
            return args;
        }

        // Step 19: write the IL-delegate Invoke return into the caller's dest.
        unsafe void WriteNeoDelegateInvokeReturn(IMethod invokeMethod, object result, byte* retDstPtr, AutoList mStack, int retRefBase)
        {
            if (retDstPtr == null) return;
            var retType = invokeMethod.ReturnType;
            if (retType == null || retType == AppDomain.VoidType) return;
            Type clrT = retType.TypeForCLR;
            if (clrT == typeof(int)) *(int*)retDstPtr = (int)result;
            else if (clrT == typeof(long)) *(long*)retDstPtr = (long)result;
            else if (clrT == typeof(float)) *(float*)retDstPtr = (float)result;
            else if (clrT == typeof(double)) *(double*)retDstPtr = (double)result;
            else if (clrT == typeof(bool)) *(int*)retDstPtr = (bool)result ? 1 : 0;
            else if (clrT == typeof(byte)) *(int*)retDstPtr = (byte)result;
            else if (clrT == typeof(sbyte)) *(int*)retDstPtr = (sbyte)result;
            else if (clrT == typeof(short)) *(int*)retDstPtr = (short)result;
            else if (clrT == typeof(ushort)) *(int*)retDstPtr = (ushort)result;
            else if (clrT == typeof(uint)) *(int*)retDstPtr = (int)(uint)result;
            else if (clrT == typeof(ulong)) *(long*)retDstPtr = (long)(ulong)result;
            else if (clrT == typeof(char)) *(int*)retDstPtr = (int)(char)result;
            else if (clrT.IsValueType && !clrT.IsPrimitive && !clrT.IsEnum)
            {
                int sz = Optimizer.GetNeoValueTypeManagedSize(clrT);
                WriteNeoValueType(result, retDstPtr, sz);
            }
            else
            {
                if (retRefBase >= mStack.Count) mStack.Add(result);
                else mStack[retRefBase] = result;
                *(int*)retDstPtr = retRefBase;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void CopyNeoCallArguments(ref NeoCallParamMap map, byte* frameBase, byte* targetBase, AutoList mStack, ILRuntime.Runtime.Enviorment.AppDomain appdomain)
        {
            if (map.PrimitiveSize == null)
                return;
            bool[] byRefSrc = map.PrimitiveByRefSrc;
            System.Type[] byRefElemType = map.PrimitiveByRefElemType;
            for (int i = 0; i < map.PrimitiveSize.Length; i++)
            {
                // Step 13 Area 4b / 4c: a byref source slot holds an 8-byte Ref Slot
                // (objectIndex, offset). The dest slot is sized by the REFERENT type
                // (a struct `this` for 4b; the byref param's element type for 4c), so
                // DEREFERENCE the byref and copy the referent bytes into the dest
                // instead of copying the 8 byref bytes verbatim. The byref may be:
                //  - frame-native (objIdx == -1): offset is an absolute frame byte
                //    offset; copy PrimitiveSize[i] bytes from frameBase + offset.
                //  - mStack-object field (objIdx >= 0): a `ref obj.field` shape; read
                //    the field via the field accessor (CLR object -> GetFieldValue by
                //    hash; ILTypeInstance -> Primitives[off]) and flatten into dest.
                if (byRefSrc != null && i < byRefSrc.Length && byRefSrc[i])
                {
                    int objIdx = *(int*)(frameBase + map.PrimitiveSrc[i]);
                    int offset = *(int*)(frameBase + map.PrimitiveSrc[i] + 4);
                    if (objIdx == -1)
                    {
                        // frame-native byref: offset is an absolute frame byte offset.
                        Unsafe.CopyBlock(targetBase + map.PrimitiveDst[i], frameBase + offset, map.PrimitiveSize[i]);
                    }
                    else if (objIdx >= 0 && objIdx < mStack.Count)
                    {
                        // 4c mStack-object field deref. `offset` is the field hash (CLR
                        // object) or the Primitives byte offset (ILTypeInstance). Read
                        // the field as a boxed object and flatten into the dest slot.
                        System.Type elemType = (byRefElemType != null && i < byRefElemType.Length) ? byRefElemType[i] : null;
                        NeoMarshalByrefFieldToSlot(appdomain, mStack, objIdx, offset, elemType, targetBase + map.PrimitiveDst[i], map.PrimitiveSize[i], isWrite: false);
                    }
                    else
                    {
                        // Step 20 fixer round 1: the byref source held a stale /
                        // out-of-range objIdx (the producing ldloca/ldflda was
                        // folded by addrAlias, leaving stale frame bytes). Zero the
                        // dest rather than crash -- the consumer (a CLR-method
                        // redirect) treats a zeroed struct as `default` and the
                        // sync-async path fails the assertion cleanly instead of
                        // throwing IndexOutOfRangeException. (A genuine byref
                        // always has objIdx == -1 or a valid mStack index.)
                        Unsafe.InitBlock(targetBase + map.PrimitiveDst[i], 0, map.PrimitiveSize[i]);
                    }
                }
                else
                {
                    Unsafe.CopyBlock(targetBase + map.PrimitiveDst[i], frameBase + map.PrimitiveSrc[i], map.PrimitiveSize[i]);
                }
            }
        }

        // Step 13 Area 4c: the shared byref-field marshal (D3 unification). Reads
        // OR writes a referent through a Ref Slot (objIdx, off) where the referent
        // is a FIELD of an mStack object. The object is a CLR object (off = field
        // hash) or an ILTypeInstance (off = Primitives byte offset). For a CLR
        // object: read via GetFieldValue / write via SetFieldValue, flatten/re-box
        // per the element type. For an ILTypeInstance: read/write Primitives bytes
        // directly (the field's flat-bytes region).
        static unsafe void NeoMarshalByrefFieldToSlot(ILRuntime.Runtime.Enviorment.AppDomain appdomain, AutoList mStack, int objIdx, int off, System.Type elemType, byte* slot, int sz, bool isWrite)
        {
            object target = mStack[objIdx];
            if (target is ILTypeInstance ili)
            {
                // F-10: a byref produced by `ldflda &instance.<clrStructField>` carries
                // (objIdx, ReferenceOffset | NeoF10ByrefOffsetFlag). The field's storage
                // is the BOXED CLR struct at ManagedObjects[ReferenceOffset], NOT a
                // Primitives byte region. Marshal between the boxed struct and the dest
                // slot via ReadNeoValueType / WriteNeoValueType (the Step-13b/area4
                // boxed-ref-vs-flat-bytes bridge).
                if ((off & JITCompiler.NeoF10ByrefOffsetFlag) != 0)
                {
                    int refOff = off & ~JITCompiler.NeoF10ByrefOffsetFlag;
                    if (isWrite)
                    {
                        // Box the slot's flat bytes into the field's element type and
                        // store at ManagedObjects[refOff]. The elemType comes from the
                        // call signature; when the caller did not propagate it (the
                        // Step-20 builder-byref write-back path), recover the type
                        // from the boxed struct already at ManagedObjects[refOff] (a
                        // write-back updates an existing boxed struct).
                        Type boxType = elemType;
                        if (boxType == null)
                        {
                            object existing = ili.ManagedObjects[refOff];
                            if (existing != null)
                                boxType = existing.GetType();
                        }
                        if (boxType != null)
                        {
                            int cur = 0;
                            object boxed = ILIntepreter.ReadNeoValueType(boxType, slot, ref cur, sz);
                            ili.ManagedObjects[refOff] = boxed;
                        }
                        else
                            throw new NotImplementedException("neo-clrstruct-field-of-il: NeoMarshalByrefFieldToSlot write with a null elemType and no existing boxed struct to recover the type from (cannot box the CLR struct)");
                    }
                    else
                    {
                        object boxed = ili.ManagedObjects[refOff];
                        if (boxed != null)
                            ILIntepreter.WriteNeoValueType(boxed, slot, sz);
                        else
                            Unsafe.InitBlock(slot, 0, (uint)sz);
                    }
                    return;
                }
                // IL heap field: flat-bytes region at Primitives[off].
                if (isWrite)
                    Unsafe.CopyBlock(ref ili.Primitives[off], ref *slot, (uint)sz);
                else
                    Unsafe.CopyBlock(ref *slot, ref ili.Primitives[off], (uint)sz);
                return;
            }
            if (target is Array)
            {
                // An array-element byref reaches here only via ldelema + a byref-
                // param call -- the ldelema path encodes (arrIdx, elemByteOff) and
                // the element bytes are in the CLR array backing store. Reading/
                // writing a CLR-array element through this field-marshal is not
                // supported (the array case is owned by the stind/ldind consumer).
                throw new NotImplementedException(
                    "Step 13 Area 4c: a CLR-array-element byref param is not handled (route via the array stind/ldind path, not the field accessor)");
            }
            // CLR object field: route via the field-hash accessor.
            if (isWrite)
            {
                // Flatten the dest slot bytes into a boxed element and write it.
                object value;
                if (elemType != null && (elemType.IsPrimitive || elemType.IsEnum))
                {
                    int cur = 0;
                    value = ILIntepreter.ReadNeoValueType(elemType, slot, ref cur, sz);
                }
                else if (elemType != null && elemType.IsValueType)
                {
                    int cur = 0;
                    value = ILIntepreter.ReadNeoValueType(elemType, slot, ref cur, sz);
                }
                else
                {
                    // reference-type field: the slot holds an mStack index.
                    int vIdx = *(int*)slot;
                    value = vIdx >= 0 ? mStack[vIdx] : null;
                }
                NeoWriteClrObjectField(appdomain, target, off, value);
            }
            else
            {
                object fieldValue = NeoReadClrObjectField(appdomain, target, off);
                if (elemType != null && (elemType.IsPrimitive || elemType.IsEnum))
                {
                    // Flatten the boxed primitive/enum into the slot.
                    if (fieldValue != null)
                        ILIntepreter.WriteNeoValueType(fieldValue, slot, sz);
                    else
                        Unsafe.InitBlock(slot, 0, (uint)sz);
                }
                else if (elemType != null && elemType.IsValueType)
                {
                    if (fieldValue != null)
                        ILIntepreter.WriteNeoValueType(fieldValue, slot, sz);
                    else
                        Unsafe.InitBlock(slot, 0, (uint)sz);
                }
                else
                {
                    // reference-type field: store the object's mStack index. The
                    // field value is either already on mStack or must be parked.
                    if (fieldValue == null)
                    {
                        *(int*)slot = -1;
                    }
                    else
                    {
                        int newIdx = mStack.Count;
                        mStack.Add(fieldValue);
                        *(int*)slot = newIdx;
                    }
                }
            }
        }

        // Step 13 Area 4b / 4c: post-call reverse copy (write-back). After a CLR
        // call, for each byref slot flagged for write-back (a `ref`/`out` param or
        // a mutating VT `this`), write the (possibly-mutated) dest slot bytes BACK
        // through the source byref -- the inverse of CopyNeoCallArguments. The
        // reflection fallback's struct mutation lands in the callee param region
        // (the boxed-struct in-place mutation), so this propagates it to the
        // caller's local/field. The autogen path's struct `this` does NOT write
        // into the param region (documented 4a limitation); a `ref`/`out` param's
        // write-back IS observed because the redirect reads the param into a local,
        // calls with `ref`/`out`, and the local's final value is what the reader
        // wrote... NOTE: the autogen wrapper reads the param ONCE (prologue) and
        // does NOT re-flatten after the call, so the autogen write-back for a
        // byref param is owned by the wrapper's own epilogue (the 4c autogen
        // write-back). This helper covers the reflection fallback's callee-region
        // mutation for BOTH the `this` slot and the byref-param slots.
        // NOTE (F-5 / Step 17): this covers MUTATING INSTANCE METHODS / ref-out
        // PARAMS, NOT constructors -- the newobj path (VT-THIS-ADDR) performs its
        // own slot-0 -> caller-dest copy-back in ExecuteNeo's Ret arm.
        static void CopyNeoCallThisBack(ref NeoCallParamMap map, byte* frameBase, byte* targetBase, AutoList mStack, ILRuntime.Runtime.Enviorment.AppDomain appdomain)
        {
            CopyNeoCallThisBack(ref map, frameBase, targetBase, mStack, appdomain, null);
        }

        // Step 20 fixer round 1 (TaskAwaiter round-trip): the write-back reads each
        // flagged byref source's Ref Slot (objIdx, off) AFTER the call. But the
        // C# compiler reuses the byref source register as the CALL DEST for a
        // non-mutating VT-this instance method (e.g. `call r6, r6, get_IsCompleted`
        // -- r6 holds the ldloca byref AND receives the bool result), so the call
        // OVERWRITES the byref bytes before the write-back reads them -> the
        // write-back then interprets the result int as an mStack objIdx
        // (ArgumentOutOfRangeException) or a wrong frame offset. The fix: capture
        // every flagged byref source's (objIdx, off) into a snapshot BEFORE the
        // call, then read the snapshot here. The snapshot is a flat (objIdx,off)
        // pair array (8 bytes per flagged slot), passed by the Call/Callvirt site
        // (null = legacy re-read behavior, for callers that did not snapshot).
        static void CopyNeoCallThisBack(ref NeoCallParamMap map, byte* frameBase, byte* targetBase, AutoList mStack, ILRuntime.Runtime.Enviorment.AppDomain appdomain, int* byRefSnapshot)
        {
            if (map.PrimitiveSize == null || map.PrimitiveByRefSrc == null)
                return;

            bool[] byRefSrc = map.PrimitiveByRefSrc;
            bool[] writeBack = map.PrimitiveByRefWriteBack;
            System.Type[] byRefElemType = map.PrimitiveByRefElemType;
            int snapIdx = 0;
            for (int i = 0; i < byRefSrc.Length; i++)
            {
                if (!byRefSrc[i])
                    continue;
                // 4c: gate the write-back on the per-slot flag (ref/out, not in-only).
                // 4b VT `this` is always flagged for write-back.
                if (writeBack != null && i < writeBack.Length && !writeBack[i])
                    continue;
                if (i >= map.PrimitiveSrc.Length)
                    continue;
                int objIdx, offset;
                if (byRefSnapshot != null)
                {
                    objIdx = byRefSnapshot[snapIdx * 2];
                    offset = byRefSnapshot[snapIdx * 2 + 1];
                    snapIdx++;
                }
                else
                {
                    objIdx = *(int*)(frameBase + map.PrimitiveSrc[i]);
                    offset = *(int*)(frameBase + map.PrimitiveSrc[i] + 4);
                }
                if (objIdx == -1)
                {
                    // frame-native byref: write the (possibly-mutated) slot bytes
                    // back to the caller's in-frame local.
                    Unsafe.CopyBlock(frameBase + offset, targetBase + map.PrimitiveDst[i], map.PrimitiveSize[i]);
                }
                else if (objIdx >= 0 && objIdx < mStack.Count)
                {
                    // 4c mStack-object field: write back through the field accessor.
                    System.Type elemType = (byRefElemType != null && i < byRefElemType.Length) ? byRefElemType[i] : null;
                    NeoMarshalByrefFieldToSlot(appdomain, mStack, objIdx, offset, elemType, targetBase + map.PrimitiveDst[i], map.PrimitiveSize[i], isWrite: true);
                }
                // else: the snapshot/byref source held a stale or out-of-range
                // objIdx (the ldloca that produced this byref was folded by
                // addrAlias, so the dest temp holds stale frame bytes). Skip the
                // write-back -- it is only meaningful for a genuinely-mutating
                // instance method, whose byref would be a real ldloca product
                // (objIdx == -1 or a valid mStack index). A non-mutating VT-this
                // call (get_IsCompleted / GetResult on a TaskAwaiter) is a no-op
                // here, so skipping is semantically correct.
            }
        }

        // Capture the (objIdx, off) of every write-back-flagged byref source slot
        // BEFORE a call, into a caller-provided snapshot buffer (8 bytes per slot,
        // indexed in write-back iteration order). The snapshot is read by the
        // snapshot-aware CopyNeoCallThisBack overload so a byref source register
        // that the call reused as its dest (clobbering the byref bytes) is still
        // write-back-able. Returns the number of captured slots.
        static int SnapshotNeoCallByRefSources(ref NeoCallParamMap map, byte* frameBase, int* snapshot)
        {
            if (map.PrimitiveSize == null || map.PrimitiveByRefSrc == null)
                return 0;
            bool[] byRefSrc = map.PrimitiveByRefSrc;
            bool[] writeBack = map.PrimitiveByRefWriteBack;
            int n = 0;
            for (int i = 0; i < byRefSrc.Length; i++)
            {
                if (!byRefSrc[i])
                    continue;
                if (writeBack != null && i < writeBack.Length && !writeBack[i])
                    continue;
                if (i >= map.PrimitiveSrc.Length)
                    continue;
                snapshot[n * 2] = *(int*)(frameBase + map.PrimitiveSrc[i]);
                snapshot[n * 2 + 1] = *(int*)(frameBase + map.PrimitiveSrc[i] + 4);
                n++;
            }
            return n;
        }

        bool InvokeNeoCallTarget(IMethod targetMethod, bool isNewobj, byte* targetBase, AutoList mStack, byte* retDstPtr, int targetRetRefBase, out bool unhandledException)
        {
            unhandledException = false;
            if (targetMethod is ILMethod ilm)
            {
                ExecuteNeo(ilm, targetBase, retDstPtr, targetRetRefBase, out unhandledException);
                return !unhandledException;
            }
            else if (targetMethod is CLRMethod clrMethod)
            {
                InvokeNeoClrMethod(clrMethod, isNewobj, targetBase, mStack, retDstPtr, targetRetRefBase);
                return true;
            }

            throw new NotImplementedException("Unknown method type in Neo mode.");
        }

        // F-7B-SIB (IL-direct-Call byref ABI): the runtime mirror of
        // NeoRunDelegateTargetOnThis's two byref channels, specialized for a
        // NON-inlined direct `Call` to an IL method. A direct Call's targetBase
        // IS the callee frame base (no delegate-adapter `this` shift; params sit
        // where the callee layout placed them). The byref params were copied
        // VERBATIM into targetBase (an IL callee's byref is unflagged in the
        // NeoCallParamMap, so CopyNeoCallArguments copies the 8-byte Ref Slot
        // raw -- the delegate path's identical premise). A frame-native byref's
        // offset is CALLER-frame-relative; the callee's stind/ldind resolve
        // objectIndex==-1 against the CALLEE's (higher) frame base, so the raw
        // offset addresses the wrong cell. Two channels (mirroring the delegate
        // path, ILIntepreter.Neo.cs:686-807):
        //
        //  * F-7 primitive/value-byref: re-base the offset by the frame distance
        //    so the callee's deref lands in the caller cell. The write-back is
        //    flat BYTES (no mStack index) -> survives the callee Ret pop. Undone
        //    after the run so a re-read of targetBase (e.g. the snapshot write-
        //    back) sees the original caller-relative offset.
        //
        //  * F-7B reference-byref (`ref string` / `ref <class>`): PROMOTE the
        //    referent into a CALLER-owned mStack slot (reserved here, BEFORE the
        //    callee reserves its frameRefBase, so the slot sits BELOW the callee's
        //    region and survives the callee Ret pop) and rewrite the byref to the
        //    mStack-object shape `(callerSlot, NeoF10ByrefOffsetFlag)`. The
        //    Stind_Ref/Ldind_Ref caller-owned-slot arms (ILIntepreter.Neo.cs:4021,
        //    :4067) read+write through that stable slot -- the same lifetime
        //    guarantee the single-reference RETURN promotion provides. After the
        //    run, the caller cell is stamped to the caller-owned slot index.
        //
        // A direct Call has a SINGLE invocation (no multicast chain), so the
        // caller-owned slot is a local (not cross-invocation shared), and the
        // rebase/rewrite undo is once-per-call. The bookkeeping for the post-run
        // stamp + undo is returned via the out params; the caller performs them
        // after InvokeNeoCallTarget returns. Returns true if at least one byref
        // param was touched (so the caller knows to run the post-pass); false
        // (with all out params inert) for a byref-less call -- a no-op fast path.
        unsafe bool NeoPreCallByrefFixup(ILMethod target, byte* targetBase, byte* callerFrameBase,
            AutoList mStack, out int promotedSlotOff, out int promotedOrigObjIdx, out int promotedOrigOff,
            out int promotedCallerSlot, out int rebasedSlotOff, out int rebasedOrigOff)
        {
            promotedSlotOff = -1;
            promotedOrigObjIdx = -1;
            promotedOrigOff = 0;
            promotedCallerSlot = -1;
            rebasedSlotOff = -1;
            rebasedOrigOff = 0;

            var tParams = target.Parameters;
            var tParamInfos = target.CompiledFrame.ParamInfos;
            if (tParams == null || tParamInfos == null)
                return false;

            long frameDist = targetBase - callerFrameBase; // target base is ABOVE caller
            int firstParam = target.HasThis ? 1 : 0;
            int nParams = tParams.Count;
            bool touched = false;
            for (int p = 0; p < nParams; p++)
            {
                int slotIdx = firstParam + p;
                if (slotIdx >= tParamInfos.Length) break;
                var pt = tParams[p];
                if (pt == null || !pt.IsByRef) continue;
                var slot = tParamInfos[slotIdx];
                if (slot.Size != 8) continue; // byref Ref Slot is 8 bytes
                int objIdx = *(int*)(targetBase + slot.Offset + 0);
                if (objIdx != -1) continue; // mStack-object byref: absolute, no rebase
                int origOff = *(int*)(targetBase + slot.Offset + 4);

                // F-7B (D2): gate the promotion on a REFERENCE-typed referent
                // (mirrors NeoRunDelegateTargetOnThis:739-740). The byref's
                // ElementType is the de-byref'd referent type. Primitive/value
                // byrefs keep the F-7 byte-relativization path.
                IType elemType = pt.ElementType;
                bool isRefByref = elemType != null && !elemType.IsPrimitive && !elemType.IsValueType;
                if (isRefByref)
                {
                    // Reserve the caller-owned mStack slot. It sits at the CURRENT
                    // mStack.Count, BEFORE InvokeNeoCallTarget -> the callee's
                    // ExecuteNeo reserves its frameRefBase ABOVE it, so the slot
                    // survives the callee Ret pop. Seed it with the byref's CURRENT
                    // referent (the caller cell at callerFrameBase+origOff holds the
                    // referent's mStack index) so the READ path observes the entry
                    // value.
                    promotedCallerSlot = mStack.Count;
                    mStack.Add(null);
                    int callerSrcIdx = *(int*)(callerFrameBase + origOff);
                    mStack[promotedCallerSlot] = callerSrcIdx >= 0 ? mStack[callerSrcIdx] : null;

                    // Rewrite the byref to the caller-owned mStack-object shape:
                    // objectIndex = promotedCallerSlot, offset = NeoF10ByrefOffsetFlag
                    // (the high-bit discriminator the Stind_Ref/Ldind_Ref arms
                    // dispatch on). Disjoint from real field hashes / array indices
                    // / Primitives offsets.
                    *(int*)(targetBase + slot.Offset + 0) = promotedCallerSlot;
                    *(int*)(targetBase + slot.Offset + 4) = JITCompiler.NeoF10ByrefOffsetFlag;
                    promotedSlotOff = slot.Offset;
                    promotedOrigObjIdx = objIdx; // always -1 (frame-native)
                    promotedOrigOff = origOff;
                }
                else
                {
                    // F-7 primitive/value-byref relativization (UNCHANGED from
                    // the delegate path). The callee's deref at the rebased offset
                    // resolves to callerFrameBase + origOff (the caller cell); the
                    // mutation lands in-frame and survives the callee pop as flat
                    // bytes.
                    *(int*)(targetBase + slot.Offset + 4) = origOff - (int)frameDist;
                    rebasedOrigOff = origOff;
                    rebasedSlotOff = slot.Offset;
                }
                touched = true;
                // One byref param per direct Call is the common case (and the only
                // case the delegate-Invoke sibling and all current tests exercise);
                // the first byref suffices. A multi-byref direct call
                // (`Foo(ref int a, ref int b)`) would need per-slot bookkeeping --
                // recorded as a sequencing note (the delegate path has the same
                // one-byref limitation).
                break;
            }
            return touched;
        }

        // F-7 (NEO-DELEGATE-REFOUT): run ONE IL delegate target on THIS interpreter
        // (same-frame fast path) with its bound `instance`, returning false on an
        // unhandled target exception. The delegate-Invoke callvirt marshaled the
        // explicit params into targetBase per the INVOKE method's layout, whose
        // slot 0 is the adapter `this` (4-byte mStack index) and whose explicit
        // params follow at offset 4+. An INSTANCE target's layout coincides
        // (this@0, params@4+), so targetBase is used directly; a STATIC target has
        // no `this`, so shift the base forward by the Invoke `this` slot (4) so the
        // target reads its params where the Invoke layout placed them.
        //
        // BYREF RELATIVIZATION: a byref param's 8-byte Ref Slot (objectIndex, off)
        // was copied VERBATIM into targetBase (an IL callee's byref is unflagged,
        // so CopyNeoCallArguments copies the 8 bytes raw). The frame-native case
        // (objectIndex == -1) carries an offset RELATIVE TO THE CALLER's frame.
        // The target's stind/ldind resolve objectIndex==-1 against the TARGET's
        // frame base (which sits ABOVE the caller's frame), so the raw offset would
        // address the wrong cell. Re-base the offset by the frame distance
        // (callerFrameBase -> targetFrameBase) so it resolves back to the caller's
        // cell: the target's mutation then lands in the caller's frame and the
        // write-back is live. (mStack-object byrefs -- objectIndex >= 0 -- address
        // mStack absolutely and need no rebase.) NOTE: the Neo Step-17 IL-byref
        // tests pass because the optimizer INLINES those small targets, so the
        // byref never actually crosses a frame; a delegate-Invoke target CANNOT be
        // inlined (indirect call), making this the first real cross-frame IL byref
        // -- hence the relativization is required here even though Call_IL does
        // not need it for inlined targets.
        unsafe bool NeoRunDelegateTargetOnThis(ILMethod target, ILTypeInstance instance,
            byte* targetBase, int thisSlotShift, byte* callerFrameBase,
            AutoList mStack, byte* retDstPtr, int targetRetRefBase, ref int callerOwnedRefSlot,
            out bool unhandledException)
        {
            byte* dTargetBase = targetBase + thisSlotShift;

            // D2: for an instance target, overwrite the adapter mStack slot (stored
            // at targetBase+0 by the Invoke map) with the bound `instance`, so the
            // target's `this` is the bound object (mirrors NeoInvokeSub's
            // WriteNeoCallSlot(paramInfos[0], ..., instance)). No-op for static.
            if (target.HasThis && instance != null)
            {
                int thisIdx = *(int*)targetBase;
                if (thisIdx >= 0 && thisIdx < mStack.Count)
                    mStack[thisIdx] = instance;
            }

            // F-7 / F-7B: marshal a frame-native byref param so the target's
            // stind/ldind land back in the caller's frame. Two channels:
            //
            //  * F-7 primitive/value-byref (`!IsByRefOfReference`): re-base the
            //    byref's OFFSET by the frame distance so objectIndex==-1 resolution
            //    against the target's (higher) frame base yields the caller's cell.
            //    The write-back is flat BYTES (no mStack index) -> survives the
            //    callee pop byte-for-byte. Undone after the run for multicast.
            //
            //  * F-7B reference-byref (`IsByRefOfReference`, e.g. `ref string`):
            //    the referent is an mStack OBJECT. The relativization alone is a
            //    DANGLING-INDEX trap: the callee's stind.ref would write the NEW
            //    object's CALLEE-frame mStack index into the caller cell, and the
            //    callee's Ret pop (`mStack.RemoveRange(frameRefBase, ...)`) would
            //    then delete that index. PROMOTE the referent into a CALLER-OWNED
            //    mStack slot (reserved here, BEFORE the callee reserves, so it sits
            //    BELOW the callee's frameRefBase and survives the pop) and rewrite
            //    the byref to an mStack-object shape `(callerSlot, off|flag)`. The
            //    `Stind_Ref`/`Ldind_Ref` caller-owned-slot arms then read+write the
            //    object THROUGH that stable slot -- the same lifetime guarantee the
            //    single-reference RETURN promotion (`Ret` arm) already provides.
            //    The caller-owned slot is reserved ONCE per delegate-Invoke (shared
            //    across the multicast chain via `callerOwnedRefSlot`, last write
            //    wins -- D3), and the byref rewrite is restored after each run so a
            //    multicast re-invocation sees a stable, non-compounding byref.
            var tParams = target.Parameters;
            var tParamInfos = target.CompiledFrame.ParamInfos;
            int rebasedSlotOff = -1;   // at most one byref param per delegate signature
            int rebasedOrigOff = 0;
            int promotedSlotOff = -1;  // F-7B: the reference-byref slot rewritten to mStack-object form
            int promotedOrigObjIdx = -1;
            int promotedOrigOff = 0;
            if (tParams != null && tParamInfos != null)
            {
                long frameDist = dTargetBase - callerFrameBase; // target base is ABOVE caller
                int firstParam = target.HasThis ? 1 : 0;
                int nParams = tParams.Count;
                for (int p = 0; p < nParams; p++)
                {
                    int slotIdx = firstParam + p;
                    if (slotIdx >= tParamInfos.Length) break;
                    var pt = tParams[p];
                    if (pt == null || !pt.IsByRef) continue;
                    var slot = tParamInfos[slotIdx];
                    if (slot.Size != 8) continue; // byref Ref Slot is 8 bytes
                    int objIdx = *(int*)(dTargetBase + slot.Offset + 0);
                    if (objIdx != -1) continue; // mStack-object byref: absolute, no rebase
                    int origOff = *(int*)(dTargetBase + slot.Offset + 4);

                    // F-7B (D2): gate the promotion on a REFERENCE-typed referent.
                    // The byref's ElementType is the de-byref'd referent type
                    // (ILType sets byRefType.elementType = this at construction).
                    // Primitive/value byrefs keep the F-7 byte-relativization path.
                    IType elemType = pt.ElementType;
                    bool isRefByref = elemType != null && !elemType.IsPrimitive && !elemType.IsValueType;
                    if (isRefByref)
                    {
                        // Reserve the caller-owned mStack slot ONCE (shared across the
                        // multicast chain). Initialized with the byref's CURRENT
                        // referent: the caller cell at callerFrameBase+origOff holds
                        // the referent's mStack index; copy that object into the
                        // caller-owned slot so the READ path observes the entry value.
                        if (callerOwnedRefSlot < 0)
                        {
                            callerOwnedRefSlot = mStack.Count;
                            mStack.Add(null);
                        }
                        int callerSrcIdx = *(int*)(callerFrameBase + origOff);
                        mStack[callerOwnedRefSlot] = callerSrcIdx >= 0 ? mStack[callerSrcIdx] : null;

                        // Rewrite the byref to the caller-owned mStack-object shape:
                        // objectIndex = callerOwnedRefSlot (the stable slot the callee
                        // pop does NOT touch), offset = NeoF10ByrefOffsetFlag (the
                        // high-bit discriminator marking the caller-owned-slot shape;
                        // disjoint from real field hashes / array indices / Primitives
                        // offsets). The Stind_Ref/Ldind_Ref caller-owned-slot arms
                        // dispatch on this flag.
                        *(int*)(dTargetBase + slot.Offset + 0) = callerOwnedRefSlot;
                        *(int*)(dTargetBase + slot.Offset + 4) = JITCompiler.NeoF10ByrefOffsetFlag;
                        promotedSlotOff = slot.Offset;
                        promotedOrigObjIdx = objIdx;   // always -1 (frame-native)
                        promotedOrigOff = origOff;
                    }
                    else
                    {
                        // F-7 primitive/value-byref relativization (UNCHANGED).
                        *(int*)(dTargetBase + slot.Offset + 4) = origOff - (int)frameDist;
                        rebasedOrigOff = origOff;
                        rebasedSlotOff = slot.Offset;
                    }
                    break; // a delegate signature has at most one byref per slot; first suffices
                }
            }

            bool ok = InvokeNeoCallTarget(target, false, dTargetBase, mStack, retDstPtr, targetRetRefBase, out unhandledException);

            // F-7B (D1): after a reference-byref target runs, the caller cell MUST
            // reflect the LAST write. The caller-owned slot holds the surviving
            // object; stamp the caller-owned slot index into the caller cell so the
            // caller's subsequent `s` read resolves to it (the cell now points at a
            // caller-region slot, surviving the callee pop -- parity with the
            // single-reference RETURN promotion). For multicast, this also feeds
            // the NEXT target's read (the byref is restored below, then the next
            // run re-promotes and reads the caller cell, which still points at this
            // slot -- last object wins, D3).
            if (promotedSlotOff >= 0)
            {
                *(int*)(callerFrameBase + promotedOrigOff) = callerOwnedRefSlot;
                // Restore the raw frame-native byref in targetBase for a multicast
                // re-invocation of the same targetBase (idempotent rewrite, D3/2.6).
                *(int*)(dTargetBase + promotedSlotOff + 0) = promotedOrigObjIdx;
                *(int*)(dTargetBase + promotedSlotOff + 4) = promotedOrigOff;
            }

            // Undo the primitive/value-byref rebase so the raw caller-relative
            // offset is restored for any re-invocation (multicast) of the same
            // targetBase.
            if (rebasedSlotOff >= 0)
                *(int*)(dTargetBase + rebasedSlotOff + 4) = rebasedOrigOff;

            return ok;
        }

        void InvokeNeoClrMethod(CLRMethod clrMethod, bool isNewobj, byte* targetBase, AutoList mStack, byte* retDstPtr, int targetRetRefBase)
        {
            var redirectNeo = clrMethod.RedirectionNeo;
            if (redirectNeo != null)
            {
                // The Neo Redirection owns the dest write for both call and
                // newobj (the redirect delegate allocates/stores the result).
                redirectNeo(this, targetBase, mStack, clrMethod, isNewobj, retDstPtr, targetRetRefBase);
                return;
            }

            object res = clrMethod.Invoke(targetBase, mStack, isNewobj);

            // Step 18 (D3): for a reflection-constructed newobj the returned
            // object MUST be stored into the caller's dest (the early-return
            // previously skipped this, so the dest was never filled). The
            // redirect path already returned above (it owns its dest write).
            // A non-newobj void/no-return also early-returns.
            if (retDstPtr == null)
                return;
            if (isNewobj)
            {
                // The constructed object is a reference type; store it into the
                // dest mStack ref slot and write the index to the dest byte
                // offset (mirrors the reference-type return store below).
                if (targetRetRefBase >= mStack.Count)
                    mStack.Add(res);
                else
                    mStack[targetRetRefBase] = res;
                *(int*)retDstPtr = targetRetRefBase;
                return;
            }

            IType retType = clrMethod.ReturnType;
            if (retType == null || retType == AppDomain.VoidType)
                return;

            if (retType.TypeForCLR.IsPrimitive || retType.TypeForCLR.IsEnum)
            {
                if (retType.TypeForCLR == typeof(int) || retType.TypeForCLR.IsEnum) *(int*)retDstPtr = (int)res;
                else if (retType.TypeForCLR == typeof(long)) *(long*)retDstPtr = (long)res;
                else if (retType.TypeForCLR == typeof(float)) *(float*)retDstPtr = (float)res;
                else if (retType.TypeForCLR == typeof(double)) *(double*)retDstPtr = (double)res;
                else if (retType.TypeForCLR == typeof(bool)) *(int*)retDstPtr = (bool)res ? 1 : 0;
                else if (retType.TypeForCLR == typeof(byte)) *(int*)retDstPtr = (byte)res;
                else if (retType.TypeForCLR == typeof(sbyte)) *(int*)retDstPtr = (sbyte)res;
                else if (retType.TypeForCLR == typeof(short)) *(int*)retDstPtr = (short)res;
                else if (retType.TypeForCLR == typeof(ushort)) *(int*)retDstPtr = (ushort)res;
                else if (retType.TypeForCLR == typeof(uint)) *(uint*)retDstPtr = (uint)res;
                else if (retType.TypeForCLR == typeof(ulong)) *(ulong*)retDstPtr = (ulong)res;
                else if (retType.TypeForCLR == typeof(char)) *(int*)retDstPtr = (char)res;
            }
            else if (retType.IsValueType)
            {
                // Step 13b (D6): write a CLR struct return value's flat managed
                // bytes into the caller's dest frame slot (already sized by
                // AllocateLocalStackSpaces for the value type) via WriteNeoValueType.
                // The inverse of the param read path (D2/D4) -- same size source
                // (Optimizer.GetNeoValueTypeManagedSize), so the write matches the
                // caller's dest layout by construction. A struct with reference
                // fields and no binder is unreadable on the way IN and unwriteable
                // on the way OUT the same way; for the reflection fallback (no
                // binder ref-mapping) we only support pure-primitive / zero-managed-
                // count binder structs. The boxed CLR return `res` carries the GC
                // refs for a binder struct when the binder populated them upstream
                // (def not here); this path writes the flat primitive bytes.
                int retSz = Optimizer.GetNeoValueTypeManagedSize(retType.TypeForCLR);
                WriteNeoValueType(res, retDstPtr, retSz);
            }
            else
            {
                // neo-array-multidim Gap 1: a NULL reference return MUST be encoded
                // as the Neo null sentinel (-1) in the dest, NOT as targetRetRefBase
                // (a valid mStack index). The index-based null test (C# `x != null`
                // lowers to `ldnull; cgt.un`) only inspects the dest's 4-byte index
                // (Cgt_Un at ILIntepreter.Neo.cs:1305-1308 keys on `cguA != -1`); a
                // valid index reads as "not null" even when the result is null, so
                // `a[i,j] != null` fires a false positive. This mirrors Ldnull
                // (`*(int*)dst = -1`) and the convention at CLRMethod.Invoke
                // (`idx < 0 ? null : mStack[idx]`) / the autogen Ldelem_Ref null
                // encoding. A non-null result keeps the targetRetRefBase store
                // (unchanged for the primitive/metadata paths).
                if (res == null)
                {
                    *(int*)retDstPtr = -1;
                }
                else
                {
                    if (targetRetRefBase >= mStack.Count)
                        mStack.Add(res);
                    else
                        mStack[targetRetRefBase] = res;

                    *(int*)retDstPtr = targetRetRefBase;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static object ReadNeoCallThis(OpCodeR* ip, byte* targetBase, AutoList mStack)
        {
            int thisArgOffset = (int)((uint)ip->Operand4 >> 16);
            int thisIdx = *(int*)(targetBase + thisArgOffset);
            if (thisIdx < 0 || thisIdx >= mStack.Count)
                throw new NullReferenceException("Neo callvirt this is null.");

            object thisObj = mStack[thisIdx];
            if (thisObj == null)
                throw new NullReferenceException("Neo callvirt this is null.");

            return thisObj;
        }

        static IMethod ResolveNeoCallvirtILTarget(OpCodeR* ip, IMethod declaredMethod, byte* targetBase, AutoList mStack)
        {
            object thisObj = ReadNeoCallThis(ip, targetBase, mStack);
            if (thisObj is ILTypeInstance instance)
            {
                int slot = ip->Operand4 & 0xffff;
                if (slot == 0xffff)
                {
                    if (!instance.Type.TryGetNeoVTableSlot(declaredMethod, out slot))
                        throw new MissingMethodException(string.Format("Neo callvirt cannot resolve VTable slot for {0} on {1}.", declaredMethod, instance.Type.FullName));
                }

                var vtable = instance.Type.NeoVTable;
                if (slot < 0 || slot >= vtable.Length)
                    throw new MissingMethodException(string.Format("Neo callvirt VTable slot {0} is missing on {1}.", slot, instance.Type.FullName));

                IMethod actual = vtable[slot];
                if (actual == null)
                    throw new MissingMethodException(string.Format("Neo callvirt VTable slot {0} is null on {1}.", slot, instance.Type.FullName));

                return actual;
            }

            throw new InvalidOperationException(string.Format("Neo Callvirt_IL requires ILTypeInstance this, got {0}.", thisObj.GetType().FullName));
        }

        static IMethod ResolveNeoCallvirtInterfaceTarget(OpCodeR* ip, IMethod declaredMethod, byte* targetBase, AutoList mStack)
        {
            object thisObj = ReadNeoCallThis(ip, targetBase, mStack);
            if (!(thisObj is ILTypeInstance instance))
                throw new InvalidOperationException(string.Format("Neo Callvirt_Interface requires ILTypeInstance this (CLR object through an interface is out of scope for Step 11), got {0}.", thisObj.GetType().FullName));

            ILType runtimeType = instance.Type;
            IType ifaceType = declaredMethod != null ? declaredMethod.DeclearingType : null;
            int ifaceMethodSlot = ip->Operand4 & 0xffff;

            if (ifaceType == null || !runtimeType.TryResolveNeoInterfaceClassSlot(ifaceType, ifaceMethodSlot, out int classSlot))
                throw new MissingMethodException(string.Format("Neo Callvirt_Interface: type {0} does not implement interface {1} (method slot {2}).",
                    runtimeType.FullName, ifaceType != null ? ifaceType.FullName : "<null>", ifaceMethodSlot));

            var vtable = runtimeType.NeoVTable;
            if (classSlot < 0 || classSlot >= vtable.Length)
                throw new MissingMethodException(string.Format("Neo Callvirt_Interface: interface slot out of range on {0} (interface {1}, slot {2} -> class slot {3}).",
                    runtimeType.FullName, ifaceType.FullName, ifaceMethodSlot, classSlot));

            IMethod actual = vtable[classSlot];
            if (actual == null)
                throw new MissingMethodException(string.Format("Neo Callvirt_Interface: interface slot is null on {0} (interface {1}, slot {2} -> class slot {3}).",
                    runtimeType.FullName, ifaceType.FullName, ifaceMethodSlot, classSlot));

            return actual;
        }

        static CLRMethod ResolveNeoCallvirtCLRTarget(OpCodeR* ip, IMethod declaredMethod, byte* targetBase, AutoList mStack)
        {
            ReadNeoCallThis(ip, targetBase, mStack);
            if (declaredMethod is CLRMethod clrMethod)
                return clrMethod;

            throw new InvalidOperationException(string.Format("Neo Callvirt_CLR requires CLRMethod, got {0}.", declaredMethod));
        }

        static IMethod ResolveNeoGenericCallvirtTarget(OpCodeR* ip, IMethod declaredMethod, byte* targetBase, AutoList mStack)
        {
            object thisObj = ReadNeoCallThis(ip, targetBase, mStack);
            if (thisObj is ILTypeInstance)
                return ResolveNeoCallvirtILTarget(ip, declaredMethod, targetBase, mStack);
            if (declaredMethod is CLRMethod)
                return declaredMethod;

            throw new InvalidOperationException(string.Format("Neo generic callvirt cannot dispatch non-IL object {0} to {1}.", thisObj.GetType().FullName, declaredMethod));
        }

        // neo-array-multidim-ilvt (sub-gap 1+2): handle an IL value-type-element
        // multi-dim array's Set/Get element call WITHOUT going through the
        // reflection CLRMethod.Invoke reader (which mis-reads the IL-VT element
        // param as an mStack index -- the formal param is ILTypeInstance, but the
        // call site passes the IL-VT struct as flat bytes + a ref region). The
        // box/unbox is done from the CALLER frame (where the full struct + its ref
        // region live), mirroring the rank-1 Stelem_Ref/Ldelem_Ref CopyFrameToIL/
        // CopyILToFrame path. The element ILType is recovered from the JIT-time
        // token-keyed map (GetNeoIlVtArrayElementType). Returns true if handled.
        //
        // `map` is the call's NeoCallParamMap (the LAST prim entry is the element
        // value's caller-frame source; the ref entries hold its ref-region source).
        // `targetBase` carries the array `this` + the int indices (correctly
        // populated by CopyNeoCallArguments for the 4-byte reference/primitive
        // params). `retDstPtr`/`targetRetRefBase` are the Get return dest.
        static unsafe bool TryNeoIlVtElementArrayCall(
            OpCodeR* ip, IMethod targetMethod, ref NeoCallParamMap map,
            byte* frameBase, int frameRefBase, byte* targetBase,
            AutoList mStack, ILRuntime.Runtime.Enviorment.AppDomain appdomain,
            byte* retDstPtr, int targetRetRefBase)
        {
            ILType elemIl = JITCompiler.GetNeoIlVtArrayElementType(ip->Operand2);
            if (elemIl == null)
                return false;
            // Only Set / Get / Address on the array (ctor is handled by the
            // reflection path, which works once the ILType.GetConstructor
            // delegation resolves it).
            string mname = targetMethod.Name;
            bool isSet = mname == "Set";
            bool isGet = mname == "Get";
            bool isAddress = mname == "Address";
            if (!isSet && !isGet && !isAddress)
                return false;

            // Read the array `this` from targetBase (the thisArg offset is the
            // high 16 bits of Operand4 -- see ReadNeoCallThis).
            int thisArgOff = (int)((uint)ip->Operand4 >> 16);
            int arrIdx = *(int*)(targetBase + thisArgOff);
            if (arrIdx < 0)
                throw new NullReferenceException();
            Array arr = (Array)mStack[arrIdx];
            if (arr == null)
                throw new NullReferenceException();

            // Read the int indices: they follow the `this` in targetBase, in
            // declaration order. The method's formal params are (int, int, ...,
            // [ILTypeInstance]). Walk targetBase with a cursor past `this`.
            int rank = arr.Rank;
            int[] indices = new int[rank];
            int idxCur = thisArgOff + 4; // indices follow the 4-byte `this`
            for (int d = 0; d < rank; d++)
            {
                indices[d] = *(int*)(targetBase + idxCur);
                idxCur += 4;
            }

            int elemPrimSize = elemIl.TotalPrimitiveSize;
            int elemRefCount = elemIl.TotalReferenceCount;

            if (isAddress)
            {
                // Address (multi-dim `ref a[i,j]` ldelema). Return a byref to the
                // IL-VT element's storage. The element lives as a boxed
                // ILTypeInstance cell (the multi-dim Set path boxes it); the byref
                // encodes (mStackIdx_of_the_box, fieldOffset=0), mirroring the
                // rank-1 Ldelema IL-VT-element encoding. A consumer that mutates
                // through the byref (a `ref T` param's `stfld`/`stobj`) resolves
                // mStack[mStackIdx] -> the SAME box the array cell references, so the
                // in-place mutation is observable on a subsequent `a[i,j]` Get.
                // A null (uninitialized) cell is materialized as a fresh default
                // instance + stored back into the cell (lazy init) so the byref
                // points at a mutable box (matches `ref` semantics: the element must
                // exist before its address is taken).
                object got = arr.GetValue(indices);
                ILTypeInstance elemIns;
                if (got is ILTypeInstance ei && ei.Type == elemIl)
                {
                    elemIns = ei;
                }
                else
                {
                    // null or mismatched cell -> materialize a default box + store it
                    // back so the returned byref is observable via a later Get.
                    elemIns = elemIl.Instantiate(false);
                    elemIns.Boxed = true;
                    arr.SetValue(elemIns, indices);
                }
                int elemMStackIdx = mStack.Count;
                mStack.Add(elemIns);
                // 8-byte Ref Slot: (mStackIdx, 0). The Address call's return dest is
                // retDstPtr (a byref value lives in the caller's frame byte region).
                if (retDstPtr != null)
                {
                    *(int*)(retDstPtr + 0) = elemMStackIdx;
                    *(int*)(retDstPtr + 4) = 0;
                }
                return true;
            }
            else if (isSet)
            {
                // The element value is the LAST param. Its caller-frame primitive
                // source is the LAST PrimitiveSrc entry; its ref-region source is
                // the trailing RefSrc entries (elemRefCount of them). Recover both
                // (the CopyNeoCallArguments dest was sized ILTypeInstance=4 bytes,
                // truncating the struct -- so read from the CALLER frame, not the
                // dest). Box into a fresh ILTypeInstance and Array.SetValue it.
                int nPrim = map.PrimitiveSrc != null ? map.PrimitiveSrc.Length : 0;
                if (nPrim < 1 + rank)
                    return false; // malformed (no element value entry) -> let reflection try
                int valPrimSrc = map.PrimitiveSrc[nPrim - 1];
                int valRefSrcBase = -1;
                if (elemRefCount > 0)
                {
                    int nRef = map.RefSrc != null ? map.RefSrc.Length : 0;
                    if (nRef < elemRefCount)
                        return false;
                    // The value's ref-region source is the trailing elemRefCount
                    // RefSrc entries (preceding ref params, if any, occupy earlier
                    // entries -- none for the green target: the only ref-region
                    // param is the value).
                    valRefSrcBase = map.RefSrc[nRef - elemRefCount];
                }
                ILTypeInstance box = elemIl.Instantiate(false);
                CopyFrameToIL(frameBase, valPrimSrc, valRefSrcBase < 0 ? 0 : valRefSrcBase,
                    elemPrimSize, elemRefCount, mStack, frameRefBase, box);
                box.Boxed = true;
                arr.SetValue(box, indices);
                return true;
            }
            else // Get
            {
                object got = arr.GetValue(indices);
                if (got is ILTypeInstance elemIns && elemIns.Type == elemIl)
                {
                    // Unbox the IL-VT element into the caller's dest frame region
                    // (the stored element IS the IL-VT's ILTypeInstance, whether or
                    // not it was marked Boxed on store -- CopyILToFrame copies its
                    // Primitives + ManagedObjects into the dest flat-bytes + ref
                    // region, the inverse of the Set box). A Boxed element still
                    // represents the struct value.
                    CopyILToFrame(elemIns, frameBase, ip->DstOffset, ip->Operand3,
                        elemPrimSize, elemRefCount, mStack, frameRefBase);
                }
                else if (got != null)
                {
                    // A reference/boxed element: store the object on the dest ref
                    // slot and write its mStack index (mirrors Ldelem_Ref).
                    if (targetRetRefBase >= 0)
                    {
                        int dstIdx = targetRetRefBase;
                        if (dstIdx < mStack.Count)
                            mStack[dstIdx] = got;
                        else
                        {
                            mStack.Add(got);
                            dstIdx = mStack.Count - 1;
                        }
                        *(int*)retDstPtr = dstIdx;
                    }
                }
                else
                {
                    // null element (uninitialized cell) -> default struct: zero
                    // the dest primitive bytes (ref slots already null-init).
                    if (elemPrimSize > 0)
                        Unsafe.InitBlock(retDstPtr, 0, (uint)elemPrimSize);
                    *(int*)retDstPtr = -1;
                }
                return true;
            }
        }

        internal unsafe byte* ExecuteNeo(ILMethod method, byte* esp, byte* retDst, int retRefBase, out bool unhandledException,
            byte* vtNewobjCallerDst = null, int vtNewobjCallerDstRefBase = -1, int vtNewobjCallerPrimSize = 0, int vtNewobjCallerRefCount = 0,
            int constrainedSlot0SeedRefOffset = -1, int constrainedSlot0SeedSrcRefBase = -1, int constrainedSlot0SeedRefCount = 0)
        {
#if DEBUG
            if (method == null)
                throw new NullReferenceException();
#endif
#if DEBUG && !NO_PROFILER
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == AppDomain.UnityMainThreadID)

#if UNITY_5_5_OR_NEWER
                UnityEngine.Profiling.Profiler.BeginSample(method.ToString());
#else
                UnityEngine.Profiler.BeginSample(method.ToString());
#endif

#endif
            unhandledException = false;

            OpCodeR[] body = method.CompiledFrame.NeoExecuteBody;
            AutoList mStack = stack.ManagedStack;
            ref readonly var nf = ref method.CompiledFrame;
            int frameSize = nf.TotalStructSize;
            int totalRefSize = nf.TotalRefSize;
            int returnPrimitiveSize = nf.ReturnPrimitiveSize;
            int returnRefCount = nf.ReturnRefCount;
            var localInfos = nf.LocalInfos;
            var localIsRef = nf.LocalIsReference;

            byte* frameBase = esp;
            byte* newEsp = esp + frameSize;
            // TODO: stack overflow check vs stack.StackBase upper bound; will be added in Step 14 / 26

            // Zero locals primitive region
            if (nf.LocalsPrimitiveSize > 0)
                Unsafe.InitBlock(frameBase + nf.ParamPrimitiveSize, 0, (uint)nf.LocalsPrimitiveSize);
            if (localInfos != null && localIsRef != null)
            {
                for (int i = 0; i < localInfos.Length; i++)
                {
                    if (localIsRef[i])
                    {
                        *(int*)(frameBase + localInfos[i].Offset) = -1;
                    }
                }
            }

            // Managed stack reservation for this frame's reference slots
            int frameRefBase = mStack.Count;
            for (int i = 0; i < totalRefSize; i++)
                mStack.Add(null);

            // Step 17 (b) (neo-step17-stobj-refloop): seed the callee slot-0 ref
            // region for a constrained.callvirt DIRECT-CALL on an IL value type
            // WITH reference fields. The seed must run AFTER the reservation above
            // (which zeroes the slots) and BEFORE the body. The caller recovered
            // the source local's ref base (R2) and passes it via these params.
            // `constrainedSlot0SeedRefOffset` is slot-0's RefOffset within THIS
            // frame (calleeFrame.ParamInfos[0].RefOffset); the source ref region
            // is at mStack[constrainedSlot0SeedSrcRefBase + 0..].
            // (M2, review-loop): there is intentionally NO `constrainedSlot0Seed-
            // RefBase` param -- the seed TARGET is always the callee's own
            // `frameRefBase` (= mStack.Count at entry, captured above), never a
            // caller-supplied base. Only RefOffset/SrcRefBase/RefCount are needed.
            if (constrainedSlot0SeedRefCount > 0
                && constrainedSlot0SeedSrcRefBase >= 0 && constrainedSlot0SeedRefOffset >= 0)
            {
                for (int i = 0; i < constrainedSlot0SeedRefCount; i++)
                    mStack[frameRefBase + constrainedSlot0SeedRefOffset + i] =
                        mStack[constrainedSlot0SeedSrcRefBase + i];
            }

            // Frames stack placeholder: keep existing StackFrame plumbing alive.
            // BasePointer is interpreted as byte* via reinterpret cast; full debugger
            // adaptation is deferred to Step 14/26.
            StackFrame frame = new StackFrame();
            frame.LocalVarPointer = (StackObject*)frameBase;
            frame.BasePointer = (StackObject*)frameBase;
            frame.Method = method;
            frame.IsRegister = true;
            frame.ManagedStackBase = frameRefBase;
            frame.ValueTypeBasePointer = stack.ValueTypeStackPointer;
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
            frame.Address = new IntegerReference();
#endif
            stack.PushFrame(ref frame);

            int finallyEndAddress = 0;
            Exception lastCaughtEx = null;
            var ehs = method.ExceptionHandlerRegister;
            // Step 14: when an exception escapes this frame unhandled, we must
            // NOT throw from inside the per-iteration catch (that would skip the
            // bottom-of-method cleanup below and leak this frame on the frames
            // stack / its mStack reservation). Instead we stash the to-be-thrown
            // exception here, break out of the loop, let the cleanup run, and
            // re-throw AFTER cleanup -- making every Neo frame self-cleaning.
            Exception pendingThrow = null;

            fixed (OpCodeR* ptr = body)
            {
                OpCodeR* ip = ptr;
                bool returned = false;
                // Shared locals across case blocks. Declared at method scope so
                // IL2CPP / non-O3 builds reuse the same stack slot for every case.
                IType t;
                ILType ilType;
                ILTypeInstance ins;
                object obj;
                int sz, refCnt, srcIdx, dstIdx, srcRefOffset, dstRefOffset;
                while (!returned)
                {
                    try
                    {
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
                        if (ShouldBreak)
                            Break();
                        var insOffset = (int)(ip - ptr);
                        frame.Address.Value = insOffset;
                        AppDomain.DebugService.CheckShouldBreak(method, this, insOffset);
#endif
                        // Permanent Neo dispatch guard (rasen neo-jit-bogus-opcode, task 4.1).
                        // The loop's termination relies on a Ret/throw; an unconditional ip++
                        // with no bounds check would otherwise read a garbage Code past
                        // body.Length (an un-terminated body or a control-flow target past the
                        // end) and silently dispatch it. This one-int-compare tripwire is checked
                        // BEFORE the ip->Code deref so an overrun never reads OOB; it turns the
                        // failure into a loud, locatable throw. Ships in ALL Neo builds.
                        {
                            int _ipIdx = (int)(ip - ptr);
                            if (_ipIdx >= body.Length)
                                throw new InvalidOperationException(
                                    "Neo: ip ran past body end in " + method
                                    + " at index " + _ipIdx + "/" + body.Length
                                    + " (unterminated body or a control-flow target past the end)");
                        }
                        OpCodeREnum code = ip->Code;
                        switch (code)
                        {
                            case OpCodeREnum.Ldc_I4_M1:
                                *(int*)(frameBase + ip->DstOffset) = -1;
                                break;
                            case OpCodeREnum.Ldc_I4_0:
                                *(int*)(frameBase + ip->DstOffset) = 0;
                                break;
                            case OpCodeREnum.Ldc_I4_1:
                                *(int*)(frameBase + ip->DstOffset) = 1;
                                break;
                            case OpCodeREnum.Ldc_I4_2:
                                *(int*)(frameBase + ip->DstOffset) = 2;
                                break;
                            case OpCodeREnum.Ldc_I4_3:
                                *(int*)(frameBase + ip->DstOffset) = 3;
                                break;
                            case OpCodeREnum.Ldc_I4_4:
                                *(int*)(frameBase + ip->DstOffset) = 4;
                                break;
                            case OpCodeREnum.Ldc_I4_5:
                                *(int*)(frameBase + ip->DstOffset) = 5;
                                break;
                            case OpCodeREnum.Ldc_I4_6:
                                *(int*)(frameBase + ip->DstOffset) = 6;
                                break;
                            case OpCodeREnum.Ldc_I4_7:
                                *(int*)(frameBase + ip->DstOffset) = 7;
                                break;
                            case OpCodeREnum.Ldc_I4_8:
                                *(int*)(frameBase + ip->DstOffset) = 8;
                                break;
                            case OpCodeREnum.Ldc_I4:
                            case OpCodeREnum.Ldc_I4_S:
                                *(int*)(frameBase + ip->DstOffset) = ip->Operand;
                                break;
                            case OpCodeREnum.Ldc_I8:
                                *(long*)(frameBase + ip->DstOffset) = ip->OperandLong;
                                break;
                            case OpCodeREnum.Ldc_R4:
                                *(float*)(frameBase + ip->DstOffset) = ip->OperandFloat;
                                break;
                            case OpCodeREnum.Ldc_R8:
                                *(double*)(frameBase + ip->DstOffset) = ip->OperandDouble;
                                break;
                            case OpCodeREnum.Ldnull:
                                *(int*)(frameBase + ip->DstOffset) = -1;
                                break;
                            case OpCodeREnum.Ldstr:
                                dstIdx = frameRefBase + ip->Operand;
                                mStack[dstIdx] = AppDomain.GetString(ip->OperandLong);
                                *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                break;
                            // neo-ldtoken: loads a metadata-token handle. The JIT
                            // encodes the token kind in Operand (1 = TypeReference,
                            // 0 = FieldReference) and the packed token in OperandLong.
                            // There is NO MethodReference branch -- the JIT throws at
                            // emission for `ldtoken <method>`, so RuntimeMethodHandle
                            // is out of scope by construction.
                            //   Type path (Operand==1, the dominant typeof(T) case):
                            //    resolve IType via AppDomain.GetType and push
                            //    type.ReflectionType as a Neo object reference (mirrors
                            //    Legacy ExecuteR AssignToRegister(..., type.
                            //    ReflectionType)). The dest ref slot index lives in
                            //    Operand4 (@20-23) -- NOT Operand3 (@16-19), which
                            //    aliases the high dword of OperandLong and would
                            //    clobber the field path's declaring-type token.
                            //    Type.GetTypeFromHandle is a no-op pass-through in
                            //    ILRuntime (the Neo GetTypeFromHandle_0_Neo stub reads
                            //    this argument reference and writes it straight back),
                            //    so the System.Type flows to the consumer -- NO real
                            //    RuntimeTypeHandle struct is ever materialised, exactly
                            //    as in Legacy.
                            //   Field path (Operand==0): mirror the Ldsfld arm -- the
                            //    OperandLong encoding is IDENTICAL to Ldsfld (decl-type
                            //    token in the high 32 bits, static-field index in the
                            //    low 32 bits). Read the IL static field value per
                            //    category (primitive / inline-VT / reference). A CLR
                            //    declaring type throws a tagged NIE (Legacy itself
                            //    throws NIE there). NOTE: like Legacy this reads the
                            //    field VALUE, not a RuntimeFieldHandle -- a Legacy quirk
                            //    (there is no GetFieldFromHandle consumer of a real
                            //    handle); mirroring it is the mandate.
                            case OpCodeREnum.Ldtoken:
                                {
                                    if (ip->Operand == 1) // type path: push ReflectionType
                                    {
                                        var type = AppDomain.GetType((int)ip->OperandLong);
                                        if (type == null)
                                            throw new TypeLoadException("Neo Ldtoken: type not resolved for token 0x" + ip->OperandLong.ToString("X"));
                                        dstIdx = frameRefBase + ip->Operand4;
                                        mStack[dstIdx] = type.ReflectionType;
                                        *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                    }
                                    else // field path (Operand == 0) -- mirrors the Ldsfld arm
                                    {
                                        var declType = AppDomain.GetType((int)(ip->OperandLong >> 32));
                                        if (declType == null)
                                            throw new TypeLoadException("Neo Ldtoken: declaring type not resolved for token 0x" + ip->OperandLong.ToString("X"));
                                        if (declType is ILType ilt)
                                        {
                                            int sIdx = (int)ip->OperandLong;
                                            var sinst = ilt.StaticInstance;
                                            var off = ilt.GetStaticFieldOffset(sIdx);
                                            var ft = ilt.StaticFieldTypes.Length > sIdx ? ilt.StaticFieldTypes[sIdx] : null;
                                            byte* dstSlot = frameBase + ip->DstOffset;
                                            // Array-initializer blob (child-6): a C# `new T[]{ many }`
                                            // lowers to `ldtoken <PrivateImplementationDetails> <blob
                                            // field>; call RuntimeHelpers.InitializeArray`. The blob
                                            // field's declared type is a compiler-generated `.size N`
                                            // struct with NO instance fields, so its computed
                                            // TotalPrimitiveSize/TotalReferenceCount are both 0 -- the
                                            // value-type arm below would copy 0 bytes, and the Neo
                                            // static instance never materialised the byte[] either
                                            // (ManagedObjects is null when the declaring type has no
                                            // reference statics; ILTypeInstance's InitialValue replay
                                            // skips the store). The blob lives only in Cecil's
                                            // FieldDefinition.InitialValue, so surface it here as a
                                            // Neo reference for the downstream InitializeArray Neo
                                            // redirect to bulk-copy (mirrors Legacy, whose static
                                            // instance stores the byte[] and whose redirect reads
                                            // param 1 as byte[]). Must precede the value-type arm --
                                            // the blob field IS a value-type ILType.
                                            byte[] initBlob = null;
                                            var sfd = ilt.StaticFieldDefinitions;
                                            if (sfd != null && sfd.Length > sIdx)
                                                initBlob = sfd[sIdx].InitialValue;
                                            if (initBlob != null && initBlob.Length > 0)
                                            {
                                                mStack.Add(initBlob);
                                                *(int*)dstSlot = mStack.Count - 1;
                                            }
                                            else if (ft != null && ft.IsPrimitive)
                                            {
                                                int psz = AppDomain.GetPrimitiveSize(ft);
                                                if (psz == 1) *(int*)dstSlot = (sbyte)sinst.Primitives[off.PrimitiveOffset];
                                                else if (psz == 2) *(int*)dstSlot = Unsafe.ReadUnaligned<short>(ref sinst.Primitives[off.PrimitiveOffset]);
                                                else if (psz == 4) *(int*)dstSlot = Unsafe.ReadUnaligned<int>(ref sinst.Primitives[off.PrimitiveOffset]);
                                                else if (psz == 8) *(long*)dstSlot = Unsafe.ReadUnaligned<long>(ref sinst.Primitives[off.PrimitiveOffset]);
                                            }
                                            else if (ft != null && ft.IsValueType && ft is ILType vtil)
                                            {
                                                Unsafe.CopyBlockUnaligned(ref Unsafe.AsRef<byte>(dstSlot), ref sinst.Primitives[off.PrimitiveOffset], (uint)vtil.TotalPrimitiveSize);
                                                for (int ri = 0; ri < vtil.TotalReferenceCount; ri++)
                                                {
                                                    object rv = sinst.ManagedObjects[off.ReferenceOffset + ri];
                                                    // Allocate a ref slot + store its index in the
                                                    // dest's ref region (mirrors how a VT load
                                                    // materialises refs -- see the Ldsfld arm).
                                                    mStack.Add(rv);
                                                    *(int*)(dstSlot + vtil.TotalPrimitiveSize + ri * 4) = mStack.Count - 1;
                                                }
                                            }
                                            else
                                            {
                                                // Reference static field: materialise a ref-slot
                                                // mStack index into the dest register (mirrors Ldsfld).
                                                object rv = sinst.ManagedObjects[off.ReferenceOffset];
                                                mStack.Add(rv);
                                                *(int*)dstSlot = mStack.Count - 1;
                                            }
                                        }
                                        else throw new NotImplementedException("Neo Ldtoken: CLR field handle not implemented (mirrors Legacy NIE; this reads the field VALUE, not a RuntimeFieldHandle)");
                                    }
                                }
                                break;
                            case OpCodeREnum.Move:
                                Unsafe.CopyBlock(frameBase + ip->DstOffset, frameBase + ip->SrcOffset, (uint)ip->Operand2);
                                if (ip->Operand == 1)
                                {
                                    dstRefOffset = ip->Operand3;
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    dstIdx = frameRefBase + dstRefOffset;
                                    if (srcIdx >= 0)
                                    {
                                        mStack[dstIdx] = mStack[srcIdx];
                                        *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                    }
                                    else
                                    {
                                        mStack[dstIdx] = null;
                                        *(int*)(frameBase + ip->DstOffset) = -1;
                                    }
                                }
                                break;
                            // Step 12b: whole value-type copy (assignment / local-
                            // init / starg of a value type with reference fields).
                            // Operand2 = primitive byte size; Operand3 = dst ref-
                            // run base; Operand = src ref-run base; Operand4 =
                            // refCount. In-frame value-type reference fields live
                            // out-of-line in the frame mStack ref region (Step 12
                            // design), so the copy is a byte CopyBlock for the
                            // primitive region PLUS a direct mStack-to-mStack copy
                            // of refCount reference slots. src and dst runs share
                            // the same length (same-type assignment), preserving
                            // object identity per C# shallow struct-copy semantics.
                            case OpCodeREnum.Move_Vt:
                                {
                                    int vtPrimSize = ip->Operand2;
                                    int vtDstRefBase = ip->Operand3;
                                    int vtSrcRefBase = ip->Operand;
                                    int vtRefCount = ip->Operand4;
                                    if (vtPrimSize > 0)
                                        Unsafe.CopyBlock(frameBase + ip->DstOffset, frameBase + ip->SrcOffset, (uint)vtPrimSize);
                                    for (int i = 0; i < vtRefCount; i++)
                                    {
                                        mStack[frameRefBase + vtDstRefBase + i] =
                                            mStack[frameRefBase + vtSrcRefBase + i];
                                    }
                                }
                                break;
                            // Step 12: ldloca / ldloca.s of a value-type local.
                            // The C# compiler emits `ldloca V; stfld/ldfld/initobj`
                            // for struct field access. The Neo offset-lowering pass
                            // resolves the ldloca dest back to the source local's
                            // frame offset for the _Inline field opcodes and
                            // Initobj, so those do not read this temp's bytes.
                            // Therefore ldloca itself is a no-op at runtime for the
                            // Step 12 in-frame-VT field-access path. (Genuine byref
                            // use of the address -- ref params, stind, fixed -- is a
                            // separate pointer model not covered by Step 12 and
                            // remains unimplemented; the dest temp is left as-is.)
                            case OpCodeREnum.Ldloca:
                            case OpCodeREnum.Ldloca_S:
                            case OpCodeREnum.Ldarga:
                            case OpCodeREnum.Ldarga_S:
                                {
                                    // Step 17: produce a real 8-byte Ref Slot =
                                    // (-1, absoluteFrameOffset) -- a frame-native
                                    // managed address. The optimizer's addrAlias
                                    // folding still resolves the pure in-frame-VT
                                    // `ldloca V; stfld/ldfld/initobj` pattern at
                                    // compile time, in which case this dest is
                                    // dead at runtime (writing the slot is
                                    // harmless). When the address ESCAPES the
                                    // folding window (byref param / stind / ldind
                                    // / stobj / ldobj / ldelema / constrained
                                    // box), the optimizer leaves this dest real
                                    // and this arm produces the genuine Ref Slot.
                                    // (ldloca/ldarga of a reference-typed slot is
                                    // illegal in verifiable IL, so the source is
                                    // always a value-type slot -> frame-native.)
                                    // ip->SrcOffset = the source slot's frame byte
                                    // offset (lowered from R2).
                                    int dst = ip->DstOffset;
                                    *(int*)(frameBase + dst + 0) = -1;            // frame-native
                                    *(int*)(frameBase + dst + 4) = ip->SrcOffset; // absolute frame byte offset
                                }
                                break;
                            // Step 12: ldflda of a nested in-frame value-type
                            // field. Like ldloca, the address is resolved at
                            // offset-lowering time (the dest is recorded in the
                            // address-alias map with the accumulated nested-field
                            // byte offset), so the leaf _Inline field access uses
                            // the folded absolute offset directly. ldflda itself
                            // is therefore a no-op at runtime for the in-frame-VT
                            // path. (Heap-instance ldflda remains Step 6+.)
                            case OpCodeREnum.Ldflda:
                                {
                                    // Step 17: dispatch on the operand's Ref Slot
                                    // objectIndex half. ip->Operand2 =
                                    // field.PrimitiveOffset. ip->SrcOffset = the
                                    // operand slot byte offset (lowered from R2).
                                    // When the operand is an in-frame VT address
                                    // (produced by a real ldloca/ldarga), its
                                    // objectIndex half is -1 and the offset half
                                    // is the VT base -> produce a frame-native
                                    // Ref Slot. When the operand is a heap IL
                                    // object, its slot holds an mStack index
                                    // (objectIndex >= 0) -> produce
                                    // (mStackIdx, fieldPrimOff).
                                    //
                                    // F-6 / NEO-VT-FLDADDR: the JIT stamps
                                    // NeoLdfldaInlineMarker (Operand4 bit 0x1) when
                                    // the source is an in-frame IL value type. With
                                    // the marker set, the operand slot may hold
                                    // FLAT BYTES (a constrained-boxed `this` in an
                                    // IL-struct method body) instead of a Ref Slot.
                                    // The leading int still distinguishes the two
                                    // shapes soundly: with the marker, a non-(-1)
                                    // leading int is a FIELD VALUE (flat bytes),
                                    // NOT an mStack index (the heap/CLR path is non-
                                    // marker), so it cannot mis-fire the heap branch.
                                    int dst = ip->DstOffset;
                                    int operandSlotOff = ip->SrcOffset;
                                    int fieldPrimOff = ip->Operand2;
                                    bool inlineMarker = (ip->Operand4 & JITCompiler.NeoLdfldaInlineMarker) != 0;
                                    bool clrStructFieldMarker = (ip->Operand4 & JITCompiler.NeoLdfldaClrStructFieldMarker) != 0;
                                    bool heapIlRefFieldMarker = (ip->Operand4 & JITCompiler.NeoLdfldaHeapIlRefFieldMarker) != 0;
                                    int objIdx = *(int*)(frameBase + operandSlotOff + 0);
                                    if (heapIlRefFieldMarker && objIdx >= 0)
                                    {
                                        // neo-byref-ldind-ref-heap: the operand is a HEAP IL
                                        // reference instance and the addressed field is a
                                        // REFERENCE-typed field (string / IL-class / object)
                                        // laid out as a ManagedObjects slot at ReferenceOffset
                                        // (NOT a Primitives byte offset). Produce a byref
                                        // (objIdx, ReferenceOffset). No bit-flag is needed: a
                                        // reference load/store (ldind_ref/stind_ref) on a heap
                                        // IL instance ALWAYS targets ManagedObjects (a ref
                                        // field's storage), so the consumer arms dispatch on
                                        // `mStack[objIdx] is ILTypeInstance` (content-based,
                                        // AFTER the F-7B bit-30 caller-owned-slot flag and the
                                        // CLR-array/CLR-object arms), avoiding any collision
                                        // with non-deterministic CLR field-hash offset values
                                        // that may set high bits. objIdx is the IL instance's
                                        // mStack index (>= 0).
                                        *(int*)(frameBase + dst + 0) = objIdx;
                                        *(int*)(frameBase + dst + 4) = ip->Operand3;
                                    }
                                    else if (clrStructFieldMarker && objIdx >= 0)
                                    {
                                        // F-10 / NEO-CLRSTRUCT-FIELD-OF-IL (shape 4): the
                                        // operand is a HEAP IL reference instance and the
                                        // addressed field is a CLR-struct field laid out as
                                        // a reference slot (the boxed struct lives at
                                        // ManagedObjects[ReferenceOffset]). Produce a byref
                                        // (objIdx, ReferenceOffset | flag) so the consumers
                                        // (Ldobj/Stobj/stind/ldind/CopyNeoCallArguments) route
                                        // to ManagedObjects[refOff]. objIdx is the IL
                                        // instance's mStack index (>= 0); the flag in the
                                        // offset half discriminates from a Primitives offset.
                                        *(int*)(frameBase + dst + 0) = objIdx;
                                        *(int*)(frameBase + dst + 4) = ip->Operand3 | JITCompiler.NeoF10ByrefOffsetFlag;
                                    }
                                    else if (objIdx == -1)
                                    {
                                        // Shape 1/2 (marker or not): operand slot
                                        // holds a frame-native Ref Slot produced by
                                        // a real ldloca/ldarga or a byref-`this` --
                                        // resolve through its offset half.
                                        int vtBase = *(int*)(frameBase + operandSlotOff + 4);
                                        *(int*)(frameBase + dst + 0) = -1;
                                        *(int*)(frameBase + dst + 4) = vtBase + fieldPrimOff;
                                    }
                                    else if (inlineMarker)
                                    {
                                        // Shape 3 (F-6): operand slot holds the in-
                                        // frame VT's flat bytes (e.g. a constrained-
                                        // boxed `this`). The slot's own frame byte
                                        // offset IS the struct base.
                                        *(int*)(frameBase + dst + 0) = -1;
                                        *(int*)(frameBase + dst + 4) = operandSlotOff + fieldPrimOff;
                                    }
                                    else
                                    {
                                        // Heap IL / CLR-object operand: its slot
                                        // holds an mStack index (objectIndex >= 0).
                                        // For a CLR object, fieldPrimOff is the
                                        // FieldInfo hash (set by AppDomain.GetFieldOffset
                                        // for a non-IL declaring type as type.GetFieldIndex
                                        // (token)); the stind/ldind/stobj/ldobj consumer
                                        // resolves it via CLRType.GetFieldValue /
                                        // SetFieldValue (Step 13 Area 4d). For an IL heap
                                        // object, fieldPrimOff is the field's PrimitiveOffset
                                        // (the Primitives byte offset). No JIT change was
                                        // needed for 4d -- the hash was already stamped.
                                        *(int*)(frameBase + dst + 0) = objIdx;
                                        *(int*)(frameBase + dst + 4) = fieldPrimOff;
                                    }
                                }
                                break;
                            case OpCodeREnum.Add:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) + *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Sub:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) - *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Mul:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) * *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Div:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) / *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Div_Un:
                                *(int*)(frameBase + ip->DstOffset) = (int)(*(uint*)(frameBase + ip->SrcOffset) / *(uint*)(frameBase + ip->OperandOffset));
                                break;
                            case OpCodeREnum.Rem:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) % *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Rem_Un:
                                *(int*)(frameBase + ip->DstOffset) = (int)(*(uint*)(frameBase + ip->SrcOffset) % *(uint*)(frameBase + ip->OperandOffset));
                                break;
                            case OpCodeREnum.And:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) & *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Or:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) | *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Xor:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) ^ *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Shl:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) << *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Shr:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) >> *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Shr_Un:
                                *(int*)(frameBase + ip->DstOffset) = (int)(*(uint*)(frameBase + ip->SrcOffset) >> *(int*)(frameBase + ip->OperandOffset));
                                break;
                            case OpCodeREnum.Neg:
                                *(int*)(frameBase + ip->DstOffset) = -*(int*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Not:
                                *(int*)(frameBase + ip->DstOffset) = ~*(int*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Add_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) + *(long*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Sub_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) - *(long*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Mul_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) * *(long*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Div_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) / *(long*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Div_Un_I8:
                                *(long*)(frameBase + ip->DstOffset) = (long)(*(ulong*)(frameBase + ip->SrcOffset) / *(ulong*)(frameBase + ip->OperandOffset));
                                break;
                            case OpCodeREnum.Rem_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) % *(long*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Rem_Un_I8:
                                *(long*)(frameBase + ip->DstOffset) = (long)(*(ulong*)(frameBase + ip->SrcOffset) % *(ulong*)(frameBase + ip->OperandOffset));
                                break;
                            case OpCodeREnum.And_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) & *(long*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Or_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) | *(long*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Xor_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) ^ *(long*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Shl_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) << *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Shr_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) >> *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Shr_Un_I8:
                                *(long*)(frameBase + ip->DstOffset) = (long)(*(ulong*)(frameBase + ip->SrcOffset) >> *(int*)(frameBase + ip->OperandOffset));
                                break;
                            case OpCodeREnum.Neg_I8:
                                *(long*)(frameBase + ip->DstOffset) = -*(long*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Not_I8:
                                *(long*)(frameBase + ip->DstOffset) = ~*(long*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Add_R4:
                                *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) + *(float*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Sub_R4:
                                *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) - *(float*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Mul_R4:
                                *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) * *(float*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Div_R4:
                                *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) / *(float*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Rem_R4:
                                *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) % *(float*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Neg_R4:
                                *(float*)(frameBase + ip->DstOffset) = -*(float*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Add_R8:
                                *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) + *(double*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Sub_R8:
                                *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) - *(double*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Mul_R8:
                                *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) * *(double*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Div_R8:
                                *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) / *(double*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Rem_R8:
                                *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) % *(double*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Neg_R8:
                                *(double*)(frameBase + ip->DstOffset) = -*(double*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Ceq:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) == *(int*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            // neo-ceq-null-sentinel: the type-specialized Ceq for a
                            // REFERENCE operand. Under the Neo flat frame a reference
                            // is an mStack index in the slot's primitive bytes, and
                            // null is a NON-ZERO index (IL-static Ldsfeld
                            // mStack.Add(null)+index) or the -1 sentinel (CLR-static
                            // Ldsfeld / Ldnull), so the raw-int32 Ceq above mis-
                            // compares the index integers (e.g. index N vs ldnull -1
                            // -> "not equal" -> x==null wrongly FALSE -> lazy-init
                            // skipped -> downstream NRE). Resolve each operand to its
                            // referenced object (or null) and compare by C# reference
                            // equality: null==null -> true, obj==null -> false,
                            // obj1==obj2 -> identity (Legacy Ceq parity,
                            // ILIntepreter.Register.cs:4557-4603 -- same-type Object =
                            // mStack[a]==mStack[b], Null = true, mixed Object/Null =
                            // mStack[v]==null). a = SrcOffset/Register2, b =
                            // OperandOffset/Register3, dest = DstOffset/Register1.
                            case OpCodeREnum.Ceq_Ref:
                                {
                                    int cra = *(int*)(frameBase + ip->SrcOffset);
                                    int crb = *(int*)(frameBase + ip->OperandOffset);
                                    object rra = cra >= 0 ? mStack[cra] : null;
                                    object rrb = crb >= 0 ? mStack[crb] : null;
                                    *(int*)(frameBase + ip->DstOffset) = rra == rrb ? 1 : 0;
                                }
                                break;
                            case OpCodeREnum.Cgt:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) > *(int*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgt_Un:
                                {
                                    // Step 15: CIL `cgt.un` doubles as the reference
                                    // "not null" test (the C# `is`/`as != null`/`!= null`
                                    // lowering is `ldnull; cgt.un`). Under the Neo flat
                                    // frame a reference is an mStack index with -1 =
                                    // null, so a naive unsigned compare mis-handles the
                                    // null sentinel (-1 == 0xFFFFFFFF). Mirror Legacy's
                                    // reference/integer rule (ILIntepreter.Register.cs
                                    // Cgt_Un): when the src slot is null (-1) the result
                                    // is false; otherwise the unsigned compare holds, OR
                                    // the operand is itself null (-1). (Diverges from raw
                                    // unsigned semantics for TWO symmetric sentinel
                                    // collisions, both because -1 == 0xFFFFFFFF: (a) the
                                    // operand case `cgt.un x, (uint)0xFFFFFFFF`, where the
                                    // `cguB == -1` clause short-circuits the compare to
                                    // `true`; and (b) the source case
                                    // `cgt.un (uint)0xFFFFFFFF, x`, where the leading
                                    // `cguA != -1` clause forces the result to `false`.
                                    // Neither is exercised by the validated tests.)
                                    int cguA = *(int*)(frameBase + ip->SrcOffset);
                                    int cguB = *(int*)(frameBase + ip->OperandOffset);
                                    bool cguRes = cguA != -1 && ((uint)cguA > (uint)cguB || cguB == -1);
                                    *(int*)(frameBase + ip->DstOffset) = cguRes ? 1 : 0;
                                }
                                break;
                            case OpCodeREnum.Clt:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) < *(int*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Clt_Un:
                                *(int*)(frameBase + ip->DstOffset) = *(uint*)(frameBase + ip->SrcOffset) < *(uint*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Ceq_I8:
                                *(int*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) == *(long*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgt_I8:
                                *(int*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) > *(long*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgt_Un_I8:
                                *(int*)(frameBase + ip->DstOffset) = *(ulong*)(frameBase + ip->SrcOffset) > *(ulong*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Clt_I8:
                                *(int*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) < *(long*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Clt_Un_I8:
                                *(int*)(frameBase + ip->DstOffset) = *(ulong*)(frameBase + ip->SrcOffset) < *(ulong*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Ceq_R4:
                                *(int*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) == *(float*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Ceq_R8:
                                *(int*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) == *(double*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgt_R4:
                            case OpCodeREnum.Cgt_Un_R4:
                                *(int*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) > *(float*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgt_R8:
                            case OpCodeREnum.Cgt_Un_R8:
                                *(int*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) > *(double*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Clt_R4:
                            case OpCodeREnum.Clt_Un_R4:
                                *(int*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) < *(float*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Clt_R8:
                            case OpCodeREnum.Clt_Un_R8:
                                *(int*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) < *(double*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Br:
                            case OpCodeREnum.Br_S:
                                ip = ptr + ip->Operand;
                                continue;
                            case OpCodeREnum.Brtrue:
                            case OpCodeREnum.Brtrue_S:
                                // Brtrue/Brfalse test a truth value (CIL int32 / object
                                // ref). Under the Neo flat frame a temp register's slot is
                                // sized to the method's MAX value-type size (>=8), so the
                                // bool/int32 result a compare or a bool-returning call
                                // writes only the LOW 4 bytes, leaving STALE upper bytes
                                // when the register was reused (e.g. `||`-chained string-!=
                                // on IL-VT fields -- the array-element read leaves a non-
                                // zero high dword). Reading the full slot width (the prior
                                // `Operand2 == 8 ? *(long*)` path) then mis-fires the branch
                                // on the stale high bytes. Roslyn lowers every non-int32
                                // truthiness (long, float, object) to a compare/ceq whose
                                // result IS a 4-byte int32 0/1, so the truth value reaching
                                // here is ALWAYS the low int32. Test only that. (See
                                // openspec/changes/neo-vt-field-orchain-compare/design.md.)
                                if (*(int*)(frameBase + ip->DstOffset) != 0)
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Brfalse:
                            case OpCodeREnum.Brfalse_S:
                                if (*(int*)(frameBase + ip->DstOffset) == 0)
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            // neo-brtrue-on-reference: the type-specialized branch
                            // for a REFERENCE condition. Under the Neo flat frame a
                            // reference is an mStack index in the slot's primitive
                            // bytes, and null is a NON-ZERO index (IL-static Ldsfeld
                            // does mStack.Add(null)+index) or the -1 sentinel (CLR-
                            // static Ldsfeld / Ldnull). The plain int32 (!=0) test
                            // therefore misreads null as truthy. Test the REFERENCED
                            // object's nullness instead: mStack[idx] != null (Legacy
                            // `mStack[reg1->Value] != null` parity,
                            // ILIntepreter.Register.cs:2053). `idx >= 0` makes the -1
                            // sentinel falsey; a non-negative index to a null
                            // mStack entry is falsey via mStack[idx] == null. Covers
                            // all three null encodings. Brtrue/Brfalse ARE lowered by
                            // LowerNeoOffsets, so ip->DstOffset is a real byte offset.
                            case OpCodeREnum.Brtrue_Ref:
                                {
                                    int idx = *(int*)(frameBase + ip->DstOffset);
                                    if (idx >= 0 && mStack[idx] != null)
                                    {
                                        ip = ptr + ip->Operand;
                                        continue;
                                    }
                                }
                                break;
                            case OpCodeREnum.Brfalse_Ref:
                                {
                                    int idx = *(int*)(frameBase + ip->DstOffset);
                                    if (!(idx >= 0 && mStack[idx] != null))
                                    {
                                        ip = ptr + ip->Operand;
                                        continue;
                                    }
                                }
                                break;
                            case OpCodeREnum.Switch:
                                {
                                    // CIL switch: jump-table dispatch. The index value
                                    // lives in Register1's slot. Switch is NOT in the
                                    // LowerNeoOffsets case-list (only its jump-table
                                    // TARGETS are remapped by FixBranchTargetsAfterRemove
                                    // at Optimizer.Neo.cs:1728), so ip->DstOffset still
                                    // holds the raw Register1 INDEX -- resolve its byte
                                    // offset at runtime via localInfos (the SAME defensive
                                    // `(idx < localInfos.Length) ? .Offset : idx` pattern
                                    // the un-lowered Stsfld/Ldsfld arms use). The jump
                                    // table is method.JumpTablesRegister[ip->Operand];
                                    // in-range -> ip = ptr + table[idx]; continue; out-of-
                                    // range -> fall through (no jump). Byte-for-byte Legacy
                                    // (Register.cs:2754-2764).
                                    int swIdxReg = ip->DstOffset;
                                    int swIdxOff = (localInfos != null && swIdxReg < localInfos.Length) ? localInfos[swIdxReg].Offset : swIdxReg;
                                    int swVal = *(int*)(frameBase + swIdxOff);
                                    var swTable = method.JumpTablesRegister[ip->Operand];
                                    if (swVal >= 0 && swVal < swTable.Length)
                                    {
                                        ip = ptr + swTable[swVal];
                                        continue;
                                    }
                                }
                                break;
                            case OpCodeREnum.Beq:
                                if (*(int*)(frameBase + ip->DstOffset) == *(int*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            // neo-ceq-null-sentinel: Beq_Ref / Bne_Un_Ref -- the
                            // type-specialized conditional branches for a REFERENCE
                            // operand (sibling of Ceq_Ref above and Brtrue_Ref at
                            // :2091). Resolve each operand to its referenced object
                            // (or null) and branch on reference identity, not the raw
                            // mStack-index int32s. a = DstOffset/Register1, b =
                            // SrcOffset/Register2 (Legacy Beq/Bne_Un parity,
                            // ILIntepreter.Register.cs:2090-2136 / :2172-2220).
                            case OpCodeREnum.Beq_Ref:
                                {
                                    int bra = *(int*)(frameBase + ip->DstOffset);
                                    int brb = *(int*)(frameBase + ip->SrcOffset);
                                    object bra2 = bra >= 0 ? mStack[bra] : null;
                                    object brb2 = brb >= 0 ? mStack[brb] : null;
                                    if (bra2 == brb2)
                                    {
                                        ip = ptr + ip->Operand;
                                        continue;
                                    }
                                }
                                break;
                            case OpCodeREnum.Bne_Un:
                                if (*(int*)(frameBase + ip->DstOffset) != *(int*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bne_Un_Ref:
                                {
                                    int bnra = *(int*)(frameBase + ip->DstOffset);
                                    int bnrb = *(int*)(frameBase + ip->SrcOffset);
                                    object bnra2 = bnra >= 0 ? mStack[bnra] : null;
                                    object bnrb2 = bnrb >= 0 ? mStack[bnrb] : null;
                                    if (bnra2 != bnrb2)
                                    {
                                        ip = ptr + ip->Operand;
                                        continue;
                                    }
                                }
                                break;
                            case OpCodeREnum.Blt:
                                if (*(int*)(frameBase + ip->DstOffset) < *(int*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgt:
                                if (*(int*)(frameBase + ip->DstOffset) > *(int*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Ble:
                                if (*(int*)(frameBase + ip->DstOffset) <= *(int*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bge:
                                if (*(int*)(frameBase + ip->DstOffset) >= *(int*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blt_Un:
                                if (*(uint*)(frameBase + ip->DstOffset) < *(uint*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgt_Un:
                                if (*(uint*)(frameBase + ip->DstOffset) > *(uint*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Ble_Un:
                                if (*(uint*)(frameBase + ip->DstOffset) <= *(uint*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bge_Un:
                                if (*(uint*)(frameBase + ip->DstOffset) >= *(uint*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Beq_I8:
                                if (*(long*)(frameBase + ip->DstOffset) == *(long*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bne_Un_I8:
                                if (*(long*)(frameBase + ip->DstOffset) != *(long*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blt_I8:
                                if (*(long*)(frameBase + ip->DstOffset) < *(long*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgt_I8:
                                if (*(long*)(frameBase + ip->DstOffset) > *(long*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Ble_I8:
                                if (*(long*)(frameBase + ip->DstOffset) <= *(long*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bge_I8:
                                if (*(long*)(frameBase + ip->DstOffset) >= *(long*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blt_Un_I8:
                                if (*(ulong*)(frameBase + ip->DstOffset) < *(ulong*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgt_Un_I8:
                                if (*(ulong*)(frameBase + ip->DstOffset) > *(ulong*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Ble_Un_I8:
                                if (*(ulong*)(frameBase + ip->DstOffset) <= *(ulong*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bge_Un_I8:
                                if (*(ulong*)(frameBase + ip->DstOffset) >= *(ulong*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Beq_R4:
                                if (*(float*)(frameBase + ip->DstOffset) == *(float*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bne_Un_R4:
                                if (*(float*)(frameBase + ip->DstOffset) != *(float*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blt_R4:
                            case OpCodeREnum.Blt_Un_R4:
                                if (*(float*)(frameBase + ip->DstOffset) < *(float*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgt_R4:
                            case OpCodeREnum.Bgt_Un_R4:
                                if (*(float*)(frameBase + ip->DstOffset) > *(float*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Ble_R4:
                            case OpCodeREnum.Ble_Un_R4:
                                if (*(float*)(frameBase + ip->DstOffset) <= *(float*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bge_R4:
                            case OpCodeREnum.Bge_Un_R4:
                                if (*(float*)(frameBase + ip->DstOffset) >= *(float*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Beq_R8:
                                if (*(double*)(frameBase + ip->DstOffset) == *(double*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bne_Un_R8:
                                if (*(double*)(frameBase + ip->DstOffset) != *(double*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blt_R8:
                            case OpCodeREnum.Blt_Un_R8:
                                if (*(double*)(frameBase + ip->DstOffset) < *(double*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgt_R8:
                            case OpCodeREnum.Bgt_Un_R8:
                                if (*(double*)(frameBase + ip->DstOffset) > *(double*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Ble_R8:
                            case OpCodeREnum.Ble_Un_R8:
                                if (*(double*)(frameBase + ip->DstOffset) <= *(double*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bge_R8:
                            case OpCodeREnum.Bge_Un_R8:
                                if (*(double*)(frameBase + ip->DstOffset) >= *(double*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Addi:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) + ip->Operand;
                                break;
                            case OpCodeREnum.Subi:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) - ip->Operand;
                                break;
                            case OpCodeREnum.Muli:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) * ip->Operand;
                                break;
                            case OpCodeREnum.Divi:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) / ip->Operand;
                                break;
                            case OpCodeREnum.Divi_Un:
                                *(int*)(frameBase + ip->DstOffset) = (int)(*(uint*)(frameBase + ip->SrcOffset) / (uint)ip->Operand);
                                break;
                            case OpCodeREnum.Remi:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) % ip->Operand;
                                break;
                            case OpCodeREnum.Remi_Un:
                                *(int*)(frameBase + ip->DstOffset) = (int)(*(uint*)(frameBase + ip->SrcOffset) % (uint)ip->Operand);
                                break;
                            case OpCodeREnum.Andi:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) & ip->Operand;
                                break;
                            case OpCodeREnum.Ori:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) | ip->Operand;
                                break;
                            case OpCodeREnum.Xori:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) ^ ip->Operand;
                                break;
                            case OpCodeREnum.Shli:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) << ip->Operand;
                                break;
                            case OpCodeREnum.Shri:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) >> ip->Operand;
                                break;
                            case OpCodeREnum.Shri_Un:
                                *(int*)(frameBase + ip->DstOffset) = (int)(*(uint*)(frameBase + ip->SrcOffset) >> ip->Operand);
                                break;
                            case OpCodeREnum.Addi_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) + ip->OperandLong;
                                break;
                            case OpCodeREnum.Subi_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) - ip->OperandLong;
                                break;
                            case OpCodeREnum.Muli_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) * ip->OperandLong;
                                break;
                            case OpCodeREnum.Divi_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) / ip->OperandLong;
                                break;
                            case OpCodeREnum.Divi_Un_I8:
                                *(long*)(frameBase + ip->DstOffset) = (long)(*(ulong*)(frameBase + ip->SrcOffset) / (ulong)ip->OperandLong);
                                break;
                            case OpCodeREnum.Remi_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) % ip->OperandLong;
                                break;
                            case OpCodeREnum.Remi_Un_I8:
                                *(long*)(frameBase + ip->DstOffset) = (long)(*(ulong*)(frameBase + ip->SrcOffset) % (ulong)ip->OperandLong);
                                break;
                            case OpCodeREnum.Andi_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) & ip->OperandLong;
                                break;
                            case OpCodeREnum.Ori_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) | ip->OperandLong;
                                break;
                            case OpCodeREnum.Xori_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) ^ ip->OperandLong;
                                break;
                            case OpCodeREnum.Shli_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) << (int)ip->OperandLong;
                                break;
                            case OpCodeREnum.Shri_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) >> (int)ip->OperandLong;
                                break;
                            case OpCodeREnum.Shri_Un_I8:
                                *(long*)(frameBase + ip->DstOffset) = (long)(*(ulong*)(frameBase + ip->SrcOffset) >> (int)ip->OperandLong);
                                break;
                            case OpCodeREnum.Addi_R4:
                                *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) + ip->OperandFloat;
                                break;
                            case OpCodeREnum.Subi_R4:
                                *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) - ip->OperandFloat;
                                break;
                            case OpCodeREnum.Muli_R4:
                                *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) * ip->OperandFloat;
                                break;
                            case OpCodeREnum.Divi_R4:
                                *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) / ip->OperandFloat;
                                break;
                            case OpCodeREnum.Remi_R4:
                                *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) % ip->OperandFloat;
                                break;
                            case OpCodeREnum.Addi_R8:
                                *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) + ip->OperandDouble;
                                break;
                            case OpCodeREnum.Subi_R8:
                                *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) - ip->OperandDouble;
                                break;
                            case OpCodeREnum.Muli_R8:
                                *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) * ip->OperandDouble;
                                break;
                            case OpCodeREnum.Divi_R8:
                                *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) / ip->OperandDouble;
                                break;
                            case OpCodeREnum.Remi_R8:
                                *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) % ip->OperandDouble;
                                break;
                            case OpCodeREnum.Ceqi:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) == ip->Operand ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgti:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) > ip->Operand ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgti_Un:
                                *(int*)(frameBase + ip->DstOffset) = *(uint*)(frameBase + ip->SrcOffset) > (uint)ip->Operand ? 1 : 0;
                                break;
                            case OpCodeREnum.Clti:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) < ip->Operand ? 1 : 0;
                                break;
                            case OpCodeREnum.Clti_Un:
                                *(int*)(frameBase + ip->DstOffset) = *(uint*)(frameBase + ip->SrcOffset) < (uint)ip->Operand ? 1 : 0;
                                break;
                            case OpCodeREnum.Ceqi_I8:
                                *(int*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) == ip->OperandLong ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgti_I8:
                                *(int*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) > ip->OperandLong ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgti_Un_I8:
                                *(int*)(frameBase + ip->DstOffset) = *(ulong*)(frameBase + ip->SrcOffset) > (ulong)ip->OperandLong ? 1 : 0;
                                break;
                            case OpCodeREnum.Clti_I8:
                                *(int*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) < ip->OperandLong ? 1 : 0;
                                break;
                            case OpCodeREnum.Clti_Un_I8:
                                *(int*)(frameBase + ip->DstOffset) = *(ulong*)(frameBase + ip->SrcOffset) < (ulong)ip->OperandLong ? 1 : 0;
                                break;
                            case OpCodeREnum.Ceqi_R4:
                                *(int*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) == ip->OperandFloat ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgti_R4:
                            case OpCodeREnum.Cgti_Un_R4:
                                *(int*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) > ip->OperandFloat ? 1 : 0;
                                break;
                            case OpCodeREnum.Clti_R4:
                            case OpCodeREnum.Clti_Un_R4:
                                *(int*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) < ip->OperandFloat ? 1 : 0;
                                break;
                            case OpCodeREnum.Ceqi_R8:
                                *(int*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) == ip->OperandDouble ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgti_R8:
                            case OpCodeREnum.Cgti_Un_R8:
                                *(int*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) > ip->OperandDouble ? 1 : 0;
                                break;
                            case OpCodeREnum.Clti_R8:
                            case OpCodeREnum.Clti_Un_R8:
                                *(int*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) < ip->OperandDouble ? 1 : 0;
                                break;
                            case OpCodeREnum.Beqi:
                                if (*(int*)(frameBase + ip->DstOffset) == ip->Operand)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bnei_Un:
                                if (*(int*)(frameBase + ip->DstOffset) != ip->Operand)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blti:
                                if (*(int*)(frameBase + ip->DstOffset) < ip->Operand)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgti:
                                if (*(int*)(frameBase + ip->DstOffset) > ip->Operand)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blei:
                                if (*(int*)(frameBase + ip->DstOffset) <= ip->Operand)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgei:
                                if (*(int*)(frameBase + ip->DstOffset) >= ip->Operand)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blti_Un:
                                if (*(uint*)(frameBase + ip->DstOffset) < (uint)ip->Operand)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgti_Un:
                                if (*(uint*)(frameBase + ip->DstOffset) > (uint)ip->Operand)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blei_Un:
                                if (*(uint*)(frameBase + ip->DstOffset) <= (uint)ip->Operand)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgei_Un:
                                if (*(uint*)(frameBase + ip->DstOffset) >= (uint)ip->Operand)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Beqi_I8:
                                if (*(long*)(frameBase + ip->DstOffset) == ip->OperandLong)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bnei_Un_I8:
                                if (*(long*)(frameBase + ip->DstOffset) != ip->OperandLong)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blti_I8:
                                if (*(long*)(frameBase + ip->DstOffset) < ip->OperandLong)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgti_I8:
                                if (*(long*)(frameBase + ip->DstOffset) > ip->OperandLong)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blei_I8:
                                if (*(long*)(frameBase + ip->DstOffset) <= ip->OperandLong)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgei_I8:
                                if (*(long*)(frameBase + ip->DstOffset) >= ip->OperandLong)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blti_Un_I8:
                                if (*(ulong*)(frameBase + ip->DstOffset) < (ulong)ip->OperandLong)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgti_Un_I8:
                                if (*(ulong*)(frameBase + ip->DstOffset) > (ulong)ip->OperandLong)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blei_Un_I8:
                                if (*(ulong*)(frameBase + ip->DstOffset) <= (ulong)ip->OperandLong)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgei_Un_I8:
                                if (*(ulong*)(frameBase + ip->DstOffset) >= (ulong)ip->OperandLong)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Beqi_R4:
                                if (*(float*)(frameBase + ip->DstOffset) == ip->OperandFloat)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bnei_Un_R4:
                                if (*(float*)(frameBase + ip->DstOffset) != ip->OperandFloat)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blti_R4:
                            case OpCodeREnum.Blti_Un_R4:
                                if (*(float*)(frameBase + ip->DstOffset) < ip->OperandFloat)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgti_R4:
                            case OpCodeREnum.Bgti_Un_R4:
                                if (*(float*)(frameBase + ip->DstOffset) > ip->OperandFloat)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blei_R4:
                            case OpCodeREnum.Blei_Un_R4:
                                if (*(float*)(frameBase + ip->DstOffset) <= ip->OperandFloat)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgei_R4:
                            case OpCodeREnum.Bgei_Un_R4:
                                if (*(float*)(frameBase + ip->DstOffset) >= ip->OperandFloat)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Beqi_R8:
                                if (*(double*)(frameBase + ip->DstOffset) == ip->OperandDouble)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bnei_Un_R8:
                                if (*(double*)(frameBase + ip->DstOffset) != ip->OperandDouble)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blti_R8:
                            case OpCodeREnum.Blti_Un_R8:
                                if (*(double*)(frameBase + ip->DstOffset) < ip->OperandDouble)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgti_R8:
                            case OpCodeREnum.Bgti_Un_R8:
                                if (*(double*)(frameBase + ip->DstOffset) > ip->OperandDouble)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blei_R8:
                            case OpCodeREnum.Blei_Un_R8:
                                if (*(double*)(frameBase + ip->DstOffset) <= ip->OperandDouble)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgei_R8:
                            case OpCodeREnum.Bgei_Un_R8:
                                if (*(double*)(frameBase + ip->DstOffset) >= ip->OperandDouble)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Conv_I1:
                                *(int*)(frameBase + ip->DstOffset) = (sbyte)ReadConvI8(frameBase, ip->SrcOffset, (NeoPrimitiveTypeTag)ip->Operand2);
                                break;
                            case OpCodeREnum.Conv_U1:
                                *(int*)(frameBase + ip->DstOffset) = (byte)ReadConvI8(frameBase, ip->SrcOffset, (NeoPrimitiveTypeTag)ip->Operand2);
                                break;
                            case OpCodeREnum.Conv_I2:
                                *(int*)(frameBase + ip->DstOffset) = (short)ReadConvI8(frameBase, ip->SrcOffset, (NeoPrimitiveTypeTag)ip->Operand2);
                                break;
                            case OpCodeREnum.Conv_U2:
                                *(int*)(frameBase + ip->DstOffset) = (ushort)ReadConvI8(frameBase, ip->SrcOffset, (NeoPrimitiveTypeTag)ip->Operand2);
                                break;
                            case OpCodeREnum.Conv_I4:
                                *(int*)(frameBase + ip->DstOffset) = ReadConvI4(frameBase, ip->SrcOffset, (NeoPrimitiveTypeTag)ip->Operand2);
                                break;
                            case OpCodeREnum.Conv_U4:
                                *(uint*)(frameBase + ip->DstOffset) = ReadConvU4(frameBase, ip->SrcOffset, (NeoPrimitiveTypeTag)ip->Operand2);
                                break;
                            case OpCodeREnum.Conv_I8:
                                *(long*)(frameBase + ip->DstOffset) = ReadConvI8(frameBase, ip->SrcOffset, (NeoPrimitiveTypeTag)ip->Operand2);
                                break;
                            case OpCodeREnum.Conv_U8:
                                *(ulong*)(frameBase + ip->DstOffset) = ReadConvU8(frameBase, ip->SrcOffset, (NeoPrimitiveTypeTag)ip->Operand2);
                                break;
                            case OpCodeREnum.Conv_R4:
                                *(float*)(frameBase + ip->DstOffset) = ReadConvR4(frameBase, ip->SrcOffset, (NeoPrimitiveTypeTag)ip->Operand2);
                                break;
                            case OpCodeREnum.Conv_R8:
                                *(double*)(frameBase + ip->DstOffset) = ReadConvR8(frameBase, ip->SrcOffset, (NeoPrimitiveTypeTag)ip->Operand2);
                                break;
                            case OpCodeREnum.Conv_R_Un:
                                {
                                    // conv.r.un: convert an UNSIGNED integer (or widen a
                                    // float) to F. The dest slot is DoubleType (8 bytes, per
                                    // GetConvResultType), so the arm ALWAYS writes a double --
                                    // a 4-byte float write would leave the high dword stale.
                                    // Integers are interpreted UNSIGNED (the `.un` suffix):
                                    // ReadConvU4/ReadConvU8 cast via uint/ulong, so e.g. a
                                    // source 0xFFFFFFFF yields 4294967295.0, not -1.0. The
                                    // source-type tag is ip->Operand2 (stamped at
                                    // JITCompiler.cs:975, grouped with Conv_R4/R8); dispatch
                                    // on the width so a 64-bit source is NOT truncated by
                                    // ReadConvU4 (Legacy mirror Register.cs:1258-1294).
                                    var cuTag = (NeoPrimitiveTypeTag)ip->Operand2;
                                    double cuResult;
                                    if (cuTag == NeoPrimitiveTypeTag.I8 || cuTag == NeoPrimitiveTypeTag.U8)
                                        cuResult = (double)ReadConvU8(frameBase, ip->SrcOffset, cuTag);
                                    else if (cuTag == NeoPrimitiveTypeTag.R4)
                                        cuResult = (double)*(float*)(frameBase + ip->SrcOffset);
                                    else if (cuTag == NeoPrimitiveTypeTag.R8)
                                        cuResult = *(double*)(frameBase + ip->SrcOffset);
                                    else // I4 / U4
                                        cuResult = (double)ReadConvU4(frameBase, ip->SrcOffset, cuTag);
                                    *(double*)(frameBase + ip->DstOffset) = cuResult;
                                }
                                break;
                            case OpCodeREnum.Ldftn:
                                {
                                    // Step 19: load an IMethod (a managed CLR object)
                                    // into the dest ref slot. An IMethod is a reference
                                    // under the Neo object model, so it lives in mStack
                                    // with a 4-byte index in the frame byte region --
                                    // exactly like Ldstr / a reference-type local (D1).
                                    IMethod m = AppDomain.GetMethod(ip->Operand2);
                                    int ldftnDstRef = frameRefBase + ip->Operand;
                                    mStack[ldftnDstRef] = m;
                                    *(int*)(frameBase + ip->DstOffset) = ldftnDstRef;
                                    ip++;
                                    continue;
                                }
                            case OpCodeREnum.Ldvirtftn:
                                {
                                    // Step 19: resolve the virtual-method override via
                                    // the VTable slot (reusing Step 10's GetVirtualMethod),
                                    // then store the resolved IMethod into the dest ref
                                    // slot (same shape as Ldftn). The `this` source is a
                                    // Neo ref slot (object reference) for Step 19 scope
                                    // (delegate over an IL ref-type instance method or a
                                    // static method); a CLR-struct-instance-method delegate
                                    // is the area4 byref-`this` shape and is out of scope.
                                    IMethod target = AppDomain.GetMethod(ip->Operand2);
                                    int thisIdx = *(int*)(frameBase + ip->SrcOffset);
                                    object thisObj = (thisIdx >= 0) ? mStack[thisIdx] : null;
                                    if (thisObj == null)
                                        throw new NullReferenceException("Neo ldvirtftn: null this");
                                    IMethod resolved;
                                    if (target is ILMethod ilm)
                                    {
                                        resolved = ((ILTypeInstance)thisObj).Type.GetVirtualMethod(ilm);
                                    }
                                    else
                                    {
                                        if (thisObj is ILTypeInstance ilInst)
                                            resolved = ilInst.Type.GetVirtualMethod(target);
                                        else if (thisObj is CrossBindingAdaptorType adaptor)
                                            resolved = adaptor.ILInstance.Type.BaseType.GetVirtualMethod(target);
                                        else
                                            throw new InvalidOperationException("Neo ldvirtftn: unsupported this type " + thisObj.GetType().FullName);
                                    }
                                    int ldvDstRef = frameRefBase + ip->Operand;
                                    mStack[ldvDstRef] = resolved;
                                    *(int*)(frameBase + ip->DstOffset) = ldvDstRef;
                                    ip++;
                                    continue;
                                }
                            case OpCodeREnum.Call_Redirect:
                                {
                                    // Step 19: a CLR static redirect call (e.g.
                                    // System.Delegate.Combine / Remove -- the C#
                                    // `+=` / `-=` multicast lowering). The JIT
                                    // lowers it like a Call with the CLRMethod's
                                    // RedirectionNeo; the optimizer builds the same
                                    // NeoCallParamMap as for Call. Route through
                                    // InvokeNeoClrMethod (the redirect owns its dest
                                    // write). The isNewobj/needPop flags are in
                                    // Operand4 (mirrors Legacy :2807-2846).
                                    var targetMethod = AppDomain.GetMethod(ip->Operand2) as CLRMethod;
                                    if (targetMethod == null)
                                    {
                                        ip++;
                                        continue;
                                    }
                                    int crParamIdx = ip->Operand;
                                    ref var crMap = ref nf.NeoCallParams[crParamIdx];
                                    byte* crTargetBase = newEsp;
                                    CopyNeoCallArguments(ref crMap, frameBase, crTargetBase, mStack, AppDomain);

                                    bool crIsNewObj = (ip->Operand4 & 0x2) == 0x2;
                                    byte* crRetDstPtr = ip->Register1 >= 0 ? frameBase + ip->DstOffset : null;
                                    int crRetRefBase = ip->Register1 >= 0 ? frameRefBase + ip->Operand3 : -1;

                                    InvokeNeoClrMethod(targetMethod, crIsNewObj, crTargetBase, mStack, crRetDstPtr, crRetRefBase);

                                    ip++;
                                    continue;
                                }
                            case OpCodeREnum.Call:
                                {
                                    var targetMethod = AppDomain.GetMethod(ip->Operand2);
                                    if (targetMethod == null)
                                    {
                                        ip++;
                                        continue;
                                    }

                                    int callParamIdx = ip->Operand;
                                    ref var map = ref nf.NeoCallParams[callParamIdx];
                                    byte* targetBase = newEsp;
                                    CopyNeoCallArguments(ref map, frameBase, targetBase, mStack, AppDomain);

                                    // Reference parameters are passed as existing mStack indices in the frame bytes.
                                    // The primitive copy above has already copied those indices into targetBase.

                                    byte* retDstPtr = null;
                                    int targetRetRefBase = -1;

                                    if (ip->Register1 >= 0)
                                    {
                                        retDstPtr = frameBase + ip->DstOffset;
                                        targetRetRefBase = frameRefBase + ip->Operand3;
                                    }

                                    // neo-array-multidim-ilvt: an IL value-type-element multi-
                                    // dim array's Set/Get element call (lowered as a non-virtual
                                    // Call on the resolved CLR array type) must box/unbox the
                                    // element from the caller frame. Intercept before the generic
                                    // dispatch + the byref-snapshot machinery (which do not apply
                                    // to this call shape).
                                    if (TryNeoIlVtElementArrayCall(ip, targetMethod, ref map,
                                        frameBase, frameRefBase, targetBase, mStack, AppDomain,
                                        retDstPtr, targetRetRefBase))
                                    {
                                        ip++;
                                        continue;
                                    }

                                    // Step 20 fixer round 1: snapshot every write-back-flagged
                                    // byref source BEFORE the call. A VT-this instance method
                                    // whose byref source register is reused as the call dest
                                    // (the C# async-state-machine pattern `call r6, r6,
                                    // get_IsCompleted`) would otherwise have its byref bytes
                                    // clobbered by the result before CopyNeoCallThisBack reads
                                    // them. The snapshot is read by the snapshot-aware overload.
                                    int* byRefSnap = null;
                                    bool[] wbFlags = map.PrimitiveByRefWriteBack;
                                    bool needSnap = wbFlags != null && wbFlags.Length > 0;
                                    // neo-async-valuetask-asyncvoid fixer round 1: a per-call
                                    // `stackalloc` for the snapshot ACCUMULATES on the C# stack
                                    // across loop iterations (localloc never reclaims within a
                                    // frame), so a tight poll loop calling a VT-`this` instance
                                    // method (e.g. `while(!vt.IsCompleted)` -- the ValueTask<T>
                                    // probes) overflows the C# stack. A HEAP int[] is used instead:
                                    // it does NOT accumulate on the C# stack and is nesting-safe
                                    // (each nested Call gets its own array, so a Call that drives
                                    // MoveNext does not clobber its parent's snapshot). The array
                                    // is pinned for the duration of the call + write-back because
                                    // CopyNeoCallThisBack reads byRefSnap AFTER InvokeNeoCallTarget
                                    // (which may nest Calls). Trade-off: a small Gen0 alloc per
                                    // byref-writeback Call (only VT-`this` instance calls + ref/out
                                    // params hit this; ref-type-`this` calls do not).
                                    int[] snapArr = needSnap ? new int[(wbFlags.Length > 16 ? 16 : wbFlags.Length) * 2] : null;

                                    // F-7B-SIB: for a NON-inlined direct Call to an IL method, a
                                    // byref param's 8-byte Ref Slot was copied VERBATIM into
                                    // targetBase (the IL callee's byref is unflagged in the map, so
                                    // CopyNeoCallArguments copied it raw). Re-base/promote it so the
                                    // callee's stind/ldind resolve to the CALLER's frame cell (the
                                    // F-7/F-7B delegate-path mirror). A direct Call to a CLR method,
                                    // or an IL method with no byref param, is a no-op (the helper
                                    // returns false). NOTE: this runs ONLY for a non-inlined Call --
                                    // an inlined direct target is folded by the JIT inliner and never
                                    // reaches this opcode (the Step-17 inlined-byref cases are
                                    // byte-identical, unaffected).
                                    int f7bPromotedSlotOff = -1, f7bPromotedOrigObjIdx = -1, f7bPromotedOrigOff = 0;
                                    int f7bPromotedCallerSlot = -1, f7bRebasedSlotOff = -1, f7bRebasedOrigOff = 0;
                                    bool f7bTouched = false;
                                    if (targetMethod is ILMethod f7bIlm)
                                    {
                                        f7bTouched = NeoPreCallByrefFixup(f7bIlm, targetBase, frameBase, mStack,
                                            out f7bPromotedSlotOff, out f7bPromotedOrigObjIdx, out f7bPromotedOrigOff,
                                            out f7bPromotedCallerSlot, out f7bRebasedSlotOff, out f7bRebasedOrigOff);
                                    }

                                    if (needSnap)
                                    {
                                        fixed (int* snap = snapArr)
                                        {
                                            int captured = SnapshotNeoCallByRefSources(ref map, frameBase, snap);
                                            if (captured > 0)
                                                byRefSnap = snap;
                                            if (!InvokeNeoCallTarget(targetMethod, false, targetBase, mStack, retDstPtr, targetRetRefBase, out unhandledException))
                                                return null;
                                            if (f7bTouched)
                                            {
                                                if (f7bPromotedSlotOff >= 0)
                                                {
                                                    *(int*)(frameBase + f7bPromotedOrigOff) = f7bPromotedCallerSlot;
                                                    *(int*)(targetBase + f7bPromotedSlotOff + 0) = f7bPromotedOrigObjIdx;
                                                    *(int*)(targetBase + f7bPromotedSlotOff + 4) = f7bPromotedOrigOff;
                                                }
                                                if (f7bRebasedSlotOff >= 0)
                                                    *(int*)(targetBase + f7bRebasedSlotOff + 4) = f7bRebasedOrigOff;
                                            }
                                            CopyNeoCallThisBack(ref map, frameBase, targetBase, mStack, AppDomain, byRefSnap);
                                        }
                                    }
                                    else
                                    {
                                        if (!InvokeNeoCallTarget(targetMethod, false, targetBase, mStack, retDstPtr, targetRetRefBase, out unhandledException))
                                            return null;
                                        if (f7bTouched)
                                        {
                                            // F-7B: after a reference-byref target runs, stamp the caller
                                            // cell with the caller-owned slot index so the caller's
                                            // subsequent read resolves to the surviving object (the slot
                                            // sits below the callee's frameRefBase and survived the Ret
                                            // pop). Restore the raw frame-native byref in targetBase
                                            // (idempotent; a direct Call has one invocation, so no
                                            // multicast re-invocation, but the restore keeps targetBase
                                            // consistent for any downstream read).
                                            if (f7bPromotedSlotOff >= 0)
                                            {
                                                *(int*)(frameBase + f7bPromotedOrigOff) = f7bPromotedCallerSlot;
                                                *(int*)(targetBase + f7bPromotedSlotOff + 0) = f7bPromotedOrigObjIdx;
                                                *(int*)(targetBase + f7bPromotedSlotOff + 4) = f7bPromotedOrigOff;
                                            }
                                            // F-7: undo the primitive/value-byref rebase so targetBase
                                            // holds the original caller-relative offset again.
                                            if (f7bRebasedSlotOff >= 0)
                                                *(int*)(targetBase + f7bRebasedSlotOff + 4) = f7bRebasedOrigOff;
                                        }
                                        // Step 13 Area 4b: propagate a value-type instance `this`
                                        // mutation (ctor / mutating instance method) back to the
                                        // caller's in-frame local. No-op for non-mutating calls
                                        // and for non-VT-`this` calls (empty PrimitiveByRefSrc).
                                        CopyNeoCallThisBack(ref map, frameBase, targetBase, mStack, AppDomain, byRefSnap);
                                    }

                                    ip++;
                                    continue;
                                }
                            case OpCodeREnum.Newobj:
                                {
                                    var targetMethod = AppDomain.GetMethod(ip->Operand2);
                                    if (targetMethod == null)
                                    {
                                        ip++;
                                        continue;
                                    }

                                    int callParamIdx = ip->Operand;
                                    ref var map = ref nf.NeoCallParams[callParamIdx];
                                    byte* targetBase = newEsp;

                                    // dest register layout (stamped by the Optimizer
                                    // Newobj lowering from localInfos[op.Register1]).
                                    dstRefOffset = ip->Operand3;
                                    int newobjDstIdx = frameRefBase + dstRefOffset;
                                    byte* retDstPtr = frameBase + ip->DstOffset;

                                    var ilNewobjType = targetMethod.DeclearingType as ILType;
                                    if (ilNewobjType == null)
                                    {
                                        // Step 18 (D3): CLR-type newobj. Route to
                                        // InvokeNeoClrMethod(isNewobj:true). The dest is a
                                        // reference temp (4-byte mStack index + 1 ref slot),
                                        // same shape as the IL ref-type newobj dest.
                                        // InvokeNeoClrMethod stores the reflection-created
                                        // object into the dest mStack ref slot + writes the
                                        // index to the dest byte offset (the redirect path
                                        // owns its own dest write).
                                        if (targetMethod.DeclearingType is CLRType clrDeclType)
                                        {
                                            // Step 19: CLR delegate newobj (the common
                                            // Action<>/Func<> case -- these are CLR types).
                                            // Read `this`(target) + the IMethod(fnptr) and build
                                            // the DelegateAdapter via DelegateManager, mirroring
                                            // Legacy `ILIntepreter.Register.cs:3539-3561`.
                                            if (clrDeclType.IsDelegate)
                                            {
                                                CopyNeoCallArguments(ref map, frameBase, targetBase, mStack, AppDomain);
                                                // DUMP-confirmed (NeoStep19_StaticAction): the CLR
                                                // delegate ctor's NeoCallParamMap has 2 entries --
                                                // [0] = target (object, 4-byte mStack index),
                                                // [1] = fnptr (IntPtr, 8-byte slot whose first 4
                                                // bytes hold the IMethod's mStack index). The map
                                                // does NOT include the `this` slot (the Newobj
                                                // lowering reserves it in paramInfos but the map
                                                // only carries the actual args).
                                                int dtargetOff = map.PrimitiveDst[0];
                                                int dmethodOff = map.PrimitiveDst[1];
                                                int dTargetIdx = *(int*)(targetBase + dtargetOff);
                                                int dMethodIdx = *(int*)(targetBase + dmethodOff);
                                                object dIns = (dTargetIdx >= 0) ? mStack[dTargetIdx] : null;
                                                IMethod dMi = (IMethod)mStack[dMethodIdx];
                                                object dele;
                                                var dIlMethod = dMi as ILMethod;
                                                if (dIlMethod != null)
                                                {
                                                    dele = AppDomain.DelegateManager.FindDelegateAdapter(clrDeclType, dIns as ILTypeInstance, dIlMethod);
                                                }
                                                else
                                                {
                                                    object clrTarget = dIns;
                                                    if (clrTarget is ILTypeInstance ilti)
                                                        clrTarget = ilti.CLRInstance;
                                                    dele = Delegate.CreateDelegate(clrDeclType.TypeForCLR, clrTarget, ((CLRMethod)dMi).MethodInfo);
                                                }
                                                mStack[newobjDstIdx] = dele;
                                                *(int*)retDstPtr = newobjDstIdx;
                                                ip++;
                                                continue;
                                            }
                                            CopyNeoCallArguments(ref map, frameBase, targetBase, mStack, AppDomain);
                                            var clrCtor = targetMethod as CLRMethod;
                                            InvokeNeoClrMethod(clrCtor, true, targetBase, mStack, retDstPtr, newobjDstIdx);

                                            ip++;
                                            continue;
                                        }
                                        // Delegate / unknown: keep the Step 19 NIE.
                                        throw new NotImplementedException("Neo Newobj CLR type is not implemented (Step 9)");
                                    }

                                    if (ilNewobjType.IsDelegate)
                                    {
                                        // Step 19: IL-defined delegate newobj. Same shape
                                        // as the CLR delegate newobj above -- read target +
                                        // IMethod from the param-region copy (the optimizer
                                        // built a NeoCallParamMap: map[0]=target, map[1]=
                                        // fnptr), build the adapter via DelegateManager (the
                                        // IL-overload FindDelegateAdapter), store it.
                                        CopyNeoCallArguments(ref map, frameBase, targetBase, mStack, AppDomain);
                                        int ildTargetOff = map.PrimitiveDst[0];
                                        int ildMethodOff = map.PrimitiveDst[1];
                                        int ildTargetIdx = *(int*)(targetBase + ildTargetOff);
                                        int ildMethodIdx = *(int*)(targetBase + ildMethodOff);
                                        object ildIns = (ildTargetIdx >= 0) ? mStack[ildTargetIdx] : null;
                                        IMethod ildMi = (IMethod)mStack[ildMethodIdx];
                                        var ildIlMethod = ildMi as ILMethod;
                                        if (ildIlMethod == null)
                                            throw new NotImplementedException("Neo IL-delegate Newobj: non-ILMethod target is not supported (Step 19)");
                                        object dele;
                                        if (ildIns != null)
                                        {
                                            var ilIns = (ILTypeInstance)ildIns;
                                            dele = ilIns.GetDelegateAdapter(ildIlMethod);
                                            if (dele == null)
                                            {
                                                var invokeMethod = ilNewobjType.GetMethod("Invoke", ildMi.ParameterCount);
                                                if (invokeMethod == null && ildIlMethod.IsExtend)
                                                    invokeMethod = ilNewobjType.GetMethod("Invoke", ildMi.ParameterCount - 1);
                                                dele = AppDomain.DelegateManager.FindDelegateAdapter(ilIns, ildIlMethod, invokeMethod);
                                            }
                                        }
                                        else
                                        {
                                            if (ildIlMethod.DelegateAdapter == null)
                                            {
                                                var invokeMethod = ilNewobjType.GetMethod("Invoke", ildMi.ParameterCount);
                                                ildIlMethod.DelegateAdapter = AppDomain.DelegateManager.FindDelegateAdapter(null, ildIlMethod, invokeMethod);
                                            }
                                            dele = ildIlMethod.DelegateAdapter;
                                        }
                                        mStack[newobjDstIdx] = dele;
                                        *(int*)retDstPtr = newobjDstIdx;
                                        ip++;
                                        continue;
                                    }

                                    if (ilNewobjType.IsValueType && !ilNewobjType.IsPrimitive && !ilNewobjType.IsEnum)
                                    {
                                        // VT-THIS-ADDR (resolves Step 18 D1 / Q-VT-NEWOBJ): IL
                                        // value-type newobj via the copy-back pattern, adapted
                                        // from Legacy `*reg1 = *ins` to the Neo frame model.
                                        //
                                        // The callee ctor's `this` (param slot 0) is laid out
                                        // and typed as the in-frame VT value
                                        // (AllocateLocalStackSpaces sizes ParamInfos[0] as
                                        // TotalPrimitiveSize/TotalReferenceCount; the type-spec
                                        // pass seeds registerTypes[0] = declaringType), so the
                                        // ctor's `this.field =` lowers to `_Inline` and writes
                                        // the CALLEE frame's slot-0 byte/ref region. The Neo
                                        // frame-native Ref-Slot zero-copy mechanism does NOT
                                        // apply here because the `_Inline` arm writes through
                                        // the owning slot's frame bytes directly (it does not
                                        // dereference a Ref Slot), and the caller's dest region
                                        // is a SEPARATE frame buffer from the callee's slot-0
                                        // region. So: zero-init the caller's dest, copy the
                                        // caller's dest region INTO the callee's slot-0 region
                                        // (pre-call, so a ctor reading an existing field sees
                                        // the zero/default), copy the ctor args to slots [1..],
                                        // invoke, then copy the callee's slot-0 region BACK to
                                        // the caller's dest region (post-call, picking up the
                                        // ctor's writes). No heap ILTypeInstance; no mStack
                                        // `this` push; the inline-stfld seeding fix above makes
                                        // every same-owner `this.field=` resolve consistently.
                                        var ilCtor = targetMethod as ILMethod;
                                        var ctorFrame = ilCtor.CompiledFrame;
                                        // Resolve the callee slot-0 (this) primitive offset.
                                        // For a HasThis VT ctor ParamInfos[0] is the in-frame
                                        // value (the ref offset / count are read inside
                                        // ExecuteNeo's Ret-arm copy-back via ParamInfos[0]).
                                        var thisSlot = ctorFrame.ParamInfos[0];
                                        int thisPrimOff = thisSlot.Offset;

                                        int vtPrimSize = ilNewobjType.TotalPrimitiveSize;
                                        int vtRefCount = ilNewobjType.TotalReferenceCount;

                                        // 1) Zero-init the caller's dest region (prim + refs)
                                        //    so a ctor that sets only SOME fields leaves the
                                        //    rest at the default (matches Initobj semantics).
                                        if (vtPrimSize > 0)
                                            Unsafe.InitBlock(frameBase + ip->DstOffset, 0, (uint)vtPrimSize);
                                        for (int i = 0; i < vtRefCount; i++)
                                            mStack[newobjDstIdx + i] = null;

                                        // 2) Seed the callee's slot-0 PRIMITIVE bytes with the
                                        //    caller's dest prim bytes (pre-call). ExecuteNeo
                                        //    reserves the callee's slot-0 REF region as nulls
                                        //    itself (the in-frame-VT zero-init); the ctor's ref
                                        //    writes to slot-0 are copied back to the caller's
                                        //    dest by ExecuteNeo's cleanup BEFORE the mStack pop
                                        //    (see the vtNewobjCallerDst path in ExecuteNeo).
                                        if (vtPrimSize > 0)
                                            Unsafe.CopyBlock(targetBase + thisPrimOff, frameBase + ip->DstOffset, (uint)vtPrimSize);

                                        // 3) Copy the remaining ctor args (slots [1..]). The
                                        //    lowering built the NeoCallParamMap skipping slot 0.
                                        CopyNeoCallArguments(ref map, frameBase, targetBase, mStack, AppDomain);

                                        // 4) Invoke the ctor directly via ExecuteNeo (not
                                        //    InvokeNeoCallTarget) so we can pass the caller's dest
                                        //    region for the slot-0 -> dest copy-back. ExecuteNeo
                                        //    performs that copy-back in its cleanup BEFORE popping
                                        //    the callee's mStack reservation -- the ref half MUST
                                        //    be copied before the pop (the slot-0 ref entries are
                                        //    removed by the pop). retDst=null: a constructor has
                                        //    no return value (the dest is filled via the slot-0
                                        //    copy-back, not via a return write).
                                        ExecuteNeo(ilCtor, targetBase, null, -1, out unhandledException,
                                            frameBase + ip->DstOffset, newobjDstIdx, vtPrimSize, vtRefCount);
                                        if (unhandledException)
                                            return null;

                                        ip++;
                                        continue;
                                    }

                                    // IL reference-type newobj (Step 8b).
                                    ins = ilNewobjType.Instantiate(false);
                                    // Re-base an aliased reference arg BEFORE storing the new
                                    // instance. When the newobj dest register aliases a reference
                                    // argument register (the canonical eval-stack lowering of
                                    // `ldstr/ldloc arg; newobj(arg)` reuses the arg's register as
                                    // the dest), the dest ref slot (newobjDstIdx = frameRefBase +
                                    // dstRefOffset) coincides with that arg's own ref slot, so the
                                    // arg's mStack index EQUALS newobjDstIdx. Storing the instance
                                    // at mStack[newobjDstIdx] would then overwrite the arg object
                                    // before CopyNeoCallArguments copies it to the ctor, so the
                                    // ctor would receive the new instance as the aliased argument
                                    // (e.g. TestStaticFieldInstance: `new TestA("testerror")` ctor
                                    // saw `this` as the `name` arg -> InvalidCastException). Detect
                                    // the alias via the ref map (the dest register's ref offset
                                    // dstRefOffset appears as a reference-arg source) -- a
                                    // primitive int arg whose VALUE coincidentally equals
                                    // newobjDstIdx is NOT re-based (its register has no ref slot).
                                    // Re-base the colliding arg object to a fresh mStack slot and
                                    // rewrite the source so the copy hands the ctor the arg, not
                                    // the instance.
                                    if (map.RefSrc != null && map.PrimitiveSrc != null)
                                    {
                                        bool destAliasesRefArg = false;
                                        for (int ri = 0; ri < map.RefSrc.Length; ri++)
                                            if (map.RefSrc[ri] == dstRefOffset) { destAliasesRefArg = true; break; }
                                        if (destAliasesRefArg)
                                        {
                                            int aIdx = *(int*)(frameBase + ip->DstOffset);
                                            if (aIdx >= 0 && aIdx == newobjDstIdx)
                                            {
                                                mStack.Add(mStack[aIdx]);
                                                *(int*)(frameBase + ip->DstOffset) = mStack.Count - 1;
                                            }
                                        }
                                    }
                                    mStack[newobjDstIdx] = ins;

                                    *(int*)targetBase = newobjDstIdx;
                                    CopyNeoCallArguments(ref map, frameBase, targetBase, mStack, AppDomain);
                                    *(int*)retDstPtr = newobjDstIdx;

                                    int targetRetRefBase = frameRefBase + dstRefOffset;
                                    mStack.Add(mStack[newobjDstIdx]); // push 'this'

                                    if (!InvokeNeoCallTarget(targetMethod, true, targetBase, mStack, null, targetRetRefBase, out unhandledException))
                                        return null;

                                    ip++;
                                    continue;
                                }
                            case OpCodeREnum.Callvirt_IL:
                                {
                                    var targetMethod = AppDomain.GetMethod(ip->Operand2);
                                    if (targetMethod == null)
                                    {
                                        ip++;
                                        continue;
                                    }

                                    int callParamIdx = ip->Operand;
                                    ref var map = ref nf.NeoCallParams[callParamIdx];
                                    byte* targetBase = newEsp;
                                    CopyNeoCallArguments(ref map, frameBase, targetBase, mStack, AppDomain);

                                    byte* retDstPtr = ip->Register1 >= 0 ? frameBase + ip->DstOffset : null;
                                    int targetRetRefBase = ip->Register1 >= 0 ? frameRefBase + ip->Operand3 : -1;

                                    // Step 19: an IL-defined delegate's Invoke callvirt
                                    // (`del(args)`) -- the `this` is the IDelegateAdapter
                                    // built at the delegate newobj. Route to the adapter's
                                    // NeoInvoke (mirrors Legacy's IsDelegateInvoke ->
                                    // IDelegateAdapter.ILInvoke). The adapter's own method
                                    // + instance carry the target; the call args are the
                                    // delegate Invoke's explicit params.
                                    if (targetMethod.IsDelegateInvoke)
                                    {
                                        object delThis = mStack[*(int*)targetBase];
                                        if (delThis is DelegateAdapter dAdapter)
                                        {
                                            // F-7 (NEO-DELEGATE-REFOUT): for an IL delegate target,
                                            // run it on THIS interpreter (same-frame fast path) via
                                            // NeoRunDelegateTargetOnThis, which re-bases frame-native
                                            // byref offsets so a ref/out param's write-back lands in
                                            // the caller's frame cell. The old ReadNeoDelegateInvokeArgs
                                            // + NeoInvokePublic path half-read the byref through object[]
                                            // and ran the target on a SEPARATE pooled interpreter, so a
                                            // ref/out param could neither be read nor written back. The
                                            // fallback (ReadNeoDelegateInvokeArgs) stays for a non-IL
                                            // delegate target (D5).
                                            if (dAdapter.Method is ILMethod dTargetIlm)
                                            {
                                                int headShift = dTargetIlm.HasThis ? 0 : 4;
                                                // F-7B: caller-owned mStack slot for a reference-byref,
                                                // reserved ONCE and shared across the multicast chain
                                                // (last write wins, D3). -1 = not yet reserved.
                                                int f7bCallerOwnedRefSlot = -1;
                                                if (!NeoRunDelegateTargetOnThis(dTargetIlm, dAdapter.Instance,
                                                    targetBase, headShift, frameBase, mStack,
                                                    retDstPtr, targetRetRefBase, ref f7bCallerOwnedRefSlot, out unhandledException))
                                                    return null;

                                                // D3: multicast next-chain. Each subsequent IL target re-
                                                // runs the same fast path. The byref in targetBase points
                                                // at the caller cell, now holding the head's mutation, so
                                                // the next target sees the prior target's write (C#
                                                // multicast-byref semantics). Last target's return wins
                                                // (Legacy ILInvokeSub semantics).
                                                IDelegateAdapter nxt = dAdapter.Next;
                                                while (nxt != null)
                                                {
                                                    if (nxt is DelegateAdapter nAdapter && nAdapter.Method is ILMethod nIlm)
                                                    {
                                                        int nShift = nIlm.HasThis ? 0 : 4;
                                                        if (!NeoRunDelegateTargetOnThis(nIlm, nAdapter.Instance,
                                                            targetBase, nShift, frameBase, mStack,
                                                            retDstPtr, targetRetRefBase, ref f7bCallerOwnedRefSlot, out unhandledException))
                                                            return null;
                                                        nxt = nAdapter.Next;
                                                    }
                                                    else
                                                    {
                                                        // Non-IL target in the chain: fall back to the
                                                        // separate-interpreter path for this node.
                                                        object[] delArgs = ReadNeoDelegateInvokeArgs(targetMethod, targetBase, mStack);
                                                        object delRes = ((DelegateAdapter)nxt).NeoInvokePublic(delArgs);
                                                        WriteNeoDelegateInvokeReturn(targetMethod, delRes, retDstPtr, mStack, targetRetRefBase);
                                                        nxt = nxt.Next;
                                                    }
                                                }

                                                ip++;
                                                continue;
                                            }
                                            else
                                            {
                                                object[] delArgs = ReadNeoDelegateInvokeArgs(targetMethod, targetBase, mStack);
                                                object delRes = dAdapter.NeoInvokePublic(delArgs);
                                                WriteNeoDelegateInvokeReturn(targetMethod, delRes, retDstPtr, mStack, targetRetRefBase);
                                                ip++;
                                                continue;
                                            }
                                        }
                                    }

                                    IMethod actualMethod = ResolveNeoCallvirtILTarget(ip, targetMethod, targetBase, mStack);

                                    if (!InvokeNeoCallTarget(actualMethod, false, targetBase, mStack, retDstPtr, targetRetRefBase, out unhandledException))
                                        return null;

                                    ip++;
                                    continue;
                                }
                            case OpCodeREnum.Callvirt_CLR:
                                {
                                    var targetMethod = AppDomain.GetMethod(ip->Operand2);
                                    if (targetMethod == null)
                                    {
                                        ip++;
                                        continue;
                                    }

                                    int callParamIdx = ip->Operand;
                                    ref var map = ref nf.NeoCallParams[callParamIdx];
                                    byte* targetBase = newEsp;
                                    CopyNeoCallArguments(ref map, frameBase, targetBase, mStack, AppDomain);

                                    byte* retDstPtr = ip->Register1 >= 0 ? frameBase + ip->DstOffset : null;
                                    int targetRetRefBase = ip->Register1 >= 0 ? frameRefBase + ip->Operand3 : -1;
                                    // neo-array-multidim-ilvt: an IL value-type-element
                                    // multi-dim array's Set/Get must box/unbox the element
                                    // from the caller frame (the reflection reader mis-reads
                                    // the IL-VT element param). Intercept before the generic
                                    // CLR dispatch. retDstPtr is non-null for Get; for Set it
                                    // is null (void) but the helper keys on the method name.
                                    if (TryNeoIlVtElementArrayCall(ip, targetMethod, ref map,
                                        frameBase, frameRefBase, targetBase, mStack, AppDomain,
                                        retDstPtr, targetRetRefBase))
                                    {
                                        ip++;
                                        continue;
                                    }
                                    CLRMethod clrMethod = ResolveNeoCallvirtCLRTarget(ip, targetMethod, targetBase, mStack);
                                    InvokeNeoClrMethod(clrMethod, false, targetBase, mStack, retDstPtr, targetRetRefBase);

                                    ip++;
                                    continue;
                                }
                            case OpCodeREnum.Callvirt_Interface:
                                {
                                    var targetMethod = AppDomain.GetMethod(ip->Operand2);
                                    if (targetMethod == null)
                                    {
                                        ip++;
                                        continue;
                                    }

                                    int callParamIdx = ip->Operand;
                                    ref var map = ref nf.NeoCallParams[callParamIdx];
                                    byte* targetBase = newEsp;
                                    CopyNeoCallArguments(ref map, frameBase, targetBase, mStack, AppDomain);

                                    byte* retDstPtr = ip->Register1 >= 0 ? frameBase + ip->DstOffset : null;
                                    int targetRetRefBase = ip->Register1 >= 0 ? frameRefBase + ip->Operand3 : -1;
                                    IMethod actualMethod = ResolveNeoCallvirtInterfaceTarget(ip, targetMethod, targetBase, mStack);

                                    if (!InvokeNeoCallTarget(actualMethod, false, targetBase, mStack, retDstPtr, targetRetRefBase, out unhandledException))
                                        return null;

                                    ip++;
                                    continue;
                                }
                            case OpCodeREnum.Callvirt:
                                {
                                    var targetMethod = AppDomain.GetMethod(ip->Operand2);
                                    if (targetMethod == null)
                                    {
                                        ip++;
                                        continue;
                                    }

                                    int callParamIdx = ip->Operand;
                                    ref var map = ref nf.NeoCallParams[callParamIdx];
                                    byte* targetBase = newEsp;
                                    CopyNeoCallArguments(ref map, frameBase, targetBase, mStack, AppDomain);

                                    byte* retDstPtr = ip->Register1 >= 0 ? frameBase + ip->DstOffset : null;
                                    int targetRetRefBase = ip->Register1 >= 0 ? frameRefBase + ip->Operand3 : -1;
                                    IMethod actualMethod = ResolveNeoGenericCallvirtTarget(ip, targetMethod, targetBase, mStack);

                                    if (!InvokeNeoCallTarget(actualMethod, false, targetBase, mStack, retDstPtr, targetRetRefBase, out unhandledException))
                                        return null;

                                    ip++;
                                    continue;
                                }
                            case OpCodeREnum.Ret:
                                if (retDst != null && (returnPrimitiveSize > 0 || returnRefCount > 0))
                                {
                                    if (returnRefCount == 0)
                                    {
                                        if (returnPrimitiveSize > 0)
                                            Unsafe.CopyBlock(retDst, frameBase + ip->DstOffset, (uint)returnPrimitiveSize);
                                    }
                                    else
                                    {
                                        IType returnType = method.ReturnType;
                                        bool isSingleReferenceReturn = returnType != null &&
                                            !returnType.IsPrimitive &&
                                            !returnType.IsValueType &&
                                            returnPrimitiveSize == 4 &&
                                            returnRefCount == 1;
                                        if (!isSingleReferenceReturn)
                                        {
                                            // neo-ret-vt-with-ref-fields: a value-
                                            // type return WITH reference fields
                                            // (e.g. `struct S { int x; string s; }
                                            // S Make(){...}`). The return value
                                            // lives in a callee-frame register whose
                                            // primitive bytes are at
                                            // `frameBase + ip->DstOffset` and whose
                                            // ref slots are at
                                            // `mStack[frameRefBase + ip->Operand3]`
                                            // (ip->Operand3 = the return register's
                                            // RefOffset, stamped by LowerNeoOffsets).
                                            // Mirror Step 12b Move_Vt: byte CopyBlock
                                            // of returnPrimitiveSize to the caller's
                                            // dest + a ref-slot loop copying
                                            // returnRefCount slots from the callee's
                                            // return ref region to the caller's
                                            // retRefBase. Shallow copy: refs are
                                            // shared (C# struct-copy semantics).
                                            int retSrcRefOff = ip->Operand3;
                                            if (returnPrimitiveSize > 0)
                                                Unsafe.CopyBlock(retDst, frameBase + ip->DstOffset, (uint)returnPrimitiveSize);
                                            for (int i = 0; i < returnRefCount; i++)
                                                mStack[retRefBase + i] = mStack[frameRefBase + retSrcRefOff + i];
                                        }
                                        else
                                        {
                                            // Single reference-type return (a class /
                                            // ref-type return: returnPrimitiveSize==4,
                                            // returnRefCount==1). The return slot holds
                                            // an mStack index; copy that object to the
                                            // caller's retRefBase and write retRefBase
                                            // into the caller's dest.
                                            int retSrcIdx = *(int*)(frameBase + ip->DstOffset);
                                            if (retSrcIdx >= 0)
                                            {
                                                mStack[retRefBase] = mStack[retSrcIdx];
                                                *(int*)retDst = retRefBase;
                                            }
                                            else
                                            {
                                                mStack[retRefBase] = null;
                                                *(int*)retDst = -1;
                                            }
                                        }
                                    }
                                }
                                // VT-THIS-ADDR: when this ExecuteNeo invocation is a
                                // value-type ctor called from the runtime Newobj IL-VT
                                // branch (vtNewobjCallerDst != null), copy the slot-0
                                // (this) region back to the caller's dest BEFORE the
                                // mStack pop below -- the slot-0 ref entries are removed
                                // by the pop, so the ref half MUST be copied first.
                                if (vtNewobjCallerDst != null)
                                {
                                    var ctorFrameR = method.CompiledFrame;
                                    var thisSlotR = ctorFrameR.ParamInfos[0];
                                    int thisPrimOffR = thisSlotR.Offset;
                                    int thisRefOffR = thisSlotR.RefOffset;
                                    if (vtNewobjCallerPrimSize > 0)
                                        Unsafe.CopyBlock(vtNewobjCallerDst, frameBase + thisPrimOffR, (uint)vtNewobjCallerPrimSize);
                                    for (int i = 0; i < vtNewobjCallerRefCount; i++)
                                        mStack[vtNewobjCallerDstRefBase + i] = mStack[frameRefBase + thisRefOffR + i];
                                }
                                mStack.RemoveRange(frameRefBase, mStack.Count - frameRefBase);
                                returned = true;
                                continue;
                            case OpCodeREnum.Initobj:
                                t = AppDomain.GetType(ip->Operand);
                                ilType = t as ILType;
                                if (ilType != null)
                                {
                                    refCnt = 0;
                                    if (ilType.IsEnum)
                                        sz = AppDomain.GetPrimitiveSize(ilType.FieldTypes[0]);
                                    else if (ilType.IsPrimitive)
                                        sz = AppDomain.GetPrimitiveSize(ilType);
                                    else if (ilType.IsValueType)
                                    {
                                        sz = ilType.TotalPrimitiveSize;
                                        refCnt = ilType.TotalReferenceCount;
                                    }
                                    else
                                    {
                                        // Reference type initobj → write null index (-1) into the byte slot.
                                        *(int*)(frameBase + ip->DstOffset) = -1;
                                        break;
                                    }
                                    if (sz > 0)
                                        Unsafe.InitBlock(frameBase + ip->DstOffset, 0, (uint)sz);
                                    if (refCnt > 0)
                                    {
                                        // Step 12: null the in-frame VT's reference-
                                        // field mStack slots. ip->Operand3 carries
                                        // the target slot's RefOffset (stamped by
                                        // the Neo offset-lowering pass).
                                        int slotRefOffset = ip->Operand3;
                                        for (int i = 0; i < refCnt; i++)
                                            mStack[frameRefBase + slotRefOffset + i] = null;
                                    }
                                }
                                else
                                {
                                    // Step 13 / F-MAJ-1 review-fix: CLR value type Initobj.
                                    // A Neo CLR value-type LOCAL (struct OR enum) is stored as
                                    // FLAT MANAGED BYTES (Size = GetNeoValueTypeManagedSize,
                                    // RefCount = 0, isRef = false; see
                                    // JITCompiler.AllocateLocalStackSpaces CLR-VT branch under
                                    // ENABLE_NEO_MODE). Initobj therefore ZEROES the flat-bytes
                                    // region -- mirroring the IL-VT Initobj branch above and the
                                    // CLR-primitive branch below -- and does NOT touch mStack /
                                    // RefOffset (there is no ref slot: RefCount = 0). Zeroing is
                                    // correct for both pure-primitive CLR structs (default = all
                                    // fields zero) and CLR enums (default = underlying zero).
                                    CLRType clrInitType = t as CLRType;
                                    if (clrInitType == null)
                                    {
                                        // Unknown CLR type Initobj: write null index.
                                        *(int*)(frameBase + ip->DstOffset) = -1;
                                        break;
                                    }
                                    if (clrInitType.IsPrimitive)
                                    {
                                        // CLR primitive local: flat bytes; zero them.
                                        int psz = AppDomain.GetPrimitiveSize(clrInitType);
                                        if (psz > 0)
                                            Unsafe.InitBlock(frameBase + ip->DstOffset, 0, (uint)psz);
                                    }
                                    else if (!clrInitType.IsValueType)
                                    {
                                        // Reference-type CLR local Initobj: write null index.
                                        *(int*)(frameBase + ip->DstOffset) = -1;
                                    }
                                    else
                                    {
                                        // CLR struct OR CLR enum local: flat bytes; zero them.
                                        // (Pre-F-MAJ-1 this arm installed a boxed default into
                                        // mStack[frameRefBase+RefOffset]; with RefCount=0 the
                                        // stamped RefOffset is STALE -- it belongs to a
                                        // neighbouring ref slot -- so the write silently
                                        // corrupted that neighbour. The flat-bytes region is
                                        // already zeroed by frame init, but Initobj must still
                                        // re-zero it for the `v = default(T)` re-init case.)
                                        int csz = Optimizer.GetNeoValueTypeManagedSize(clrInitType.TypeForCLR);
                                        if (csz > 0)
                                            Unsafe.InitBlock(frameBase + ip->DstOffset, 0, (uint)csz);
                                    }
                                }
                                break;
                            case OpCodeREnum.Box:
                                dstRefOffset = ip->Operand3;
                                srcRefOffset = (short)ip->Operand4;
                                t = AppDomain.GetType(ip->Operand);
                                ilType = t as ILType;
                                if (ilType != null)
                                {
                                    if (ilType.IsEnum)
                                    {
                                        ins = new ILEnumTypeInstance(ilType);
                                        sz = AppDomain.GetPrimitiveSize(ilType.FieldTypes[0]);
                                        if (sz > 0)
                                        {
                                            ref byte dstP = ref MemoryMarshal.GetReference(ins.Primitives.AsSpan());
                                            Unsafe.CopyBlock(ref dstP, ref *(frameBase + ip->SrcOffset), (uint)sz);
                                        }
                                    }
                                    else if (ilType.IsPrimitive)
                                    {
                                        // Boxing a primitive IL type isn't a regular path
                                        // (compiler usually boxes CLR primitives), but handle for completeness.
                                        ins = ilType.Instantiate(false);
                                        sz = AppDomain.GetPrimitiveSize(ilType);
                                        if (sz > 0 && ins.Primitives != null)
                                        {
                                            ref byte dstP = ref MemoryMarshal.GetReference(ins.Primitives.AsSpan());
                                            Unsafe.CopyBlock(ref dstP, ref *(frameBase + ip->SrcOffset), (uint)sz);
                                        }
                                    }
                                    else if (ilType.IsValueType)
                                    {
                                        ins = ilType.Instantiate(false);
                                        CopyFrameToIL(frameBase, ip->SrcOffset, srcRefOffset,
                                                      ilType.TotalPrimitiveSize, ilType.TotalReferenceCount,
                                                      mStack, frameRefBase, ins);
                                    }
                                    else
                                    {
                                        // Boxing a reference type is a no-op: the same instance flows through.
                                        srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                        obj = srcIdx >= 0 ? mStack[srcIdx] : null;
                                        dstIdx = frameRefBase + dstRefOffset;
                                        mStack[dstIdx] = obj;
                                        *(int*)(frameBase + ip->DstOffset) = obj != null ? dstIdx : -1;
                                        break;
                                    }
                                    ins.Boxed = true;
                                    dstIdx = frameRefBase + dstRefOffset;
                                    mStack[dstIdx] = ins;
                                    *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                }
                                else
                                {
                                    // Step 13: CLR Box.
                                    CLRType clrBoxType = t as CLRType;
                                    if (clrBoxType == null)
                                        throw new InvalidCastException();
                                    object boxed;
                                    if (clrBoxType.IsPrimitive)
                                    {
                                        // CLR primitives are stored as flat bytes in the
                                        // frame; box reads the sized value and boxes it.
                                        boxed = NeoBoxReturnValue(clrBoxType, frameBase + ip->SrcOffset,
                                            AppDomain.GetPrimitiveSize(clrBoxType));
                                    }
                                    else if (!clrBoxType.TypeForCLR.IsValueType)
                                    {
                                        // Gap B: box on a CLR REFERENCE type is an IDENTITY
                                        // (ECMA III.4.3: boxing a reference type is a no-op --
                                        // the same instance flows through). The C# compiler
                                        // emits `box !!T` to flow a generic param T into an
                                        // `object`/base-class param; when T is specialized to a
                                        // concrete ref type (e.g. string), this arm fires. The
                                        // source slot holds the object's mStack index in its
                                        // first 4 bytes -- read the object as-is (NO
                                        // ReadNeoValueType, which would reinterpret the raw
                                        // bytes as a struct). Mirrors Legacy ExecuteR Box
                                        // (ILIntepreter.Register.cs:3880-3883: obj =
                                        // mStack[objRef->Value]; AssignToRegister). The F-MAJ-1
                                        // comment below assumed a ref-type source "never reaches
                                        // Box"; that holds for direct re-boxing of a known-ref
                                        // expression but NOT for a generic-param `box !!T`.
                                        int boxSrcIdx = *(int*)(frameBase + ip->SrcOffset);
                                        boxed = boxSrcIdx >= 0 ? mStack[boxSrcIdx] : null;
                                    }
                                    else
                                    {
                                        // F-MAJ-1 review-fix: a Neo CLR value-type (struct OR
                                        // enum) LOCAL is stored as FLAT MANAGED BYTES (Size =
                                        // GetNeoValueTypeManagedSize, RefCount = 0; see
                                        // JITCompiler.AllocateLocalStackSpaces CLR-VT branch
                                        // under ENABLE_NEO_MODE). Box reads the flat bytes and
                                        // boxes them via the cached typed reader
                                        // (ReadNeoValueType -> Unsafe.ReadUnaligned<T> + Box),
                                        // which yields an INDEPENDENT boxed copy, preserving
                                        // value semantics. (Pre-F-MAJ-1 this arm read a 4-byte
                                        // mStack index from the flat bytes -- garbage as an
                                        // index -> wrong object / OOB.) The Box opcode's source
                                        // is always a value-typed operand, so the flat-bytes
                                        // read is correct here unconditionally; the boxed-REF
                                        // source shape (an already-boxed struct typed as
                                        // object) never reaches Box (re-boxing an object is a
                                        // compiler no-op) -- that case is handled by the
                                        // Isinst/Castclass arms instead.
                                        int bsz = Optimizer.GetNeoValueTypeManagedSize(clrBoxType.TypeForCLR);
                                        int boxOff = ip->SrcOffset;
                                        boxed = ReadNeoValueType(clrBoxType.TypeForCLR, frameBase, ref boxOff, bsz);
                                    }
                                    dstIdx = frameRefBase + dstRefOffset;
                                    mStack[dstIdx] = boxed;
                                    *(int*)(frameBase + ip->DstOffset) = boxed != null ? dstIdx : -1;
                                }
                                break;
                            // Raw Ldfld: the field's DECLARING type is a CLRType. The Neo
                            // typed-splitter rewrites ldfld into a typed Ldfld_* arm ONLY for
                            // an ILType declaring type; for a CLR declaring type the JIT's
                            // `else` branch leaves this raw opcode with
                            // OperandLong = (typeHash<<32)|fieldHash (identical to Legacy's raw
                            // Ldfld). Decode field identity, resolve the owner from the SrcOffset
                            // slot (== DstOffset for Ldfld; R1==R2), read the CLR field, and push
                            // the value to DstOffset. Owner shape splits on the declaring type's
                            // value/ref kind: a CLR value-type owner is inline FLAT BYTES (ldloc
                            // by-value) -> box the whole struct + reflection GetValue; a CLR
                            // reference-type owner is a boxed mStack object -> Area 4d accessor.
                            // (Mirrors the Stobj/Ldobj byref resolution + Step 13 Area 4d helpers.)
                            case OpCodeREnum.Ldfld:
                                {
                                    int typeHash = (int)((ulong)ip->OperandLong >> 32);
                                    int fieldHash = (int)ip->OperandLong;
                                    var declType = AppDomain.GetType(typeHash);
                                    var ct = declType as CLRType;
                                    if (ct == null)
                                        throw new NotImplementedException("Neo raw Ldfld: declaring type " + (declType == null ? "<null>" : declType.FullName) + " is not a CLRType (raw opcode expected only for CLR-declaring-type fields)");
                                    var f = ct.GetField(fieldHash);
                                    if (f == null)
                                        throw new NotImplementedException("Neo raw Ldfld: CLR field hash " + fieldHash + " not resolved on type " + ct.FullName);
                                    Type fldClrType = f.FieldType;
                                    int ownerOff = ip->SrcOffset; // == DstOffset (Ldfld R1==R2)
                                    object fldVal;
                                    if (ct.TypeForCLR.IsValueType)
                                    {
                                        // CLR value-type owner loaded by value: the owner slot
                                        // holds the struct's FLAT MANAGED BYTES (not a byref, not
                                        // an mStack index). Box the whole struct and reflection-
                                        // read the field (handles primitive / nested-struct / ref
                                        // fields uniformly; a struct with unmappable ref fields
                                        // NIEs inside ReadNeoValueType -- the Step-13b sibling).
                                        int ownerSz = Optimizer.GetNeoValueTypeManagedSize(ct.TypeForCLR);
                                        int cur = ownerOff;
                                        object boxedOwner = ILIntepreter.ReadNeoValueType(ct.TypeForCLR, frameBase, ref cur, ownerSz);
                                        fldVal = f.GetValue(boxedOwner);
                                    }
                                    else
                                    {
                                        // CLR reference-type owner: owner slot's first int is the
                                        // mStack index of the boxed CLR object. Route through the
                                        // Area 4d reflection accessor. An ILTypeInstance /
                                        // CrossBindingAdaptorType owner (an IL type inheriting a
                                        // CLR base) or an Array owner (a CLR-struct array element)
                                        // is a distinct shape deferred here with a tagged NIE (not
                                        // the Step-6 default) so progress is measurable.
                                        int objIdx = *(int*)(frameBase + ownerOff);
                                        object target = objIdx >= 0 ? mStack[objIdx] : null;
                                        if (target == null)
                                            throw new NullReferenceException();
                                        if (target is ILTypeInstance || target is CrossBindingAdaptorType)
                                        {
                                            // IL type inheriting a CLR base: the CLR-base field lives
                                            // on the IL instance's CLRInstance (the wrapped Adaptor
                                            // object created by CrossBindingAdaptor.CreateCLRInstance,
                                            // IS-A the CLR base), NOT in its Primitives/ManagedObjects
                                            // IL-field layout. Route the read through the field-hash
                                            // accessor on CLRInstance (byte-identical to Legacy's
                                            // ILTypeInstance read-indexer CLR-inherited else branch).
                                            ILTypeInstance il = target as ILTypeInstance ?? ((CrossBindingAdaptorType)target).ILInstance;
                                            fldVal = NeoReadClrObjectField(AppDomain, il.CLRInstance, fieldHash);
                                        }
                                        else if (target is Array)
                                            throw new NotImplementedException("Neo raw Ldfld: array-element field read is deferred (ldfld on a CLR array element; follow-up). Field " + f.Name + " on " + ct.FullName);
                                        else
                                            fldVal = NeoReadClrObjectField(AppDomain, target, fieldHash);
                                        if (fldVal is CrossBindingAdaptorType cba) fldVal = cba.ILInstance;
                                    }
                                    // Marshal the boxed field value into the dest register by the
                                    // field's CLR type category (mirrors the Ldsfld CLR-static dest
                                    // push). A null ref value writes a -1 ref-slot index.
                                    byte* dstSlot = frameBase + ip->DstOffset;
                                    if (fldClrType.IsPrimitive)
                                        NeoWritePrimitiveToFrame(fldVal, dstSlot);
                                    else if (fldClrType.IsValueType)
                                        ILIntepreter.WriteNeoValueType(fldVal, dstSlot, Optimizer.GetNeoValueTypeManagedSize(fldClrType));
                                    else
                                    {
                                        mStack.Add(fldVal);
                                        *(int*)dstSlot = fldVal != null ? mStack.Count - 1 : -1;
                                    }
                                }
                                break;
                            case OpCodeREnum.Ldfld_I1:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                *(int*)(frameBase + ip->DstOffset) = (sbyte)ins.Primitives[ip->Operand2];
                                break;
                            case OpCodeREnum.Ldfld_U1:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                *(int*)(frameBase + ip->DstOffset) = ins.Primitives[ip->Operand2];
                                break;
                            case OpCodeREnum.Ldfld_I2:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                *(int*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<short>(ref ins.Primitives[ip->Operand2]);
                                break;
                            case OpCodeREnum.Ldfld_U2:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                *(int*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<ushort>(ref ins.Primitives[ip->Operand2]);
                                break;
                            case OpCodeREnum.Ldfld_I4:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                *(int*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<int>(ref ins.Primitives[ip->Operand2]);
                                break;
                            case OpCodeREnum.Ldfld_U4:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                *(uint*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<uint>(ref ins.Primitives[ip->Operand2]);
                                break;
                            case OpCodeREnum.Ldfld_I8:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                *(long*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<long>(ref ins.Primitives[ip->Operand2]);
                                break;
                            case OpCodeREnum.Ldfld_U8:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                *(ulong*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<ulong>(ref ins.Primitives[ip->Operand2]);
                                break;
                            case OpCodeREnum.Ldfld_R4:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                *(float*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<float>(ref ins.Primitives[ip->Operand2]);
                                break;
                            case OpCodeREnum.Ldfld_R8:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                *(double*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<double>(ref ins.Primitives[ip->Operand2]);
                                break;
                            case OpCodeREnum.Ldfld_Ref:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                if (ip->Operand4 != 0)
                                {
                                    // F-10: the field is a CLR-struct field of an IL
                                    // instance. The dest register is a flat-bytes local
                                    // (a CLR value-type local under the Neo model); flatten
                                    // the boxed struct at ManagedObjects[ReferenceOffset]
                                    // into the dest flat-bytes region (instead of
                                    // materializing a ref-slot mStack index).
                                    var ft5 = AppDomain.GetType(ip->Operand4);
                                    if (ft5 == null)
                                        throw new NotImplementedException("neo-clrstruct-field-of-il: Ldfld_Ref F-10 branch could not resolve the field type token (Operand4=" + ip->Operand4 + ")");
                                    Type ftClr5 = ft5.TypeForCLR;
                                    int sz5 = Optimizer.GetNeoValueTypeManagedSize(ftClr5);
                                    obj = ins.ManagedObjects[ip->Operand3];
                                    if (obj != null)
                                        ILIntepreter.WriteNeoValueType(obj, frameBase + ip->DstOffset, sz5);
                                    else
                                        Unsafe.InitBlock(frameBase + ip->DstOffset, 0, (uint)sz5);
                                }
                                else
                                {
                                    obj = ins.ManagedObjects[ip->Operand3];
                                    dstIdx = frameRefBase + ip->Operand;
                                    mStack[dstIdx] = obj;
                                    *(int*)(frameBase + ip->DstOffset) = obj != null ? dstIdx : -1;
                                }
                                break;
                            // Raw Stfld: the field's DECLARING type is a CLRType (see raw Ldfld
                            // above). Owner lives in the DstOffset slot (Stfld R1=owner=
                            // baseRegIdx-2); the value lives in the SrcOffset slot (R2=value=
                            // baseRegIdx-1). Decode field identity, marshal the source value to a
                            // boxed object, and write the CLR field. Owner shape splits on the
                            // declaring type's value/ref kind: a CLR value-type owner is a frame-
                            // native byref (-1, structBaseOff) produced by ldloca (a value-type
                            // field WRITE always goes through the struct's address) -> box/mutate/
                            // unbox at the byref target; a CLR reference-type owner is a boxed
                            // mStack object -> Area 4d accessor.
                            case OpCodeREnum.Stfld:
                                {
                                    int typeHash = (int)((ulong)ip->OperandLong >> 32);
                                    int fieldHash = (int)ip->OperandLong;
                                    var declType = AppDomain.GetType(typeHash);
                                    var ct = declType as CLRType;
                                    if (ct == null)
                                        throw new NotImplementedException("Neo raw Stfld: declaring type " + (declType == null ? "<null>" : declType.FullName) + " is not a CLRType (raw opcode expected only for CLR-declaring-type fields)");
                                    var f = ct.GetField(fieldHash);
                                    if (f == null)
                                        throw new NotImplementedException("Neo raw Stfld: CLR field hash " + fieldHash + " not resolved on type " + ct.FullName);
                                    Type fldClrType = f.FieldType;
                                    int ownerOff = ip->DstOffset;          // owner
                                    byte* valSlot = frameBase + ip->SrcOffset; // value
                                    // Marshal the source value to a boxed object by the field's
                                    // CLR type category (mirrors the Stsfld CLR-static source read).
                                    object value;
                                    if (fldClrType.IsPrimitive)
                                        value = NeoBoxPrimitiveByType(fldClrType, valSlot);
                                    else if (fldClrType.IsValueType)
                                    {
                                        int vsz = Optimizer.GetNeoValueTypeManagedSize(fldClrType);
                                        int vcur = ip->SrcOffset;
                                        value = ILIntepreter.ReadNeoValueType(fldClrType, frameBase, ref vcur, vsz);
                                    }
                                    else
                                    {
                                        int srcRefIdx = *(int*)valSlot;
                                        value = srcRefIdx >= 0 ? mStack[srcRefIdx] : null;
                                    }
                                    if (ct.TypeForCLR.IsValueType)
                                    {
                                        // CLR value-type owner: the owner slot holds a frame-native
                                        // byref (objIdx, off). Box the whole struct from the byref
                                        // target, reflection-write the field, and write the mutated
                                        // struct back (box/mutate/unbox). An array-element byref
                                        // (objIdx>=0, mStack[objIdx] is Array) is deferred with a
                                        // tagged NIE (not the Step-6 default).
                                        int objIdx = *(int*)(frameBase + ownerOff);
                                        int off = *(int*)(frameBase + ownerOff + 4);
                                        if (objIdx == -1)
                                        {
                                            int ownerSz = Optimizer.GetNeoValueTypeManagedSize(ct.TypeForCLR);
                                            int cur = off;
                                            object boxedOwner = ILIntepreter.ReadNeoValueType(ct.TypeForCLR, frameBase, ref cur, ownerSz);
                                            f.SetValue(boxedOwner, value);
                                            ILIntepreter.WriteNeoValueType(boxedOwner, frameBase + off, ownerSz);
                                        }
                                        else if (objIdx >= 0 && mStack[objIdx] is Array)
                                            throw new NotImplementedException("Neo raw Stfld: array-element field write is deferred (stfld on a CLR array element; follow-up). Field " + f.Name + " on " + ct.FullName);
                                        else
                                            throw new NotImplementedException("Neo raw Stfld: unrecognized CLR value-type owner byref shape (objIdx=" + objIdx + "). Field " + f.Name + " on " + ct.FullName);
                                    }
                                    else
                                    {
                                        // CLR reference-type owner: owner slot's first int is the
                                        // mStack index of the boxed CLR object. Route through Area
                                        // 4d. IL-instance / array owners are deferred (tagged NIE).
                                        int objIdx = *(int*)(frameBase + ownerOff);
                                        object target = objIdx >= 0 ? mStack[objIdx] : null;
                                        if (target == null)
                                            throw new NullReferenceException();
                                        if (target is ILTypeInstance || target is CrossBindingAdaptorType)
                                        {
                                            // IL type inheriting a CLR base: the CLR-base field lives
                                            // on the IL instance's CLRInstance (the wrapped Adaptor
                                            // object, IS-A the CLR base). Route the write through the
                                            // field-hash accessor on CLRInstance (byte-identical to
                                            // Legacy's ILTypeInstance.AssignFromStack CLR-inherited
                                            // else branch). No writeback: CLRInstance is a class, so
                                            // SetFieldValue's defensive ref does not replace it.
                                            ILTypeInstance il = target as ILTypeInstance ?? ((CrossBindingAdaptorType)target).ILInstance;
                                            NeoWriteClrObjectField(AppDomain, il.CLRInstance, fieldHash, value);
                                        }
                                        else if (target is Array)
                                            throw new NotImplementedException("Neo raw Stfld: array-element field write is deferred (stfld on a CLR array element; follow-up). Field " + f.Name + " on " + ct.FullName);
                                        else
                                            NeoWriteClrObjectField(AppDomain, target, fieldHash, value);
                                    }
                                }
                                break;
                            case OpCodeREnum.Stfld_I1:
                            case OpCodeREnum.Stfld_U1:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                ins.Primitives[ip->Operand2] = *(byte*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Stfld_I2:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                Unsafe.WriteUnaligned(ref ins.Primitives[ip->Operand2], *(short*)(frameBase + ip->SrcOffset));
                                break;
                            case OpCodeREnum.Stfld_U2:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                Unsafe.WriteUnaligned(ref ins.Primitives[ip->Operand2], *(ushort*)(frameBase + ip->SrcOffset));
                                break;
                            case OpCodeREnum.Stfld_I4:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                Unsafe.WriteUnaligned(ref ins.Primitives[ip->Operand2], *(int*)(frameBase + ip->SrcOffset));
                                break;
                            case OpCodeREnum.Stfld_U4:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                Unsafe.WriteUnaligned(ref ins.Primitives[ip->Operand2], *(uint*)(frameBase + ip->SrcOffset));
                                break;
                            case OpCodeREnum.Stfld_I8:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                Unsafe.WriteUnaligned(ref ins.Primitives[ip->Operand2], *(long*)(frameBase + ip->SrcOffset));
                                break;
                            case OpCodeREnum.Stfld_U8:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                Unsafe.WriteUnaligned(ref ins.Primitives[ip->Operand2], *(ulong*)(frameBase + ip->SrcOffset));
                                break;
                            case OpCodeREnum.Stfld_R4:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                Unsafe.WriteUnaligned(ref ins.Primitives[ip->Operand2], *(float*)(frameBase + ip->SrcOffset));
                                break;
                            case OpCodeREnum.Stfld_R8:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                Unsafe.WriteUnaligned(ref ins.Primitives[ip->Operand2], *(double*)(frameBase + ip->SrcOffset));
                                break;
                            case OpCodeREnum.Stfld_Ref:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                if (ip->Operand4 != 0)
                                {
                                    // F-10: the field is a CLR-struct field of an IL
                                    // instance (a reference slot holding the boxed struct).
                                    // The source register holds the value's FLAT BYTES (e.g.
                                    // a CLR-method return), NOT a ref-slot mStack index.
                                    // Box the source flat bytes into the field's CLR type and
                                    // store the boxed struct at ManagedObjects[ReferenceOffset].
                                    var ft4 = AppDomain.GetType(ip->Operand4);
                                    if (ft4 == null)
                                        throw new NotImplementedException("neo-clrstruct-field-of-il: Stfld_Ref F-10 branch could not resolve the field type token (Operand4=" + ip->Operand4 + ")");
                                    Type ftClr4 = ft4.TypeForCLR;
                                    int sz4 = Optimizer.GetNeoValueTypeManagedSize(ftClr4);
                                    int cur4 = ip->SrcOffset;
                                    object boxed4 = ILIntepreter.ReadNeoValueType(ftClr4, frameBase, ref cur4, sz4);
                                    ins.ManagedObjects[ip->Operand3] = boxed4;
                                }
                                else
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    ins.ManagedObjects[ip->Operand3] = srcIdx >= 0 ? mStack[srcIdx] : null;
                                }
                                break;
                            // ---- Step 12b deferred item: whole-IL-VT field store/load
                            // (stfld.value / ldfld.value). A struct field that is ITSELF an
                            // IL struct (e.g. `Outer { Inner inner; }`) accessed as a WHOLE
                            // value through a HEAP owner. The owner is a heap ILTypeInstance
                            // (its mStack index sits in the owner slot); the field lives at
                            // Primitives[field.PrimitiveOffset..+primSize] +
                            // ManagedObjects[field.ReferenceOffset..+refCount]. The value
                            // register (SrcOffset for Stfld / DstOffset for Ldfld) is an in-
                            // frame flat-bytes VT region whose ref-run base is recovered via
                            // the runtime localInfos scan (the established R2 pattern shared
                            // with the Stobj/Ldobj Step-17(b) arms). Mirror Move_Vt: a byte
                            // CopyBlock for the primitive region + an mStack-to-mStack copy of
                            // refCount reference slots (shallow copy, C# struct-copy
                            // semantics). Operand2 = field.PrimitiveOffset; Operand3 =
                            // field.ReferenceOffset; Operand4 = the FIELD's ILType hash
                            // (stamped by the JIT so the arm resolves
                            // TotalPrimitiveSize / TotalReferenceCount). These are HEAP-only
                            // opcodes (the in-frame-VT field path folds through the _Inline
                            // variants; Stfld_Value/Ldfld_Value have no _Inline form by
                            // design). ----
                            case OpCodeREnum.Stfld_Value:
                                {
                                    // DstOffset = owner slot (mStack index of the heap
                                    // ILTypeInstance); SrcOffset = source in-frame VT region.
                                    ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                    var ftFld = AppDomain.GetType(ip->Operand4) as ILType;
                                    if (ftFld == null)
                                        throw new NotImplementedException("neo-stfld-value: field type not resolved (Operand4=" + ip->Operand4 + ")");
                                    int fldPrimOff = ip->Operand2;
                                    int fldRefOff = ip->Operand3;
                                    int fldPrimSize = ftFld.TotalPrimitiveSize;
                                    int fldRefCount = ftFld.TotalReferenceCount;
                                    if (fldPrimSize > 0)
                                    {
                                        ref byte dstP = ref ins.Primitives[fldPrimOff];
                                        Unsafe.CopyBlock(ref dstP, ref *(frameBase + ip->SrcOffset), (uint)fldPrimSize);
                                    }
                                    if (fldRefCount > 0)
                                    {
                                        // Recover the in-frame source VT's ref-run base via
                                        // the localInfos scan (R2). A scan-miss (a non-direct-
                                        // local source) is an exotic shape; fail LOUD (tagged
                                        // NIE) instead of silently dropping the ref copy.
                                        int srcRefBase = -1;
                                        if (localInfos != null)
                                        {
                                            for (int li = 0; li < localInfos.Length; li++)
                                                if (localInfos[li].Offset == ip->SrcOffset)
                                                { srcRefBase = localInfos[li].RefOffset; break; }
                                        }
                                        if (srcRefBase < 0)
                                            throw new NotImplementedException(
                                                "Step 12b: stfld.value of an IL-VT field WITH reference fields from a non-direct-local value is deferred (ref-region base recovery; follow-up)");
                                        var dstRefs = ins.ManagedObjects;
                                        int srcBase = frameRefBase + srcRefBase;
                                        for (int i = 0; i < fldRefCount; i++)
                                            dstRefs[fldRefOff + i] = mStack[srcBase + i];
                                    }
                                }
                                break;
                            case OpCodeREnum.Ldfld_Value:
                                {
                                    // DstOffset = dest in-frame VT region; SrcOffset = owner
                                    // slot (mStack index of the heap ILTypeInstance).
                                    ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                    var ftFld = AppDomain.GetType(ip->Operand4) as ILType;
                                    if (ftFld == null)
                                        throw new NotImplementedException("neo-ldfld-value: field type not resolved (Operand4=" + ip->Operand4 + ")");
                                    int fldPrimOff = ip->Operand2;
                                    int fldRefOff = ip->Operand3;
                                    int fldPrimSize = ftFld.TotalPrimitiveSize;
                                    int fldRefCount = ftFld.TotalReferenceCount;
                                    if (fldPrimSize > 0)
                                    {
                                        ref byte srcP = ref ins.Primitives[fldPrimOff];
                                        Unsafe.CopyBlock(ref *(frameBase + ip->DstOffset), ref srcP, (uint)fldPrimSize);
                                    }
                                    if (fldRefCount > 0)
                                    {
                                        // Recover the in-frame dest VT's ref-run base via the
                                        // localInfos scan (R2); mirror the Stobj/Ldobj arms.
                                        int dstRefBase = -1;
                                        if (localInfos != null)
                                        {
                                            for (int li = 0; li < localInfos.Length; li++)
                                                if (localInfos[li].Offset == ip->DstOffset)
                                                { dstRefBase = localInfos[li].RefOffset; break; }
                                        }
                                        if (dstRefBase < 0)
                                            throw new NotImplementedException(
                                                "Step 12b: ldfld.value of an IL-VT field WITH reference fields into a non-direct-local dest is deferred (ref-region base recovery; follow-up)");
                                        var srcRefs = ins.ManagedObjects;
                                        int dstBase = frameRefBase + dstRefBase;
                                        for (int i = 0; i < fldRefCount; i++)
                                            mStack[dstBase + i] = srcRefs[fldRefOff + i];
                                    }
                                }
                                break;
                            // ---- Step 25 S3-4: STATIC field access (Stsfld / Ldsfld).
                            // Pre-S3-4 ExecuteNeo had NO static-field handlers (a stale
                            // "TODO Step 7" suppressed the Cecil-path .cctor; the NeoStep
                            // smoke probes never declared static fields). S3-4 seeds the
                            // .cctor at Cecil-free load, so Stsfld (the .cctor writes) +
                            // Ldsfld (a reader reads) MUST execute. The token encoding is
                            // (typeHash << 32) | staticFieldIdx (the SAME encoding the
                            // Legacy register VM uses at ILIntepreter.Register.cs:3288/
                            // 3316); the declaring type resolves via the recorded hash
                            // (S3-2 re-registration), the static field via the installed
                            // staticFieldOffsets (S3-4 factory). The static instance is a
                            // byte[] Primitives + AutoList ManagedObjects (ILTypeInstance
                            // Neo layout) -- read/write the per-field offset with the
                            // field type's width. Primitive widths + reference slots are
                            // handled (the capstone uses static int); an IL value-type
                            // static field spans both regions (sequenced -- the capstone
                            // uses a primitive static). ----
                            case OpCodeREnum.Stsfld:
                                {
                                    var declType = AppDomain.GetType((int)(ip->OperandLong >> 32));
                                    if (declType == null) throw new TypeLoadException("Neo Stsfld: declaring type not resolved for token 0x" + ip->OperandLong.ToString("X"));
                                    if (declType is ILType ilt)
                                    {
                                        int sIdx = (int)ip->OperandLong;
                                        var sinst = ilt.StaticInstance;
                                        var off = ilt.GetStaticFieldOffset(sIdx);
                                        var ft = ilt.StaticFieldTypes.Length > sIdx ? ilt.StaticFieldTypes[sIdx] : null;
                                        // JIT Code.Stsfld sets ONLY Register1 (= the DstOffset
                                        // alias) to the source value register; SrcOffset/
                                        // Register2 is NOT set for Stsfld. So the source value
                                        // lives at DstOffset (the same slot the typed Stfld_*
                                        // ops read from SrcOffset -- Stsfld has no instance,
                                        // so the value is the lone operand at Register1).
                                        // Stsfld/Ldsfeld are NOT lowered by LowerNeoOffsets, so
                                        // ip->DstOffset is still the raw Register1 INDEX, not a
                                        // byte offset. Resolve the register's byte offset via the
                                        // frame LocalInfos (in scope here as `localInfos`),
                                        // exactly as LowerR1 does at compile time + the CLR-static
                                        // arm below. Without this, a following Brtrue_Ref on a
                                        // reference static would dereference a garbage mStack
                                        // index read from the raw-index'th frame byte.
                                        int ilStRegIdx = ip->DstOffset;
                                        int ilStOff = (localInfos != null && ilStRegIdx < localInfos.Length) ? localInfos[ilStRegIdx].Offset : ilStRegIdx;
                                        byte* srcSlot = frameBase + ilStOff;
                                        if (ft != null && ft.IsPrimitive)
                                        {
                                            int psz = AppDomain.GetPrimitiveSize(ft);
                                            if (psz == 1) sinst.Primitives[off.PrimitiveOffset] = *srcSlot;
                                            else if (psz == 2) Unsafe.WriteUnaligned(ref sinst.Primitives[off.PrimitiveOffset], *(short*)srcSlot);
                                            else if (psz == 4) Unsafe.WriteUnaligned(ref sinst.Primitives[off.PrimitiveOffset], *(int*)srcSlot);
                                            else if (psz == 8) Unsafe.WriteUnaligned(ref sinst.Primitives[off.PrimitiveOffset], *(long*)srcSlot);
                                        }
                                        else if (ft != null && ft.IsValueType && ft is ILType vtil)
                                        {
                                            // An IL value-type static field spans both the
                                            // primitive + reference regions (mirrors the
                                            // Cecil InitializeFields static VT branch). Copy
                                            // the flat-bytes primitive region + the ref slots.
                                            Unsafe.CopyBlockUnaligned(ref sinst.Primitives[off.PrimitiveOffset], ref Unsafe.AsRef<byte>(srcSlot), (uint)vtil.TotalPrimitiveSize);
                                            for (int ri = 0; ri < vtil.TotalReferenceCount; ri++)
                                            {
                                                int srcRefIdx = *(int*)(srcSlot + vtil.TotalPrimitiveSize + ri * 4);
                                                sinst.ManagedObjects[off.ReferenceOffset + ri] = srcRefIdx >= 0 ? mStack[srcRefIdx] : null;
                                            }
                                        }
                                        else
                                        {
                                            // Reference static field: the source register is a
                                            // ref-slot mStack index.
                                            int srcRefIdx = *(int*)srcSlot;
                                            sinst.ManagedObjects[off.ReferenceOffset] = srcRefIdx >= 0 ? mStack[srcRefIdx] : null;
                                        }
                                    }
                                    else
                                    {
                                        // CLR static field: resolve via CLRType and write
                                        // through the underlying System.Reflection.FieldInfo
                                        // (mirrors Legacy ExecuteR Stsfld CLR branch,
                                        // ILIntepreter.Register.cs:3301-3308). The operand
                                        // encoding is shared IL/CLR (GetStaticFieldIndex:
                                        // typeHash<<32 | fieldHash), so sIdx=(int)OperandLong
                                        // is the field hash. NO JIT change.
                                        var ct = declType as CLRType;
                                        int sIdx = (int)ip->OperandLong;
                                        var f = ct.GetField(sIdx);
                                        if (f == null)
                                            throw new NotImplementedException("Neo Stsfld: CLR static field hash " + sIdx + " not resolved for type " + declType.FullName);
                                        var ft = f.FieldType;
                                        // Stsfld/Ldsfld are NOT lowered by LowerNeoOffsets (they
                                        // hit the no-op `default` + empty WarnUnhandledNeoLowering
                                        // Opcode), so ip->DstOffset still holds the raw Register1
                                        // INDEX, not a byte offset. Resolve the register's byte
                                        // offset at runtime via the frame LocalInfos (in scope
                                        // here as `localInfos`), exactly as LowerR1 does at
                                        // compile time for every other single-register op. The IL-
                                        // static arms above now resolve via localInfos too (the
                                        // neo-brtrue-on-reference child landed F3 here so a
                                        // following Brtrue_Ref dereferences a valid mStack index).
                                        int stRegIdx = ip->DstOffset;
                                        int stOff = (localInfos != null && stRegIdx < localInfos.Length) ? localInfos[stRegIdx].Offset : stRegIdx;
                                        byte* srcSlot = frameBase + stOff;
                                        object value;
                                        if (ft.IsPrimitive)
                                            value = NeoBoxPrimitiveByType(ft, srcSlot);
                                        else if (ft.IsValueType)
                                        {
                                            int stSlotSize = (localInfos != null && stRegIdx < localInfos.Length) ? localInfos[stRegIdx].Size : 0;
                                            if (NeoClrVtStaticFieldIsUnsafe(ft, stSlotSize))
                                                throw new NotImplementedException("Neo Stsfld: CLR static value-type field " + f.Name + " of type " + ft.FullName + " not supported under Neo (Step-13b ref-field/binder gap or slot-size overflow)");
                                            int vtOff = stOff;
                                            value = ReadNeoValueType(ft, frameBase, ref vtOff, Optimizer.GetNeoValueTypeManagedSize(ft));
                                        }
                                        else
                                        {
                                            // Reference static field: the source register is a
                                            // ref-slot mStack index.
                                            int srcRefIdx = *(int*)srcSlot;
                                            value = srcRefIdx >= 0 ? mStack[srcRefIdx] : null;
                                        }
                                        ct.SetStaticFieldValue(sIdx, value);
                                    }
                                }
                                break;
                            case OpCodeREnum.Ldsflda:
                                {
                                    // ldsflda: load the address of a static field as a Neo
                                    // byref (objIdx, off). For an IL static field, materialize
                                    // the declaring type's StaticInstance (an
                                    // ILTypeStaticInstance, which derives from ILTypeInstance)
                                    // into mStack and emit (mStackIdx, fieldOffset), where
                                    // fieldOffset is the field's PrimitiveOffset (primitive
                                    // field) or ReferenceOffset (reference field) -- the SAME
                                    // offset resolution the Neo Ldsfld IL-static path uses. A
                                    // following stind/ldind OR a ref/out method arg
                                    // (CopyNeoCallArguments -> NeoMarshalByrefFieldToSlot, +
                                    // CopyNeoCallWriteBack) then writes/reads the static
                                    // field's storage via ins.Primitives[off] /
                                    // ins.ManagedObjects[off] with ZERO consumer change --
                                    // both consumers already dispatch `mStack[objIdx] is
                                    // ILTypeInstance`. Encoding is identical to Ldsfld
                                    // (Register1 = dest, OperandLong = (typeHash<<32)|
                                    // fieldHash); Ldsflda is NOT in the LowerNeoOffsets case-
                                    // list, so ip->DstOffset is the raw Register1 INDEX --
                                    // resolve its byte offset via localInfos (mirrors Stsfld/
                                    // Ldsfld). Do NOT stamp Operand3 (it aliases OperandLong's
                                    // high dword).
                                    var ldaDeclType = AppDomain.GetType((int)((ulong)ip->OperandLong >> 32));
                                    if (ldaDeclType == null) throw new TypeLoadException("Neo Ldsflda: declaring type not resolved for token 0x" + ip->OperandLong.ToString("X"));
                                    int ldaDstReg = ip->DstOffset;
                                    int ldaDstOff = (localInfos != null && ldaDstReg < localInfos.Length) ? localInfos[ldaDstReg].Offset : ldaDstReg;
                                    if (ldaDeclType is ILType ldaIlt)
                                    {
                                        int ldaSIdx = (int)ip->OperandLong;
                                        var ldaOff = ldaIlt.GetStaticFieldOffset(ldaSIdx);
                                        var ldaFt = ldaIlt.StaticFieldTypes.Length > ldaSIdx ? ldaIlt.StaticFieldTypes[ldaSIdx] : null;
                                        mStack.Add(ldaIlt.StaticInstance);
                                        int ldaSidx = mStack.Count - 1;
                                        int ldaFieldOff = (ldaFt != null && ldaFt.IsPrimitive) ? ldaOff.PrimitiveOffset : ldaOff.ReferenceOffset;
                                        *(int*)(frameBase + ldaDstOff + 0) = ldaSidx;
                                        *(int*)(frameBase + ldaDstOff + 4) = ldaFieldOff;
                                    }
                                    else
                                    {
                                        // CLR static field: no heap object to address
                                        // (FieldInfo.GetValue/SetValue null), so the existing
                                        // (objIdx, off) object-field consumers cannot resolve
                                        // it. Defer with a tagged NIE (distinct from the Step-
                                        // 6 default) unless a future follow-up adds a
                                        // dedicated static-field byref sentinel + consumer arm.
                                        throw new NotImplementedException("Neo Ldsflda: CLR static field address deferred (follow-up)");
                                    }
                                }
                                break;
                            case OpCodeREnum.Ldsfld:
                                {
                                    var declType = AppDomain.GetType((int)(ip->OperandLong >> 32));
                                    if (declType == null) throw new TypeLoadException("Neo Ldsfld: declaring type not resolved for token 0x" + ip->OperandLong.ToString("X"));
                                    if (declType is ILType ilt)
                                    {
                                        int sIdx = (int)ip->OperandLong;
                                        var sinst = ilt.StaticInstance;
                                        var off = ilt.GetStaticFieldOffset(sIdx);
                                        var ft = ilt.StaticFieldTypes.Length > sIdx ? ilt.StaticFieldTypes[sIdx] : null;
                                        // See the IL-static Stsfeld arm: Stsfld/Ldsfeld are NOT
                                        // lowered, so ip->DstOffset is the raw Register1 INDEX.
                                        // Resolve the dest register's byte offset via localInfos
                                        // (mirrors the CLR-static arm below).
                                        int ilLdRegIdx = ip->DstOffset;
                                        int ilLdOff = (localInfos != null && ilLdRegIdx < localInfos.Length) ? localInfos[ilLdRegIdx].Offset : ilLdRegIdx;
                                        byte* dstSlot = frameBase + ilLdOff;
                                        if (ft != null && ft.IsPrimitive)
                                        {
                                            int psz = AppDomain.GetPrimitiveSize(ft);
                                            if (psz == 1) *(int*)dstSlot = (sbyte)sinst.Primitives[off.PrimitiveOffset];
                                            else if (psz == 2) *(int*)dstSlot = Unsafe.ReadUnaligned<short>(ref sinst.Primitives[off.PrimitiveOffset]);
                                            else if (psz == 4) *(int*)dstSlot = Unsafe.ReadUnaligned<int>(ref sinst.Primitives[off.PrimitiveOffset]);
                                            else if (psz == 8) *(long*)dstSlot = Unsafe.ReadUnaligned<long>(ref sinst.Primitives[off.PrimitiveOffset]);
                                        }
                                        else if (ft != null && ft.IsValueType && ft is ILType vtil)
                                        {
                                            Unsafe.CopyBlockUnaligned(ref Unsafe.AsRef<byte>(dstSlot), ref sinst.Primitives[off.PrimitiveOffset], (uint)vtil.TotalPrimitiveSize);
                                            for (int ri = 0; ri < vtil.TotalReferenceCount; ri++)
                                            {
                                                object rv = sinst.ManagedObjects[off.ReferenceOffset + ri];
                                                // Allocate a ref slot + store its index in the dest's
                                                // ref region (mirrors how a VT load materializes refs).
                                                mStack.Add(rv);
                                                *(int*)(dstSlot + vtil.TotalPrimitiveSize + ri * 4) = mStack.Count - 1;
                                            }
                                        }
                                        else
                                        {
                                            // Reference static field: materialize a ref-slot
                                            // mStack index into the dest register.
                                            object rv = sinst.ManagedObjects[off.ReferenceOffset];
                                            mStack.Add(rv);
                                            *(int*)dstSlot = mStack.Count - 1;
                                        }
                                    }
                                    else
                                    {
                                        // CLR static field: resolve via CLRType and read
                                        // through the underlying System.Reflection.FieldInfo
                                        // (mirrors Legacy ExecuteR Ldsfld CLR branch,
                                        // ILIntepreter.Register.cs:3328-3336). target=null for
                                        // a static -> FieldInfo.GetValue(null). Unwrap a
                                        // CrossBindingAdaptorType to its ILInstance on read
                                        // (Legacy parity). The dest is pushed by the field's
                                        // CLR System.Type category; the reference branch uses
                                        // the mStack.Add temp-ref convention (Stsfld/Ldsfld
                                        // carry ONLY Register1 = DstOffset; there is NO
                                        // dstRefOffset operand, unlike Box/Unbox).
                                        var ct = declType as CLRType;
                                        int sIdx = (int)ip->OperandLong;
                                        var f = ct.GetField(sIdx);
                                        if (f == null)
                                            throw new NotImplementedException("Neo Ldsfld: CLR static field hash " + sIdx + " not resolved for type " + declType.FullName);
                                        object fldVal = ct.GetFieldValue(sIdx, null);
                                        if (fldVal is CrossBindingAdaptorType cba) fldVal = cba.ILInstance;
                                        var ft = f.FieldType;
                                        // See the Stsfld CLR arm: Stsfld/Ldsfld are NOT lowered,
                                        // so ip->DstOffset is the raw Register1 INDEX. Resolve
                                        // the dest register's byte offset via `localInfos`.
                                        int ldRegIdx = ip->DstOffset;
                                        int ldOff = (localInfos != null && ldRegIdx < localInfos.Length) ? localInfos[ldRegIdx].Offset : ldRegIdx;
                                        byte* dstSlot = frameBase + ldOff;
                                        if (ft.IsPrimitive)
                                            NeoWritePrimitiveToFrame(fldVal, dstSlot);
                                        else if (ft.IsValueType)
                                        {
                                            int ldSlotSize = (localInfos != null && ldRegIdx < localInfos.Length) ? localInfos[ldRegIdx].Size : 0;
                                            if (NeoClrVtStaticFieldIsUnsafe(ft, ldSlotSize))
                                                throw new NotImplementedException("Neo Ldsfld: CLR static value-type field " + f.Name + " of type " + ft.FullName + " not supported under Neo (Step-13b ref-field/binder gap or slot-size overflow)");
                                            WriteNeoValueType(fldVal, dstSlot, Optimizer.GetNeoValueTypeManagedSize(ft));
                                        }
                                        else
                                        {
                                            // Reference static field: materialize a ref-slot
                                            // mStack index into the dest register (the SAME
                                            // mStack.Add temp-ref convention the IL-static
                                            // Ldsfld ref branch uses).
                                            mStack.Add(fldVal);
                                            *(int*)dstSlot = fldVal != null ? mStack.Count - 1 : -1;
                                        }
                                    }
                                }
                                break;
                            // ---- Step 12: in-frame value-type inline field access ----
                            // These index the frame byte region directly. Encoding:
                            //   Ldfld_*_Inline: DstOffset = dest temp byte offset;
                            //                   SrcOffset = owning VT slot byte offset;
                            //                   Operand2  = field PrimitiveOffset within the VT.
                            //   Stfld_*_Inline: DstOffset = owning VT slot byte offset;
                            //                   SrcOffset = value temp byte offset;
                            //                   Operand2  = field PrimitiveOffset within the VT.
                            // The owning VT slot holds raw bytes (NOT an mStack index),
                            // so there is no GetNeoILInstance / no branch.
                            case OpCodeREnum.Ldfld_I1_Inline:
                                *(int*)(frameBase + ip->DstOffset) =
                                    *(sbyte*)(frameBase + ip->SrcOffset + ip->Operand2);
                                break;
                            case OpCodeREnum.Ldfld_U1_Inline:
                                *(int*)(frameBase + ip->DstOffset) =
                                    *(byte*)(frameBase + ip->SrcOffset + ip->Operand2);
                                break;
                            case OpCodeREnum.Ldfld_I2_Inline:
                                *(int*)(frameBase + ip->DstOffset) =
                                    *(short*)(frameBase + ip->SrcOffset + ip->Operand2);
                                break;
                            case OpCodeREnum.Ldfld_U2_Inline:
                                *(int*)(frameBase + ip->DstOffset) =
                                    *(ushort*)(frameBase + ip->SrcOffset + ip->Operand2);
                                break;
                            case OpCodeREnum.Ldfld_I4_Inline:
                                *(int*)(frameBase + ip->DstOffset) =
                                    *(int*)(frameBase + ip->SrcOffset + ip->Operand2);
                                break;
                            case OpCodeREnum.Ldfld_U4_Inline:
                                *(uint*)(frameBase + ip->DstOffset) =
                                    *(uint*)(frameBase + ip->SrcOffset + ip->Operand2);
                                break;
                            case OpCodeREnum.Ldfld_I8_Inline:
                                *(long*)(frameBase + ip->DstOffset) =
                                    *(long*)(frameBase + ip->SrcOffset + ip->Operand2);
                                break;
                            case OpCodeREnum.Ldfld_U8_Inline:
                                *(ulong*)(frameBase + ip->DstOffset) =
                                    *(ulong*)(frameBase + ip->SrcOffset + ip->Operand2);
                                break;
                            case OpCodeREnum.Ldfld_R4_Inline:
                                *(float*)(frameBase + ip->DstOffset) =
                                    *(float*)(frameBase + ip->SrcOffset + ip->Operand2);
                                break;
                            case OpCodeREnum.Ldfld_R8_Inline:
                                *(double*)(frameBase + ip->DstOffset) =
                                    *(double*)(frameBase + ip->SrcOffset + ip->Operand2);
                                break;
                            case OpCodeREnum.Stfld_I1_Inline:
                            case OpCodeREnum.Stfld_U1_Inline:
                                *(byte*)(frameBase + ip->DstOffset + ip->Operand2) =
                                    *(byte*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Stfld_I2_Inline:
                            case OpCodeREnum.Stfld_U2_Inline:
                                *(short*)(frameBase + ip->DstOffset + ip->Operand2) =
                                    *(short*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Stfld_I4_Inline:
                            case OpCodeREnum.Stfld_U4_Inline:
                                *(int*)(frameBase + ip->DstOffset + ip->Operand2) =
                                    *(int*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Stfld_I8_Inline:
                            case OpCodeREnum.Stfld_U8_Inline:
                                *(long*)(frameBase + ip->DstOffset + ip->Operand2) =
                                    *(long*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Stfld_R4_Inline:
                                *(float*)(frameBase + ip->DstOffset + ip->Operand2) =
                                    *(float*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Stfld_R8_Inline:
                                *(double*)(frameBase + ip->DstOffset + ip->Operand2) =
                                    *(double*)(frameBase + ip->SrcOffset);
                                break;
                            // Ref inline variants operate on the frame's mStack
                            // reference region. Encoding (stamped by the Neo
                            // offset-lowering pass):
                            //   Ldfld_Ref_Inline: Operand  = source field absolute
                            //                                frame-ref index
                            //                                (owningSlot.RefOffset
                            //                                 + field.ReferenceOffset);
                            //                     Operand4 = dest temp RefOffset.
                            //   Stfld_Ref_Inline: Operand  = dest field absolute
                            //                                frame-ref index
                            //                                (owningSlot.RefOffset
                            //                                 + field.ReferenceOffset).
                            //                     SrcOffset = value temp byte offset.
                            case OpCodeREnum.Ldfld_Ref_Inline:
                                obj = mStack[frameRefBase + ip->Operand];
                                dstIdx = frameRefBase + ip->Operand4;
                                mStack[dstIdx] = obj;
                                *(int*)(frameBase + ip->DstOffset) = obj != null ? dstIdx : -1;
                                break;
                            case OpCodeREnum.Stfld_Ref_Inline:
                                srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                mStack[frameRefBase + ip->Operand] =
                                    srcIdx >= 0 ? mStack[srcIdx] : null;
                                break;
                            case OpCodeREnum.Unbox:
                            case OpCodeREnum.Unbox_Any:
                                dstRefOffset = ip->Operand3;
                                t = AppDomain.GetType(ip->Operand);
                                srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                if (srcIdx < 0)
                                    throw new NullReferenceException();
                                obj = mStack[srcIdx];
                                if (obj == null)
                                    throw new NullReferenceException();
                                ilType = t as ILType;
                                if (ilType != null)
                                {
                                    ins = obj as ILTypeInstance;
                                    if (ins == null)
                                        throw new InvalidCastException();
                                    if (ilType.IsEnum)
                                    {
                                        sz = AppDomain.GetPrimitiveSize(ilType.FieldTypes[0]);
                                        if (sz > 0)
                                        {
                                            ref byte srcP = ref MemoryMarshal.GetReference(ins.Primitives.AsSpan());
                                            Unsafe.CopyBlock(ref *(frameBase + ip->DstOffset), ref srcP, (uint)sz);
                                        }
                                    }
                                    else if (ilType.IsPrimitive)
                                    {
                                        sz = AppDomain.GetPrimitiveSize(ilType);
                                        if (sz > 0 && ins.Primitives != null)
                                        {
                                            ref byte srcP = ref MemoryMarshal.GetReference(ins.Primitives.AsSpan());
                                            Unsafe.CopyBlock(ref *(frameBase + ip->DstOffset), ref srcP, (uint)sz);
                                        }
                                    }
                                    else if (ilType.IsValueType)
                                    {
                                        CopyILToFrame(ins,
                                                      frameBase, ip->DstOffset, dstRefOffset,
                                                      ilType.TotalPrimitiveSize, ilType.TotalReferenceCount,
                                                      mStack, frameRefBase);
                                    }
                                    else
                                    {
                                        throw new InvalidCastException();
                                    }
                                }
                                else
                                {
                                    // Step 13: CLR value type Unbox / Unbox_Any.
                                    CLRType clrUnboxType = t as CLRType;
                                    if (clrUnboxType == null)
                                        throw new InvalidCastException();
                                    // obj (the boxed source) was fetched above; null was
                                    // already turned into NullReferenceException.
                                    if (clrUnboxType.IsPrimitive)
                                    {
                                        if (obj is ILEnumTypeInstance enumUnboxObj)
                                        {
                                            // Unbox a boxed IL enum to its underlying CLR
                                            // primitive (e.g. `(int)(object)E.B`). The enum's
                                            // value lives in enumUnboxObj.Primitives (the
                                            // ILEnumTypeInstance ctor under Neo allocates
                                            // fields = new byte[underlyingSize] and the
                                            // Primitives getter returns that byte[]). Copy the
                                            // underlying-primitive bytes into the dest slot --
                                            // mirrors the ILType-enum Unbox arm above (4357)
                                            // and Legacy Register.cs:4129
                                            // (res.CopyToRegister(0, ...)). Without this guard
                                            // NeoWritePrimitiveToFrame throws
                                            // "unsupported CLR primitive for Unbox:
                                            // ILEnumTypeInstance" (obj.GetType() is the enum
                                            // type, not a CLR primitive).
                                            ILType enumIlType = enumUnboxObj.Type;
                                            int enumSz = AppDomain.GetPrimitiveSize(enumIlType.FieldTypes[0]);
                                            if (enumSz > 0 && enumUnboxObj.Primitives != null)
                                            {
                                                ref byte enumSrc = ref MemoryMarshal.GetReference(enumUnboxObj.Primitives.AsSpan());
                                                Unsafe.CopyBlock(ref *(frameBase + ip->DstOffset), ref enumSrc, (uint)enumSz);
                                            }
                                        }
                                        else
                                        {
                                            // CLR primitives unbox into the dest flat-bytes slot
                                            // by value.
                                            NeoWritePrimitiveToFrame(obj, frameBase + ip->DstOffset);
                                        }
                                    }
                                    else
                                    {
                                        // F-MAJ-1 review-fix: a Neo CLR value-type (struct OR
                                        // enum) LOCAL destination is FLAT MANAGED BYTES (Size =
                                        // GetNeoValueTypeManagedSize, RefCount = 0; see
                                        // JITCompiler.AllocateLocalStackSpaces CLR-VT branch
                                        // under ENABLE_NEO_MODE). Unbox writes the boxed
                                        // struct's flat bytes into the dest via WriteNeoValueType
                                        // (an independent value copy -- value semantics
                                        // preserved). (Pre-F-MAJ-1 this arm installed a boxed
                                        // clone into mStack[frameRefBase+dstRefOffset] and
                                        // wrote the index into the flat bytes; with RefCount=0
                                        // dstRefOffset is STALE -> corruption.) This is the
                                        // inverse of the M2 Box fix. The dest of unbox.any is
                                        // always a value-typed local, so the flat-bytes write is
                                        // correct here unconditionally.
                                        int unbxSz = Optimizer.GetNeoValueTypeManagedSize(clrUnboxType.TypeForCLR);
                                        WriteNeoValueType(obj, frameBase + ip->DstOffset, unbxSz);
                                    }
                                }
                                break;
                            // Step 15: isinst (C# `is` / `as`). The checked value is
                            // always a reference (Box produced it, or it was a reference
                            // local/field). Resolve the target type statically via the
                            // type-token operand; test assignability; keep the original
                            // reference on success else write null. NEVER throws on mismatch.
                            case OpCodeREnum.Isinst:
                                {
                                    IType isinstType = AppDomain.GetType(ip->Operand);
                                    if (isinstType == null)
                                        throw new NullReferenceException();
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    obj = srcIdx >= 0 ? mStack[srcIdx] : null;
                                    object isinstResult = null;
                                    if (obj != null)
                                    {
                                        if (obj is ILTypeInstance isinstILI)
                                            isinstResult = isinstILI.CanAssignTo(isinstType) ? obj : null;
                                        else
                                            isinstResult = isinstType.TypeForCLR.IsAssignableFrom(obj.GetType()) ? obj : null;
                                    }
                                    if (isinstResult != null)
                                    {
                                        dstIdx = frameRefBase + ip->Operand3;
                                        mStack[dstIdx] = isinstResult;
                                        *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                    }
                                    else
                                        *(int*)(frameBase + ip->DstOffset) = -1;
                                }
                                break;
                            // Step 15: castclass (C# explicit `(T)obj` cast). Same
                            // assignability dispatch as isinst, but a failed check throws
                            // InvalidCastException; a null source passes through as null.
                            case OpCodeREnum.Castclass:
                                {
                                    IType castType = AppDomain.GetType(ip->Operand);
                                    if (castType == null)
                                        throw new NullReferenceException();
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    obj = srcIdx >= 0 ? mStack[srcIdx] : null;
                                    object castResult;
                                    if (obj == null)
                                    {
                                        castResult = null;
                                    }
                                    else if (obj is ILTypeInstance castILI)
                                    {
                                        if (!castILI.CanAssignTo(castType))
                                            throw new InvalidCastException(string.Format(
                                                "Cannot Cast {0} to {1}",
                                                castILI.Type.FullName, castType.FullName));
                                        castResult = obj;
                                    }
                                    else
                                    {
                                        if (!castType.TypeForCLR.IsAssignableFrom(obj.GetType()))
                                            throw new InvalidCastException(string.Format(
                                                "Cannot Cast {0} to {1}",
                                                obj.GetType().FullName, castType.FullName));
                                        castResult = obj;
                                    }
                                    if (castResult != null)
                                    {
                                        dstIdx = frameRefBase + ip->Operand3;
                                        mStack[dstIdx] = castResult;
                                        *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                    }
                                    else
                                        *(int*)(frameBase + ip->DstOffset) = -1;
                                }
                                break;
                            // ---- Step 16: array element access ----
                            // Encoding after LowerNeoOffsets:
                            //   Newarr: DstOffset=dest array byte off, SrcOffset=count
                            //           byte off, Operand=element-type token,
                            //           Operand3=dest array ref slot.
                            //   Ldlen:  DstOffset=dest(int), SrcOffset=array byte off.
                            //   Ldelem_*: DstOffset=dest, SrcOffset=array,
                            //             Operand4=index byte off, Operand3=dest ref slot.
                            //   Stelem_*: DstOffset=array, SrcOffset=index,
                            //             Operand4=value byte off, Operand3=value ref slot.
                            // Three array kinds (matches Legacy ExecuteR @4879-5304):
                            //   (a) CLR primitive array: typed CLR indexer.
                            //   (b) IL reference-type array: ILTypeInstance[] (or CLR
                            //       object[]); element is an mStack-resident object.
                            //   (c) IL value-type array: ILTypeInstance[] with every slot
                            //       pre-instantiated; element is a heap ILTypeInstance,
                            //       copied via CopyILToFrame / CopyFrameToIL (Step 12/13).
                            // Bounds: the CLR typed indexer throws IndexOutOfRangeException
                            // natively; null array -> NullReferenceException (surfaced by
                            // the Step 14 outer try/catch). No explicit bounds check.
                            case OpCodeREnum.Newarr:
                                {
                                    int count = *(int*)(frameBase + ip->SrcOffset);
                                    t = AppDomain.GetType(ip->Operand);
                                    object arr = null;
                                    if (t != null)
                                    {
                                        if (t.TypeForCLR != typeof(ILTypeInstance))
                                        {
                                            if (t is CLRType ct)
                                                arr = ct.CreateArrayInstance(count);
                                            else
                                                arr = Array.CreateInstance(t.TypeForCLR, count);
                                            // Register the array's CLR type, as Legacy does.
                                            AppDomain.GetType(arr.GetType());
                                        }
                                        else
                                        {
                                            var ilArr = new ILTypeInstance[count];
                                            ilType = (ILType)t;
                                            if (ilType.IsValueType)
                                            {
                                                for (int i = 0; i < count; i++)
                                                    ilArr[i] = ilType.Instantiate(true);
                                            }
                                            arr = ilArr;
                                        }
                                    }
                                    if (arr != null)
                                    {
                                        dstIdx = frameRefBase + ip->Operand3;
                                        mStack[dstIdx] = arr;
                                        *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                    }
                                    else
                                        *(int*)(frameBase + ip->DstOffset) = -1;
                                }
                                break;
                            case OpCodeREnum.Ldlen:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0)
                                        throw new NullReferenceException();
                                    Array lenArr = (Array)mStack[srcIdx];
                                    *(int*)(frameBase + ip->DstOffset) = lenArr.Length;
                                }
                                break;
                            case OpCodeREnum.Ldelem_I1:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    Array la = (Array)mStack[srcIdx];
                                    // bool[] vs sbyte[] disambiguation (Legacy @5161-5179).
                                    if (la is bool[] ba) *(int*)(frameBase + ip->DstOffset) = ba[li] ? 1 : 0;
                                    else *(int*)(frameBase + ip->DstOffset) = ((sbyte[])la)[li];
                                }
                                break;
                            case OpCodeREnum.Ldelem_U1:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    Array la = (Array)mStack[srcIdx];
                                    // byte[] vs bool[] disambiguation (Legacy @5181-5200).
                                    if (la is byte[] bya) *(int*)(frameBase + ip->DstOffset) = bya[li];
                                    else *(int*)(frameBase + ip->DstOffset) = ((bool[])la)[li] ? 1 : 0;
                                }
                                break;
                            case OpCodeREnum.Ldelem_I2:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    Array la = (Array)mStack[srcIdx];
                                    // short[] vs char[] disambiguation (Legacy @5201-5220).
                                    if (la is short[] sa) *(int*)(frameBase + ip->DstOffset) = sa[li];
                                    else *(int*)(frameBase + ip->DstOffset) = ((char[])la)[li];
                                }
                                break;
                            case OpCodeREnum.Ldelem_U2:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    Array la = (Array)mStack[srcIdx];
                                    // ushort[] vs char[] disambiguation (Legacy @5221-5240).
                                    if (la is ushort[] usa) *(int*)(frameBase + ip->DstOffset) = usa[li];
                                    else *(int*)(frameBase + ip->DstOffset) = ((char[])la)[li];
                                }
                                break;
                            case OpCodeREnum.Ldelem_I4:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    Array la = (Array)mStack[srcIdx];
                                    if (la is int[] ia) *(int*)(frameBase + ip->DstOffset) = ia[li];
                                    else *(int*)(frameBase + ip->DstOffset) = (int)((uint[])la)[li];
                                }
                                break;
                            case OpCodeREnum.Ldelem_U4:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    *(uint*)(frameBase + ip->DstOffset) = ((uint[])(Array)mStack[srcIdx])[li];
                                }
                                break;
                            case OpCodeREnum.Ldelem_I8:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    Array la = (Array)mStack[srcIdx];
                                    if (la is long[] lla) *(long*)(frameBase + ip->DstOffset) = lla[li];
                                    else *(long*)(frameBase + ip->DstOffset) = (long)((ulong[])la)[li];
                                }
                                break;
                            case OpCodeREnum.Ldelem_I:
                                {
                                    // native-int element load (I4-width on this VM,
                                    // OQ2 dump-confirmed). The element type is typically
                                    // IntPtr[] / UIntPtr[] (the C# `nint[]`/`UIntPtr[]`
                                    // shape) or int[]/uint[]; Ldelem_I4's typed-indexer
                                    // casts only handle int[]/uint[], so dispatch on the
                                    // native-int array kinds explicitly. (D-ARR.)
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    Array la = (Array)mStack[srcIdx];
                                    int v;
                                    if (la is int[] ia) v = ia[li];
                                    else if (la is uint[] ua) v = (int)ua[li];
                                    else if (la is IntPtr[] ipa) v = (int)ipa[li];
                                    else v = (int)((UIntPtr[])la)[li];
                                    *(int*)(frameBase + ip->DstOffset) = v;
                                }
                                break;
                            case OpCodeREnum.Ldelem_R4:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    *(float*)(frameBase + ip->DstOffset) = ((float[])(Array)mStack[srcIdx])[li];
                                }
                                break;
                            case OpCodeREnum.Ldelem_R8:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    *(double*)(frameBase + ip->DstOffset) = ((double[])(Array)mStack[srcIdx])[li];
                                }
                                break;
                            case OpCodeREnum.Ldelem_Ref:
                            case OpCodeREnum.Ldelem_Any:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    Array la = (Array)mStack[srcIdx];
                                    object elem;
                                    if (la is ILTypeInstance[] ilArr)
                                        elem = ilArr[li];
                                    else
                                        elem = la.GetValue(li);
                                    if (elem is CrossBindingAdaptorType cbat)
                                        elem = cbat.ILInstance;
                                    if (elem is ILTypeInstance elemIns
                                        && !(elemIns is DelegateAdapter)
                                        && elemIns.Type.IsValueType
                                        && !elemIns.Boxed)
                                    {
                                        // IL value-type element: copy primitive bytes +
                                        // ref slots into the dest frame region (Step 12/13
                                        // CopyILToFrame helper).
                                        CopyILToFrame(elemIns,
                                            frameBase, ip->DstOffset, ip->Operand3,
                                            elemIns.Type.TotalPrimitiveSize,
                                            elemIns.Type.TotalReferenceCount,
                                            mStack, frameRefBase);
                                    }
                                    else if (elem != null)
                                    {
                                        // Reference element: store the object on the dest
                                        // ref slot, write its mStack index to the dest slot.
                                        dstIdx = frameRefBase + ip->Operand3;
                                        mStack[dstIdx] = elem;
                                        *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                    }
                                    else
                                        *(int*)(frameBase + ip->DstOffset) = -1;
                                }
                                break;
                            case OpCodeREnum.Stelem_I1:
                                {
                                    srcIdx = *(int*)(frameBase + ip->DstOffset); // array
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int si = *(int*)(frameBase + ip->SrcOffset); // index
                                    byte val1 = *(byte*)(frameBase + ip->Operand4); // value
                                    Array sa = (Array)mStack[srcIdx];
                                    if (sa is byte[] sba) sba[si] = val1;
                                    else if (sa is bool[] sboa) sboa[si] = val1 != 0;
                                    else ((sbyte[])sa)[si] = (sbyte)val1;
                                }
                                break;
                            case OpCodeREnum.Stelem_I2:
                                {
                                    srcIdx = *(int*)(frameBase + ip->DstOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int si = *(int*)(frameBase + ip->SrcOffset);
                                    short val2 = *(short*)(frameBase + ip->Operand4);
                                    Array sa = (Array)mStack[srcIdx];
                                    if (sa is short[] ssa) ssa[si] = val2;
                                    else if (sa is ushort[] susa) susa[si] = (ushort)val2;
                                    else ((char[])sa)[si] = (char)val2;
                                }
                                break;
                            case OpCodeREnum.Stelem_I:
                                {
                                    // native-int element store. Native-int is I4-width
                                    // on this VM (OQ2 dump-confirmed: the 4-byte value
                                    // is read from ip->Operand4, mirroring Stind_I /
                                    // Ldind_I). The element type is typically IntPtr[] /
                                    // UIntPtr[] (the C# `nint[]`/`UIntPtr[]` shape) or
                                    // int[]/uint[]; the Stelem_I4 arm's typed-indexer
                                    // casts only handle int[]/uint[], so dispatch on the
                                    // native-int array kinds explicitly. (Step 16 / D-ARR
                                    // D1 Option B -- Option A `goto Stelem_I4` would
                                    // hit the `((uint[])sa)` fallback and throw.)
                                    srcIdx = *(int*)(frameBase + ip->DstOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int si = *(int*)(frameBase + ip->SrcOffset);
                                    int val4 = *(int*)(frameBase + ip->Operand4);
                                    Array sa = (Array)mStack[srcIdx];
                                    if (sa is int[] sia) sia[si] = val4;
                                    else if (sa is uint[] sua) sua[si] = (uint)val4;
                                    else if (sa is IntPtr[] ipa) ipa[si] = (IntPtr)val4;
                                    else ((UIntPtr[])sa)[si] = (UIntPtr)val4;
                                }
                                break;
                            case OpCodeREnum.Stelem_I4:
                                {
                                    srcIdx = *(int*)(frameBase + ip->DstOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int si = *(int*)(frameBase + ip->SrcOffset);
                                    int val4 = *(int*)(frameBase + ip->Operand4);
                                    Array sa = (Array)mStack[srcIdx];
                                    if (sa is int[] sia) sia[si] = val4;
                                    else ((uint[])sa)[si] = (uint)val4;
                                }
                                break;
                            case OpCodeREnum.Stelem_I8:
                                {
                                    srcIdx = *(int*)(frameBase + ip->DstOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int si = *(int*)(frameBase + ip->SrcOffset);
                                    long val8 = *(long*)(frameBase + ip->Operand4);
                                    Array sa = (Array)mStack[srcIdx];
                                    if (sa is long[] slla) slla[si] = val8;
                                    else ((ulong[])sa)[si] = (ulong)val8;
                                }
                                break;
                            case OpCodeREnum.Stelem_R4:
                                {
                                    srcIdx = *(int*)(frameBase + ip->DstOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int si = *(int*)(frameBase + ip->SrcOffset);
                                    float valr4 = *(float*)(frameBase + ip->Operand4);
                                    ((float[])(Array)mStack[srcIdx])[si] = valr4;
                                }
                                break;
                            case OpCodeREnum.Stelem_R8:
                                {
                                    srcIdx = *(int*)(frameBase + ip->DstOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int si = *(int*)(frameBase + ip->SrcOffset);
                                    double valr8 = *(double*)(frameBase + ip->Operand4);
                                    ((double[])(Array)mStack[srcIdx])[si] = valr8;
                                }
                                break;
                            case OpCodeREnum.Stelem_Ref:
                            case OpCodeREnum.Stelem_Any:
                                {
                                    srcIdx = *(int*)(frameBase + ip->DstOffset); // array
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int si = *(int*)(frameBase + ip->SrcOffset); // index
                                    Array sa = (Array)mStack[srcIdx];
                                    if (sa is ILTypeInstance[] dstIlArr)
                                    {
                                        ILTypeInstance slotIns = dstIlArr[si];
                                        if (slotIns != null && slotIns.Type.IsValueType && !slotIns.Boxed)
                                        {
                                            // IL value-type element: copy the in-frame value
                                            // (primitive + ref slots) into the pre-instantiated
                                            // element instance (Step 12/12b CopyFrameToIL).
                                            CopyFrameToIL(frameBase, ip->Operand4, ip->Operand3,
                                                slotIns.Type.TotalPrimitiveSize,
                                                slotIns.Type.TotalReferenceCount,
                                                mStack, frameRefBase, slotIns);
                                        }
                                        else
                                        {
                                            // Reference element: read the value's mStack
                                            // object and store it into the array slot.
                                            int vIdx = *(int*)(frameBase + ip->Operand4);
                                            dstIlArr[si] = vIdx >= 0 ? (ILTypeInstance)mStack[vIdx] : null;
                                        }
                                    }
                                    else
                                    {
                                        // CLR object array: the value reaching Stelem_Ref /
                                        // Stelem_Any into an object[] is a reference (C#
                                        // boxes value types before storing into object[]),
                                        // so read its mStack object and Array.SetValue it.
                                        int vIdx = *(int*)(frameBase + ip->Operand4);
                                        object vObj = vIdx >= 0 ? mStack[vIdx] : null;
                                        sa.SetValue(vObj, si);
                                    }
                                }
                                break;
                            // ---- Step 17: byref store/load-indirect dispatch ----
                            // Each arm decodes the 8-byte Ref Slot address operand
                            // (ip->DstOffset for Stind/Stobj, ip->SrcOffset for
                            // Ldind/Ldobj) = (objectIndex:int, offset:int) and
                            // dispatches: objectIndex == -1 -> frame-native byte
                            // region; objectIndex >= 0 with an ILTypeInstance ->
                            // pinned Primitives (ref il.Primitives[off]); a CLR
                            // object target is DEFERRED (Step-17-tagged NIE).
                            // Stind_* / Stobj: DstOffset = address, SrcOffset =
                            // value. Ldind_* / Ldobj: DstOffset = dest, SrcOffset
                            // = address.
                            case OpCodeREnum.Stind_I1:
                                {
                                    int objIdx = *(int*)(frameBase + ip->DstOffset + 0);
                                    int off = *(int*)(frameBase + ip->DstOffset + 4);
                                    sbyte v = *(sbyte*)(frameBase + ip->SrcOffset);
                                    if (objIdx == -1) *(sbyte*)(frameBase + off) = v;
                                    else if (mStack[objIdx] is Array cArr) cArr.SetValue(v, off);
                                    else if (NeoIsClrObject(mStack, objIdx)) NeoWriteClrObjectField(AppDomain, mStack[objIdx], off, v);
                                    else { ins = GetNeoILInstance(mStack, objIdx); ins.Primitives[off] = (byte)v; }
                                }
                                break;
                            case OpCodeREnum.Stind_I2:
                                {
                                    int objIdx = *(int*)(frameBase + ip->DstOffset + 0);
                                    int off = *(int*)(frameBase + ip->DstOffset + 4);
                                    short v = *(short*)(frameBase + ip->SrcOffset);
                                    if (objIdx == -1) *(short*)(frameBase + off) = v;
                                    else if (mStack[objIdx] is Array cArr) cArr.SetValue(v, off);
                                    else if (NeoIsClrObject(mStack, objIdx)) NeoWriteClrObjectField(AppDomain, mStack[objIdx], off, v);
                                    else { ins = GetNeoILInstance(mStack, objIdx); Unsafe.WriteUnaligned(ref ins.Primitives[off], v); }
                                }
                                break;
                            case OpCodeREnum.Stind_I4:
                                {
                                    int objIdx = *(int*)(frameBase + ip->DstOffset + 0);
                                    int off = *(int*)(frameBase + ip->DstOffset + 4);
                                    int v = *(int*)(frameBase + ip->SrcOffset);
                                    if (objIdx == -1) *(int*)(frameBase + off) = v;
                                    else if (mStack[objIdx] is Array cArr) cArr.SetValue(v, off);
                                    else if (NeoIsClrObject(mStack, objIdx)) NeoWriteClrObjectField(AppDomain, mStack[objIdx], off, v);
                                    else { ins = GetNeoILInstance(mStack, objIdx); Unsafe.WriteUnaligned(ref ins.Primitives[off], v); }
                                }
                                break;
                            case OpCodeREnum.Stind_I8:
                                {
                                    int objIdx = *(int*)(frameBase + ip->DstOffset + 0);
                                    int off = *(int*)(frameBase + ip->DstOffset + 4);
                                    long v = *(long*)(frameBase + ip->SrcOffset);
                                    if (objIdx == -1) *(long*)(frameBase + off) = v;
                                    else if (mStack[objIdx] is Array cArr) cArr.SetValue(v, off);
                                    else if (NeoIsClrObject(mStack, objIdx)) NeoWriteClrObjectField(AppDomain, mStack[objIdx], off, v);
                                    else { ins = GetNeoILInstance(mStack, objIdx); Unsafe.WriteUnaligned(ref ins.Primitives[off], v); }
                                }
                                break;
                            case OpCodeREnum.Stind_R4:
                                {
                                    int objIdx = *(int*)(frameBase + ip->DstOffset + 0);
                                    int off = *(int*)(frameBase + ip->DstOffset + 4);
                                    float v = *(float*)(frameBase + ip->SrcOffset);
                                    if (objIdx == -1) *(float*)(frameBase + off) = v;
                                    else if (mStack[objIdx] is Array cArr) cArr.SetValue(v, off);
                                    else if (NeoIsClrObject(mStack, objIdx)) NeoWriteClrObjectField(AppDomain, mStack[objIdx], off, v);
                                    else { ins = GetNeoILInstance(mStack, objIdx); Unsafe.WriteUnaligned(ref ins.Primitives[off], v); }
                                }
                                break;
                            case OpCodeREnum.Stind_R8:
                                {
                                    int objIdx = *(int*)(frameBase + ip->DstOffset + 0);
                                    int off = *(int*)(frameBase + ip->DstOffset + 4);
                                    double v = *(double*)(frameBase + ip->SrcOffset);
                                    if (objIdx == -1) *(double*)(frameBase + off) = v;
                                    else if (mStack[objIdx] is Array cArr) cArr.SetValue(v, off);
                                    else if (NeoIsClrObject(mStack, objIdx)) NeoWriteClrObjectField(AppDomain, mStack[objIdx], off, v);
                                    else { ins = GetNeoILInstance(mStack, objIdx); Unsafe.WriteUnaligned(ref ins.Primitives[off], v); }
                                }
                                break;
                            case OpCodeREnum.Stind_I:
                                // native-int store: same width as I4 on this VM.
                                goto case OpCodeREnum.Stind_I4;
                            case OpCodeREnum.Ldind_I1:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    if (objIdx == -1) *(int*)(frameBase + ip->DstOffset) = *(sbyte*)(frameBase + off);
                                    else if (mStack[objIdx] is Array cArr) *(int*)(frameBase + ip->DstOffset) = (sbyte)cArr.GetValue(off);
                                    else if (NeoIsClrObject(mStack, objIdx)) *(int*)(frameBase + ip->DstOffset) = (sbyte)NeoReadClrObjectField(AppDomain, mStack[objIdx], off);
                                    else { ins = GetNeoILInstance(mStack, objIdx); *(int*)(frameBase + ip->DstOffset) = ins.Primitives[off]; }
                                }
                                break;
                            case OpCodeREnum.Ldind_U1:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    if (objIdx == -1) *(int*)(frameBase + ip->DstOffset) = *(byte*)(frameBase + off);
                                    else if (mStack[objIdx] is Array cArr) *(int*)(frameBase + ip->DstOffset) = (byte)cArr.GetValue(off);
                                    else if (NeoIsClrObject(mStack, objIdx)) *(int*)(frameBase + ip->DstOffset) = (byte)NeoReadClrObjectField(AppDomain, mStack[objIdx], off);
                                    else { ins = GetNeoILInstance(mStack, objIdx); *(int*)(frameBase + ip->DstOffset) = ins.Primitives[off]; }
                                }
                                break;
                            case OpCodeREnum.Ldind_I2:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    int v;
                                    if (objIdx == -1) v = *(short*)(frameBase + off);
                                    else if (mStack[objIdx] is Array cArr) v = (short)cArr.GetValue(off);
                                    else if (NeoIsClrObject(mStack, objIdx)) v = (short)NeoReadClrObjectField(AppDomain, mStack[objIdx], off);
                                    else { ins = GetNeoILInstance(mStack, objIdx); v = Unsafe.ReadUnaligned<short>(ref ins.Primitives[off]); }
                                    *(int*)(frameBase + ip->DstOffset) = v;
                                }
                                break;
                            case OpCodeREnum.Ldind_U2:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    int v;
                                    if (objIdx == -1) v = *(ushort*)(frameBase + off);
                                    else if (mStack[objIdx] is Array cArr) v = (ushort)cArr.GetValue(off);
                                    else if (NeoIsClrObject(mStack, objIdx)) v = (ushort)NeoReadClrObjectField(AppDomain, mStack[objIdx], off);
                                    else { ins = GetNeoILInstance(mStack, objIdx); v = Unsafe.ReadUnaligned<ushort>(ref ins.Primitives[off]); }
                                    *(int*)(frameBase + ip->DstOffset) = v;
                                }
                                break;
                            case OpCodeREnum.Ldind_I4:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    if (objIdx == -1) *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + off);
                                    else if (mStack[objIdx] is Array cArr) *(int*)(frameBase + ip->DstOffset) = (int)cArr.GetValue(off);
                                    else if (NeoIsClrObject(mStack, objIdx)) *(int*)(frameBase + ip->DstOffset) = (int)NeoReadClrObjectField(AppDomain, mStack[objIdx], off);
                                    else { ins = GetNeoILInstance(mStack, objIdx); *(int*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<int>(ref ins.Primitives[off]); }
                                }
                                break;
                            case OpCodeREnum.Ldind_U4:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    uint v;
                                    if (objIdx == -1) v = *(uint*)(frameBase + off);
                                    else if (mStack[objIdx] is Array cArr) v = (uint)cArr.GetValue(off);
                                    else if (NeoIsClrObject(mStack, objIdx)) v = (uint)NeoReadClrObjectField(AppDomain, mStack[objIdx], off);
                                    else { ins = GetNeoILInstance(mStack, objIdx); v = Unsafe.ReadUnaligned<uint>(ref ins.Primitives[off]); }
                                    *(uint*)(frameBase + ip->DstOffset) = v;
                                }
                                break;
                            case OpCodeREnum.Ldind_I8:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    if (objIdx == -1) *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + off);
                                    else if (mStack[objIdx] is Array cArr) *(long*)(frameBase + ip->DstOffset) = (long)cArr.GetValue(off);
                                    else if (NeoIsClrObject(mStack, objIdx)) *(long*)(frameBase + ip->DstOffset) = (long)NeoReadClrObjectField(AppDomain, mStack[objIdx], off);
                                    else { ins = GetNeoILInstance(mStack, objIdx); *(long*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<long>(ref ins.Primitives[off]); }
                                }
                                break;
                            case OpCodeREnum.Ldind_R4:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    if (objIdx == -1) *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + off);
                                    else if (mStack[objIdx] is Array cArr) *(float*)(frameBase + ip->DstOffset) = (float)cArr.GetValue(off);
                                    else if (NeoIsClrObject(mStack, objIdx)) *(float*)(frameBase + ip->DstOffset) = (float)NeoReadClrObjectField(AppDomain, mStack[objIdx], off);
                                    else { ins = GetNeoILInstance(mStack, objIdx); *(float*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<float>(ref ins.Primitives[off]); }
                                }
                                break;
                            case OpCodeREnum.Ldind_R8:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    if (objIdx == -1) *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + off);
                                    else if (mStack[objIdx] is Array cArr) *(double*)(frameBase + ip->DstOffset) = (double)cArr.GetValue(off);
                                    else if (NeoIsClrObject(mStack, objIdx)) *(double*)(frameBase + ip->DstOffset) = (double)NeoReadClrObjectField(AppDomain, mStack[objIdx], off);
                                    else { ins = GetNeoILInstance(mStack, objIdx); *(double*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<double>(ref ins.Primitives[off]); }
                                }
                                break;
                            case OpCodeREnum.Ldind_I:
                                // native-int load: same width as I4 on this VM.
                                goto case OpCodeREnum.Ldind_I4;
                            case OpCodeREnum.Stind_Ref:
                                {
                                    // Store a managed reference through the pointer.
                                    // Frame-native target: write the value's mStack
                                    // index as a 4-byte slot at the offset (a ref-
                                    // typed frame slot). CLR array target (a
                                    // ldelema-produced object[]/string[] address):
                                    // Array.SetValue the managed object. 4d: a CLR
                                    // object field target -- route through the field-
                                    // hash accessor (off is the FieldInfo hash). Heap-
                                    // IL ref field: write the ManagedObjects entry (off
                                    // is the field's reference offset, stamped by Ldflda
                                    // via the heapIlRefFieldMarker; dispatch is content-
                                    // based `mStack[objIdx] is ILTypeInstance` -- the
                                    // neo-byref-ldind-ref-heap child).
                                    int objIdx = *(int*)(frameBase + ip->DstOffset + 0);
                                    int off = *(int*)(frameBase + ip->DstOffset + 4);
                                    int vIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (objIdx == -1)
                                    {
                                        *(int*)(frameBase + off) = vIdx;
                                    }
                                    else if ((off & JITCompiler.NeoF10ByrefOffsetFlag) != 0)
                                    {
                                        // F-7B: caller-owned-mStack-slot byref (a cross-frame
                                        // reference-byref promoted in NeoRunDelegateTargetOnThis).
                                        // objIdx is the stable caller-owned slot; store the
                                        // NEW object there directly. The slot sits below the
                                        // callee's frameRefBase, so the write survives the
                                        // callee Ret pop. (NeoRunDelegateTargetOnThis re-stamps
                                        // the caller cell to objIdx after the run; here we just
                                        // land the object in the surviving slot.)
                                        mStack[objIdx] = vIdx >= 0 ? mStack[vIdx] : null;
                                    }
                                    else if (mStack[objIdx] is Array cArr)
                                    {
                                        cArr.SetValue(vIdx >= 0 ? mStack[vIdx] : null, off);
                                    }
                                    else if (NeoIsClrObject(mStack, objIdx))
                                    {
                                        NeoWriteClrObjectField(AppDomain, mStack[objIdx], off, vIdx >= 0 ? mStack[vIdx] : null);
                                    }
                                    else if (mStack[objIdx] is ILTypeInstance refIns)
                                    {
                                        // neo-byref-ldind-ref-heap: a byref produced by
                                        // `ldflda &heapInstance.<refField>` (a reference field of
                                        // a heap IL class). objIdx is the IL instance's mStack
                                        // index; off is the field's ReferenceOffset (stamped by
                                        // the Ldflda heap arm via the heapIlRefFieldMarker).
                                        // Store the NEW object into the instance's
                                        // ManagedObjects[off]. Dispatch is content-based
                                        // (mStack[objIdx] is ILTypeInstance), placed AFTER the
                                        // F-7B bit-30 caller-owned-slot flag + the CLR-array /
                                        // CLR-object arms, so it never collides with non-
                                        // deterministic CLR field-hash offset values. (A heap IL
                                        // ref field is the ONLY `ldind_ref`/`stind_ref` target
                                        // whose mStack slot is an ILTypeInstance; the F-7B
                                        // caller-owned-slot reference-byref sets the bit-30 flag
                                        // and is handled above, so an F-7B-promoted IL-class
                                        // referent never reaches this branch.)
                                        refIns.ManagedObjects[off] = vIdx >= 0 ? mStack[vIdx] : null;
                                    }
                                    else
                                    {
                                        throw new NotImplementedException(
                                            "Step 17: stind_ref on an unsupported byref shape (not frame-native / caller-owned-slot / CLR-array / CLR-object / heap-IL-ref-field)");
                                    }
                                }
                                break;
                            case OpCodeREnum.Ldind_Ref:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    if (objIdx == -1)
                                    {
                                        // Frame-native: read the mStack index at the
                                        // offset and materialize the object into the
                                        // dest ref slot.
                                        srcIdx = *(int*)(frameBase + off);
                                        if (srcIdx >= 0)
                                        {
                                            dstIdx = frameRefBase + ip->Operand3;
                                            mStack[dstIdx] = mStack[srcIdx];
                                            *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                        }
                                        else
                                            *(int*)(frameBase + ip->DstOffset) = -1;
                                    }
                                    else if ((off & JITCompiler.NeoF10ByrefOffsetFlag) != 0)
                                    {
                                        // F-7B: caller-owned-mStack-slot byref (a cross-frame
                                        // reference-byref promoted in NeoRunDelegateTargetOnThis).
                                        // objIdx is the stable caller-owned slot holding the
                                        // referent object; materialize it into the callee's dest
                                        // ref slot for the READ path.
                                        object elem = mStack[objIdx];
                                        if (elem != null)
                                        {
                                            dstIdx = frameRefBase + ip->Operand3;
                                            mStack[dstIdx] = elem;
                                            *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                        }
                                        else
                                            *(int*)(frameBase + ip->DstOffset) = -1;
                                    }
                                    else if (mStack[objIdx] is Array cArr)
                                    {
                                        // CLR array target (ldelema-produced object[]
                                        // /string[] address): read the element and
                                        // materialize it into the dest ref slot.
                                        object elem = cArr.GetValue(off);
                                        if (elem != null)
                                        {
                                            dstIdx = frameRefBase + ip->Operand3;
                                            mStack[dstIdx] = elem;
                                            *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                        }
                                        else
                                            *(int*)(frameBase + ip->DstOffset) = -1;
                                    }
                                    else if (NeoIsClrObject(mStack, objIdx))
                                    {
                                        // 4d: CLR-object reference-type field -- read it
                                        // via the field-hash accessor and materialize.
                                        object elem = NeoReadClrObjectField(AppDomain, mStack[objIdx], off);
                                        if (elem != null)
                                        {
                                            dstIdx = frameRefBase + ip->Operand3;
                                            mStack[dstIdx] = elem;
                                            *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                        }
                                        else
                                            *(int*)(frameBase + ip->DstOffset) = -1;
                                    }
                                    else if (mStack[objIdx] is ILTypeInstance refIns)
                                    {
                                        // neo-byref-ldind-ref-heap: a byref produced by
                                        // `ldflda &heapInstance.<refField>` (a reference field of
                                        // a heap IL class). objIdx is the IL instance's mStack
                                        // index; off is the field's ReferenceOffset (stamped by
                                        // the Ldflda heap arm via the heapIlRefFieldMarker).
                                        // Read ManagedObjects[off] and materialize into the dest
                                        // ref slot. Content-based dispatch (placed AFTER the
                                        // F-7B bit-30 caller-owned-slot flag + the CLR-array /
                                        // CLR-object arms) avoids collision with non-deterministic
                                        // CLR field-hash offset values. (See Stind_Ref for the
                                        // F-7B-promoted-IL-class-referent rationale.)
                                        object elem = refIns.ManagedObjects[off];
                                        if (elem != null)
                                        {
                                            dstIdx = frameRefBase + ip->Operand3;
                                            mStack[dstIdx] = elem;
                                            *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                        }
                                        else
                                            *(int*)(frameBase + ip->DstOffset) = -1;
                                    }
                                    else
                                    {
                                        throw new NotImplementedException(
                                            "Step 17: ldind_ref on an unsupported byref shape (not frame-native / caller-owned-slot / CLR-array / CLR-object / heap-IL-ref-field)");
                                    }
                                }
                                break;
                            // Stobj / Ldobj: value-type-sized copy through the
                            // pointer. Operand = type token (sized via the domain).
                            case OpCodeREnum.Stobj:
                                {
                                    int objIdx = *(int*)(frameBase + ip->DstOffset + 0);
                                    int off = *(int*)(frameBase + ip->DstOffset + 4);
                                    t = AppDomain.GetType(ip->Operand);
                                    ilType = t as ILType;
                                    int primSize = ilType != null ? ilType.TotalPrimitiveSize : AppDomain.GetPrimitiveSize(t);
                                    int refCount = ilType != null ? ilType.TotalReferenceCount : 0;
                                    if (objIdx == -1)
                                    {
                                        Unsafe.CopyBlock(frameBase + off, frameBase + ip->SrcOffset, (uint)primSize);
                                        // Step 17 (b) (neo-step17-stobj-refloop): a value type
                                        // WITH reference fields copied through a frame-native
                                        // byref needs its ref-region half copied too (the
                                        // primitive CopyBlock above only covers TotalPrimitiveSize).
                                        // The byref carries the dest local's primitive byte offset
                                        // (`off`) but NOT its ref-region mStack base. Recover the
                                        // dest + source ref bases via the localInfos scan (R2:
                                        // the byref resolves to a direct local -- dump-confirmed).
                                        // Mirrors Move_Vt's mStack-to-mStack ref copy.
                                        if (refCount > 0)
                                        {
                                            int dstRefBase = -1, srcRefBase = -1;
                                            if (localInfos != null)
                                            {
                                                for (int li = 0; li < localInfos.Length; li++)
                                                {
                                                    var sl = localInfos[li];
                                                    if (sl.Offset == off) dstRefBase = sl.RefOffset;
                                                    if (sl.Offset == ip->SrcOffset) srcRefBase = sl.RefOffset;
                                                }
                                            }
                                            if (dstRefBase < 0 || srcRefBase < 0)
                                            {
                                                throw new NotImplementedException(
                                                    "Step 17: stobj of an IL value type WITH reference fields through a non-direct-local byref (nested-field via ldflda) is deferred (ref-region base recovery; follow-up)");
                                            }
                                            for (int i = 0; i < refCount; i++)
                                                mStack[frameRefBase + dstRefBase + i] = mStack[frameRefBase + srcRefBase + i];
                                        }
                                    }
                                    else if (NeoIsClrObject(mStack, objIdx))
                                    {
                                        // 4d: a CLR-object value-type field -- box the
                                        // src flat bytes into the element type and write
                                        // via the field-hash accessor.
                                        int srcCur = ip->SrcOffset;
                                        object boxed = ILIntepreter.ReadNeoValueType(t.TypeForCLR, frameBase, ref srcCur, primSize);
                                        NeoWriteClrObjectField(AppDomain, mStack[objIdx], off, boxed);
                                    }
                                    else
                                    {
                                        ins = GetNeoILInstance(mStack, objIdx);
                                        // F-10: a byref produced by `ldflda &instance.<clrStructField>`
                                        // carries (objIdx, ReferenceOffset | flag). The field's storage
                                        // is the boxed CLR struct at ManagedObjects[ReferenceOffset].
                                        if ((off & JITCompiler.NeoF10ByrefOffsetFlag) != 0)
                                        {
                                            int f10RefOff = off & ~JITCompiler.NeoF10ByrefOffsetFlag;
                                            int f10Cur = ip->SrcOffset;
                                            object f10Boxed = ILIntepreter.ReadNeoValueType(t.TypeForCLR, frameBase, ref f10Cur, primSize);
                                            ins.ManagedObjects[f10RefOff] = f10Boxed;
                                        }
                                        else
                                        {
                                            // IL value-type field: copy primitives into
                                            // Primitives and ref slots into ManagedObjects.
                                            ref byte dstP = ref ins.Primitives[off];
                                        Unsafe.CopyBlock(ref dstP, ref *(frameBase + ip->SrcOffset), (uint)primSize);
                                        if (refCount > 0)
                                        {
                                            // Step 17 (b): the IL-instance byref's ref region
                                            // IS ins.ManagedObjects. The src value is a frame-
                                            // native local (the byref target is the IL instance;
                                            // the src value lives in the frame). Recover the src
                                            // ref base via the localInfos scan and copy the ref
                                            // slots into the instance's ManagedObjects.
                                            int srcRefBase = -1;
                                            if (localInfos != null)
                                            {
                                                for (int li = 0; li < localInfos.Length; li++)
                                                    if (localInfos[li].Offset == ip->SrcOffset)
                                                    { srcRefBase = localInfos[li].RefOffset; break; }
                                            }
                                            // Step 17 (b) (M1, review-loop): mirror the frame-native
                                            // branch -- a scan-miss is an exotic byref shape we do NOT
                                            // resolve. Fail LOUD (tagged NIE) instead of silently
                                            // skipping the ref copy and leaving stale/null ref slots
                                            // in the instance's ManagedObjects (silent corruption).
                                            if (srcRefBase < 0)
                                            {
                                                throw new NotImplementedException(
                                                    "Step 17: stobj of an IL-instance VT field WITH reference fields from a non-direct-local value (nested-field/temp) is deferred (ref-region base recovery; follow-up)");
                                            }
                                            var dstRefs = ins.ManagedObjects;
                                            int srcBase = frameRefBase + srcRefBase;
                                            for (int i = 0; i < refCount; i++)
                                                dstRefs[i] = mStack[srcBase + i];
                                        }
                                        } // end F-10 else (existing IL-VT-field Primitives path)
                                    }
                                }
                                break;
                            case OpCodeREnum.Ldobj:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    t = AppDomain.GetType(ip->Operand);
                                    ilType = t as ILType;
                                    int primSize = ilType != null ? ilType.TotalPrimitiveSize : AppDomain.GetPrimitiveSize(t);
                                    int refCount = ilType != null ? ilType.TotalReferenceCount : 0;
                                    if (objIdx == -1)
                                    {
                                        Unsafe.CopyBlock(frameBase + ip->DstOffset, frameBase + off, (uint)primSize);
                                        // Step 17 (b) (neo-step17-stobj-refloop): mirror of the
                                        // Stobj arm's ref-region copy. The byref (SrcOffset) carries
                                        // the SOURCE local's primitive byte offset (`off`) but NOT
                                        // its ref-region mStack base; recover the src + dst ref bases
                                        // via the localInfos scan (R2; dump-confirmed direct-local).
                                        if (refCount > 0)
                                        {
                                            int srcRefBase = -1, dstRefBase = -1;
                                            if (localInfos != null)
                                            {
                                                for (int li = 0; li < localInfos.Length; li++)
                                                {
                                                    var sl = localInfos[li];
                                                    if (sl.Offset == off) srcRefBase = sl.RefOffset;
                                                    if (sl.Offset == ip->DstOffset) dstRefBase = sl.RefOffset;
                                                }
                                            }
                                            if (srcRefBase < 0 || dstRefBase < 0)
                                            {
                                                throw new NotImplementedException(
                                                    "Step 17: ldobj of an IL value type WITH reference fields through a non-direct-local byref (nested-field via ldflda) is deferred (ref-region base recovery; follow-up)");
                                            }
                                            for (int i = 0; i < refCount; i++)
                                                mStack[frameRefBase + dstRefBase + i] = mStack[frameRefBase + srcRefBase + i];
                                        }
                                    }
                                    else if (NeoIsClrObject(mStack, objIdx))
                                    {
                                        // 4d: a CLR-object value-type field -- read via
                                        // the field-hash accessor and flatten into dest.
                                        object val = NeoReadClrObjectField(AppDomain, mStack[objIdx], off);
                                        int dstOff = ip->DstOffset;
                                        ILIntepreter.WriteNeoValueType(val, frameBase + dstOff, primSize);
                                    }
                                    else
                                    {
                                        ins = GetNeoILInstance(mStack, objIdx);
                                        // F-10: a byref produced by `ldflda &instance.<clrStructField>`
                                        // carries (objIdx, ReferenceOffset | flag). Read the boxed CLR
                                        // struct at ManagedObjects[ReferenceOffset] and flatten it into
                                        // the dest (the value-type-sized copy target is the boxed struct).
                                        if ((off & JITCompiler.NeoF10ByrefOffsetFlag) != 0)
                                        {
                                            int f10RefOff = off & ~JITCompiler.NeoF10ByrefOffsetFlag;
                                            object f10Boxed = ins.ManagedObjects[f10RefOff];
                                            if (f10Boxed != null)
                                                ILIntepreter.WriteNeoValueType(f10Boxed, frameBase + ip->DstOffset, primSize);
                                            else
                                                Unsafe.InitBlock(frameBase + ip->DstOffset, 0, (uint)primSize);
                                        }
                                        else
                                        {
                                            ref byte srcP = ref ins.Primitives[off];
                                            Unsafe.CopyBlock(ref *(frameBase + ip->DstOffset), ref srcP, (uint)primSize);
                                            if (refCount > 0)
                                            {
                                            // Step 17 (b): the IL-instance byref's ref region IS
                                            // ins.ManagedObjects. Read the ref slots out into the
                                            // dest value local's frame ref region.
                                            int dstRefBase = -1;
                                            if (localInfos != null)
                                            {
                                                for (int li = 0; li < localInfos.Length; li++)
                                                    if (localInfos[li].Offset == ip->DstOffset)
                                                    { dstRefBase = localInfos[li].RefOffset; break; }
                                            }
                                            // Step 17 (b) (M1, review-loop): mirror the frame-native
                                            // branch -- a scan-miss is an exotic byref shape we do NOT
                                            // resolve. Fail LOUD (tagged NIE) instead of silently
                                            // skipping the ref copy and leaving stale/null ref slots
                                            // in the dest local's frame ref region (silent corruption).
                                            if (dstRefBase < 0)
                                            {
                                                throw new NotImplementedException(
                                                    "Step 17: ldobj of an IL-instance VT field WITH reference fields into a non-direct-local dest (nested-field/temp) is deferred (ref-region base recovery; follow-up)");
                                            }
                                            var srcRefs = ins.ManagedObjects;
                                            int dstBase = frameRefBase + dstRefBase;
                                            for (int i = 0; i < refCount; i++)
                                                mStack[dstBase + i] = srcRefs[i];
                                        }
                                        } // end F-10 else (existing IL-VT-field Primitives path)
                                    }
                                }
                                break;
                            // Step 17 (D-LDELEMA): produce a Ref Slot addressing
                            // array element `elementIdx`. The green target is an
                            // IL value-type array (ILTypeInstance[] with pre-
                            // instantiated elements, the Step 16 representation):
                            // resolve the element ILTypeInstance and park it on
                            // mStack, then encode (mStackIdx, 0) so the consumers
                            // (stind/ldind) hit the standard IL-instance Primitives
                            // path on that element instance. The element reference
                            // is rooted by mStack for the pointer's lifetime (the
                            // frame's mStack range is torn down on method exit).
                            case OpCodeREnum.Ldelema:
                                {
                                    int arrIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (arrIdx < 0) throw new NullReferenceException();
                                    int elementIdx = *(int*)(frameBase + ip->Operand4);
                                    Array la = (Array)mStack[arrIdx];
                                    if (la is ILTypeInstance[] ilArr)
                                    {
                                        ILTypeInstance elem = ilArr[elementIdx];
                                        if (elem == null)
                                            throw new NullReferenceException();
                                        int elemMStackIdx = mStack.Count;
                                        mStack.Add(elem);
                                        *(int*)(frameBase + ip->DstOffset + 0) = elemMStackIdx;
                                        *(int*)(frameBase + ip->DstOffset + 4) = 0;
                                    }
                                    else
                                    {
                                        // Step 17 D-LDELEMA remainder: a CLR primitive / struct
                                        // array. Encode (arrIdx, elementIdx) where the `off` half
                                        // IS the element index (NOT a byte offset) -- the
                                        // consumer (stind/ldind) detects `mStack[arrIdx] is Array`
                                        // and routes to la.GetValue(elementIdx) / la.SetValue(..).
                                        // The standard IL-instance Primitives path does NOT apply
                                        // (a CLR array element lives in the Array's backing
                                        // storage, not in Primitives). Scoped to the smoke's green
                                        // target (primitive element read/write via stind_i4/
                                        // ldind_i4); other element kinds are NIE-tagged in the
                                        // consumer arm. Validate the element is a value type so
                                        // the element-index encoding is well-formed.
                                        Type elemClrType = la.GetType().GetElementType();
                                        if (elemClrType == null || !elemClrType.IsValueType)
                                            throw new NotImplementedException(
                                                "Step 17: ldelema on a CLR array with a reference-type element is deferred (use direct indexing)");
                                        *(int*)(frameBase + ip->DstOffset + 0) = arrIdx;
                                        *(int*)(frameBase + ip->DstOffset + 4) = elementIdx;
                                    }
                                }
                                break;
                            // Step 14: exception handling. Throw reads the exception
                            // object from its register-1 ref slot (Register1 is a raw
                            // register index -- Throw is NOT lowered by LowerNeoOffsets,
                            // so resolve it via localInfos, exactly like Ret reads its
                            // source). The frame byte at localInfos[Register1].Offset
                            // holds the mStack index of the exception object.
                            case OpCodeREnum.Throw:
                                {
                                    int exByteOff = localInfos[ip->Register1].Offset;
                                    int exIdx = *(int*)(frameBase + exByteOff);
                                    Exception ex = GetNeoException(mStack, exIdx);
                                    throw ex;
                                }
                            // Step 14: rethrow the in-flight exception captured by the
                            // outer catch into lastCaughtEx.
                            case OpCodeREnum.Rethrow:
                                throw lastCaughtEx;
                            // Neo: Nop is a true no-op (the optimizer strips nops in
                            // normal methods, but the async state machine's exception-
                            // region structure can surface one). Mirror Leave/Endfinally
                            // which ARE handled. (neo-step20-async-suspend B3.)
                            case OpCodeREnum.Nop:
                                ip++;
                                continue;
                            // Step 14: Leave / Leave_S. Route through any enclosing
                            // finally whose try range straddles the leave boundary
                            // (port of Legacy ILIntepreter.Register.cs:2765-2783).
                            // ip->Operand is the leave target byte offset.
                            case OpCodeREnum.Leave:
                            case OpCodeREnum.Leave_S:
                                {
                                    if (ehs != null)
                                    {
                                        int addr = (int)(ip - ptr);
                                        var eh = FindExceptionHandlerByBranchTarget(addr, ip->Operand, ehs);
                                        if (eh != null)
                                        {
                                            finallyEndAddress = ip->Operand;
                                            ip = ptr + eh.HandlerStart;
                                            continue;
                                        }
                                    }
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                            // Step 14: Endfinally. If the finally was entered for an
                            // in-flight exception (finallyEndAddress < 0 sentinel set
                            // in HandleException), re-throw lastCaughtEx so the search
                            // resumes. Otherwise resume at the recorded leave target,
                            // routing through any further enclosing finally. Port of
                            // Legacy ILIntepreter.Register.cs:2785-2806.
                            case OpCodeREnum.Endfinally:
                                {
                                    if (finallyEndAddress < 0)
                                    {
                                        finallyEndAddress = 0;
                                        throw lastCaughtEx;
                                    }
                                    int addr = (int)(ip - ptr);
                                    var eh = FindExceptionHandlerByBranchTarget(addr, finallyEndAddress, ehs);
                                    if (eh != null)
                                    {
                                        ip = ptr + eh.HandlerStart;
                                        continue;
                                    }
                                    ip = ptr + finallyEndAddress;
                                    finallyEndAddress = 0;
                                    continue;
                                }
                            // IL filter blocks (filter / endfilter): out of scope for
                            // Step 14 (rare in C#). Remains a Step-tagged NIE.
                            case OpCodeREnum.Endfilter:
                                throw new NotImplementedException("Neo: IL filter blocks (endfilter) are not implemented (Step 14, out of scope)");
                            // Step 17 D-CONSTRAINED follow-up (constrained.callvirt on a
                            // value type). The JIT emits [Push..., Constrained T, Callvirt M]
                            // (Constrained BEFORE the callvirt); the Constrained arm carries
                            // the constrained type token (Operand) and OWNS the dispatch
                            // (box-once for CLR value types / overrides-of-Object; direct-call
                            // for IL value-type interface impls), then skips the trailing
                            // callvirt. See the arm body for the full mechanism + the residual
                            // NIE-guarded sub-cases (IL VT with ref fields; null/ref-type this).
                            case OpCodeREnum.Constrained:
                                {
                                    // Step 17 D-CONSTRAINED follow-up: constrained.callvirt
                                    // dispatch on a value type. The JIT emits the sequence
                                    // [Push..., Constrained T, Callvirt M] -- the Constrained
                                    // arm runs FIRST (carrying the constrained type token in
                                    // ip->Operand), and the trailing Callvirt at ip+1 carries
                                    // the method token (Operand2), the param map (Operand),
                                    // and the return-slot info. The Constrained arm OWNS the
                                    // dispatch (box-once on the byref `this`), then advances
                                    // past the trailing callvirt (ip += 2). REUSES Step 13's
                                    // Box-arm machinery (ReadNeoValueType / CopyFrameToIL) +
                                    // the existing Callvirt_IL/Callvirt_CLR resolvers on the
                                    // boxed receiver. The non-constrained Callvirt paths are
                                    // byte-identical (this arm only fires for Constrained).
                                    IType constrainedType = AppDomain.GetType(ip->Operand);
                                    OpCodeR* cv = ip + 1;
                                    OpCodeREnum cvCode = cv->Code;
                                    if (cvCode != OpCodeREnum.Callvirt &&
                                        cvCode != OpCodeREnum.Callvirt_IL &&
                                        cvCode != OpCodeREnum.Callvirt_CLR &&
                                        cvCode != OpCodeREnum.Callvirt_Interface &&
                                        cvCode != OpCodeREnum.Call_Redirect)
                                    {
                                        throw new NotImplementedException(
                                            "Step 17: Constrained not immediately followed by a callvirt (unexpected JIT shape)");
                                    }
                                    IMethod targetMethod = AppDomain.GetMethod(cv->Operand2);
                                    if (targetMethod == null)
                                    {
                                        ip += 2;
                                        continue;
                                    }
                                    int callParamIdx = cv->Operand;
                                    ref var cmap = ref nf.NeoCallParams[callParamIdx];
                                    byte* targetBase = newEsp;
                                    // Copy the NON-`this` args into the callee param region.
                                    // Slot 0 (the `this`) is handled below (box-once writes
                                    // the boxed receiver's mStack index into the dest ref
                                    // slot); CopyNeoCallArguments would mis-copy the 8-byte
                                    // byref into the 4-byte ref dest, so skip slot 0 here and
                                    // copy the rest verbatim.
                                    if (cmap.PrimitiveSize != null)
                                    {
                                        for (int i = 1; i < cmap.PrimitiveSize.Length; i++)
                                        {
                                            Unsafe.CopyBlock(targetBase + cmap.PrimitiveDst[i],
                                                frameBase + cmap.PrimitiveSrc[i],
                                                cmap.PrimitiveSize[i]);
                                        }
                                    }
                                    // Note: NO ref-region copy here. The standard call paths
                                    // (Callvirt_CLR / Callvirt_Interface -> CopyNeoCallArguments)
                                    // copy ONLY the primitive bytes for a ref-typed param and let
                                    // the callee read the object by its mStack index (stored in
                                    // the param's first 4 primitive bytes). A ref-region copy
                                    // here would (a) mis-read the source -- cmap.RefSrc[i] is a
                                    // RefOffset (an index into mStack[frameRefBase+...]), NOT a
                                    // frame byte offset, so `*(int*)(frameBase + RefSrc[i])` reads
                                    // garbage -- and (b) OVERWRITE the caller's own mStack object
                                    // slot (mStack[frameRefBase + RefDst[i]]), destroying the very
                                    // object the callee's primitive index points at. This was the
                                    // F3 "accepted-known" loop (never exercised for a ref-type T
                                    // before Gap A): for T=string, it nulled the `y` arg to
                                    // CompareTo, producing a wrong result. The IL-VT-with-ref-fields
                                    // `this` case is owned by the direct-call path above
                                    // (constrainedSlot0Seed), not this box-once path, so dropping
                                    // this loop loses nothing. Mirrors Legacy ExecuteR (the
                                    // constrained arm does not touch the non-`this` ref region).

                                    // Resolve the byref `this` source (slot 0): an 8-byte Ref
                                    // Slot produced by ldloca/ldarga addressing the struct.
                                    int thisSrcOff = cmap.PrimitiveSrc[0];
                                    int thisObjIdx = *(int*)(frameBase + thisSrcOff);
                                    int thisByteOff = *(int*)(frameBase + thisSrcOff + 4);

                                    byte* retDstPtr = cv->Register1 >= 0 ? frameBase + cv->DstOffset : null;
                                    int targetRetRefBase = cv->Register1 >= 0 ? frameRefBase + cv->Operand3 : -1;

                                    // Resolve the constrained type's concrete override of M (NOT
                                    // the call-site static method, and NOT the bogus `Operand4`
                                    // slot which for a constrained callvirt is just the 0x1 flag).
                                    IMethod actualMethod = null;
                                    if (constrainedType is ILType ctIl)
                                        actualMethod = ctIl.GetVirtualMethod(targetMethod);
                                    if (actualMethod == null)
                                        actualMethod = targetMethod;

                                    // Dispatch shape keys on the resolved method's kind + the
                                    // constrained type:
                                    //  * IL value type -> direct-call (the override is an ILMethod
                                    //    whose body uses in-frame Ldfld_Inline; pass the struct's
                                    //    FLAT BYTES into the callee slot-0 frame region, exactly
                                    //    like `local.VTMethod()` via area4's PrimitiveByRefSrc).
                                    //    NO box: a Neo IL-struct method body cannot consume a boxed
                                    //    ILTypeInstance `this`.
                                    //  * CLR value type (override resolves to a CLRMethod, e.g.
                                    //    Int32.ToString) -> box-once into a boxed CLR object and
                                    //    dispatch via the CLR method (reflection/redirect), which
                                    //    expects a boxed `this`.
                                    //  * Already-boxed / reference-type `this` (objIdx >= 0) -> the
                                    //    box-once is a no-op; dispatch on the object.
                                    if (actualMethod is ILMethod ilmOverride && thisObjIdx < 0 &&
                                        constrainedType is ILType ilConstrained && ilConstrained.IsValueType)
                                    {
                                        // Direct-call path: copy the struct's flat primitive bytes
                                        // into the callee's slot-0 frame region (the override reads
                                        // `this` via in-frame Ldfld_Inline from its ParamInfos[0]).
                                        var calleeFrame = ilmOverride.CompiledFrame;
                                        var thisSlotInfo = calleeFrame.ParamInfos[0];
                                        if (ilConstrained.TotalPrimitiveSize > 0)
                                        {
                                            Unsafe.CopyBlock(targetBase + thisSlotInfo.Offset,
                                                frameBase + thisByteOff,
                                                (uint)ilConstrained.TotalPrimitiveSize);
                                        }
                                        // Step 17 (b) (neo-step17-stobj-refloop): seed the callee
                                        // slot-0 REF region from the caller's struct-local ref
                                        // region. The byref carries the source local's primitive
                                        // byte offset (thisByteOff) but NOT its ref-region mStack
                                        // base; recover it via the localInfos scan (R2). The seed
                                        // runs inside the callee's ExecuteNeo (after its mStack
                                        // reservation, before the body) via the constrained-slot-0
                                        // hook params -- the callee reserves its own frameRefBase,
                                        // so a pre-call write to mStack[Count+...] would be
                                        // clobbered by the reservation's zeroing.
                                        int conSrcLocalRefOffset = -1;
                                        if (ilConstrained.TotalReferenceCount > 0)
                                        {
                                            if (localInfos != null)
                                            {
                                                for (int li = 0; li < localInfos.Length; li++)
                                                    if (localInfos[li].Offset == thisByteOff)
                                                    { conSrcLocalRefOffset = localInfos[li].RefOffset; break; }
                                            }
                                            if (conSrcLocalRefOffset < 0)
                                            {
                                                throw new NotImplementedException(
                                                    "Step 17: constrained.callvirt on an IL value type WITH reference fields through a non-direct-local byref is deferred (ref-region base recovery; follow-up)");
                                            }
                                        }
                                        int conSeedSrcRefBase = ilConstrained.TotalReferenceCount > 0
                                            ? (frameRefBase + conSrcLocalRefOffset) : -1;
                                        // Call the callee's ExecuteNeo directly (not via
                                        // InvokeNeoCallTarget) to pass the slot-0 seed hook params.
                                        ExecuteNeo(ilmOverride, targetBase, retDstPtr, targetRetRefBase, out unhandledException,
                                            constrainedSlot0SeedRefOffset: thisSlotInfo.RefOffset,
                                            constrainedSlot0SeedSrcRefBase: conSeedSrcRefBase,
                                            constrainedSlot0SeedRefCount: ilConstrained.TotalReferenceCount);
                                        if (unhandledException)
                                            return null;
                                    }
                                    else
                                    {
                                        // Box-once path (CLR value type, or already-boxed receiver).
                                        object boxedReceiver = null;
                                        // Gap A: reference-type constrained T (e.g. T=string, a CLRType
                                        // that is NOT a value type). ECMA III.3.19 constrained. on a
                                        // reference type is a plain callvirt on the object -- NO box. The
                                        // JIT emits the constrained `this` as a managed pointer
                                        // (ldarga/ldloca), so slot-0's source is an 8-byte frame-native
                                        // byref (thisObjIdx == -1, thisByteOff == the receiver slot's byte
                                        // offset). The receiver object's mStack index lives at
                                        // *(int*)(frameBase + thisByteOff). Deref it and use the object
                                        // as-is. Mirrors Legacy ExecuteR Constrained ref-type path
                                        // (ILIntepreter.Register.cs:3936-3937: insIdx = objRef->Value).
                                        if (boxedReceiver == null && constrainedType != null &&
                                            !constrainedType.IsValueType && thisObjIdx < 0)
                                        {
                                            int recvIdx = *(int*)(frameBase + thisByteOff);
                                            boxedReceiver = (recvIdx >= 0 && recvIdx < mStack.Count) ? mStack[recvIdx] : null;
                                        }
                                        if (thisObjIdx >= 0)
                                        {
                                            boxedReceiver = mStack[thisObjIdx];
                                        }
                                        else if (constrainedType is ILType ilBoxType && ilBoxType.IsValueType)
                                        {
                                            // Review-loop round 1 (F1 fix): an IL value type whose
                                            // constrained callvirt resolves to an INHERITED CLRMethod
                                            // (Object.ToString / ValueType.GetHashCode / Object.Equals
                                            // -- i.e. NO IL override on the struct). The discriminator's
                                            // ILMethod-direct-call gate (above) does NOT fire (actualMethod
                                            // is a CLRMethod, not an ILMethod), so execution lands here.
                                            // The previous code fell through to the generic
                                            // `constrainedType.IsValueType && clrT != null` branch below,
                                            // where `clrT = ilBoxType.TypeForCLR = ILTypeInstance` (a CLASS,
                                            // not a value type) and `ReadNeoValueType(typeof(ILTypeInstance),
                                            // frameBase + thisByteOff, ...)` interpreted the struct's FLAT
                                            // BYTES as an ILTypeInstance shape -> corrupt boxed receiver ->
                                            // native segfault (ToString) / NRE (GetHashCode). REGRESSION over
                                            // HEAD's clean Step-17 NIE.
                                            //
                                            // Correct fix: box the IL struct into a REAL ILTypeInstance
                                            // (the IL box representation), reusing Step 13's Box-arm
                                            // machinery (`ilType.Instantiate(false)` + `CopyFrameToIL`).
                                            // The inherited CLRMethod (Object.ToString etc.) is then
                                            // dispatched on the boxed ILTypeInstance via reflection --
                                            // which, for Object.ToString, calls the host ILTypeInstance
                                            // override (returns the type's full name when no IL ToString
                                            // override exists). This makes `anyIlStruct.ToString()` /
                                            // string interpolation actually WORK (high-value -- debugging,
                                            // logging) instead of crashing.
                                            //
                                            // Step 17 (b) (neo-step17-stobj-refloop): an IL VT WITH
                                            // reference fields boxes cleanly now -- recover the source
                                            // local's ref base via the localInfos scan (R2) and pass the
                                            // real refOffset + refCount to CopyFrameToIL (it iterates
                                            // ManagedObjects). The byref carries the primitive byte
                                            // offset but NOT the ref base; the scan recovers it for a
                                            // direct local (the green target). A non-direct-local byref
                                            // stays a tagged NIE.
                                            int boxSrcRefOffset = 0;
                                            if (ilBoxType.TotalReferenceCount > 0)
                                            {
                                                boxSrcRefOffset = -1;
                                                if (localInfos != null)
                                                {
                                                    for (int li = 0; li < localInfos.Length; li++)
                                                        if (localInfos[li].Offset == thisByteOff)
                                                        { boxSrcRefOffset = localInfos[li].RefOffset; break; }
                                                }
                                                if (boxSrcRefOffset < 0)
                                                {
                                                    throw new NotImplementedException(
                                                        "Step 17: constrained.callvirt on an IL value type WITH reference fields (inherited CLR method) through a non-direct-local byref is deferred (ref-region base recovery; follow-up)");
                                                }
                                            }
                                            ILTypeInstance ilBox = ilBoxType.Instantiate(false);
                                            CopyFrameToIL(frameBase, thisByteOff, boxSrcRefOffset,
                                                ilBoxType.TotalPrimitiveSize, ilBoxType.TotalReferenceCount,
                                                mStack, frameRefBase, ilBox);
                                            ilBox.Boxed = true;
                                            boxedReceiver = ilBox;
                                        }
                                        else if (constrainedType != null)
                                        {
                                            Type clrT = constrainedType.TypeForCLR;
                                            if (constrainedType.IsPrimitive)
                                            {
                                                int psz = AppDomain.GetPrimitiveSize(constrainedType);
                                                boxedReceiver = NeoBoxReturnValue(constrainedType, frameBase + thisByteOff, psz);
                                            }
                                            else if (constrainedType.IsValueType && clrT != null && !(constrainedType is ILType))
                                            {
                                                // A genuine CLR value type (the implementer's tested path:
                                                // e.g. Int32.ToString, a CLR struct override). Read the flat
                                                // managed bytes and box them. The `!(constrainedType is ILType)`
                                                // guard is the F1 fix's blast-radius tightening: an IL value
                                                // type's `TypeForCLR` is `ILTypeInstance` (a CLASS), which
                                                // must NOT reach `ReadNeoValueType` (handled in the IL-VT
                                                // branch above). Without this guard the F1 segfault recurs.
                                                int managedSz = Optimizer.GetNeoValueTypeManagedSize(clrT);
                                                int cur = 0;
                                                boxedReceiver = ReadNeoValueType(clrT, frameBase + thisByteOff, ref cur, managedSz);
                                            }
                                        }

                                        if (boxedReceiver == null)
                                        {
                                            throw new NotImplementedException(
                                                "Step 17: constrained.callvirt on a null or unsupported constrained type is not handled (box-once no-op / ref-type this)");
                                        }

                                        // Park the boxed receiver on mStack and write its index
                                        // into the callee param region's `this` ref slot (slot 0).
                                        int boxedIdx = mStack.Count;
                                        mStack.Add(boxedReceiver);
                                        *(int*)(targetBase + cmap.PrimitiveDst[0]) = boxedIdx;

                                        if (actualMethod is CLRMethod clrDispatch)
                                            InvokeNeoClrMethod(clrDispatch, false, targetBase, mStack, retDstPtr, targetRetRefBase);
                                        else if (actualMethod is ILMethod ilmBox)
                                        {
                                            if (!InvokeNeoCallTarget(ilmBox, false, targetBase, mStack, retDstPtr, targetRetRefBase, out unhandledException))
                                                return null;
                                        }
                                        else
                                            throw new NotImplementedException(
                                                "Step 17: constrained.callvirt could not resolve a concrete override on the boxed receiver");
                                    }

                                    ip += 2; // skip Constrained + the trailing callvirt
                                    continue;
                                }
                            default:
                                {
                                    // Permanent Neo dispatch guard (rasen neo-jit-bogus-opcode,
                                    // task 4.2). A Code outside the named OpCodeREnum range is
                                    // garbage (operand/register bytes read as Code via a
                                    // mis-targeted ip, or a future lowering regression). Throw a
                                    // locatable diagnostic BEFORE the garbage can alias a real
                                    // case label. Named-but-unimplemented opcodes (e.g. ldtoken)
                                    // fall through to the existing Step-6 message below.
                                    int _rawCode = (int)code;
                                    if (_rawCode < 0 || _rawCode >= NeoOpCodeCount)
                                    {
                                        int _idx = (int)(ip - ptr);
                                        throw new InvalidOperationException(
                                            "Neo: corrupt opcode " + _rawCode
                                            + " at " + method + ":" + _idx + "/" + body.Length
                                            + " R1=" + ip->Register1 + " R2=" + ip->Register2
                                            + " R3=" + ip->Register3 + " R4=" + ip->Register4
                                            + " Op=" + ip->Operand + " Op2=" + ip->Operand2
                                            + " Op3=" + ip->Operand3 + " Op4=" + ip->Operand4
                                            + " OpL=" + ip->OperandLong);
                                    }
                                    throw new NotImplementedException(string.Format("Neo: opcode {0} not yet implemented (Step 6)", code));
                                }
                        }
                        ip++;
                    }
                    catch (Exception ex)
                    {
                        var oriESP = (StackObject*)newEsp;
                        StackObject* tmpEsp = oriESP;
                        bool isJmp = HandleException(ex, ref tmpEsp, ehs, method, (int)(ip - ptr), ref frame, ref lastCaughtEx, ref unhandledException, ref finallyEndAddress, out int jmpTarget, out bool isCatch);
                        if (isCatch)
                        {
                            // Truncate mStack back to this frame's reserved region
                            int targetCount = frameRefBase + totalRefSize;
                            if (mStack.Count > targetCount)
                            {
                                mStack.RemoveRange(targetCount, mStack.Count - targetCount);
                            }
                            // Step 14: write the caught exception object into the catch
                            // handler's reserved exception-variable slot. The slot's
                            // byte offset and ref offset were stamped by the JIT
                            // (CompiledFrame.NeoCatchExceptionByteOffset /
                            // NeoCatchExceptionRefOffset) at the catch exception
                            // register's post-compaction index -- mirroring Legacy's
                            // AssignToRegister(exReg = paramCnt + locCnt, ex) at
                            // ILIntepreter.Register.cs:5326-5327. `ex` is already the
                            // unwrapped inner exception when the propagated object was
                            // an ILRuntimeException (HandleException does the unwrap),
                            // matching Legacy semantics. The slot is -1 only when the
                            // method has no catch handler (then isCatch can't be true).
                            int catchByteOff = nf.NeoCatchExceptionByteOffset;
                            int catchRefOff = nf.NeoCatchExceptionRefOffset;
                            if (catchByteOff >= 0)
                            {
                                int catchSlotIdx = frameRefBase + catchRefOff;
                                mStack[catchSlotIdx] = ex;
                                *(int*)(frameBase + catchByteOff) = catchSlotIdx;
                            }
                        }
                        if (isJmp)
                        {
                            ip = ptr + jmpTarget;
                            continue;
                        }
                        if (unhandledException)
                        {
                            // Step 14: re-throw path (e.g. Endfinally re-throws
                            // lastCaughtEx and no handler matches). Stash and let
                            // the bottom cleanup run before re-throwing.
                            pendingThrow = ex;
                            returned = true;
                            break;
                        }
                        unhandledException = true;
                        returned = true;
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
                        if (!AppDomain.DebugService.Break(this, ex))
#endif
                        {
                            // Step 14: stash the wrapped exception and break out
                            // so the bottom-of-method cleanup (frame pop + mStack
                            // truncate) runs BEFORE the re-throw -- making this
                            // Neo frame self-cleaning instead of relying on the
                            // caller's HandleException frame-pop loop to clean up
                            // a leaked frame/mStack reservation.
                            pendingThrow = new ILRuntimeException(ex.Message, this, method, oriESP, ex);
                            break;
                        }
                    }
                }
            }

            // VT-THIS-ADDR: the slot-0 -> caller-dest copy-back for a value-type
            // ctor invoked via the runtime Newobj IL-VT branch is performed in
            // the Ret arm ABOVE (before the mStack pop, which removes the slot-0
            // ref entries). Nothing to do here on the exception-unwind path: a
            // ctor that throws abandons the construction (no copy-back).

            // Unwind: pop frame, truncate mStack back to entry baseline.
            // Frames stack popping: best-effort (BasePointer compares by pointer).
            if (stack.Frames.Count > 0 && stack.Frames.Peek().BasePointer == frame.BasePointer)
            {
                stack.Frames.Pop();
            }
            if (mStack.Count > frameRefBase)
            {
                mStack.RemoveRange(frameRefBase, mStack.Count - frameRefBase);
            }

#if DEBUG && !NO_PROFILER
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == AppDomain.UnityMainThreadID)
#if UNITY_5_5_OR_NEWER
                UnityEngine.Profiling.Profiler.EndSample();
#else
                UnityEngine.Profiler.EndSample();
#endif
#endif
            // Step 14: if an exception escaped this frame unhandled, re-throw it
            // now -- AFTER the frame/mStack cleanup above, so this Neo frame is
            // self-cleaning. The exception propagates via the C# stack into the
            // caller's per-iteration catch, where HandleException searches the
            // CALLER's handler table at the call-site address (cross-frame
            // propagation). Because this frame was already popped and its mStack
            // reservation already released, the caller's HandleException
            // frame-pop loop finds the caller's frame on top and does not need
            // to clean up this frame.
            if (pendingThrow != null)
                throw pendingThrow;
            return frameBase;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ILTypeInstance GetNeoILInstance(AutoList mStack, int objIndex)
        {
            if (objIndex < 0)
                throw new NullReferenceException();
            object o = mStack[objIndex];
            if (o == null)
                // A null heap owner at a typed IL-instance field arm is a plain
                // NullReferenceException (ldfld/stfld/ldobj/stobj on null) -- NOT a
                // deferred feature. Throwing NRE here is CLR-faithful and avoids
                // masking an upstream materialization gap as a "Step 17/13b deferred"
                // NIE (the previous behaviour).
                throw new NullReferenceException();
            if (o is ILTypeInstance ins)
                return ins;
            // A CLR object reached a typed IL-instance field arm. The field was
            // JIT-classified as IL-declared (else the JIT emits the raw Ldfld/Stfld
            // opcode handled by the CLR field-hash path), so it lives on the
            // underlying ILTypeInstance. The common shape is a CrossBindingAdaptor
            // wrapper (an IL type that inherits a CLR base, flowed through CLR code
            // / reflection / a generic collection, comes back as its CLR adaptor) --
            // unwrap it (mirrors the raw Ldfld/Stfld handler, child 9).
            if (o is CrossBindingAdaptorType cba)
            {
                var il = cba.ILInstance;
                if (il == null)
                    throw new NullReferenceException();
                return il;
            }
            // Any other CLR shape is genuinely unexpected at a typed IL-field arm
            // (the byref consumers + the raw Stfld/Ldfld handlers route CLR objects
            // to the field-hash accessor BEFORE reaching here). Keep the defensive
            // Step-tagged NIE as the guard.
            throw new NotImplementedException(
                "Step 17/13b: field/element access on a CLR object via the IL-instance path is deferred (CLR field-hash plumbing lands in Step 13b). Owner type: " + o.GetType().FullName);
        }

        // ---- Step 13 Area 4d: CLR-object field access via field identity (the
        //      field-hash path). A `ref clrObj.field` produced by `ldflda` lands
        //      as a Ref Slot (clrObjMStackIdx, fieldHash) where fieldHash is the
        //      CLR FieldInfo's hash (stamped by the JIT's GetFieldOffset for a
        //      CLR declaring type as type.GetFieldIndex(token)). The stind/ldind/
        //      stobj/ldobj consumers + the 4c byref marshal call these helpers to
        //      route through the CLRType's reflection accessor (GetFieldValue /
        //      SetFieldValue). The hash is resolvable at runtime via the object's
        //      runtime CLRType -- no JIT stamp change is required (the hash is
        //      already the offset half). ----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static object NeoReadClrObjectField(ILRuntime.Runtime.Enviorment.AppDomain appdomain, object target, int fieldHash)
        {
            if (target == null)
                throw new NullReferenceException();
            var ct = appdomain.GetType(target.GetType()) as CLRType;
            if (ct == null)
                throw new NotImplementedException("Step 13 Area 4d: CLR-object field read on a non-CLR-resolvable target. Type: " + target.GetType().FullName);
            object tmp = target;
            return ct.GetFieldValue(fieldHash, tmp);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void NeoWriteClrObjectField(ILRuntime.Runtime.Enviorment.AppDomain appdomain, object target, int fieldHash, object value)
        {
            if (target == null)
                throw new NullReferenceException();
            var ct = appdomain.GetType(target.GetType()) as CLRType;
            if (ct == null)
                throw new NotImplementedException("Step 13 Area 4d: CLR-object field write on a non-CLR-resolvable target. Type: " + target.GetType().FullName);
            object tmp = target;
            ct.SetFieldValue(fieldHash, ref tmp, value);
        }

        // Returns true if `mStack[objIdx]` is a CLR (non-IL, non-Array) object --
        // the discriminator for the 4d CLR-object-field path. Used by the
        // stind/ldind consumer arms BEFORE the ILTypeInstance fallback.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool NeoIsClrObject(AutoList mStack, int objIdx)
        {
            if (objIdx < 0)
                return false;
            object o = mStack[objIdx];
            if (o == null)
                return false;
            return !(o is ILTypeInstance) && !(o is Array);
        }


        // Step 14: resolve a Throw operand's exception object from its mStack ref
        // slot. A null exception object (ref index -1) is itself a
        // NullReferenceException in the CLR. The object may be an
        // ILRuntimeException wrapping a deeper IL-thrown exception; the shared
        // HandleException unwraps it on catch entry, so here we throw the object
        // as-is.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Exception GetNeoException(AutoList mStack, int objIndex)
        {
            if (objIndex < 0)
                throw new NullReferenceException();
            object o = mStack[objIndex];
            Exception ex = o as Exception;
            // D-IL-EXCEPTION-THROW: an IL-typed exception operand is an
            // ILTypeInstance (not a CLR Exception) -- unwrap its CLRInstance,
            // which is the ExceptionAdaptor's Adapter (a real CLR Exception)
            // established in the ILTypeInstance ctor. This is the same
            // IL<->CLR bridge used for CLR-method dispatch on an IL instance
            // (ILIntepreter.cs:2936 / AppDomain.cs:1450). The first `as` still
            // succeeds for every existing CLR-Exception operand, so this
            // fallback is unreachable for existing code (byte-identical Legacy
            // path on the CLR-Exception case).
            if (ex == null && o is ILTypeInstance ili)
                ex = ili.CLRInstance as Exception;
            if (ex == null)
                throw new NullReferenceException();
            return ex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe int ReadConvI4(byte* frameBase, ushort offset, NeoPrimitiveTypeTag tag)
        {
            unchecked
            {
                switch (tag)
                {
                    case NeoPrimitiveTypeTag.U4:
                        return (int)*(uint*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.I8:
                        return (int)*(long*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.U8:
                        return (int)*(ulong*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.R4:
                        return (int)*(float*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.R8:
                        return (int)*(double*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.I4:
                    default:
                        return *(int*)(frameBase + offset);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe uint ReadConvU4(byte* frameBase, ushort offset, NeoPrimitiveTypeTag tag)
        {
            unchecked
            {
                switch (tag)
                {
                    case NeoPrimitiveTypeTag.U4:
                        return *(uint*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.I8:
                        return (uint)*(long*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.U8:
                        return (uint)*(ulong*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.R4:
                        return (uint)*(float*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.R8:
                        return (uint)*(double*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.I4:
                    default:
                        return (uint)*(int*)(frameBase + offset);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe long ReadConvI8(byte* frameBase, ushort offset, NeoPrimitiveTypeTag tag)
        {
            unchecked
            {
                switch (tag)
                {
                    case NeoPrimitiveTypeTag.U4:
                        return *(uint*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.I8:
                        return *(long*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.U8:
                        return (long)*(ulong*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.R4:
                        return (long)*(float*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.R8:
                        return (long)*(double*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.I4:
                    default:
                        return *(int*)(frameBase + offset);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe ulong ReadConvU8(byte* frameBase, ushort offset, NeoPrimitiveTypeTag tag)
        {
            unchecked
            {
                switch (tag)
                {
                    case NeoPrimitiveTypeTag.U4:
                        return *(uint*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.I8:
                        return (ulong)*(long*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.U8:
                        return *(ulong*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.R4:
                        return (ulong)*(float*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.R8:
                        return (ulong)*(double*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.I4:
                    default:
                        return (ulong)*(int*)(frameBase + offset);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe float ReadConvR4(byte* frameBase, ushort offset, NeoPrimitiveTypeTag tag)
        {
            switch (tag)
            {
                case NeoPrimitiveTypeTag.U4:
                    return *(uint*)(frameBase + offset);
                case NeoPrimitiveTypeTag.I8:
                    return *(long*)(frameBase + offset);
                case NeoPrimitiveTypeTag.U8:
                    return *(ulong*)(frameBase + offset);
                case NeoPrimitiveTypeTag.R4:
                    return *(float*)(frameBase + offset);
                case NeoPrimitiveTypeTag.R8:
                    return (float)*(double*)(frameBase + offset);
                case NeoPrimitiveTypeTag.I4:
                default:
                    return *(int*)(frameBase + offset);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe double ReadConvR8(byte* frameBase, ushort offset, NeoPrimitiveTypeTag tag)
        {
            switch (tag)
            {
                case NeoPrimitiveTypeTag.U4:
                    return *(uint*)(frameBase + offset);
                case NeoPrimitiveTypeTag.I8:
                    return *(long*)(frameBase + offset);
                case NeoPrimitiveTypeTag.U8:
                    return *(ulong*)(frameBase + offset);
                case NeoPrimitiveTypeTag.R4:
                    return *(float*)(frameBase + offset);
                case NeoPrimitiveTypeTag.R8:
                    return *(double*)(frameBase + offset);
                case NeoPrimitiveTypeTag.I4:
                default:
                    return *(int*)(frameBase + offset);
            }
        }

        // Copies frame byte region + frame mStack refs into an ILTypeInstance.
        // Used by Box (frame → boxed instance).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe void CopyFrameToIL(byte* frameBase, int primOffset, int refOffset,
                                         int primSize, int refCount,
                                         AutoList mStack, int frameRefBase,
                                         ILTypeInstance dst)
        {
            if (primSize > 0 && dst.Primitives != null)
            {
                ref byte dstP = ref MemoryMarshal.GetReference(dst.Primitives.AsSpan());
                Unsafe.CopyBlock(ref dstP, ref *(frameBase + primOffset), (uint)primSize);
            }
            if (refCount > 0)
            {
                var dstRefs = dst.ManagedObjects;
                int srcBase = frameRefBase + refOffset;
                for (int i = 0; i < refCount; i++)
                    dstRefs[i] = mStack[srcBase + i];
            }
        }

        // Step 20 (neo-step20-async, D5): Hoist an in-frame IL value-type state
        // machine to a fresh heap ILTypeInstance WITHOUT a CrossBindingAdaptor
        // (initializeCLRInstance:false). The inverse of CopyFrameToIL's
        // heap->frame direction; a Box without the CLR instance. Pure primitive
        // shipped standalone + unit-probed; NOT wired into any redirect (the
        // suspend slice wires it into AwaitUnsafeOnCompleted).
        internal static unsafe ILTypeInstance HoistNeoILValueToHeap(ILType smType,
            byte* srcFrame, int srcPrimOff, int srcRefBase,
            AutoList mStack, int srcRefOff, int refCount)
        {
            ILTypeInstance heap = new ILTypeInstance(smType, false);
            int primSize = smType.TotalPrimitiveSize;
            if (primSize > 0 && heap.Primitives != null)
            {
                ref byte dstP = ref MemoryMarshal.GetReference(heap.Primitives.AsSpan());
                Unsafe.CopyBlock(ref dstP, ref *(srcFrame + srcPrimOff), (uint)primSize);
            }
            if (refCount > 0)
            {
                var dstRefs = heap.ManagedObjects;
                int srcBase = srcRefBase + srcRefOff;
                for (int i = 0; i < refCount; i++)
                    dstRefs[i] = mStack[srcBase + i];
            }
            return heap;
        }

        // Copies ILTypeInstance contents back to the frame byte region + frame mStack refs.
        // Used by Unbox / Unbox_Any (boxed instance → frame).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe void CopyILToFrame(ILTypeInstance src,
                                         byte* frameBase, int primOffset, int refOffset,
                                         int primSize, int refCount,
                                         AutoList mStack, int frameRefBase)
        {
            if (primSize > 0 && src.Primitives != null)
            {
                ref byte srcP = ref MemoryMarshal.GetReference(src.Primitives.AsSpan());
                Unsafe.CopyBlock(ref *(frameBase + primOffset), ref srcP, (uint)primSize);
            }
            if (refCount > 0)
            {
                var srcRefs = src.ManagedObjects;
                int dstBase = frameRefBase + refOffset;
                for (int i = 0; i < refCount; i++)
                    mStack[dstBase + i] = srcRefs[i];
            }
        }

        // Step 6 entry shim: convert raw bytes the Neo interpreter wrote into
        // retDst back into a CLR object the outer Run() pipeline can consume.
        // Step 8 will replace this whole code path with the proper Neo call
        // convention; for now it's only invoked from the top-level Invoke entry
        // for primitive return types.
        public static unsafe object NeoBoxReturnValue(IType returnType, byte* retDst, int retSize)
        {
            var clr = returnType.TypeForCLR;
            if (clr == typeof(int))
                return *(int*)retDst;
            if (clr == typeof(uint))
                return *(uint*)retDst;
            if (clr == typeof(long))
                return *(long*)retDst;
            if (clr == typeof(ulong))
                return *(ulong*)retDst;
            if (clr == typeof(short))
                return *(short*)retDst;
            if (clr == typeof(ushort))
                return *(ushort*)retDst;
            if (clr == typeof(byte))
                return *retDst;
            if (clr == typeof(sbyte))
                return *(sbyte*)retDst;
            if (clr == typeof(bool))
                return *retDst != 0;
            if (clr == typeof(char))
                return *(char*)retDst;
            if (clr == typeof(float))
                return *(float*)retDst;
            if (clr == typeof(double))
                return *(double*)retDst;
            // Fallback: raw bytes as int
            return retSize >= 4 ? (object)*(int*)retDst : (object)(int)*retDst;
        }

        // Step 13: box a sized primitive value of the given CLR Type from a raw
        // frame byte pointer into a boxed object (the inverse of NeoBoxReturnValue
        // without the IType lookup -- callers already hold the System.Type).
        // NOTE: currently has no call sites after the Box-enum arm was removed
        // (MINOR-1). Retained intentionally -- it may be revived by Step 15
        // (enum cross-cast). Do NOT remove.
        static unsafe object NeoBoxPrimitiveByType(Type clr, byte* src)
        {
            if (clr == typeof(int))
                return *(int*)src;
            if (clr == typeof(uint))
                return *(uint*)src;
            if (clr == typeof(long))
                return *(long*)src;
            if (clr == typeof(ulong))
                return *(ulong*)src;
            if (clr == typeof(short))
                return *(short*)src;
            if (clr == typeof(ushort))
                return *(ushort*)src;
            if (clr == typeof(byte))
                return *src;
            if (clr == typeof(sbyte))
                return *(sbyte*)src;
            if (clr == typeof(bool))
                return *src != 0;
            if (clr == typeof(char))
                return *(char*)src;
            if (clr == typeof(float))
                return *(float*)src;
            if (clr == typeof(double))
                return *(double*)src;
            if (clr == typeof(IntPtr))
                return *(IntPtr*)src;
            if (clr == typeof(UIntPtr))
                return *(UIntPtr*)src;
            // Fallback: raw bytes as int.
            return (object)*(int*)src;
        }

        // Step 13: the inverse of NeoBoxPrimitiveByType -- write a boxed CLR
        // primitive back into a raw frame byte pointer by the value's CLR type.
        static unsafe void NeoWritePrimitiveToFrame(object obj, byte* dst)
        {
            Type clr = obj.GetType();
            if (clr == typeof(int))
                *(int*)dst = (int)obj;
            else if (clr == typeof(uint))
                *(uint*)dst = (uint)obj;
            else if (clr == typeof(long))
                *(long*)dst = (long)obj;
            else if (clr == typeof(ulong))
                *(ulong*)dst = (ulong)obj;
            else if (clr == typeof(short))
                *(short*)dst = (short)obj;
            else if (clr == typeof(ushort))
                *(ushort*)dst = (ushort)obj;
            else if (clr == typeof(byte))
                *dst = (byte)obj;
            else if (clr == typeof(sbyte))
                *(sbyte*)dst = (sbyte)obj;
            else if (clr == typeof(bool))
                *dst = (bool)obj ? (byte)1 : (byte)0;
            else if (clr == typeof(char))
                *(char*)dst = (char)obj;
            else if (clr == typeof(float))
                *(float*)dst = (float)obj;
            else if (clr == typeof(double))
                *(double*)dst = (double)obj;
            else if (clr == typeof(IntPtr))
                *(IntPtr*)dst = (IntPtr)obj;
            else if (clr == typeof(UIntPtr))
                *(UIntPtr*)dst = (UIntPtr)obj;
            else
                throw new NotImplementedException("Neo: unsupported CLR primitive for Unbox: " + clr.FullName);
        }
    }
}
#endif
