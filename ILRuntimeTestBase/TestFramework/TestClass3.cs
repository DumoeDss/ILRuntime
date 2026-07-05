#define TEST_MISSING_METHOD
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ILRuntime.Other;

namespace ILRuntimeTest.TestFramework
{
    [NeedAdaptor]
    public class TestClass3
    {
        public TestStruct Struct;
        public static string getString(int startIndex = 0, int length = -1)
        {
            throw new Exception();
        }

        public static string getString(ref int startIndex, int length = -1)
        {
            startIndex++;
            return startIndex.ToString() + length;
        }

        public static void setBit(ref byte value, int pos, int bit) { value = (byte)(value & ~(1 << pos) | (bit << pos)); }
    }

    [NeedAdaptor]
    public class TestClass4
    {
        protected int a;
        protected int b;
        protected TestClass2 cls2;

        public virtual void KKK()
        {
            a = 1;
            b = 2;
        }

        public void TestArrayOut(out TestStruct[] arr)
        {
            arr = new TestStruct[10];
        }
    }

    public struct TestStruct
    {
        public static TestStruct instance;
        public Action<int> testField;
        public int value;
        public static void DoTest(ref TestStruct a)
        {
            a.value = 11111;
        }

        public static void DoTest(ref int a)
        {
            a = 22222;
        }

        public static void DoTest2(TestStruct aaa)
        {
            aaa.value = 232425235;
        }

        public static int Add(int a, int b)
        {
            return a + b;
        }
    }
    public class TestHashMap<TKey, TValue>
    {
        private System.Collections.Generic.Dictionary<TKey, TValue> dic;
        public TestHashMap()
        {
            dic = new System.Collections.Generic.Dictionary<TKey, TValue>();
        }
        public TValue this[TKey key]
        {
            get => dic[key];
            set => dic[key] = value;
        }

        public System.Collections.Generic.IEnumerator<System.Collections.Generic.KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return dic.GetEnumerator();
        }

        public bool Add(TKey key, TValue value)
        {
            dic.Add(key, value);
            return true;
        }
    }

    public class TestCLRBinding
    {
        public static int ValidateNeoSmallPrimitiveArgs(byte b, sbyte sb, short s, ushort us, bool flag, char ch, int tail)
        {
            return b + sb + s + us + (flag ? 1000 : 0) + ch + tail;
        }

        // ---- Step 13b K2/K2-FAM helpers (host C#; read by the IL side via the
        //      reflection fallback CLRMethod.Invoke(byte*) -- no autogen redirect
        //      for these). Taking a CLR struct BY VALUE and returning a primitive
        //      verifies the param bytes cross IL->CLR correctly without needing
        //      IL-side ldfld on CLR struct fields (a separate deferred concern).
        //      These mirror the K2 reproducer shape. ----

        // K2: a CLR struct by-value PARAMETER (no binder). Returns the int sum
        // of the three float fields as a check the struct's flat bytes arrived
        // unchanged. Before 13b the caller-temp-slot fallback miscopied the
        // boxed-ref mStack index into the param region (K2).
        public static int SumTestVector3NoBindingFields(TestVector3NoBinding v)
        {
            return (int)(v.x + v.y + v.z);
        }

        // K2: a CLR struct by-value PARAMETER (WITH binder). TestVector3 has a
        // registered ValueTypeBinder (pure-primitive, 3 floats).
        public static int SumTestVector3Fields(TestVector3 a, TestVector3 b)
        {
            return (int)(a.X + a.Y + a.Z + b.X + b.Y + b.Z);
        }

        // K2 (return side): a CLR struct RETURN value. The reflection return
        // path (InvokeNeoClrMethod) must write the struct's flat bytes into the
        // caller's dest local. Returns a known struct; the IL caller checks it
        // by re-feeding it to Sum... above (no IL-side ldfld needed).
        public static TestVector3NoBinding MakeTestVector3NoBinding(float x, float y, float z)
        {
            return new TestVector3NoBinding(x, y, z);
        }

        public void LoadAsset<T>(string name, T obj)
        {

        }
        public void Emit<T>(T obj)
        {
            LoadAsset("123", obj);
        }

        // ---- F-MAJ-1 helpers (host C#; read by the IL side via the reflection
        //      fallback). Mirror the Make/Sum pattern for the boundary structs
        //      and the int-return control. ----
        public static TestStruct4 MakeTestStruct4(int a)
        {
            return new TestStruct4(a);
        }
        public static int SumTestStruct4(TestStruct4 v)
        {
            return v.a;
        }
        public static TestStruct8 MakeTestStruct8(int a, int b)
        {
            return new TestStruct8(a, b);
        }
        public static int SumTestStruct8(TestStruct8 v)
        {
            return v.a + v.b;
        }
        // Two CLR int returns (the documented control: primitive writes 4 bytes
        // into a 4-byte slot; no overflow). Different values to distinguish them.
        public static int MakeIntA() { return 600; }
        public static int MakeIntB() { return 3; }
        // A method that touches the frame between two Make() calls (live-range
        // overlap probe): returns an int the caller must observe so the compiler
        // does not dead-code-eliminate the call.
        public static int TouchFrame(int x)
        {
            return x + 1;
        }

        // ---- Review-loop M1/M2/M3 host helpers (neo-opt-harden-2 review fix).
        //      These exist to give the IL-side probes a way to (a) build a runtime
        //      string that occupies an mStack ref slot (a "canary" neighbour for
        //      the Initobj-zero-init probe), (b) read its length back, and (c)
        //      unbox a boxed CLR struct and report a field-derived sum so Box/
        //      Isinst/Castclass on a CLR struct local can be observed. ----

        // M1 canary: a 7-char string built at runtime (forces an mStack ref slot).
        public static string MakeCanary()
        {
            return "CANARY!";
        }

        // M1 canary read-back: returns the string length (expect 7).
        public static int StringLength(string s)
        {
            return s != null ? s.Length : -1;
        }

        // M2/M3 read-back: unbox a boxed TestVector3NoBinding and report the int
        // sum of its three float fields (expect 600 for (100,200,300)).
        public static int UnboxAndSumVector3NoBinding(object o)
        {
            if (o is TestVector3NoBinding v)
                return (int)(v.x + v.y + v.z);
            return -1;
        }

        // M3: report whether `o is TestVector3NoBinding` on the host side (a
        // second opinion independent of the IL-side isinst). Returns 1 / 0.
        public static int IsVector3NoBinding(object o)
        {
            return o is TestVector3NoBinding ? 1 : 0;
        }

        // Mixed-frame helper (M-extra): consume a CLR struct, an int, and a
        // string in one frame to confirm no cross-corruption among neighbouring
        // flat-bytes / primitive / ref slots.
        public static int MixedFrameSum(TestVector3NoBinding v, int n, string s)
        {
            int sum = (int)(v.x + v.y + v.z) + n;
            return sum + (s != null ? s.Length : 0);
        }

#if TEST_MISSING_METHOD
        public int missingField;
        public void MissingMethodGeneric<T>(T obj)
        {

        }

        public void MissingMethod()
        {

        }

        public static void MissingStaticMethod()
        {

        }
#endif
    }

#if TEST_MISSING_METHOD
    public class MissingType
    {
    }
#endif

    // ---- F-MAJ-1 boundary structs (managed sizes 4 and 8 bytes; pure primitive).
    //      Used by the NeoOptHardTest_Fmaj1_StructSize4 / ...Size8 probes to test
    //      the overflow onset: a 4-byte struct must NOT overflow a 4-byte slot;
    //      an 8-byte struct overflows by 4. ----
    public struct TestStruct4
    {
        public int a;
        public TestStruct4(int a) { this.a = a; }
    }

    public struct TestStruct8
    {
        public int a;
        public int b;
        public TestStruct8(int a, int b) { this.a = a; this.b = b; }
    }
}
