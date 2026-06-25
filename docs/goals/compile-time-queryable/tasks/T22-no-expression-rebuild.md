# T22 — *(out of scope)* No `Expression<T>` reimplementation

The compile-time queryable chain rewriter is not a replacement for
`System.Linq.Expressions.Expression<T>`. The two coexist; this feature
neither subsumes nor reimplements the existing expression-tree pipeline.

Cite — `out-of-scope`: *"Reimplementing System.Linq.Expressions.Expression<T> via this mechanism"*.

## Outcome

"Out of scope" here means a precise set of non-promises:

- Existing `Expression<T>` lambdas continue to flow through
  `ExpressionLambdaRewriter` unchanged. Conversions from a lambda to an
  `Expression<TDelegate>` target type still classify as
  `ConversionKind.AnonymousFunction` against a target whose
  `TypeSymbolExtensions.IsExpressionTree()` returns true, and lower in
  `ClosureConversion` exactly as they do today.
- The new mechanism coexists with `Expression<T>` rather than replacing
  it. A provider whose operators accept expression-tree predicates (the
  common LINQ shape, `Where(Expression<Func<T,bool>>)`) keeps doing so;
  the `[CompileTimeQueryable]` rewriter sees the *call site*, while the
  lambda argument continues to be quoted into an `Expression<T>` by the
  existing pipeline. The rewriter's input is the chain of `BoundCall`s,
  not the lambda bodies — those remain `Expression<T>` instances at
  runtime unless the rewriter's emitted substitution chooses otherwise.
- Conversions to `Expression<T>` are not routed through the new
  `IQueryableChainRewriter` pipeline. The chain-detection predicate
  (T06) is invocation-typed, keyed on the receiver's
  `[CompileTimeQueryable]` attribute; `Expression<T>` carries no such
  attribute and is not a receiver type, so no path through
  `Binder_Invocation` ever offers an `Expression<T>` conversion to a
  rewriter.
- `Expression<T>`-rooted chains are not in scope. A user expression like
  `Expression<Func<IQueryable<T>, IQueryable<T>>> q = src => src.Where(...)`
  remains an expression-tree, not a compile-time queryable chain.
  `Expression<T>` is a BCL type, not a provider-marked type — it has no
  `[CompileTimeQueryable]` annotation and is excluded from the predicate
  by construction (see Edge cases below for the defensive guard).

## Files to touch

None in the existing `ExpressionLambdaRewriter` path. Concretely, neither
`src/Compilers/CSharp/Portable/Lowering/ClosureConversion/ExpressionLambdaRewriter.cs`
nor its call sites in `ClosureConversion.cs` are modified. The
`WellKnownType.System_Linq_Expressions_Expression` / `Expression_T`
lookups stay where they are; the new rewriter pipeline does not consume
them.

The new code paths added by T01–T11 are gated on the presence of the
`[CompileTimeQueryable]` attribute on the receiver type's
`OriginalDefinition`. Because `Expression<T>` does not carry that
attribute, none of the new wiring fires for expression-tree-rooted
expressions, and `ExpressionLambdaRewriter` remains the sole owner of
the lambda → expression-tree lowering.

The language design spec (T17) should call out this coexistence under a
"Relationship to `System.Linq.Expressions.Expression<T>`" section, so
provider authors understand the two are orthogonal and that
quoted-lambda arguments are still expression trees at runtime.

## Dependencies

Guards T06 (chain detection). The receiver-type predicate in T06 must
NOT trigger on `Expression<TDelegate>` receivers — not even if someone
were to fork the BCL and add `[CompileTimeQueryable]` to `Expression<T>`.
The attribute-lookup helper `TryGetCompileTimeQueryableRewriterName` is
*authored* by T01 (it lives next to the `[CompileTimeQueryable]`
attribute-description lookup in `Symbols/TypeSymbolExtensions.cs`) and
*called* by T06; the short-circuit therefore lands inside T01's
implementation of that helper — not as a T06 follow-up. The
short-circuit: if the receiver type's `OriginalDefinition` is
`WellKnownType.System_Linq_Expressions_Expression_T` (or the non-generic
`System_Linq_Expressions_Expression`, or any type under the
`System.Linq.Expressions` BCL namespace), return false unconditionally.

Indirectly guards T05 (discovery) and T09 (rewriter invocation): with
the predicate refusing `Expression<T>`, neither runs against an
expression-tree receiver. No other tasks gain new responsibilities from
T22 — it is a scope exclusion whose enforcement point is named here so
T01 picks it up alongside the helper itself.

## Edge cases / risks

- **Chain rooted at a marked type that contains an `Expression<T>`
  mid-stream.** The canonical LINQ shape:
  `pgTable.Where(row => row.Age > 18).Select(row => row.Name)`. Each
  operator's signature takes `Expression<Func<T, …>>`, so the lambda
  arguments are still quoted into expression trees by the existing
  `ExpressionLambdaRewriter`. T06's predicate matches on the *receiver*
  (`Postgres9Table<T>`), not on the argument types, so the
  expression-tree arguments are transparent: at chain-capture time they
  are `BoundLambda` / `BoundUnboundLambda` nodes wrapped in a
  `BoundConversion` targeting `Expression<TDelegate>` inside the
  `BoundCall`s the rewriter receives, and they only become runtime
  `Expression<T>` instances after `ExpressionLambdaRewriter` lowers them
  in `ClosureConversion`. The rewriter may inspect them (T03's
  `RewriteContext` surfaces the raw bound tree) but does not replace the
  `Expression<T>` lowering — the substituted source it emits either
  reuses the expression-tree shape or generates provider-native code
  (e.g. SQL text), at the rewriter's discretion.
- **User marking `Expression<T>` itself.** A defensive guard is required.
  Without it, a user could ship a metadata-edited shim assembly that
  applies `[CompileTimeQueryable]` to `Expression<T>` and trick the
  binder into treating `Expression<T>`-returning members (e.g.
  `Expression<TDelegate>.Update(...)`, which returns
  `Expression<TDelegate>` and so satisfies T06's same-`OriginalDefinition`
  rule) as chain operators. The predicate should explicitly short-circuit
  when the receiver type's `OriginalDefinition` equals
  `WellKnownType.System_Linq_Expressions_Expression_T` or its non-generic
  base `WellKnownType.System_Linq_Expressions_Expression`. A broader
  check that names itself `IsBclExpressionTreeType` (or similar — there is
  no such helper in the codebase today; T01 introduces it) and rejects
  any type whose containing namespace is `System.Linq.Expressions` and
  whose containing assembly is one of the BCL identities is the stronger
  v1 fence and is recommended.
- **Migration story for code currently using `Expression<T>`.** None
  promised. Users opt into the new mechanism by authoring (or consuming)
  a *new* provider type marked with `[CompileTimeQueryable]`; existing
  code that targets `IQueryable<T>` with `Expression<T>` predicates is
  untouched and unaffected. There is no compatibility shim — the two
  pipelines run side by side, and a single source file can mix
  `IQueryable<T>`-based chains (runtime via `ExpressionLambdaRewriter`)
  with `[CompileTimeQueryable]`-marked chains (compile-time via the new
  rewriter) without interference.
- **Provider whose marked type wraps `IQueryable<T>` internally.** A
  provider whose `Postgres9Table<T>` happens to implement
  `IQueryable<T>` is fine: the marked receiver triggers the rewriter,
  and the rewriter may emit code that itself constructs `Expression<T>`s
  if that is the easiest way to talk to the underlying provider runtime.
  The boundary is "who owns the chain's binding-time shape" — T22 says
  it is *not* `ExpressionLambdaRewriter`, but the *runtime* code the
  rewriter emits is free to use `Expression<T>` if the provider needs
  it.

## Open questions

None.

## Landmarks

- **Existing `Expression<T>` rewriter:**
  `src/Compilers/CSharp/Portable/Lowering/ClosureConversion/ExpressionLambdaRewriter.cs`
  — owns the lambda → `Expression<TDelegate>` lowering. **Do not touch.**
- **BCL types excluded from the chain predicate:**
  `WellKnownType.System_Linq_Expressions_Expression` and
  `WellKnownType.System_Linq_Expressions_Expression_T` — short-circuited
  inside T01's `TryGetCompileTimeQueryableRewriterName` helper (called by
  T06) so that `Expression<T>`-typed receivers never produce a chain
  object.
