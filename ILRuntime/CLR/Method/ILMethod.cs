using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;

using ILRuntime.Mono.Cecil;
using ILRuntime.Runtime;
using ILRuntime.Runtime.Intepreter.OpCodes;
using ILRuntime.Runtime.Intepreter;
using ILRuntime.Runtime.Intepreter.RegisterVM;
using ILRuntime.Runtime.Debugger;
using ILRuntime.CLR.TypeSystem;
using ILRuntime.Reflection;
using ILRuntime.Hybrid;
using System.Diagnostics;
namespace ILRuntime.CLR.Method
{
    public sealed class ILMethod : IMethod
    {
        OpCode[] body;
        OpCodeR[] bodyRegister;
        Dictionary<int, RegisterVMSymbol> registerSymbols;
        bool symbolFixed;
        MethodReference reference;
        MethodDefinition def;
        List<IType> parameters;
        ILRuntime.Runtime.Enviorment.AppDomain appdomain;
        ILType declaringType;
        ExceptionHandler[] exceptionHandler, exceptionHandlerR;
        KeyValuePair<string, IType>[] genericParameters;
        IType[] genericArguments;
        ILMethod genericDefinition;
        Dictionary<int, int[]> jumptables, jumptablesR;
        bool isDelegateInvoke;
#if ENABLE_NEO_MODE
        // Step 22: the generic-method template (cached per open definition). Null
        // until the first capture-eligible concrete instantiation populates it
        // (InitCodeBody). The BodyRegister/InitCodeBody getter of a generic-
        // instance ILMethod routes through CloneAndPatch when this is non-null on
        // its genericDefinition; the per-occurrence JIT path is the fallback. A
        // null template (not yet captured / capture deferred for struct-T-first)
        // also falls through to JIT.
        Runtime.Intepreter.RegisterVM.GenericMethodTemplate genericMethodTemplate;
        // Step 25: the AOT-init dual-path flag. Default false (every method the
        // AOT loader did not bind). When true, BodyRegister short-circuits to the
        // AOT-populated bodyRegister (InitCodeBody/JIT is SKIPPED); compiledFrame
        // was populated field-by-field from a .neo NeoMethodDefRecord by
        // InitCodeBodyFromNeo. The Cecil/JIT path is byte-identical when this is
        // false (the reference + the fallback). Neo-only.
        internal bool isNeoAotBody;
        // V5 (neo-aot-generic-cecilfree): read accessor for the S2 Cecil-free
        // generic-def matcher (a Cecil-free OWN method that S1 bound a non-generic
        // body to is NOT a generic-def template target).
        internal bool IsNeoAotBodyBound { get { return isNeoAotBody; } }
        // Step 25 S3-2 (Cecil-free load): true for an ILMethod built by
        // CreateFromNeoShell (no Cecil MethodDefinition). The Cecil-reading
        // properties (Name, HasThis, Parameters, ReturnType, SignatureString,
        // IsConstructor, IsStatic, Definition, etc.) return the recorded shell
        // data when this is set; access to a Cecil-only surface that the shell
        // does not carry throws a descriptive NotSupportedException (NEVER
        // silently null -- D5). Neo-only; the Cecil ctor path leaves this false.
        internal bool isNeoAotShell;
        // The recorded shell data (set by CreateFromNeoShell; read by the guarded
        // Cecil-reading properties). Neo-only.
        string neoShellName;
        bool neoShellHasThis, neoShellIsCtor, neoShellIsStatic, neoShellIsVirtual;
        IType neoShellReturnType;
        List<IType> neoShellParameters;
        // V5 (neo-aot-generic-cecilfree): a generic-method DEFINITION shell's
        // generic-parameter names (e.g. ["T"]), stamped by the S2 template-bind
        // loop from the .neo NeoTemplateRecord.GenericParamNames. A non-generic
        // shell leaves this null (GenericParameterCount reads 0). Neo-only.
        string[] neoShellGenericParamNames;
        // V4 (neo-debugger-aot-body): the LOCAL variable metadata deserialized
        // from the .neo LocalVariables[] table (one entry per declared local).
        // Populated by InitCodeBodyFromNeo (same closure that resolves catch
        // types). Used by the Neo debugger frame read when Definition == null
        // (a Cecil-free shell) -- the JIT path reads Definition.Body.Variables
        // instead. null on the JIT/S1-Cecil-present path (the debugger falls
        // back to Definition). Neo-only.
        IType[] neoAotLocalTypes;
        string[] neoAotLocalNames;
        // True iff neoAotLocalTypes/Names were populated (an AOT body whose .neo
        // carried LocalVariables[]). The debugger tests this to pick the source.
        internal bool HasNeoAotLocalMeta { get { return neoAotLocalTypes != null; } }

        // V4 (neo-debugger-aot-body): the debugger frame read calls these to get
        // local i's resolved type + name when Definition == null (a Cecil-free
        // shell). Bounds-safe; a null entry (an unresolved TypeRef) returns null
        // for the type (rendered as "<unknown local type>" upstream, mirroring
        // the JIT-path null-type guard). Neo-only.
        internal IType GetNeoAotLocalType(int localIndex)
        {
            if (neoAotLocalTypes == null || (uint)localIndex >= (uint)neoAotLocalTypes.Length) return null;
            return neoAotLocalTypes[localIndex];
        }
        internal string GetNeoAotLocalName(int localIndex)
        {
            if (neoAotLocalNames == null || (uint)localIndex >= (uint)neoAotLocalNames.Length) return null;
            return neoAotLocalNames[localIndex];
        }
#endif
        bool isEventAdd, isEventRemove;
        int eventFieldIndex;
        bool jitPending;
        ILRuntimeMethodInfo refletionMethodInfo;
        ILRuntimeConstructorInfo reflectionCtorInfo;
        int paramCnt, localVarCnt, stackRegisterCnt;
        int jitFlags;
        bool jitOnDemand;
        bool jitImmediately;
        int warmupCounter = 0;
        Mono.Collections.Generic.Collection<Mono.Cecil.Cil.VariableDefinition> variables;
        int hashCode = -1;
        Runtime.Intepreter.RegisterVM.CompiledFrame compiledFrame;
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
        bool isDebuggerStepThrough;
#endif
        static int instance_id = 0x10000000;
        MethodPatchContext patchCtx;

        const int JITWarmUpThreshold = 10;

        public bool Compiling { get; set; }

        public bool IsRegisterBodyReady { get { return bodyRegister != null; } }

        public MethodDefinition Definition { get { return def; } }

        internal MethodReference MethodReference { get { return reference; } }

        public Dictionary<int, int[]> JumpTables { get { return jumptables; } }
        public Dictionary<int, int[]> JumpTablesRegister { get { return jumptablesR; } }

        internal Dictionary<int, RegisterVMSymbol> RegisterVMSymbols { get { return registerSymbols; } }

        internal int JITFlags { get { return jitFlags; } }

        internal bool IsRegisterVMSymbolFixed { get { return symbolFixed; } }

        internal IDelegateAdapter DelegateAdapter { get; set; }

        internal int StartLine { get; set; }

        internal int EndLine { get; set; }

        public ILRuntime.Runtime.Enviorment.AppDomain AppDomain { get { return appdomain; } }

        public MethodInfo ReflectionMethodInfo
        {
            get
            {
                if (IsConstructor)
                    throw new NotSupportedException();
                if (refletionMethodInfo == null)
                    refletionMethodInfo = new ILRuntimeMethodInfo(this);
                return refletionMethodInfo;
            }
        }

        public ConstructorInfo ReflectionConstructorInfo
        {
            get
            {
                if (!IsConstructor)
                    throw new NotSupportedException();
                if (reflectionCtorInfo == null)
                    reflectionCtorInfo = new ILRuntimeConstructorInfo(this);
                return reflectionCtorInfo;
            }
        }

        internal ExceptionHandler[] ExceptionHandler
        {
            get
            {
                return exceptionHandler;
            }
        }

        internal ExceptionHandler[] ExceptionHandlerRegister
        {
            get
            {
                return exceptionHandlerR;
            }
        }

#if ENABLE_NEO_MODE
        // rasen neo-overhaul-eh-table-remap: build the Neo (Register) exception-
        // handler table from the front-half `addr` map. Extracted verbatim from
        // InitCodeBody so it can be invoked BEFORE the Neo back-half
        // (RunNeoBackHalf -> LowerNeoOffsets) at BOTH RunNeoBackHalf funnels
        // (JITCompiler.Compile + GenericMethodTemplate.DoCloneAndPatch).
        //
        // Why: exceptionHandlerR is otherwise NULL while LowerNeoOffsets runs --
        // InitCodeBody builds it (at the :959 site) AFTER Compile, but Compile's
        // tail is RunNeoBackHalf -> LowerNeoOffsets (the Push-deletion pass). So
        // the deletion pass could not keep the EH table consistent with the post-
        // deletion body, leaving TryStart/TryEnd/HandlerStart/HandlerEnd stale
        // and causing a thrown exception to miss its handler. Building it from
        // `addr` before the back-half lets FixBranchTargetsAfterRemove re-map the
        // four fields in lockstep with the branch targets.
        //
        // Idempotent: a no-op if exceptionHandlerR is already populated (the Neo
        // back-half path builds it first; the :959 site then skips). Methods with
        // no protected regions leave exceptionHandlerR null.
        internal void BuildExceptionHandlerRegister(Dictionary<Mono.Cecil.Cil.Instruction, int> addr)
        {
            if (exceptionHandlerR != null)
                return;
            if (def == null || !def.HasBody || def.Body.ExceptionHandlers.Count == 0)
                return;
            exceptionHandlerR = new ExceptionHandler[def.Body.ExceptionHandlers.Count];
            for (int i = 0; i < def.Body.ExceptionHandlers.Count; i++)
            {
                var eh = def.Body.ExceptionHandlers[i];
                ExceptionHandler e = new ExceptionHandler();
                e.HandlerStart = addr[eh.HandlerStart];
                e.HandlerEnd = eh.HandlerEnd != null ? addr[eh.HandlerEnd] - 1 : def.Body.Instructions.Count - 1;
                e.TryStart = addr[eh.TryStart];
                e.TryEnd = addr[eh.TryEnd] - 1;
                switch (eh.HandlerType)
                {
                    case Mono.Cecil.Cil.ExceptionHandlerType.Catch:
                        e.CatchType = appdomain.GetType(eh.CatchType, declaringType, this);
                        e.HandlerType = ExceptionHandlerType.Catch;
                        break;
                    case Mono.Cecil.Cil.ExceptionHandlerType.Finally:
                        e.HandlerType = ExceptionHandlerType.Finally;
                        break;
                    case Mono.Cecil.Cil.ExceptionHandlerType.Fault:
                        e.HandlerType = ExceptionHandlerType.Fault;
                        break;
                    default:
                        throw new NotImplementedException();
                }
                exceptionHandlerR[i] = e;
            }
        }
#endif

        public string Name
        {
            get
            {
#if ENABLE_NEO_MODE
                if (isNeoAotShell) return neoShellName;
#endif
                return def.Name;
            }
        }

        public IType DeclearingType
        {
            get
            {
                return declaringType;
            }
        }

        public bool HasThis
        {
            get
            {
#if ENABLE_NEO_MODE
                if (isNeoAotShell) return neoShellHasThis;
#endif
                return def.HasThis;
            }
        }
        public int GenericParameterCount
        {
            get
            {
                if (IsGenericInstance)
                    return 0;
#if ENABLE_NEO_MODE
                // V5 (neo-aot-generic-cecilfree): a Cecil-free generic-def shell
                // reports its arity from the .neo-stamped names (a non-generic
                // shell has null names -> 0). This is what the S2
                // MatchGenericDefinition loop (GenericParameterCount > 0) + the
                // generic dispatch read.
                if (isNeoAotShell) return neoShellGenericParamNames != null ? neoShellGenericParamNames.Length : 0;
#endif
                return def.GenericParameters.Count;
            }
        }
        public bool IsGenericInstance
        {
            get
            {
                return genericParameters != null;
            }
        }
        public Mono.Collections.Generic.Collection<Mono.Cecil.Cil.VariableDefinition> Variables
        {
            get
            {
                return variables;
            }
        }

        public KeyValuePair<string, IType>[] GenericArguments { get { return genericParameters; } }

        public IType[] GenericArugmentsArray { get { return genericArguments; } }

        public ILMethod GenericDefinition { get { return genericDefinition; } }

#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
        public bool IsDebuggerStepThrough { get { return isDebuggerStepThrough; } }
#endif
        public bool ShouldUseRegisterVM
        {
            get
            {
                if (bodyRegister != null)
                {
                    body = null;
                    exceptionHandler = null;
                    return true;
                }
                else
                {
                    if (def.HasBody && def.Body.Instructions.Count == 0)
                        return false;
                    if (jitImmediately)
                    {
                        InitCodeBody(true);
                        return true;
                    }
                    else
                    {
                        if (jitOnDemand)
                        {
                            warmupCounter++;
                            if (warmupCounter > JITWarmUpThreshold && !jitPending)
                            {
                                jitPending = true;
                                AppDomain.EnqueueJITCompileJob(this);
                            }
                        }
                        return false;
                    }
                }
            }
        }
        public ILMethod(MethodReference reference, MethodDefinition md, ILType type, ILRuntime.Runtime.Enviorment.AppDomain domain, int flags)
        {
            this.reference = reference;
            def = md;
            if (type.HasGenericParameter && !type.IsGenericInstance)
            {
                KeyValuePair<string, IType>[] gas = new KeyValuePair<string, IType>[type.TypeDefinition.GenericParameters.Count];
                for (int i = 0; i < gas.Length; i++)
                {
                    var gp = type.TypeDefinition.GenericParameters[i];
                    gas[i] = new KeyValuePair<string, IType>(gp.Name, new ILGenericParameterType(gp));
                }
                declaringType = (ILType)type.MakeGenericInstance(gas);
            }
            else
                declaringType = type;
            this.jitFlags = flags;
            if (def.ReturnType.IsGenericParameter)
            {
                ReturnType = FindGenericArgument(def.ReturnType.Name);
            }
            else
                ReturnType = domain.GetType(def.ReturnType, type, this);
            if (type.IsDelegate && def.Name == "Invoke")
                isDelegateInvoke = true;
            this.appdomain = domain;
            paramCnt = def.HasParameters ? def.Parameters.Count : 0;

            if (declaringType.IsGenericInstance)
            {
                if (reference is MethodDefinition)
                {
                    this.reference = new MethodReference(def.Name, ReturnType.ToTypeReference(appdomain.LoadedModules[0]), declaringType.TypeReference);
                    for (int i = 0; i < paramCnt; i++)
                    {
                        this.reference.Parameters.Add(new ParameterDefinition(Parameters[i].ToTypeReference(appdomain.LoadedModules[0])));
                    }
                }
            }
            if (def.HasCustomAttributes)
            {
                for (int i = 0; i < def.CustomAttributes.Count; i++)
                {
                    int f;
                    if (def.CustomAttributes[i].GetJITFlags(domain, out f))
                    {
                        this.jitFlags = f;
                        break;
                    }

#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
                    else if (def.CustomAttributes[i].AttributeType.Name == nameof(DebuggerStepThroughAttribute))
                    {
                        isDebuggerStepThrough = true;
                    }
#endif
                }
            }
            jitImmediately = (jitFlags & ILRuntimeJITFlags.JITImmediately) == ILRuntimeJITFlags.JITImmediately;
            jitOnDemand = (jitFlags & ILRuntimeJITFlags.JITOnDemand) == ILRuntimeJITFlags.JITOnDemand;
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
            if (def.HasBody && def.Body.Instructions.Count > 0)
            {
                var sp = GetValidSequence(0, 1);
                if (sp != null)
                {
                    StartLine = sp.StartLine;
                    sp = GetValidSequence(def.Body.Instructions.Count - 1, -1);
                    if (sp != null)
                    {
                        EndLine = sp.EndLine;
                    }
                }
            }
#endif
        }

        public void FixRegisterVMSymbol()
        {
            if (!symbolFixed && registerSymbols != null)
            {
                symbolFixed = true;
                JITCompiler.FixSymbol(registerSymbols);
            }
        }

        Mono.Cecil.Cil.SequencePoint GetValidSequence(int startIdx, int dir)
        {
            var seqMapping = def.DebugInformation.GetSequencePointMapping();
            var cur = DebugService.FindSequencePoint(def.Body.Instructions[startIdx], seqMapping);
            while (cur != null && cur.StartLine == 0x0feefee)
            {
                startIdx += dir;
                if (startIdx >= 0 && startIdx < def.Body.Instructions.Count)
                {
                    cur = DebugService.FindSequencePoint(def.Body.Instructions[startIdx], seqMapping);
                }
                else
                    break;
            }

            return cur;
        }

#if ENABLE_NEO_MODE
        /// <summary>
        /// Step 25 S3-2 (Cecil-free load): build a live ILMethod shell WITHOUT a
        /// Cecil MethodDefinition (def = null), for a Cecil-free ILType built
        /// from a NeoTypeDefRecord. The shell carries the Cecil-reading surface
        /// ExecuteNeo / Run / Instantiate / the VTable / interface dispatch need
        /// (Name, HasThis, Parameters, ReturnType, SignatureString, IsConstructor,
        /// IsStatic, DeclearingType, ParameterCount), set DIRECTLY from the
        /// resolved ref data. The method BODY (CompiledFrame) is bound later by
        /// NeoAssemblyLoader.Attach -> InitCodeBodyFromNeo (the S1 path, which
        /// overwrites CompiledFrame field-by-field + sets isNeoAotBody).
        ///
        /// Cecil-only surfaces the shell does NOT carry (GenericParameters,
        /// Variables, the reflection MethodInfo, the debugger sequence points)
        /// throw a descriptive NotSupportedException when accessed (NEVER silently
        /// null -- D5). The capstone probe is non-generic + host-side (no
        /// debugger), so these are unreachable. Neo-only; Legacy compiles this
        /// out. The Cecil ctor + ALL lazy inits are UNCHANGED (the shell is a
        /// SEPARATE construction path; isNeoAotShell defaults false).
        /// </summary>
        internal static ILMethod CreateFromNeoShell(string name, ILType declaringType,
            ILRuntime.Runtime.Enviorment.AppDomain domain, List<IType> parameters,
            IType returnType, bool isConstructor, bool isStatic)
        {
            var m = new ILMethod();
            m.isNeoAotShell = true;
            m.appdomain = domain;
            m.declaringType = declaringType;
            m.neoShellName = name;
            m.neoShellParameters = parameters ?? new List<IType>();
            m.paramCnt = m.neoShellParameters.Count;
            m.neoShellReturnType = returnType ?? domain.VoidType;
            m.neoShellIsCtor = isConstructor;
            m.neoShellIsStatic = isStatic;
            m.neoShellHasThis = !isStatic && !declaringType.IsInterface;
            m.neoShellIsVirtual = false;   // the capstone probe's own methods are non-virtual shells; VTable slots carry the override IMethods directly
            m.jitFlags = domain.DefaultJITFlags;
            m.jitImmediately = false;
            m.jitOnDemand = false;
            return m;
        }

        // The private parameterless ctor used only by CreateFromNeoShell (def
        // stays null; the shell data is set by the factory). Neo-only.
        ILMethod() { }
#endif

        public IType FindGenericArgument(string name, bool findDeclaringType= true)
        {
            IType res = findDeclaringType ? declaringType.FindGenericArgument(name) : null;
            if ((res == null) && genericParameters != null)
            {
                foreach (var i in genericParameters)
                {
                    if (i.Key == name)
                        return i.Value;
                }
            }
            if (res == null
#if ENABLE_NEO_MODE
                && !isNeoAotShell   // V5: a Cecil-free shell has no def.GenericParameters; the genericParameters dict (name->concrete) is the sole source
#endif
                && def.HasGenericParameters)
            {
                bool found = false;
                TypeReference pt = null;
                foreach (var j in def.GenericParameters)
                {
                    if (j.Name == name)
                    {
                        found = true;
                        pt = j;
                        break;
                    }
                }
                if (found)
                {
                    res = new ILGenericParameterType(pt);
                }
            }
            return res;
            
        }

        internal OpCode[] Body
        {
            get
            {
                if (body == null)
                    InitCodeBody(false);
                return body;
            }
        }

        internal void SetMethodPatchContext(MethodPatchContext context)
        {
            this.patchCtx = context;
        }

        internal void SetBodyAndJumptables(OpCode[] body, Dictionary<int, int[]> jumptables)
        {
            if (def.HasBody)
            {
                localVarCnt = def.Body.Variables.Count;
                variables = def.Body.Variables;
#if !DEBUG || DISABLE_ILRUNTIME_DEBUG
                def.Body = null;
#endif
            }
            this.body = body;
            this.jumptables = jumptables;
        }

        internal OpCodeR[] BodyRegister
        {
            get
            {
#if ENABLE_NEO_MODE
                // Step 25: an AOT-attached method already has bodyRegister
                // populated from the .neo -- skip InitCodeBody/JIT entirely.
                // The flag is false for every method the AOT loader did not
                // bind, so the JIT path below is byte-identical to before.
                if (isNeoAotBody)
                    return bodyRegister;
#endif
                if (bodyRegister == null)
                    InitCodeBody(true);
                return bodyRegister;
            }
        }

        public bool HasBody
        {
            get
            {
                return body != null;
            }
        }

        public int LocalVariableCount
        {
            get
            {
                return localVarCnt;
            }
        }

        public int StackRegisterCount
        {
            get
            {
                return stackRegisterCnt;
            }
        }

        internal ref readonly Runtime.Intepreter.RegisterVM.CompiledFrame CompiledFrame
        {
            get
            {
#if ENABLE_NEO_MODE
                if (compiledFrame.NeoExecuteBody == null)
#else
                if (compiledFrame.CodeBody == null)
#endif
                {
                    InitCodeBody(true);
                }
                return ref compiledFrame;
            }
        }

        public bool IsConstructor
        {
            get
            {
#if ENABLE_NEO_MODE
                if (isNeoAotShell) return neoShellIsCtor;
#endif
                return def.IsConstructor;
            }
        }

        public bool IsVirtual
        {
            get
            {
#if ENABLE_NEO_MODE
                if (isNeoAotShell) return neoShellIsVirtual;
#endif
                return def.IsVirtual;
            }
        }

        public bool IsDelegateInvoke
        {
            get
            {
                return isDelegateInvoke;
            }
        }

        public bool IsEventAdd
        {
            get
            {
                return isEventAdd;
            }
        }

        public bool IsEventRemove
        {
            get
            {
                return isEventRemove;
            }
        }

        public int EventFieldIndex
        {
            get { return eventFieldIndex; }
        }

        public bool IsStatic
        {
            get
            {
#if ENABLE_NEO_MODE
                if (isNeoAotShell) return neoShellIsStatic;
#endif
                return def.IsStatic;
            }
        }

        public int ParameterCount
        {
            get
            {
                return paramCnt;
            }
        }


        public List<IType> Parameters
        {
            get
            {
#if ENABLE_NEO_MODE
                if (isNeoAotShell) return neoShellParameters;
#endif
                if (def.HasParameters && parameters == null)
                {
                    InitParameters();
                }
                return parameters;
            }
        }

        public IType ReturnType
        {
            get
            {
#if ENABLE_NEO_MODE
                if (isNeoAotShell) return neoShellReturnType;
#endif
                return returnTypeValue;
            }
            private set { returnTypeValue = value; }
        }
        IType returnTypeValue;

        string signatureString;
        public string SignatureString
        {
            get
            {
                if (signatureString == null)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append(Name);
                    sb.Append('|');
                    sb.Append(GenericParameterCount);
                    sb.Append('(');
                    var ps = Parameters;
                    if (ps != null)
                    {
                        for (int i = 0; i < ps.Count; i++)
                        {
                            if (i > 0)
                                sb.Append(',');
                            sb.Append(ps[i] != null ? ps[i].FullName : string.Empty);
                        }
                    }
                    sb.Append(")->");
                    sb.Append(ReturnType != null ? ReturnType.FullName : string.Empty);
                    signatureString = sb.ToString();
                }
                return signatureString;
            }
        }

        public void Prewarm(bool recursive)
        {
            HashSet<ILMethod> alreadyPrewarmed = null;
            if (recursive)
            {
                alreadyPrewarmed = new HashSet<ILMethod>();
            }
            Prewarm(alreadyPrewarmed);
        }

        void PrewarmBody(HashSet<ILMethod> alreadyPrewarmed)
        {
            //当前方法用到的IType，提前InitializeMethods()。各个子调用，提前InitParameters()
            var body = Body;

            foreach (var ins in body)
            {
                switch (ins.Code)
                {
                    case OpCodeEnum.Call:
                    case OpCodeEnum.Newobj:
                    case OpCodeEnum.Ldftn:
                    case OpCodeEnum.Ldvirtftn:
                    case OpCodeEnum.Callvirt:
                        {
                            var m = appdomain.GetMethod(ins.TokenInteger);
                            if (m is ILMethod)
                            {
                                ILMethod ilm = (ILMethod)m;
                                //如果参数alreadyPrewarmed不为空，则不仅prewarm当前方法，还会递归prewarm所有子调用
                                //如果参数alreadyPrewarmed为空，则只prewarm当前方法
                                if (alreadyPrewarmed != null)
                                {
                                    ilm.Prewarm(alreadyPrewarmed);
                                }
                            }
                            else if (m is CLRMethod)
                            {
                                CLRMethod clrm = (CLRMethod)m;
                                ILRuntime.CLR.Utils.Extensions.GetTypeFlags(clrm.DeclearingType.TypeForCLR);
                            }
                        }
                        break;
                    case OpCodeEnum.Ldfld:
                    case OpCodeEnum.Stfld:
                    case OpCodeEnum.Ldflda:
                    case OpCodeEnum.Ldsfld:
                    case OpCodeEnum.Ldsflda:
                    case OpCodeEnum.Stsfld:
                    case OpCodeEnum.Ldtoken:
                        {
                            //提前InitializeBaseType()
                            var t = appdomain.GetType((int)(ins.TokenLong >> 32));
                            if (t != null)
                            {
                                var baseType = t.BaseType;
                            }
                        }
                        break;
                }
            }
        }

        void PrewarmBodyRegister(HashSet<ILMethod> alreadyPrewarmed)
        {
            //当前方法用到的IType，提前InitializeMethods()。各个子调用，提前InitParameters()
            var body = BodyRegister;

            foreach (var ins in body)
            {
                switch (ins.Code)
                {
                    case OpCodeREnum.Call:
                    case OpCodeREnum.Newobj:
                    case OpCodeREnum.Ldftn:
                    case OpCodeREnum.Ldvirtftn:
                    case OpCodeREnum.Callvirt:
                        {
                            var m = appdomain.GetMethod(ins.Operand);
                            if (m is ILMethod)
                            {
                                ILMethod ilm = (ILMethod)m;
                                //如果参数alreadyPrewarmed不为空，则不仅prewarm当前方法，还会递归prewarm所有子调用
                                //如果参数alreadyPrewarmed为空，则只prewarm当前方法
                                if (alreadyPrewarmed != null)
                                {
                                    ilm.Prewarm(alreadyPrewarmed);
                                }
                            }
                            else if (m is CLRMethod)
                            {
                                CLRMethod clrm = (CLRMethod)m;
                                ILRuntime.CLR.Utils.Extensions.GetTypeFlags(clrm.DeclearingType.TypeForCLR);
                            }
                        }
                        break;
                    case OpCodeREnum.Ldfld:
                    case OpCodeREnum.Stfld:
                    case OpCodeREnum.Ldflda:
                    case OpCodeREnum.Ldsfld:
                    case OpCodeREnum.Ldsflda:
                    case OpCodeREnum.Stsfld:
                    case OpCodeREnum.Ldtoken:
                        {
                            //提前InitializeBaseType()
                            var t = appdomain.GetType((int)(ins.OperandLong >> 32));
                            if (t != null)
                            {
                                var baseType = t.BaseType;
                            }
                        }
                        break;
                }
            }
        }
        private void Prewarm(HashSet<ILMethod> alreadyPrewarmed)
        {
            if (alreadyPrewarmed != null && alreadyPrewarmed.Add(this) == false)
                return;
            if (GenericParameterCount > 0 && !IsGenericInstance)
                return;
            //当前方法用到的CLR局部变量，提前InitializeFields()、GetTypeFlags()
            for (int i = 0; i < LocalVariableCount; i++)
            {
                var v = Variables[i];
                var vt = v.VariableType;
                IType t;
                if (vt.IsGenericParameter)
                {
                    t = FindGenericArgument(vt.Name);
                }
                else
                {
                    t = appdomain.GetType(v.VariableType, DeclearingType, this);
                }
                if (t is CLRType)
                {
                    CLRType ct = (CLRType)t;
                    var fields = ct.Fields;
                    ILRuntime.CLR.Utils.Extensions.GetTypeFlags(ct.TypeForCLR);
                }
            }
            if (jitImmediately || jitOnDemand)
                PrewarmBodyRegister(alreadyPrewarmed);
            else
                PrewarmBody(alreadyPrewarmed);
        }

        internal void InitCodeBody(bool register)
        {
#if ENABLE_NEO_MODE
            // V5 (neo-aot-generic-cecilfree): a Cecil-free generic-INSTANCE shell
            // (def == null, isNeoAotShell, IsGenericInstance) has no Cecil
            // MethodDefinition.Body. Route it through the Step-22 template path
            // (TryInstantiate -> CloneAndPatch -> RunNeoBackHalf) WITHOUT touching
            // def. The instance's genericDefinition is the Cecil-free generic-def
            // shell whose GenericMethodTemplateCache is the S2-bound .neo template.
            // A miss (no cached template, or TryInstantiate returns false) is fatal
            // here -- there is no JIT fallback for a shell (no Cecil body to JIT),
            // so throw a descriptive NIE (a generic call reaching this point means
            // the S2 bind skipped the template; the skip report records why).
            if (isNeoAotShell && IsGenericInstance && genericDefinition != null)
            {
                var template = genericDefinition.GenericMethodTemplateCache;
                if (template != null)
                {
                    var addr = new Dictionary<Mono.Cecil.Cil.Instruction, int>();
                    if (Runtime.Intepreter.RegisterVM.GenericMethodTemplateOps.TryInstantiate(
                            template, this, appdomain, declaringType, addr, ref compiledFrame))
                    {
                        bodyRegister = compiledFrame.CodeBody;
                        stackRegisterCnt = compiledFrame.StackRegisterCount;
                        jumptablesR = compiledFrame.SwitchTargets;
                        registerSymbols = compiledFrame.Symbols;
                        return;
                    }
                }
                throw new NotImplementedException("Cecil-free generic instance has no cached template (S2 bind skipped): " + Name);
            }
#endif
            if (def.HasBody)
            {
                localVarCnt = def.Body.Variables.Count;
                Dictionary<Mono.Cecil.Cil.Instruction, int> addr = new Dictionary<Mono.Cecil.Cil.Instruction, int>();

                bool noRelease = false;
                bool hasInstruction = def.Body.Instructions.Count > 0;
                if (register && hasInstruction)
                {
#if ENABLE_NEO_MODE
                    // Step 22: generic-method template path. For a generic instance:
                    //   - if the definition already has a cached template -> CloneAndPatch.
                    //   - else if THIS instantiation is capture-eligible (ref/primitive
                    //     typeArgs -> front-half is T-invariant) -> per-occurrence JIT
                    //     WITH the capture hook, then store the template on the def.
                    //   - else (struct-T first instantiation, or non-generic) -> JIT.
                    // Non-generic + first-generic-instantiation-via-JIT paths are
                    // byte-identical to before this change (the template is additive).
                    bool viaTemplate = false;
                    Runtime.Intepreter.RegisterVM.JITCompiler.TemplateCapture cap = null;
                    if (IsGenericInstance && genericDefinition != null)
                    {
                        var template = genericDefinition.GenericMethodTemplateCache;
                        if (template != null)
                        {
                            viaTemplate = Runtime.Intepreter.RegisterVM.GenericMethodTemplateOps.TryInstantiate(
                                template, this, appdomain, declaringType, addr, ref compiledFrame);
                        }
                        else if (Runtime.Intepreter.RegisterVM.GenericMethodTemplateOps.IsCaptureEligible(genericArguments))
                        {
                            cap = new Runtime.Intepreter.RegisterVM.JITCompiler.TemplateCapture();
                        }
                    }
                    if (!viaTemplate)
                    {
                        JITCompiler jit = new JITCompiler(appdomain, declaringType, this);
                        if (cap != null) jit.templateCapture = cap;
                        jit.Compile(addr, ref compiledFrame);
                        if (cap != null && cap.TemplateBody != null && genericDefinition != null)
                            genericDefinition.StoreGenericTemplate(cap, genericArguments);
                        bodyRegister = compiledFrame.CodeBody;
                        stackRegisterCnt = compiledFrame.StackRegisterCount;
                        jumptablesR = compiledFrame.SwitchTargets;
                        registerSymbols = compiledFrame.Symbols;
                    }
                    else
                    {
                        bodyRegister = compiledFrame.CodeBody;
                        stackRegisterCnt = compiledFrame.StackRegisterCount;
                        jumptablesR = compiledFrame.SwitchTargets;
                        registerSymbols = compiledFrame.Symbols;
                    }
#else
                    JITCompiler jit = new JITCompiler(appdomain, declaringType, this);
                    jit.Compile(addr, ref compiledFrame);
                    bodyRegister = compiledFrame.CodeBody;
                    stackRegisterCnt = compiledFrame.StackRegisterCount;
                    jumptablesR = compiledFrame.SwitchTargets;
                    registerSymbols = compiledFrame.Symbols;
#endif
                }
                else
                {
                    if (hasInstruction)
                        InitStackCodeBody(addr);
                    else if(declaringType.IsGenericInstance)
                    {
                        var gd = declaringType.GetGenericDefinition();
                        var mi = gd.GetMethodByGenericDefinition(this);
                        if (mi.patchCtx.IsValid)
                        {
                            mi.patchCtx.GenerateCodeBody(declaringType.TypeReference, this, appdomain);
                            return;
                        }
                        else
                            body = new OpCode[0];
                    }
                    if (jitOnDemand)
                        noRelease = bodyRegister == null;
                }
                if (def.Body.ExceptionHandlers.Count > 0)
                {
                    ExceptionHandler[] ehs;
                    bool neoEhAlreadyBuilt = false;
                    if (register)
                    {
#if ENABLE_NEO_MODE
                        // rasen neo-overhaul-eh-table-remap: idempotent. The Neo
                        // back-half (RunNeoBackHalf -> LowerNeoOffsets) already
                        // materialized exceptionHandlerR from `addr` BEFORE the
                        // Push-deletion pass, so the table is complete here and
                        // the shared fill loop below is skipped (avoids a double
                        // build / clobber). For any register path reaching here
                        // without a Neo back-half, BuildExceptionHandlerRegister
                        // builds it now (it is a no-op if already populated).
                        BuildExceptionHandlerRegister(addr);
                        ehs = exceptionHandlerR;
                        neoEhAlreadyBuilt = true;
#else
                        if (exceptionHandlerR == null)
                            exceptionHandlerR = new Method.ExceptionHandler[def.Body.ExceptionHandlers.Count];
                        ehs = exceptionHandlerR;
#endif
                    }
                    else
                    {
                        if (exceptionHandler == null)
                            exceptionHandler = new Method.ExceptionHandler[def.Body.ExceptionHandlers.Count];
                        ehs = exceptionHandler;
                    }

                    if (!neoEhAlreadyBuilt)
                    {
                        for (int i = 0; i < def.Body.ExceptionHandlers.Count; i++)
                        {
                            var eh = def.Body.ExceptionHandlers[i];
                            ExceptionHandler e = new ExceptionHandler();
                            e.HandlerStart = addr[eh.HandlerStart];
                            e.HandlerEnd = eh.HandlerEnd != null ? addr[eh.HandlerEnd] - 1 : def.Body.Instructions.Count - 1;
                            e.TryStart = addr[eh.TryStart];
                            e.TryEnd = addr[eh.TryEnd] - 1;
                            switch (eh.HandlerType)
                            {
                                case Mono.Cecil.Cil.ExceptionHandlerType.Catch:
                                    e.CatchType = appdomain.GetType(eh.CatchType, declaringType, this);
                                    e.HandlerType = ExceptionHandlerType.Catch;
                                    break;
                                case Mono.Cecil.Cil.ExceptionHandlerType.Finally:
                                    e.HandlerType = ExceptionHandlerType.Finally;
                                    break;
                                case Mono.Cecil.Cil.ExceptionHandlerType.Fault:
                                    e.HandlerType = ExceptionHandlerType.Fault;
                                    break;
                                default:
                                    throw new NotImplementedException();
                            }
                            ehs[i] = e;
                        }
                    }
                    //Mono.Cecil.Cil.ExceptionHandlerType.
                }
                variables = def.Body.Variables;
#if !DEBUG || DISABLE_ILRUNTIME_DEBUG
                //Release Method body to save memory
                if(!noRelease)
                    def.Body = null;
#endif
            }
            else
            {
                body = new OpCode[0];
                bodyRegister = new OpCodeR[0];
            }
        }
#if ENABLE_NEO_MODE
        // ===== Step 25: the AOT-init dual-path (Neo-only) =====
        //
        // Populate compiledFrame field-by-field from a deserialized .neo
        // NeoMethodDefRecord (Step 23), BYPASSING InitCodeBody / JITCompiler.
        // ExecuteNeo reads ONLY method.CompiledFrame (NeoExecuteBody + frame
        // metadata) + resolves token operands via the AppDomain hash maps -- it
        // is Cecil-free at execution time -- so once compiledFrame is populated,
        // ExecuteNeo runs the AOT body directly. The Cecil/JIT path STAYS as the
        // reference + the fallback (the isNeoAotBody flag defaults false; the
        // BodyRegister getter short-circuits only when the flag is set).
        //
        // The catch-type resolver is loader-provided (it needs the .neo model's
        // TypeRef table + the AppDomain -- both in the loader's scope, NOT on
        // ILMethod). It maps a TypeRef index -> the runtime IType for
        // CheckExceptionType matching.
        internal void InitCodeBodyFromNeo(
            ILRuntime.Runtime.NeoAOT.NeoMethodDefRecord rec,
            Func<int, IType> resolveCatchType)
        {
            int bodyLen = rec.NeoExecuteBody != null ? rec.NeoExecuteBody.Length : 0;

            // ----- frame layout: direct field copies (the record IS the frame) -----
            compiledFrame.NeoExecuteBody         = rec.NeoExecuteBody;
            compiledFrame.LocalInfos             = rec.LocalInfos;
            compiledFrame.ParamInfos             = rec.ParamInfos;
            compiledFrame.TotalStructSize        = rec.TotalStructSize;
            compiledFrame.TotalRefSize           = rec.TotalRefSize;
            compiledFrame.ParamPrimitiveSize     = rec.ParamPrimitiveSize;
            compiledFrame.ParamReferenceCount    = rec.ParamReferenceCount;
            compiledFrame.LocalsPrimitiveSize    = rec.LocalsPrimitiveSize;
            compiledFrame.LocalsReferenceCount   = rec.LocalsReferenceCount;
            compiledFrame.ReturnPrimitiveSize    = rec.ReturnPrimitiveSize;
            compiledFrame.ReturnRefCount         = rec.ReturnRefCount;
            compiledFrame.StackRegisterCount     = rec.StackRegisterCount;
            compiledFrame.LocalIsReference       = rec.LocalIsReference;
            compiledFrame.NeoCatchExceptionRegIndex   = rec.NeoCatchExceptionRegIndex;
            compiledFrame.NeoCatchExceptionByteOffset = rec.NeoCatchExceptionByteOffset;
            compiledFrame.NeoCatchExceptionRefOffset  = rec.NeoCatchExceptionRefOffset;

            // ----- SwitchTargets: Dictionary<int,int[]> from the serialized pairs -----
            compiledFrame.SwitchTargets = RebuildSwitchTargetsFromNeo(rec.SwitchTargets);

            // ----- NeoCallParams: NeoCallParamMap[] from NeoCallParamMapRecord[]
            //       (the CLR System.Type[] element types resolved back from aqnames) -----
            compiledFrame.NeoCallParams = RebuildNeoCallParamsFromNeo(rec.NeoCallParams);

            // ----- EH: rebuild Method.ExceptionHandler[] (the exceptionHandlerR
            //       field ExecuteNeo scans via GetCorrespondingExceptionHandler) -----
            exceptionHandlerR = RebuildEHFromNeo(rec.ExceptionHandlers, bodyLen, resolveCatchType);

            // ----- ILMethod-level mirrors (what InitCodeBody sets on the JIT path) -----
            bodyRegister     = rec.NeoExecuteBody;
            stackRegisterCnt = rec.StackRegisterCount;
            jumptablesR      = compiledFrame.SwitchTargets;
            // V4 (neo-debugger-aot-body): localVarCnt is the DECLARED-LOCAL count
            // (Body.Variables.Count), NOT LocalInfos.Length (which also holds params
            // + temp stack registers). The prior `= LocalInfos.Length` over-counted
            // (e.g. an instance method with 2 locals + 1 throw stack reg + `this`
            // param -> LocalInfos.Length=4 but only 2 locals), which made the Neo
            // debugger frame read over-iterate (harmless -- the extra iterations
            // threw + were swallowed -- but a latent bug). The authoritative source
            // is now rec.LocalVariables.Length (one entry per declared local). When
            // LocalVariables is absent (a pre-V4 record -- rejected by the Version
            // guard), fall back to the Cecil-derived count via the layout identity
            // LocalInfos.Length - paramCount - stackRegisterCount.
            int declaredLocalCount;
            if (rec.LocalVariables != null)
                declaredLocalCount = rec.LocalVariables.Length;
            else
            {
                int pCnt = (HasThis ? 1 : 0) + ParameterCount;
                int liLen = rec.LocalInfos != null ? rec.LocalInfos.Length : 0;
                declaredLocalCount = liLen - pCnt - rec.StackRegisterCount;
                if (declaredLocalCount < 0) declaredLocalCount = 0;
            }
            localVarCnt = declaredLocalCount;
            // V4 (neo-debugger-aot-body): resolve each local's declared TYPE (the
            // .neo LocalVariables[i].TypeRefIdx -> runtime IType, via the SAME
            // closure the catch-type resolver uses) + carry its NAME. This is what
            // the Neo debugger frame read uses for an AOT body's locals on BOTH the
            // S1 path (same-AppDomain Attach) AND the S3-2 Cecil-free shell path
            // (Definition == null). The debugger tests HasNeoAotLocalMeta (true for
            // every AOT body whose .neo carried LocalVariables[]) and reads the
            // resolved type/name from here; the JIT path (no .neo) reads Cecil
            // Definition.Body.Variables instead. A TypeRef that does not resolve ->
            // null (rendered as "<unknown local type>" upstream); a generic-
            // parameter local's TypeRef resolves via the loader's name-keyed path
            // (non-generic methods never hit this).
            if (rec.LocalVariables != null && rec.LocalVariables.Length > 0)
            {
                neoAotLocalTypes = new IType[rec.LocalVariables.Length];
                neoAotLocalNames = new string[rec.LocalVariables.Length];
                for (int i = 0; i < rec.LocalVariables.Length; i++)
                {
                    var lv = rec.LocalVariables[i];
                    neoAotLocalTypes[i] = lv.TypeRefIdx >= 0 ? resolveCatchType(lv.TypeRefIdx) : null;
                    neoAotLocalNames[i] = lv.Name;
                }
            }
            // OQ2: registerSymbols stays null -- ExecuteNeo does not read symbols and
            // the Cecil-keyed symbol map is not serialized (debugger-on-AOT deferred).

            isNeoAotBody = true;
        }

        static Dictionary<int, int[]> RebuildSwitchTargetsFromNeo(KeyValuePair<int, int[]>[] pairs)
        {
            if (pairs == null || pairs.Length == 0) return null;
            var dict = new Dictionary<int, int[]>(pairs.Length);
            for (int i = 0; i < pairs.Length; i++)
                dict[pairs[i].Key] = pairs[i].Value;
            return dict;
        }

        static NeoCallParamMap[] RebuildNeoCallParamsFromNeo(
            ILRuntime.Runtime.NeoAOT.NeoCallParamMapRecord[] recs)
        {
            if (recs == null || recs.Length == 0) return null;
            var arr = new NeoCallParamMap[recs.Length];
            for (int i = 0; i < recs.Length; i++)
            {
                var r = recs[i];
                arr[i] = new NeoCallParamMap
                {
                    PrimitiveSrc             = r.PrimitiveSrc,
                    PrimitiveDst             = r.PrimitiveDst,
                    PrimitiveSize            = r.PrimitiveSize,
                    RefSrc                   = r.RefSrc,
                    RefDst                   = r.RefDst,
                    PrimitiveByRefSrc        = r.PrimitiveByRefSrc,
                    PrimitiveByRefWriteBack  = r.PrimitiveByRefWriteBack,
                    // CLR byref element types are name-stable; re-resolve via the
                    // Step-23 helper (Type.GetType(aqname), null on miss).
                    PrimitiveByRefElemType   = ResolveElemTypesFromNeo(r.PrimitiveByRefElemTypeAqName),
                };
            }
            return arr;
        }

        static System.Type[] ResolveElemTypesFromNeo(string[] aqNames)
        {
            if (aqNames == null || aqNames.Length == 0) return null;
            var arr = new System.Type[aqNames.Length];
            for (int i = 0; i < aqNames.Length; i++)
            {
                arr[i] = ILRuntime.Runtime.NeoAOT.NeoAssemblyReader.ResolveAqName(aqNames[i]);
                // A null/empty aqname is EXPECTED (a frame-native byref / non-byref
                // slot -- see NeoCallParamMapRecord) and stays null. But a NON-EMPTY
                // aqname that fails to resolve is a real miss (a CLR byref element
                // type from an assembly not loaded in the host -- an S3 concern) --
                // fail LOUD (a silent null slot could NRE in the runtime byref
                // copy/write-back), rather than degrade. The sibling catch-type
                // resolver is tolerant (a miss just means the handler won't match);
                // the element-type slot has no safe fallback.
                if (arr[i] == null && !string.IsNullOrEmpty(aqNames[i]))
                    throw new NotImplementedException(
                        "Neo AOT NeoCallParamMap: unresolved byref element type aqname='"
                        + aqNames[i] + "' (CLR type from an unloaded assembly; S3 host-registration concern).");
            }
            return arr;
        }

        // Rebuild the runtime ExceptionHandler[] from the body-index records. The
        // record stores the RAW JIT-time addr[] indices: TryStart/HandlerStart are
        // the FIRST instruction of the region (used verbatim); TryEnd/HandlerEnd
        // are the instruction AFTER the region (EXCLUSIVE), so the runtime's
        // INCLUSIVE convention (addr <= TryEnd, see GetCorrespondingExceptionHandler)
        // needs the -1 adjustment -- EXACTLY mirroring InitCodeBody's Cecil
        // conversion (e.TryEnd = addr[eh.TryEnd] - 1). A -1 end (Cecil null --
        // the last handler in the body) -> bodyLen - 1 (InitCodeBody's fallback).
        static ExceptionHandler[] RebuildEHFromNeo(
            ILRuntime.Runtime.NeoAOT.NeoExceptionHandlerRecord[] recs,
            int bodyLen,
            Func<int, IType> resolveCatchType)
        {
            if (recs == null || recs.Length == 0) return null;
            var arr = new ExceptionHandler[recs.Length];
            for (int i = 0; i < recs.Length; i++)
            {
                var r = recs[i];
                var e = new ExceptionHandler();
                e.TryStart     = r.TryStartIdx;
                e.TryEnd       = r.TryEndIdx     >= 0 ? r.TryEndIdx - 1     : bodyLen - 1;
                e.HandlerStart = r.HandlerStartIdx;
                e.HandlerEnd   = r.HandlerEndIdx >= 0 ? r.HandlerEndIdx - 1 : bodyLen - 1;
                switch ((Mono.Cecil.Cil.ExceptionHandlerType)r.HandlerType)
                {
                    case Mono.Cecil.Cil.ExceptionHandlerType.Catch:
                        e.HandlerType = ExceptionHandlerType.Catch;
                        // OQ3: IL catch types resolve via LoadedTypes (the S1 probe
                        // uses IL catch types only); CLR catch types route through
                        // GetType(aqname) in the loader's resolver.
                        e.CatchType   = (r.CatchTypeRefIdx >= 0 && resolveCatchType != null)
                                        ? resolveCatchType(r.CatchTypeRefIdx) : null;
                        break;
                    case Mono.Cecil.Cil.ExceptionHandlerType.Finally:
                        e.HandlerType = ExceptionHandlerType.Finally;
                        break;
                    case Mono.Cecil.Cil.ExceptionHandlerType.Fault:
                        e.HandlerType = ExceptionHandlerType.Fault;
                        break;
                    default:
                        // Fail-loud to MATCH InitCodeBody (line ~828 throws NIE for
                        // the default case). An unknown/Filter HandlerType is a
                        // future-S2/S3 case (the Step-24 partition emits only
                        // Catch/Finally/Fault today); silently misclassifying it as
                        // Catch would mis-dispatch at runtime, where the JIT path
                        // throws. Be loud, not silent.
                        throw new NotImplementedException(
                            "Neo AOT EH rebuild: unsupported HandlerType=" + r.HandlerType
                            + " (Filter/unknown is not supported; InitCodeBody throws for the same case).");
                }
                arr[i] = e;
            }
            return arr;
        }
#endif

        void InitStackCodeBody(Dictionary<Mono.Cecil.Cil.Instruction, int> addr)
        {
            body = new OpCode[def.Body.Instructions.Count];
            for (int i = 0; i < body.Length; i++)
            {
                var c = def.Body.Instructions[i];
                OpCode code = new OpCode();
                code.Code = (OpCodeEnum)c.OpCode.Code;
                addr[c] = i;
                body[i] = code;
            }
            for (int i = 0; i < body.Length; i++)
            {
                var c = def.Body.Instructions[i];
                InitToken(ref body[i], c.Operand, addr);
                if (i > 0 && c.OpCode.Code == Mono.Cecil.Cil.Code.Callvirt && def.Body.Instructions[i - 1].OpCode.Code == Mono.Cecil.Cil.Code.Constrained)
                {
                    body[i - 1].TokenLong = body[i].TokenInteger;
                }
            }
        }

        unsafe void InitToken(ref OpCode code, object token, Dictionary<Mono.Cecil.Cil.Instruction, int> addr)
        {
            switch (code.Code)
            {
                case OpCodeEnum.Leave:
                case OpCodeEnum.Leave_S:
                case OpCodeEnum.Br:
                case OpCodeEnum.Br_S:
                case OpCodeEnum.Brtrue:
                case OpCodeEnum.Brtrue_S:
                case OpCodeEnum.Brfalse:
                case OpCodeEnum.Brfalse_S:
                //比较流程控制
                case OpCodeEnum.Beq:
                case OpCodeEnum.Beq_S:
                case OpCodeEnum.Bne_Un:
                case OpCodeEnum.Bne_Un_S:
                case OpCodeEnum.Bge:
                case OpCodeEnum.Bge_S:
                case OpCodeEnum.Bge_Un:
                case OpCodeEnum.Bge_Un_S:
                case OpCodeEnum.Bgt:
                case OpCodeEnum.Bgt_S:
                case OpCodeEnum.Bgt_Un:
                case OpCodeEnum.Bgt_Un_S:
                case OpCodeEnum.Ble:
                case OpCodeEnum.Ble_S:
                case OpCodeEnum.Ble_Un:
                case OpCodeEnum.Ble_Un_S:
                case OpCodeEnum.Blt:
                case OpCodeEnum.Blt_S:
                case OpCodeEnum.Blt_Un:
                case OpCodeEnum.Blt_Un_S:
                    code.TokenInteger = addr[(Mono.Cecil.Cil.Instruction)token];
                    break;
                case OpCodeEnum.Ldc_I4:
                    code.TokenInteger = (int)token;
                    break;
                case OpCodeEnum.Ldc_I4_S:
                    code.TokenInteger = (sbyte)token;
                    break;
                case OpCodeEnum.Ldc_I8:
                    code.TokenLong = (long)token;
                    break;
                case OpCodeEnum.Ldc_R4:
                    {
                        float val = (float)token;
                        code.TokenInteger = *(int*)&val;
                    }
                    break;
                case OpCodeEnum.Ldc_R8:
                    {
                        double val = (double)token;
                        code.TokenLong = *(long*)&val;
                    }
                    break;
                case OpCodeEnum.Stloc:
                case OpCodeEnum.Stloc_S:
                case OpCodeEnum.Ldloc:
                case OpCodeEnum.Ldloc_S:
                case OpCodeEnum.Ldloca:
                case OpCodeEnum.Ldloca_S:
                    {
                        Mono.Cecil.Cil.VariableDefinition vd = (Mono.Cecil.Cil.VariableDefinition)token;
                        code.TokenInteger = vd.Index;
                    }
                    break;
                case OpCodeEnum.Ldarg_S:
                case OpCodeEnum.Ldarg:
                case OpCodeEnum.Ldarga:
                case OpCodeEnum.Ldarga_S:
                case OpCodeEnum.Starg:
                case OpCodeEnum.Starg_S:
                    {
                        Mono.Cecil.ParameterDefinition vd = (Mono.Cecil.ParameterDefinition)token;
                        code.TokenInteger = vd.Index;
                        if (HasThis)
                            code.TokenInteger++;
                    }
                    break;
                case OpCodeEnum.Call:
                case OpCodeEnum.Newobj:
                case OpCodeEnum.Ldftn:
                case OpCodeEnum.Ldvirtftn:
                case OpCodeEnum.Callvirt:
                    {
                        try
                        {
                            bool invalidToken;
                            var m = appdomain.GetMethod(token, declaringType, this, out invalidToken);
                            if (m != null)
                            {
                                if (code.Code == OpCodeEnum.Callvirt && m is ILMethod)
                                {
                                    ILMethod ilm = (ILMethod)m;
                                    if (!ilm.def.IsAbstract && !ilm.def.IsVirtual && !ilm.DeclearingType.IsInterface)
                                        code.Code = OpCodeEnum.Call;
                                }
                                if (invalidToken)
                                    code.TokenInteger = m.GetHashCode();
                                else
                                    code.TokenInteger = token.GetHashCode();
                            }
                            else
                            {
                                //Cannot find method or the method is dummy
                                MethodReference _ref = (MethodReference)token;
                                int paramCnt = _ref.HasParameters ? _ref.Parameters.Count : 0;
                                if (_ref.HasThis)
                                    paramCnt++;
                                code.TokenLong = paramCnt;
                            }
                        }
                        catch(Exception e)
                        {
                            appdomain.CacheException(e);
                            MethodReference _ref = (MethodReference)token;
                            int paramCnt = _ref.HasParameters ? _ref.Parameters.Count : 0;
                            if (_ref.HasThis)
                                paramCnt++;
                            code.TokenLong = (long)paramCnt | ((long)e.GetHashCode() << 32);
                        }
                    }
                    break;
                case OpCodeEnum.Constrained:
                case OpCodeEnum.Box:
                case OpCodeEnum.Unbox_Any:
                case OpCodeEnum.Unbox:
                case OpCodeEnum.Initobj:
                case OpCodeEnum.Isinst:
                case OpCodeEnum.Newarr:
                case OpCodeEnum.Stobj:
                case OpCodeEnum.Ldobj:
                case OpCodeEnum.Castclass:
                    {
                        code.TokenInteger = GetTypeTokenHashCode(token);
                    }
                    break;
                case OpCodeEnum.Stfld:
                case OpCodeEnum.Ldfld:
                case OpCodeEnum.Ldflda:
                    {
                        code.TokenLong = appdomain.GetStaticFieldIndex(token, declaringType, this);
                    }
                    break;

                case OpCodeEnum.Stsfld:
                case OpCodeEnum.Ldsfld:
                case OpCodeEnum.Ldsflda:
                    {
                        code.TokenLong = appdomain.GetStaticFieldIndex(token, declaringType, this);
                    }
                    break;
                case OpCodeEnum.Ldstr:
                    {
                        long hashCode = appdomain.CacheString(token);
                        code.TokenLong = hashCode;
                    }
                    break;
                case OpCodeEnum.Ldtoken:
                    {
                        if (token is FieldReference)
                        {
                            code.TokenInteger = 0;
                            code.TokenLong = appdomain.GetStaticFieldIndex(token, declaringType, this);
                        }
                        else if (token is TypeReference)
                        {
                            code.TokenInteger = 1;
                            code.TokenLong = GetTypeTokenHashCode(token);
                        }
                        else
                            throw new NotImplementedException();
                    }
                    break;
                case OpCodeEnum.Switch:
                    {
                        PrepareJumpTable(token, addr);
                        code.TokenInteger = token.GetHashCode();
                    }
                    break;
            }
        }

        public void SetEventAddOrRemove(bool isEventAdd, bool isEventRemove, int fieldIdx)
        {
            this.isEventRemove = isEventRemove;
            this.isEventAdd = isEventAdd;
            eventFieldIndex = fieldIdx;
        }

        internal int GetTypeTokenHashCode(object token)
        {
            var t = appdomain.GetType(token, declaringType, this);
            bool isGenericParameter = CheckHasGenericParamter(token);
            if (t == null && isGenericParameter)
            {
                t = FindGenericArgument(((TypeReference)token).Name);
            }
            if (t != null)
            {
                if (t is ILType || isGenericParameter)
                {
                    appdomain.CacheType(t);
                    return t.GetHashCode();
                }
                else
                    return token.GetHashCode();
            }
            return 0;
        }

        bool CheckHasGenericParamter(object token)
        {
            if (token is TypeReference)
            {
                TypeReference _ref = ((TypeReference)token);
                if (_ref.IsArray)
                    return CheckHasGenericParamter(((ArrayType)_ref).ElementType);
                if (_ref.IsGenericParameter)
                    return true;
                if (_ref.IsGenericInstance)
                {
                    GenericInstanceType gi = (GenericInstanceType)_ref;
                    foreach(var i in gi.GenericArguments)
                    {
                        if (CheckHasGenericParamter(i))
                            return true;
                    }
                    return false;
                }
                else
                    return false;
            }
            else
                return false;
        }

        void PrepareJumpTable(object token, Dictionary<Mono.Cecil.Cil.Instruction, int> addr)
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
                addrs[i] = addr[e[i]];
            }

            jumptables[hashCode] = addrs;
        }

        void InitParameters()
        {
            parameters = new List<IType>();
            foreach (var i in def.Parameters)
            {
                IType type = null;
                bool isByRef = false;
                bool isArray = false;
                int rank = 1;
                TypeReference pt = i.ParameterType;
                if (pt.IsByReference)
                {
                    isByRef = true;
                    pt = ((ByReferenceType)pt).ElementType;
                }
                if (pt.IsArray)
                {
                    isArray = true;
                    rank = ((ArrayType)pt).Rank;
                    pt = ((ArrayType)pt).ElementType;
                }
                if (pt.IsGenericParameter)
                {
                    type = FindGenericArgument(pt.Name);
                    if (type == null)
                    {
                        throw new NotSupportedException("Cannot find Generic Parameter " + pt.Name + " in " + def.FullName);
                    }
                }
                else
                    type = appdomain.GetType(pt, declaringType, this);

                if (isArray)
                    type = type.MakeArrayType(rank);
                if (isByRef)
                    type = type.MakeByRefType();
                parameters.Add(type);
            }
        }

        public IMethod MakeGenericMethod(IType[] genericArguments)
        {
#if ENABLE_NEO_MODE
            // V5 (neo-aot-generic-cecilfree): the Cecil-free generic-def shell
            // path. No Cecil MethodDefinition / MethodReference is available, so
            // the Cecil ctor + GenericInstanceMethod arms below cannot run. Build
            // a generic-INSTANCE shell directly: resolve each generic arg to its
            // concrete name (from this def's .neo-stamped neoShellGenericParamNames),
            // resolve the concrete parameter types (the open def's params are
            // generic-param ILGenericParameterType; substitute the concrete arg),
            // and set genericDefinition/genericArguments/genericParameters. The
            // instance's BodyRegister getter then routes through the Step-22
            // template path (TryInstantiate -> CloneAndPatch) -- it NEVER touches
            // def (the instance is a shell, isNeoAotShell). Neo-only.
            if (isNeoAotShell)
            {
                return MakeGenericMethodShell(genericArguments);
            }
#endif
            KeyValuePair<string, IType>[] genericParameters = new KeyValuePair<string, IType>[genericArguments.Length];
            for (int i = 0; i < genericArguments.Length; i++)
            {
                string name = def.GenericParameters[i].Name;
                IType val = genericArguments[i];
                genericParameters[i] = new KeyValuePair<string, IType>(name, val);
            }

            GenericInstanceMethod gim = new GenericInstanceMethod(reference);
            foreach (var i in genericArguments)
            {
                TypeReference tRef = null;
                if (i is ILType ilType)
                {
                    tRef = ilType.TypeReference;
                }
                else
                {
                    CLRType clrType = (CLRType)i;
                    tRef = appdomain.LoadedModules[0].ImportReference(clrType.TypeForCLR);
                }
                gim.GenericArguments.Add(tRef);
            }
            ILMethod m = new ILMethod(gim, def, declaringType, appdomain, jitFlags);
            m.genericParameters = genericParameters;
            m.genericArguments = genericArguments;
            m.genericDefinition = this;
            if (m.def.ReturnType.IsGenericParameter)
            {
                m.ReturnType = m.FindGenericArgument(m.def.ReturnType.Name);
            }
            if(patchCtx.IsValid)
            {
                patchCtx.GenerateCodeBody(declaringType.TypeReference, m, appdomain);
            }
            return m;
        }

#if ENABLE_NEO_MODE
        // V5 (neo-aot-generic-cecilfree): the Cecil-free generic-instance shell
        // constructor. Builds a generic-instance ILMethod (isNeoAotShell, no def)
        // from a Cecil-free generic-DEFINITION shell + a concrete type-arg array.
        // The instance carries:
        //   - genericDefinition = this (the open def shell, whose
        //     GenericMethodTemplateCache is the S2-bound .neo template),
        //   - genericArguments = the concrete args,
        //   - genericParameters = (name, concrete) pairs (name from this def's
        //     neoShellGenericParamNames),
        //   - neoShellParameters = the concrete parameter types (the open def's
        //     params substituted with the concrete arg -- the back-half + the
        //     Call arg-copy read the concrete param types),
        //   - neoShellReturnType = the concrete return type (substituted).
        // The instance's BodyRegister getter routes through TryInstantiate ->
        // CloneAndPatch -> RunNeoBackHalf (which reads template.VariableTypes +
        // the shell params, NEVER def). Neo-only.
        ILMethod MakeGenericMethodShell(IType[] genericArguments)
        {
            int arity = neoShellGenericParamNames != null ? neoShellGenericParamNames.Length : 0;
            var gps = new KeyValuePair<string, IType>[genericArguments.Length];
            for (int i = 0; i < genericArguments.Length; i++)
            {
                string name = i < arity ? neoShellGenericParamNames[i] : ("T" + i);
                gps[i] = new KeyValuePair<string, IType>(name, genericArguments[i]);
            }
            var m = new ILMethod();
            m.isNeoAotShell = true;
            m.appdomain = appdomain;
            m.declaringType = declaringType;
            m.neoShellName = neoShellName;
            m.neoShellIsCtor = neoShellIsCtor;
            m.neoShellIsStatic = neoShellIsStatic;
            m.neoShellHasThis = neoShellHasThis;
            m.neoShellIsVirtual = neoShellIsVirtual;
            m.neoShellGenericParamNames = neoShellGenericParamNames;  // the instance reports the SAME arity (IsGenericInstance short-circuits GenericParameterCount to 0 anyway)
            m.jitFlags = jitFlags;
            m.jitImmediately = false;
            m.jitOnDemand = false;
            // Substitute the open def's generic-param params/return with the concrete arg.
            var openParams = neoShellParameters ?? new List<IType>();
            var subParams = new List<IType>(openParams.Count);
            for (int i = 0; i < openParams.Count; i++)
                subParams.Add(SubstituteGenericParam(openParams[i], gps));
            m.neoShellParameters = subParams;
            m.paramCnt = subParams.Count;
            m.neoShellReturnType = SubstituteGenericParam(neoShellReturnType ?? appdomain.VoidType, gps);
            m.genericParameters = gps;
            m.genericArguments = genericArguments;
            m.genericDefinition = this;
            return m;
        }

        // V5: substitute a generic-parameter IType with its concrete binding from
        // the (name, concrete) pairs. A non-generic-param type passes through. An
        // ILGenericParameterType whose Name matches a pair is replaced by the pair's
        // concrete type; arrays/byref of a generic param are rebuilt on the concrete
        // element. Mirrors the Cecil-path generic-arg substitution semantics.
        static IType SubstituteGenericParam(IType t, KeyValuePair<string, IType>[] gps)
        {
            if (t == null) return null;
            if (t is ILGenericParameterType gpt)
            {
                for (int i = 0; i < gps.Length; i++)
                    if (gps[i].Key == gpt.Name) return gps[i].Value;
                return t;
            }
            if (t.IsArray && t.ElementType != null)
            {
                var et = SubstituteGenericParam(t.ElementType, gps);
                return et != t.ElementType ? et.MakeArrayType(t.ArrayRank) : t;
            }
            if (t.IsByRef && t.ElementType != null)
            {
                var et = SubstituteGenericParam(t.ElementType, gps);
                return et != t.ElementType ? et.MakeByRefType() : t;
            }
            return t;
        }

        // V5 (neo-aot-generic-cecilfree): stamp the .neo-stamped generic-param
        // names onto a Cecil-free generic-def shell (called by the S2 bind loop
        // BEFORE MatchGenericDefinition so GenericParameterCount > 0). Neo-only.
        internal void SetNeoShellGenericParamNames(string[] names)
        {
            if (!isNeoAotShell) return;
            neoShellGenericParamNames = names;
        }
        // V5 read accessor (the S2 VariableType re-resolution reads the names to
        // synthesize a Cecil GenericParameter for a generic-param local on a shell).
        internal string[] NeoShellGenericParamNames { get { return neoShellGenericParamNames; } }
        // V5: set a Cecil-free generic-def shell's return type (from the .neo
        // template's ReturnTypeRefIdx). Called by the S2 bind loop. Neo-only.
        internal void SetNeoShellReturnType(IType retType)
        {
            if (!isNeoAotShell) return;
            neoShellReturnType = retType ?? appdomain.VoidType;
        }
#endif
#if ENABLE_NEO_MODE
        // Step 22: the cached template for THIS open generic definition (null until
        // the first capture-eligible concrete instantiation populates it). Not built
        // by compiling the open definition (that corrupts shared caches); captured
        // from a concrete instance's front-half in InitCodeBody.
        internal Runtime.Intepreter.RegisterVM.GenericMethodTemplate GenericMethodTemplateCache
        {
            get { return IsGenericInstance ? null : genericMethodTemplate; }
        }

        internal void StoreGenericTemplate(Runtime.Intepreter.RegisterVM.JITCompiler.TemplateCapture cap, IType[] captureTypeArgs)
        {
            if (IsGenericInstance) return;  // only the definition caches
            if (genericMethodTemplate != null) return;  // already cached
            try
            {
                genericMethodTemplate = Runtime.Intepreter.RegisterVM.GenericMethodTemplateOps.StoreFromCapture(this, cap, captureTypeArgs);
            }
            catch (Exception)
            {
                genericMethodTemplate = null;
            }
        }

        // Step 25 S2: install a GenericMethodTemplate reconstructed from a deserialized
        // .neo TemplateTable record, OVERWRITING any previously-cached (JIT-captured)
        // template. Distinct from StoreGenericTemplate: the S2 loader runs AFTER the
        // V2 capstone's compile step (which already cached a JIT-captured template via
        // NeoCompiler.CaptureTemplate -> the capture hook), so the loader's AOT template
        // MUST replace it for a subsequent generic call to route through CloneAndPatch
        // against the AOT template body. The overwrite is intentional + Neo-only;
        // StoreGenericTemplate's guard stays intact for the normal JIT capture path.
        internal void InitTemplateFromNeo(Runtime.Intepreter.RegisterVM.GenericMethodTemplate template)
        {
            if (IsGenericInstance) return;  // only the definition caches
            genericMethodTemplate = template;
        }
#endif

        string cachedName;
        public override string ToString()
        {
            if (cachedName == null)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(declaringType.FullName);
                sb.Append('.');
                sb.Append(Name);
                sb.Append('(');
                bool isFirst = true;
#if ENABLE_NEO_MODE
                // Step 25 S3-4: a Cecil-free shell (isNeoAotShell) has NO Cecil
                // MethodDefinition -> InitParameters()/def.Parameters NRE. Use the
                // shell's neoShellParameters (the Parameters property's source).
                // Pre-S3-4 a Cecil-free method whose body threw -> the ExecuteNeo
                // catch wrapped it in ILRuntimeException -> GetStackTrace ->
                // ToString -> InitParameters -> NRE, masking the real inner
                // exception. The guard surfaces the real exception (a Cecil-
                // property guard per the S3-2 D5 discipline).
                if (isNeoAotShell)
                {
                    var sp = neoShellParameters;
                    if (sp != null)
                    {
                        for (int i = 0; i < sp.Count; i++)
                        {
                            if (isFirst) isFirst = false;
                            else sb.Append(", ");
                            sb.Append(sp[i].FullName);
                        }
                    }
                }
                else
                {
#endif
                    if (parameters == null)
                        InitParameters();
                    for (int i = 0; i < parameters.Count; i++)
                    {
                        if (isFirst)
                            isFirst = false;
                        else
                            sb.Append(", ");
                        sb.Append(parameters[i].FullName);
                        sb.Append(' ');
                        sb.Append(def.Parameters[i].Name);
                    }
#if ENABLE_NEO_MODE
                }
#endif
                sb.Append(')');
                cachedName = sb.ToString();
            }
            return cachedName;
        }

        public override int GetHashCode()
        {
            if (hashCode == -1)
                hashCode = System.Threading.Interlocked.Add(ref instance_id, 1);
            return hashCode;
        }


        bool? isExtend;
        public bool IsExtend
        {
            get
            {
                if (isExtend == null)
                {
                    isExtend = this.IsExtendMethod();
                }
                return isExtend.Value;
            }
        }
    }
}
