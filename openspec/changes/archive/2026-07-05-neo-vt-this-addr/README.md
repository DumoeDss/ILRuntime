# neo-vt-this-addr

[VT-THIS-ADDR] fix: IL value-type `newobj` delivered -- track a VT `this` (param slot 0) and a VT `newobj` dest as in-frame addresses for ALL field access (JIT dest-typing + addrAlias/`this`-root extension + Newobj arm IL-VT branch). Resolves the Q-VT-NEWOBJ deferral from Step 18 (both the newobj-instruction form and the `VT x = new VT(args)` local form). Modifies `neo-value-types` (primary), `neo-byref` (byref `this` caller), `neo-newobj` (flips IL-VT newobj DEFERRED -> delivered).
