# neo-generic-redirect-resolution

B1: resolve the 2-generic-arg redirect (AwaitUnsafeOnCompleted<TA,TSM>) on RedirectMapNeo via a deterministic TaskCompletionSource probe; then re-attempt the async suspend machinery (Phase 2). Absorbs the disproven controlflow-iscompleted investigation.
