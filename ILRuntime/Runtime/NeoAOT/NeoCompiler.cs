#if ENABLE_NEO_MODE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;
using ILRuntime.Mono.Cecil;
using ILRuntime.Runtime.Intepreter.RegisterVM;

namespace ILRuntime.Runtime.NeoAOT
{
    // ===== Step 24: the ilrt_neoc bulk-compile driver =====
    //
    // The SINGLE public seam that drives the Step-22 template + Step-23 serializer
    // layers end-to-end. Every NeoAOT type (NeoAssemblyWriter/Reader/Model) and the
    // force-compile / template entries (ILMethod.BodyRegister,
    // ILMethod.GenericMethodTemplateCache) are internal with NO InternalsVisibleTo,
    // so a separate-assembly CLI cannot reach them. This class lives INSIDE the
    // ILRuntime assembly (gated #if ENABLE_NEO_MODE), reaches every internal it
    // needs, and exposes one public Compile(...) surface. The ilrt_neoc CLI project
    // is a thin wrapper over this class (arg parse + streams + report + exit code).
    //
    // ADDITIVE: a new caller of existing, unchanged internals. It does NOT modify
    // the JIT, the runtime, ExecuteNeo, the optimizer, the Step-22 template
    // mechanism, or the Step-23 serializer (NeoAssemblyWriter.Write is called
    // as-is). Legacy compiles this whole file out.

    /// <summary>
    /// Bulk-compiles an IL assembly (or an explicit type set) to a .neo stream:
    /// enumerate every IL type/method in the input set, partition non-generic
    /// methods (emitted to the MethodDefTable via NeoAssemblyWriter.Write) from
    /// generic-method definitions (a template is captured for each via a
    /// synthesized capture-eligible instantiation, then emitted to the
    /// TemplateTable), force-compile each non-generic method inside a per-method
    /// try/catch (a compile failure is recorded as a skip and the method is
    /// omitted -- the .neo is still written for the survivor subset), and finally
    /// serialize via NeoAssemblyWriter.Write. Per-method compile failures do NOT
    /// abort the run.
    /// </summary>
    public sealed class NeoCompiler
    {
        /// <summary>
        /// CLI entry: Cecil-load the input assembly (+ reference assemblies for
        /// type resolution) from disk into a FRESH AppDomain, enumerate every IL
        /// type in the INPUT module (ref-assembly types are compiled-against but
        /// filtered out of the emitted .neo by the module filter), and compile.
        /// Wrap input-load / serializer fatals and rethrow as a fatal exception
        /// (the CLI maps any thrown exception to exit code 1).
        /// </summary>
        public NeoCompilerResult Compile(string inputAssemblyPath,
            IReadOnlyList<string> referenceAssemblyPaths, Stream outputStream)
        {
            if (string.IsNullOrEmpty(inputAssemblyPath))
                throw new NeoCompilerFatal("input assembly path is empty");
            if (!File.Exists(inputAssemblyPath))
                throw new NeoCompilerFatal("input assembly not found: " + inputAssemblyPath);
            if (outputStream == null)
                throw new NeoCompilerFatal("output stream is null");

            // The AppDomain starts a foreground JIT-worker thread in its field
            // initializer (AsyncJITCompileWorker); it MUST be disposed so the worker
            // exits, otherwise the process never terminates (the CLI would hang after
            // the compile). Disposed in the finally below (mirrors the test CLI's
            // session.Dispose). The host-side Compile(IReadOnlyList<ILType>, Stream)
            // overload does NOT create an AppDomain -- it reuses the caller's -- so it
            // must NOT dispose it (the caller owns the lifecycle).
            var appdomain = new ILRuntime.Runtime.Enviorment.AppDomain();
            try
            {
                ModuleDefinition inputModule;

                // Build a Cecil resolver with the input dir + every existing ref dir
                // on the search path, so Cecil can resolve the module's own
                // AssemblyReferences when reading. The JIT itself resolves TypeRefs /
                // MethodRefs via appdomain.GetType(...) (the AppDomain), which falls
                // back to the CLR for BCL references; IL ref assemblies are
                // LoadAssembly-ed below so their types enter LoadedTypes.
                var resolver = new DefaultAssemblyResolver();
                try
                {
                    string inputDir = Path.GetDirectoryName(Path.GetFullPath(inputAssemblyPath));
                    if (!string.IsNullOrEmpty(inputDir)) resolver.AddSearchDirectory(inputDir);
                    if (referenceAssemblyPaths != null)
                    {
                        foreach (var rp in referenceAssemblyPaths)
                        {
                            if (string.IsNullOrEmpty(rp) || !File.Exists(rp)) continue;
                            string rd = Path.GetDirectoryName(Path.GetFullPath(rp));
                            if (!string.IsNullOrEmpty(rd)) resolver.AddSearchDirectory(rd);
                        }
                    }
                }
                catch { /* resolver search-dir setup is best-effort */ }

                // Read the input module WITH the resolver (so its AssemblyReferences
                // resolve), then register it with the AppDomain. We must hand the
                // SAME module instance to the AppDomain (LoadAssembly(Stream) would
                // re-read without the resolver), so the module is read here and
                // InitializeFromModule is driven directly.
                try
                {
                    var param = new ReaderParameters { AssemblyResolver = resolver };
                    inputModule = ModuleDefinition.ReadModule(inputAssemblyPath, param);
                    appdomain.InitializeFromModule(inputModule);
                }
                catch (Exception ex)
                {
                    throw new NeoCompilerFatal("failed to load input assembly: " + ex.Message, ex);
                }

                // Register each reference assembly with the HOST CLR so the
                // AppDomain's CLR-type fallback (GetType(string) -> the live
                // System.AppDomain.CurrentDomain.GetAssemblies() scan) resolves the
                // ref's types as CLRType. This mirrors the in-process runtime model,
                // where a host CLR assembly referenced by the IL (e.g. one defining a
                // CLR enum like ILRuntimeTest.TestFramework.TestCLREnum) is resident
                // in the host System.AppDomain and is found by the same fallback
                // WITHOUT any explicit registration call. WITHOUT this, a Cecil
                // TypeReference to a host CLR type fatal-aborts at AppDomain.cs:1409
                // (the Step-24 TestCLREnum gap).
                //
                // We MUST NOT appdomain.LoadAssembly(refStream) here: that loads the
                // ref as IL and wraps its types as ILType in mapType, which
                // GetType(string) returns BEFORE the CLR fallback -- shadowing the
                // real CLR type and producing a downstream NullReferenceException
                // (reproduced on HEAD: the naive LoadAssembly-the-ref path gets past
                // every TestCLREnum reference but NREs later). A ref is registered by
                // exactly ONE path: the CLR side (Assembly.LoadFrom), never the IL
                // side. (Multi-hotfix-assembly cross-refs -- where a ref's types
                // SHOULD be ILType -- are a separate, already-deferred V1 scenario.)
                //
                // Best-effort: a ref already loaded / ref-only metadata / a missing
                // file throws (FileLoadException / FileNotFoundException) -- caught,
                // and resolution falls back to the existing CLR/BCL scan + the Cecil
                // resolver set up above. A failed LoadFrom never fatal-aborts.
                if (referenceAssemblyPaths != null)
                {
                    foreach (var rp in referenceAssemblyPaths)
                    {
                        if (string.IsNullOrEmpty(rp) || !File.Exists(rp)) continue;
                        try
                        {
                            System.Reflection.Assembly.LoadFrom(rp);
                        }
                        catch (Exception)
                        {
                            // BCL / already-loaded / ref-only-metadata / unresolvable
                            // ref -- best-effort skip; resolution falls back to the
                            // existing CLR/BCL scan.
                        }
                    }
                }

                // Filter LoadedTypes to the INPUT module (ref-assembly types compiled-
                // against but NOT emitted), then drive the shared core.
                var inputTypes = appdomain.LoadedTypes.Values
                    .OfType<ILType>()
                    .Where(it => it.TypeDefinition.Module == inputModule)
                    .ToArray();

                var result = new NeoCompilerResult();
                try
                {
                    CompileCore(appdomain, inputTypes, result, outputStream);
                }
                catch (Exception ex)
                {
                    throw new NeoCompilerFatal("serializer failure: " + ex.Message, ex);
                }
                return result;
            }
            finally
            {
                appdomain.Dispose();
            }
        }

        /// <summary>
        /// Host-side entry (V1-A self-check): compile an EXPLICIT type set from an
        /// already-loaded AppDomain (no re-load). Reuses the SAME core as the CLI
        /// entry -- this is the "the same NeoCompiler driver the CLI uses" path,
        /// scoped to a small probe type to keep the self-check sub-second. The
        /// generic/non-generic split + template capture + force-compile + serialize
        /// wiring is identical to the CLI path.
        /// </summary>
        public NeoCompilerResult Compile(IReadOnlyList<ILType> inputTypes, Stream outputStream)
        {
            if (outputStream == null)
                throw new NeoCompilerFatal("output stream is null");
            var result = new NeoCompilerResult();
            // inputTypes may belong to different AppDomains in theory; use the
            // first type's AppDomain for IntType (capture-eligibility synthesis).
            ILRuntime.Runtime.Enviorment.AppDomain appdomain = null;
            var types = inputTypes != null ? inputTypes.ToArray() : Array.Empty<ILType>();
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] != null) { appdomain = types[i].AppDomain; break; }
            }
            CompileCore(appdomain, types, result, outputStream);
            return result;
        }

        // ===== the shared core: partition + capture + force-compile + Write =====
        //
        // inputTypes is ALREADY module-filtered by the callers. This enumerates
        // each type's GetMethods() + GetConstructors(), partitions generic-method
        // DEFINITIONS (-> template capture, NEVER methods[]) from non-generic
        // methods (-> methods[], each force-compiled with a per-method try/catch
        // for the skip set), then hands the survivor set to
        // NeoAssemblyWriter.Write (which CompileFresh-compiles each method again
        // -- deterministic, as Step-23's self-check already relies on). Write is
        // called UNCHANGED (no Step-23 modification -- D6-preferred).
        void CompileCore(ILRuntime.Runtime.Enviorment.AppDomain appdomain,
            ILType[] inputTypes, NeoCompilerResult result, Stream outputStream)
        {
            var methods = new List<ILMethod>();
            var templates = new List<GenericMethodTemplate>();

            // ===== Per-TYPE pre-filter (Step 25 CLR-adaptor TRUE COMPLETION):
            // eagerly trigger the lazy CLR-base/interface adaptor resolution
            // BEFORE the per-method compile loop AND BEFORE serialization. An IL
            // type whose CLR base or CLR interface needs a CrossBindingAdaptor
            // that is NOT registered in the compile AppDomain throws
            // TypeLoadException here -- the throw sites at ILType.cs:1505 (CLR
            // interface), :1568 (generic-instance CLR base), :1593 (non-generic
            // CLR base). Catching it at the TYPE level lets us drop the WHOLE
            // type cleanly: the type is excluded from BOTH the per-method loop
            // AND NeoAssemblyWriter.Write (whose TypeDef emission at
            // NeoAssemblyWriter.cs:755/:762 also touches BaseType and would re-
            // fire the lazy resolution). The ILType init is MEMOIZED
            // (baseTypeInitialized / interfaceInitialized), so a survivor's later
            // BaseType access does NOT re-throw. Only TypeLoadException is caught
            // (the adaptor-absence exception); any OTHER init failure stays a
            // loud fatal (it propagates to the NeoCompilerFatal wrapper -> exit
            // 1), not a silent skip. Types whose CLR base resolves via a BUILT-IN
            // adaptor (ExceptionAdaptor / AttributeAdapter, registered by the
            // AppDomain ctor) are found here and SUCCEED -- they are NOT skipped.
            // (Design D1/D2.) =====
            var compilableTypes = new List<ILType>();
            foreach (var type in inputTypes)
            {
                if (type == null) continue;
                try
                {
                    _ = type.FirstCLRBaseType;   // triggers InitializeBaseType (sites 1568/1593)
                    _ = type.FirstCLRInterface;  // triggers InitializeInterfaces (site 1505;
                                                 // also reads BaseType internally at :1510)
                    // A2 field-init-NRE closure: also trigger FIELD init now. A
                    // type whose field type fails to resolve (e.g. a compiler-
                    // generated anonymous OPEN generic definition like
                    // <>f__AnonymousType0`2<j,k>, whose generic-parameter field
                    // yields a null fieldType via FindGenericArgument) throws
                    // TypeLoadException in ILType.InitializeFields (the Neo-gated
                    // null-fieldType guard at ILType.cs, mirroring the adaptor
                    // sites). Triggering it HERE -- via TotalPrimitiveSize, whose
                    // getter calls InitializeFields when fieldMapping==null --
                    // fires the TLE inside this try, so the existing
                    // TypeLoadException catch skips the type cleanly instead of
                    // NRE-ing later during NeoAssemblyWriter.BuildTypeDef
                    // (type.TotalPrimitiveSize at NeoAssemblyWriter.cs:756).
                    _ = type.TotalPrimitiveSize;  // triggers InitializeFields (field-type resolution)
                    compilableTypes.Add(type);
                }
                catch (TypeLoadException ex)
                {
                    result.Skipped.Add(MakeTypeSkip(type, ex));   // harness-adaptor skip
                }
            }

            // compilableTypes (the survivors) replaces inputTypes for BOTH the
            // per-method loop AND Write. A skipped type's methods are never
            // enumerated, never force-compiled, and never serialized.
            foreach (var type in compilableTypes)
            {
                if (type == null) continue;
                // GetMethods(): all non-ctor methods. GetConstructors(): INSTANCE
                // ctors ONLY (a static .cctor routes to the SEPARATE staticConstructor
                // field in InitializeMethods, NOT the constructors list -- see
                // ILType.cs:2101-2108). Step 25 S3-4: ALSO compile the .cctor (via
                // GetStaticConstroctor) so its body lands in the .neo MethodDef table
                // + is force-compiled like any other non-generic method. Pre-S3-4 the
                // .cctor body was NEVER compiled (GetConstructors() did not include
                // it), so StaticCtorMethodRefIdx was -1 + the seed had nothing to run.
                var typeMethods = new List<ILMethod>();
                if (type.GetMethods() != null)
                {
                    foreach (var m in type.GetMethods())
                    {
                        var ilm = m as ILMethod;
                        if (ilm != null) typeMethods.Add(ilm);
                    }
                }
                if (type.GetConstructors() != null) typeMethods.AddRange(type.GetConstructors());
                {
                    // The static .cctor (if any). Cast to ILMethod; a CLR type's .cctor
                    // (a CLRMethod) is out of scope for a .neo MethodDef (IL only).
                    var cctor = type.GetStaticConstroctor() as ILMethod;
                    if (cctor != null) typeMethods.Add(cctor);
                }

                foreach (var ilm in typeMethods)
                {
                    // Generic INSTANCES are runtime artifacts (created on demand);
                    // they are neither definitions to template nor methods to emit.
                    if (ilm.IsGenericInstance) continue;

                    // A method with no Cecil body -- a delegate's Invoke/
                    // BeginInvoke/EndInvoke (runtime-implemented), an abstract/
                    // extern/PInvoke method -- has NO IL to AOT-compile.
                    // ILMethod.InitCodeBody guards `if (def.HasBody)`, so for
                    // these BodyRegister returns null WITHOUT throwing, the
                    // per-method force-compile below silently admits them to
                    // methods[], and NeoAssemblyWriter.CompileFresh's JIT later
                    // NREs on the null body (JITCompiler.Compile :360). Omit them
                    // here (mirrors the IsGenericInstance silent skip): the .neo
                    // loader's additive JIT fallback handles them at load time.
                    // (A2-extension: null-body-method CompileFresh NRE. This is a
                    // pre-compile filter, NOT a broadened catch -- a method WITH a
                    // body whose JIT NREs still throws in the force-compile try
                    // below and is recorded as a skip; a genuine CompileFresh bug
                    // on a body-bearing method still stays a loud fatal.)
                    if (ilm.Definition != null && !ilm.Definition.HasBody) continue;

                    if (ilm.GenericParameterCount > 0)
                    {
                        // Generic DEFINITION -> template capture (D4). NEVER methods[]
                        // (Write's CompileFresh on an open definition is non-determin-
                        // istic -- the Step-22 cache-corruption quirk).
                        var tpl = CaptureTemplate(appdomain, ilm, result);
                        if (tpl != null) templates.Add(tpl);
                        // (capture failure already recorded as a skip)
                    }
                    else
                    {
                        // Non-generic -> force-compile with a per-method try/catch
                        // (D5/D6-preferred). Survivors go to methods[]; a compile
                        // throw is recorded as a skip and the method is omitted.
                        try
                        {
                            _ = ilm.BodyRegister;   // triggers InitCodeBody -> JIT compile
                            methods.Add(ilm);
                        }
                        catch (Exception ex)
                        {
                            result.Skipped.Add(MakeSkip(ilm, ex));
                        }
                    }
                }
            }

            // Serialize. NeoAssemblyWriter.Write calls CompileFresh per method
            // internally (the per-occurrence Neo JIT). Every method here already
            // force-compiled cleanly above, so Write's CompileFresh re-compiles
            // deterministically (Step-23 self-check relies on the same determinism).
            var writer = new NeoAssemblyWriter();
            writer.Write(compilableTypes.ToArray(), methods.ToArray(), templates.ToArray(), outputStream);

            // TypesCompiled is the EMITTED count (the survivor subset), consistent
            // with MethodsCompiled / TemplatesCaptured being emitted counts. A
            // harness-adaptor-requiring type that was pre-filtered out is NOT
            // counted here -- it is in result.Skipped (drives exit 2).
            result.TypesCompiled = compilableTypes.Count;
            result.MethodsCompiled = methods.Count;
            result.TemplatesCaptured = templates.Count;
        }

        // ===== Template capture via synthesized capture-eligible instantiation (D4) =====
        //
        // The production (non-DEBUG) equivalent of GenericMethodTemplateOps.
        // ForceBuildTemplate: synthesize one primitive (AppDomain.IntType) per
        // generic parameter via definition.MakeGenericMethod(IType[]) (public),
        // then read the instance's BodyRegister to trigger InitCodeBody's capture
        // hook -> StoreGenericTemplate caches on definition.GenericMethodTemplateCache.
        // int-per-param is universally capture-eligible (IsCaptureEligible only
        // rejects non-primitive value types; struct-T is a CloneAndPatch-time
        // concern, not a capture-time concern). Reaches the internal BodyRegister
        // + GenericMethodTemplateCache directly (in-assembly) -- does NOT call the
        // DEBUG-gated ForceBuildTemplate, so it works in Release_Neo too. No
        // Step-22 code change.
        static GenericMethodTemplate CaptureTemplate(ILRuntime.Runtime.Enviorment.AppDomain appdomain,
            ILMethod definition, NeoCompilerResult result)
        {
            try
            {
                if (definition.GenericMethodTemplateCache != null)
                    return definition.GenericMethodTemplateCache;

                int n = definition.GenericParameterCount;
                var captureArgs = new IType[n];
                for (int i = 0; i < n; i++) captureArgs[i] = appdomain != null ? appdomain.IntType : null;

                var capInstance = definition.MakeGenericMethod(captureArgs) as ILMethod;
                if (capInstance == null)
                {
                    result.Skipped.Add(MakeSkip(definition,
                        new NeoCompilerFatal("MakeGenericMethod returned non-ILMethod")));
                    return null;
                }
                _ = capInstance.BodyRegister;   // triggers InitCodeBody -> capture hook
                var cached = definition.GenericMethodTemplateCache;
                if (cached == null)
                {
                    result.Skipped.Add(MakeSkip(definition,
                        new NeoCompilerFatal("template capture did not populate the cache")));
                }
                return cached;
            }
            catch (Exception ex)
            {
                result.Skipped.Add(MakeSkip(definition, ex));
                return null;
            }
        }

        static MethodSkip MakeSkip(ILMethod method, Exception ex)
        {
            string display;
            try
            {
                string tname = method.DeclearingType != null ? (method.DeclearingType.FullName ?? "?") : "?";
                var parms = method.Parameters;
                string psig;
                if (parms == null || parms.Count == 0) psig = "";
                else psig = string.Join(", ", parms.Select(p => p != null ? (p.FullName ?? "?") : "?"));
                display = tname + "." + (method.Name ?? "?") + "(" + psig + ")";
            }
            catch
            {
                display = method != null ? (method.Name ?? "?") : "?";
            }
            return new MethodSkip
            {
                MethodDisplay = display,
                ExceptionType = ex != null ? ex.GetType().Name : "?",
                Message = ex != null ? ex.Message : "",
            };
        }

        // A TYPE-level skip (a whole IL type omitted because its lazy CLR-base /
        // CLR-interface adaptor resolution threw TypeLoadException in the
        // CompileCore pre-filter). Mirrors MakeSkip, but MethodDisplay is a clear
        // type-level marker -- "(type) <FullName>" -- so the CLI's existing
        // "SKIP <display>: <ex>: <msg>" printer (Program.cs, UNCHANGED) renders a
        // type skip distinctly from a per-method skip. Reuses the existing
        // NeoCompilerResult.Skipped list + the IsComplete -> exit-2 path, so NO
        // CLI change is needed (Design D2 + OQ1 default).
        static MethodSkip MakeTypeSkip(ILType type, Exception ex)
        {
            string display;
            try
            {
                display = "(type) " + (type.FullName ?? "?");
            }
            catch
            {
                display = "(type) ?";
            }
            return new MethodSkip
            {
                MethodDisplay = display,
                ExceptionType = ex != null ? ex.GetType().Name : "?",
                Message = ex != null ? ex.Message : "",
            };
        }
    }

    /// <summary>
    /// The compile report. MethodsCompiled / TemplatesCaptured / TypesCompiled are
    /// the EMITTED counts (the survivor subset); Skipped holds every method or
    /// template-capture that threw. IsComplete is true iff nothing was skipped.
    /// </summary>
    public sealed class NeoCompilerResult
    {
        public int TypesCompiled;
        public int MethodsCompiled;        // methods emitted to MethodDefTable
        public int TemplatesCaptured;      // templates emitted to TemplateTable
        public List<MethodSkip> Skipped = new List<MethodSkip>();
        public bool IsComplete => Skipped == null || Skipped.Count == 0;
    }

    public sealed class MethodSkip
    {
        public string MethodDisplay;       // declaring-type-full-name.method-name(params)
        public string ExceptionType;       // e.g. "NotImplementedException"
        public string Message;
    }

    /// <summary>
    /// A fatal error prevented any .neo output (bad args, input file missing, the
    /// input assembly failed to load, the serializer threw). The CLI maps this to
    /// exit code 1. Distinct from a skip (a per-method compile failure -- the .neo
    /// is still written and the CLI returns exit code 2).
    /// </summary>
    public sealed class NeoCompilerFatal : Exception
    {
        public NeoCompilerFatal(string message) : base(message) { }
        public NeoCompilerFatal(string message, Exception inner) : base(message, inner) { }
    }
}
#endif
