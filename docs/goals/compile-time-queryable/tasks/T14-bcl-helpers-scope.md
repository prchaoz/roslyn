# T14 — *(unresolved)* BCL helpers scope

Cite — `out-of-scope`: `bcl-helpers` *was not selected* in the out-of-scope set; absence may be intentional (in scope) or oversight (out of scope). The sibling `[Quotable]` v1 project explicitly excluded BCL helpers (custom-expression-trees/facts.md line 65); a deliberate divergence here would warrant a note in the answer, so the more likely reading is "skipped, not implicitly in".

## What the question is

Beyond the user-visible attribute and the analyzer-side rewriter interfaces, should v1 ship any *runtime-side* convenience surface that rewriter authors can lean on? The candidates a rewriter author would plausibly want, sorted from cheap to ambitious:

- **String/identifier mangling.** Helpers for projection-name escaping, fresh-name generation that is stable across compilations, and operator-name to SQL-keyword maps. Useful but tiny.
- **A typed materializer-cache hook.** A static `MaterializerRegistry<TKey, TFactory>` so multiple rewriters can share generated `Func<DbDataReader, T>` delegates across call sites in one process.
- **A "decline reason" enum / structured result.** Shared vocabulary for the `string? Rewrite(...)` decline path (T11) — versus today's "any null means decline".
- **A generic visitor base class for the bound-call sequence** the rewriter sees (`BoundCall` walk, projection-expression walk, lambda-body walk). The closest analogue is Quotable's hypothetical `ExpressionVisitor<TState>`.
- **A SQL-fragment builder** (parameter-collecting, dialect-agnostic). The Postgres sample (T15) needs one; the question is whether `System.Runtime.QueryFragment` or similar belongs in BCL.

Note: the rewriter interface itself (`IQueryableChainRewriter`, `ChainRewriteContext`, `IDiagnosticReporter`) is *not* the BCL surface in question — those live Roslyn-side because they reference Roslyn types (`SemanticModel`, `ITypeSymbol`, `SyntaxNode`). The sibling project handles this split for Quotable: only `[Quotable]` ships in `dotnet/runtime`; `IExpressionQuoter` + context types live under `Microsoft.CodeAnalysis.CSharp.Quotation` (custom-expression-trees/plan.md step 1). T14 asks about the *third* layer — runtime helpers that *user-emitted* substituted code calls into.

## Options

### A — In-scope for v1

Ship one or more helper assemblies. Two sub-shapes:

- **A.1 — Folded into `System.Runtime.CompilerServices`.** Same assembly as `[CompileTimeQueryable]`. Cheap to discover, but pollutes a heavily-used namespace and ties helper churn to `System.Runtime`'s freeze cadence.
- **A.2 — New assembly `System.Runtime.QueryableRewriting` (or `Microsoft.Extensions.CompileTimeQueryable`).** Isolates the API surface; opts users in via package reference. Higher up-front cost (new package, new BCL board review, separate versioning) but matches how other extension surfaces (`Microsoft.Extensions.*`) ship.

Either way the well-known-type registration in Roslyn (`WellKnownTypes.cs`, `WellKnownMember.cs`, `WellKnownMembers.cs`, `AttributeDescription.cs`, `MissingSpecialMember.cs` — both lists; cf. custom-expression-trees/plan.md step 1) grows by one entry per helper that the compiler must recognise. Most helpers are pure runtime, so the Roslyn touch is small; the cost is the BCL PR and the public-API freeze.

### B — Out of scope for v1

Ship only the attribute (`[CompileTimeQueryable]`, T01) plus the analyzer-facing interfaces in Roslyn (T02, T03). Rewriter authors hand-write any runtime helpers their emitted source needs and ship them alongside their NuGet package. This matches Quotable v1 exactly (facts.md: *"BCL-shipped runtime-compilation helpers, generic tree-visitor base classes, or other 'do-it-for-you' libraries on top of the new mechanism. Quoter authors supply their own runtime types."*) and is the conservative default.

### C — Hybrid (interface-only)

Ship a *minimal abstraction* in BCL — for example an empty `IQueryableMaterializer` marker, or an `IRewriterDiagnostic` shape that the rewriter-emitted code throws on `Decline` — but no concrete helpers. Gives downstream libraries a shared type to bind against without committing the BCL to specific algorithms. Pays most of the versioning cost of A with little of the convenience.

## Recommendation

DO NOT pick. The right answer depends on a constraint the interview did not pin down: whether the team wants the Postgres sample (T15) to depend on a hand-rolled helper that lives under `src/Samples/` (option B) or on a BCL/Microsoft.Extensions package this PR also delivers (option A). If T20 ("no shared plumbing with `[Quotable]`") is read strictly, the matching choice here is option B (Quotable shipped nothing; this one shouldn't either). If T20 only forbids *code path* sharing and the team is willing to ship parallel-but-similar helper surfaces for the two features, A or C are open.

## Files to touch under each option

- **Option A** (any sub-shape):
  - BCL declarations in `dotnet/runtime` (separate PR, not this repo).
  - Roslyn-side well-known-type registration: `src/Compilers/Core/Portable/WellKnownTypes.cs`, `src/Compilers/Core/Portable/WellKnownMember.cs`, `src/Compilers/Core/Portable/WellKnownMembers.cs`, `src/Compilers/Core/Portable/Symbols/Attributes/AttributeDescription.cs`, and the test-side cross-check in `src/Compilers/CSharp/Test/Symbol/Symbols/MissingSpecialMember.cs` (both the `WellKnownType` switch near line 660 and the `WellKnownMember` switch near line 1106 — plan step 1 calls this out as a common omission that trips `AllWellKnownTypeMembers`).
  - PublicAPI tracking for any Roslyn-visible surface: `src/Compilers/Core/Portable/PublicAPI.Unshipped.txt`, `src/Compilers/CSharp/Portable/PublicAPI.Unshipped.txt`, all behind `[RSEXPERIMENTAL00N]` (next free ID per `src/Compilers/Core/Portable/InternalUtilities/RoslynExperiments.cs`; current head is 007).
  - Reference-assembly snapshot regenerated: `src/Tools/SemanticSearch/ReferenceAssemblies/Apis/Microsoft.CodeAnalysis.CSharp.txt`.
- **Option B**: no BCL or Roslyn well-known-type churn beyond T01's `[CompileTimeQueryable]`. Helpers, if needed, ship in the canonical sample (T15) under `src/Samples/` and are not part of the compiler contract.
- **Option C**: identical Roslyn touch to A (interfaces still need well-known-type entries if the compiler must recognise them); BCL surface area is smaller — typically one or two interfaces, no concrete classes.

## Edge cases / risks

- **Versioning coupling.** BCL ships with the runtime; the compiler ships independently (via the .NET SDK and as `Microsoft.Net.Compilers.Toolset`). A BCL helper added in `System.Runtime` 11.0 is unusable from a project targeting `net10.0` even if the user upgrades the compiler — and unlike the attribute, helpers are called at runtime, so polyfilling them is non-trivial. Option A.2 (separate package) sidesteps this but creates a second axis of version compatibility (helper-package version * compiler version * rewriter-NuGet version).
- **Helper APIs becoming a de-facto contract.** Once a rewriter author writes `return $"new {nameof(QueryFragment)}({builder.ToString()})";`, removing or renaming `QueryFragment` is a breaking change for every rewriter shipping in the wild — even though the helper was nominally "for sample/convenience use". Quotable v1 chose B partly to avoid this trap.
- **Reverse pressure on the rewriter interface.** If we ship a `QueryFragment` builder, the `IQueryableChainRewriter.Rewrite` signature is tempted to grow a `QueryFragment` overload returning a typed value instead of `string?`. That contradicts T02's "source-text only" answer (`rewriter-output: source-text`). Picking A makes the T02 contract harder to keep narrow.
- **Symmetry with Quotable (T20).** T20 forbids *shared plumbing* with `[Quotable]`. It does not, strictly, forbid this feature shipping a runtime helper surface that Quotable lacks — but a reviewer reading T20 will reasonably ask whether the asymmetry is intentional. If A is chosen, expect to defend why Quotable shouldn't get the same treatment (and possibly re-open that decision upstream).
- **Empty helpers as a future tax.** Option C ships a marker interface in BCL with no implementations. If v2 wants to add methods, the interface needs DIM or a parallel interface — the very paper cut that motivates picking B today.
- **Discovery vs. analyzer assemblies.** Helpers consumed at *runtime* by emitted code are simple. Helpers a *rewriter author* uses at *compile time* (e.g., a base class for walking `BoundCall` sequences) belong on the *Roslyn* side, not BCL, because they reference `BoundNode`/`SyntaxNode`. If the question is read to include these, option B has a third sub-answer: ship them under `Microsoft.CodeAnalysis.CSharp.QueryableRewriting` next to the rewriter interfaces, mirroring `Microsoft.CodeAnalysis.CSharp.Quotation` from the sibling plan.
