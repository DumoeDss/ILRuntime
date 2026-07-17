# neo-brtrue-on-reference

CORRECTNESS: Neo brtrue/brfalse test low-int32 !=0 but a null ref is a non-zero mStack index / -1 -> null misclassifies as truthy. The ceq/brfalse-on-reference lazy-init 'if(x==null)' gap (actively failing). Paired with F3 (IL-static Stsfld/Ldsfld raw DstOffset). Fix together carefully.
