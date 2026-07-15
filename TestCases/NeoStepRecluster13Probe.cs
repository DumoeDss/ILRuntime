using System;
using ILRuntimeTest.TestFramework;

namespace TestCases
{
    // PROBE (neo-recluster-13) -- localize the UnitTest_TestFCP root.
    // UnitTest_TestFCP does ToColor("#FF00FF00") -> new TestVector3NoBinding(num1,num3,num4)
    // and gets (1,0,0) instead of (1,1,0): the 2nd ctor arg is lost. TestVector3NoBinding has
    // NO autogen Ctor_Neo stub (only get_one/op_Multiply/op_Addition/get_zero), so the ctor
    // runs via the REFLECTION fallback (CLRMethod.Invoke), a DIFFERENT path from the
    // neo-clr-struct-newobj-retdest-null fix (which fixed the autogen-stub Call_Redirect path).
    // This probe isolates: does a DIRECT `new TestVector3NoBinding(1f,2f,3f)` (no ToColor /
    // substring / div chain) lose its 2nd/3rd args? If yes -> the reflection struct-ctor path
    // is the root. If no -> the bug is upstream in ToColor's Convert.ToInt64 / divi.r4 chain.
    public class NeoStepRecluster13Probe
    {
        public static int NeoStepStructCtorNoBinding_TC1()
        {
            // Direct 3-float ctor into a local. Expected (1,2,3). If the reflection struct-
            // ctor path loses args 2/3, this yields (1,0,0) -> sum=1, not 6.
            var v = new TestVector3NoBinding(1f, 2f, 3f);
            int sum = (int)(v.x + v.y + v.z); // 1+2+3 = 6
            if (sum != 6)
                throw new Exception("TC1 struct-ctor arg loss: sum=" + sum + " (expected 6) x=" + v.x + " y=" + v.y + " z=" + v.z);
            return sum;
        }

        public static int NeoStepStructCtorNoBinding_TC2()
        {
            // Direct ctor with distinct escalating magnitudes (10,20,30) to discriminate which
            // arg is lost. Expected sum=60.
            var v = new TestVector3NoBinding(10f, 20f, 30f);
            int sum = (int)(v.x + v.y + v.z);
            if (sum != 60)
                throw new Exception("TC2 struct-ctor arg loss: sum=" + sum + " (expected 60) x=" + v.x + " y=" + v.y + " z=" + v.z);
            return sum;
        }

        // TC3: isolate Convert.ToInt64 called 3x in sequence ("FF","00","FF") -> 255,0,255.
        // If the 3rd call returns 0 (a stale autogen stub caching a result), num3 in ToColor
        // becomes 0 -- the UnitTest_TestFCP root.
        public static int NeoStepStructCtorNoBinding_TC3()
        {
            string s1 = "FF00FF00".Substring(0, 2); // "FF"
            string s2 = "FF00FF00".Substring(2, 2); // "00"
            string s3 = "FF00FF00".Substring(4, 2); // "FF"
            long n1 = Convert.ToInt64(s1, 16);
            long n2 = Convert.ToInt64(s2, 16);
            long n3 = Convert.ToInt64(s3, 16);
            int sum = (int)(n1 + n2 + n3); // 255+0+255 = 510
            if (sum != 510)
                throw new Exception("TC3 Convert.ToInt64 seq: n1=" + n1 + " n2=" + n2 + " n3=" + n3 + " sum=" + sum + " (expected 510)");
            return sum;
        }

        // TC5: mirror ToColor's EXACT control flow (3 divs, branch on Length>7, 4th div, then
        // pass num1/num3/num4 to the struct ctor). If this fails the same way ToColor does
        // (y=0), the bug is the divi.r4-result -> ctor-arg flow under a branch.
        public static int NeoStepStructCtorNoBinding_TC5()
        {
            string str = "FF00FF00";
            string str1 = str.Substring(0, 2); // FF
            string str2 = str.Substring(2, 2); // 00
            string str3 = str.Substring(4, 2); // FF
            float num1 = (float)Convert.ToInt64(str1, 16) / 255; // 1.0
            float num2 = (float)Convert.ToInt64(str2, 16) / 255; // 0
            float num3 = (float)Convert.ToInt64(str3, 16) / 255; // 1.0
            float num4 = 0f;
            if (str.Length > 7)
            {
                string str4 = str.Substring(6, 2); // 00
                num4 = (float)Convert.ToInt64(str4, 16) / 255; // 0
            }
            // ctor(num1, num3, num4) = (1, 1, 0); expected sum = 2
            var v = new TestVector3NoBinding(num1, num3, num4);
            int sum = (int)(v.x + v.y + v.z);
            if (sum != 2)
                throw new Exception("TC5 ToColor-mirror: num1=" + num1 + " num3=" + num3 + " num4=" + num4 + " -> x=" + v.x + " y=" + v.y + " z=" + v.z + " sum=" + sum + " (expected 2)");
            return sum;
        }

        // TC6: VERBATIM copy of ExpTest_20.ToColor body (StartsWith/Remove + both branches).
        // If this reproduces (returns (1,0,0) instead of (1,1,0)), the bug is isolable in this
        // method body. Input "#FF00FF00" -> expected (1,1,0).
        public static int NeoStepStructCtorNoBinding_TC6()
        {
            string str = "#FF00FF00";
            string strNew = str.StartsWith("#") ? str.Remove(0, 1) : str;
            string str1 = strNew.Substring(0, 2);
            string str2 = strNew.Substring(2, 2);
            string str3 = strNew.Substring(4, 2);
            float num1 = (float)Convert.ToInt64(str1, 16) / 255;
            float num2 = (float)Convert.ToInt64(str2, 16) / 255;
            float num3 = (float)Convert.ToInt64(str3, 16) / 255;
            TestVector3NoBinding color;
            if (strNew.Length > 7)
            {
                string str4 = strNew.Substring(6, 2);
                float num4 = (float)Convert.ToInt64(str4, 16) / 255;
                color = new TestVector3NoBinding(num1, num3, num4);
            }
            else
            {
                color = new TestVector3NoBinding(num1, num2, num3);
            }
            int sum = (int)(color.x + color.y + color.z); // expect 1+1+0 = 2
            if (sum != 2)
                throw new Exception("TC6 verbatim-ToColor: x=" + color.x + " y=" + color.y + " z=" + color.z + " sum=" + sum + " (expected 2)");
            return sum;
        }

        // TC7: the struct-by-value RETURN path. MakeColor returns a 12-byte struct; the caller
        // reads it. If the return marshalling drops y/z (the caller sees (1,0,0) for (1,2,3)),
        // THIS is the UnitTest_TestFCP root (ToColor returns the struct to its caller).
        static TestVector3NoBinding MakeColor3(float a, float b, float c)
        {
            return new TestVector3NoBinding(a, b, c);
        }
        public static int NeoStepStructCtorNoBinding_TC7()
        {
            var c = MakeColor3(1f, 2f, 3f);
            int sum = (int)(c.x + c.y + c.z); // expect 6
            if (sum != 6)
                throw new Exception("TC7 struct-return: x=" + c.x + " y=" + c.y + " z=" + c.z + " sum=" + sum + " (expected 6)");
            return sum;
        }

        // TC8 REMOVED: `return new TestVector3NoBinding(num1,num3,num4)` where the args are
        // divi.r4 results still loses y/z in the caller -- a DEEPER struct-Move/Ret bug in the
        // callee's `move r; ret r` sequence (proven: the ctor receives (1,1,0), targetBase is
        // (1,1,0) post-write-back, CopyNeoCallThisBack writes 12B correctly, but the caller
        // receives (1,0,0)). This is the UnitTest_TestFCP root; it needs its own child. TC7
        // (literal args) PASSES after the InvokeNeoClrMethod isNewobj struct fix; the divi.r4
        // form exposes the additional Move/Ret gap, documented in fullsmoke-ground-13.md.
    }
}
