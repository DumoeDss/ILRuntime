using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ILRuntime.CLR.Method;
using ILRuntime.Runtime.Enviorment;
using ILRuntime.Runtime.Intepreter;

namespace ILRuntime.Runtime.Adapters
{
    // Built-in CrossBindingAdaptor for System.Exception. Registered in the
    // AppDomain ctor alongside AttributeAdapter so an IL
    // `class X : System.Exception` LOADS (ILType.cs base-type resolution looks
    // up CrossBindingAdaptors keyed by the CLR base TypeForCLR -- without this
    // adaptor it throws TypeLoadException). The nested Adapter IS a CLR
    // System.Exception, so an IL exception instance's CLRInstance (created via
    // CreateCLRInstance in the ILTypeInstance ctor) is a real CLR Exception --
    // which the Throw opcode unwraps and the CLR catch machinery dispatches.
    //
    // Mirrors AttributeAdapter's shape precisely (same nested Adapter +
    // ILInstance bridge + cached IMethod forwarding pattern).
    public class ExceptionAdaptor : CrossBindingAdaptor
    {
        public override Type AdaptorType
        {
            get
            {
                return typeof(Adapter);
            }
        }

        public override Type BaseCLRType
        {
            get
            {
                return typeof(Exception);
            }
        }

        public override object CreateCLRInstance(Enviorment.AppDomain appdomain, ILTypeInstance instance)
        {
            return new Adapter(appdomain, instance);
        }

        public class Adapter : Exception, CrossBindingAdaptorType
        {
            ILTypeInstance instance;
            ILRuntime.Runtime.Enviorment.AppDomain appdomain;

            bool isToStringGot;
            IMethod toString;

            public Adapter(ILRuntime.Runtime.Enviorment.AppDomain appdomain, ILTypeInstance instance)
            {
                this.appdomain = appdomain;
                this.instance = instance;
            }

            public ILTypeInstance ILInstance
            {
                get
                {
                    return instance;
                }
            }

            public override string ToString()
            {
                if (!isToStringGot)
                {
                    isToStringGot = true;
                    IMethod m = appdomain.ObjectType.GetMethod("ToString", 0);
                    toString = instance.Type.GetVirtualMethod(m);
                }
                if (toString == null || toString is ILMethod)
                {
                    return instance.ToString();
                }
                else
                    return instance.Type.FullName;
            }
        }
    }
}
