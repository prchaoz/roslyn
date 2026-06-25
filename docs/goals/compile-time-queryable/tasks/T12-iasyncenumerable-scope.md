# T12 — *(unresolved)* `IAsyncEnumerable<T>` scope

Settle async-chain scope:

- Does the receiver-static-type rule cover chains rooted at marked types that implement `IAsyncEnumerable<T>`?
- Does a chain that transitions mid-stream into `IAsyncEnumerable<T>` stay captured?

Blocks T03, T06, T07, T08 (async terminators).

Cite — `ienumerable-scope` *(skipped with note)*: *"I think about IAsyncEnumerable"*.

## What the question is

T06 fixes chain-detection on a single invariant: every call whose receiver static type equals the provider-marked type belongs to the chain. The marked type is something like `Postgres9Table<T>`, which a provider may declare as implementing `IQueryable<T>`, `IAsyncEnumerable<T>`, both, or neither. The unresolved question is what happens when the static type of an intermediate result is `IAsyncEnumerable<T>` rather than the marked type — either because the marked type itself returns `IAsyncEnumerable<T>` from an `AsAsyncEnumerable()`-style operator, or because consumers compose the chain with `System.Linq.AsyncEnumerable` extensions defined on `IAsyncEnumerable<T>`. Four downstream tasks depend on this answer: **T03** (whether `ChainCalls` is homogeneously typed or needs per-call segment metadata), **T06** (whether chain detection extends past the marked-type frontier), **T07** (whether dataflow capture follows a local across the marked→async transition), and **T08** (which async terminators — `await foreach`, `await ToListAsync()`, `ToArrayAsync` — count, and against which receiver type). The four leading designs below trade rewriter expressive power against binder complexity and against the cost of getting the answer wrong in v2.

## Options

### (a) Marked-type-only (status quo proposed by T06)

**Rule.** Chain capture follows the receiver-static-type test exactly. A call belongs to the chain iff its receiver's static type is the marked type. Once the static type widens to `IAsyncEnumerable<T>` (or anything else), capture stops there: that call is the chain root for the substitution, and anything downstream is treated as consumption. Async terminators (`ToArrayAsync`, `ToListAsync`, etc.) and `await foreach` are recognized as terminators only when their receiver is still the marked type — i.e. the provider declared `ToListAsync` as a member of `Postgres9Table<T>` rather than relying on `System.Linq.AsyncEnumerable`.

**Downstream implications.**
- **T03** — `ChainCalls` stays homogeneously typed (`IMethodSymbol` per call, receiver always the marked type). No async-segment metadata.
- **T06** — rule unchanged; no async-aware code path.
- **T07** — dataflow capture terminates when a local's declared type is not the marked type. The marked→async transition is a hard boundary.
- **T08** — terminator recognition runs against the marked type only. `await foreach (var x in markedChain)` is the terminator iff `markedChain` is still the marked static type at the `await foreach`.

**Worked example.**
```csharp
[CompileTimeQueryable("Postgres9.Rewriter, Postgres9")]
public sealed class Postgres9Table<T> : IQueryable<T>, IAsyncEnumerable<T>
{
    public Postgres9Table<T> Where(Expression<Func<T, bool>> p) => ...;
    public Task<List<T>> ToListAsync() => ...; // member, returns Task<List<T>>
    public IAsyncEnumerable<T> AsAsyncEnumerable() => this;
}

// Captured under (a):
var list = await pgUsers.Where(u => u.Age > 18).ToListAsync(); // OK — full chain, marked type throughout

// NOT captured past the transition under (a):
IAsyncEnumerable<User> stream = pgUsers.Where(u => u.Active).AsAsyncEnumerable();
//                                                          ^^^^^^^^^^^^^^^^^^^^^
// Chain root is the AsAsyncEnumerable() call; the .Where(...) belongs to the chain, the iteration
// below does not — it's pure consumption.
await foreach (var u in stream) { /* runs against the substituted IAsyncEnumerable<T> */ }
```

**Files-to-touch.** None beyond T06/T07/T08 baseline.

**Edge cases / risks.**
- Provider must declare `ToArrayAsync`/`ToListAsync` as members of the marked type, not as `System.Linq.AsyncEnumerable` extensions, to get the rewriter invoked. This is a real burden — async LINQ is conventionally extension-only in modern .NET.
- `await foreach (var u in pgUsers.Where(...))` works (the receiver of the implicit `GetAsyncEnumerator` is still the marked type), but `await foreach (var u in pgUsers.Where(...).AsAsyncEnumerable())` does not. Users will struggle to predict which form captures.
- Silent fallback (T11) absorbs the loss when the transition fires too early, which masks the bug: the chain compiles fine, but server-side execution stops mid-way and the rest runs client-side. Performance regression with no diagnostic.
- Trivial to specify and ship; trivial to widen later without breaking existing rewriters. The most conservative v1.
- A provider that today exposes only `IAsyncEnumerable<T>` and not `IQueryable<T>` (some cloud SDKs) cannot meaningfully adopt the feature at all — its public surface returns `IAsyncEnumerable<T>` after the very first operator.

### (b) Marked-type OR `IAsyncEnumerable<T>` if rooted at marked type

**Rule.** Chain capture follows the marked type as in T06; additionally, once the static type widens to `IAsyncEnumerable<T>` *as a direct return from a call whose receiver was inside the chain*, subsequent calls whose receiver static type is `IAsyncEnumerable<T>` (or `IAsyncEnumerable<T>`-derived) continue to belong to the chain. The chain terminates at any `IAsyncEnumerable<T>` terminator: `await foreach`, `await System.Linq.AsyncEnumerable.ToListAsync(stream)`, `await stream.ToArrayAsync()`, etc. The provenance bit ("this `IAsyncEnumerable<T>` started life inside the marked-type chain") is what gates capture — an `IAsyncEnumerable<T>` introduced from elsewhere (parameter, field, unrelated method) is not chained, even at the same static type.

**Downstream implications.**
- **T03** — `ChainCall` needs a `SegmentKind` enum (`MarkedType` / `AsyncEnumerableContinuation`) so the rewriter can emit different code per segment (e.g., translate the marked segment to SQL and emit a `System.Linq.AsyncEnumerable` pipeline for the async segment, or refuse with a diagnostic and fall back per T11). `ChainCalls` is no longer homogeneously typed.
- **T06** — chain detection adds a second branch: receiver static type is `IAsyncEnumerable<T>` *and* the receiver is a local/temp whose origin is inside the captured chain. Requires a per-call "is the receiver chain-tracked?" lookup, which T07 has to compute.
- **T07** — dataflow capture has to follow a local across the marked→async transition. The local's static type changes, but the capture state ("this value's provenance is chained") persists. Requires extending the per-local capture record with a provenance bit.
- **T08** — `await foreach`, `await ToListAsync()`, `await ToArrayAsync()`, `await CountAsync()`, etc., become terminators against any receiver whose static type is `IAsyncEnumerable<T>` and whose provenance is chained.

**Worked example.**
```csharp
var stream = pgUsers
    .Where(u => u.Active)                         // Postgres9Table<User> — marked-type segment
    .AsAsyncEnumerable()                          // returns IAsyncEnumerable<User> — provenance set
    .Where(u => u.Score > 90)                     // System.Linq.AsyncEnumerable.Where extension — async segment
    .Select(u => u.Email);                        // System.Linq.AsyncEnumerable.Select extension — async segment

await foreach (var e in stream) { /* terminator: captured */ }

// NOT captured: provenance is not chained
IAsyncEnumerable<User> external = SomeOtherApi();
await foreach (var u in external.Where(...)) { /* external — fallback */ }
```

**Files-to-touch.**
- `src/Compilers/CSharp/Portable/Binder/Binder_Invocation.cs` — chain-detection predicate gets a second arm checking `IAsyncEnumerable<T>` receivers against the chain-local set computed in T07.
- `src/Compilers/CSharp/Portable/Binder/ForEachLoopBinder.cs` (around the `IsIAsyncEnumerable` check at line 1782) — query the chain registry on the collection expression to decide whether the `await foreach` is a captured terminator versus a plain consumer.
- `src/Compilers/CSharp/Portable/CompileTimeQueryable/ChainCall.cs` (new in T03) — add `SegmentKind` enum.

**Edge cases / risks.**
- The "provenance" bit requires a side table keyed on `BoundLocal` / `BoundParameter` / `BoundTempReference` symbols, computed during T07 and consulted during T06. Any control-flow shape T07 doesn't model (catch blocks, switch arms, conditional assignments) yields false negatives — chain breaks where the user expected continuation. Document T07's known limitations as the floor for what (b) supports.
- Rewriters now have to emit a hybrid output: SQL for the marked segment plus async-stream code for the async segment, with a typed boundary in between. That's a substantially larger contract than (a) and changes what the v1 sample rewriter (T15) has to demonstrate.
- `System.Linq.AsyncEnumerable` is extension-method-only. Extension-method receiver static type is the *first parameter type*, which is `IAsyncEnumerable<T>` for these operators. The receiver-static-type test in T06 still works mechanically, but T13 (branched chains) becomes harder: extensions can be defined in any namespace, so the "same provider type appears later" heuristic doesn't apply uniformly.
- `IAsyncEnumerable<T>` is declared `IAsyncEnumerable<out T>` (covariant in `T` for reference types). Provenance tracking has to decide whether a value's chain-membership survives the variant assignment — assigning a chained `IAsyncEnumerable<Derived>` into an `IAsyncEnumerable<Base>` local, or vice-versa via `.Cast<>`/`.OfType<>` (which is a new call, not a conversion). Either explicitly normalize on element type at the assignment site or document that variant assignments break the chain.
- `[EnumeratorCancellation]` and `WithCancellation(ct)` calls are part of every realistic async chain. The rewriter has to receive these as chain calls, but they don't have a SQL-side analog — the rewriter must thread them through to the emitted async pipeline. Document explicitly.

### (c) `IAsyncEnumerable<T>` transition is itself a terminator

**Rule.** Any call inside the chain whose return type is `IAsyncEnumerable<T>` (rather than the marked type) is treated as a terminator. The substitution site is the call producing the `IAsyncEnumerable<T>`. Downstream consumption — `await foreach`, `await ToListAsync()` from `System.Linq.AsyncEnumerable`, etc. — is outside the chain. The rewriter's job for an async chain is to emit "produce this `IAsyncEnumerable<T>` server-side"; everything afterward is client-side.

**Downstream implications.**
- **T03** — `ChainCalls` stays homogeneously typed. Terminator carries an `IsAsync` flag (or a `TerminatorKind` enum value) for emit, but per-call shape is unchanged.
- **T06** — chain detection unchanged.
- **T07** — capture stops cleanly at the `IAsyncEnumerable<T>` transition, no provenance tracking required.
- **T08** — add `IAsyncEnumerable<T>`-returning calls as a new terminator category alongside materializers, aggregates, `foreach`, and `await ToArrayAsync`.

**Worked example.**
```csharp
// Substituted at the AsAsyncEnumerable() call:
IAsyncEnumerable<User> stream = pgUsers.Where(u => u.Active).AsAsyncEnumerable();
//                              ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
// Substituted with: ExecuteSqlAsync<User>("SELECT ... FROM users WHERE active")

// Pure consumption, not captured:
await foreach (var u in stream.Where(u => u.Score > 90)) { ... }
//                             ^^^^^^^^^^^^^^^^^^^^^^^^
// System.Linq.AsyncEnumerable.Where — runs client-side, never sees the rewriter.
```

**Files-to-touch.**
- `src/Compilers/CSharp/Portable/Binder/Binder_Invocation.cs` — terminator-recognition predicate (T08) adds a check on the return-type widening to `IAsyncEnumerable<T>`.
- No `ForEachLoopBinder.cs` change needed: `await foreach` is always plain consumption under (c).

**Edge cases / risks.**
- Cleanest implementation; smallest binder delta; preserves T03's homogeneity. The most pragmatic v1 if marked-type-only (a) is too restrictive.
- Hides cross-segment optimization: the rewriter never sees the `.Where(u => u.Score > 90)` after the transition, so even if the predicate is SQL-translatable, it runs client-side. A provider can't push the predicate down without inspecting more of the surrounding tree, which would require a separate analyzer.
- "Return type widens to `IAsyncEnumerable<T>`" needs a precise definition. A method that returns `IAsyncEnumerable<T>` directly is clear; one that returns a *derived* class implementing `IAsyncEnumerable<T>` (e.g., the marked type itself) is not — the marked type's `AsAsyncEnumerable()` is the canonical case where the return type *is* the marked type when typed concretely but `IAsyncEnumerable<T>` if returned via the interface. Define the rule on declared return type, not on implemented interfaces.
- `await foreach (var x in pgUsers.Where(...))` — the `pgUsers.Where(...)` call returns the marked type, not `IAsyncEnumerable<T>`. Under (c), the chain captures `pgUsers.Where(...)`, and the `await foreach` is a separate terminator-by-iteration (T08 already covers `foreach`). For this case T08's `foreach` and `await foreach` rules collapse into one "any iteration of a marked-type chain is a terminator" rule. Confirm during T08 enrichment.
- Future-proof: (c) trivially widens to (b) later by promoting "stops at transition" to "follows past transition". Going from (b) to (c) is a breaking change for users who depend on async-segment continuation.

### (d) Follow chain through `IAsyncEnumerable<T>` transitions if same provider type appears later

**Rule.** Same as (b), plus: the chain may re-enter the marked type after an `IAsyncEnumerable<T>` segment. Useful only if the provider exposes operators like `AsAsyncEnumerable().ServerSideMap(...)` that re-project async-stream results back onto a server-side query shape. The provenance bit from (b) extends to a provenance *type*: "this value's origin is chained, and its current static type is either the marked type or `IAsyncEnumerable<T>`."

**Downstream implications.**
- **T03** — `ChainCall` carries `SegmentKind` and a transition direction (`MarkedToAsync`, `AsyncToMarked`).
- **T06** — chain-detection predicate becomes "receiver is a chained value, regardless of its current static type, provided the static type is in {marked, `IAsyncEnumerable<T>`}".
- **T07** — dataflow capture handles bidirectional transitions; the per-local provenance record carries the current static type alongside the chain identity.
- **T08** — terminators recognized in both type domains. Aggregates can appear in either segment.

**Worked example.**
```csharp
// Hypothetical: provider exposes a re-projection operator.
public sealed class Postgres9Table<T>
{
    public IAsyncEnumerable<T> AsAsyncEnumerable() => ...;
    public Postgres9Table<U> ServerSideMap<U>(IAsyncEnumerable<U> projected) => ...;
}

var q = pgUsers.AsAsyncEnumerable()           // IAsyncEnumerable<User> — async segment
               .Select(u => u.OrgId)           // System.Linq.AsyncEnumerable — async segment
               .Distinct()                     // System.Linq.AsyncEnumerable — async segment
               // ... merged back into a marked-type query somewhere:
               // (in practice, this case rarely exists)
               ;
```

**Files-to-touch.**
- Superset of (b): `Binder_Invocation.cs`, `ForEachLoopBinder.cs`, `ChainCall.cs`. Additionally, the chain-detection predicate must accept *either* static type form and look up provenance to decide.
- `src/Compilers/CSharp/Portable/Binder/Binder_Conversions.cs` — any implicit conversion that crosses the marked↔async boundary must preserve provenance. T07's symbol-keyed side table needs a conversion-aware lookup.

**Edge cases / risks.**
- No known v1 provider needs this. The `ServerSideMap`-style operator is hypothetical; including it pays full binder-complexity cost without an actual consumer.
- Bidirectional transitions interact poorly with T13's branched-chain decisions: a DAG that crosses segment types in different branches is hard to render in any of T13's three shapes. (d) effectively forces T13 to whole-DAG.
- The provenance type record has to be persisted across `BoundConversion` nodes; the existing capture walker doesn't model conversion identity. Real implementation cost is substantially higher than (b).
- Easy to defer to v2 without breaking users who shipped against (a), (b), or (c). (d) is strictly a superset of (b).
- The decision affects the public shape of `ChainRewriteContext` (T03) — (d) is the only option where rewriters absolutely require a segment-aware iteration API rather than a flat `ChainCalls` array. Locking in (a)/(b)/(c) leaves room to add a parallel "DAG" context later; locking in (d) commits to the more complex shape immediately.

## Recommendation

DO NOT pick. The four options span a clear cost/expressive-power curve from (a) cheapest/most restrictive to (d) most expressive/most expensive. The decision hinges on three inputs that are out of scope for T12 itself:

- **What providers ship in v1?** If the canonical sample (T15, Postgres) declares its async terminators as members of `Postgres9Table<T>`, (a) suffices. If the sample composes `System.Linq.AsyncEnumerable` extensions onto the marked type, (a) silently demotes those calls to client-side and (b)/(c) become required.
- **Is the async-segment continuation a v1 or a v2 feature?** (b) and (d) require the rewriter contract to handle two emit modes (SQL-segment plus async-stream segment). T02's `IQueryableChainRewriter.Rewrite` returning a single string of source text was designed against the single-segment model. (b)/(d) likely force T02 to evolve (e.g. return an array of per-segment substitutions, or pivot the rewriter to a "translate this prefix and tell us what's left" protocol).
- **What's the silent-fallback story (T11) for partial captures?** (a) and (c) have clean fallback boundaries: either the whole chain rewrites or the chain runs from the transition point. (b) and (d) introduce hybrid execution shapes that succeed-with-degradation, which is harder to diagnose when users see unexpected performance.

Dependencies/risks summary:
- (a) is the safest v1 — narrow public API surface and zero provenance machinery. The risk is that providers can't model async-LINQ-style composition at all and the feature looks unusable in modern .NET.
- (b) is the most expressive v1 the existing rewriter contract can absorb. The risk is the public `ChainCall` shape locks in `SegmentKind` semantics before any provider has shipped against them.
- (c) is the cleanest middle ground — small binder delta, no rewriter-contract evolution, predictable substitution boundary. The risk is that we permanently lose the ability to push predicates past the async transition without a follow-up breaking change.
- (d) is a strict superset of (b). Including it in v1 commits to the largest API surface and forces T13 to whole-DAG; deferring it leaves room to add later without breaking shipped rewriters.

Forward-compatibility note (independent of which option you pick): (c) widens to (b) additively (terminator-at-transition becomes a chain-call-at-transition), and (a) widens to either (c) or (b) additively. Going *from* (b) *to* (c) is a breaking change for rewriters that depend on async-segment continuation. Whichever option v1 picks, lock in the public `ChainCall` shape (T03) accordingly so the v2 widening is additive.

## Open coupling

- **T15 (sample rewriter)** is the strongest forcing function: whichever option the sample needs, T12 must support. Confirm the Postgres sample's async surface before locking T12.
- **T13 (branched chains)** intersects: under (b)/(d), a chain that splits at the async transition into two consumers is both a branched chain *and* a multi-segment chain. The two design decisions must be made together if either picks the more permissive option.
- **T08 (terminators)** absorbs whichever async terminator set this task selects; T12 picks the rule, T08 codifies the catalog.
- **T03 (`ChainRewriteContext`)** carries a placeholder `ChainCall` shape today; lock T12 before T03 finalizes whether `ChainCall` is homogeneous (`a`/`c`) or segmented (`b`/`d`).
