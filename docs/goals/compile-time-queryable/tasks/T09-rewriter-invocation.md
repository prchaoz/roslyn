# T09 — Invoke rewriter at the binding site

Once a chain is captured, invoke the rewriter at the chain's binding site with a `ChainRewriteContext`. Rewriter returns C# source text or `null`.

Cite — `rewriter-output`: *"(a) C# source text — Quotable-style substitution"*.

## Outcome

T09 is the orchestration seam that turns a captured chain (T06–T08) into a substituted bound expression (T10) or a silent fallback (T11). It is the queryable analog of `Binder_Conversions.CreateQuotationConversion` in the in-design `[Quotable]` plan — same try/catch shape, same conversion-kind rule, same diagnostic-fan-in pattern.

**When.** Immediately after T06 has identified the chain's terminator and T07 has finished dataflow capture — i.e. inside the binding phase, before any lowering pass. The invocation happens at the *terminator's* binding site: the `BindInvocationExpressionContinued` call that would otherwise construct the terminator's `BoundCall`, the `BindForEach` binding step over the chain, or the `BindAwait` binding step over an async terminator. The operator calls along the captured spine are already bound when the rewriter runs; the terminator's `BoundCall` is constructed only if the rewriter declines or fails (T11 fallback). The substituted expression replaces what the binder would otherwise have returned for the terminator.

**Failure semantics.** Three return shapes for `IQueryableChainRewriter.Rewrite(context)`:

| Rewriter behaviour | Compiler reaction |
|---|---|
| Returns non-null source string | Hand to T10 for parse + bind + substitute. |
| Returns `null` AND the rewriter reported zero diagnostics through `context`'s diagnostic reporter | T11 silent fallback — leave the original `BoundCall` spine in place, emit no diagnostic. |
| Returns `null` AND the rewriter reported one or more diagnostics through `context`'s reporter | Hard error path — rewriter explicitly declined with reasons; the reported diagnostics already point at the call site. No `ERR_ChainRewriterDeclined` in this branch. |
| Throws any exception | Catch all, emit `ERR_ChainRewriterThrew` at the chain location with exception type + message, fall through to T11 fallback with `hasErrors: true`. Containment is unconditional — we never let an analyzer-loaded rewriter take down the compiler. |

The "null with zero diagnostics → silent fallback" rule is the resolution of `decline-behavior` (option b). It mirrors the analogous `ERR_QuoterDeclined` rule from `[Quotable]` *inverted*: queryable chains have a runtime fallback to fall to (the marked type's normal `IQueryable`/`IEnumerable` plumbing), so silence is correct; `[Quotable]` has nowhere to fall and errors instead.

**Diagnostic surfacing.** Rewriter-reported diagnostics flow through `ChainRewriteContext.Diagnostics` (the T03 `IDiagnosticReporter`), which forwards `Diagnostic.Create(DiagnosticDescriptor, location, message)` calls into the active `BindingDiagnosticBag.DiagnosticBag`. Compiler-emitted codes (analog of `[Quotable]`'s 9391–9394 block):

| Code | When |
|---|---|
| `ERR_ChainRewriterNotFound` | Registry (T05) returned failure — wrong AQN, assembly not loaded, type does not implement `IQueryableChainRewriter`. Same `QuoterNotFound` analog. |
| `ERR_ChainRewriterReturnedInvalidSource` | T10 parse fails OR `ClassifyImplicitConversionFromExpression` returns `!Exists`. |
| `ERR_ChainRewriterThrew` | Rewriter's `Rewrite` method threw. |
| `ERR_ChainRewriterDeclined` | Reserved; currently unused under the silent-fallback policy. Keep allocated so the option to switch to a "loud decline" feature flag remains open. |

Each code must be listed under the `false` arm of `ErrorFacts.IsBuildOnlyDiagnostic` so it flows through `SemanticModel.GetDiagnostics`.

**Caching.** Per-chain syntactic identity. The key is the chain's *root* `SyntaxNode` (the terminator's `InvocationExpressionSyntax`, or the syntax node that anchors the captured spine), scoped to a single `CSharpCompilation` instance. The bound representation is not part of the key — the binder revisits the same chain syntax during `SemanticModel.AnalyzeDataFlow`, error-recovery, and IDE re-bindings, and the rewriter must not see a second invocation per syntactic site. Cache shape: `ConcurrentDictionary<SyntaxNode, ChainSubstitutionResult>` on the compilation, where `ChainSubstitutionResult` records `{source, diagnostics, declined, threw}`. Across edits, a fresh `CSharpCompilation` invalidates the cache — that's the correct granularity for IDE incrementality. The rewriter *instance* itself is preserved in `QueryableChainRewriterRegistry` (T05) across compilations and must be idempotent (mirrors the `[Quotable]` contract).

## Files to touch

- `src/Compilers/CSharp/Portable/Binder/Binder_Invocation.cs` — `BindInvocationExpressionContinued` (~line 1163). Before the terminator's `BoundCall` is constructed (~line 1390), branch into the chain-rewriter pipeline when (a) `IsCompileTimeQueryableEnabled` is true (T04 feature flag, queried via `CSharpCompilation.HasFeature(CodeAnalysis.Feature.CompileTimeQueryable)` — sibling of `FeatureStrictEnabled` at `CSharpCompilation.cs:251`), (b) `method` is a terminator (T08), and (c) the receiver's static type is `[CompileTimeQueryable]`-marked or transitively reached from such (T06). The terminator-detection check is operator-agnostic (T06) — it inspects the `[CompileTimeQueryable]` attribute on the *receiver/return* type, not the method name.
- `src/Compilers/CSharp/Portable/Binder/Binder_Conversions.cs` (~2897, ~3162). Add `CreateChainSubstitutionConversion` — direct analog of `CreateQuotationConversion` from the `[Quotable]` plan. This is the materialization helper invoked from `Binder_Invocation`. Responsibilities, in order:
  1. Resolve rewriter via `Compilation.QueryableChainRewriterRegistry.Resolve(rewriterAQN)` (T05).
  2. Construct `ChainRewriteContext` (T03) from the captured chain — syntax, scoped `SemanticModel`, ordered `ChainCall`s, in-chain `LambdaExpressionSyntax`s, captures, diagnostic reporter, fresh-name generator.
  3. Invoke `rewriter.Rewrite(context)` inside `try/catch (Exception ex)`.
  4. Hand the returned source to T10. **T10 owns** parse + bind + type-check against the chain's destination type (including the `Conversions.ClassifyImplicitConversionFromExpression` rule, the `bound.HasErrors`/`bound.Type is null` carve-outs, and `ERR_ChainRewriterReturnedInvalidSource` emission). T09 receives back a validated `BoundExpression` already convertible to the destination, or a sentinel that maps to "binding failed → T11 fallback".
  5. Wrap the validated `BoundExpression` in `BoundConversion` with **`Conversion.Identity`** — *not* `Conversion.AnonymousFunction` (see Edge cases). The operand already has the destination type from T10; identity is semantically correct and avoids every downstream `AnonymousFunction`-branching site that unconditionally casts the operand to `BoundLambda`.
- `src/Compilers/CSharp/Portable/CompileTimeQueryable/ChainRewriterInvoker.cs` — new. Holds the per-`CSharpCompilation` substitution cache and the orchestration logic shared between the invocation and conversion-materialization call sites.
- `src/Compilers/CSharp/Portable/Errors/ErrorCode.cs` — four new entries (`ERR_ChainRewriterNotFound`, `ERR_ChainRewriterReturnedInvalidSource`, `ERR_ChainRewriterThrew`, `ERR_ChainRewriterDeclined`).
- `src/Compilers/CSharp/Portable/CSharpResources.resx` + 14 `xlf/CSharpResources.*.xlf` files — autoregenerated; commit the diff.
- `src/Compilers/CSharp/Portable/Errors/ErrorFacts.cs` — `IsBuildOnlyDiagnostic` switch arm for all four codes.
- Coordination with T03: `ChainRewriteContext`'s constructor is the only legal way to populate it; T09 is the only call site.
- Coordination with T05: `QueryableChainRewriterRegistry.Resolve` is the only legal lookup; T09 caches by *chain* syntax, T05 caches by *rewriter* AQN — orthogonal caches at different granularities.

## Dependencies

- **Built on T01** (attribute drives the detection check), **T02** (interface defines the call), **T03** (context is the parameter), **T05** (registry resolves AQN → instance), **T06–T08** (capture pipeline produces the input).
- **Gated by T04** — when the feature flag is off, the new branch in `BindInvocationExpressionContinued` is never taken; behaviour is identical to today's compiler.
- **Feeds T10** (consumes the returned source) and **T11** (consumes the `null`-with-zero-diagnostics signal).
- **Pending T13** — branched-chain handling changes whether one rewriter invocation handles a DAG with shared prefixes or each terminated path is its own invocation. The cache shape (currently a single `SyntaxNode` key) and the `ChainRewriteContext`'s `ChainCalls` shape change with T13.
- **Pending T12** (`IAsyncEnumerable` scope) — determines whether `await ToArrayAsync` and `await foreach` terminators reach this path with the same context shape or whether a parallel async path is needed.
- **Independent of T20** — the `[Quotable]` analog informs the design but does not share code (T20 explicitly rejects shared plumbing).

## Edge cases / risks

- **Rewriter throws.** Unconditional `try/catch (Exception)` containment, `ERR_ChainRewriterThrew` with type + message, fall back via T11. Documented contract: rewriter authors must surface their own diagnostics through `context.Diagnostics` rather than throwing.
- **Rewriter returns syntactically valid but semantically wrong source.** Mitigation lives in T10 (which owns parse + bind + type-check). T09's only obligation is to honour T10's verdict — if T10 reports a type-mismatch, fall through to T11 fallback without wrapping in `Conversion.Identity`. Without T10's classifier rule, a rewriter returning `42` for an `IQueryable<X>`-typed chain would silently miscompile at emit (P1 finding on the `[Quotable]` plan).
- **Re-entrancy.** If a rewriter's emitted source contains another `[CompileTimeQueryable]` chain, T09 will be invoked recursively when T10 binds the substituted source. Track active chain `SyntaxNode` identities on a thread-local stack; if the same node re-enters, emit a diagnostic and fall back. Note this is a sharper issue than `[Quotable]`'s `AnalyzeDataFlow` re-entry, since rewriter output is itself binder input.
- **`Conversion.Identity` not `Conversion.AnonymousFunction`.** `ClosureConversion.VisitConversion` unconditionally executes `(BoundLambda)conversion.Operand` when `conversion.ConversionKind == ConversionKind.AnonymousFunction`. Substituted operands are `BoundObjectCreationExpression`, `BoundCall`, etc. — never `BoundLambda` — so the cast `InvalidCastException`s. Same trap in `DiagnosticsPass_ExpressionTrees`, `SpillSequenceSpiller`, `LocalRewriter_Conversion`, `CSharpSemanticModel`. The fix is to wrap with `Conversion.Identity` — semantically correct because T10 validates the operand already has the destination type.
- **`InMethodBinder.IdentifierMap` (DEBUG predictor).** T10's parse of the rewriter source produces `IdentifierNameSyntax` nodes the predictor never saw. Binding them sets bit 2 only at the identifier-binding path in `Binder_Expressions.cs`, and `MethodCompiler.assertBindIdentifierTargets` trips `Debug.Assert(false)`. (Exact line numbers from the `[Quotable]` plan are stale against current `main`; verify against `MethodCompiler.cs` at implementation time.) Mitigation (under `#if DEBUG`): walk the binder chain to the owning `InMethodBinder`, null `IdentifierMap` around the bind call, restore in `finally`.
- **Performance.** A solution with N chain sites incurs N rewriter invocations — modulo the cache. The per-compilation `SyntaxNode → ChainSubstitutionResult` cache is the only thing standing between repeated `SemanticModel.AnalyzeDataFlow` calls and 10× rewriter work. Cache keys MUST be `SyntaxNode` reference identity, not `SyntaxNode.Span` (incremental parses give new nodes for unchanged text → behaves correctly: new node → new entry, and the old result is GC'd with the old `Compilation`).
- **IDE incrementality.** Each typing edit produces a new `CSharpCompilation`, which invalidates the cache. That is the correct granularity — the chain shape may have changed even if the trivia didn't. The rewriter *instance* (cached on `QueryableChainRewriterRegistry`, T05) is preserved across edits; the rewriter must therefore be idempotent and stateless across invocations. Mirror the `[Quotable]` contract: forbid `static` mutable state in rewriters; document explicitly.
- **`SemanticModel` re-entry.** Public `SemanticModel` APIs (`GetSymbolInfo`, `AnalyzeDataFlow`) re-bind the chain via `MemberSemanticModel`, ignoring the bound-tree cache (same pattern as `UnboundLambda._bindingCache` in `[Quotable]`'s Step 5). The substitution cache must be consulted from `MemberSemanticModel`'s binding path too, otherwise IDE consumers double-invoke the rewriter per query.
- **Concurrent compilations.** `QueryableChainRewriterRegistry` is shared via the analyzer assembly cache (T05); two `CSharpCompilation`s built concurrently from the same project (e.g. design-time vs background) may invoke the same rewriter instance simultaneously. The substitution cache is per-compilation and unaffected; rewriter idempotency carries the rest.

## Open questions

- **T13 (branched chains).** Determines whether one rewriter invocation handles a DAG (with shared prefixes visible to the rewriter) or each terminated path is its own invocation with overlapping context. The substitution cache key (currently `SyntaxNode`) and the `ChainRewriteContext.ChainCalls` shape (currently `ImmutableArray<ChainCall>`) both change with this decision. Surface only; resolve in T13.
