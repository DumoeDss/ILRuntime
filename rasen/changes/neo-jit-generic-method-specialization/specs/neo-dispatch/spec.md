# neo-dispatch delta: neo-jit-generic-method-specialization

## ADDED Requirement: Generic-method template re-specializes T-qualified call tokens

A Neo generic-method template (`GenericMethodTemplate`) SHALL record a
`MethodToken` patch for every T-qualified Call/Callvirt site in the template body
(a call whose Cecil method token contains a method-generic-parameter T, e.g.
`LoadAsset<T>` called from inside a generic method `G<T>`), so that
`DoCloneAndPatch` re-resolves the call's method hash (`Operand2`) for each concrete
instantiation. This SHALL cover `Call`, `Callvirt`, `Callvirt_IL`, `Callvirt_CLR`,
and `Call_Redirect`.

Rationale: without such a patch, `HasIdentityToken()` returns false for a body whose
only T-identity site is a T-qualified call, so `TryInstantiate` wrongfully ref-shares
one instantiation's body across all all-reference-type-arg instantiations, baking the
capture-T call target into every instance and dispatching the wrong CLR redirect
(`LoadAsset<TestCLRBinding>` served for `<String>`/`<Int32>` callers).

#### Scenario: T-qualified generic-method call dispatches the correct instantiation

- **WHEN** a generic IL method `G<T>` containing a call to a generic method
  `M<T>` (on a CLR or IL type) is instantiated for two different reference-type T
  arguments in the same run
- **THEN** each instantiation SHALL dispatch `M<concreteT>` (not the capture-T
  `M`), verified by the correct per-T CLR redirect/behavior

#### Scenario: Reliability cross-check skips inliner-scrambled symbols

- **WHEN** a generic IL method's body has been modified by inlining (an inlined
  call removed but its Cecil symbol left linked at a body index now holding a
  different call) AND a recorded MethodToken patch's Cecil token resolves to a
  method whose Name differs from the body op's current method
- **THEN** `DoCloneAndPatch` SHALL skip that patch (preserve the capture-T hash)
  rather than corrupt the unrelated call's `Operand2`
- AND the body SHALL still compile and execute correctly (identical to the
  token-free ref-share semantics for that call)

#### Scenario: Constrained trailing callvirt not double-patched

- **WHEN** a T-qualified call is the trailing callvirt of a `constrained.` prefix
- **THEN** the Constrained-case patch (sourced from the pre-captured Cecil pair)
  SHALL own that call's `Operand2`, and the Call-case extractor SHALL skip it
