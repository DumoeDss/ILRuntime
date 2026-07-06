using System;
using System.Collections.Generic;

namespace TestCases
{
    // ===== Step 22: generic-method TEMPLATE mechanism -- V2 functional roundtrip =====
    //
    // The generic-method template path (CloneAndPatch) is wired into
    // ILMethod.BodyRegister/#if ENABLE_NEO_MODE InitCodeBody. The FIRST capture-
    // eligible instantiation (ref/primitive typeArgs) of each generic definition
    // captures the template via the per-occurrence JIT; SUBSEQUENT instantiations
    // (any T, incl. struct-T) CloneAndPatch from it. So invoking a generic method
    // more than once with different T exercises BOTH paths.
    //
    // This file defines the matrix generic methods (also used by the host-side V1
    // structural self-check) + a V2 functional roundtrip test that invokes them
    // and asserts correct results via `throw new Exception(...)` (the established
    // NeoStep assertion pattern). The structural V1 body-equivalence self-check
    // lives host-side (ILRuntime.Tests.NeoStep22SelfCheck), invoked separately.

    // An IL value type for the struct-T cells (exercises the Initobj-prefix
    // rebuild -- the front-half T-dep CloneAndPatch handles).
    public struct NeoStep22Struct
    {
        public int X;
        public long Y;
    }

    // A reference IL class type (the second ref-T cell beside object).
    public class NeoStep22RefClass
    {
        public int V;
    }

    public class NeoStep22GenericProbes
    {
        // Shape 1: T local / param / return. For struct-T the local `current`
        // fires the auto-Initobj insertion (the front-half T-dependence the design
        // mistakenly attributed to TypeSpecialize; CloneAndPatch rebuilds it).
        public static T ProbeBasic<T>(T v, int n)
        {
            T current = v;
            int sum = n + 1;
            return current;
        }

        // Shape 2: Newarr T (TypeToken PatchEntry on Newarr.Operand).
        public static T[] MakeArray<T>(int n)
        {
            T[] a = new T[n];
            return a;
        }

        // Shape 3: ref T -> Stobj/Ldobj (TypeToken PatchEntry).
        public static void StoreRef<T>(ref T acc, T v) { acc = v; }
        public static T LoadRef<T>(ref T v) { return v; }

        // Shape 4: Box T (TypeToken; token-bearing -> blocks ref-share).
        public static object BoxIt<T>(T v) { return (object)v; }

        // Shape 5: constrained.callvirt T.M (the Constrained TypeToken PatchEntry
        // site -- BLOCKER-1 regression guard). The C# compiler lowers
        // `v.GetHashCode()` / `a.Equals(b)` on a generic T as
        // `constrained. T / callvirt Object::GetHashCode()`, so the JIT emits a
        // Constrained op whose Operand is T's type hash (concrete-T-dependent).
        public static int HashIt<T>(T v) { return v.GetHashCode(); }
        public static bool EqualsIt<T>(T a, T b) { return a.Equals(b); }

        // Shape 6: a T-QUALIFIED method call (MAJOR-2 reachability probe). With
        // `where T : IComparable<T>`, `a.CompareTo(b)` resolves to
        // `IComparable<T>::CompareTo(T)` -- a method whose DECLARING TYPE carries
        // the generic param T, so the callvirt method token (Operand2) is
        // potentially T-dependent. This probes whether such a token reaches the
        // CloneAndPatch path and diverges from per-occurrence.
        public static int CompareThem<T>(T a, T b) where T : IComparable<T>
        {
            return a.CompareTo(b);
        }

        // Shape 7 (MINOR-3): struct-T + control-flow delta-shift coverage. Each
        // has a generic-T local `current` (fires the auto-Initobj prefix for
        // struct-T -> the prefix grows -> DoCloneAndPatch must shift branch
        // Operands / SwitchTargets / Addr by the delta). BranchIt = if/else
        // (branch Operands); SwitchIt = switch (SwitchTargets); TryCatch =
        // try/catch (exception-handler Addr).
        public static T BranchIt<T>(T v, int n)
        {
            T current = v;
            if (n > 0) current = v;
            else current = v;
            return current;
        }
        public static T SwitchIt<T>(T v, int n)
        {
            T current = v;
            switch (n)
            {
                case 0: current = v; break;
                case 1: current = v; break;
                default: current = v; break;
            }
            return current;
        }
        public static T TryCatch<T>(T v)
        {
            T current = v;
            try { current = v; }
            catch (System.Exception) { current = v; }
            return current;
        }
    }

    // Shape 5: generic method on a generic type (exercised functionally in V2).
    public class NeoStep22GenericHolder<T>
    {
        public static U Echo<U>(U u) { return u; }
    }

    public class NeoStep22Test
    {
        // V2 functional roundtrip. The first call per definition captures the
        // template (per-occurrence); later calls CloneAndPatch. All must produce
        // correct results regardless of which path produced the body.
        public static void NeoStep22TemplateEquivalence()
        {
            // ProbeBasic across primitive / ref / struct T.
            int r1 = NeoStep22GenericProbes.ProbeBasic(7, 3);
            if (r1 != 7) throw new Exception("NeoStep22 ProbeBasic<int> failed");
            long r2 = NeoStep22GenericProbes.ProbeBasic(9L, 3);
            if (r2 != 9L) throw new Exception("NeoStep22 ProbeBasic<long> failed");
            string r3 = NeoStep22GenericProbes.ProbeBasic("hi", 3);
            if (r3 != "hi") throw new Exception("NeoStep22 ProbeBasic<string> failed");
            NeoStep22Struct sv = new NeoStep22Struct { X = 5, Y = 6 };
            NeoStep22Struct rs = NeoStep22GenericProbes.ProbeBasic(sv, 3);
            if (rs.X != 5 || rs.Y != 6) throw new Exception("NeoStep22 ProbeBasic<struct> failed");

            // MakeArray (Newarr T) across T.
            int[] a1 = NeoStep22GenericProbes.MakeArray<int>(3);
            if (a1.Length != 3) throw new Exception("NeoStep22 MakeArray<int> failed");
            long[] a2 = NeoStep22GenericProbes.MakeArray<long>(2);
            if (a2.Length != 2) throw new Exception("NeoStep22 MakeArray<long> failed");

            // StoreRef / LoadRef (Stobj/Ldobj T).
            int acc = 0;
            NeoStep22GenericProbes.StoreRef<int>(ref acc, 42);
            if (acc != 42) throw new Exception("NeoStep22 StoreRef<int> failed");
            int ld = NeoStep22GenericProbes.LoadRef<int>(ref acc);
            if (ld != 42) throw new Exception("NeoStep22 LoadRef<int> failed");

            // BoxIt (Box T) -- a token-bearing body, so ref-T instantiations
            // CloneAndPatch rather than ref-share. (Boxing a generic-param ref-T
            // hits a pre-existing runtime Box-arm bug unrelated to Step 22, so the
            // V2 functional roundtrip covers the value-T box; the V1 structural
            // self-check covers BoxIt<object>/<RefClass> body equivalence.)
            object ob = NeoStep22GenericProbes.BoxIt(123);
            if ((int)ob != 123) throw new Exception("NeoStep22 BoxIt<int> failed");

            // Generic method on a generic type.
            int g = NeoStep22GenericHolder<string>.Echo<int>(77);
            if (g != 77) throw new Exception("NeoStep22 GenericHolder.Echo failed");

            // HashIt / EqualsIt (constrained.callvirt T.M): the BLOCKER-1
            // regression guard is the V1 STRUCTURAL cell (HashIt/EqualsIt bodies
            // match per-occurrence across all T -- see NeoStep22SelfCheck). A V2
            // FUNCTIONAL cell for the constrained pattern is NOT exercised here
            // because the runtime Constrained arm (Step 17) has pre-existing gaps
            // for ref-type-T and primitive-T `this` (box-once NIE/NRE), and every
            // capture-eligible T is ref/primitive -- so the capture call itself
            // cannot execute. This is a Step-17 runtime task, NOT a Step-22
            // regression: V1 proves the CloneAndPatch body is byte-identical to
            // per-occurrence, so runtime behavior is path-independent. The runtime
            // Constrained arm READS the patched concrete-T token correctly (dump-
            // gated: HashIt<long> via CloneAndPatch resolves ip->Operand to
            // System.Int64, not the stale capture-T Int32).
        }
    }
}
