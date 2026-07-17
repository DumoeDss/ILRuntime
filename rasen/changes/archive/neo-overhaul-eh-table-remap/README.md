# neo-overhaul-eh-table-remap

LATENT CORRECTNESS (child-1 sibling): method.ExceptionHandlerRegister (TryStart/TryEnd/HandlerStart/HandlerEnd, body-indexed) is NOT remapped by LowerNeoOffsets Push-deletion. Trigger: try/catch/finally + >3-arg call/newobj that THROWS -> mis-routed exception. Plumb exceptionHandlerR through LowerNeoOffsets + THROWING-EH probe.
