using System;

namespace TestCases
{
    // ---- Single IL interface ----
    public interface INeoStep11Single
    {
        string Foo();
    }

    public class NeoStep11SingleImpl : INeoStep11Single
    {
        public string Foo()
        {
            return "single-impl";
        }
    }

    // ---- Override through interface variable ----
    public interface INeoStep11Override
    {
        string Bar();
    }

    public class NeoStep11OverrideBase : INeoStep11Override
    {
        public virtual string Bar()
        {
            return "base-bar";
        }
    }

    public class NeoStep11OverrideDerived : NeoStep11OverrideBase
    {
        public override string Bar()
        {
            return "derived-bar";
        }
    }

    // ---- Multiple interfaces (no cross-pollution) ----
    public interface INeoStep11IfaceA
    {
        string DoA();
    }

    public interface INeoStep11IfaceB
    {
        string DoB();
    }

    public class NeoStep11MultiImpl : INeoStep11IfaceA, INeoStep11IfaceB
    {
        public string DoA()
        {
            return "a";
        }

        public string DoB()
        {
            return "b";
        }
    }

    // ---- Interface inheritance chain ----
    public interface INeoStep11Parent
    {
        string ParentMethod();
    }

    public interface INeoStep11Child : INeoStep11Parent
    {
        string ChildMethod();
    }

    public class NeoStep11ChainImpl : INeoStep11Child
    {
        public string ParentMethod()
        {
            return "parent";
        }

        public string ChildMethod()
        {
            return "child";
        }
    }

    // ---- IL class implements a CLR interface (IDisposable) on the IL side ----
    public class NeoStep11DisposableImpl : IDisposable
    {
        public bool WasDisposed;

        public void Dispose()
        {
            WasDisposed = true;
        }
    }

    // ---- Negative contract (Step 11): the runtime handler throws a clear
    //      MissingMethodException when the `this` object's runtime type does
    //      not implement the encoded interface, with no ip overrun, no
    //      null-deref, and no Legacy fallback. This is enforced by the handler
    //      in ResolveNeoCallvirtInterfaceTarget (verified by code review). The
    //      custom test harness has no [ExpectedException] facility and treats
    //      any uncaught exception as a test failure, and IL try/catch needs
    //      Leave_S (Step 6, not yet implemented), so a runnable negative case
    //      cannot be expressed as a *passing* test here. The types below stay
    //      available for an explicit manual check once Step 6 lands. ----
    public interface INeoStep11Negative
    {
        string NegMethod();
    }

    public class NeoStep11NegativeUnrelated
    {
        public string SomethingElse()
        {
            return "unrelated";
        }
    }

    public class NeoStep11Test
    {
        public static void NeoStep11TestSingleInterfaceDispatch()
        {
            INeoStep11Single x = new NeoStep11SingleImpl();
            string result = x.Foo();
            AssertEqual("NeoStep11TestSingleInterfaceDispatch", "single-impl", result);
        }

        public static void NeoStep11TestInterfaceOverrideDispatch()
        {
            INeoStep11Override x = new NeoStep11OverrideDerived();
            string result = x.Bar();
            AssertEqual("NeoStep11TestInterfaceOverrideDispatch", "derived-bar", result);
        }

        public static void NeoStep11TestMultipleInterfacesNoCrossPollution()
        {
            NeoStep11MultiImpl obj = new NeoStep11MultiImpl();
            INeoStep11IfaceA a = obj;
            INeoStep11IfaceB b = obj;
            AssertEqual("NeoStep11TestMultipleInterfacesNoCrossPollution A", "a", a.DoA());
            AssertEqual("NeoStep11TestMultipleInterfacesNoCrossPollution B", "b", b.DoB());
        }

        public static void NeoStep11TestInterfaceInheritanceChain()
        {
            NeoStep11ChainImpl obj = new NeoStep11ChainImpl();
            INeoStep11Child child = obj;
            INeoStep11Parent parent = obj;
            AssertEqual("NeoStep11TestInterfaceInheritanceChain child", "child", child.ChildMethod());
            AssertEqual("NeoStep11TestInterfaceInheritanceChain parent", "parent", parent.ParentMethod());
        }

        // Note: this calls Dispose() through a CLR-typed interface reference
        // (IDisposable). IDisposable.Dispose resolves to a CLRMethod, so the
        // callvirt is lowered by Step 10's generic Callvirt arm
        // (MayCallvirtTargetILObject), NOT by Step 11's new Callvirt_Interface
        // opcode. Behavior is correct, but this test does NOT exercise the
        // interface offset-map. The IL-interface tests in this file
        // (single / multi / inheritance-chain) are what drive
        // Callvirt_Interface + the interface offset-map.
        public static void NeoStep11TestClrInterfaceIDisposable()
        {
            NeoStep11DisposableImpl obj = new NeoStep11DisposableImpl();
            IDisposable disp = obj;
            disp.Dispose();
            if (!obj.WasDisposed)
                throw new Exception("NeoStep11TestClrInterfaceIDisposable: Dispose was not invoked through the interface.");
        }

        private static void AssertEqual(string scenario, string expected, string actual)
        {
            if (actual != expected)
            {
                Console.WriteLine(scenario);
                Console.WriteLine(expected);
                Console.WriteLine(actual);
                throw new Exception(scenario);
            }
        }
    }
}
