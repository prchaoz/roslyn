# T04 — Feature flag `/features:compile-time-queryable`

Gate the entire mechanism behind `/features:compile-time-queryable`. With the flag off, behavior is identical to today's compiler.

Cite — `feature-flag`: *"compile-time-queryable"*.

## Outcome

A single `/features:compile-time-queryable` switch turns the rewriter on. "Done" means the constant exists in `Feature.cs`, a single-expression query helper exists on `CSharpCompilation`, and every downstream gate (T05/T06/T09/T10) consults it. With the flag absent, `CSharpCompilation.GetDiagnostics()`, emit output, IDE semantic queries, and analyzer-visible bound trees are byte-for-byte identical to the same compilation built against a compiler that never knew the feature.

Concretely:

- **Feature constant.** `internal const string CompileTimeQueryable = "compile-time-queryable";` added to `Microsoft.CodeAnalysis.Feature`. The string literal matches the spelling the user types on the command line; `Feature.AssertValidFeature` (DEBUG-only reflection scan) picks it up automatically.
- **Compilation query helper.** `internal bool IsCompileTimeQueryableEnabled => HasFeature(CodeAnalysis.Feature.CompileTimeQueryable);` added to `CSharpCompilation`. Name and shape mirror the Quotable precedent (`IsCustomExpressionTreesEnabled`) and the existing one-liner pattern at `CSharpCompilation.cs:251–265` (`FeatureStrictEnabled`, `IsPeVerifyCompatEnabled`, `FeatureDisableLengthBasedSwitch`).

No `MessageID`, no `LanguageVersion` entry, no `Required` / `LangVersion` checks — this is a `/features:` flag, not a versioned language feature (cf. Quotable plan §Step 2 explicitly correcting that mistake).

## Files to touch

- `src/Compilers/Core/Portable/CommandLine/Feature.cs` — add one `internal const string CompileTimeQueryable = "compile-time-queryable";` alongside the existing flag constants. No other change; `AssertValidFeature` reflects over `BindingFlags.NonPublic | BindingFlags.Static` literals, so the new constant is automatically validated.
- `src/Compilers/CSharp/Portable/Compilation/CSharpCompilation.cs` — add the `IsCompileTimeQueryableEnabled` expression-bodied property in the cluster around line 251–265, with an XML doc comment matching the local style (one sentence describing the flag, one sentence describing the effect when set).

That is the entire surface area of this task. Every other touch point — discovery (T05), chain detection (T06), invocation (T09), substitution (T10) — reads `compilation.IsCompileTimeQueryableEnabled` at the head of its entry point and short-circuits to the legacy code path when false.

## Dependencies

- **Gated by this task:** T05 (analyzer-driven rewriter discovery — must not walk analyzer assemblies when the flag is off), T06 (chain detection in the binder — must not classify provider-marked receivers when the flag is off), T09 (rewriter invocation — never call `IQueryableChainRewriter.Rewrite` when the flag is off), T10 (source-text substitution — emit must produce the original bound call tree when the flag is off).
- **Independent of this task but must remain inert without it:** T01 (`[CompileTimeQueryable]` attribute in `System.Runtime.CompilerServices`), T02 (`IQueryableChainRewriter` interface), T03 (`ChainRewriteContext`). These land BCL/abstraction surface area only; with the flag off the binder never inspects the attribute, never resolves the rewriter, and never constructs a `ChainRewriteContext`. The attribute is observable via reflection and `GetAttributes()` (that is unavoidable), but it has no semantic effect.
- **Not gated by this task:** T19–T22 (out-of-scope statements), T16 (compiler tests — but the test harness needs a helper to flip the flag on for in-feature tests; see edge cases), T17 (spec doc — must document the flag spelling and the "off → identical behavior" contract).

## Edge cases / risks

- **Analyzer authoring against a flagged-off compiler.** Customer-authored rewriter assemblies still load as analyzers regardless of the flag (the analyzer loader does not know about `/features:`). Discovery in T05 must early-return on `!IsCompileTimeQueryableEnabled` *before* enumerating analyzer assemblies — otherwise a typo'd or non-rewriter analyzer assembly could surface diagnostics or perf cost on every build that happens to reference the package. The cost of an opted-out build must be zero `Assembly.GetTypes()` calls on the analyzer set.
- **`SemanticModel` API contract while disabled.** A chain like `table.Where(...).ToList()` must, with the flag off, produce identical `SymbolInfo`, `TypeInfo`, `GetOperation`, and `GetSpeculativeXxx` results as a compiler that never had the feature. Concretely: the binder must not synthesize a wrapped-method symbol, must not rewrite the receiver, and must not emit any compile-time-queryable-specific diagnostics. The gate at the head of T06's chain-detection routine is the single point of compliance — guard it with a test that compares `GetOperation` trees with the flag off vs. a control compilation (T16).
- **Serializable `<Features>` plumbing in `.csproj`.** `/features:compile-time-queryable` ends up in `CSharpParseOptions.Features` (a per-tree dictionary aggregated by `Compilation.SyntaxTreeCommonFeatures`). MSBuild surfaces this via `<Features>compile-time-queryable;$(Features)</Features>` (the single semicolon-separated property used by `Microsoft.Managed.Core.targets`; cf. `run-nullable-analysis=never` at line 106). The spec doc (T17) must call out the exact spelling — typos silently disable the feature because the parsed dictionary is built with `ImmutableDictionary.CreateBuilder<string, string>()` (ordinal, case-sensitive comparer) and `HasFeature` does a plain `TryGetValue`. Document both the command-line form (`/features:compile-time-queryable`) and the MSBuild `<Features>` form.
- **Interaction with `LanguageVersion`.** This flag is intentionally orthogonal to `LangVersion`. It does not require any specific language version, does not bump `LanguageVersion.Latest`, and does not emit `ERR_FeatureNotAvailableInVersionN`-style "feature is not available in C# N" diagnostics. Rationale: rewriter output is plain C# source text routed back through the existing binder; the language surface is unchanged. If a future iteration wants version-gating, that becomes a separate `MessageID` + `CheckFeatureAvailability` change layered on top.
- **Per-compilation immutability.** `_features` is built once in the `Compilation` base constructor (`Compilation.cs:94`) from `SyntaxTreeCommonFeatures(trees)`. It is immutable for the lifetime of a given `CSharpCompilation` instance; `AddSyntaxTrees` / `RemoveSyntaxTrees` produce a new `Compilation` with a freshly recomputed `_features`. The expression-bodied helper therefore costs only a dictionary `TryGetValue` per access, which matches the cluster style (`FeatureStrictEnabled`, `FeatureDisableLengthBasedSwitch`); a stored bool would also be safe but is unnecessary.
- **Public API surface.** `Feature.cs` is `internal` and the new property is `internal`. No `PublicAPI.Unshipped.txt` entry is required for this task. (T01's `[CompileTimeQueryable]` attribute lives in `System.Runtime.CompilerServices` — a BCL namespace exposed via Roslyn's `WellKnownType` mechanism, not a Roslyn public type — so T01 too need not touch `PublicAPI.Unshipped.txt`; the auto-updated `src/Tools/SemanticSearch/ReferenceAssemblies/Apis/Microsoft.CodeAnalysis.CSharp.txt` covers reference-assembly surface there.)

## Open questions

None for this task. The flag spelling is fixed by the interview (`compile-time-queryable`), the constant placement and query-helper shape are fully determined by the Quotable precedent and the existing `CSharpCompilation` cluster, and no naming or scoping question requires coordinator input. T12 (IAsyncEnumerable scope), T13 (branched chains), and T14 (BCL helpers scope) intersect with this task only insofar as they too must respect `IsCompileTimeQueryableEnabled` at their entry points — which is uniform with T05/T06/T09/T10 and requires no additional design decision here.
