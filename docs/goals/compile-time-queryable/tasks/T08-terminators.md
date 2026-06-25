# T08 — Terminator recognition

Recognize as terminators:

- Materializers — `ToArray`, `ToList`, `ToDictionary`, `ToHashSet`.
- Aggregates — `Single`, `First`, `Last`, `Count`, `Any`, `All`, `Sum`, `Min`, `Max`, `Average` (and `*OrDefault` variants).
- `foreach` over the chain.
- `await` on async terminators (e.g. `ToArrayAsync`).

Cite — `terminators`: `[materializers, aggregates, foreach, await-async]`.

## Outcome

The list above is illustrative, not the recognition mechanism. Per `operator-shape: agnostic`
(T06), chain membership is decided by a *single* symbolic rule: a call belongs to the chain iff
its receiver's static type is the provider-marked queryable type **and** its return type is
also that same marked type. The terminator predicates fall out by negation of that rule, split
by the kind of bound node sitting at the exit of the chain:

- **Invocation terminator (covers materializers, aggregates, async terminators).** At a
  `BoundCall` whose receiver's static type is the marked queryable type `Q`, the call is a
  terminator iff `call.Method.ReturnType` (after substitution) is *not* `Q`. No closed name
  list. No signature pattern. The same predicate fires for `ToList()` (`List<T>`),
  `Count()` (`int`), `ToArrayAsync()` (`Task<T[]>`), and any user-defined extension method
  that drops out of `Q`. Whether the call is an instance member, an extension method, or an
  explicit interface implementation is irrelevant — the receiver-static-type and return-type
  symbols are read off `MethodSymbol`, not off the syntactic invocation form.
- **`foreach` terminator.** Recognized in `ForEachLoopBinder.BindForEachPartsWorker` when the
  bound collection expression's static type is `Q`. The chain ends at the collection-expression
  node, not at any synthesized `GetEnumerator` call inside `BoundForEachStatement` — pattern-
  based and interface-based enumeration look identical from the chain-boundary perspective.
  `IsAsync` (the `await foreach` form) flips the recognition into the async family but does
  not otherwise change the predicate.
- **`await` terminator.** By the time `BindAwait` runs, the operand's static type is already
  `Task<…>`/`ValueTask<…>` (or `Q` itself in the uncommon direct-`GetAwaiter` case), so the
  invocation-terminator rule has typically already closed the chain one node deeper. The
  reason `Binder_Await.cs` still appears in **Files to touch** is two-fold: (a) the rewriter
  needs to know whether the terminator was awaited so it can choose async vs sync result
  semantics in the substituted source text — that bit is set here, not at the invocation;
  (b) the dataflow extension in T07 must follow `await`-of-local-of-marked-`Q` through
  `BoundAwaitExpression` rather than dropping the chain at the await boundary. Whether
  `BindAwait` itself participates in *recognition* (as opposed to *annotation*) is the
  cleanest open question to surface to T12.

A consequence worth stating: the *chain detection* in T06 is operator-agnostic; the *terminator
detection* here is necessarily different, because the chain ends at any node whose binding
result leaves `Q`. Intermediate `LINQ` operators such as `OrderBy`/`ThenBy` therefore stay
*inside* the chain (they return `Q` again under the marked-queryable design), and no special-
case carving is required for them. They become terminators only if the provider deliberately
returns a different type — at which point the receiver/return-type rule already covers it.

## Files to touch

- `src/Compilers/CSharp/Portable/Binder/Binder_Invocation.cs` — the invocation terminator hook
  hangs off the same point as T06's chain detection. When `BindInvocationExpression` sees a
  call with a marked receiver but a non-marked return type, the chain ends here; the chain so
  far is handed to T09 (rewriter invocation).
- `src/Compilers/CSharp/Portable/Binder/ForEachLoopBinder.cs` (the file the task brief calls
  *"`BoundForEachStatement` binder"* — there is no separate `BoundForEachStatement.cs`; the
  bound node is auto-generated from `BoundNodes.xml`). The recognition site is
  `BindForEachPartsWorker`, *after* `collectionExpr` has been bound but before
  `GetEnumeratorInfoAndInferCollectionElementType` — i.e., while we still have the marked-type
  symbol on the un-coerced collection expression.
- `src/Compilers/CSharp/Portable/Binder/Binder_Await.cs` (the *"`BoundAwait`"* binder — again,
  `BoundAwaitExpression` is generated, not a hand-written binder file). `BindAwait` is the
  annotation point: tag the enclosing chain as "await-terminated" so the rewriter can pick
  async-result semantics in its returned source text. The chain itself was already closed by
  the invocation-terminator rule one node deeper.
- Coordinates with T06 (chain detection) and T07 (intra-method dataflow capture) for the
  receiver-type check; feeds T09 (rewriter invocation) and T11 (decline fallback). Do *not*
  alter `Binder_Conversions.cs`: terminator recognition runs before conversion classification.

## Dependencies

- Built on T06 (chain detection — provides the receiver-type / return-type predicate) and T07
  (dataflow capture — extends the chain across simple local-variable assignments before a
  terminator is reached).
- Feeds T09 (rewriter invocation — terminator is the trigger to fire `Quote`-equivalent on
  the captured chain) and T11 (decline-fallback — on null return, the terminator site falls
  back to runtime `IQueryable`/`IEnumerable` plumbing).
- T12 (`IAsyncEnumerable<T>` scope) directly affects the async-terminator surface: until T12
  is settled, the await-terminator predicate cannot decide whether
  `markedQueryable.AsAsyncEnumerable().ToListAsync()` belongs to the chain (mid-stream
  transition) or whether `markedQueryable.ToArrayAsync()` is recognized at the marked-type
  call site only.

## Edge cases / risks

- **`foreach` over `IAsyncEnumerable<T>` vs `await foreach`.** `ForEachLoopBinder.IsAsync`
  distinguishes the two syntactic forms. A marked type that implements
  `IAsyncEnumerable<T>` and is iterated with a sync `foreach` would fail enumeration anyway
  (the binder reports an error). The terminator predicate should not care: chain capture ends
  at the collection expression regardless, and T11 fallback handles the error case.
- **`ConfigureAwait` on async terminators.** `markedQueryable.ToArrayAsync().ConfigureAwait(false)`
  — the `ToArrayAsync()` call is the terminator (return type `Task<T[]>` leaves `Q`); the
  `ConfigureAwait(...)` extension method runs *outside* the chain on the resulting
  `Task<T[]>`. The eventual `await` is two nodes removed from the chain. Recognition at
  `BindAwait` therefore needs to walk through `ConfiguredTaskAwaitable` /
  `ConfiguredAsyncEnumerable` only if T12 explicitly opts in; the conservative default is
  *not* to follow.
- **`await using` resource scoping.** `await using` is bound through
  `UsingStatementBinder.BindUsingStatementOrDeclarationFromParts` (called from
  `Binder_Statements.cs:702`). A marked queryable that is `IAsyncDisposable` would land here
  via its disposal path, not via the chain — disposal is orthogonal to terminator recognition
  but worth noting for the spec: the chain ends at the `using` *expression* (handled by the
  invocation-terminator rule when the resource initializer leaves `Q`), not at the disposal
  itself.
- **`OrderBy`/`ThenBy` — intermediate or terminator?** Intermediate, by construction: under the
  operator-agnostic rule they return `Q` so the receiver/return-type predicate keeps them
  in-chain. They become terminators only if the provider authors them to return a different
  ordered-queryable type that is not also marked `[CompileTimeQueryable]` — in which case the
  predicate already fires correctly.
- **User-defined extension methods named like terminators but on a different receiver.** Not
  ambiguous: an extension `ToList<T>(this IEnumerable<T>)` against a marked receiver `Q` binds
  with `this` parameter of `IEnumerable<T>`, so `call.ReceiverOpt` is the marked-`Q`
  expression but the *method's reduced receiver type* is `IEnumerable<T>`. Recognition uses
  the *expression*'s static type (`Q`), so the call is correctly classified as a chain exit.
- **Explicit interface implementations.** A marked type that explicitly implements
  `IEnumerable<T>.GetEnumerator` does not hide its surface from this predicate: the receiver-
  static-type rule sees `Q` regardless of which method ultimately dispatches. Same for
  `IAsyncEnumerable<T>` once T12 settles.
- **`*OrDefault` variants.** Covered identically by the return-type rule; no name-list
  enumeration needed.
- **Pattern-based `GetEnumerator()` on a marked type.** A type that exposes a public
  `GetEnumerator()` returning a non-marked enumerator is enumerated via the pattern. `foreach`
  recognition keys off the collection expression's static type (`Q`), so this is handled
  uniformly — the binder's enumeration-info builder runs *after* terminator capture.
- **Risk: terminator recognition must not fire on a `BoundBadExpression` receiver.** When the
  receiver bound with errors, hand off to T11 fallback rather than attempting rewrite.

## Open questions

T12 directly bears here and is unresolved:

- Does the receiver-static-type rule extend to chains rooted at marked types that *only* implement
  `IAsyncEnumerable<T>` (no synchronous `IEnumerable<T>` surface)? If yes, `await foreach` is
  the canonical foreach-terminator form for them.
- Does a chain that transitions mid-stream from `Q` into `IAsyncEnumerable<T>` (e.g., via
  `.AsAsyncEnumerable()`) stay captured? Under the strict marked-type rule it does not — the
  transition call already exits `Q`. T12 must decide whether async-bridge methods get a
  carve-out or whether providers are expected to keep `Q` async-aware end-to-end.
- Are `ToArrayAsync`/`ToListAsync`/`CountAsync` (etc.) recognized only at the marked-type
  call site, or also after an `IAsyncEnumerable<T>` bridge? Same T12 decision.
- `ConfigureAwait`-on-terminator: opt-in (follow into `ConfiguredTaskAwaitable` /
  `ConfiguredAsyncEnumerable`) or opt-out (the chain ends at the un-configured terminator
  call)? Surface only — do not invent a default before T12.
- Does `BindAwait` participate in terminator *recognition* at all, or is its role purely
  *annotation* of an already-closed chain (as the **Outcome** section currently posits)? The
  brief's "Files to touch" list includes `Binder_Await.cs`, so the design must commit one way.
