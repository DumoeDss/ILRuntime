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
}
