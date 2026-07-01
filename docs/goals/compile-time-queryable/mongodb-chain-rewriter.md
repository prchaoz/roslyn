# MongoDB chain rewriter — refined design (Approach 3 of `mongodb-approaches.md`)

MongoDB instantiated against the compile-time queryable chain rewriter (T01–T22). This doc
does three things:

1. fixes the **driver-side surface** (`MongoQuery<T>`, operators, terminators, runtime
   helpers) that the feature's receiver-static-type rules require;
2. fixes the **rewriter contract usage** — what `MongoChainRewriter` reads from
   `ChainRewriteContext` and exactly what source text it returns, including the prepared-
   pipeline runtime model that delivers the allocation objective;
3. resolves, **for MongoDB v1**, the unresolved core-feature questions (T12 async scope,
   T13 branching, T14 helpers) and feeds back two hard requirements the MongoDB case
   imposes on T06/T08.

Nothing here exposes compiler internals: the rewriter consumes syntax + public
`SemanticModel` (T03) and produces C# source text (T02/T10) — bound trees never cross the
boundary in either direction.

---

## 1. Driver surface: a marked entry type, not `AsQueryable()`

Two facts about the core feature dictate the driver API shape:

- **T01:** `[CompileTimeQueryable]` targets **class** types (`AttributeTargets.Class`).
  The driver's `AsQueryable()` returns an interface (`IMongoQueryable<T>` in 2.x,
  `IQueryable<T>` in 3.x), so today's entry point can never be the marked type.
- **T06/T08:** a call is *in* the chain iff its receiver's static type is the marked type
  `Q` **and** its return type is `Q` again. The standard `System.Linq.Queryable.Where`
  extension returns `IQueryable<T>` — under the rule, the very first standard LINQ operator
  would *exit* the chain. Operators must therefore be **instance methods (or Q-returning
  members) on the marked type**.

So the driver ships a new, opt-in entry point rather than retrofitting `AsQueryable()`:

```csharp
[CompileTimeQueryable("MongoDB.Driver.Compiler.MongoChainRewriter, MongoDB.Driver.Compiler")]
public sealed class MongoQuery<T> : IAsyncEnumerable<T>   // + IQueryable<T> for fallback
{
    // operators — receiver Q, return Q → in-chain per T06
    public MongoQuery<T>       Where(Expression<Func<T, bool>> predicate) => ...;
    public MongoQuery<TResult> Select<TResult>(Expression<Func<T, TResult>> selector) => ...;
    public MongoQuery<T>       OrderBy<TKey>(Expression<Func<T, TKey>> key) => ...;
    public MongoQuery<T>       ThenBy<TKey>(Expression<Func<T, TKey>> key) => ...;
    public MongoQuery<T>       Skip(int count) => ...;
    public MongoQuery<T>       Take(int count) => ...;
    public MongoQuery<TResult> GroupBy<TKey, TResult>(...) => ...;

    // terminators — receiver Q, return type leaves Q → chain exit per T08
    public Task<List<T>> ToListAsync(CancellationToken ct = default) => ...;
    public Task<T>       FirstAsync(CancellationToken ct = default) => ...;
    public Task<T?>      FirstOrDefaultAsync(CancellationToken ct = default) => ...;
    public Task<long>    CountAsync(CancellationToken ct = default) => ...;
    public Task<bool>    AnyAsync(CancellationToken ct = default) => ...;
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default) => ...;
}

// entry point
public static class MongoCollectionExtensions
{
    public static MongoQuery<T> Query<T>(this IMongoCollection<T> collection) => ...;
}
```

Design consequences, in order of importance:

- **Runtime fallback plumbing is mandatory (T11).** Every member above has a real runtime
  implementation that routes through the driver's existing LINQ3 provider. T11's silent
  fallback re-binds the original chain; "the type's normal plumbing *if any*" must be
  "always" for MongoDB — a declined chain must produce identical results at runtime cost,
  never a binder error.
- **`collection.Query()` is the opt-in signal.** Existing `AsQueryable()` code is untouched
  and unaffected; migration is mechanical (`AsQueryable` → `Query`) and each migrated call
  site is individually revertible. This also sidesteps any binary-compat question for the
  driver.
- **Expression-tree parameters are kept deliberately.** The operators still take
  `Expression<Func<...>>` so the *fallback* path works unchanged through LINQ3. On the
  rewritten path these arguments are never evaluated — substitution happens at binding,
  before lowering, so the `Expression.Lambda` factory chain for the whole spine is never
  emitted (this is where O3 is won; see §5).
- **Widening T01 to `AttributeTargets.Interface` is not needed** for this design; note it
  upstream as a nice-to-have for providers that cannot add a concrete entry type, but do
  not block on it.

### 1.1 Hard requirement fed back into T06/T08: compare `OriginalDefinition`

`Select` returns `MongoQuery<TResult>`, not `MongoQuery<T>`. If the T06/T08 predicate
compares **constructed** types, every projection terminates the chain and
`.Where(...).Select(...).ToListAsync()` — the headline scenario — never captures past
`Where`. The predicate must be:

> receiver's static type and the call's (substituted) return type share the same
> **original named-type definition**, and that definition is `[CompileTimeQueryable]`-marked.

T01 already gestures at constructed-type resolution ("resolve through constructed types");
T06/T08 must specify `OriginalDefinition` comparison explicitly. This is the single most
important correction the MongoDB instantiation forces on the core plan — file it against
T06/T08 before their implementation starts.

### 1.2 Second feedback item: terminator's generic result

`FirstOrDefaultAsync` on `MongoQuery<string>` returns `Task<string?>` — a type mentioning
the chain's element type but not `Q`. The T08 "return type is not `Q`" rule handles this
correctly with `OriginalDefinition` comparison (`Task<>` ≠ `MongoQuery<>`); called out only
so T16's test matrix includes generic-result terminators.

---

## 2. Async scope: resolve T12 as option (a), marked-type-only

T12's four options exist because conventional async LINQ is extension-method-only over
`IAsyncEnumerable<T>`. MongoDB does not have that problem: **we are designing the surface**,
so every async terminator is a *member* of `MongoQuery<T>` (§1). Option (a) — the cheapest,
most conservative rule, zero provenance machinery — is then sufficient:

- `await q.Where(...).ToListAsync()` — invocation terminator at `ToListAsync` (return type
  leaves `Q`); the `await` is annotation only (T08).
- `await foreach (var x in q.Where(...))` — foreach terminator; the collection expression's
  static type is still `Q` because `MongoQuery<T>` implements `IAsyncEnumerable<T>` itself.
- `q.Where(...).ToListAsync().ConfigureAwait(false)` — chain already closed at
  `ToListAsync`; `ConfigureAwait` runs outside the chain on the returned `Task`. No
  carve-out needed (T08's conservative default).
- The driver intentionally ships **no** `AsAsyncEnumerable()` bridge in v1, so the
  mid-stream-transition question never arises. If a user pipes a `MongoQuery<T>` into
  `System.Linq.AsyncEnumerable` extensions, the first such call exits `Q`, the captured
  prefix still rewrites, and the async suffix runs client-side — acceptable, documented.

Per T12's forward-compat note, (a) widens additively to (b)/(c) later; MongoDB v1 locks
nothing in. **This resolves T12 for the MongoDB provider and removes MongoDB as a forcing
function for the more expensive options.** (The Postgres sample, T15, remains the other
input; if it also declares member terminators, T12 can close as (a) globally.)

### 2.1 Await semantics of the substitution

The substitution replaces the **terminator invocation expression**, whose type is
`Task<List<T>>` (etc.). The rewriter therefore emits a *Task-typed* expression and never
emits `await` itself — the user's `await`, `ConfigureAwait`, or lack thereof applies to the
substituted expression unchanged. This keeps the inline path (T10) a single expression in
every async case and makes the T08 "was it awaited" annotation an optimization hint MongoDB
does not need for correctness.

---

## 3. What `MongoChainRewriter.Rewrite` reads

From `ChainRewriteContext` (T03), in translation order:

1. **`ChainCalls`** (source→terminator). Operator-agnostic on the compiler side; the
   rewriter dispatches on `IMethodSymbol.Name` + `OriginalDefinition` being the known
   `MongoQuery<T>` members. Any unrecognized in-chain method (a future driver version's
   operator this rewriter build doesn't know, or a user extension returning `Q`) →
   **decline** (silent fallback, §6).
2. **`Lambdas`** — consumed as opaque syntax, translated by the shared MQL translator
   (`LambdaExpressionSyntax` + `SemanticModel.GetSymbolInfo`/`GetTypeInfo` on subexpressions
   + the schema name map, §4). Never re-bound, never `AnalyzeDataFlow` (forbidden by T03's
   contract).
3. **`Captures`** — every outer local/parameter read inside the chain, including lambda
   bodies (`u => u.Age > minAge` → `minAge`). Each becomes a **pipeline parameter** (§5).
   Capture types must be BSON-representable (primitives, strings, dates, ObjectId, arrays
   thereof, or types with a compile-time-visible schema); otherwise decline. `ref`/ref-struct
   captures: decline (T10 cannot wrap them anyway).
4. **Terminator arguments** (from the terminator's `ChainCall`): `CancellationToken`,
   session handles, etc. Their **syntax is spliced verbatim** into the output in original
   order — legal and side-effect-safe precisely because substitution happens at binding,
   before any evaluation exists. The rewriter must preserve argument order and never
   duplicate an argument expression.
5. **`SemanticModel`** — only for symbol/type queries on nodes *inside* the provided syntax
   (member resolution in lambda bodies, constant-ness via `GetConstantValue`, attribute
   reads for schema, §4).

The translator is a **pure function** of (1)–(5). No static mutable state, no I/O, no
lazily-loaded schema — this is what T09's caching and T11's determinism contract require
(a rewriter that answers differently on re-binding corrupts IDE incrementality).

---

## 4. Schema integration (O1) — symbols are the source of truth

The rewriter does **not** consume the schema generator's output; both derive from the same
declarative inputs, read independently:

- member → element name: `[BsonElement("...")]`, `[BsonId]`, else the declared name filtered
  through **declarative conventions** — an assembly-level
  `[MongoConventions(CamelCase = true, IgnoreExtraElements = ...)]` attribute readable via
  `ISymbol.GetAttributes()` at compile time and via reflection at runtime;
- representation: `[BsonRepresentation]`, enum handling, `DateTimeKind` rules — same
  attribute set.

The schema **source generator** (shared foundation from `mongodb-approaches.md`) reads the
identical attributes to emit serializers and class maps, so compile-time MQL and runtime
serialization cannot drift *provided mapping is declarative*. The imperative runtime
`ConventionRegistry`/`BsonClassMap.RegisterClassMap` APIs are invisible at compile time —
**any document type whose mapping the rewriter cannot fully derive from attributes +
declarative conventions is a decline**, and the chain falls back to LINQ3, which honors the
imperative registry. Correctness is preserved in both worlds; only the fast path narrows.
The companion analyzer (§6.1) can flag "this type uses imperative mapping, chains over it
won't compile-time-rewrite" as a code-quality hint.

---

## 5. What the rewriter emits: prepared pipelines (O2 + O3)

### 5.1 The runtime model

A naïve output — inline `new BsonDocument("$match", new BsonDocument(...))` per stage —
satisfies O2 but rebuilds the pipeline AST per call, failing O3. A `static readonly`
pipeline field would fix that, but T10 synthesizes only a method + capture record; the
rewriter cannot plant fields in user types. The resolution is a small runtime surface in
`MongoDB.Driver` (this is the T14 question, resolved for MongoDB as **option B**: the
provider ships its own helpers in its own package — no BCL surface, matching Quotable v1):

```csharp
namespace MongoDB.Driver.Compiled;

public static class PreparedPipeline
{
    // one generic arity per parameter count, p0..pN typed — no object[], no boxing
    public static Task<List<TResult>> ToListAsync<TDoc, TResult, TP0>(
        IMongoCollection<TDoc> collection, string mqlTemplate, TP0 p0,
        CancellationToken ct = default) => ...;

    public static Task<long> CountAsync<TDoc, TP0>(...) => ...;
    // ... one entry point per terminator kind
}
```

Semantics of `mqlTemplate`: extended-JSON pipeline text with `"$$p0"`-style parameter
slots. On **first** execution per call site the template is parsed once into a pipeline
skeleton — a cached, immutable stage array with recorded parameter positions — keyed by the
template string itself. Compile-time-emitted templates are **interned string literals**, so
the cache probe is a reference-equality dictionary hit. Steady-state per call:

- one cache probe (no allocation),
- rendering parameter values into the skeleton's slots directly on the wire-serialization
  path (the driver must serialize the values anyway; no intermediate `BsonDocument` AST),
- the driver's normal command execution.

This is the **prepared-statement model**: the query plan (template parse, name mapping,
stage shaping) is paid at compile time or once per process; per-call work is parameter
rendering only. That — not "zero bytes ever" — is the honest O3 end state: no expression
trees, no closures, no LINQ provider, no per-call pipeline AST; what remains is the `Task`,
the result materialization, and driver wire buffers (pooled).

### 5.2 Emitted source, worked example

```csharp
// user writes:
var names = await collection.Query()
    .Where(u => u.Age > minAge && u.Active)
    .OrderBy(u => u.Name)
    .Select(u => u.Name)
    .ToListAsync(ct);
```

The rewriter returns (inline mode — a single expression replacing the terminator-rooted
chain, T10's common path):

```csharp
global::MongoDB.Driver.Compiled.PreparedPipeline.ToListAsync<global::My.App.User, string, int>(
    collection,
    /* language=mongodb-mql */
    "[{\"$match\":{\"age\":{\"$gt\":\"$$p0\"},\"active\":true}}," +
    "{\"$sort\":{\"name\":1}}," +
    "{\"$project\":{\"name\":1,\"_id\":0}}]",
    minAge,
    ct)
```

Notes:

- `minAge` (capture) and `ct` (spliced terminator argument) are referenced lexically —
  valid at the chain site, so inline mode binds; the wrapped path (synthesized static
  method + capture record) exists as T10's escape hatch but MongoDB output is
  expression-shaped by construction and should essentially never need it. `q.Where(...)`
  chains reached *through a local* (T07 dataflow) still substitute at the terminator site
  where all captures remain in scope.
- Element names (`age`, `name`) come from the schema map (§4); the projection's result
  shape (`string` here) is driven by the `Select` translation, and the result deserializer
  is the schema generator's — reflection-free end to end.
- The MQL literal in generated-visible source **is** the O2 deliverable: what runs on the
  server is reviewable at the substitution site, `git diff`-able in golden tests, and
  stable. IDE "view rewritten chain" tooling stays out of scope per T21; the companion
  analyzer (§6.1) provides an opt-in Info diagnostic carrying the MQL per rewritten chain
  for build-log visibility.
- Fluent-API purists can read the same output as sugar over
  `.Aggregate().AppendStage(...)`; we emit the `PreparedPipeline` form instead of literal
  `AppendStage` chains because the fluent builders allocate stage objects per call —
  exactly the redundancy O3 exists to remove.

### 5.3 Operator → MQL coverage, v1

| Operator | MQL | Notes |
|---|---|---|
| `Where` | `$match` | consecutive `Where`s merge into one `$match` with `$and` |
| `Select` | `$project` | member init / anonymous type / identity; computed fields via `$let`-free operator expressions where possible, else decline |
| `OrderBy`/`ThenBy`(+`Descending`) | `$sort` | merged into a single stage |
| `Skip`/`Take` | `$skip`/`$limit` | constant or parameter |
| `GroupBy` + `Select` | `$group` | v1: aggregate-shaped result selectors only |
| `CountAsync`/`AnyAsync` | `$count` / `$limit:1`+`$count` | terminator-fused |
| `FirstAsync`/`FirstOrDefaultAsync`/`SingleAsync` | `$limit: 1/2` | `Single` fetches 2 to preserve throw semantics |
| `Distinct` | `$group` on `_id` | |

Everything else — `Join`/`$lookup`, `SelectMany`/`$unwind`, window functions, `$facet` —
is a **decline** in v1 and a translator (not compiler) addition later. Coverage grows
without touching Roslyn again; that asymmetry is the point of the provider-pluggable
design.

---

## 6. Decline policy (T11 applied to MongoDB)

`Rewrite` returns `null` **with zero diagnostics** (silent fallback → LINQ3) for anything
that is *legal but untranslatable*: unknown operators, non-declarative schema (§4),
unserializable or `ref` captures, branched chains (§7), method calls in lambda bodies with
no MQL mapping. Falling back is always semantics-preserving because LINQ3 remains the
runtime implementation of every `MongoQuery<T>` member (§1).

Rewriter-reported **diagnostics** (which make `null` a hard error per T09's table) are
reserved for *definite user bugs* the runtime path would also throw on — e.g. a projection
into a type with no usable schema at all. Policy: when in doubt, silent decline; a false
compile error is worse than a slow query.

### 6.1 Discoverability: companion analyzer, not compiler surface

T11's known cost is that a chain can silently regress to runtime translation. The MongoDB
package mitigates this **without touching the feature's silent-default choice**: a plain
Roslyn analyzer (same NuGet, same translator core) re-runs classification over
`MongoQuery<T>` chains and reports, at severities configurable via `.editorconfig`:

- `MDBQ001` (Info, default) — "chain compile-time-rewritten; MQL: …"
- `MDBQ002` (Info, default; teams may raise to Warning) — "chain falls back to runtime
  translation: `<reason>`" — the CI tripwire T11's enrichment asks for, cheaper than the
  IL-golden recipe and available in the IDE.

Because analyzer and rewriter share one translator, their verdicts cannot drift.

---

## 7. Branching (T13): ship on Option A, keep `$facet` as the v2 forcing case

MongoDB has a sharper version of T13's canonical example: the paging+count DAG

```csharp
var q = collection.Query().Where(u => u.Active);
var page  = await q.OrderBy(u => u.Name).Skip(20).Take(10).ToListAsync(ct); // A
var total = await q.CountAsync(ct);                                          // B
```

maps under whole-DAG (Option B) to a **single server round-trip** via `$facet` — a real
optimization no relational provider gets as cheaply. Despite that temptation, MongoDB v1
targets **Option A (per-terminator)**:

- The translator is a pure function with no prefix-mutable state (parameter names are
  derived per-path from capture identity, not from a shared counter), so Option A's
  prefix-stateful trap and duplicate-diagnostic risk don't bite — the prefix simply
  translates twice into two independent prepared templates.
- Option A shares its flat `ChainRewriteContext` shape with Option C and widens additively
  to B later (T13's own compatibility analysis); `$facet` is then purely a rewriter upgrade
  plus the T09 multi-site substitution work, with no breaking change for shipped rewriters.
- Two round-trips (page + count) is exactly what LINQ3 does today, so v1 is never *worse*
  than the status quo — it's the same two queries, each translation-free.

Register MongoDB's `$facet` case on T13 as the concrete v2 payoff for Option B, replacing
the hypothetical CTE argument.

---

## 8. Packaging and rollout

- **`MongoDB.Driver`** gains `MongoQuery<T>`, `Query()`, `PreparedPipeline` (§5.1), and the
  declarative-conventions attribute. Pure library work, useful even without the compiler
  feature (the types run on LINQ3 fallback everywhere).
- **`MongoDB.Driver.Compiler`** (analyzer-channel NuGet): `MongoChainRewriter`, the shared
  MQL translator, the schema source generator, the companion analyzer (§6.1). Discovered
  via the AQN in the attribute through the T05 registry.
- **Feature flag (T04):** the package's `buildTransitive/*.targets` adds
  `<Features>$(Features);compile-time-queryable</Features>` behind an opt-out property
  (`<MongoCompileTimeQueries>false</MongoCompileTimeQueries>`), so adoption is
  package-reference + `Query()` call sites, nothing else. While the Roslyn feature is
  `[Experimental]`, the targets also set the corresponding `RSEXPERIMENTAL` suppression for
  the driver's own rewriter project only — consumers never implement the interface.
- **Compiler-version skew:** on any toolchain without the feature flag the attribute is
  inert, `Query()` chains bind normally, and everything runs on LINQ3 — the same behavior
  as a total decline. One code path, three fallback triggers (old compiler, flag off,
  rewriter decline), identical semantics.

## 9. Verification plan

- **Golden substitution tests** (driver repo): chain source → exact emitted text, per
  operator-coverage row in §5.3, including capture, splice-order, and decline cases.
- **Semantic parity harness**: every corpus chain executes twice against a live test server
  — compiled path vs. forced-fallback (flag off) — asserting result equality including
  ordering, nullability, and exception behavior (`Single` on 2+ docs, `First` on empty).
  This is the guard that makes silent fallback safe: both paths are continuously proven
  equivalent.
- **Roslyn-side (T16)**: MongoDB becomes the second fixture provider next to Postgres
  (T15), specifically exercising what Postgres doesn't: generic-instantiation-changing
  operators (§1.1), `Task`-typed terminators with spliced `CancellationToken`s,
  `await foreach` over a marked `IAsyncEnumerable<T>`, and T12(a) boundary cases.
- **Benchmarks (T18 rows)**: per-call allocations and latency vs. LINQ3 for the §5.2 chain
  (expect: trees+translation+AST → cache probe + parameter render); compile-time overhead
  per chain site; fallback-path overhead (must be ≈0: one attribute probe per marked-type
  terminator when the rewriter declines).

## 10. Feedback into the core feature (actionable, filed against tasks)

| Item | Task | Change |
|---|---|---|
| Predicate must compare `OriginalDefinition`, not constructed types (else `Select` kills every chain) | T06/T08 | specify + test |
| Generic-result terminators (`Task<T?>`) in the test matrix | T08/T16 | test only |
| T12 → option (a) suffices for MongoDB; confirm Postgres sample, then close T12 as (a) | T12/T15 | resolve |
| T13 → option A for v1; register `$facet` as the concrete whole-DAG v2 motivation | T13 | resolve for this provider |
| T14 → option B (provider ships own runtime helpers; no BCL surface) | T14 | resolve for this provider |
| Substitution output is Task-typed; `await` stays at the call site — confirm T08's await-annotation is hint-only | T08 | spec wording |
| MongoDB joins Postgres as second canonical rewriter in the spec's motivation | T17 | doc |

## 11. Open questions (MongoDB-scoped)

- **Sessions/transactions:** `ToListAsync(IClientSessionHandle, CancellationToken)`
  overloads — splice like any terminator argument, but `PreparedPipeline` needs matching
  entry points. Decide overload surface before freezing §5.1.
- **`GroupBy` result-selector coverage** for v1 — aggregate-only (`Count/Sum/Min/Max/Avg`)
  vs. `$push`-style accumulation. Leaning aggregate-only.
- **Server-version-sensitive MQL** (operators added in newer MongoDB versions): emit
  lowest-common-denominator MQL, or a `[MongoConventions(MinServerVersion = ...)]` knob
  gating richer translations? Leaning the knob, default conservative.
- **Template stability guarantee:** the emitted MQL string participates in golden tests
  and possibly users' own snapshot tests; define a canonical-ordering rule (stage merge
  order, key order) so rewriter upgrades don't churn byte-identical templates gratuitously.
