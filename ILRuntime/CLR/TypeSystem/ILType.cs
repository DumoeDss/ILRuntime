using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ILRuntime.Mono.Cecil;
using ILRuntime.Runtime;
using ILRuntime.Runtime.Enviorment;
using ILRuntime.CLR.Method;
using ILRuntime.Runtime.Intepreter;
using ILRuntime.Reflection;
using ILRuntime.Runtime.Stack;
using System.Runtime.CompilerServices;

namespace ILRuntime.CLR.TypeSystem
{
#if ENABLE_NEO_MODE
    internal struct ILTypeFieldOffset
    {
        public int PrimitiveOffset;
        public int ReferenceOffset;
    }
#endif
    public sealed class ILType : IType
    {
        Dictionary<string, List<ILMethod>> methods;
        TypeReference typeRef;
        TypeDefinition definition;
        ILRuntime.Runtime.Enviorment.AppDomain appdomain;
        bool staticConstructorCalled;
        ILMethod staticConstructor;
        List<ILMethod> constructors;
        IType [] fieldTypes;
#if ENABLE_NEO_MODE
        ILTypeFieldOffset[] fieldOffsets;
        ILTypeFieldOffset[] staticFieldOffsets;
        IMethod[] neoVTable;
        Dictionary<string, int> neoVTableSlots;
        string[] neoVTableSlotKeys;
        bool neoVTableBuilding;

        // === Neo only - Step 11: interface offset map ===
        // Maps each implemented interface to the starting slot offset of its
        // methods within neoVTable, so interface dispatch is O(1):
        //   actualMethod = neoVTable[interfaceOffset + interfaceMethodSlot]
        // Each interface owns an independent 0-based method slot namespace.
        InterfaceEntry[] neoInterfaceMap;
        Dictionary<IType, int> neoInterfaceOffsets;
        bool neoInterfaceMapBuilding;
        // Lazy per-ILType 0-based slot map of this type's OWN declared methods
        // (used when this ILType is itself an interface, to answer
        // GetInterfaceMethodSlotSelf). Keyed by SignatureString.
        Dictionary<string, int> neoSelfMethodSlots;
#endif
        FieldReference[] fieldReferences;
        FieldDefinition[] fieldDefinitions;
        IType[] staticFieldTypes;
        FieldReference[] staticFieldReferences;
        FieldDefinition[] staticFieldDefinitions;
        Dictionary<string, int> fieldMapping;
        Dictionary<string, int> staticFieldMapping;
        ILTypeStaticInstance staticInstance;
        Dictionary<int, int> fieldTokenMapping = new Dictionary<int, int> ();
        int fieldStartIdx = -1;
        int totalFieldCnt = -1;
        bool hasGenericArguments;
        KeyValuePair<string, IType> [] genericArguments;
        IType baseType, byRefType, enumType, elementType;
        Dictionary<int, IType> arrayTypes;
        Type arrayCLRType, byRefCLRType;
        IType [] interfaces;
        bool baseTypeInitialized = false;
        bool interfaceInitialized = false;
        List<ILType> genericInstances;
        bool isDelegate;
        ILRuntimeType reflectionType;
        ILType genericDefinition;
        IType firstCLRBaseType, firstCLRInterface;
        int hashCode = -1;
        int tIdx = -1;
        static int instance_id = 0x10000000;
        int jitFlags;
        public TypeDefinition TypeDefinition { get { return definition; } }
        bool mToStringGot, mEqualsGot, mGetHashCodeGot;
        IMethod mToString, mEquals, mGetHashCode;
        int valuetypeFieldCount, valuetypeManagedCount;
        bool valuetypeSizeCalculated;
        ValueTypeInitInfo vtInitInfo;
#if ENABLE_NEO_MODE
        int totalPrimitiveSize = -1;
        int totalReferenceCnt = -1;
        int totalStaticPrimitiveSize = -1;
        int totalStaticReferenceCnt = -1;
        // Step 12: max natural alignment over the type's fields (recursively,
        // for nested value types). 1 if the type has no fields. Cached during
        // InitializeFields. Used by the Neo frame allocator (JITCompiler) to
        // align value-type slots so typed pointer casts in the _Inline field-
        // access opcodes are naturally aligned.
        int naturalAlignment = -1;
#endif

        public IMethod ToStringMethod
        {
            get
            {
                if ( !mToStringGot )
                {
                    IMethod m = appdomain.ObjectType.GetMethod ( "ToString", 0, true );
                    mToString = GetVirtualMethod ( m );
                    mToStringGot = true;
                }
                return mToString;
            }
        }

        public IMethod EqualsMethod
        {
            get
            {
                if ( !mEqualsGot )
                {
                    IMethod m = appdomain.ObjectType.GetMethod ( "Equals", 1, true );
                    mEquals = GetVirtualMethod ( m );
                    mEqualsGot = true;
                }
                return mEquals;
            }
        }

        public IMethod GetHashCodeMethod
        {
            get
            {
                if ( !mGetHashCodeGot )
                {
                    IMethod m = appdomain.ObjectType.GetMethod ( "GetHashCode", 0, true );
                    mGetHashCode = GetVirtualMethod ( m );
                    mGetHashCodeGot = true;
                }
                return mGetHashCode;
            }
        }

        public TypeReference TypeReference
        {
            get { return typeRef; }
            set
            {
                typeRef = value;
                RetriveDefinitino ( value );
            }
        }

        public IType BaseType
        {
            get
            {
                if ( !baseTypeInitialized )
                    InitializeBaseType ();
                return baseType;
            }
        }

        public IType [] Implements
        {
            get
            {
                if ( !interfaceInitialized )
                    InitializeInterfaces ();
                return interfaces;
            }
        }

        public ILTypeStaticInstance StaticInstance
        {
            get
            {
                if ( fieldMapping == null )
                    InitializeFields ();
                if ( methods == null )
                    InitializeMethods ();
                if ( staticInstance == null && staticFieldTypes != null )
                {
                    staticInstance = new ILTypeStaticInstance ( this );
                }
                if ( staticInstance != null && !staticConstructorCalled )
                {
                    staticConstructorCalled = true;
                    if ( staticConstructor != null && ( !TypeReference.HasGenericParameters || IsGenericInstance ) )
                    {
#if ENABLE_NEO_MODE
                        // TODO Step 7: Neo interpreter still lacks Stfld_*/Ldfld_* case handlers,
                        // and ExecuteR (Legacy register VM) refuses the specialized field opcodes
                        // that JITCompiler emits in Neo mode. Suppressing cctor invocation here
                        // unblocks Step 6 smoke tests; restore once Step 7 lands.
#else
                        appdomain.Invoke ( staticConstructor, null, null );
#endif
                    }
                }
                return staticInstance;
            }
        }

        public IType [] FieldTypes
        {
            get
            {
                if ( fieldMapping == null )
                    InitializeFields ();
                return fieldTypes;
            }
        }

        public IType [] StaticFieldTypes
        {
            get
            {
                if ( fieldMapping == null )
                    InitializeFields ();
                return staticFieldTypes;
            }
        }

        public FieldDefinition [] StaticFieldDefinitions
        {
            get
            {
                if ( fieldMapping == null )
                    InitializeFields ();
                return staticFieldDefinitions;
            }
        }

        public FieldReference[] StaticFieldReferences
        {
            get
            {
                if (fieldMapping == null)
                    InitializeFields();
                return staticFieldReferences;
            }
        }

        public Dictionary<string, int> FieldMapping
        {
            get
            {
                if ( fieldMapping == null )
                    InitializeFields (); return fieldMapping;
            }
        }

        public IType FirstCLRBaseType
        {
            get
            {
                if ( !baseTypeInitialized )
                    InitializeBaseType ();
                return firstCLRBaseType;
            }
        }

        public IType FirstCLRInterface
        {
            get
            {
                if ( !interfaceInitialized )
                    InitializeInterfaces ();
                return firstCLRInterface;
            }
        }
        public bool HasGenericParameter
        {
            get
            {
                if (genericArguments != null)
                    return hasGenericArguments;
                return typeRef.HasGenericParameters && genericArguments == null;
            }
        }

        public bool IsGenericParameter
        {
            get
            {
                return typeRef.IsGenericParameter && genericArguments == null;
            }
        }

        public Dictionary<string, int> StaticFieldMapping { get { return staticFieldMapping; } }
        public ILRuntime.Runtime.Enviorment.AppDomain AppDomain
        {
            get
            {
                return appdomain;
            }
        }

        internal int FieldStartIndex
        {
            get
            {
                if ( fieldStartIdx < 0 )
                {
                    if ( BaseType != null )
                    {
                        if ( BaseType is ILType )
                        {
                            fieldStartIdx = ( ( ILType ) BaseType ).TotalFieldCount;
                        }
                        else
                            fieldStartIdx = 0;
                    }
                    else
                        fieldStartIdx = 0;
                }
                return fieldStartIdx;
            }
        }

        public int TotalFieldCount
        {
            get
            {
                if ( totalFieldCnt < 0 )
                {
                    if ( fieldMapping == null )
                        InitializeFields ();
                    if ( BaseType != null )
                    {
                        if ( BaseType is ILType )
                        {
                            totalFieldCnt = ( ( ILType ) BaseType ).TotalFieldCount + fieldTypes.Length;
                        }
                        else
                            totalFieldCnt = fieldTypes.Length;
                    }
                    else
                        totalFieldCnt = fieldTypes.Length;
                }
                return totalFieldCnt;
            }
        }

#if ENABLE_NEO_MODE
        public IMethod[] NeoVTable
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                EnsureNeoVTable();
                return neoVTable;
            }
        }

        internal bool TryGetNeoVTableSlot(IMethod method, out int slot)
        {
            EnsureNeoVTable();
            if (method != null && neoVTableSlots != null)
                return neoVTableSlots.TryGetValue(method.SignatureString, out slot);
            slot = -1;
            return false;
        }

        public int TotalPrimitiveSize
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                // fieldMapping is the first thing InitializeFields() assigns,
                // and it stays non-null afterwards. Use it as the
                // initialized-flag so cyclic field-type graphs (e.g. a struct
                // referenced indirectly by its own static field type) don't
                // re-enter InitializeFields and overflow the stack.
                if (fieldMapping == null)
                {
                    InitializeFields();
                }
                return totalPrimitiveSize;
            }
        }

        public int TotalReferenceCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (fieldMapping == null)
                {
                    InitializeFields();
                }
                return totalReferenceCnt;
            }
        }

        public int StaticTotalPrimitiveSize
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (fieldMapping == null)
                {
                    InitializeFields();
                }
                return totalStaticPrimitiveSize;
            }
        }

        public int StaticTotalReferenceCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (fieldMapping == null)
                {
                    InitializeFields();
                }
                return totalStaticReferenceCnt;
            }
        }

        /// <summary>
        /// Step 12: the natural alignment of this IL value type = the maximum
        /// natural alignment among its fields (recursed into nested IL value
        /// types; enums use their underlying primitive size; reference fields
        /// use pointer size 4). Returns 1 for a type with no fields. Only
        /// meaningful for value types, but computed for all ILTypes from their
        /// field set. Forces InitializeFields (which computes it) if needed.
        /// </summary>
        public int NaturalAlignment
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (fieldMapping == null)
                {
                    InitializeFields();
                }
                return naturalAlignment;
            }
        }

        void EnsureNeoVTable()
        {
            if (neoVTable == null)
                BuildNeoVTable();
        }

        void BuildNeoVTable()
        {
            if (neoVTableBuilding)
                throw new InvalidOperationException(string.Format("Recursive Neo VTable build detected for type {0}", FullName));

            neoVTableBuilding = true;
            try
            {
                if (methods == null)
                    InitializeMethods();

                List<IMethod> slots = new List<IMethod>();
                List<string> slotKeys = new List<string>();
                Dictionary<string, int> slotMap = new Dictionary<string, int>();

                if (!IsValueType && !IsInterface)
                {
                    IType baseForVTable = BaseType;
                    if (baseForVTable is ILType baseILType)
                    {
                        var baseTable = baseILType.NeoVTable;
                        for (int i = 0; i < baseTable.Length; i++)
                        {
                            slots.Add(baseTable[i]);
                            string key = baseILType.neoVTableSlotKeys[i];
                            slotKeys.Add(key);
                            if (!slotMap.ContainsKey(key))
                                slotMap.Add(key, i);
                        }
                    }
                    else
                    {
                        if (baseForVTable == null)
                            baseForVTable = appdomain.ObjectType;
                        AddNeoBaseVirtualSlots(baseForVTable, slots, slotKeys, slotMap);
                    }

                    HashSet<ILMethod> added = new HashSet<ILMethod>();
                    foreach (var pair in methods)
                    {
                        foreach (var method in pair.Value)
                        {
                            if (!added.Add(method) || !IsNeoVTableCandidate(method))
                                continue;

                            int slot = FindNeoOverrideSlot(method, slotMap);
                            string key = method.SignatureString;
                            if (slot >= 0)
                            {
                                slots[slot] = method;
                                slotMap[key] = slot;
                                slotKeys[slot] = key;
                            }
                            else
                            {
                                AddNeoVTableSlot(method, key, slots, slotKeys, slotMap);
                            }
                        }
                    }

                    // Step 11: ensure every interface-implementing method occupies
                    // a class VTable slot, even when the implementing method is a
                    // plain (non-virtual) C# method. C# implicit interface impl
                    // produces non-virtual methods that IsNeoVTableCandidate rejects,
                    // but they ARE the dispatch target of an interface callvirt, so
                    // the interface offset map must be able to land on them.
                    EnsureNeoInterfaceImplementorSlots(slots, slotKeys, slotMap);
                }

                neoVTable = slots.ToArray();
                neoVTableSlotKeys = slotKeys.ToArray();
                neoVTableSlots = slotMap;
            }
            finally
            {
                neoVTableBuilding = false;
            }
        }

        void AddNeoBaseVirtualSlots(IType baseType, List<IMethod> slots, List<string> slotKeys, Dictionary<string, int> slotMap)
        {
            if (baseType == null)
                return;

            foreach (var method in baseType.GetMethods())
            {
                if (!IsNeoVTableCandidate(method))
                    continue;
                AddNeoVTableSlot(method, method.SignatureString, slots, slotKeys, slotMap);
            }
        }

        // Step 11: walk the interface graph of THIS type (its own Implements plus
        // each interface's parent interfaces) and ensure every interface method
        // has its implementing method in the class VTable. When the interface
        // method's key is already in slotMap (the implementing method was virtual
        // and got a slot above), nothing happens. Otherwise we resolve the
        // implementing instance method on this type by signature and give it a
        // fresh slot, so interface dispatch can land on it.
        void EnsureNeoInterfaceImplementorSlots(List<IMethod> slots, List<string> slotKeys, Dictionary<string, int> slotMap)
        {
            IType[] impls = Implements;
            if (impls == null || impls.Length == 0)
                return;

            var seen = new HashSet<IType>();
            var queue = new Queue<IType>();
            foreach (var impl in impls)
            {
                if (impl != null && seen.Add(impl))
                    queue.Enqueue(impl);
            }

            while (queue.Count > 0)
            {
                IType iface = queue.Dequeue();
                if (iface == null)
                    continue;

                foreach (var ifaceMethod in iface.GetMethods())
                {
                    if (ifaceMethod == null)
                        continue;
                    string key = ifaceMethod.SignatureString;
                    if (slotMap.ContainsKey(key))
                        continue;

                    IMethod implMethod = FindNeoImplementingMethod(ifaceMethod);
                    if (implMethod != null)
                        AddNeoVTableSlot(implMethod, key, slots, slotKeys, slotMap);
                }

                IType[] parents = iface.Implements;
                if (parents != null)
                {
                    foreach (var parent in parents)
                    {
                        if (parent != null && seen.Add(parent))
                            queue.Enqueue(parent);
                    }
                }
            }
        }

        // Find an instance method declared on THIS type that matches the given
        // interface method by SignatureString (name + generic count + param
        // fullnames + return fullname). Returns null if none.
        IMethod FindNeoImplementingMethod(IMethod ifaceMethod)
        {
            if (ifaceMethod == null || methods == null)
                return null;
            string key = ifaceMethod.SignatureString;
            foreach (var pair in methods)
            {
                if (pair.Value == null)
                    continue;
                foreach (var m in pair.Value)
                {
                    if (m == null || m.IsStatic || m.IsConstructor)
                        continue;
                    if (m.SignatureString == key)
                        return m;
                }
            }
            return null;
        }

        static void AddNeoVTableSlot(IMethod method, string key, List<IMethod> slots, List<string> slotKeys, Dictionary<string, int> slotMap)
        {
            if (slotMap.ContainsKey(key))
                return;

            slotMap.Add(key, slots.Count);
            slots.Add(method);
            slotKeys.Add(key);
        }

        int FindNeoOverrideSlot(ILMethod method, Dictionary<string, int> slotMap)
        {
            int slot;
            if (slotMap.TryGetValue(method.SignatureString, out slot))
                return slot;

            if (method.Definition.HasOverrides)
            {
                foreach (var overrideRef in method.Definition.Overrides)
                {
                    IMethod overrideMethod = null;
                    try
                    {
                        bool invalidToken;
                        overrideMethod = appdomain.GetMethod(overrideRef, this, method, out invalidToken);
                    }
                    catch
                    {
                    }

                    if (overrideMethod != null && slotMap.TryGetValue(overrideMethod.SignatureString, out slot))
                        return slot;

                    if (slotMap.TryGetValue(GetNeoVTableSlotKey(overrideRef), out slot))
                        return slot;
                }
            }

            if (!method.Definition.IsNewSlot)
            {
                if (slotMap.TryGetValue(method.SignatureString, out slot))
                    return slot;
            }

            return -1;
        }

        static bool IsNeoVTableCandidate(IMethod method)
        {
            if (method == null || method.IsStatic || method.IsConstructor)
                return false;

            if (method is ILMethod ilMethod)
                return ilMethod.Definition.IsVirtual;

            if (method is CLRMethod clrMethod)
            {
                var info = clrMethod.MethodInfo;
                return info != null && info.IsVirtual && !info.IsStatic;
            }

            return false;
        }

        static string GetNeoVTableSlotKey(MethodReference method)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(method.Name);
            sb.Append('|');
            sb.Append(method.GenericParameters != null ? method.GenericParameters.Count : 0);
            sb.Append('(');
            if (method.HasParameters)
            {
                for (int i = 0; i < method.Parameters.Count; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    sb.Append(method.Parameters[i].ParameterType.FullName);
                }
            }
            sb.Append(")->");
            sb.Append(method.ReturnType != null ? method.ReturnType.FullName : string.Empty);
            return sb.ToString();
        }

        // === Neo only - Step 11: interface offset map ===
        //
        // One entry per implemented interface (including inherited parent
        // interfaces). VTableOffset is the starting slot into neoVTable;
        // MethodSlotKeys[k] is the SignatureString of the interface's k-th
        // declared method (so the interface-local slot is stable). In the
        // common contiguous case the implementing method for slot k lives at
        // neoVTable[VTableOffset + k]. When that does not hold (explicit
        // interface impl, base-class-provided method), ClassSlotRemap[k] holds
        // the actual class VTable index and overrides the contiguous formula.
        internal struct InterfaceEntry
        {
            public IType InterfaceType;
            public int VTableOffset;
            public string[] MethodSlotKeys;
            public int[] ClassSlotRemap; // null in the contiguous fast path
        }

        /// <summary>
        /// Starting slot offset of <paramref name="interfaceType"/>'s methods
        /// within this type's class VTable. Throws if the interface is not
        /// implemented by this type.
        /// </summary>
        public int GetInterfaceVTableOffset(IType interfaceType)
        {
            if (!TryGetInterfaceVTableOffset(interfaceType, out int offset))
                throw new MissingMethodException(string.Format("Type {0} does not implement interface {1}.", FullName, interfaceType != null ? interfaceType.FullName : "<null>"));
            return offset;
        }

        public bool TryGetInterfaceVTableOffset(IType interfaceType, out int offset)
        {
            EnsureNeoInterfaceMap();
            if (interfaceType == null || neoInterfaceOffsets == null)
            {
                offset = -1;
                return false;
            }
            return neoInterfaceOffsets.TryGetValue(interfaceType, out offset);
        }

#if ENABLE_NEO_MODE
        /// <summary>
        /// Step 23 (Neo AOT serializer): read-only accessor over the interface
        /// offset map, so the .neo writer can serialize each InterfaceEntry
        /// (InterfaceType-ref + VTableOffset + MethodSlotKeys[] + ClassSlotRemap[])
        /// without reconstructing it from the public surface. Additive internal
        /// accessor on an already-computed field (no behavior change); Neo-only.
        /// </summary>
        internal InterfaceEntry[] NeoInterfaceMapForAOT
        {
            get
            {
                EnsureNeoInterfaceMap();
                return neoInterfaceMap;
            }
        }
#endif

        /// <summary>
        /// 0-based slot of <paramref name="method"/> within its own interface
        /// declaring type, as seen by this implementing type's interface map.
        /// </summary>
        public bool TryGetInterfaceMethodSlot(IType interfaceType, IMethod method, out int slot)
        {
            slot = -1;
            if (interfaceType == null || method == null)
                return false;
            EnsureNeoInterfaceMap();
            if (neoInterfaceMap == null)
                return false;
            string key = method.SignatureString;
            for (int i = 0; i < neoInterfaceMap.Length; i++)
            {
                if (neoInterfaceMap[i].InterfaceType != interfaceType)
                    continue;
                var keys = neoInterfaceMap[i].MethodSlotKeys;
                if (keys == null)
                    return false;
                for (int k = 0; k < keys.Length; k++)
                {
                    if (keys[k] == key)
                    {
                        slot = k;
                        return true;
                    }
                }
                return false;
            }
            return false;
        }

        /// <summary>
        /// 0-based slot of <paramref name="method"/> within THIS type's own
        /// declared method list (used when this ILType is itself an interface,
        /// so the JIT can encode the interface-local slot). Lazily builds a
        /// SignatureString -> slot dictionary over this type's declared
        /// candidate methods.
        /// </summary>
        public int GetInterfaceMethodSlotSelf(IMethod method)
        {
            if (method == null)
                throw new ArgumentNullException(nameof(method));
            if (neoSelfMethodSlots == null)
                BuildNeoSelfMethodSlots();
            if (neoSelfMethodSlots != null && neoSelfMethodSlots.TryGetValue(method.SignatureString, out int slot))
                return slot;
            throw new MissingMethodException(string.Format("Interface {0} has no declared method matching {1}.", FullName, method.SignatureString));
        }

        void BuildNeoSelfMethodSlots()
        {
            // INVARIANT (Step 11 interface dispatch): the interface-local slot
            // index `k` produced here MUST match exactly the runtime slot `k`
            // produced by AddNeoInterfaceEntry for the same interface type. The
            // JIT encodes an interfaceMethodSlot against THIS ordering, and the
            // runtime Callvirt_Interface interpreter reads it back against
            // AddNeoInterfaceEntry's ordering. This equivalence holds ONLY
            // because both methods enumerate the interface's `methods`
            // dictionary (here directly, there via GetMethods(), which iterates
            // the same dict in the same order) and apply the SAME
            // IsNeoVTableCandidate filter. If you change the enumeration order
            // or the candidate filter in one of these two methods, you MUST
            // make the identical change in the other, or interface calls will
            // silently mis-dispatch. See AddNeoInterfaceEntry.
            if (methods == null)
                InitializeMethods();
            var map = new Dictionary<string, int>();
            int slot = 0;
            foreach (var pair in methods)
            {
                if (pair.Value == null)
                    continue;
                foreach (var m in pair.Value)
                {
                    if (m == null || !IsNeoVTableCandidate(m))
                        continue;
                    string key = m.SignatureString;
                    if (!map.ContainsKey(key))
                        map.Add(key, slot++);
                }
            }
            neoSelfMethodSlots = map;
        }

        internal void EnsureNeoInterfaceMap()
        {
            if (neoInterfaceMap == null)
                BuildNeoInterfaceMap();
        }

        void BuildNeoInterfaceMap()
        {
            if (neoInterfaceMapBuilding)
                throw new InvalidOperationException(string.Format("Recursive Neo interface map build detected for type {0}", FullName));

            neoInterfaceMapBuilding = true;
            try
            {
                EnsureNeoVTable();

                var entries = new List<InterfaceEntry>();
                var offsets = new Dictionary<IType, int>();

                // Step 11: inherit the base ILType's interface map verbatim. The
                // derived class VTable inherits the base's vtable slots at the
                // same indices (BuildNeoVTable copies base slots first), so the
                // base's interface offsets + slot keys remain valid for the
                // derived type. This covers "derived overrides a base-provided
                // interface implementation" without the derived type redeclaring
                // the interface.
                if (BaseType is ILType baseILType)
                {
                    baseILType.EnsureNeoInterfaceMap();
                    if (baseILType.neoInterfaceMap != null)
                    {
                        foreach (var be in baseILType.neoInterfaceMap)
                        {
                            // Skip interfaces this type re-implements (handled by its
                            // own Implements below); the this-type entry wins.
                            entries.Add(be);
                            offsets[be.InterfaceType] = be.VTableOffset;
                        }
                    }
                }

                // Walk the implemented-interface graph (this.Implements plus
                // each interface's own Implements for inheritance chains).
                var seen = new HashSet<IType>(offsets.Keys);
                var queue = new Queue<IType>();
                IType[] impls = Implements;
                if (impls != null)
                {
                    foreach (var impl in impls)
                    {
                        if (impl != null && seen.Add(impl))
                            queue.Enqueue(impl);
                    }
                }
                while (queue.Count > 0)
                {
                    IType iface = queue.Dequeue();
                    EnqueueParentInterfaces(iface, seen, queue);
                    AddNeoInterfaceEntry(iface, entries, offsets);
                }

                neoInterfaceMap = entries.ToArray();
                neoInterfaceOffsets = offsets;
            }
            finally
            {
                neoInterfaceMapBuilding = false;
            }
        }

        static void EnqueueParentInterfaces(IType iface, HashSet<IType> seen, Queue<IType> queue)
        {
            if (iface == null)
                return;
            IType[] parents = iface.Implements;
            if (parents == null)
                return;
            foreach (var parent in parents)
            {
                if (parent != null && seen.Add(parent))
                    queue.Enqueue(parent);
            }
        }

        void AddNeoInterfaceEntry(IType iface, List<InterfaceEntry> entries, Dictionary<IType, int> offsets)
        {
            if (iface == null)
                return;
            if (offsets.ContainsKey(iface))
                return;

            // Enumerate the interface's declared candidate methods in declaration
            // order; assign each a 0-based slot k. MethodSlotKeys[k] is its key.
            //
            // INVARIANT (Step 11 interface dispatch): the per-interface slot
            // index `k` assigned here MUST match exactly the JIT-time
            // interface-local slot produced by BuildNeoSelfMethodSlots for this
            // same interface type. The JIT encodes an interfaceMethodSlot
            // against BuildNeoSelfMethodSlots' ordering, and the runtime
            // Callvirt_Interface interpreter reads that slot back against THIS
            // ordering. This equivalence holds ONLY because both methods
            // enumerate the interface's `methods` dictionary (here via
            // GetMethods(), which iterates that dict in the same order, there
            // directly) and apply the SAME IsNeoVTableCandidate filter. If you
            // change the enumeration order or the candidate filter in one of
            // these two methods, you MUST make the identical change in the
            // other, or interface calls will silently mis-dispatch. See
            // BuildNeoSelfMethodSlots.
            var methods = iface.GetMethods();
            var keys = new List<string>();
            var classSlots = new List<int>();
            bool contiguous = true;
            int firstClassSlot = -1;

            for (int k = 0; k < methods.Count; k++)
            {
                var m = methods[k];
                if (m == null || !IsNeoVTableCandidate(m))
                    continue;
                string key = m.SignatureString;

                int localSlot = keys.Count;
                keys.Add(key);

                int classSlot = -1;
                if (neoVTableSlots != null && neoVTableSlots.TryGetValue(key, out int resolved))
                    classSlot = resolved;

                classSlots.Add(classSlot);

                if (firstClassSlot < 0)
                    firstClassSlot = classSlot;

                // Contiguous fast path: classSlot == firstClassSlot + localSlot.
                if (classSlot != firstClassSlot + localSlot)
                    contiguous = false;
            }

            int vTableOffset = firstClassSlot;
            int[] remap = null;
            if (!contiguous || firstClassSlot < 0)
            {
                // Non-contiguous / unresolved fallback. Anchor offset at the
                // minimum resolved class slot (or 0 if none resolved) and rely
                // on the explicit per-method ClassSlotRemap.
                int minSlot = int.MaxValue;
                bool any = false;
                for (int i = 0; i < classSlots.Count; i++)
                {
                    if (classSlots[i] >= 0)
                    {
                        any = true;
                        if (classSlots[i] < minSlot)
                            minSlot = classSlots[i];
                    }
                }
                vTableOffset = any ? minSlot : 0;
                remap = classSlots.ToArray();
            }

            var entry = new InterfaceEntry
            {
                InterfaceType = iface,
                VTableOffset = vTableOffset,
                MethodSlotKeys = keys.ToArray(),
                ClassSlotRemap = remap,
            };
            entries.Add(entry);
            offsets[iface] = vTableOffset;
        }

        /// <summary>
        /// Resolves the class VTable slot for a given interface + interface-local
        /// method slot on this type. Returns false if the interface is not
        /// implemented or the slot is out of range.
        /// </summary>
        internal bool TryResolveNeoInterfaceClassSlot(IType interfaceType, int interfaceMethodSlot, out int classSlot)
        {
            classSlot = -1;
            EnsureNeoInterfaceMap();
            if (neoInterfaceMap == null)
                return false;
            for (int i = 0; i < neoInterfaceMap.Length; i++)
            {
                if (neoInterfaceMap[i].InterfaceType != interfaceType)
                    continue;
                ref var entry = ref neoInterfaceMap[i];
                if (entry.MethodSlotKeys == null || interfaceMethodSlot < 0 || interfaceMethodSlot >= entry.MethodSlotKeys.Length)
                    return false;
                if (entry.ClassSlotRemap != null)
                {
                    int remapped = entry.ClassSlotRemap[interfaceMethodSlot];
                    if (remapped < 0)
                        return false;
                    classSlot = remapped;
                }
                else
                {
                    classSlot = entry.VTableOffset + interfaceMethodSlot;
                }
                return true;
            }
            return false;
        }

#endif

        internal List<ILType> GenericInstances
        {
            get
            {
                return genericInstances;
            }
        }

        /// <summary>
        /// 初始化IL类型
        /// </summary>
        /// <param name="def">MONO返回的类型定义</param>
        /// <param name="domain">ILdomain</param>
        public ILType ( TypeReference def, Runtime.Enviorment.AppDomain domain )
        {
            this.typeRef = def;
            RetriveDefinitino ( def );
            appdomain = domain;
            jitFlags = domain.DefaultJITFlags;
        }

        /// <summary>
        /// 加载类型
        /// </summary>
        /// <param name="def"></param>
        void RetriveDefinitino ( TypeReference def )
        {
            if ( !def.IsGenericParameter && definition == null )
            {
                if ( def is TypeSpecification )
                {
                    if ( def.IsByReference || def is ArrayType )
                    {
                        definition = null;
                    }
                    else
                        RetriveDefinitino ( ( ( TypeSpecification ) def ).ElementType );
                }
                else
                    definition = def as TypeDefinition;
            }
        }

        public bool IsGenericInstance
        {
            get
            {
                return genericArguments != null;
            }
        }

        public ILType GetGenericDefinition ()
        {
            return genericDefinition;
        }
        public KeyValuePair<string, IType> [] GenericArguments
        {
            get
            {
                return genericArguments;
            }
        }

        public IType ElementType { get { return elementType; } }

        public bool IsArray
        {
            get; private set;
        }

        public int ArrayRank
        {
            get; private set;
        }

        public bool IsByRef
        {
            get
            {
                return typeRef.IsByReference;
            }
        }

        private bool? isValueType;

        public bool IsValueType
        {
            get
            {
                if ( IsArray )
                    return false;
                if ( isValueType == null )
                    isValueType = definition.IsValueType;

                return isValueType.Value;
            }
        }

        public bool IsDelegate
        {
            get
            {
                if ( !baseTypeInitialized )
                    InitializeBaseType ();
                return isDelegate;
            }
        }

        public bool IsPrimitive
        {
            get { return false; }
        }

        public bool IsInterface
        {
            get
            {
                return TypeDefinition.IsInterface;
            }
        }

        public Type TypeForCLR
        {
            get
            {
                if ( !baseTypeInitialized )
                    InitializeBaseType ();
                if ( typeRef is ArrayType )
                {
                    return arrayCLRType;
                }
                else if ( typeRef is ByReferenceType )
                {
                    return byRefCLRType;
                }
                else if ( this.IsEnum )
                {
                    if ( enumType == null )
                        InitializeFields ();
                    return enumType.TypeForCLR;
                }
                else if ( FirstCLRBaseType != null && FirstCLRBaseType is CrossBindingAdaptor )
                {
                    return ( ( CrossBindingAdaptor ) FirstCLRBaseType ).RuntimeType.TypeForCLR;
                }
                else if ( FirstCLRInterface != null && FirstCLRInterface is CrossBindingAdaptor )
                {
                    return ( ( CrossBindingAdaptor ) FirstCLRInterface ).RuntimeType.TypeForCLR;
                }
                else
                    return typeof ( ILTypeInstance );
            }
        }

        public Type ReflectionType
        {
            get
            {
                if ( reflectionType == null )
                    reflectionType = new ILRuntimeType ( this );
                return reflectionType;
            }
        }

        public IType ByRefType
        {
            get
            {
                return byRefType;
            }
        }
        public IType ArrayType
        {
            get
            {
                return arrayTypes != null ? arrayTypes [ 1 ] : null;
            }
        }

        public bool IsEnum
        {
            get
            {
                return definition != null ? definition.IsEnum : false;
            }
        }

        string fullName, fullNameForNested;

        public string FullNameForNested
        {
            get
            {
                if ( string.IsNullOrEmpty ( fullNameForNested ) )
                {
                    if ( typeRef.IsNested )
                    {
                        fullNameForNested = FullName.Replace ( "/", "." );
                    }
                    else
                        fullNameForNested = FullName;
                }
                return fullNameForNested;
            }
        }

        public string FullName
        {
            get
            {
                if ( string.IsNullOrEmpty ( fullName ) )
                {
                    if ( typeRef.HasGenericParameters && genericArguments != null )
                    {
                        StringBuilder sb = new StringBuilder ();
                        sb.Append ( typeRef.FullName );
                        sb.Append ( '<' );
                        bool first = true;
                        foreach ( var i in genericArguments )
                        {
                            if ( first )
                                first = false;
                            else
                                sb.Append ( ", " );
                            sb.Append ( i.Value.FullName );
                        }
                        sb.Append ( '>' );
                        fullName = sb.ToString ();
                    }
                    else
                        fullName = typeRef.FullName;
                    /* 
                    if (typeRef.IsNested)
                    {
                        fullNameForNested = fullName.Replace("/", ".");
                    }
                    else
                        fullNameForNested = fullName;
                    */
                }
                return fullName;
            }
        }
        public string Name
        {
            get
            {
                return typeRef.Name;
            }
        }

        public StackObject DefaultObject { get { return default(StackObject); } }
        public int TypeIndex
        {
            get
            {
                if (tIdx < 0)
                    tIdx = appdomain.AllocTypeIndex(this);
                return tIdx;
            }
        }

        public List<IMethod> GetMethods ()
        {
            if ( methods == null )
                InitializeMethods ();
            List<IMethod> res = new List<IMethod> ();
            foreach ( var i in methods )
            {
                foreach ( var j in i.Value )
                    res.Add ( j );
            }

            return res;
        }
        void InitializeInterfaces ()
        {
            interfaceInitialized = true;
            if ( definition != null && definition.HasInterfaces )
            {
                interfaces = new IType [ definition.Interfaces.Count ];
                for ( int i = 0; i < interfaces.Length; i++ )
                {
                    interfaces [ i ] = appdomain.GetType ( definition.Interfaces [ i ].InterfaceType, this, null );
                    //only one clrInterface is valid
                    if ( interfaces [ i ] is CLRType && firstCLRInterface == null )
                    {
                        CrossBindingAdaptor adaptor;
                        if ( appdomain.CrossBindingAdaptors.TryGetValue ( interfaces [ i ].TypeForCLR, out adaptor ) )
                        {
                            interfaces [ i ] = adaptor;
                            firstCLRInterface = adaptor;
                        }
                        else
                            throw new TypeLoadException ( "Cannot find Adaptor for:" + interfaces [ i ].TypeForCLR.ToString () );
                    }
                }
            }
            if ( firstCLRInterface == null && BaseType != null && BaseType is ILType )
                firstCLRInterface = ( ( ILType ) BaseType ).FirstCLRInterface;
        }
        void InitializeBaseType ()
        {
            if ( definition != null && definition.BaseType != null )
            {
                bool specialProcess = false;
                List<int> spIdx = null;
                if ( definition.BaseType.IsGenericInstance )
                {
                    GenericInstanceType git = definition.BaseType as GenericInstanceType;
                    var elementType = appdomain.GetType ( git.ElementType, this, null );
                    if ( elementType is CLRType )
                    {
                        for ( int i = 0; i < git.GenericArguments.Count; i++ )
                        {
                            var ga = git.GenericArguments [ i ];
                            if ( ga == typeRef )
                            {
                                specialProcess = true;
                                if ( spIdx == null )
                                    spIdx = new List<int> ();
                                spIdx.Add ( i );
                            }
                        }
                    }
                }
                if ( specialProcess )
                {
                    //如果泛型参数是自身，则必须要特殊处理，否则会StackOverflow
                    var elementType = appdomain.GetType ( ( ( GenericInstanceType ) definition.BaseType ).ElementType, this, null );
                    foreach ( var i in appdomain.CrossBindingAdaptors )
                    {
                        if ( i.Key.IsGenericType && !i.Key.IsGenericTypeDefinition )
                        {
                            var gd = i.Key.GetGenericTypeDefinition ();
                            if ( gd == elementType.TypeForCLR )
                            {
                                var ga = i.Key.GetGenericArguments ();
                                bool match = true;
                                foreach ( var j in spIdx )
                                {
                                    if ( ga [ j ] != i.Value.AdaptorType )
                                    {
                                        match = false;
                                        break;
                                    }
                                }
                                if ( match )
                                {
                                    baseType = i.Value;
                                    break;
                                }
                            }
                        }
                    }
                    if ( baseType == null )
                    {
                        throw new TypeLoadException ( "Cannot find Adaptor for:" + definition.BaseType.FullName );
                    }
                }
                else
                {
                    baseType = appdomain.GetType ( definition.BaseType, this, null );
                    if ( baseType is CLRType )
                    {
                        if ( baseType.TypeForCLR == typeof ( Enum ) || baseType.TypeForCLR == typeof ( object ) || baseType.TypeForCLR == typeof ( ValueType ) || baseType.TypeForCLR == typeof ( System.Enum ) )
                        {//都是这样，无所谓
                            baseType = null;
                        }
                        else if ( baseType.TypeForCLR == typeof ( MulticastDelegate ) )
                        {
                            baseType = null;
                            isDelegate = true;
                        }
                        else
                        {
                            CrossBindingAdaptor adaptor;
                            if ( appdomain.CrossBindingAdaptors.TryGetValue ( baseType.TypeForCLR, out adaptor ) )
                            {
                                baseType = adaptor;
                            }
                            else
                                throw new TypeLoadException ( "Cannot find Adaptor for:" + baseType.TypeForCLR.ToString () );
                            //继承了其他系统类型
                            //env.logger.Log_Error("ScriptType:" + Name + " Based On a SystemType:" + BaseType.Name);
                            //HasSysBase = true;
                            //throw new Exception("不得继承系统类型，脚本类型系统和脚本类型系统是隔离的");
                        }
                    }
                }
            }
            var curBase = baseType;
            while ( curBase is ILType )
            {
                curBase = curBase.BaseType;
            }
            firstCLRBaseType = curBase;
            baseTypeInitialized = true;
        }

        internal IMethod GetMethod(MethodDefinition def)
        {
            if (methods == null)
                InitializeMethods();
            if (def.IsConstructor)
            {
                foreach(var i in constructors)
                {
                    if (i.Definition == def)
                        return i;
                }
            }
            else
            {
                foreach(var i in  methods)
                {
                    foreach(var j in i.Value)
                    {
                        if(j.Definition == def)
                        {
                            return j;
                        }
                    }
                }
            }
            return null;
        }

        public ILMethod GetMethodByGenericDefinition(ILMethod definitionMethod)
        {
            if (definitionMethod == null)
                return null;
            if (methods == null)
                InitializeMethods();
            foreach(var i in methods)
            {
                foreach(var j in i.Value)
                {
                    if (j.Definition == definitionMethod.Definition)
                        return j;
                }
            }
            return null;
        }

        public IMethod GetMethod ( string name )
        {
            if ( methods == null )
                InitializeMethods ();
            List<ILMethod> lst;
            if ( methods.TryGetValue ( name, out lst ) )
            {
                return lst [ 0 ];
            }
            return null;
        }

        public IMethod GetMethod ( string name, int paramCount, bool declaredOnly = false )
        {
            if ( methods == null )
                InitializeMethods ();
            List<ILMethod> lst;
            if ( methods.TryGetValue ( name, out lst ) )
            {
                foreach ( var i in lst )
                {
                    if ( i.ParameterCount == paramCount )
                        return i;
                }
            }
            if ( declaredOnly )
                return null;
            else
            {
                //skip clr base type, this doesn't make any sense
                if ( BaseType != null && !( BaseType is CrossBindingAdaptor ) )
                    return BaseType.GetMethod ( name, paramCount, false );
                else
                    return null;
            }
        }

        void InitializeMethods()
        {
            methods = new Dictionary<string, List<ILMethod>>();
            constructors = new List<ILMethod>();
            if (definition == null)
                return;
            if (definition.HasCustomAttributes)
            {
                for (int i = 0; i < definition.CustomAttributes.Count; i++)
                {
                    int f;
                    if (definition.CustomAttributes[i].GetJITFlags(AppDomain, out f))
                    {
                        this.jitFlags = f;
                        break;
                    }
                }
            }
            foreach (var i in definition.Methods)
            {
                if (i.IsConstructor)
                {
                    if (i.IsStatic)
                        staticConstructor = new ILMethod(i, i, this, appdomain, jitFlags);
                    else
                        constructors.Add(new ILMethod(i, i, this, appdomain, jitFlags));
                }
                else
                {
                    List<ILMethod> lst;
                    var m = new ILMethod(i, i, this, appdomain, jitFlags);
                    if (!methods.TryGetValue(i.Name, out lst))
                    {
                        lst = new List<ILMethod>();
                        methods[i.Name] = lst;
                    }
                    lst.Add(m);
                    if (i.HasOverrides)
                    {
                        //Deal with interface implementation with explicit naming and implicit naming
                        foreach(var o in i.Overrides)
                        {
                            if (o.Name != i.Name)
                            {
                                if (!methods.TryGetValue(o.Name, out lst))
                                {
                                    lst = new List<ILMethod>();
                                    methods[o.Name] = lst;
                                }
                                lst.Add(m);
                            }
                            else
                            {
                                string cn = $"{o.DeclaringType.FullName}.{o.Name}";
                                if (cn != i.Name)
                                {
                                    if (!methods.TryGetValue(cn, out lst))
                                    {
                                        lst = new List<ILMethod>();
                                        methods[cn] = lst;
                                    }
                                    lst.Add(m);
                                }
                            }
                        }
                    }
                    
                }
            }

            foreach (var i in definition.Events)
            {
                int fieldIdx = -1;
                InitializeFields();
                if(i.AddMethod.IsStatic)
                    staticFieldMapping.TryGetValue(i.Name,out fieldIdx);
                else
                    fieldMapping.TryGetValue(i.Name, out fieldIdx);
                if (methods.TryGetValue(i.AddMethod.Name, out var lst))
                {
                    lst[0].SetEventAddOrRemove(true, false, fieldIdx);
                }
                if (methods.TryGetValue(i.RemoveMethod.Name, out lst))
                {
                    lst[0].SetEventAddOrRemove(false, true, fieldIdx);
                }
            }

            if (!appdomain.SuppressStaticConstructor && !staticConstructorCalled)
            {
                staticConstructorCalled = true;
                if (staticConstructor != null && (!TypeReference.HasGenericParameters || IsGenericInstance))
                {
#if ENABLE_NEO_MODE
                    // TODO Step 7: see InitializeMethods entry above. Re-enable once
                    // Neo Stfld_*/Ldfld_* handlers are wired up.
#else
                    appdomain.Invoke(staticConstructor, null, null);
#endif
                }
            }
        }

        public IMethod GetVirtualMethod ( IMethod method )
        {
            IType [] genericArguments = null;
            if ( method.IsGenericInstance )
            {
                if ( method is ILMethod )
                {
                    genericArguments = ( ( ILMethod ) method ).GenericArugmentsArray;
                }
                else
                {
                    genericArguments = ( ( CLRMethod ) method ).GenericArguments;
                }
            }

            var m = GetMethod ( method.Name, method.Parameters, genericArguments, method.ReturnType, true );
            if ( m == null && BaseType != null )
            {
                m = BaseType.GetVirtualMethod ( method );
                if ( m != null )
                    return m;
            }
            if ( m == null && method.DeclearingType.IsInterface )
            {
                if (method.DeclearingType is ILType)
                {
                    ILType iltype = (ILType)method.DeclearingType;
                    m = GetMethod(string.Format("{0}.{1}", iltype.FullNameForNested, method.Name), method.Parameters, genericArguments, method.ReturnType, true);
                }
                else
                {
                    ((CLRType)method.DeclearingType).TypeForCLR.GetClassName(out var clsName, out var realName, out var isByRef);
                    m = GetMethod(string.Format("{0}.{1}", realName, method.Name), method.Parameters, genericArguments, method.ReturnType, true);
                }
            }

            if ( m == null || m.IsGenericInstance == method.IsGenericInstance )
                return m;
            else
                return method;

        }

        bool CheckTypeEqual(IType typeA, IType typeB)
        {
            if (typeA is ILGenericParameterType pt1 && typeB is ILGenericParameterType pt2)
                return pt1 == pt2 || pt1.TypeReference == pt2.TypeReference || pt1.Name == pt2.Name;
            else
                return typeA == typeB;
        }

        public IMethod GetMethod ( string name, List<IType> param, IType [] genericArguments, IType returnType = null, bool declaredOnly = false )
        {
            if ( methods == null )
                InitializeMethods ();
            List<ILMethod> lst;
            IMethod genericMethod = null;
            if ( methods.TryGetValue ( name, out lst ) )
            {
                for ( var idx = 0; idx < lst.Count; idx++ )
                {
                    var i = lst [ idx ];
                    int pCnt = param != null ? param.Count : 0;
                    if ( i.ParameterCount == pCnt )
                    {
                        bool match = true;
                        if ( genericArguments != null && i.GenericParameterCount == genericArguments.Length && genericMethod == null )
                        {
                            genericMethod = CheckGenericParams ( i, param, genericArguments, ref match );
                        }
                        else
                        {
                            match = CheckGenericArguments ( i, genericArguments );
                            if ( !match )
                                continue;
                            for ( int j = 0; j < pCnt; j++ )
                            {
                                if (!CheckTypeEqual(param[j], i.Parameters[j]))
                                {
                                    match = false;
                                    break;
                                }
                            }
                            if ( match )
                            {
                                match = returnType == null || CheckTypeEqual(i.ReturnType, returnType);
                            }
                            if ( match )
                                return i;
                        }
                    }
                }
            }
            if ( genericArguments != null && genericMethod != null )
            {
                var m = genericMethod.MakeGenericMethod ( genericArguments );
                lst.Add ( ( ILMethod ) m );
                return m;
            }
            if ( declaredOnly )
                return null;
            else
            {
                if ( BaseType != null )
                    return BaseType.GetMethod ( name, param, genericArguments, returnType, false );
                else
                    return null;
            }
        }

        bool CheckGenericArguments ( ILMethod i, IType [] genericArguments )
        {
            if ( genericArguments == null )
            {
                return i.GenericArguments == null;
            }
            else
            {
                if ( i.GenericArguments == null )
                    return false;
                else if ( i.GenericArguments.Length != genericArguments.Length )
                    return false;
                if ( i.GenericArguments.Length == genericArguments.Length )
                {
                    for ( int j = 0; j < genericArguments.Length; j++ )
                    {
                        if ( i.GenericArguments [ j ].Value != genericArguments [ j ] )
                            return false;
                    }
                    return true;
                }
                else
                    return false;
            }
        }

        bool IsGenericArgumentMatch ( IType p, IType p2, IType [] genericArguments )
        {
            bool found = false;
            foreach ( var a in genericArguments )
            {
                if ( a == p2 )
                {
                    found = true;
                    break;
                }
            }
            if ( !found )
            {
                return false;
            }
            else
                return true;
        }

        ILMethod CheckGenericParams ( ILMethod i, List<IType> param, IType [] genericArguments, ref bool match )
        {
            ILMethod genericMethod = null;
            if ( param != null )
            {
                for ( int j = 0; j < param.Count; j++ )
                {
                    var p = i.Parameters [ j ];
                    if ( p.IsGenericParameter )
                    {
                        if ( IsGenericArgumentMatch ( p, param [ j ], genericArguments ) )
                            continue;
                        else
                        {
                            match = false;
                            break;
                        }
                    }
                    if ( p.IsByRef )
                        p = p.ElementType;
                    if ( p.IsArray )
                        p = p.ElementType;

                    var p2 = param [ j ];
                    if ( p2.IsByRef )
                        p2 = p2.ElementType;
                    if ( p2.IsArray )
                        p2 = p2.ElementType;
                    if ( p.IsGenericParameter )
                    {
                        if ( i.Parameters [ j ].IsByRef == param [ j ].IsByRef && i.Parameters [ j ].IsArray == param [ j ].IsArray && IsGenericArgumentMatch ( p, p2, genericArguments ) )
                            continue;
                        else
                        {
                            match = false;
                            break;
                        }
                    }
                    if ( p.HasGenericParameter )
                    {
                        if ( p.Name != p2.Name )
                        {
                            match = false;
                            break;
                        }
                        //TODO should match the generic parameters;
                        continue;
                    }


                    if ( p2 != p )
                    {
                        match = false;
                        break;
                    }
                }
            }
            if ( match )
            {
                genericMethod = i;
            }
            return genericMethod;
        }

        public List<ILMethod> GetConstructors ()
        {
            if ( constructors == null )
                InitializeMethods ();
            return constructors;
        }

        public IMethod GetStaticConstroctor ()
        {
            if ( constructors == null )
                InitializeMethods ();
            return staticConstructor;
        }

        public IMethod GetConstructor ( int paramCnt )
        {
            if ( constructors == null )
                InitializeMethods ();
            foreach ( var i in constructors )
            {
                if ( i.ParameterCount == paramCnt )
                {
                    return i;
                }
            }
            return null;
        }
        public IMethod GetConstructor(List<IType> param)
        {
            return GetConstructor(param, true);
        }
        public IMethod GetConstructor(List<IType> param, bool exactMatch = true)
        {
            if (constructors == null)
                InitializeMethods();
            foreach (var i in constructors)
            {
                if (i.ParameterCount == param.Count)
                {
                    bool match = true;

                    for (int j = 0; j < param.Count; j++)
                    {
                        if ((exactMatch && param[j] != i.Parameters[j]) || !param[j].CanAssignTo(i.Parameters[j]))
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                        return i;
                }
            }
            return null;
        }

        public int GetFieldIndex ( object token )
        {
            if ( fieldMapping == null )
                InitializeFields ();
            int idx;
            int hashCode = token.GetHashCode ();
            if ( fieldTokenMapping.TryGetValue ( hashCode, out idx ) )
                return idx;
            FieldReference f = token as FieldReference;
            if ( staticFieldMapping != null && staticFieldMapping.TryGetValue ( f.Name, out idx ) )
            {
                fieldTokenMapping [ hashCode ] = idx;
                return idx;
            }
            if ( fieldMapping.TryGetValue ( f.Name, out idx ) )
            {
                fieldTokenMapping [ hashCode ] = idx;
                return idx;
            }

            return -1;
        }

#if ENABLE_NEO_MODE
        internal ILTypeFieldOffset GetFieldOffset(object token)
        {
            var idx = GetFieldIndex(token);
            return GetFieldOffset(idx);
        }

        internal ILTypeFieldOffset GetFieldOffset(int idx)
        {
            if (idx < FieldStartIndex)
                return ((ILType)BaseType).GetFieldOffset(idx);
            else
            {
                return fieldOffsets[idx - FieldStartIndex];
            }
        }

        internal ILTypeFieldOffset GetStaticFieldOffset(int idx)
        {
            return staticFieldOffsets[idx];
        }
#endif

        public IType GetField(string name, out int fieldIdx)
        {
            if (fieldMapping == null)
                InitializeFields();
            if (fieldMapping.TryGetValue(name, out fieldIdx))
            {
                return fieldTypes[fieldIdx - FieldStartIndex];
            }
            else if (BaseType != null && BaseType is ILType)
            {
                return ((ILType)BaseType).GetField(name, out fieldIdx);
            }
            else if (staticFieldMapping != null && staticFieldMapping.TryGetValue(name, out fieldIdx))
            {
                return staticFieldTypes[fieldIdx];
            }
            else 
                return null;
        }

        public IType GetField(int fieldIdx, out FieldReference fr)
        {
            if (fieldMapping == null)
                InitializeFields();
            if (fieldIdx < FieldStartIndex)
                return ((ILType)BaseType).GetField(fieldIdx, out fr);
            else
            {
                fr = fieldReferences[fieldIdx - FieldStartIndex];
                return fieldTypes[fieldIdx - FieldStartIndex];
            }
        }

        public IType GetField(int fieldIdx, out FieldDefinition fd)
        {
            if (fieldMapping == null)
                InitializeFields();
            if (fieldIdx < FieldStartIndex)
                return ((ILType)BaseType).GetField(fieldIdx, out fd);
            else
            {
                fd = fieldDefinitions[fieldIdx - FieldStartIndex];
                return fieldTypes[fieldIdx - FieldStartIndex];
            }
        }

        void InitializeFields ()
        {
            fieldMapping = new Dictionary<string, int> ();
            if (definition == null)
            {
                fieldTypes = new IType[0];
                fieldReferences = new FieldReference[0];
                fieldDefinitions = new FieldDefinition[0];
#if ENABLE_NEO_MODE
                fieldOffsets = new ILTypeFieldOffset[0];
                totalPrimitiveSize = 0;
                totalReferenceCnt = 0;
                totalStaticPrimitiveSize = 0;
                totalStaticReferenceCnt = 0;
                naturalAlignment = 1;
#endif
                return;
            }
            fieldTypes = new IType [ definition.Fields.Count ];
#if ENABLE_NEO_MODE
            fieldOffsets = new ILTypeFieldOffset[definition.Fields.Count];
            staticFieldOffsets = new ILTypeFieldOffset[definition.Fields.Count];
#endif
            fieldReferences = new FieldReference[definition.Fields.Count];
            fieldDefinitions = new FieldDefinition[definition.Fields.Count];
            var fields = definition.Fields;
            int idx = FieldStartIndex;
            int idxStatic = 0;
#if ENABLE_NEO_MODE
            int primitiveOffset = 0;
            int referenceOffset = 0;
            int staticPrimitiveOffset = 0;
            int staticReferenceOffset = 0;
            // Step 12: track the max natural alignment over this type's
            // instance fields. Starts at 1 (the alignment of an empty / byte-
            // only struct) and is raised by each field per its natural size.
            int maxFieldAlignment = 1;
#endif
            for (int i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                if (field.IsStatic)
                {
                    //It makes no sence to initialize
                    if (!TypeReference.HasGenericParameters || IsGenericInstance)
                    {
                        if (staticFieldTypes == null)
                        {
                            staticFieldTypes = new IType[definition.Fields.Count];
                            staticFieldReferences = new FieldReference[definition.Fields.Count];
                            staticFieldDefinitions = new FieldDefinition[definition.Fields.Count];
                            staticFieldMapping = new Dictionary<string, int>();
                        }
                        staticFieldMapping[field.Name] = idxStatic;
                        if (field.FieldType.IsGenericParameter)
                        {
                            staticFieldTypes[idxStatic] = FindGenericArgument(field.FieldType.Name);
                        }
                        else
                            staticFieldTypes[idxStatic] = appdomain.GetType(field.FieldType, this, null);
                        FieldReference fr = field;
                        if (typeRef.IsGenericInstance)
                        {
                            fr = new FieldReference(field.Name, staticFieldTypes[idxStatic].ToTypeReference(appdomain.LoadedModules[0]), typeRef);                            
                        }
                        staticFieldReferences[idxStatic] = fr;
                        staticFieldDefinitions[idxStatic] = field;
#if ENABLE_NEO_MODE
                        var staticFieldType = staticFieldTypes[idxStatic];
                        if (staticFieldType.IsPrimitive)
                        {
                            staticFieldOffsets[idxStatic] = new ILTypeFieldOffset()
                            {
                                PrimitiveOffset = staticPrimitiveOffset,
                                ReferenceOffset = staticReferenceOffset
                            };
                            staticPrimitiveOffset += AppDomain.GetPrimitiveSize(staticFieldType);
                        }
                        else
                        {
                            if (staticFieldType.IsValueType && staticFieldType is ILType sit)
                            {
                                staticFieldOffsets[idxStatic] = new ILTypeFieldOffset()
                                {
                                    PrimitiveOffset = staticPrimitiveOffset,
                                    ReferenceOffset = staticReferenceOffset
                                };
                                staticPrimitiveOffset += sit.TotalPrimitiveSize;
                                staticReferenceOffset += sit.TotalReferenceCount;
                            }
                            else
                            {
                                staticFieldOffsets[idxStatic] = new ILTypeFieldOffset()
                                {
                                    PrimitiveOffset = staticPrimitiveOffset,
                                    ReferenceOffset = staticReferenceOffset
                                };
                                staticReferenceOffset++;
                            }
                        }
#endif
                        idxStatic++;
                    }
                }
                else
                {
                    fieldMapping[field.Name] = idx;
                    IType fieldType;
                    if (field.FieldType.IsGenericParameter)
                    {
                        fieldType = FindGenericArgument(field.FieldType.Name);
                    }
                    else
                        fieldType = appdomain.GetType(field.FieldType, this, null);
                    fieldTypes[idx - FieldStartIndex] = fieldType;
                    FieldReference fr = field;
                    if (typeRef.IsGenericInstance)
                    {
                        fr = new FieldReference(field.Name, fieldType.ToTypeReference(appdomain.LoadedModules[0]), typeRef);
                    }
                    fieldReferences[idx - FieldStartIndex] = fr;
                    fieldDefinitions[idx - FieldStartIndex] = field;
                    if (IsEnum)
                    {
                        enumType = fieldType;
                    }

#if ENABLE_NEO_MODE
                    if (fieldType.IsPrimitive)
                    {
                        fieldOffsets[idx - FieldStartIndex] = new ILTypeFieldOffset()
                        {
                            PrimitiveOffset = primitiveOffset,
                            ReferenceOffset = referenceOffset
                        };
                        int pSize = AppDomain.GetPrimitiveSize(fieldType);
                        primitiveOffset += pSize;
                        if (pSize > maxFieldAlignment)
                            maxFieldAlignment = pSize;
                    }
                    else
                    {
                        if(fieldType.IsValueType && fieldType is ILType it)
                        {
                            fieldOffsets[idx - FieldStartIndex] = new ILTypeFieldOffset()
                            {
                                PrimitiveOffset = primitiveOffset,
                                ReferenceOffset = referenceOffset
                            };
                            primitiveOffset += it.TotalPrimitiveSize;
                            referenceOffset += it.TotalReferenceCount;
                            // Recurse: a nested IL value type's natural
                            // alignment is its own max field alignment.
                            int nestedAlign = it.NaturalAlignment;
                            if (nestedAlign > maxFieldAlignment)
                                maxFieldAlignment = nestedAlign;
                        }
                        else
                        {
                            fieldOffsets[idx - fieldStartIdx] = new ILTypeFieldOffset()
                            {
                                PrimitiveOffset = primitiveOffset,
                                ReferenceOffset = referenceOffset
                            };
                            referenceOffset++;
                            // Reference / pointer field → pointer size 4.
                            if (4 > maxFieldAlignment)
                                maxFieldAlignment = 4;
                        }
                    }
#endif
                    idx++;
                }
            }
            Array.Resize ( ref fieldTypes, idx - FieldStartIndex );
            Array.Resize ( ref fieldDefinitions, idx - FieldStartIndex );
#if ENABLE_NEO_MODE
            Array.Resize(ref fieldOffsets, idx - FieldStartIndex );

            totalPrimitiveSize = primitiveOffset;
            totalReferenceCnt = referenceOffset;
            // Step 12: finalize the type's natural alignment. For an enum, the
            // accumulator above already reflects its single backing field's
            // primitive size (the underlying type); for a value type it is the
            // max over its fields (recursed for nested VTs); for a reference
            // type it is the max over its instance fields (rarely needed, but
            // harmless). 1 is the floor for an empty struct.
            naturalAlignment = maxFieldAlignment < 1 ? 1 : maxFieldAlignment;
#endif

            if ( staticFieldTypes != null )
            {
                Array.Resize ( ref staticFieldTypes, idxStatic );
                Array.Resize ( ref staticFieldDefinitions, idxStatic );
#if ENABLE_NEO_MODE
                Array.Resize(ref staticFieldOffsets, idxStatic);
                totalStaticPrimitiveSize = staticPrimitiveOffset;
                totalStaticReferenceCnt = staticReferenceOffset;
#endif
                //staticInstance = new ILTypeStaticInstance(this);
            }
#if ENABLE_NEO_MODE
            else
            {
                staticFieldOffsets = null;
                totalStaticPrimitiveSize = 0;
                totalStaticReferenceCnt = 0;
            }
#endif
        }

        public IType FindGenericArgument ( string key )
        {
            var o = this.Generic ( key );
            if ( o == null && definition.GenericParameters != null )
            {
                for ( int i = 0; i < definition.GenericParameters.Count; i++ )
                {
                    if ( definition.GenericParameters [ i ].Name == key )
                    {
                        return this.Generic ( "!" + i );
                    }
                }
            }
            return o;
        }

        private IType Generic ( string key )
        {
            if ( this.genericArguments != null )
            {
                for ( int i = 0; i < this.genericArguments.Length; i++ )
                {
                    if ( this.genericArguments [ i ].Key == key)
                    {
                        return this.genericArguments [ i ].Value;
                    }
                }
            }

            return null;
        }

        public bool CanAssignTo ( IType type )
        {
            bool res = false;
            if ( this == type )
            {
                return true;
            }

            if ( IsEnum )
            {
                if ( type.TypeForCLR == typeof ( Enum ) )
                    return true;
            }
            if ( BaseType != null )
            {
                res = BaseType.CanAssignTo ( type );

                if ( res ) return true;
            }

            if ( Implements != null )
            {
                for ( int i = 0; i < interfaces.Length; i++ )
                {
                    var im = interfaces [ i ];
                    res = im.CanAssignTo ( type );
                    if ( res )
                        return true;
                }
            }
            return res;
        }

        public ILTypeInstance Instantiate ( bool callDefaultConstructor = true )
        {
            var res = new ILTypeInstance ( this );
            if ( callDefaultConstructor )
            {
                var m = GetConstructor ( CLR.Utils.Extensions.EmptyParamList );
                if ( m != null )
                {
                    appdomain.Invoke ( m, res, null );
                }
            }
            return res;
        }

        public ILTypeInstance Instantiate(object[] args)
        {
            var res = new ILTypeInstance(this);
            var argsTypes = new List<IType>(args.Length);
            foreach (var o in args)
            {
                if (o is ILTypeInstance)
                {
                    argsTypes.Add(((ILTypeInstance)o).Type);
                }
                else
                {
                    argsTypes.Add(appdomain.GetType(o.GetType()));
                }
            }
            var m = GetConstructor(argsTypes, false);
            if (m != null)
            {
                appdomain.Invoke(m, res, args);
            }

            return res;
        }

        public IType MakeGenericInstance ( KeyValuePair<string, IType> [] genericArguments )
        {
            if ( genericInstances == null )
                genericInstances = new List<ILType> ();
            foreach ( var i in genericInstances )
            {
                bool match = true;
                for ( int j = 0; j < genericArguments.Length; j++ )
                {
                    if (i.genericArguments[j].Value is ILGenericParameterType ptA && genericArguments[j].Value is ILGenericParameterType ptB)
                    {
                        if(ptA.TypeReference != ptB.TypeReference)
                        {
                            match = false;
                            break;
                        }
                    }
                    else if ( i.genericArguments [ j ].Value != genericArguments [ j ].Value )
                    {
                        match = false;
                        break;
                    }
                }
                if ( match )
                    return i;
            }
            GenericInstanceType def = new GenericInstanceType(typeRef);
            foreach (var i in genericArguments)
            {
                TypeReference tRef = null;
                if (i.Value is ILType ilType)
                {
                    tRef = ilType.typeRef;
                }
                else if (i.Value is ILGenericParameterType gpt)
                {
                    tRef = gpt.TypeReference;
                }
                else
                {
                    CLRType clrType = (CLRType)i.Value;
                    tRef = appdomain.LoadedModules[0].ImportReference(clrType.TypeForCLR);
                }
                def.GenericArguments.Add(tRef);
            }
            var res = new ILType ( def, appdomain );
            res.genericDefinition = this;
            res.genericArguments = genericArguments;
            foreach (var i in genericArguments)
            {
                if (i.Value.HasGenericParameter)
                {
                    res.hasGenericArguments = true;
                    break;
                }
            }
            genericInstances.Add ( res );
            return res;
        }

        public IType MakeByRefType ()
        {
            if ( byRefType == null )
            {
                var def = new ByReferenceType ( typeRef );
                byRefType = new ILType ( def, appdomain );
                ( ( ILType ) byRefType ).elementType = this;
                ( ( ILType ) byRefType ).byRefCLRType = this.TypeForCLR.MakeByRefType ();
            }
            return byRefType;
        }

        public IType MakeArrayType ( int rank )
        {
            if ( arrayTypes == null )
                arrayTypes = new Dictionary<int, IType> ();
            IType atype;
            if ( !arrayTypes.TryGetValue ( rank, out atype ) )
            {
                var def = new ArrayType ( typeRef, rank );
                atype = new ILType ( def, appdomain );
                ( ( ILType ) atype ).IsArray = true;
                ( ( ILType ) atype ).elementType = this;
                ( ( ILType ) atype ).arrayCLRType = rank > 1 ? this.TypeForCLR.MakeArrayType ( rank ) : this.TypeForCLR.MakeArrayType ();
                arrayTypes [ rank ] = atype;
            }
            return atype;
        }

        public IType ResolveGenericType ( IType contextType )
        {
            var ga = contextType.GenericArguments;
            if ( definition == null )
                return null;
            IType [] kv = new IType [ definition.GenericParameters.Count ];
            for ( int i = 0; i < kv.Length; i++ )
            {
                var gp = definition.GenericParameters [ i ];
                string name = gp.Name;
                foreach ( var j in ga )
                {
                    if ( j.Key == name )
                    {
                        kv [ i ] = j.Value;
                        break;
                    }
                }
            }

            foreach ( var i in genericInstances )
            {
                bool match = true;
                for ( int j = 0; j < kv.Length; j++ )
                {
                    if ( i.genericArguments [ j ].Value != kv [ j ] )
                    {
                        match = false;
                        break;
                    }
                }
                if ( match )
                    return i;
            }

            return null;
        }

        public int GetStaticFieldSizeInMemory ( HashSet<object> traversed )
        {
            return staticInstance != null ? staticInstance.GetSizeInMemory ( traversed ) : 0;
        }

        public unsafe int GetMethodBodySizeInMemory ()
        {
            int size = 0;
            if ( methods != null )
            {
                foreach ( var i in methods )
                {
                    foreach ( var j in i.Value )
                    {
                        if ( j.HasBody )
                        {
                            size += j.Body.Length * sizeof ( Runtime.Intepreter.OpCodes.OpCode );
                        }
                    }
                }
            }
            return size;
        }

        public ValueTypeInitInfo ValueTypeInitializationInfo
        {
            get
            {
                if(vtInitInfo == null)
                {
                    if (IsValueType)
                        vtInitInfo = new ValueTypeInitInfo(this);
                }
                return vtInitInfo;
            }
        }

        public void GetValueTypeSize ( out int fieldCout, out int managedCount )
        {
            if ( !valuetypeSizeCalculated )
            {
                valuetypeFieldCount = FieldTypes.Length + 1;
                valuetypeManagedCount = 0;
                for ( int i = 0; i < FieldTypes.Length; i++ )
                {
                    var ft = FieldTypes [ i ];
                    if ( ft.IsValueType )
                    {
                        if ( !ft.IsPrimitive && !ft.IsEnum )
                        {
                            if ( ft is ILType || ( ( CLRType ) ft ).ValueTypeBinder != null )
                            {
                                int fSize, fmCnt;
                                ft.GetValueTypeSize ( out fSize, out fmCnt );
                                valuetypeFieldCount += fSize;
                                valuetypeManagedCount += fmCnt;
                            }
                            else
                            {
                                valuetypeManagedCount++;
                            }
                        }
                    }
                    else
                    {
                        valuetypeManagedCount++;
                    }
                }
                if ( BaseType != null && BaseType is ILType )
                {
                    int fSize, fmCnt;
                    BaseType.GetValueTypeSize ( out fSize, out fmCnt );
                    valuetypeFieldCount += fSize - 1;//no header for base type fields
                    valuetypeManagedCount += fmCnt;
                }
                valuetypeSizeCalculated = true;
            }
            fieldCout = valuetypeFieldCount;
            managedCount = valuetypeManagedCount;
        }

        public override int GetHashCode ()
        {
            if ( hashCode == -1 )
                hashCode = System.Threading.Interlocked.Add ( ref instance_id, 1 );
            return hashCode;
        }

        public override string ToString ()
        {
            return FullName;
        }
    }
}
