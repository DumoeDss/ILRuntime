using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ILRuntime.CLR.Utils;
using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;
using ILRuntime.Runtime.Stack;
using ILRuntime.Runtime.Enviorment;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;


#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
using AutoList = System.Collections.Generic.List<object>;
#else
using AutoList = ILRuntime.Other.UncheckedList<object>;
#endif
namespace ILRuntime.Runtime.Intepreter
{
    public class ILTypeStaticInstance : ILTypeInstance
    {
        public unsafe ILTypeStaticInstance(ILType type)
        {
            this.type = type;
#if !ENABLE_NEO_MODE
            fields = new StackObject[type.StaticFieldTypes.Length];
            managedObjs = new AutoList(fields.Length);
            for (int i = 0; i < fields.Length; i++)
            {
                var ft = type.StaticFieldTypes[i];
                managedObjs.Add(null);
                StackObject.Initialized(ref fields[i], i, ft, managedObjs);
            }
            int idx = 0;
            foreach (var i in type.TypeDefinition.Fields)
            {
                if (i.IsStatic)
                {
                    if (i.InitialValue != null && i.InitialValue.Length > 0)
                    {
                        fields[idx].ObjectType = ObjectTypes.Object;
                        fields[idx].Value = idx;
                        managedObjs[idx] = i.InitialValue;
                    }
                    idx++;
                }
            }
#else
            int pSize = type.StaticTotalPrimitiveSize;
            int mCnt = type.StaticTotalReferenceCount;
            if (pSize > 0)
                fields = new byte[pSize];
            if (mCnt > 0)
            {
                managedObjs = new AutoList(mCnt);
                for (int i = 0; i < mCnt; i++)
                    managedObjs.Add(null);
            }
            int idxStatic = 0;
            // Step 25 S3-4: a Cecil-free ILType (isNeoAotType) has NO Cecil
            // TypeDefinition (the Neo guard throws; the Cecil InitialValue byte
            // blobs are a Cecil-emit detail NOT carried in the .neo). The .cctor
            // is the initializer -- a field with a non-constant initializer is the
            // .cctor's job, already seeded at Cecil-free load. SKIP the Cecil
            // InitialValue replay loop on a Cecil-free type (the byte[] / AutoList
            // sizing above is driven by the static totals, which the factory sets
            // from the record; GetStaticFieldOffset reads the installed
            // staticFieldOffsets). A Cecil-loaded Neo type runs the loop unchanged.
            if (!type.isNeoAotType)
            {
                foreach (var f in type.TypeDefinition.Fields)
                {
                    if (f.IsStatic)
                    {
                        if (f.InitialValue != null && f.InitialValue.Length > 0)
                        {
                            var offset = type.GetStaticFieldOffset(idxStatic);
                            if (managedObjs != null)
                                managedObjs[offset.ReferenceOffset] = f.InitialValue;
                        }
                        idxStatic++;
                    }
                }
            }
#endif
        }
    }

    unsafe class ILEnumTypeInstance : ILTypeInstance
    {
        public ILEnumTypeInstance(ILType type)
        {
            if (!type.IsEnum)
                throw new NotSupportedException();
            this.type = type;
#if !ENABLE_NEO_MODE
            fields = new StackObject[1];
#else
            var ut = type.FieldTypes[0];
            int size = type.AppDomain.GetPrimitiveSize(ut);
            fields = new byte[size];
#endif
        }
#if !ENABLE_NEO_MODE

        public override ILTypeInstance Clone()
        {
            ILEnumTypeInstance ins = new ILEnumTypeInstance(type);
            ins.fields[0] = fields[0];
            return ins;
        }

        public override string ToString()
        {
            var fields = type.TypeDefinition.Fields;
            long longVal = 0;
            int intVal = 0;
            bool isLong = this.fields[0].ObjectType == ObjectTypes.Long;
            if (isLong)
            {
                fixed (StackObject* f = this.fields)
                    longVal = *(long*)&f->Value;
            }
            else
                intVal = this.fields[0].Value;
            for (int i = 0; i < fields.Count; i++)
            {
                var f = fields[i];
                if (f.IsStatic)
                {
                    if (isLong)
                    {
                        long val = f.Constant is long ? (long)f.Constant : (long)(ulong)f.Constant;
                        if (val == longVal)
                            return f.Name;
                    }
                    else
                    {
                        if (f.Constant is int)
                        {
                            if ((int)f.Constant == intVal)
                                return f.Name;
                        }
                        else if (f.Constant is short)
                        {
                            if ((short)f.Constant == intVal)
                                return f.Name;
                        }
                        else if(f.Constant is long)
                        {
                            if ((long)f.Constant == longVal)
                                return f.Name;
                        }
                        else if (f.Constant is byte)
                        {
                            if ((byte)f.Constant == intVal)
                                return f.Name;
                        }
                        else if (f.Constant is uint)
                        {
                            int val = (int) (uint) f.Constant;
                            if (val == intVal)
                                return f.Name;
                        }
                        else if (f.Constant is ushort)
                        {
                            if ((ushort)f.Constant == intVal)
                                return f.Name;
                        }
                        else if (f.Constant is sbyte)
                        {
                            if ((sbyte)f.Constant == intVal)
                                return f.Name;
                        }
                        else
                            throw new NotImplementedException();
                    }
                }
            }
            return isLong ? longVal.ToString() : intVal.ToString();
        }
#else
        public override ILTypeInstance Clone()
        {
            var ins = new ILEnumTypeInstance(type);
            Buffer.BlockCopy(fields, 0, ins.fields, 0, fields.Length);
            return ins;
        }

        public override string ToString()
        {
            int size = fields.Length;
            long longVal = 0;
            int intVal = 0;
            bool isLong = size == 8;
            var span = new ReadOnlySpan<byte>(fields);
            switch (size)
            {
                case 1:
                    intVal = fields[0];
                    break;
                case 2:
                    intVal = MemoryMarshal.Read<short>(span);
                    break;
                case 4:
                    intVal = MemoryMarshal.Read<int>(span);
                    break;
                case 8:
                    longVal = MemoryMarshal.Read<long>(span);
                    break;
                default:
                    throw new NotImplementedException();
            }
            var defFields = type.TypeDefinition.Fields;
            for (int i = 0; i < defFields.Count; i++)
            {
                var f = defFields[i];
                if (f.IsStatic)
                {
                    if (isLong)
                    {
                        long val = f.Constant is long ? (long)f.Constant : (long)(ulong)f.Constant;
                        if (val == longVal)
                            return f.Name;
                    }
                    else
                    {
                        if (f.Constant is int)
                        {
                            if ((int)f.Constant == intVal)
                                return f.Name;
                        }
                        else if (f.Constant is short)
                        {
                            if ((short)f.Constant == intVal)
                                return f.Name;
                        }
                        else if (f.Constant is long)
                        {
                            if ((long)f.Constant == longVal)
                                return f.Name;
                        }
                        else if (f.Constant is byte)
                        {
                            if ((byte)f.Constant == intVal)
                                return f.Name;
                        }
                        else if (f.Constant is uint)
                        {
                            int val = (int)(uint)f.Constant;
                            if (val == intVal)
                                return f.Name;
                        }
                        else if (f.Constant is ushort)
                        {
                            if ((ushort)f.Constant == intVal)
                                return f.Name;
                        }
                        else if (f.Constant is sbyte)
                        {
                            if ((sbyte)f.Constant == intVal)
                                return f.Name;
                        }
                        else
                            throw new NotImplementedException();
                    }
                }
            }
            return isLong ? longVal.ToString() : intVal.ToString();
        }
#endif
#if ENABLE_NEO_MODE
        // C3 (neo-enum-cluster-residual): a boxed IL enum (ILEnumTypeInstance) must
        // compare/hash by VALUE. The Legacy ILTypeInstance.Equals/GetHashCode enum
        // branch is #if !ENABLE_NEO_MODE, so under Neo it compiled out and
        // base.Equals fell back to REFERENCE equality -- two separately-boxed enum
        // values were never equal (EnumTest Test30/32/33, e.g.
        // boxedEnum.Equals(Feature3)). Overriding on the derived class covers every
        // dispatch path (Object.Equals redirect Equals_3_Neo, static Object.Equals
        // Equals_4_Neo, reflection invoke) because the boxed receiver IS an
        // ILEnumTypeInstance at the CLR level (Box arm creates `new
        // ILEnumTypeInstance`). fields here is the Neo byte[] holding the underlying
        // value (Primitives returns the same array).
        public override bool Equals(object obj)
        {
            if (obj is ILEnumTypeInstance other)
            {
                if (this.type != other.type)
                    return false;
                byte[] a = this.fields;
                byte[] b = other.fields;
                if (a == b)
                    return true;
                if (a == null || b == null || a.Length != b.Length)
                    return false;
                for (int i = 0; i < a.Length; i++)
                    if (a[i] != b[i])
                        return false;
                return true;
            }
            return base.Equals(obj);
        }

        public override int GetHashCode()
        {
            // Mirror Legacy: hash the underlying value (fields[0].Value.GetHashCode()),
            // not the instance identity -- keeps Equals/GetHashCode consistent for
            // enum-valued dict keys.
            if (fields == null || fields.Length == 0)
                return 0;
            long v = 0;
            int n = fields.Length < 8 ? fields.Length : 8;
            for (int i = 0; i < n; i++)
                v |= (long)fields[i] << (i * 8);
            return v.GetHashCode();
        }
#endif
    }

    public class ILTypeInstance
    {
        protected ILType type;
#if ENABLE_NEO_MODE
        protected byte[] fields;
#else
        protected StackObject[] fields;
#endif
        protected AutoList managedObjs;
        object clrInstance;
        ulong valueTypeMask;
        Dictionary<ILMethod, IDelegateAdapter> delegates;

        public ILType Type
        {
            get
            {
                return type;
            }
        }

#if ENABLE_NEO_MODE
        public StackObject[] Fields
        {
            get { return null; }
        }

        public byte[] Primitives
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return fields;
            }
        }
#else
        public StackObject[] Fields
        {
            get { return fields; }
        }
#endif

        public virtual bool IsValueType
        {
            get
            {
                return type.IsValueType && !Boxed;
            }
        }

        /// <summary>
        /// 是否已装箱
        /// </summary>
        public bool Boxed { get; set; }

        public AutoList ManagedObjects { get { return managedObjs; } }

        public object CLRInstance { get { return clrInstance; } set { clrInstance = value; } }

        protected ILTypeInstance()
        {

        }
        public ILTypeInstance(ILType type, bool initializeCLRInstance = true)
        {
            this.type = type;
#if ENABLE_NEO_MODE
            int pSize = type.TotalPrimitiveSize;
            int mCnt = type.TotalReferenceCount;
            if (pSize > 0)
                fields = new byte[pSize];
            if (mCnt > 0)
            {
                managedObjs = new AutoList(mCnt);
                for (int i = 0; i < mCnt; i++)
                    managedObjs.Add(null);
            }
#else
            fields = new StackObject[type.TotalFieldCount];
            var cnt = fields.Length;
            managedObjs = new AutoList(cnt);
            for (int i = 0; i < cnt; i++)
            {
                managedObjs.Add(null);
            }
            InitializeFields(type);
#endif

            if (initializeCLRInstance)
            {
                if (type.FirstCLRBaseType is Enviorment.CrossBindingAdaptor)
                {
                    clrInstance = ((Enviorment.CrossBindingAdaptor)type.FirstCLRBaseType).CreateCLRInstance(type.AppDomain, this);
                }
                else
                {
                    clrInstance = this;
                }
                if(type.FirstCLRInterface is Enviorment.CrossBindingAdaptor)
                {
                    if (clrInstance != this)//Only one CLRInstance is allowed atm, so implementing multiple interfaces is not supported
                    {
                        throw new NotSupportedException("Inheriting and implementing interface at the same time is not supported yet");
                    }
                    clrInstance = ((Enviorment.CrossBindingAdaptor)type.FirstCLRInterface).CreateCLRInstance(type.AppDomain, this);
                }
            }
            else
                clrInstance = this;
        }

        public unsafe object this[int index]
        {
            get
            {
#if !ENABLE_NEO_MODE
                if (index < fields.Length && index >= 0)
                {
                    fixed (StackObject* ptr = fields)
                    {
                        StackObject* esp = &ptr[index];
                        return StackObject.ToObject(esp, null, managedObjs);
                    }
                }
                else
                {
                    if (Type.FirstCLRBaseType != null && Type.FirstCLRBaseType is Enviorment.CrossBindingAdaptor)
                    {
                        CLRType clrType = type.AppDomain.GetType(((Enviorment.CrossBindingAdaptor)Type.FirstCLRBaseType).BaseCLRType) as CLRType;
                        return clrType.GetFieldValue(index, clrInstance);
                    }
                    else
                        throw new TypeLoadException();
                }
#else
                // F-4 / NEO-IL-EX-FIELDACCESS path #4 (Neo): read an IL-declared
                // field off a Neo ILTypeInstance through the cross-binding-adaptor
                // forward path / host reflection. Under the Neo object model the
                // instance stores fields as byte[] Primitives (fields, sized to
                // type.TotalPrimitiveSize) + AutoList ManagedObjects (managedObjs,
                // sized to type.TotalReferenceCount), NOT a StackObject[] -- so the
                // Legacy byte-length gate (fields.Length == TotalFieldCount) does
                // NOT apply. Gate IL-field vs CLR-inherited by the field INDEX
                // (the exact analogue of the Legacy gate; out-of-range -> the
                // FirstCLRBaseType CLR-inherited branch, byte-identical to Legacy).
                if (index < type.TotalFieldCount && index >= 0)
                {
                    ILTypeFieldOffset off = type.GetFieldOffset(index);
                    IType ft = type.GetField(index, out ILRuntime.Mono.Cecil.FieldReference _);
                    if (ft.IsPrimitive)
                    {
                        return ReadNeoPrimitive(fields, off.PrimitiveOffset, ft, type.AppDomain);
                    }
                    else if (ft.IsValueType && ft is ILType)
                    {
                        // IL value-type field: spans both the primitive and the
                        // reference sub-regions (ILType.cs:2334-2348 allocates it
                        // across both). Reconstruction off the split storage is not
                        // supported here -- throw a TAGGED NIE so a caller sees the
                        // real reason instead of wrong data. Rare for the caught-
                        // exception shape (primitive/string fields dominate).
                        throw new NotImplementedException("Neo ILTypeInstance indexer: IL-value-type field reconstruction not supported (field " + index + " of " + type.FullName + ")");
                    }
                    else
                    {
                        // Reference / enum (boxed) / CLR-struct (F-10 boxed) field.
                        return managedObjs != null ? managedObjs[off.ReferenceOffset] : null;
                    }
                }
                else
                {
                    if (Type.FirstCLRBaseType != null && Type.FirstCLRBaseType is Enviorment.CrossBindingAdaptor)
                    {
                        CLRType clrType = type.AppDomain.GetType(((Enviorment.CrossBindingAdaptor)Type.FirstCLRBaseType).BaseCLRType) as CLRType;
                        return clrType.GetFieldValue(index, clrInstance);
                    }
                    else
                        throw new TypeLoadException();
                }
#endif
            }
            set
            {
#if !ENABLE_NEO_MODE
                value = ILIntepreter.CheckAndCloneValueType(value, type.AppDomain);
                if (index < fields.Length && index >= 0)
                {
                    fixed (StackObject* ptr = fields)
                    {
                        StackObject* esp = &ptr[index];
                        if (value != null)
                        {
                            var vt = value.GetType();
                            if (vt.IsPrimitive)
                            {
                                ILIntepreter.UnboxObject(esp, value, managedObjs, type.AppDomain);
                            }
                            else if (vt.IsEnum)
                            {
                                esp->ObjectType = ObjectTypes.Integer;
                                esp->Value = value.ToInt32();
                                esp->ValueLow = 0;
                            }
                            else
                            {
                                
                                esp->ObjectType = ObjectTypes.Object;
                                esp->Value = index;
                                managedObjs[index] = value;
                            }
                        }
                        else
                            *esp = StackObject.Null;
                    }
                }
                else
                {
                    if (Type.FirstCLRBaseType != null && Type.FirstCLRBaseType is Enviorment.CrossBindingAdaptor)
                    {
                        CLRType clrType = type.AppDomain.GetType(((Enviorment.CrossBindingAdaptor)Type.FirstCLRBaseType).BaseCLRType) as CLRType;
                        clrType.SetFieldValue(index, ref clrInstance, value);
                    }
                    else
                        throw new TypeLoadException();
                }
#else
                // F-4 path #4 (Neo set arm, mirrors the get arm). Write the
                // value into the Neo split storage. The Legacy clone prelude is
                // engine-agnostic and retained.
                value = ILIntepreter.CheckAndCloneValueType(value, type.AppDomain);
                if (index < type.TotalFieldCount && index >= 0)
                {
                    ILTypeFieldOffset off = type.GetFieldOffset(index);
                    IType ft = type.GetField(index, out ILRuntime.Mono.Cecil.FieldReference _);
                    if (ft.IsPrimitive)
                    {
                        if (value != null)
                            WriteNeoPrimitive(fields, off.PrimitiveOffset, ft, value, type.AppDomain);
                        else
                            WriteNeoPrimitiveDefault(fields, off.PrimitiveOffset, ft, type.AppDomain);
                    }
                    else if (ft.IsValueType && ft is ILType)
                    {
                        throw new NotImplementedException("Neo ILTypeInstance indexer: IL-value-type field write not supported (field " + index + " of " + type.FullName + ")");
                    }
                    else
                    {
                        // Reference / enum (boxed) / CLR-struct (F-10 boxed).
                        if (managedObjs != null)
                            managedObjs[off.ReferenceOffset] = value;
                    }
                }
                else
                {
                    if (Type.FirstCLRBaseType != null && Type.FirstCLRBaseType is Enviorment.CrossBindingAdaptor)
                    {
                        CLRType clrType = type.AppDomain.GetType(((Enviorment.CrossBindingAdaptor)Type.FirstCLRBaseType).BaseCLRType) as CLRType;
                        clrType.SetFieldValue(index, ref clrInstance, value);
                    }
                    else
                        throw new TypeLoadException();
                }
#endif
            }
        }
#if ENABLE_NEO_MODE
        // F-4 path #4 helpers: read/write a Neo primitive field out of / into the
        // byte[] Primitives region, keyed by the AppDomain primitive singletons
        // (the SAME reference-identity comparison the storage allocator at
        // ILType.cs:2300 uses, so the width/encoding is guaranteed consistent).
        // Mirrors the Ldfld_*/Stfld_* arms at ILIntepreter.Neo.cs:2753-2860.
        private static object ReadNeoPrimitive(byte[] prim, int offset, IType fieldType, Enviorment.AppDomain domain)
        {
            if (fieldType == domain.IntType)
                return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<int>(ref prim[offset]);
            if (fieldType == domain.LongType)
                return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<long>(ref prim[offset]);
            if (fieldType == domain.ShortType)
                return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<short>(ref prim[offset]);
            if (fieldType == domain.ByteType)
                return prim[offset];
            if (fieldType == domain.SByteType)
                return (sbyte)prim[offset];
            if (fieldType == domain.UShortType)
                return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ushort>(ref prim[offset]);
            if (fieldType == domain.UIntType)
                return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<uint>(ref prim[offset]);
            if (fieldType == domain.ULongType)
                return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ulong>(ref prim[offset]);
            if (fieldType == domain.FloatType)
                return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<float>(ref prim[offset]);
            if (fieldType == domain.DoubleType)
                return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<double>(ref prim[offset]);
            if (fieldType == domain.BoolType)
                return prim[offset] != 0;
            if (fieldType == domain.CharType)
                return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<char>(ref prim[offset]);
            if (fieldType == domain.IntPtrType)
                return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<System.IntPtr>(ref prim[offset]);
            // Unknown primitive IType (should not happen: the allocator only
            // routes the singletons above into the Primitives region). Surface it
            // rather than return wrong data.
            throw new NotImplementedException("Neo ILTypeInstance indexer: unsupported primitive field type " + fieldType.FullName);
        }

        private static void WriteNeoPrimitive(byte[] prim, int offset, IType fieldType, object value, Enviorment.AppDomain domain)
        {
            if (fieldType == domain.IntType)
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref prim[offset], (int)value);
            else if (fieldType == domain.LongType)
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref prim[offset], (long)value);
            else if (fieldType == domain.ShortType)
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref prim[offset], (short)value);
            else if (fieldType == domain.ByteType)
                prim[offset] = (byte)value;
            else if (fieldType == domain.SByteType)
                prim[offset] = (byte)(sbyte)value;
            else if (fieldType == domain.UShortType)
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref prim[offset], (ushort)value);
            else if (fieldType == domain.UIntType)
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref prim[offset], (uint)value);
            else if (fieldType == domain.ULongType)
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref prim[offset], (ulong)value);
            else if (fieldType == domain.FloatType)
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref prim[offset], (float)value);
            else if (fieldType == domain.DoubleType)
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref prim[offset], (double)value);
            else if (fieldType == domain.BoolType)
                prim[offset] = (bool)value ? (byte)1 : (byte)0;
            else if (fieldType == domain.CharType)
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref prim[offset], (char)value);
            else if (fieldType == domain.IntPtrType)
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref prim[offset], (System.IntPtr)value);
            else
                throw new NotImplementedException("Neo ILTypeInstance indexer: unsupported primitive field type " + fieldType.FullName);
        }

        private static void WriteNeoPrimitiveDefault(byte[] prim, int offset, IType fieldType, Enviorment.AppDomain domain)
        {
            // null primitive write -> zero the width (matching the Ldfld default
            // for a never-assigned field). The width comes from the allocator's
            // GetPrimitiveSize.
            int sz = domain.GetPrimitiveSize(fieldType);
            for (int i = 0; i < sz; i++)
                prim[offset + i] = 0;
        }
#endif
#if !ENABLE_NEO_MODE
        public unsafe void AssignFieldNoClone(int index, object value)
        {
            if (index < fields.Length && index >= 0)
            {
                fixed (StackObject* ptr = fields)
                {
                    StackObject* esp = &ptr[index];
                    if (value != null)
                    {
                        var vt = value.GetType();
                        if (vt.IsPrimitive)
                        {
                            ILIntepreter.UnboxObject(esp, value, managedObjs, type.AppDomain);
                        }
                        else if (vt.IsEnum)
                        {
                            esp->ObjectType = ObjectTypes.Integer;
                            esp->Value = value.ToInt32();
                            esp->ValueLow = 0;
                        }
                        else
                        {

                            esp->ObjectType = ObjectTypes.Object;
                            esp->Value = index;
                            managedObjs[index] = value;
                        }
                    }
                    else
                        *esp = StackObject.Null;
                }
            }
            else
            {
                if (Type.FirstCLRBaseType != null && Type.FirstCLRBaseType is Enviorment.CrossBindingAdaptor)
                {
                    CLRType clrType = type.AppDomain.GetType(((Enviorment.CrossBindingAdaptor)Type.FirstCLRBaseType).BaseCLRType) as CLRType;
                    clrType.SetFieldValue(index, ref clrInstance, value);
                }
                else
                    throw new TypeLoadException();
            }
        }
#endif

        const int SizeOfILTypeInstance = 21;
        public unsafe int GetSizeInMemory(HashSet<object> traversedObj)
        {
            if (traversedObj.Contains(this))
                return 0;
            traversedObj.Add(this);
            if (type == null)
                return SizeOfILTypeInstance;
#if ENABLE_NEO_MODE
            var size = SizeOfILTypeInstance + (fields != null ? fields.Length : 0);
#else
            var size = SizeOfILTypeInstance + sizeof(StackObject) * fields.Length;
#endif
            if (managedObjs != null)
            {
                size += managedObjs.Count * 4;
                foreach (var i in managedObjs)
                {
                    size += GetSizeInMemory(i, traversedObj);
                }
            }
            return size;
        }

        static int GetSizeInMemory(object obj, HashSet<object> traversedObj)
        {
            if (obj == null)
                return 0;
            if (obj is ILTypeInstance)
                return ((ILTypeInstance)obj).GetSizeInMemory(traversedObj);
            if (traversedObj.Contains(obj))
                return 0;
            traversedObj.Add(obj);
            if (obj is string)
            {
                return Encoding.Unicode.GetByteCount((string)obj);
            }
            Type t = obj.GetType();
            if (t.IsArray)
            {
                Array arr = (Array)obj;
                var et = t.GetElementType();
                int elementSize = 0;
                if (et.IsPrimitive)
                {
                    elementSize = System.Runtime.InteropServices.Marshal.SizeOf(et);
                }
                else
                    elementSize = 4;
                return arr.Length * elementSize;
            }
            else
            {
                if (t.IsPrimitive)
                {
                    return System.Runtime.InteropServices.Marshal.SizeOf(t);
                }
                else
                {
                    int size = 0;
                    System.Collections.ICollection collection = obj as System.Collections.ICollection;
                    if (collection != null)
                    {
                        var enu = collection.GetEnumerator();
                        while (enu.MoveNext())
                        {
                            size += GetSizeInMemory(enu.Current, traversedObj);
                        }
                    }
                    else
                    {
                        System.Collections.IDictionary dictionary = obj as System.Collections.IDictionary;
                        if (dictionary != null)
                        {
                            var enu = dictionary.GetEnumerator();
                            while (enu.MoveNext())
                            {
                                size += GetSizeInMemory(enu.Key, traversedObj);
                                size += GetSizeInMemory(enu.Value, traversedObj);
                            }
                        }
                    }
                    return size;
                }
            }
        }

#if !ENABLE_NEO_MODE
        void InitializeFields(ILType type)
        {
            for (int i = 0; i < type.FieldTypes.Length; i++)
            {
                var idx = type.FieldStartIndex + i;
                var ft = type.FieldTypes[i];
                if (ft.IsValueType && idx < 64)
                {
                    valueTypeMask |= (ulong)1 << idx;
                }
                StackObject.Initialized(ref fields[idx], idx, ft, managedObjs);
            }
            if (type.BaseType != null && type.BaseType is ILType)
                InitializeFields((ILType)type.BaseType);
        }
#endif

        internal unsafe void PushFieldAddress(int fieldIdx, StackObject* esp, AutoList managedStack)
        {
            esp->ObjectType = ObjectTypes.FieldReference;
            esp->Value = managedStack.Count;
            managedStack.Add(this);
            esp->ValueLow = fieldIdx;
        }

#if !ENABLE_NEO_MODE
        internal unsafe void PushToStack(int fieldIdx, StackObject* esp, ILIntepreter intp, AutoList managedStack)
        {
            if (fieldIdx < fields.Length && fieldIdx >= 0)
            {
                PushToStackSub(ref fields[fieldIdx], fieldIdx, esp, managedStack, intp);
            }
            else
            {
                if (Type.FirstCLRBaseType != null && Type.FirstCLRBaseType is Enviorment.CrossBindingAdaptor)
                {
                    CLRType clrType = intp.AppDomain.GetType(((Enviorment.CrossBindingAdaptor)Type.FirstCLRBaseType).BaseCLRType) as CLRType;
                    if (!clrType.CopyFieldToStack(fieldIdx, clrInstance, intp, ref esp, managedStack))
                    {
                        var obj = clrType.GetFieldValue(fieldIdx, clrInstance);
                        if (obj is CrossBindingAdaptorType)
                            obj = ((CrossBindingAdaptorType)obj).ILInstance;
                        ILIntepreter.PushObject(esp, managedStack, obj);
                    }
                }
                else
                    throw new TypeLoadException();
            }
        }

        internal unsafe void CopyToRegister(int fieldIdx,ref RegisterFrameInfo info, short reg)
        {
            if (fieldIdx < fields.Length && fieldIdx >= 0)
            {
                fixed(StackObject* ptr = fields)
                {
                    info.Intepreter.CopyToRegister(ref info, reg, &ptr[fieldIdx], managedObjs);
                }
            }
            else
            {
                if (Type.FirstCLRBaseType != null && Type.FirstCLRBaseType is Enviorment.CrossBindingAdaptor)
                {
                    CLRType clrType = info.Intepreter.AppDomain.GetType(((Enviorment.CrossBindingAdaptor)Type.FirstCLRBaseType).BaseCLRType) as CLRType;
                    var obj = clrType.GetFieldValue(fieldIdx, clrInstance);
                    if (obj is CrossBindingAdaptorType)
                        obj = ((CrossBindingAdaptorType)obj).ILInstance;
                    ILIntepreter.AssignToRegister(ref info, reg, obj);
                }
                else
                    throw new TypeLoadException();
            }
        }
#else
        internal unsafe void PushToStack(int fieldIdx, StackObject* esp, ILIntepreter intp, AutoList managedStack)
        {
        }

        internal unsafe void CopyToRegister(int fieldIdx,ref RegisterFrameInfo info, short reg)
        {
        }
#endif

        bool NeedCheckFieldValueType(int fieldIdx)
        {
            return fieldIdx >= 64 || ((valueTypeMask & ((ulong)1 << fieldIdx)) != 0);
        }

#if !ENABLE_NEO_MODE
        unsafe void PushToStackSub(ref StackObject field, int fieldIdx, StackObject* esp, AutoList managedStack, ILIntepreter intp)
        {
            if (field.ObjectType >= ObjectTypes.Object)
            {
                var obj = managedObjs[fieldIdx];
                if (obj != null && NeedCheckFieldValueType(fieldIdx))
                {
                    if (obj is ILTypeInstance)
                    {
                        ILTypeInstance ili = (ILTypeInstance)obj;
                        if (ili.type != null && ili.type.IsValueType)
                        {
                            intp.AllocValueType(esp, ili.type);
                            var dst = ILIntepreter.ResolveReference(esp);
                            ili.CopyValueTypeToStack(dst, managedStack);
                            return;
                        }
                    }
                    else
                    {
                        var ot = obj.GetType();
                        ValueTypeBinder binder;
                        if (ot.IsValueType && type.AppDomain.ValueTypeBinders.TryGetValue(ot, out binder))
                        {
                            intp.AllocValueType(esp, binder.CLRType);
                            var dst = ILIntepreter.ResolveReference(esp);
                            binder.CopyValueTypeToStack(obj, dst, managedStack);
                            return;
                        }
                    }
                }
                *esp = field;
                esp->Value = managedStack.Count;
                managedStack.Add(managedObjs[fieldIdx]);
            }
            else
                *esp = field;
        }

        internal unsafe void CopyValueTypeToStack(StackObject* ptr, AutoList mStack)
        {
            ptr->ObjectType = ObjectTypes.ValueTypeDescriptor;
            ptr->Value = type.TypeIndex;
            ptr->ValueLow = type.TotalFieldCount;
            for(int i = 0; i < fields.Length; i++)
            {
                var val = ptr - (i + 1);
                switch (val->ObjectType)
                {
                    case ObjectTypes.Object:
                    case ObjectTypes.FieldReference:
                    case ObjectTypes.ArrayReference:
                        mStack[val->Value] = ILIntepreter.CheckAndCloneValueType(managedObjs[i], type.AppDomain);
                        val->ValueLow = fields[i].ValueLow;
                        break;
                    case ObjectTypes.ValueTypeObjectReference:
                        {
                            var obj = managedObjs[i];
                            var dst = ILIntepreter.ResolveReference(val);
                            var vt = type.AppDomain.GetTypeByIndex(dst->Value);
                            if (vt is ILType)
                            {
                                ((ILTypeInstance)obj).CopyValueTypeToStack(dst, mStack);
                            }
                            else
                            {
                                ((CLRType)vt).ValueTypeBinder.CopyValueTypeToStack(obj, dst, mStack);
                            }
                        }
                        break;
                    default:
                        *val = fields[i];
                        break;
                }                
            }
        }
#else
        internal unsafe void CopyValueTypeToStack(StackObject* ptr, AutoList mStack)
        {
        }
#endif

#if !ENABLE_NEO_MODE
        internal void Clear()
        {   
            InitializeFields(type);
        }

        internal void InitializeField(int fieldIdx)
        {
            int curStart = type.FieldStartIndex;
            ILType curType = type;
            while(curType != null)
            {
                int maxIdx = curType.FieldStartIndex + curType.FieldTypes.Length;
                if (fieldIdx < maxIdx && fieldIdx >= curType.FieldStartIndex)
                {
                    var ft = curType.FieldTypes[fieldIdx - curType.FieldStartIndex];
                    StackObject.Initialized(ref fields[fieldIdx], fieldIdx, ft, managedObjs);
                    return;
                }
                else
                    curType = curType.BaseType as ILType;
            }
            throw new NotImplementedException();
        }

        internal unsafe void AssignFromStack(int fieldIdx, StackObject* esp, ILIntepreter intp, AutoList managedStack)
        {
            if (fieldIdx < fields.Length && fieldIdx >= 0)
                AssignFromStackSub(ref fields[fieldIdx], fieldIdx, esp, managedStack);
            else
            {
                var appdomain = intp != null ? intp.AppDomain : type.AppDomain;
                if (Type.FirstCLRBaseType != null && Type.FirstCLRBaseType is Enviorment.CrossBindingAdaptor)
                {
                    CLRType clrType = appdomain.GetType(((Enviorment.CrossBindingAdaptor)Type.FirstCLRBaseType).BaseCLRType) as CLRType;
                    if (intp != null && !clrType.AssignFieldFromStack(fieldIdx, ref clrInstance, intp, esp, managedStack))
                    {
                        var field = clrType.GetField(fieldIdx);
                        clrType.SetFieldValue(fieldIdx, ref clrInstance, field.FieldType.CheckCLRTypes(ILIntepreter.CheckAndCloneValueType(StackObject.ToObject(esp, appdomain, managedStack), appdomain)));
                    }
                }
                else
                    throw new TypeLoadException();
            }
        }

        internal unsafe void AssignFromStack(StackObject* esp, ILIntepreter intp, AutoList managedStack)
        {
            StackObject* val = ILIntepreter.ResolveReference(esp);
            int cnt = val->ValueLow;
            for (int i = 0; i < cnt; i++)
            {
                var addr = val - (i + 1);
                AssignFromStack(i, addr, intp, managedStack);
            }
        }

        unsafe void AssignFromStackSub(ref StackObject field, int fieldIdx, StackObject* esp, AutoList managedStack)
        {
            esp = ILIntepreter.GetObjectAndResolveReference(esp);
            field = *esp;
            switch (field.ObjectType)
            {
                case ObjectTypes.Object:
                case ObjectTypes.ArrayReference:
                case ObjectTypes.FieldReference:
                    field.ObjectType = ObjectTypes.Object;
                    field.Value = fieldIdx;
                    if (NeedCheckFieldValueType(fieldIdx))
                        managedObjs[fieldIdx] = ILIntepreter.CheckAndCloneValueType(managedStack[esp->Value], Type.AppDomain);
                    else
                        managedObjs[fieldIdx] = managedStack[esp->Value];
                    break;
                case ObjectTypes.ValueTypeObjectReference:
                    {
                        var domain = type.AppDomain;
                        field.ObjectType = ObjectTypes.Object;
                        field.Value = fieldIdx;
                        var dst = ILIntepreter.ResolveReference(esp);
                        var vt = domain.GetTypeByIndex(dst->Value);
                        if(vt is ILType)
                        {
                            var ins = managedObjs[fieldIdx];
                            if (ins == null)
                                throw new NullReferenceException();
                            ILTypeInstance child = (ILTypeInstance)ins;
                            child.AssignFromStack(esp, null, managedStack);
                        }
                        else
                        {
                            managedObjs[fieldIdx] = ((CLRType)vt).ValueTypeBinder.ToObject(dst, managedStack);
                        }
                        
                    }
                    break;
                default:
                    if (managedObjs != null)
                        managedObjs[fieldIdx] = null;
                    break;
            }
        }
#else
        internal void Clear()
        {
        }

        internal void InitializeField(int fieldIdx)
        {
        }

        internal unsafe void AssignFromStack(int fieldIdx, StackObject* esp, ILIntepreter intp, AutoList managedStack)
        {
        }

        internal unsafe void AssignFromStack(StackObject* esp, ILIntepreter intp, AutoList managedStack)
        {
        }
#endif

       
        public override string ToString()
        {
            var m = type.ToStringMethod;
            if (m != null)
            {
                if (m is ILMethod)
                {
                    var res = type.AppDomain.Invoke(m, this, null);
                    return res.ToString();
                }
                else
                    return clrInstance.ToString();
            }
            else
                return type.FullName;
        }

        public override bool Equals(object obj)
        {
#if !ENABLE_NEO_MODE
            if (type != null)
            {
                var m = type.EqualsMethod;
                if (m != null && m is ILMethod)
                {
                    using (var ctx = type.AppDomain.BeginInvoke(m))
                    {
                        ctx.PushObject(this);
                        ctx.PushObject(obj);
                        ctx.Invoke();
                        return ctx.ReadBool();
                    }
                }
                else
                {
                    if (this is ILEnumTypeInstance)
                    {
                        if (obj is ILEnumTypeInstance)
                        {
                            ILEnumTypeInstance enum1 = (ILEnumTypeInstance)this;
                            ILEnumTypeInstance enum2 = (ILEnumTypeInstance)obj;
                            if (enum1.type == enum2.type)
                            {
                                bool res;
                                if (enum1.fields[0].ObjectType == ObjectTypes.Integer)
                                    res = enum1.fields[0].Value == enum2.fields[0].Value;
                                else
                                    res = enum1.fields[0] == enum2.fields[0];
                                return res;
                            }
                            else
                                return false;
                        }
                        else
                            return base.Equals(obj);
                    }
                    else
                        return base.Equals(obj);
                }
            }
            else
#endif
                return base.Equals(obj);
        }
        public override int GetHashCode()
        {
#if !ENABLE_NEO_MODE
            if (type != null)
            {
                var m = type.GetHashCodeMethod;
                if (m != null && m is ILMethod)
                {
                    using (var ctx = type.AppDomain.BeginInvoke(m))
                    {
                        ctx.PushObject(this);
                        ctx.Invoke();
                        return ctx.ReadInteger();
                    }
                }
                else
                {
                    if (this is ILEnumTypeInstance)
                    {
                        return ((ILEnumTypeInstance)this).fields[0].Value.GetHashCode();
                    }
                    else
                        return base.GetHashCode();
                }
            }
            else
#endif
                return base.GetHashCode();
        }

        public virtual bool CanAssignTo(IType type)
        {
            return this.type.CanAssignTo(type);
        }

        public virtual ILTypeInstance Clone()
        {
            ILTypeInstance ins = new ILTypeInstance(type);
#if !ENABLE_NEO_MODE
            for (int i = 0; i < fields.Length; i++)
            {
                ins.fields[i] = fields[i];
                ins.managedObjs[i] = ILIntepreter.CheckAndCloneValueType(managedObjs[i], Type.AppDomain);
            }
#else
            if (fields != null)
                Array.Copy(fields, ins.fields, fields.Length);
            if (managedObjs != null)
            {
                for (int i = 0; i < managedObjs.Count; i++)
                    ins.managedObjs[i] = ILIntepreter.CheckAndCloneValueType(managedObjs[i], Type.AppDomain);
            }
#endif
            return ins;
        }

        internal IDelegateAdapter GetDelegateAdapter(ILMethod method)
        {
            if (delegates == null)
                return null;

            IDelegateAdapter res;
            if (delegates.TryGetValue(method, out res))
                return res;
            return null;
        }

        internal void SetDelegateAdapter(ILMethod method, IDelegateAdapter adapter)
        {
            if (delegates == null)
                delegates = new Dictionary<ILMethod, IDelegateAdapter>();

            if (!delegates.ContainsKey(method))
                delegates[method] = adapter;
            else
                throw new NotSupportedException();
        }
    }
}
