#if ENABLE_NEO_MODE && DEBUG
using System;
using System.Collections.Generic;

using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;

namespace ILRuntime.Runtime.Intepreter.RegisterVM
{
    // F-4 #3 + F-12 TRUE-COMPLETION host-side self-check for the parametrized
    // `ILIntepreter.Run` Neo arm. Invoked host-side (CLI special mode
    // `NeoF4ParamRun`). Each cell drives `appdomain.Invoke(...)` exactly ONCE
    // (-> `Run` -> `ExecuteNeo`, no nesting), which is the correct shape for a
    // HOST -> IL re-entry and isolates the parametrized-Run machinery.
    //
    // WHY HOST-SIDE (not an IL test method): the F-4 #3 / F-12 scenarios are
    // host -> IL re-entry actions (CLR catches an IL exception, then
    // `appdomain.Invoke(instanceMethod, caughtException)`; CLR invokes a
    // reference-returning method). Driving them from WITHIN an IL method would
    // nest `domain.Invoke` (a second `Run`/`ExecuteNeo`) inside an in-flight
    // `ExecuteNeo`, which hits a PRE-EXISTING Neo re-entrancy corruption (the
    // outer frame's instruction pointer runs off the end of its body -> garbage
    // opcode; reproduced with the OLD parameterless-only Run shim too, so it is
    // NOT this change). The host check invokes `Run` once per cell.
    //
    // Cells:
    //   F12_RefReturn   -- invoke the parameterless static `EchoRef` (returns the
    //                      reference-type string "hi") via `appdomain.Invoke`. On
    //                      HEAD `Run` reads it via `NeoBoxReturnValue` (primitives
    //                      only) -> the raw mStack index as an int -> NOT a string
    //                      -> FAIL. After the fix `Run`'s reference-return branch
    //                      boxes the string -> PASS.
    //   F4_InstanceReentry -- invoke `BuildF4Ex` (constructs a MyEx with
    //                      Msg="f4code") to obtain an instance, then invoke the
    //                      instance method `GetCode` on it via
    //                      `appdomain.Invoke(getCode, instance)`. On HEAD `Run`
    //                      drops `instance` -> the override NREs on the Msg.Length
    //                      field access -> the NRE propagates -> FAIL. After the
    //                      fix `Run` marshals slot-0 `this` -> returns 6 -> PASS.
    public static class NeoF4ParamRunCheck
    {
        public class Result
        {
            public int TotalCells;
            public int Passed;
            public int Failed;
            public List<string> Failures = new List<string>();
        }

        public static Result Run(ILRuntime.Runtime.Enviorment.AppDomain appdomain)
        {
            var res = new Result();

            if (!appdomain.LoadedTypes.TryGetValue("TestCases.NeoStep14Test", out var t) || !(t is ILType probeType))
            {
                res.TotalCells = 1; res.Failed = 1;
                res.Failures.Add("TestCases.NeoStep14Test not loaded / not an ILType");
                return res;
            }

            // ===== Cell 1: F-12 reference-type return via Run =====
            res.TotalCells++;
            {
                var echoRef = probeType.GetMethod("EchoRef", 0) as ILMethod;
                if (echoRef == null)
                {
                    res.Failed++;
                    res.Failures.Add("F12_RefReturn: EchoRef method not found");
                    Console.WriteLine("  [FAIL] F12_RefReturn: EchoRef method not found");
                }
                else
                {
                    object ret = null;
                    try
                    {
                        ret = appdomain.Invoke(echoRef, null);
                    }
                    catch (Exception ex)
                    {
                        res.Failed++;
                        res.Failures.Add("F12_RefReturn: Invoke threw " + ex.GetType().Name + ": " + ex.Message);
                        Console.WriteLine("  [FAIL] F12_RefReturn: Invoke threw " + ex.GetType().Name + ": " + ex.Message);
                        goto cell2;
                    }
                    // The reference-return fix yields the boxed string "hi". On HEAD
                    // the primitive fallback yields a boxed int (the raw mStack
                    // index), which is NOT a string.
                    if (ret is string s && s == "hi")
                    {
                        res.Passed++;
                        Console.WriteLine("[NeoF4ParamRun] F12_RefReturn: PASS (boxed string \"hi\")");
                    }
                    else
                    {
                        res.Failed++;
                        string got = ret == null ? "null" : (ret.GetType().Name + ":" + ret);
                        res.Failures.Add("F12_RefReturn: expected string \"hi\", got " + got);
                        Console.WriteLine("  [FAIL] F12_RefReturn: expected string \"hi\", got " + got);
                    }
                }
            }

            cell2:
            // ===== Cell 2: F-4 #3 instance-method re-entry via Run =====
            res.TotalCells++;
            {
                var buildF4Ex = probeType.GetMethod("BuildF4Ex", 0) as ILMethod;
                // GetCode is an INSTANCE method on the nested MyEx type
                // (FullName "TestCases.NeoStep14Test/MyEx" -- nested IL types use
                // "/" as the separator in LoadedTypes).
                ILMethod getCode = null;
                if (appdomain.LoadedTypes.TryGetValue("TestCases.NeoStep14Test/MyEx", out var myExT) && myExT is ILType myExType)
                    getCode = myExType.GetMethod("GetCode", 0) as ILMethod;
                if (buildF4Ex == null || getCode == null)
                {
                    res.Failed++;
                    res.Failures.Add("F4_InstanceReentry: BuildF4Ex/GetCode method not found (buildF4Ex=" + (buildF4Ex != null) + ", getCode=" + (getCode != null) + ")");
                    Console.WriteLine("  [FAIL] F4_InstanceReentry: BuildF4Ex/GetCode method not found");
                    goto done;
                }
                // Obtain a MyEx instance (Msg="f4code") via the public Run path.
                object instance = null;
                try
                {
                    instance = appdomain.Invoke(buildF4Ex, null);
                }
                catch (Exception ex)
                {
                    res.Failed++;
                    res.Failures.Add("F4_InstanceReentry: BuildF4Ex Invoke threw " + ex.GetType().Name + ": " + ex.Message);
                    Console.WriteLine("  [FAIL] F4_InstanceReentry: BuildF4Ex Invoke threw " + ex.GetType().Name + ": " + ex.Message);
                    goto done;
                }
                // Re-invoke the instance method GetCode on that instance via the
                // PUBLIC Run path. This is the F-4 #3 scenario. `instance` is the
                // CLR view returned by Invoke (an adaptor / ILTypeInstance); Run's
                // HasThis arm unwraps CrossBindingAdaptorType -> ILInstance.
                object ret = null;
                try
                {
                    ret = appdomain.Invoke(getCode, instance);
                }
                catch (Exception ex)
                {
                    // HEAD: Run drops `instance` -> GetCode NREs on Msg.Length.
                    res.Failed++;
                    res.Failures.Add("F4_InstanceReentry: GetCode Invoke threw " + ex.GetType().Name + ": " + ex.Message);
                    Console.WriteLine("  [FAIL] F4_InstanceReentry: GetCode Invoke threw " + ex.GetType().Name + ": " + ex.Message);
                    goto done;
                }
                // After the fix GetCode returns "f4code".Length == 6.
                if (ret is int code && code == 6)
                {
                    res.Passed++;
                    Console.WriteLine("[NeoF4ParamRun] F4_InstanceReentry: PASS (GetCode returned 6)");
                }
                else
                {
                    res.Failed++;
                    string got = ret == null ? "null" : (ret.GetType().Name + ":" + ret);
                    res.Failures.Add("F4_InstanceReentry: expected int 6, got " + got);
                    Console.WriteLine("  [FAIL] F4_InstanceReentry: expected int 6, got " + got);
                }
            }

            done:
            return res;
        }
    }
}
#endif
