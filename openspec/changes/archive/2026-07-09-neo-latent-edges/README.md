# neo-latent-edges

TRUE COMPLETION: triage + fix the 4 small latent edges -- F-9 (inlined IL-method return-move mis-classification), F-7 (delegate ref/out byref marshal), F-2 (ref-only VT local newobj inliner), F-11 (generic-instance eager-compile stale JIT bodyRegister). Reproducible -> fix; not -> close as needs-reproducer.
