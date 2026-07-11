# neo-ldind-stind-byref-clr-struct

LATENT AV (child-8 unmasked): ldloca+ldflda+ldind.r4/stind.r4 on a CLR struct local AV-crashes in ExecuteNeo. Fix the byref-into-CLR-struct ldind/stind path. THROWING/probe.
