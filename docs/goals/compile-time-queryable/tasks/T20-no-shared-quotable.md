# T20 — *(out of scope)* No shared plumbing with `[Quotable]`

Keep code paths separate from the existing `[Quotable]` feature.

Cite — `out-of-scope`: *"Shared plumbing with the [Quotable] feature"*.

## Outcome

The two features mirror each other in *architecture* but share no *code*. Concretely, after this feature lands the repo contains two parallel surface areas with disjoint type identities:

| Concept | `[Quotable]` (existing) | `compile-time-queryable` (new) |
|---|---|---|
| Trigger attribute | `[Quotable(string quoterAQN)]` | `[CompileTimeQueryable(string rewriterAQN)]` (T01) |
| Rewriter interface | `IExpressionQuoter.Quote(QuotationContext)` | `IQueryableChainRewriter.Rewrite(ChainRewriteContext)` (T02) |
| Per-invocation context | `QuotationContext` | `ChainRewriteContext` (T03) |
| Compiler feature flag | `/features:custom-expression-trees` | `/features:compile-time-queryable` (T04) |
| Internal registry | `QuoterRegistry` | `QueryableChainRewriterRegistry` (T05) |
| Analyzer-discovery hook | `CommonCompiler.PopulateLanguageSpecificRuntimeState` override populates `QuoterRegistry` | Same hook (it is a generic `CommonCompiler` extension point); a **separate per-feature walk** inside the override resolves `[CompileTimeQueryable]` AQNs and populates `QueryableChainRewriterRegistry` (T05) |
| Diagnostic codes | `ERR_QuoterNotFound`/`Threw`/`Declined`/`ReturnedInvalidSource` | Distinct `ERR_QueryableChainRewriter*` codes — not reused |

Each feature is fully functional when the other's flag is off. Turning on `compile-time-queryable` does not require the `custom-expression-trees` flag, and vice versa. A consumer that ships rewriters for both features writes two attributes, two analyzer types, and two implementations — by design.

## Files to touch

This task touches no production code on its own. Its job is to constrain T05 (discovery), T09 (invocation), and T10 (substitution) so they do **not** extract a shared interface, base class, or helper out of the existing Quotable code under `src/Compilers/CSharp/Portable/Quotation/`:

- T05's registry lives in a new `src/Compilers/CSharp/Portable/QueryableChain/` subtree (mirroring `Quotation/`'s layout) — copy the resolution mechanics (AQN split, `Assembly.GetType(typeName)`, `TargetInvocationException` unwrap), do not call into `QuoterRegistry` or factor a `RewriterRegistry<TInterface>` base.
- T09's invocation harness uses its own `try/catch`, its own decline/throw error codes, and its own `BindingDiagnosticReporter`-shaped reporter — do not generalize `Quotation/BindingDiagnosticReporter.cs` to a two-feature helper.
- T10's substitution reuses the *technique* documented in the Quotable plan Step 5 (suspending `InMethodBinder.IdentifierMap` under `#if DEBUG`, wrapping with `Conversion.Identity`, validating via `ClassifyImplicitConversionFromExpression`), but each call site does the work itself; do not move the helper into a shared file.
- If an implementer notices overlap during T05/T09/T10, the resolution is to **file a follow-up issue and stop** — not refactor in-batch. Cross-feature refactors are explicitly post-v1.

## Dependencies

- This task **guards T01–T11** — every step in the inner pipeline (attribute, interface, context, flag, discovery, chain detection, dataflow capture, terminators, invocation, substitution, fallback) must respect the no-share rule. T20 has no implementation deliverable of its own; it's enforced by code review on those tasks.
- Out-of-scope siblings (T19 VB, T21 IDE, T22 `Expression<T>` rebuild) are orthogonal.
- T17 (spec) must state the no-share rule explicitly so downstream readers know the parallelism is deliberate.

## Edge cases / risks

- **Tempting reuses to refuse.** The strongest pull is to share `CapturedVariable` and `IDiagnosticReporter` — both are tiny POCOs that look identical across the two features. Resist: they live in different namespaces (`Microsoft.CodeAnalysis.CSharp.Quotation` vs the new `Microsoft.CodeAnalysis.CSharp.QueryableChain` per T17) and a Quotable-side change to either type would silently reshape the queryable-chain contract. The duplication is the point.
- **Namespace collisions.** Picking `Microsoft.CodeAnalysis.CSharp.Quotation` for the new types — or letting a "shared" `Microsoft.CodeAnalysis.CSharp.Rewriting` namespace emerge — defeats T20. The new types must sit in a sibling namespace decided by T17; both surfaces stay separately experimental (Quotable reserved `RSEXPERIMENTAL008`, so this feature should claim the next free id and not piggyback).
- **Bound-tree walker reuse.** Quotable's `QuotationCaptureWalker` (`src/Compilers/CSharp/Portable/Quotation/QuotationCaptureWalker.cs`) is the obvious copy-target for T07's dataflow capture. Copy the *pattern* (a `BoundTreeWalker` with the ~10 visit overrides catalogued in the Quotable plan Step 5: `Block`, `ForEach`, `Using`, `For`, `Catch`, `Switch`, `SwitchSection`, `SwitchExpressionArm`, `LocalFunction`, and skipping `nameof`) into a new `QueryableChain/QueryableChainCaptureWalker.cs`. Do not subclass, do not factor out a `BoundTreeCaptureWalkerBase`. The walker's semantics for queryable-chain capture (per T07: simple intra-method local-variable dataflow stopping at terminators) will diverge from Quotable's lambda-body walk; pre-emptively sharing locks both features into one set of override rules.
- **Diagnostic-pipeline reuse.** Each feature owns its `ERR_*` range; reusing `ERR_QuoterNotFound` for a missing chain rewriter would conflate the two surfaces in user-facing messages and break independent suppression with `#pragma warning disable`. Allocate fresh codes during T09/T11.
- **Spec ambiguity.** T17 must spell out that the two features are independent products that happen to share an analyzer-loading mechanism. Without that, future contributors will read the symmetry as accidental and "clean up" by unifying.
- **Future v2 unification.** A `Microsoft.CodeAnalysis.CSharp.Rewriting` umbrella that abstracts both quoters and chain rewriters is a defensible follow-up after both features ship and stabilize. v1 explicitly forecloses it — the cost of getting the wrong abstraction wired through binding is higher than the cost of duplicating five small files.

## Open questions

None.
