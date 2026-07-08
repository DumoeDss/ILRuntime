# neo-f7b-reftype-writeback

TRUE COMPLETION: F-7B ref-type byref write-back -- a delegate ref param where the callee REASSIGNS the referent (s = s + "!") leaves a dangling mStack index (the new object lands in the callee's frame ref region, popped by ExecuteNeo). Fix = mStack lifetime promotion for the byref-write-back case.
