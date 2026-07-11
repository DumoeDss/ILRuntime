# Spec Delta - neo-optimizer

## Status: NO SPEC CHANGE

This change fixes two regressions in already-shipped Neo AOT code paths. It
does not introduce, modify, or remove any durable capability behavior that the
`openspec/specs/neo-optimizer/spec.md` canonical spec describes:

- The `.neo` serializer's `ReturnTypeRefIdx` field already exists (added by
  Step 25 S3-2). This change only fixes a cast bug in HOW the return IType is
  resolved to a Cecil TypeReference (handling `ILGenericParameterType`), not
  the field's contract or the format.
- The optimizer's register->byte-offset lowering is an internal
  implementation detail, not a spec'd capability surface. The fix makes an
  existing lowering helper defensively bounds-safe; no lowering contract
  changes.
- The Step-24 self-check test replication is a host-side DEBUG-only test
  fixture, not a capability.

The canonical spec `openspec/specs/neo-optimizer/spec.md` remains accurate as-
is. No `ADDED` / `MODIFIED` / `REMOVED` delta is needed.

(If a future change spec's the `.cctor`-in-MethodDefTable contract or the
return-type token resolution rules formally, that change should carry the
delta; this regression fix does not.)
