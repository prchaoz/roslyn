# T02 — Add `IQueryableChainRewriter` interface

Add `IQueryableChainRewriter` with `string? Rewrite(ChainRewriteContext context)`.

Cite — `rewriter-output`: *"(a) C# source text — Quotable-style substitution"*.

## Outcome

A public interface `IQueryableChainRewriter` lives Roslyn-side (mirrors Quotable's `Microsoft.CodeAnalysis.CSharp.Quotation.IExpressionQuoter`), marked `[Experimental("RSEXPERIMENTAL00N")]`. Single member:

```csharp
string? Rewrite(ChainRewriteContext context);
```

Return semantics, externally visible:

- Non-null — parsed via `SyntaxFactory.ParseExpression`, type-validated against the chain's destination type, and substituted at the chain site per T10.
- `null` — the rewriter declined; chain falls back to runtime evaluation per T11. The implementer is expected to have reported diagnostics first via `context.Diagnostics`; if `null` is returned with zero reports, the compiler emits a single decline diagnostic at the chain site (mirrors `ERR_QuoterDeclined`).
- Instances are constructed once per compilation via `Activator.CreateInstance` against the AQN in `[CompileTimeQueryable(...)]` (T01); the registry in T05 caches them. Implementers must therefore be effectively stateless across chain sites.

## Files to touch

- **New:** `src/Compilers/CSharp/Portable/QueryableChain/IQueryableChainRewriter.cs` (subdirectory chosen to mirror Quotable's `Quotation/`; T05's registry lives next to it).
- **PublicAPI:** `src/Compilers/CSharp/Portable/PublicAPI.Unshipped.txt` — list every member explicitly. `[Experimental]` does **not** auto-suppress PublicAPI tracking (Quotable plan, Step 1).
- **Auto-updated by build:** `src/Tools/SemanticSearch/ReferenceAssemblies/Apis/Microsoft.CodeAnalysis.CSharp.txt` — commit the diff.
- Allocate the next free `RSEXPERIMENTAL00N` id (Quotable reserves `008`).
- Internal Roslyn consumers (T05 registry, T09 invocation harness) need `#pragma warning disable RSEXPERIMENTAL00N` at file top.

## Dependencies

- **Depends on T03** — `ChainRewriteContext` must exist to type the parameter; do not merge T02 before T03's shape lands.
- **Blocks T05** (discovery dispatches against this contract), **T09** (invocation calls `Rewrite`), **T10** (substitution consumes the returned string), **T11** (`null`-return is the decline signal).
- **Soft-depends on T04** — the interface is unconditional, but call sites only invoke `Rewrite` when `CSharpCompilation.HasFeature("compile-time-queryable")` is set.
- **Independent of Quotable** — per T20, no shared types with `IExpressionQuoter` even though the resolution mechanics are copied.

## Edge cases / risks

- **User exception inside `Rewrite`** must be caught at the call site and surfaced as a compiler diagnostic, not propagated (mirrors `ERR_QuoterThrew`). `Activator.CreateInstance` exception wrapping (`TargetInvocationException`) must be unwrapped to `InnerException` per Quotable plan Step 3.
- **Returned source with parse errors** must be rejected by the binder (T10) with the parse messages concatenated into a single diagnostic; the interface contract does not enforce well-formedness.
- **Type-mismatch on returned source** — even a syntactically valid string that binds to the wrong type must be rejected via `Conversions.ClassifyImplicitConversionFromExpression` before identity-wrap. The interface XML doc should warn implementers, but enforcement is in T09/T10.
- **Implicit statefulness** — registry caches one instance per AQN per compilation, so a `Rewrite` that mutates instance state can let two chain sites interfere. The XML doc must require effective statelessness; revisit if T13 picks a model that legitimately needs per-DAG scratch state.
- **`[Experimental]` opt-in surface** — `[CompileTimeQueryable]` (T01) references the rewriter only by string AQN, so it does not transitively trigger the experimental warning. Implementers writing `class MyRewriter : IQueryableChainRewriter` get the warning on the interface; downstream NuGet authors will need to suppress it in their analyzer projects. Worth a one-line callout in T17.

## Open questions

- **T13 (branched chains) reshapes what `Rewrite` is asked to translate.** If T13 picks per-terminator linear chains, this signature is unchanged. If it picks whole-DAG, `string?` still suffices (the rewriter emits one fragment representing the DAG, IIFE-shaped if needed) but `ChainRewriteContext` becomes tree-shaped — see T03. If T13 picks linear-only v1, any branching triggers T11 fallback before `Rewrite` is ever invoked. The interface signature does not change either way; the implementer's contract does. Lock the XML doc once T13 resolves.
- **T12 (`IAsyncEnumerable<T>` scope)** — async-scoped chains change the set of legal output shapes (the rewriter may need to emit an `async` lambda or `await foreach` form). The interface signature is unaffected; document the allowed shapes in T17 once T12 resolves.
- **T14 (BCL helpers scope)** — if BCL ships rewriter-author helpers, the interface may need to move from `Microsoft.CodeAnalysis.CSharp.*` to `System.Runtime.CompilerServices` so BCL helpers can reference it without taking a Roslyn dependency. Current plan keeps it Roslyn-side (mirrors `IExpressionQuoter`); revisit when T14 lands.
- **Namespace name** — `Microsoft.CodeAnalysis.CSharp.QueryableChain` vs `…CompileTimeQueryable`. Defer to T17 spec authoring.
