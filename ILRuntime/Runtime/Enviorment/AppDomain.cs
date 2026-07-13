using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using ILRuntime.Mono.Cecil;
using System.Reflection;
using ILRuntime.Mono.Cecil.Cil;

using ILRuntime.CLR.TypeSystem;
using ILRuntime.CLR.Method;
using ILRuntime.CLR.Utils;
using ILRuntime.Runtime.Intepreter;
using ILRuntime.Runtime.Debugger;
using ILRuntime.Runtime.Stack;
using ILRuntime.Other;
using ILRuntime.Runtime.Intepreter.RegisterVM;
using System.Threading;
using ILRuntime.Hybrid;
using ILRuntime.Runtime.Intepreter.OpCodes;



#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
using AutoList = System.Collections.Generic.List<object>;
#else
using AutoList = ILRuntime.Other.UncheckedList<object>;
#endif
namespace ILRuntime.Runtime.Enviorment
{
    public unsafe delegate StackObject* CLRRedirectionDelegate(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj);
    public unsafe delegate void CLRRedirectionDelegateNeo(ILIntepreter intp, byte* frameBase, AutoList mStack, CLRMethod method, bool isNewObj, byte* retDst, int retRefBase);
    public delegate object CLRFieldGetterDelegate(ref object target);
    public unsafe delegate StackObject* CLRFieldBindingDelegate(ref object target, ILIntepreter __intp, StackObject* __esp, AutoList __mStack);
    public delegate void CLRFieldSetterDelegate(ref object target, object value);
    public delegate object CLRMemberwiseCloneDelegate(ref object target);
    public delegate object CLRCreateDefaultInstanceDelegate();
    public delegate object CLRCreateArrayInstanceDelegate(int size);

    public struct TypeSizeInfo
    {
        public ILType Type;
        public int StaticFieldSize;
        public int MethodBodySize;
        public int TotalSize;
    }

    public struct PrewarmInfo
    {
        public string TypeName;
        public string[] MethodNames;
    }
    public class AppDomain
    {
        Queue<ILIntepreter> freeIntepreters = new Queue<ILIntepreter>();
        Dictionary<int, ILIntepreter> intepreters = new Dictionary<int, ILIntepreter>();
        Dictionary<Type, CrossBindingAdaptor> crossAdaptors = new Dictionary<Type, CrossBindingAdaptor>(new ByReferenceKeyComparer<Type>());
        Dictionary<Type, ValueTypeBinder> valueTypeBinders = new Dictionary<Type, ValueTypeBinder>();
        ThreadSafeDictionary<string, IType> mapType = new ThreadSafeDictionary<string, IType>();
        Dictionary<Type, IType> clrTypeMapping = new Dictionary<Type, IType>(new ByReferenceKeyComparer<Type>());
        List<IType> typesByIndex = new List<IType>();
        ThreadSafeDictionary<int, IType> mapTypeToken = new ThreadSafeDictionary<int, IType>();
        ThreadSafeDictionary<int, IMethod> mapMethod = new ThreadSafeDictionary<int, IMethod>();
        ThreadSafeDictionary<int, Exception> mapException = new ThreadSafeDictionary<int, Exception>();
        ThreadSafeDictionary<long, string> mapString = new ThreadSafeDictionary<long, string>();
        Dictionary<System.Reflection.MethodBase, CLRRedirectionDelegate> redirectMap = new Dictionary<System.Reflection.MethodBase, CLRRedirectionDelegate>();
        Dictionary<System.Reflection.MethodBase, CLRRedirectionDelegateNeo> redirectMapNeo = new Dictionary<System.Reflection.MethodBase, CLRRedirectionDelegateNeo>();
        Dictionary<System.Reflection.FieldInfo, CLRFieldGetterDelegate> fieldGetterMap = new Dictionary<System.Reflection.FieldInfo, CLRFieldGetterDelegate>();
        Dictionary<System.Reflection.FieldInfo, CLRFieldSetterDelegate> fieldSetterMap = new Dictionary<System.Reflection.FieldInfo, CLRFieldSetterDelegate>();
        Dictionary<System.Reflection.FieldInfo, KeyValuePair<CLRFieldBindingDelegate, CLRFieldBindingDelegate>> fieldBindingMap = new Dictionary<FieldInfo, KeyValuePair<CLRFieldBindingDelegate, CLRFieldBindingDelegate>>();
        Dictionary<Type, CLRMemberwiseCloneDelegate> memberwiseCloneMap = new Dictionary<Type, CLRMemberwiseCloneDelegate>(new ByReferenceKeyComparer<Type>());
        Dictionary<Type, CLRCreateDefaultInstanceDelegate> createDefaultInstanceMap = new Dictionary<Type, CLRCreateDefaultInstanceDelegate>(new ByReferenceKeyComparer<Type>());
        Dictionary<Type, CLRCreateArrayInstanceDelegate> createArrayInstanceMap = new Dictionary<Type, CLRCreateArrayInstanceDelegate>(new ByReferenceKeyComparer<Type>());
        IType voidType, sbyteType, shortType, intType, longType, byteType, ushortType, uintType, ulongType,intptrType, boolType, floatType, doubleType, charType, objectType, jitAttributeType;
        DelegateManager dMgr;
        Assembly[] loadedAssemblies;
        Dictionary<string, byte[]> references = new Dictionary<string, byte[]>();
        DebugService debugService;
        AsyncJITCompileWorker jitWorker = new AsyncJITCompileWorker();
        int defaultJITFlags;
        List<ModuleDefinition> loadedModules = new List<ModuleDefinition>();
        List<Assembly> referenceAssemblies = new List<Assembly>();
        
        public static string Version { get { return "3.0.0"; } }

        /// <summary>
        /// Determine if invoking unbinded CLR method(using reflection) is allowed
        /// </summary>
        public bool AllowUnboundCLRMethod { get; set; }

#if DEBUG && !NO_PROFILER
        public int UnityMainThreadID { get; set; }
        public bool IsNotUnityMainThread()
        {
            return UnityMainThreadID != 0 && (UnityMainThreadID != System.Threading.Thread.CurrentThread.ManagedThreadId);
        }
#endif

        internal bool SuppressStaticConstructor { get; set; }

        internal List<ModuleDefinition> LoadedModules { get { return loadedModules; } }

#if ENABLE_NEO_MODE
        // Step 25 S3-2 (APPROACH 1): read-only snapshots of the compile-time
        // token-hash maps, so the .neo writer can record EVERY identity-hash
        // key that maps to a given resolved IType / IMethod. The baked token
        // operands in the deserialized bodies carry these hashes (ILType/
        // ILMethod identity hashes for IL refs; Cecil TypeReference /
        // MethodReference identity hashes for CLR-type + method-call tokens --
        // ALL identity-based against process-global counters, NONE reproducible
        // in a fresh AppDomain). The Cecil-free loader re-registers each
        // resolved ref under its recorded hash so the bodies resolve. Neo-only;
        // the maps are private otherwise. Returns a list of (hash, resolved)
        // pairs (a ref may appear under multiple baked hashes -- different Cecil
        // token instances of the same logical ref).
        internal IEnumerable<KeyValuePair<int, IType>> NeoTypeTokenSnapshot
        {
            get
            {
                foreach (var kv in mapTypeToken.InnerDictionary)
                    yield return kv;
            }
        }
        internal IEnumerable<KeyValuePair<int, IMethod>> NeoMethodTokenSnapshot
        {
            get
            {
                foreach (var kv in mapMethod.InnerDictionary)
                    yield return kv;
            }
        }
#endif

        public int DefaultJITFlags { get { return defaultJITFlags; } }

        public bool IsNeoMode
        {
            get
            {
                return (defaultJITFlags & ILRuntimeJITFlags.JITNeo) != 0;
            }
        }

        public unsafe AppDomain(int defaultJITFlags = ILRuntimeJITFlags.None)
        {
            AllowUnboundCLRMethod = true;
            InvocationContext.InitializeDefaultConverters();
            loadedAssemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            var mi = typeof(System.Runtime.CompilerServices.RuntimeHelpers).GetMethod("InitializeArray");
            RegisterCLRMethodRedirection(mi, CLRRedirections.InitializeArray);
#if ENABLE_NEO_MODE
            // child-6 (neo-arrays): Neo dispatch consults RedirectMapNeo
            // exclusively (CLRMethod.RedirectionNeo), which has no entry for
            // InitializeArray -> array initializers fell through to the
            // reflection fallback and hit the Step-13b RuntimeFieldHandle NIE.
            // Register the Neo redirect so InvokeNeoClrMethod serves it.
            // Register the Neo redirect so InvokeNeoClrMethod serves it.
            RegisterCLRMethodRedirectionNeo(mi, CLRRedirections.InitializeArrayNeo);
#endif
            mi = typeof(AppDomain).GetMethod("GetCurrentStackTrace");
            RegisterCLRMethodRedirection(mi, CLRRedirections.GetCurrentStackTrace);
            foreach (var i in typeof(System.Activator).GetMethods())
            {
                if (i.Name == "CreateInstance" && i.IsGenericMethodDefinition)
                {
                    RegisterCLRMethodRedirection(i, CLRRedirections.CreateInstance);
#if ENABLE_NEO_MODE
                    // child-22 (neo-activator): Neo dispatch consults RedirectMapNeo
                    // exclusively (CLRMethod.RedirectionNeo). Without a Neo entry the
                    // generic Activator.CreateInstance<T>() fell through to the autogen
                    // CreateInstance_*_Neo stub, which calls host Activator.Create-
                    // Instance<ILTypeInstance>() -> MissingMethodException on IL types.
                    // Registered for the generic DEFINITION, so TryGetRedirection
                    // (GetGenericMethodDefinition first) serves it for every
                    // instantiation. Mirrors the InitializeArrayNeo registration.
                    RegisterCLRMethodRedirectionNeo(i, CLRRedirections.CreateInstanceNeo);
#endif
                }
                else if (i.Name == "CreateInstance" && i.GetParameters().Length == 1)
                {
                    RegisterCLRMethodRedirection(i, CLRRedirections.CreateInstance2);
#if ENABLE_NEO_MODE
                    // First-registered-wins (this ctor runs before the test-harness
                    // autogen binding initializer), so this preempts the autogen
                    // CreateInstance_2_Neo stub.
                    RegisterCLRMethodRedirectionNeo(i, CLRRedirections.CreateInstance2Neo);
#endif
                }
                else if (i.Name == "CreateInstance" && i.GetParameters().Length == 2)
                {
                    RegisterCLRMethodRedirection(i, CLRRedirections.CreateInstance3);
#if ENABLE_NEO_MODE
                    RegisterCLRMethodRedirectionNeo(i, CLRRedirections.CreateInstance3Neo);
#endif
                }
            }
            foreach (var i in typeof(System.Type).GetMethods())
            {
                if (i.Name == "GetType" && i.IsStatic)
                {
                    RegisterCLRMethodRedirection(i, CLRRedirections.GetType);
                }
                if (i.Name == "Equals" && i.GetParameters()[0].ParameterType == typeof(Type))
                {
                    RegisterCLRMethodRedirection(i, CLRRedirections.TypeEquals);
                }
                if (i.Name == "IsAssignableFrom" && i.GetParameters()[0].ParameterType == typeof(Type))
                {
                    RegisterCLRMethodRedirection(i, CLRRedirections.IsAssignableFrom);
                }
            }
            foreach (var i in typeof(System.Delegate).GetMethods())
            {
                if (i.Name == "Combine" && i.GetParameters().Length == 2)
                {
                    RegisterCLRMethodRedirection(i, CLRRedirections.DelegateCombine);
#if ENABLE_NEO_MODE
                    RegisterCLRMethodRedirectionNeo(i, CLRRedirections.DelegateCombineNeo);
#endif
                }
                if (i.Name == "Remove")
                {
                    RegisterCLRMethodRedirection(i, CLRRedirections.DelegateRemove);
#if ENABLE_NEO_MODE
                    RegisterCLRMethodRedirectionNeo(i, CLRRedirections.DelegateRemoveNeo);
#endif
                }
                if (i.Name == "op_Equality")
                {
                    RegisterCLRMethodRedirection(i, CLRRedirections.DelegateEqulity);
                }
                if (i.Name == "op_Inequality")
                {
                    RegisterCLRMethodRedirection(i, CLRRedirections.DelegateInequlity);
                }
            }
            foreach (var i in typeof(MethodBase).GetMethods())
            {
                if (i.Name == "Invoke" && i.GetParameters().Length == 2)
                {
                    RegisterCLRMethodRedirection(i, CLRRedirections.MethodInfoInvoke);
                }
            }
            foreach (var i in typeof(Enum).GetMethods())
            {
                if (i.Name == "Parse" && i.GetParameters().Length == 2)
                {
                    RegisterCLRMethodRedirection(i, CLRRedirections.EnumParse);
                }
                if (i.Name == "GetValues" && i.GetParameters().Length == 1)
                {
                    RegisterCLRMethodRedirection(i, CLRRedirections.EnumGetValues);
                }
                if (i.Name == "GetNames" && i.GetParameters().Length == 1)
                {
                    RegisterCLRMethodRedirection(i, CLRRedirections.EnumGetNames);
                }
                if (i.Name == "GetName")
                {
                    RegisterCLRMethodRedirection(i, CLRRedirections.EnumGetName);
                }
#if NET_4_6 || NET_STANDARD_2_0
                if (i.Name == "HasFlag")
                {
                    RegisterCLRMethodRedirection(i, CLRRedirections.EnumHasFlag);
                }
                if (i.Name == "CompareTo")
                {
                    RegisterCLRMethodRedirection(i, CLRRedirections.EnumCompareTo);
                }
#endif
                if (i.Name == "ToObject" && i.GetParameters()[1].ParameterType == typeof(int))
                {
                    RegisterCLRMethodRedirection(i, CLRRedirections.EnumToObject);
#if ENABLE_NEO_MODE
                    // C12 (neo-ilruntimetype-runtime-bridge): the autogen
                    // ToObject_3_Neo stub passes the ILRuntimeType raw to the
                    // framework Enum.ToObject -> "Type must be a type provided
                    // by the runtime". Register the hand-written Neo redirect
                    // (first-registered-wins: this ctor runs before the
                    // test-harness CLRBindings.Initialize autogen Register).
                    RegisterCLRMethodRedirectionNeo(i, CLRRedirections.EnumToObjectNeo);
#endif
                }
            }
            mi = typeof(System.Type).GetMethod("GetTypeFromHandle");
            RegisterCLRMethodRedirection(mi, CLRRedirections.GetTypeFromHandle);
            mi = typeof(System.Type).GetMethod("MakeGenericType");
            RegisterCLRMethodRedirection(mi, CLRRedirections.TypeMakeGenericType);
            mi = typeof(object).GetMethod("GetType");
            RegisterCLRMethodRedirection(mi, CLRRedirections.ObjectGetType);
#if ENABLE_NEO_MODE
            // C4 (callvirt-gettype-vtable): Neo redirect for Object.GetType so an IL
            // instance's GetType() returns its ILRuntimeType (not typeof(ILTypeInstance)).
            // Reached after ResolveNeoCallvirtILTarget falls back to CLR dispatch for the
            // inherited non-virtual GetType (no VTable slot). Mirrors InitializeArrayNeo.
            RegisterCLRMethodRedirectionNeo(mi, CLRRedirections.ObjectGetTypeNeo);
#endif
            mi = typeof(Delegate).GetMethod("CreateDelegate", new Type[] { typeof(Type), typeof(MethodInfo) });
            RegisterCLRMethodRedirection(mi, CLRRedirections.DelegateCreateDelegate);
#if ENABLE_NEO_MODE
            // C12 (neo-ilruntimetype-runtime-bridge): Neo dispatch consults
            // RedirectMapNeo exclusively. Without a Neo entry the reflection
            // fallback passed the ILRuntimeType raw to the framework
            // Delegate.CreateDelegate -> "Type must be a runtime Type".
            // First-registered-wins preempts any autogen CreateDelegate stub.
            RegisterCLRMethodRedirectionNeo(mi, CLRRedirections.DelegateCreateDelegateNeo);
#endif
            mi = typeof(Delegate).GetMethod("CreateDelegate", new Type[] { typeof(Type), typeof(object), typeof(string) });
            RegisterCLRMethodRedirection(mi, CLRRedirections.DelegateCreateDelegate2);
#if ENABLE_NEO_MODE
            RegisterCLRMethodRedirectionNeo(mi, CLRRedirections.DelegateCreateDelegate2Neo);
#endif
            mi = typeof(Delegate).GetMethod("CreateDelegate", new Type[] { typeof(Type), typeof(object), typeof(MethodInfo) });
            RegisterCLRMethodRedirection(mi, CLRRedirections.DelegateCreateDelegate3);
#if ENABLE_NEO_MODE
            RegisterCLRMethodRedirectionNeo(mi, CLRRedirections.DelegateCreateDelegate3Neo);
#endif
            mi = typeof(Delegate).GetMethod("get_Target");
            RegisterCLRMethodRedirection(mi, CLRRedirections.DelegateGetTarget);
            dMgr = new DelegateManager(this);
            dMgr.RegisterDelegateConvertor<Action>((dele) =>
            {
                return dele;
            });

            RegisterCrossBindingAdaptor(new Adapters.AttributeAdapter());
            // D-IL-EXCEPTION-THROW: built-in System.Exception adaptor so an IL
            // `class X : System.Exception` loads (base-type resolution at
            // ILType.cs:1412-1418 looks up CrossBindingAdaptors by CLR base type
            // and throws TypeLoadException without this). The nested Adapter IS
            // a CLR Exception, so an IL exception instance's CLRInstance is a
            // real Exception the Throw opcode can unwrap. Engine-agnostic.
            RegisterCrossBindingAdaptor(new Adapters.ExceptionAdaptor());

#if ENABLE_NEO_MODE
            // Step 20 (neo-step20-async): register custom Neo async builder
            // redirects BEFORE the test-harness CLRBindings.Initialize runs. The
            // redirect map is first-registered-wins, so these custom redirects
            // WIN and the autogen non-functional *Neo builder stubs are skipped.
            // Sync-completing slice only; AwaitUnsafeOnCompleted/AwaitOnCompleted
            // throw a tagged NIE (the suspend slice -- neo-step20-async-suspend).
            CLRRedirectionsAsyncNeo.Register(this);
#endif

            debugService = new Debugger.DebugService(this);

#if ENABLE_NEO_MODE
            defaultJITFlags = ILRuntimeJITFlags.JITNeo | ILRuntimeJITFlags.JITImmediately;            
#else
            if ((defaultJITFlags & ILRuntimeJITFlags.JITNeo) != 0)
                throw new NotSupportedException("Cannot use JITNeo flag without ENABLE_NEO_MODE macro.");
#endif
            this.defaultJITFlags = defaultJITFlags;
        }

        public void Dispose()
        {
            debugService.StopDebugService();
            jitWorker.Dispose();
        }

        public IType VoidType { get { return voidType; } }
        public IType SByteType { get { return sbyteType; } }
        public IType ShortType { get { return shortType; } }
        public IType IntType { get { return intType; } }
        public IType LongType { get { return longType; } }
        public IType ByteType { get { return byteType; } }
        public IType UShortType { get { return ushortType; } }
        public IType UIntType { get { return uintType; } }
        public IType ULongType { get { return ulongType; } }
        public IType IntPtrType { get { return intptrType; } }
        public IType CharType { get { return charType; } }
        public IType BoolType { get { return boolType; } }
        public IType FloatType { get { return floatType; } }
        public IType DoubleType { get { return doubleType; } }
        public IType ObjectType { get { return objectType; } }

        public IType JITAttributeType { get { return jitAttributeType; } }

        /// <summary>
        /// Attention, this property isn't thread safe
        /// </summary>
        public Dictionary<string, IType> LoadedTypes { get { return mapType.InnerDictionary; } }

        bool IsThreadBinding = false;
        bool IsBindingDone = false;
        static object bindingLockObject = new object();
        internal Dictionary<MethodBase, CLRRedirectionDelegate> RedirectMap 
        { 
            get 
            {
                if (!IsThreadBinding && IsBindingDone)
                {
                    return redirectMap;
                }
                else
                {
                    lock(bindingLockObject)
                    {
                        return redirectMap;
                    }
                }
            } 
        }
        internal Dictionary<MethodBase, CLRRedirectionDelegateNeo> RedirectMapNeo 
        { 
            get 
            {
                if (!IsThreadBinding && IsBindingDone)
                {
                    return redirectMapNeo;
                }
                else
                {
                    lock(bindingLockObject)
                    {
                        return redirectMapNeo;
                    }
                }
            } 
        }
        internal Dictionary<FieldInfo, CLRFieldGetterDelegate> FieldGetterMap
        {
            get 
            {
                if (!IsThreadBinding && IsBindingDone)
                {
                    return fieldGetterMap;
                }
                else
                {
                    lock (bindingLockObject)
                    {
                        return fieldGetterMap;
                    }
                }
            } 
        }
        internal Dictionary<FieldInfo, CLRFieldSetterDelegate> FieldSetterMap 
        { 
            get 
            {
                if (!IsThreadBinding && IsBindingDone)
                {
                    return fieldSetterMap;
                }
                else
                {
                    lock (bindingLockObject)
                    {
                        return fieldSetterMap;
                    }
                }
            }
        }
        internal Dictionary<FieldInfo, KeyValuePair<CLRFieldBindingDelegate, CLRFieldBindingDelegate>> FieldBindingMap 
        {
            get 
            {
                if (!IsThreadBinding && IsBindingDone)
                {
                    return fieldBindingMap;
                }
                else
                {
                    lock (bindingLockObject)
                    {
                        return fieldBindingMap;
                    }
                }
            }
        }
        internal Dictionary<Type, CLRMemberwiseCloneDelegate> MemberwiseCloneMap 
        {
            get 
            {
                if (!IsThreadBinding && IsBindingDone)
                {
                    return memberwiseCloneMap;
                }
                else
                {
                    lock (bindingLockObject)
                    {
                        return memberwiseCloneMap;
                    }
                }
            }
        }
        internal Dictionary<Type, CLRCreateDefaultInstanceDelegate> CreateDefaultInstanceMap 
        { 
            get 
            {
                if (!IsThreadBinding && IsBindingDone)
                {
                    return createDefaultInstanceMap;
                }
                else
                {
                    lock (bindingLockObject)
                    {
                        return createDefaultInstanceMap;
                    }
                }
            } 
        }

        internal Dictionary<Type, CLRCreateArrayInstanceDelegate> CreateArrayInstanceMap 
        { 
            get 
            {
                if (!IsThreadBinding && IsBindingDone)
                {
                    return createArrayInstanceMap;
                }
                else
                {
                    lock (bindingLockObject)
                    {
                        return createArrayInstanceMap;
                    }
                }
            }
        }
        internal Dictionary<Type, CrossBindingAdaptor> CrossBindingAdaptors { get { return crossAdaptors; } }

        internal Dictionary<Type, ValueTypeBinder> ValueTypeBinders 
        { 
            get 
            {
                if (!IsThreadBinding && IsBindingDone)
                {
                    return valueTypeBinders;
                }
                else
                {
                    lock (bindingLockObject)
                    {
                        return valueTypeBinders;
                    }
                }
            }
        }
        public DebugService DebugService { get { return debugService; } }
        internal Dictionary<int, ILIntepreter> Intepreters { get { return intepreters; } }
        internal Queue<ILIntepreter> FreeIntepreters { get { return freeIntepreters; } }

        public DelegateManager DelegateManager { get { return dMgr; } }

        internal void EnqueueJITCompileJob(ILMethod method)
        {
            jitWorker.QueueCompileJob(method);
        }

        /// <summary>
        /// 加载Assembly 文件，从指定的路径
        /// </summary>
        /// <param name="path">路径</param>
        public void LoadAssemblyFile(string path)
        {
            FileInfo file = new FileInfo(path);

            if (!file.Exists)
            {
                throw new FileNotFoundException(string.Format("Assembly File not find!:\r\n{0}", path));
            }
            else
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    LoadAssembly(fs);

                    fs.Dispose();
                }
            }
        }

        public string GetCurrentStackTrace()
        {
            throw new NotSupportedException("Cannot call this method from CLR side");
        }

#if USE_MDB || USE_PDB
        /// <summary>
        /// 加载Assembly 文件和PDB文件或MDB文件，从指定的路径（PDB和MDB文件按默认命名方式，并且和Assembly文件处于同一目录中
        /// </summary>
        /// <param name="path">路径</param>
        public void LoadAssemblyFileAndSymbol(string path)
        {
            FileInfo file = new FileInfo(path);

            if (!file.Exists)
            {
                throw new FileNotFoundException(string.Format("Assembly File not find!:\r\n{0}", path));
            }
            else
            {
                var dlldir = file.DirectoryName;
                var assname = Path.GetFileNameWithoutExtension(file.Name);
                var pdbpath = string.Format("{0}/{1}.pdb",dlldir,assname);
                var mdbpath = string.Format("{0}/{1}.mdb", dlldir, assname);

                string symbolPath = "";

                bool isPDB = true;
                if (File.Exists(pdbpath))
                {
                    symbolPath = pdbpath;
                }
                else if (File.Exists(mdbpath))
                {
                    symbolPath = mdbpath;
                    isPDB = false;
                }


                if (string.IsNullOrEmpty(symbolPath))
                {
                    throw new FileNotFoundException(string.Format("symbol file not find!:\r\ncheck:\r\n{0}\r\n{1}\r\n", pdbpath,mdbpath));
                }

                using (FileStream fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read))
                {                  
                    
                    using (var pdbfs = new System.IO.FileStream(symbolPath, FileMode.Open))
                    {
                        if (isPDB)
                        {
                            LoadAssemblyPDB(fs, pdbfs);
                        }
                        else
                        {
                            LoadAssemblyMDB(fs, pdbfs);
                        }
                    }
                }
            }
        }
#endif

#if USE_PDB
        /// <summary>
        /// 加载Assembly 文件和PDB文件，两者都从指定的路径
        /// </summary>
        /// <param name="assemblyFilePath">Assembly 文件路径</param>
        /// <param name="symbolFilePath">symbol文件路径</param>
        public void LoadAssemblyFileAndPDB(string assemblyFilePath,string symbolFilePath)
        {
            FileInfo assfile = new FileInfo(assemblyFilePath);
            FileInfo pdbfile = new FileInfo(symbolFilePath);
            if (!assfile.Exists)
            {
                throw new FileNotFoundException(string.Format("Assembly File not find!:\r\n{0}", assemblyFilePath));
            }

            if (!pdbfile.Exists)
            {
                throw new FileNotFoundException(string.Format("symbol file not find!:\r\n{0}", symbolFilePath));
            }

            using (FileStream fs = new FileStream(assfile.FullName, FileMode.Open, FileAccess.Read))
            {

                using (var pdbfs = new System.IO.FileStream(pdbfile.FullName, FileMode.Open))
                {
                    LoadAssemblyPDB(fs, pdbfs);
                }
            }

        }

        /// <summary>
        ///  从流加载Assembly,以及symbol符号文件(pdb)
        /// </summary>
        /// <param name="stream">Assembly Stream</param>
        /// <param name="symbol">PDB Stream</param>
        public void LoadAssemblyPDB(System.IO.Stream stream, System.IO.Stream symbol)
        {
            LoadAssembly(stream, symbol, new Mono.Cecil.Pdb.PdbReaderProvider());
        }

#endif

#if USE_MDB
        /// <summary>
        /// 加载Assembly 文件和MDB文件，两者都从指定的路径
        /// </summary>
        /// <param name="assemblyFilePath">Assembly 文件路径</param>
        /// <param name="symbolFilePath">symbol文件路径</param>
        public void LoadAssemblyFileAndMDB(string assemblyFilePath, string symbolFilePath)
        {
            FileInfo assfile = new FileInfo(assemblyFilePath);
            FileInfo pdbfile = new FileInfo(symbolFilePath);
            if (!assfile.Exists)
            {
                throw new FileNotFoundException(string.Format("Assembly File not find!:\r\n{0}", assemblyFilePath));
            }

            if (!pdbfile.Exists)
            {
                throw new FileNotFoundException(string.Format("symbol file not find!:\r\n{0}", symbolFilePath));
            }

            using (FileStream fs = new FileStream(assfile.FullName, FileMode.Open, FileAccess.Read))
            {

                using (var pdbfs = new System.IO.FileStream(pdbfile.FullName, FileMode.Open))
                {
                    LoadAssemblyMDB(fs, pdbfs);
                }
            }
        }

        /// <summary>
        ///  从流加载Assembly,以及symbol符号文件(Mdb)
        /// </summary>
        /// <param name="stream">Assembly Stream</param>
        /// <param name="symbol">PDB Stream</param>
        public void LoadAssemblyMDB(System.IO.Stream stream, System.IO.Stream symbol)
        {
            LoadAssembly(stream, symbol, new Mono.Cecil.Mdb.MdbReaderProvider());
        }
#endif
        /// <summary>
        /// 从流加载Assembly 不加载symbol符号文件
        /// </summary>
        /// <param name="stream">Dll数据流</param>
        public void LoadAssembly(System.IO.Stream stream)
        {
            LoadAssembly(stream, null, null);
        }

        /// <summary>
        /// 从流加载Assembly,以及symbol符号文件(pdb)
        /// </summary>
        /// <param name="stream">Assembly Stream</param>
        /// <param name="symbol">symbol Stream</param>
        /// <param name="symbolReader">symbol 读取器</param>
        /// <param name="inMemory">是否完整读入内存</param>
        public void LoadAssembly(System.IO.Stream stream, System.IO.Stream symbol, ISymbolReaderProvider symbolReader)
        {
            var module = ModuleDefinition.ReadModule(stream); //从MONO中加载模块

            if (symbolReader != null && symbol != null)
            {
                module.ReadSymbols(symbolReader.GetSymbolReader(module, symbol)); //加载符号表
            }
            InitializeFromModule(module);
        }

        internal void AddType(ILType type)
        {
            mapType[type.FullName] = type;
            mapTypeToken[type.GetHashCode()] = type;
            mapTypeToken[type.TypeDefinition.GetHashCode()] = type;
        }

#if ENABLE_NEO_MODE
        /// <summary>
        /// Step 25 S3-2 (Cecil-free load): load a deserialized .neo model into
        /// THIS AppDomain WITHOUT reading any Cecil ModuleDefinition. The fresh
        /// AppDomain's mapType / mapTypeToken / mapMethod are populated PURELY
        /// from the .neo tables + the host CLR refs (Assembly.LoadFrom, the S3-5
        /// pattern). A method invoked on a Cecil-free ILType then executes via
        /// ExecuteNeo and yields the correct result (the capstone). Neo-only;
        /// the Cecil LoadAssembly(Stream) path is UNCHANGED.
        ///
        /// Flow (design D3): (1) host-CLR-ref registration via Assembly.LoadFrom;
        /// (2) two-pass ILType build -- pass 1 builds each NeoTypeDefRecord into a
        /// Cecil-free ILType (CreateFromNeoRecord) + registers in mapType, pass 2
        /// resolves base/interface by NAME + finalizes VTable/interface-map; (3)
        /// hash re-registration -- for each recorded APPROACH-1 binding, re-resolve
        /// the ref by NAME + rebind in mapTypeToken/mapMethod under the recorded
        /// hash so the bodies' baked token operands resolve; (4) bind bodies via
        /// NeoAssemblyLoader.Attach.
        /// </summary>
        internal NeoAOT.NeoLoadReport LoadNeoAssembly(NeoAOT.NeoAssemblyModel model,
            IReadOnlyList<string> hostClrRefPaths)
        {
            var report = new NeoAOT.NeoLoadReport();
            if (model == null || model.TypeDefs == null) return report;

            // (1) Host CLR ref registration. Best-effort (a BCL/already-loaded/
            // unresolvable ref is skipped, never fatal). MUST be Assembly.LoadFrom
            // (NOT LoadAssembly(refStream) -- that shadows CLR types as ILType,
            // the S3-5 Q1.2 finding). Puts host CLR assemblies into System.AppDomain
            // so GetType(string)'s live-assembly CLR fallback resolves them.
            if (hostClrRefPaths != null)
            {
                foreach (var rp in hostClrRefPaths)
                {
                    if (string.IsNullOrEmpty(rp) || !System.IO.File.Exists(rp)) continue;
                    try { System.Reflection.Assembly.LoadFrom(rp); }
                    catch { /* best-effort */ }
                }
            }

            // Initialize the BCL primitive types (a fresh Cecil-free AppDomain
            // has NO Cecil load -> InitializeFromModule never ran -> the IntType
            // / LongType / etc. getters + GetPrimitiveSize NRE/NIE). Resolve them
            // by name via the CLR fallback (the live System.AppDomain assemblies).
            // Mirrors InitializeFromModule's primitive-init block.
            EnsureNeoPrimitiveTypes();

            // (2) Two-pass build.
            // Pass 1: build every .neo ILType (Cecil-free) + register in mapType
            // (so pass 2 + ref resolution find them by name). Base/interface
            // resolution is DEFERRED (forward references within the .neo).
            var built = new ILType[model.TypeDefs.Length];
            for (int i = 0; i < model.TypeDefs.Length; i++)
            {
                var rec = model.TypeDefs[i];
                ILType t = null;
                try { t = ILType.CreateFromNeoRecord(rec, model, this); }
                catch (Exception ex) { report.Skipped.Add(("CreateFromNeoRecord threw: " + ex.GetType().Name + ": " + ex.Message, "#" + i)); continue; }
                if (t == null) { report.Skipped.Add(("CreateFromNeoRecord null", "#" + i)); continue; }
                built[i] = t;
                mapType[t.FullName] = t;
                mapTypeToken[t.GetHashCode()] = t;   // register under the FRESH identity hash
            }
            // Pass 2: resolve base/interface by NAME (all .neo types now in
            // mapType) + finalize VTable/interface-map/field indices.
            for (int i = 0; i < model.TypeDefs.Length; i++)
            {
                var t = built[i];
                if (t == null) continue;
                try { t.FinalizeFromNeoRecord(model.TypeDefs[i], model); }
                catch (Exception ex) { report.Skipped.Add(("FinalizeFromNeoRecord threw: " + ex.GetType().Name, t.FullName)); }
            }

            // ===== Step 25 neo-aot-multi-hotfix: cross-assembly IL-to-IL re-resolve.
            // This is a SUBSEQUENT .neo load (a second "hotfix assembly") OR the
            // first. Track every Cecil-free ILType built so far (this model's
            // survivors + any from prior LoadNeoAssembly calls). Then re-resolve
            // cross-assembly refs on ALL of them: a type built in an EARLIER load
            // whose field/base/interface referenced a type in THIS load had those
            // refs resolve to NULL at build time (the referenced type was absent
            // from mapType). Now that THIS load's types are registered, those NULL
            // slots can be re-resolved by NAME. Best-effort + idempotent (a slot
            // that is still unresolvable stays null). =====
            if (neoAotBuilt == null) neoAotBuilt = new List<(ILType, NeoAOT.NeoTypeDefRecord, NeoAOT.NeoAssemblyModel)>();
            // add this model's survivors to the tracked set FIRST (so a self-
            // contained .neo also benefits if it was loaded into an AppDomain that
            // already had a prior load; the re-resolve is idempotent on already-
            // resolved slots).
            for (int i = 0; i < model.TypeDefs.Length; i++)
            {
                var t = built[i];
                if (t == null) continue;
                neoAotBuilt.Add((t, model.TypeDefs[i], model));
            }
            // re-resolve cross-assembly refs on every tracked Cecil-free type now
            // that THIS load's types are in mapType. A type whose refs are all
            // already resolved is a fast no-op (the per-slot null guards).
            foreach (var entry in neoAotBuilt)
            {
                try { entry.type.ReResolveCrossAssemblyRefs(entry.model, entry.rec); }
                catch (Exception ex) { report.Skipped.Add(("ReResolveCrossAssemblyRefs threw: " + ex.GetType().Name + ": " + ex.Message, entry.type.FullName)); }
            }

            // (3) APPROACH 1 hash re-registration. For each recorded binding,
            // re-resolve the ref by NAME + rebind under the RECORDED compile-time
            // hash (an ALIAS key alongside the fresh hash). This makes the baked
            // token operands (which carry the compile-time hash) resolve in B's
            // maps. A binding whose name does not resolve in B is skipped.
            ReRegisterTokenBindings(model);

            // (4) Bind bodies. NeoAssemblyLoader.Attach matches each
            // NeoMethodDefRecord to a Cecil-free ILMethod shell (built in pass 1)
            // + populates CompiledFrame via InitCodeBodyFromNeo.
            try
            {
                var attachReport = NeoAOT.NeoAssemblyLoader.Attach(this, model);
                foreach (var a in attachReport.Attached) report.Attached.Add(a);
                foreach (var s in attachReport.Skipped) report.Skipped.Add(s);
            }
            catch (Exception ex) { report.Skipped.Add(("Attach threw: " + ex.GetType().Name + ": " + ex.Message, "?")); }

            // (5) Step 25 S3-4: seed each Cecil-free type's .cctor. Runs AFTER
            // Attach (so the .cctor's CompiledFrame is populated from the .neo
            // body -- running it before Attach would JIT-fallback with no AOT body
            // bound) + AFTER the hash re-registration (so the .cctor's own Stsfld
            // token operands resolve via the re-registered hashes). For each built
            // Cecil-free ILType whose staticConstructor is set (the factory tracks
            // the .cctor shell from the .neo MethodDefs): call Invoke(.cctor, null,
            // null) -- the SAME call the Legacy lazy StaticInstance getter uses.
            // Best-effort: a .cctor that throws is recorded as a skip, NEVER fatal
            // (the additive contract; the static state is left at default). A type
            // with no .cctor (StaticCtorMethodRefIdx == -1 -> no staticConstructor)
            // is skipped.
            for (int i = 0; i < built.Length; i++)
            {
                var t = built[i];
                if (t == null) continue;
                var cctor = t.StaticConstructorForNeoAOT;
                if (cctor == null) continue;
                try
                {
                    // Instantiate the StaticInstance FIRST (it sizes the static
                    // byte[]/AutoList from the installed static totals + offsets;
                    // the .cctor's Stsfld writes into it). C4 made the lazy getter
                    // fire the .cctor under Neo (the Cecil-path win), so for a
                    // Cecil-free type WITH static fields the getter invoked it
                    // already (flag now true). Guard the explicit seed so the .cctor
                    // runs EXACTLY ONCE: if the getter did not fire it (e.g. a side-
                    // effect-only .cctor with no static fields -> staticInstance not
                    // materialized -> flag still false), seed here + set the flag
                    // FIRST to mirror the lazy path and prevent a re-entrant .cctor
                    // via the getter during Invoke.
                    _ = t.StaticInstance;
                    if (!t.StaticConstructorCalledForNeoAOT)
                    {
                        t.StaticConstructorCalledForNeoAOT = true;
                        Invoke(cctor, null, null);
                    }
                    report.Attached.Add(".cctor seeded: " + t.FullName);
                }
                catch (Exception ex)
                {
                    report.Skipped.Add((".cctor seed skipped (" + ex.GetType().Name + ": " + ex.Message + ")", t.FullName));
                }
            }

            return report;
        }

#if ENABLE_NEO_MODE
        // Step 25 S3-2: initialize the BCL primitive types on a fresh Cecil-free
        // AppDomain (no Cecil load -> InitializeFromModule never ran). Resolves
        // them by name via the CLR fallback. Idempotent (guards on voidType ==
        // null). Neo-only.
        internal void EnsureNeoPrimitiveTypes()
        {
            if (voidType != null) return;
            voidType = GetType("System.Void");
            sbyteType = GetType("System.SByte");
            shortType = GetType("System.Int16");
            intType = GetType("System.Int32");
            longType = GetType("System.Int64");
            byteType = GetType("System.Byte");
            ushortType = GetType("System.UInt16");
            uintType = GetType("System.UInt32");
            ulongType = GetType("System.UInt64");
            intptrType = GetType("System.IntPtr");
            charType = GetType("System.Char");
            boolType = GetType("System.Boolean");
            floatType = GetType("System.Single");
            doubleType = GetType("System.Double");
            objectType = GetType("System.Object");
        }

        // Step 25 neo-aot-multi-hotfix: the Cecil-free ILTypes built by
        // LoadNeoAssembly, each paired with its originating .neo record + model,
        // so a SUBSEQUENT LoadNeoAssembly (a second "hotfix assembly") can
        // re-resolve cross-assembly IL-to-IL refs on the EARLIER-built types now
        // that the newly-loaded types are in mapType. Neo-only. Lazily allocated.
        List<(ILType type, NeoAOT.NeoTypeDefRecord rec, NeoAOT.NeoAssemblyModel model)> neoAotBuilt;
#endif


        // ref under the recorded compile-time hash. The ref is re-resolved by
        // NAME (IL via LoadedTypes; CLR via GetType(name)); a miss is skipped.
        internal void ReRegisterTokenBindings(NeoAOT.NeoAssemblyModel model)
        {
            if (model.TypeTokenBindings != null)
            {
                foreach (var b in model.TypeTokenBindings)
                {
                    if (b.Hash == 0 || string.IsNullOrEmpty(b.FullName)) continue;
                    IType resolved = null;
                    if (!LoadedTypes.TryGetValue(b.FullName, out resolved) || resolved == null)
                    {
                        try { resolved = GetType(b.FullName); } catch { resolved = null; }
                    }
                    if (resolved != null) mapTypeToken[b.Hash] = resolved;
                }
            }
            if (model.MethodTokenBindings != null)
            {
                foreach (var b in model.MethodTokenBindings)
                {
                    if (b.Hash == 0 || string.IsNullOrEmpty(b.FullName) || string.IsNullOrEmpty(b.MethodName)) continue;
                    IMethod resolved = ResolveMethodRefByName(b.FullName, b.MethodName, b.ParamCount);
                    if (resolved != null) mapMethod[b.Hash] = resolved;
                }
            }
        }

        // Resolve a method ref by name + declaring type + param count. IL via
        // LoadedTypes -> ILType.GetMethod (hierarchy-aware). If the declaring type
        // is a Cecil-free ILType that LACKS the method (e.g. an interface whose
        // abstract methods are NOT in the .neo MethodDefs -- they have no body),
        // build + register a Cecil-free shell on it. The shell's body is NEVER
        // run for an interface method (interface dispatch resolves the impl via
        // the implementing type's VTable); the shell only provides the
        // DeclearingType the dispatch reads. A miss returns null.
        IMethod ResolveMethodRefByName(string declaringFullName, string name, int paramCount)
        {
            if (!LoadedTypes.TryGetValue(declaringFullName, out var it) || it == null)
            {
                // neo-aot-delegate-exe-parity: a CLR delegate type defined in a host
                // assembly (e.g. TestCLRBinding.Clr2IlRefIntDelegate) is NOT pre-
                // loaded into LoadedTypes -- it resolves via GetType(name) (the CLR
                // fallback). A Newobj binding for its .ctor would otherwise miss.
                try { it = GetType(declaringFullName); }
                catch { it = null; }
                if (it == null) return null;
            }
            // neo-aot-delegate-exe-parity: a body's token operand (Ldftn/Call/
            // Callvirt/Newobj) re-resolves via this NAME lookup when the .neo was
            // compiled in a DIFFERENT AppDomain. A CLR declaring type (a host-CLR
            // helper like TestCLRBinding.InvokeRefCallback, or a CLR delegate .ctor
            // such as Clr2IlRefIntDelegate..ctor) is a CLRType, not an ILType -- the
            // prior `!(it is ILType)` guard returned null for every CLR method/ctor
            // binding, leaving the operand NULL at ExecuteNeo (silently skipped).
            if (it is ILType ilt)
            {
            IMethod m = null;
            try { m = ilt.GetMethod(name, paramCount, false); }
            catch { m = null; }
            if (m != null) return m;
            // neo-aot-delegate-exe-parity: a constructor token (delegate .ctor, IL
            // type .ctor) is NOT in the methods dictionary (InitializeMethods files
            // constructors into a separate list). A body's Newobj operand resolves
            // via this name-lookup, so a .ctor binding that misses here leaves the
            // Newobj operand NULL at ExecuteNeo (silently skipped / wrong result).
            // Search the constructors list by param count (the recorded MethodRef
            // for a .ctor carries the ctor param count). GetConstructors walks the
            // own-declared ctors only (a .ctor is never inherited).
            if (name == ".ctor" || name == ".cctor")
            {
                try
                {
                    var ctors = ilt.GetConstructors();
                    if (ctors != null)
                    {
                        foreach (var c in ctors)
                        {
                            if (c != null && c.ParameterCount == paramCount) return c;
                        }
                    }
                }
                catch { }
            }
            // Cecil-free ILType missing the method (interface abstract method):
            // synthesize a shell so the dispatch's declared-method resolution has
            // a non-null target (whose DeclearingType is the interface).
#if ENABLE_NEO_MODE
            if (ilt.isNeoAotType)
            {
                try
                {
                    var shell = ILMethod.CreateFromNeoShell(name, ilt, this,
                        new List<IType>(paramCount), this.VoidType, false, false);
                    ilt.AddNeoAotShell(name, shell);
                    return shell;
                }
                catch { return null; }
            }
#endif
            return null;
            }
            // CLR declaring type: resolve the method/ctor by name + param count on
            // the CLRType (its GetMethod walks the reflected CLR methods; a .ctor
            // name is matched among constructors). A miss returns null (best-effort,
            // same as the IL arm).
            if (it is CLRType clrt)
            {
                try
                {
                    if (name == ".ctor" || name == ".cctor")
                    {
                        // CLRType stores constructors separately from methods; use the
                        // param-count ctor lookup (a delegate .ctor signature is fixed).
                        return clrt.GetConstructorByParamCount(paramCount);
                    }
                    return clrt.GetMethod(name, paramCount, false);
                }
                catch { return null; }
            }
            return null;
        }
#endif

        internal void InitializeFromModule(ModuleDefinition module)
        {
            loadedModules.Add(module);
            if (module.HasAssemblyReferences) //如果此模块引用了其他模块
            {
                /*foreach (var ar in module.AssemblyReferences)
                {
                    if (moduleref.Contains(ar.Name) == false)
                        moduleref.Add(ar.Name);
                    if (moduleref.Contains(ar.FullName) == false)
                        moduleref.Add(ar.FullName);
                }
                */
            }

            if (module.HasTypes)
            {
                foreach (var t in module.GetTypes()) //获取所有此模块定义的类型
                {
                    if (t.IsPrimitive)
                        continue;
                    ILType type = new ILType(t, this);

                    AddType(type);

                }
            }

            if (voidType == null)
            {
                voidType = GetType("System.Void");
                sbyteType = GetType("System.SByte");
                shortType = GetType("System.Int16");
                intType = GetType("System.Int32");
                longType = GetType("System.Int64");
                byteType = GetType("System.Byte");
                ushortType = GetType("System.UInt16");
                uintType = GetType("System.UInt32");
                ulongType = GetType("System.UInt64");
                intptrType = GetType("System.IntPtr");
                charType = GetType("System.Char");
                boolType = GetType("System.Boolean");
                floatType = GetType("System.Single");
                doubleType = GetType("System.Double");
                objectType = GetType("System.Object");
                jitAttributeType = GetType("ILRuntime.Runtime.ILRuntimeJITAttribute");
            }
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
            debugService.NotifyModuleLoaded(module.Name);
#endif
        }

        /// <summary>
        /// External reference should be added to the AppDomain by the method
        /// </summary>
        /// <param name="name">Assembly name, without .dll</param>
        /// <param name="content">file content</param>
        public void AddReferenceBytes(string name, byte[] content)
        {
            references[name] = content;
        }

        public void AddReferenceAssembly(Assembly asm)
        {
            referenceAssemblies.Add(asm);
        }

        public void RegisterCLRMethodRedirection(MethodBase mi, CLRRedirectionDelegate func)
        {
            if (mi == null)
                return;

            if (!IsThreadBinding)
            {
                if (!redirectMap.ContainsKey(mi))
                    redirectMap[mi] = func;
            }
            else
            {
                lock (bindingLockObject)
                {
                    if (!redirectMap.ContainsKey(mi))
                        redirectMap[mi] = func;
                }
            }
            
        }

        public void RegisterCLRMethodRedirectionNeo(MethodBase mi, CLRRedirectionDelegateNeo func)
        {
            if (mi == null)
                return;

            if (!IsThreadBinding)
            {
                if (!redirectMapNeo.ContainsKey(mi))
                    redirectMapNeo[mi] = func;
            }
            else
            {
                lock (bindingLockObject)
                {
                    if (!redirectMapNeo.ContainsKey(mi))
                        redirectMapNeo[mi] = func;
                }
            }
            
        }

        public void RegisterCLRFieldGetter(FieldInfo f, CLRFieldGetterDelegate getter)
        {
            if (!IsThreadBinding)
            {
                if (!fieldGetterMap.ContainsKey(f))
                    fieldGetterMap[f] = getter;
            }
            else
            {
                lock (bindingLockObject)
                {
                    if (!fieldGetterMap.ContainsKey(f))
                        fieldGetterMap[f] = getter;
                }
            }
        }

        public void RegisterCLRFieldSetter(FieldInfo f, CLRFieldSetterDelegate setter)
        {
            if (!IsThreadBinding)
            {
                if (!fieldSetterMap.ContainsKey(f))
                    fieldSetterMap[f] = setter;
            }
            else
            {
                lock (bindingLockObject)
                {
                    if (!fieldSetterMap.ContainsKey(f))
                        fieldSetterMap[f] = setter;
                }
            }
        }

        public void RegisterCLRFieldBinding(FieldInfo f, CLRFieldBindingDelegate copyToStack, CLRFieldBindingDelegate assignFromStack)
        {
            if (!IsThreadBinding)
            {
                if (!fieldBindingMap.ContainsKey(f))
                    fieldBindingMap[f] = new KeyValuePair<CLRFieldBindingDelegate, CLRFieldBindingDelegate>(copyToStack, assignFromStack);
            }
            else
            {
                lock (bindingLockObject)
                {
                    if (!fieldBindingMap.ContainsKey(f))
                        fieldBindingMap[f] = new KeyValuePair<CLRFieldBindingDelegate, CLRFieldBindingDelegate>(copyToStack, assignFromStack);
                }
            }
        }

        public void RegisterCLRMemberwiseClone(Type t, CLRMemberwiseCloneDelegate memberwiseClone)
        {
            if (!IsThreadBinding)
            {
                if (!memberwiseCloneMap.ContainsKey(t))
                    memberwiseCloneMap[t] = memberwiseClone;
            }
            else
            {
                lock (bindingLockObject)
                {
                    if (!memberwiseCloneMap.ContainsKey(t))
                        memberwiseCloneMap[t] = memberwiseClone;
                }
            }
        }

        public void RegisterCLRCreateDefaultInstance(Type t, CLRCreateDefaultInstanceDelegate createDefaultInstance)
        {
            if (!IsThreadBinding)
            {
                if (!createDefaultInstanceMap.ContainsKey(t))
                    createDefaultInstanceMap[t] = createDefaultInstance;
            }
            else
            {
                lock (bindingLockObject)
                {
                    if (!createDefaultInstanceMap.ContainsKey(t))
                        createDefaultInstanceMap[t] = createDefaultInstance;
                }
            }
        }

        public void RegisterCLRCreateArrayInstance(Type t, CLRCreateArrayInstanceDelegate createArray)
        {
            if (!IsThreadBinding)
            {
                if (!createArrayInstanceMap.ContainsKey(t))
                    createArrayInstanceMap[t] = createArray;
            }
            else
            {
                lock (bindingLockObject)
                {
                    if (!createArrayInstanceMap.ContainsKey(t))
                        createArrayInstanceMap[t] = createArray;
                }
            }
        }

        public void RegisterValueTypeBinder(Type t, ValueTypeBinder binder)
        {
            if (!IsThreadBinding)
            {
                if (!valueTypeBinders.ContainsKey(t))
                {
                    valueTypeBinders[t] = binder;
                    binder.RegisterCLRRedirection(this);

                    var ct = GetType(t) as CLRType;
                    binder.CLRType = ct;
                }
            }
            else
            {
                lock (bindingLockObject)
                {
                    if (!valueTypeBinders.ContainsKey(t))
                    {
                        valueTypeBinders[t] = binder;
                        binder.RegisterCLRRedirection(this);

                        var ct = GetType(t) as CLRType;
                        binder.CLRType = ct;
                    }
                }
            }
            
        }

        /// <summary>
        /// 初始化注册Bindings(开启线程做binding没完成时，获取CLR重定向方法会有些消耗)
        /// </summary>
        /// <param name="isThread"></param>
        public void InitializeBindings(bool isThread = false)
        {
            if (IsBindingDone) 
                return;

            IsThreadBinding = isThread;

            if (isThread)
            {
                Thread thread = new Thread(() =>
                {
                    CLRBinding.CLRBindingUtils.Initialize(this);

                    IsBindingDone = true;   //这里线程没有竞争写

#if DEBUG && !NO_PROFILER
                    UnityEngine.Debug.Log("CLRBindingUtils.Initialize Done in thread..");
#endif
                });
                thread.Name = string.Format("CLRBindings-Thread #{0}",thread.ManagedThreadId);
                thread.Start();
            }
            else
            {
                CLRBinding.CLRBindingUtils.Initialize(this);
                IsBindingDone = true;
            }
        }

        /// <summary>
        /// 更近类型名称返回类型
        /// </summary>
        /// <param name="fullname">类型全名 命名空间.类型名</param>
        /// <returns></returns>
        public IType GetType(string fullname)
        {
            IType res;
            if (fullname == null)
            {
                return null;
            }

            if (mapType.InnerDictionary.TryGetValue(fullname, out res))
                return res;


            string baseType;
            List<string> genericParams;
            bool isArray;
            byte rank;
            ParseGenericType(fullname, out baseType, out genericParams, out isArray, out rank);

            bool isByRef = !string.IsNullOrEmpty(baseType) && baseType[baseType.Length - 1] == '&';
            if (isByRef)
                baseType = baseType.Substring(0, baseType.Length - 1);
            if (genericParams != null || isArray || isByRef)
            {
                IType bt = GetType(baseType);
                if (bt == null)
                {
                    bt = GetType(baseType.Replace("/", "+"));
                }

                if (bt == null)
                    return null;
                if (genericParams != null)
                {
                    KeyValuePair<string, IType>[] genericArguments = new KeyValuePair<string, IType>[genericParams.Count];
                    for (int i = 0; i < genericArguments.Length; i++)
                    {
                        string key = null;
                        if (bt is ILType)
                        {
                            ILType ilt = (ILType)bt;
                            key = ilt.TypeDefinition.GenericParameters[i].FullName;
                        }
                        else
                            key = "!" + i;
                        IType val = GetType(genericParams[i]);
                        if (val == null)
                            return null;
                        genericArguments[i] = new KeyValuePair<string, IType>(key, val);
                    }
                    bt = bt.MakeGenericInstance(genericArguments);
                    mapType[bt.FullName] = bt;
                    mapTypeToken[bt.GetHashCode()] = bt;
                    if (bt is CLRType)
                    {
                        clrTypeMapping[bt.TypeForCLR] = bt;

                        //It still make sense for CLRType, since CLR uses [T] for generics instead of <T>
                        StringBuilder sb = new StringBuilder();
                        sb.Append(baseType);
                        sb.Append('<');
                        for (int i = 0; i < genericParams.Count; i++)
                        {
                            if (i > 0)
                                sb.Append(",");
                            /*if (genericParams[i].Contains(","))
                                sb.Append(genericParams[i].Substring(0, genericParams[i].IndexOf(',')));
                            else*/
                            sb.Append(genericParams[i]);
                        }
                        sb.Append('>');
                        var asmName = sb.ToString();
                        if (bt.FullName != asmName)
                            mapType[asmName] = bt;
                    }
                }

                if (isArray)
                {
                    bt = bt.MakeArrayType(rank);
                    if (bt is CLRType)
                        clrTypeMapping[bt.TypeForCLR] = bt;
                    mapType[bt.FullName] = bt;
                    mapTypeToken[bt.GetHashCode()] = bt;
                    if (!isByRef)
                    {
                        mapType[fullname] = bt;
                        return bt;
                    }
                }

                if (isByRef)
                {
                    res = bt.MakeByRefType();
                    if (bt is CLRType)
                        clrTypeMapping[bt.TypeForCLR] = bt;
                    mapType[fullname] = res;
                    mapType[res.FullName] = res;
                    mapTypeToken[res.GetHashCode()] = res;
                    return res;
                }
                else
                {
                    mapType[fullname] = bt;
                    return bt;
                }
            }
            else
            {
                Type t = Type.GetType(fullname);
                if(t == null)
                {
                    foreach(var i in referenceAssemblies)
                    {
                        t = i.GetType(fullname);
                        if (t != null)
                            break;
                    }
                }
                if (t == null)
                {
                    foreach(var i in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        t = i.GetType(fullname);
                        if (t != null)
                            break;
                    }
                }
                if (t != null)
                {
                    if (!clrTypeMapping.TryGetValue(t, out res))
                    {
                        res = new CLRType(t, this);
                        clrTypeMapping[t] = res;
                    }
                    mapType[fullname] = res;
                    mapType[res.FullName] = res;
                    mapType[t.AssemblyQualifiedName] = res;
                    mapTypeToken[res.GetHashCode()] = res;
                    return res;
                }
            }
            return null;
        }

        internal static void ParseGenericType(string fullname, out string baseType, out List<string> genericParams, out bool isArray, out byte rank)
        {
            StringBuilder sb = new StringBuilder();
            int depth = 0;
            rank = 0;
            baseType = "";
            genericParams = null;

            if (fullname.Length > 2 && fullname[fullname.Length - 2] == '[' && fullname[fullname.Length - 1] == ']')
            {
                fullname = fullname.Substring(0, fullname.Length - 2);
                rank = 1;
                isArray = true;
            }
            else
                isArray = false;
            if (fullname.Length > 2 && fullname[fullname.Length - 2] == '[' && fullname[fullname.Length - 1] == ']')
            {
                baseType = fullname;
                return;
            }
            bool isGenericType = false;
            foreach (var i in fullname)
            {
                if (i == '<' || i == '[')
                {
                    isGenericType = true;
                    break;
                }
            }
            if (isGenericType)
            {
                foreach (var i in fullname)
                {
                    if (i == '<' || i == '[')
                    {
                        depth++;
                        if (depth == 1)
                        {
                            if (isArray && sb.Length == 0)
                            {
                                continue;
                            }
                            else
                            {
                                baseType = sb.ToString();
                                sb.Length = 0;
                                genericParams = new List<string>();
                                continue;
                            }
                        }
                    }
                    if (i == ',' && depth == 1)
                    {
                        string name = sb.ToString();
                        if (name.StartsWith("["))
                            name = name.Substring(1, name.Length - 2);
                        if (!string.IsNullOrEmpty(name))
                            genericParams.Add(name);
                        else
                            ++rank;
                        sb.Length = 0;
                        continue;
                    }
                    if (i == '>' || i == ']')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            string name = sb.ToString();
                            if (name.StartsWith("["))
                                name = name.Substring(1, name.Length - 2);
                            if (!string.IsNullOrEmpty(name))
                                genericParams.Add(name);
                            else if (!string.IsNullOrEmpty(baseType))
                            {
                                if (!isArray)
                                {
                                    isArray = true;
                                    ++rank;
                                }
                                else
                                {
                                    baseType += "[]";
                                }
                            }
                            else
                            {
                                sb.Append("<>");
                                continue;
                            }
                            sb.Length = 0;
                            continue;
                        }
                    }
                    sb.Append(i);
                }
                if (sb.Length > 0)
                {
                    baseType += sb.ToString();
                }
                if (genericParams != null && genericParams.Count == 0)
                    genericParams = null;
            }
            else
                baseType = fullname;
        }

        string GetAssemblyName(IMetadataScope scope)
        {
            return scope is AssemblyNameReference ? ((AssemblyNameReference)scope).FullName : null;
        }

        internal int AllocTypeIndex(IType type)
        {
            lock (typesByIndex)
            {
                int index = typesByIndex.Count;
                typesByIndex.Add(type);
                return index;
            }
        }

        internal IType GetTypeByIndex(int index)
        {
            return typesByIndex[index];
        }

        internal IType GetType(object token, IType contextType, IMethod contextMethod)
        {
            if (token is RequiredModifierType rmt)
                token = rmt.ElementType;
            int hash = token.GetHashCode();
            IType res;
            if (mapTypeToken.TryGetValue(hash, out res))
                return res;
            Mono.Cecil.ModuleDefinition module = null;
            KeyValuePair<string, IType>[] genericArguments = null;
            string typename = null;
            string scope = null;
            bool dummyGenericInstance = false;
            if (token is Mono.Cecil.TypeDefinition)
            {
                Mono.Cecil.TypeDefinition _def = (token as Mono.Cecil.TypeDefinition);
                module = _def.Module;
                typename = _def.FullName;
                scope = GetAssemblyName(_def.Scope);
            }
            else if (token is Mono.Cecil.TypeReference)
            {
                Mono.Cecil.TypeReference _ref = (token as Mono.Cecil.TypeReference);
                if (_ref.IsGenericParameter)
                {
                    IType t = null;
                    if (contextType != null)
                    {
                        t = contextType.FindGenericArgument(_ref.Name);
                    }
                    if ((t == null || t is ILGenericParameterType) && contextMethod != null && contextMethod is ILMethod)
                    {
                        t = ((ILMethod)contextMethod).FindGenericArgument(_ref.Name, false);
                    }
                    if (t != null)
                    {
                        mapTypeToken[t.GetHashCode()] = t;
                        mapType[t.FullName] = t;
                    }
                    return t;
                }
                if (_ref.IsByReference)
                {
                    var et = ((ByReferenceType)_ref).ElementType;
                    bool valid = !et.ContainsGenericParameter;
                    var t = GetType(et, contextType, contextMethod);
                    if (t != null)
                    {
                        res = t.MakeByRefType();
                        if (res is ILType && valid)
                        {
                            ///Unify the TypeReference
                            ((ILType)res).TypeReference = _ref;
                        }
                        if (valid)
                        {
                            mapTypeToken[hash] = res;
                            mapTypeToken[res.GetHashCode()] = res;
                            if (!string.IsNullOrEmpty(res.FullName))
                                mapType[res.FullName] = res;
                        }
                        return res;
                    }
                    return null;
                }
                if (_ref.IsArray)
                {
                    ArrayType at = (ArrayType)_ref;
                    var t = GetType(at.ElementType, contextType, contextMethod);
                    bool isInvalidToken = false;
                    if (t == null && at.ElementType.IsGenericParameter)
                    {
                        t = new ILGenericParameterType(at.ElementType);
                        isInvalidToken = true;
                    }
                    if (t != null)
                    {
                        res = t.MakeArrayType(at.Rank);
                        if (!_ref.ContainsGenericParameter)
                        {
                            if (res is ILType)
                            {
                                ///Unify the TypeReference
                                ((ILType)res).TypeReference = _ref;
                            }
                            mapTypeToken[hash] = res;
                        }
                        if (!isInvalidToken)
                        {
                            mapTypeToken[res.GetHashCode()] = res;

                            if (!string.IsNullOrEmpty(res.FullName))
                                mapType[res.FullName] = res;
                        }
                        return res;
                    }
                    return t;
                }
                module = _ref.Module;
                if (_ref.IsGenericInstance)
                {
                    GenericInstanceType gType = (GenericInstanceType)_ref;
                    typename = gType.ElementType.FullName;
                    scope = GetAssemblyName(gType.ElementType.Scope);
                    TypeReference tr = gType.ElementType;
                    genericArguments = new KeyValuePair<string, IType>[gType.GenericArguments.Count];
                    for (int i = 0; i < genericArguments.Length; i++)
                    {
                        string key = tr.GenericParameters[i].Name;
                        IType val;
                        if (gType.GenericArguments[i].IsGenericParameter)
                        {
                            val = contextType.FindGenericArgument(gType.GenericArguments[i].Name);
                            dummyGenericInstance = true;
                            if (val == null || val is ILGenericParameterType)
                            {
                                if (contextMethod != null && contextMethod is ILMethod)
                                {
                                    var resGA = ((ILMethod)contextMethod).FindGenericArgument(gType.GenericArguments[i].Name, false);
                                    if (resGA != null)
                                        val = resGA;
                                }
                                else if (val == null)
                                    return null;
                            }
                        }
                        else
                            val = GetType(gType.GenericArguments[i], contextType, contextMethod);
                        if (gType.GenericArguments[i].ContainsGenericParameter)
                            dummyGenericInstance = true;
                        if (val != null)
                            genericArguments[i] = new KeyValuePair<string, IType>(key, val);
                        else
                        {
                            if (!dummyGenericInstance)
                                return null;
                            genericArguments = null;
                            break;
                        }
                    }
                }
                else
                {
                    typename = _ref.FullName;
                    scope = GetAssemblyName(_ref.Scope);
                }
            }
            else
            {
                throw new NotImplementedException(
                    $"Neo GetType(token): unhandled token shape {token?.GetType().Name} [neo-bare-nie]");
            }
            res = GetType(typename);
            if (res == null)
            {
                typename = typename.Replace("/", "+");
                res = GetType(typename);
            }
            if (res == null && scope != null)
                res = GetType(typename + ", " + scope);
            if (res == null)
            {
                if (scope != null)
                {
                    string aname = scope.Split(',')[0];
                    foreach (var i in loadedAssemblies)
                    {
                        if (aname == i.GetName().Name)
                        {
                            res = GetType(typename + ", " + i.FullName);
                            if (res != null)
                                break;
                        }
                    }
                }
                if (res == null)
                {
                    foreach (var j in loadedAssemblies)
                    {
                        res = GetType(typename + ", " + j.FullName);
                        if (res != null)
                            break;
                    }
                }
                if (res != null && scope != null)
                {
                    mapType[typename + ", " + scope] = res;
                }
            }
            if (res == null)
                throw new KeyNotFoundException("Cannot find Type:" + typename);
            if (genericArguments != null)
            {
                res = res.MakeGenericInstance(genericArguments);
                if (!dummyGenericInstance && res is ILType)
                {
                    ((ILType)res).TypeReference = (TypeReference)token;
                }
                if (!string.IsNullOrEmpty(res.FullName))
                {
                    if (res is CLRType || !((ILType)res).TypeReference.HasGenericParameters)
                        mapType[res.FullName] = res;
                }
            }
            mapTypeToken[res.GetHashCode()] = res;
            if (!dummyGenericInstance)
                mapTypeToken[hash] = res;
            return res;
        }

        public IType GetType(int hash)
        {
            IType res;
            if (mapTypeToken.TryGetValue(hash, out res))
                return res;
            else
                return null;
        }

        /// <summary>
        /// 根据CLR类型获取 IL类型
        /// </summary>
        /// <param name="t"></param>
        /// <returns></returns>
        public IType GetType(Type t)
        {
            IType res;
            if (clrTypeMapping.TryGetValue(t, out res))
                return res;
            else
            {
                res = GetType(t.AssemblyQualifiedName);
                if(res == null)
                {
                    res = new CLRType(t, this);
                    clrTypeMapping[t] = res;
                    mapType[res.FullName] = res;
                    mapType[t.AssemblyQualifiedName] = res;
                    mapTypeToken[res.GetHashCode()] = res;
                }
                return res;
            }
        }

        /// <summary>
        /// Create a instance of the specified type, which is inherited from a CLR Type
        /// </summary>
        /// <typeparam name="T">CLR Type</typeparam>
        /// <param name="type">Full Name of the type</param>
        /// <param name="args">Arguments for the constructor</param>
        /// <returns></returns>
        public T Instantiate<T>(string type, object[] args = null)
        {
            ILTypeInstance ins = Instantiate(type, args);
            return (T)ins.CLRInstance;
        }

        /// <summary>
        /// Create a instance of the specified type
        /// </summary>
        /// <param name="type">Full Name of the type</param>
        /// <param name="args">Arguments for the constructor</param>
        /// <returns></returns>
        public ILTypeInstance Instantiate(string type, object[] args = null)
        {
            IType t;
            if (mapType.TryGetValue(type, out t))
            {
                ILType ilType = t as ILType;
                if (ilType != null)
                {
                    bool hasConstructor = args != null && args.Length != 0;
                    var res = ilType.Instantiate(!hasConstructor);
                    if (hasConstructor)
                    {
                        var ilm = ilType.GetConstructor(args.Length);
                        Invoke(ilm, res, args);
                    }
                    return res;
                }
            }

            return null;
        }

        /// <summary>
        /// Prewarm all methods of the specified type
        /// </summary>
        /// <param name="type"></param>
        /// <param name="recursive"></param>
        public void Prewarm(string type, bool recursive = true)
        {
            IType t = GetType(type);
            if (t == null || t is CLRType)
                return;
            ILType ilType = (ILType)t;
            foreach(var i in ilType.TypeDefinition.NestedTypes)
            {
                Prewarm(i.FullName, recursive);
            }
            var methods = t.GetMethods();
            foreach (var i in methods)
            {
                ((ILMethod)i).Prewarm(recursive);
            }
        }

        /// <summary>
        /// Prewarm all methods specified by the parameter
        /// </summary>
        /// <param name="info"></param>
        /// <param name="recursive"></param>
        public void Prewarm(PrewarmInfo[] info, bool recursive = true)
        {
            foreach(var i in info)
            {
                IType t = GetType(i.TypeName);
                if (t == null || t is CLRType || i.MethodNames == null)
                    continue;
                var methods = t.GetMethods();
                foreach (var mn in i.MethodNames)
                {
                    foreach(var j in methods)
                    {
                        ILMethod m = (ILMethod)j;
                        if(m.Name == mn && m.GenericParameterCount == 0)
                        {
                            m.Prewarm(recursive);
                        }
                    }
                }
            }
        }
        /// <summary>
        /// Invoke a method
        /// </summary>
        /// <param name="type">Type's fullname</param>
        /// <param name="method">Method name</param>
        /// <param name="p">Parameters</param>
        /// <returns></returns>
        public object Invoke(string type, string method, object instance, params object[] p)
        {
            IType t = GetType(type);
            if (t == null)
                return null;
            var m = t.GetMethod(method, p != null ? p.Length : 0);
            if (m != null)
            {
                for (int i = 0; i < m.ParameterCount; i++)
                {
                    if (p[i] == null)
                        continue;
                    if (!m.Parameters[i].TypeForCLR.IsAssignableFrom(p[i].GetType()))
                    {
                        throw new ArgumentException("Parameter type mismatch");
                    }
                }
                return Invoke(m, instance, p);
            }
            return null;
        }

        /// <summary>
        /// Invoke a generic method
        /// </summary>
        /// <param name="type">Type's fullname</param>
        /// <param name="method">Method name</param>
        /// <param name="genericArguments">Generic Arguments</param>
        /// <param name="instance">Object Instance of the method</param>
        /// <param name="p">Parameters</param>
        /// <returns></returns>
        public object InvokeGenericMethod(string type, string method, IType[] genericArguments, object instance, params object[] p)
        {
            IType t = GetType(type);
            if (t == null)
                return null;
            var m = t.GetMethod(method, p.Length);

            if (m != null)
            {
                m = m.MakeGenericMethod(genericArguments);
                return Invoke(m, instance, p);
            }
            return null;
        }

        internal ILIntepreter RequestILIntepreter()
        {
            ILIntepreter inteptreter = null;
            lock (freeIntepreters)
            {
                if (freeIntepreters.Count > 0)
                {
                    inteptreter = freeIntepreters.Dequeue();
                    //Clear debug state, because it may be in ShouldBreak State
                    inteptreter.ClearDebugState();
                }
                else
                {
                    inteptreter = new ILIntepreter(this);
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
                    intepreters[inteptreter.GetHashCode()] = inteptreter;
                    debugService.ThreadStarted(inteptreter);
#endif
                }
            }

            return inteptreter;
        }

        internal void FreeILIntepreter(ILIntepreter inteptreter)
        {
            lock (freeIntepreters)
            {
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
                if (inteptreter.CurrentStepType != StepTypes.None)
                {
                    //We should resume all other threads if we are currently doing stepping operation
                    foreach (var i in intepreters)
                    {
                        if (i.Value != inteptreter)
                        {
                            i.Value.ClearDebugState();
                            i.Value.Resume();
                        }
                    }
                    inteptreter.ClearDebugState();
                }
#endif
                inteptreter.Stack.ManagedStack.Clear();
                inteptreter.Stack.Frames.Clear();
                inteptreter.Stack.ClearAllocator();
                freeIntepreters.Enqueue(inteptreter);
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
                //debugService.ThreadEnded(inteptreter);
#endif

            }
        }

        /// <summary>
        /// Invokes a specific method
        /// </summary>
        /// <param name="m">Method</param>
        /// <param name="instance">object instance</param>
        /// <param name="p">Parameters</param>
        /// <returns></returns>
        public object Invoke(IMethod m, object instance, params object[] p)
        {
            object res = null;
            if (m is ILMethod)
            {
                ILIntepreter inteptreter = RequestILIntepreter();
                try
                {
                    res = inteptreter.Run((ILMethod)m, instance, p);
                }
                finally
                {
                    FreeILIntepreter(inteptreter);
                }
            }

            return res;
        }

        public InvocationContext BeginInvoke(IMethod m)
        {
            if (m is ILMethod)
            {
                ILIntepreter inteptreter = RequestILIntepreter();
                return new InvocationContext(inteptreter, (ILMethod)m);
            }
            else
                throw new NotSupportedException("Cannot invoke CLRMethod");
        }
        

        bool IsInvalidMethodReference(MethodReference _ref)
        {
            if ((_ref.DeclaringType.Name == "Object" || _ref.DeclaringType.Name == "Attribute")
                    && _ref.Name == ".ctor"
                    && _ref.DeclaringType.Namespace == "System"
                    && _ref.ReturnType.Name == "Void"
                    && _ref.ReturnType.Namespace == "System")
            {
                return true;
            }
            return false;
        }

        internal void CacheException(Exception ex)
        {
            mapException[ex.GetHashCode()] = ex;
        }

        internal Exception GetException(int token)
        {
            if (mapException.TryGetValue(token, out var ex))
                return ex;
            return null;
        }

        internal void CacheMethod(IMethod m)
        {
            mapMethod[m.GetHashCode()] = m;
        }

        internal void CacheType(IType type)
        {
            mapTypeToken[type.GetHashCode()] = type;
        }
        
        
        internal IMethod GetMethod(object token, ILType contextType, ILMethod contextMethod, out bool invalidToken)
        {
            string methodname = null;
            string typename = null;
            List<IType> paramList = null;
            int hashCode = token.GetHashCode();
            IMethod method;
            IType[] genericArguments = null;
            IType returnType;
            invalidToken = false;
            bool isConstructor = false;
            if (mapMethod.TryGetValue(hashCode, out method))
                return method;
            IType type = null;
            if (token is Mono.Cecil.MethodReference)
            {
                Mono.Cecil.MethodReference _ref = (token as Mono.Cecil.MethodReference);

                if(IsInvalidMethodReference(_ref))
                {
                    mapMethod[hashCode] = null;
                    return null;
                }
                
                methodname = _ref.Name;
                var typeDef = _ref.DeclaringType;
                type = GetType(typeDef, contextType, contextMethod);
                if (type == null)
                    throw new KeyNotFoundException("Cannot find type:" + typename);

                if (token is Mono.Cecil.MethodDefinition)
                {
                    var def = _ref as MethodDefinition;
                    isConstructor = def.IsConstructor;
                }
                else
                    isConstructor = methodname == ".ctor";

                if (_ref.IsGenericInstance)
                {
                    GenericInstanceMethod gim = (GenericInstanceMethod)_ref;
                    genericArguments = new IType[gim.GenericArguments.Count];
                    for (int i = 0; i < genericArguments.Length; i++)
                    {
                        if (gim.GenericArguments[i].ContainsGenericParameter)
                            invalidToken = true;
                        var gt = GetType(gim.GenericArguments[i], contextType, contextMethod);
                        if (gt == null)
                        {
                            gt = contextMethod.FindGenericArgument(gim.GenericArguments[i].Name);
                            if (gt == null)//This means it contains unresolved generic arguments, which means it's not searching the generic instance
                            {
                                genericArguments = null;
                                break;
                            }
                            else
                                genericArguments[i] = gt;
                        }
                        else
                            genericArguments[i] = gt;
                    }
                }
                if (!invalidToken && typeDef.IsGenericInstance)
                {
                    GenericInstanceType gim = (GenericInstanceType)typeDef;
                    for (int i = 0; i < gim.GenericArguments.Count; i++)
                    {
                        if (gim.GenericArguments[i].ContainsGenericParameter)
                        {
                            invalidToken = true;
                            break;
                        }
                    }
                }
                paramList = _ref.GetParamList(this, contextType, contextMethod, genericArguments);
                returnType = GetType(_ref.ReturnType, type, null);
                if (returnType == null)
                    returnType = GetType(_ref.ReturnType, contextType, null);
            }
            else
            {
                throw new NotImplementedException(
                    $"Neo GetMethod(token): unhandled method-reference shape {token?.GetType().Name} [neo-bare-nie]");
                //Mono.Cecil.GenericInstanceMethod gmethod = _def as Mono.Cecil.GenericInstanceMethod;
                //genlist = new MethodParamList(environment, gmethod);
            }

            if (isConstructor)
                method = type.GetConstructor(paramList);
            else
            {
                method = type.GetMethod(methodname, paramList, genericArguments, returnType, true);
            }

            if (method == null)
            {
                if (isConstructor && contextType.FirstCLRBaseType != null && contextType.FirstCLRBaseType is CrossBindingAdaptor && type.TypeForCLR == ((CrossBindingAdaptor)contextType.FirstCLRBaseType).BaseCLRType)
                {
                    method = contextType.BaseType.GetConstructor(paramList);
                    if (method == null)
                        throw new KeyNotFoundException(string.Format("Cannot find method:{0} in type:{1}, token={2}", methodname, type.FullName, token));
                    invalidToken = true;
                    mapMethod[method.GetHashCode()] = method;
                }
                else
                    throw new KeyNotFoundException(string.Format("Cannot find method:{0} in type:{1}, token={2}", methodname, type.FullName, token));
            }
            if (!invalidToken)
                mapMethod[hashCode] = method;
            else
                mapMethod[method.GetHashCode()] = method;
            return method;
        }

        internal IMethod GetMethod(int tokenHash)
        {
            IMethod res;
            if (mapMethod.TryGetValue(tokenHash, out res))
                return res;

            return null;
        }

        internal long GetStaticFieldIndex(object token, IType contextType, IMethod contextMethod)
        {
            FieldReference f = token as FieldReference;
            var type = GetType(f.DeclaringType, contextType, contextMethod);

            if (type is ILType)
            {
                var it = type as ILType;
                int idx = it.GetFieldIndex(token);
                long res = 0;
                if (it.TypeReference.HasGenericParameters)
                {
                    mapTypeToken[type.GetHashCode()] = it;
                }

                res = ((long)type.GetHashCode() << 32) | (uint)idx;
                return res;
            }
            else
            {
                int idx = type.GetFieldIndex(token);
                long res = ((long)type.GetHashCode() << 32) | (uint)idx;

                return res;
            }
        }

#if ENABLE_NEO_MODE
        internal ILTypeFieldOffset GetFieldOffset(object token, IType contextType, IMethod contextMethod, out IType type, out IType fieldType)
        {
            FieldReference f = token as FieldReference;
            type = GetType(f.DeclaringType, contextType, contextMethod);
            fieldType = GetType(f.FieldType, type, contextMethod);
            if (type is ILType it)
            {
                if (it.TypeReference.HasGenericParameters)
                {
                    mapTypeToken[type.GetHashCode()] = it;
                }
                return it.GetFieldOffset(token);
            }
            else
            {
                return new ILTypeFieldOffset() { PrimitiveOffset = type.GetFieldIndex(token) }; 
            }
        }

        internal int GetPrimitiveSize(IType fieldType)
        {
            if (fieldType == IntType)
            {
                return 4;
            }
            else if (fieldType == LongType)
            {
                return 8;
            }
            else if (fieldType == ShortType)
            {
                return 2;
            }
            else if (fieldType == ByteType)
            {
                return 1;
            }
            else if (fieldType == BoolType)
            {
                return 1;
            }
            else if (fieldType == FloatType)
            {
                return 4;
            }
            else if (fieldType == DoubleType)
            {
                return 8;
            }
            else if (fieldType == SByteType)
            {
                return 1;
            }
            else if (fieldType == UShortType)
            {
                return 2;
            }
            else if (fieldType == UIntType)
            {
                return 4;
            }
            else if (fieldType == ULongType)
            {
                return 8;
            }
            else if (fieldType == CharType)
            {
                return 4;
            }
            else if (fieldType == IntPtrType)
            {
                return 8;
            }
            else if (fieldType.IsValueType && fieldType.TypeForCLR != null
                     && !(fieldType is CLR.TypeSystem.ILType))
            {
                // neo-bare-nie: an enum or a CLR value type (struct) that callers
                // legitimately route into this primitive-size helper. The Neo JIT
                // call-param-slot allocator AllocateNeoCallParamSlot calls here for
                // any type whose TypeForCLR.IsEnum (enum-typed call params), and the
                // ExecuteNeo Stobj/Ldobj arms call here for a value-type token that
                // is NOT an ILType (ilType == null, a CLR struct such as TestVector3).
                // Size it via the canonical Neo managed-size helper: an enum maps to
                // its underlying primitive; a CLR struct uses Unsafe.SizeOf. This is
                // STRICTLY ADDITIVE -- the helper previously threw for ALL
                // non-primitives, so every caller reaching this branch was already
                // broken (a whole-method JIT failure or a runtime opcode throw). IL
                // value types are sized by their CALLERS (ilType.TotalPrimitiveSize)
                // before reaching this helper, so they are intentionally excluded and
                // an ILType that does reach the residual throw is a caller bug.
                return Optimizer.GetNeoValueTypeManagedSize(fieldType.TypeForCLR);
            }
            else
                throw new NotImplementedException(
                    $"Neo GetPrimitiveSize: unsupported IType '{fieldType?.FullName}' (not a primitive/enum/CLR-value-type) [neo-bare-nie]");
        }
#endif

        internal long CacheString(object token)
        {
            long oriHash = token.GetHashCode() & 0xFFFFFFFF;
            long hashCode = oriHash;
            string str = (string)token;
            lock (mapString)
            {
                bool isCollision = CheckStringCollision(hashCode, str);
                long cnt = 0;
                while (isCollision)
                {
                    cnt++;
                    hashCode = cnt << 32 | oriHash;
                    isCollision = CheckStringCollision(hashCode, str);
                }
                mapString[hashCode] = (string)token;
            }
            return hashCode;
        }

        bool CheckStringCollision(long hashCode, string newStr)
        {
            string oldVal;
            if (mapString.TryGetValue(hashCode, out oldVal))
                return oldVal != newStr;
            return false;
        }

        internal string GetString(long hashCode)
        {
            string res = null;
            if (mapString.TryGetValue(hashCode, out res))
                return res;
            return res;
        }

        public void RegisterCrossBindingAdaptor(CrossBindingAdaptor adaptor)
        {
            var bType = adaptor.BaseCLRType;

            if (bType != null)
            {
                if (!crossAdaptors.ContainsKey(bType))
                {
                    var t = adaptor.AdaptorType;
                    var res = GetType(t);
                    if (res == null)
                    {
                        res = new CLRType(t, this);
                        mapType[res.FullName] = res;
                        mapType[t.AssemblyQualifiedName] = res;
                        clrTypeMapping[t] = res;
                    }
                    adaptor.RuntimeType = res;
                    crossAdaptors[bType] = adaptor;
                }
                else
                    throw new Exception("Crossbinding Adapter for " + bType.FullName + " is already added.");
            }
            else
            {
                var bTypes = adaptor.BaseCLRTypes;
                var t = adaptor.AdaptorType;
                var res = GetType(t);
                if (res == null)
                {
                    res = new CLRType(t, this);
                    mapType[res.FullName] = res;
                    mapType[t.AssemblyQualifiedName] = res;
                    clrTypeMapping[t] = res;
                }
                adaptor.RuntimeType = res;

                foreach (var i in bTypes)
                {
                    if (!crossAdaptors.ContainsKey(i))
                    {
                        crossAdaptors[i] = adaptor;
                    }
                    else
                        throw new Exception("Crossbinding Adapter for " + i.FullName + " is already added.");
                }
            }
        }

        public void RegisterTypeStaticFieldAccessor(Type type, PatchGetFieldDelegate getter, PatchSetFieldDelegate setter)
        {
            CLRType clrType = GetType(type) as CLRType;
            clrType.GetStaticFieldCallback = getter;
            clrType.SetStaticFieldCallback = setter;
        }

        public unsafe int GetSizeInMemory(out List<TypeSizeInfo> detail)
        {
            int size = RuntimeStack.MAXIMAL_STACK_OBJECTS * sizeof(StackObject) * (intepreters.Count);
            detail = new List<TypeSizeInfo>();
            HashSet<object> traversed = new HashSet<object>();
            foreach(var i in LoadedTypes)
            {
                ILType type = i.Value as ILType;
                if(type != null)
                {
                    TypeSizeInfo info = new TypeSizeInfo();
                    info.Type = type;
                    info.StaticFieldSize = type.GetStaticFieldSizeInMemory(traversed);
                    info.MethodBodySize = type.GetMethodBodySizeInMemory();
                    info.TotalSize = info.StaticFieldSize + info.MethodBodySize;
                    size += info.TotalSize;
                    detail.Add(info);
                }
            }
            detail.Sort((a, b) => b.TotalSize - a.TotalSize);
            return size;
        }
    }
}
