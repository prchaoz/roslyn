# T19 — *(out of scope)* No VB.NET support

The compile-time queryable chain rewriter is a C#-only feature in v1. VB.NET is not
a target.

Cite — `out-of-scope`: *"VB.NET support"*.

## Outcome

"Out of scope" here means a precise set of non-promises:

- No VB binder, lowering, or emit changes. The rewriter pipeline (chain detection,
  dataflow capture, rewriter invocation, substitution) is wired into the C# compiler
  only.
- `[CompileTimeQueryable]` itself MAY be visible to VB consumers — it is a public
  BCL attribute living in `System.Runtime.CompilerServices` and is language-agnostic
  at the metadata level. VB code can read it via reflection, but the VB compiler
  performs no compile-time action on it.
- VB code that constructs and consumes an instance of a marked provider type
  (e.g. a `Postgres9Table<T>`) falls back to that type's normal runtime plumbing —
  whatever `IQueryable<T>` / `IEnumerable<T>` / `IAsyncEnumerable<T>` surface the
  type already exposes. This is the same "silent fallback" behavior C# uses when
  the feature flag is off or the rewriter declines (T11), just permanent for VB.

## Files to touch

None in `src/Compilers/VisualBasic/`. The VB compiler tree (`Analysis`, `Binding`,
`BoundTree`, `CodeGen`, `Lowering`, `Semantics`, …) is untouched by this work.

The language design spec (T17) must call this out explicitly under a
"Language scope" section, so VB consumers of provider libraries understand that
chains they write get evaluated at runtime even when the provider is annotated.

## Dependencies

None. This is a scope exclusion, not a deliverable.

## Edge cases / risks

- **Cross-language solutions.** A VB project consuming a C# provider assembly
  whose types carry `[CompileTimeQueryable]` is supported in the sense that
  metadata loads cleanly and chains run via the provider's runtime
  `IQueryable` / `IEnumerable` / `IAsyncEnumerable` implementation. A C# project
  consuming the same provider gets the rewrite; the two stay source-compatible
  because the attribute is purely an opt-in hint to the C# binder.
- **Runtime behavior of VB callers.** Marked provider types must remain valid
  runtime queryables even when no rewriter ever fires — i.e. provider authors
  cannot ship a type that only works post-rewrite. T11 already requires this for
  C# decline/fallback; VB just makes the requirement permanent.
- **Attribute visibility from VB.** The attribute is a normal public type, so
  VB code can apply it (e.g. on a provider type authored in VB) without
  diagnostic. It simply has no effect — the VB compiler does not look it up.
  T17 should note this so nobody mistakes "VB can write the attribute" for "VB
  participates in rewriting."
- **Future re-entry.** Nothing here is designed to be VB-hostile; a later
  feature could mirror the C# binder hook in VB. v1 just makes no promises and
  ships no plumbing.

## Open questions

None.

## Landmarks

- **VB compiler tree:** `src/Compilers/VisualBasic/Portable/` — do not touch.
- **BCL attribute:** `[CompileTimeQueryable]` lives in
  `System.Runtime.CompilerServices` and is language-agnostic at the BCL level;
  only the C# binder reacts to it.
