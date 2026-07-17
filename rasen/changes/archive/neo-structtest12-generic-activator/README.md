# neo-structtest12-generic-activator

wave2 LAST: StructTest12 = Activator.CreateInstance<T> in generic method resolves T to ILTypeInstance (not enclosing T) + redirect returns heap not struct. 2 coupled JIT bugs. re-audit + fix + full-smoke 1->0
