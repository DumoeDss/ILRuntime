using System;
using System.Collections.Generic;

namespace TestCases
{
    // ===== Step 23: .neo binary format serialization INPUTS (the V1 matrix) =====
    //
    // These classes are the serialization inputs for the host-side V1 roundtrip
    // self-check (ILRuntime/Runtime/Intepreter/RegisterVM/NeoStep23RoundtripCheck.cs).
    // The self-check compiles each matrix method via the Neo JIT, serializes the
    // resulting CompiledFrame + the declaring ILType + any cached template to a
    // MemoryStream, deserializes, and asserts deserialized == original
    // (OpCodeR[] byte-for-byte, CompiledFrame field-for-field, type layout
    // identical, template faithful). They also serve as a compile-cleanliness
    // gate under Debug_Neo.
    //
    // V2 (deserialize -> ExecuteNeo) is Step 25; V1 is structural equivalence.

    // ---- TypeDef coverage: a base IL type + a derived IL type that implements
    //      an interface, declares varied instance fields (primitive / IL value
    //      type / CLR value type / reference), and has a static ctor + virtual
    //      methods (for the VTable). ----

    public interface INeoStep23Iface
    {
        int IfaceMethod(int x);
    }

    // A second IL interface (Minor-3: multi-interface TypeDef coverage -- a type
    // implementing >=2 interfaces exercises a deeper VTable + the NeoInterface-
    // EntryRecord[] serialization with >1 entry, both carrying real TypeRef idxs).
    public interface INeoStep23Iface2
    {
        int IfaceMethod2(int x);
        string IfaceMethod3(int x);
    }

    // An IL value type field (Neo Step 12 in-frame VT).
    public struct NeoStep23ILValue
    {
        public int A;
        public long B;
    }

    public class NeoStep23Base
    {
        public int BaseField;
        public virtual int VirtBase(int x) { return x + 1; }
    }

    public class NeoStep23TypeDefProbe : NeoStep23Base, INeoStep23Iface
    {
        public long PrimField;                 // primitive instance field
        public NeoStep23ILValue IlVtField;     // IL value-type instance field
        public DateTime ClrVtField;            // CLR value-type instance field
        public string RefField;                // reference instance field
        public static int s_counter;           // static field (static-instance seed)

        static NeoStep23TypeDefProbe()
        {
            s_counter = 42;  // static ctor -> InitializerTable coverage
        }

        public override int VirtBase(int x) { return x + 2; }
        public int IfaceMethod(int x) { return x + 3; }

        public int SumFields()
        {
            return (int)PrimField + (int)IlVtField.A + BaseField + s_counter;
        }
    }

    // ---- Minor-3: a multi-interface TypeDef (implements 2 IL interfaces). The
    //      shipped NeoStep23TypeDefProbe has ifaces=1; this one has ifaces=2,
    //      exercising the multi-entry NeoInterfaceEntryRecord[] path + a deeper
    //      VTable (VirtBase + IfaceMethod + IfaceMethod2 + IfaceMethod3). ----

    public class NeoStep23MultiIfaceProbe : NeoStep23Base, INeoStep23Iface, INeoStep23Iface2
    {
        public long ExtraField;        // an instance field so Fields[] is non-empty

        public override int VirtBase(int x) { return x + 4; }
        public int IfaceMethod(int x) { return x + 5; }
        public int IfaceMethod2(int x) { return x + 6; }
        public string IfaceMethod3(int x) { return (x + 7).ToString(); }

        public int SumExtra()
        {
            return (int)ExtraField + BaseField;
        }
    }

    // ---- MethodDef coverage: the V1 roundtrip matrix methods. ----

    public class NeoStep23RoundtripProbes
    {
        // (a) non-generic method: primitive locals + arithmetic + a call.
        public static int ProbeBasic(int a, int b)
        {
            int x = a + 1;
            int y = b + 2;
            int z = x * y;
            return z + 7;
        }

        // (b) generic method WITH a template (template-eligible -- exercises the
        //     NeoTemplateRecord serialization + the faithful PatchEntry capture).
        public static T GenericProbe<T>(T v, int n)
        {
            T current = v;
            object boxed = v;          // Box T -> TypeToken patch site
            int h = v.GetHashCode();   // constrained. T callvirt -> Constrained patch
            return current;
        }

        // (c) method with an exception handler (EH table -> NeoExceptionHandlerRecord).
        public static int TryCatchProbe(int a)
        {
            int result = 0;
            try
            {
                if (a < 0) throw new Exception("neg");
                result = a * 2;
            }
            catch (Exception ex)
            {
                result = ex.Message.Length;
            }
            return result;
        }

        // (d) diverse locals: primitive / IL value-type / CLR value-type.
        public static long MixedLocals(int n)
        {
            int prim = n + 1;
            NeoStep23ILValue ilvt = default(NeoStep23ILValue);
            ilvt.A = n;
            ilvt.B = n * 2L;
            DateTime clrvt = DateTime.Now;
            long sum = prim + ilvt.A + ilvt.B + clrvt.Ticks;
            return sum;
        }

        // (e) byref params (ref/out -- Step 17 byref support; the
        //     NeoCallParamMap + PrimitiveByRefElemType serialization path).
        public static void ByrefParams(ref int acc, int add, out int result)
        {
            acc = acc + add;
            result = acc * 2;
        }

        // A method with a switch (SwitchTargets serialization).
        public static int SwitchProbe(int x)
        {
            switch (x)
            {
                case 0: return 100;
                case 1: return 200;
                case 2: return 300;
                default: return 999;
            }
        }

        // (g) Minor-3: a CLR call carrying a byref param whose element type is a
        //     REAL System.Int32 (int.TryParse(string, out int)). This is the one
        //     divergence path the shipped 9 cells NEVER hit: every shipped byref
        //     slot had PrimitiveByRefElemType == null, so the null->"" aqname
        //     normalization was exercised only on the empty side. Here the `out
        //     int` slot carries a non-empty "System.Int32" aqname, proving the
        //     real-value aqname serialization path roundtrips (diagnostic: this
        //     cell yields nonEmptyAqnameSlots=1).
        public static int ClrByrefCall(string s)
        {
            int.TryParse(s, out int result);
            return result;
        }

        // (h) Minor-3: a multi-clause / nested exception handler (try / catch /
        //     catch / finally -> 3 EH clauses with mixed HandlerType: Catch,
        //     Catch, Finally). The shipped TryCatchProbe has exactly 1 catch
        //     clause; this exercises ExceptionHandlers[].Length > 1 + the Finally
        //     HandlerType + a second CatchTypeRefIdx.
        public static int NestedEH(int a)
        {
            int result = 0;
            try
            {
                if (a < 0) throw new Exception("neg");
                result = a * 2;
            }
            catch (FormatException fex)
            {
                result = 100 + fex.Message.Length;
            }
            catch (Exception ex)
            {
                result = 200 + ex.Message.Length;
            }
            finally
            {
                result += 1;
            }
            return result;
        }

        // (i) Minor-3: a multi-Constrained-pair generic template -- the Step-22
        //     BLOCKER-1 shape (constrained. T callvirt) DOUBLED. Two DISTINCT
        //     constrained callvirts (GetHashCode + ToString) -> ConstrainedType-
        //     RefIdxs.Length == 2 + ConstrainedMethodRefIdxs.Length == 2, plus a
        //     Box T TypeToken patch. This is the single most important coverage
        //     gain: the shipped GenericProbe template has exactly 1 constrained
        //     pair; this proves the multi-pair template (+ its Constrained* arrays)
        //     roundtrips faithfully. (Serialization input only -- never executed;
        //     the runtime Constrained arm gaps are a Step-17 matter, out of scope.)
        public static int MultiConstrainedGeneric<T>(T v)
        {
            int h = v.GetHashCode();     // constrained. T -> Object::GetHashCode
            string s = v.ToString();     // constrained. T -> Object::ToString (distinct)
            object boxed = v;            // Box T (token-bearing TypeToken patch)
            return h + s.Length;
        }

        // (j) Minor-3 (cheap): a zero-locals method (LocalInfos.Length == 0). The
        //     shipped methods all allocate locals; this exercises the empty-
        //     LocalInfos serialization path (WriteStackSlotInfoArray of a 0-length
        //     array) + a near-minimal body.
        public static int ZeroLocals(int n)
        {
            return n;
        }
    }
}
