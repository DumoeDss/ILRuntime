using System;

namespace TestCases
{
    // ===== Step 25: the V2 load+execute probe (deserialize + ExecuteNeo == JIT) =====
    //
    // A SMALL dedicated NON-GENERIC probe type for the Step-25 V2 functional
    // self-check (ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep25LoadExecCheck.cs).
    // The self-check compiles this probe via the SAME public NeoCompiler driver
    // the ilrt_neoc CLI uses -> a .neo MemoryStream -> reads it back via
    // NeoAssemblyReader -> runs each probe method via the JIT path (capture A) ->
    // NeoAssemblyLoader.Attach (overwrites the body with the AOT body) -> runs
    // the SAME methods via ExecuteNeo on the AOT body (capture B) -> asserts A==B
    // AND A==the known-expected value.
    //
    // PARAMETERLESS by design: the Neo host entry (ILIntepreter.Run) is a Step-6
    // shim that takes no args (the test-harness convention -- BaseTestUnit invokes
    // GetMethod(name,0) + App.Invoke(im, null)). Inputs are baked in as locals so
    // each method is deterministic; the self-check pins the expected value. NON-
    // GENERIC + NON-NESTED: S1 attaches non-generic methods only, and a top-level
    // type keeps the TypeRef full name == the LoadedTypes key (nested uses "/" vs "+").
    //
    // Matrix: arithmetic; try/catch EH; mixed locals + an internal byref call
    // (exercises the NeoCallParamMap rebuild); a TYPED catch (a specific type,
    // not Exception -- exercises catch-type-ref resolution + typed EH dispatch on
    // the AOT path); a constant body (the body-mutation cell mutates its Ldc_I4
    // before Attach to PROVE ExecuteNeo runs the genuine AOT body, not the JIT
    // body). Each method returns a deterministic primitive the self-check pins
    // (47 / 100 / 61 / 200 / 1234567).

    public class NeoStep25LoadProbe
    {
        // (a) pure arithmetic on locals, no calls. Expected: 6*3=18, 4-2=2,
        //     (18+2)*2=40, +7 = 47.
        public static int ArithProbe()
        {
            int a = 6;
            int b = 4;
            int x = a * 3;
            int y = b - 2;
            int z = (x + y) * 2;
            return z + 7;
        }

        // (b) exception handler (EH table -> NeoExceptionHandlerRecord -> the
        //     InitCodeBodyFromNeo EH rebuild). seed<0 forces the throw -> catch
        //     path so the rebuilt EH is exercised at runtime. throw new System.
        //     Exception is the CLR-newobj + Step-14 catch shape. The catch body
        //     uses a caught-flag pattern (no CLR property chain) to stay within
        //     the Step-6 host-entry shim's reach. Expected: 100 (catch taken).
        public static int TryCatchProbe()
        {
            int seed = -1;
            int result = 0;
            try
            {
                if (seed < 0) throw new Exception("neg");
                result = seed * 5;
            }
            catch (Exception ex)
            {
                result = 100;
            }
            return result;
        }

        // (d) TYPED catch -- a SPECIFIC catch type (InvalidOperationException),
        //     NOT the universal `catch (Exception)`. This forces the AOT EH rebuild
        //     to resolve the catch TypeRef to the runtime IType (ResolveTypeRefToIType
        //     -> appdomain.GetType for a CLR type) AND the runtime EH dispatch to
        //     match the thrown type against that specific catch type (the generic
        //     `catch (Exception)` in TryCatchProbe does not meaningfully stress the
        //     match). seed<0 forces the throw -> typed catch path. The catch body
        //     uses a caught-flag pattern (no CLR property chain) to stay within the
        //     Step-6 host-entry shim's reach. Expected: 200 (typed catch taken).
        public static int TypedCatchProbe()
        {
            int seed = -1;
            int result = 0;
            try
            {
                if (seed < 0) throw new InvalidOperationException("neg");
                result = seed * 5;
            }
            catch (InvalidOperationException)
            {
                result = 200;
            }
            return result;
        }

        // (e) CONSTANT body for the body-mutation cell. Compiles to a single
        //     Ldc_I4 < CONST > + Ret. The self-check deserializes this body,
        //     MUTATES the Ldc_I4 Operand to MUTATED before Attach, then asserts the
        //     post-Attach run returns MUTATED (NOT CONST). If ExecuteNeo were still
        //     running the JIT body, the result would be CONST -- so observing
        //     MUTATED proves the AOT body genuinely ran (the load-bearing property
        //     of the dual-path). CONST is chosen outside sbyte range so the JIT
        //     emits a real Ldc_I4 (not a Ldc_I4_S / Ldc_I4_* short form) and the
        //     Operand field carries the full value. Expected (unmutated): CONST.
        public static int ConstProbe()
        {
            return 1234567;
        }

        // Helper with a byref PARAM. MixedLocalsProbe calls this with `ref val`,
        // so MixedLocalsProbe's BODY carries a call with a byref argument -> the
        // NeoCallParamMap rebuild (PrimitiveByRefSrc + WriteBack + ElemType) is
        // exercised by the AOT path. The write-back makes `val` observable.
        public static int BumpRef(ref int x)
        {
            x = x + 10;
            return x;
        }

        // (c) mixed locals (int + long accumulator + loop counter) + a loop + an
        //     internal byref call (BumpRef). The return folds the write-back so a
        //     mis-rebuilt NeoCallParamMap diverges from JIT.
        //     Expected: n=4,prim=5, loop acc=5+6+7+8=26, val=5->15, bumped=15 ->
        //     26 + 15 + 5 + 15 = 61.
        public static int MixedLocalsProbe()
        {
            int n = 4;
            int prim = n + 1;
            long acc = 0;
            for (int i = 0; i < n; i++) acc += (long)(prim + i);
            int val = 5;
            int bumped = BumpRef(ref val);   // val -> 15 via byref write-back
            return (int)acc + bumped + prim + val;
        }
    }
}
