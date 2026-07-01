# MongoDB C# driver modernization — three compile-time approaches

## Objectives

1. **O1 — Compile-time collection schemas.** Member→BSON element maps, serializers, and
   class-map registration generated at build time (the Mongo analog of
   `System.Text.Json`'s `JsonSerializerContext`).
2. **O2 — Compile-time query lowering.** Lower
   `collection.AsQueryable().Where(...).Select(...).ToListAsync()` into an explicit fluent
   pipeline — `.Match(BsonDocument).Project(BsonDocument).ToListAsync()` — so the MQL that
   actually runs is visible, reviewable, and fixed at build time.
3. **O3 — Eliminate redundant allocations.** No runtime `Expression<T>` trees, no LINQ
   provider translation pass, no per-call `BsonDocument` churn; ideally zero per-query
   allocations beyond the driver's wire-level work.

## Hard constraint

**No internal compiler API may be exposed — in particular, no bound trees.** Any contract
between the compiler and a query provider must be expressed in terms of syntax, the public
`SemanticModel`, and C# source text.

## Shared foundation (identical in all three approaches)

Two components are approach-independent and are the durable investment; build them first
regardless of which lowering mechanism wins:

- **Schema source generator (O1).** An incremental generator triggered by
  `[MongoCollection]` on POCOs (or a partial `MongoContext` class). Emits:
  - the member→BSON element-name table (respecting `[BsonElement]`, conventions, `_id`);
  - reflection-free `IBsonSerializer<T>` implementations (span-based read/write);
  - class-map registration via a module initializer.
  This is achievable today with zero Roslyn changes and is consumed by every approach
  below: the query translator needs the same member→element table to render `$match` /
  `$project` keys.

- **MQL translator library.** A pure function: *(lambda syntax, `SemanticModel`, schema
  table) → MQL stage (JSON/`BsonDocument` factory source)*. It works on
  `LambdaExpressionSyntax` + public symbol info only — never on runtime expression trees
  and never on bound nodes. Untranslatable constructs produce **compile-time diagnostics**
  (a major part of "better understanding queries": today's driver throws
  `ExpressionNotSupportedException` at runtime). The translator is reused verbatim as the
  core of Approach 1's generator, Approach 2's generator, and Approach 3's chain rewriter.

The three approaches differ only in **how the lowered pipeline is substituted at the call
site** and **how much of O3 each can reach**.

---

## Approach 1 — Source generators + interceptors (zero Roslyn changes)

Ship entirely on today's compiler: the schema generator (above) plus an **interceptor
generator** that rewrites whole query chains at their call sites.

### Mechanics

For each statement-local chain
`var names = await collection.AsQueryable().Where(u => u.Age > min).Select(u => u.Name).ToListAsync(ct);`
the generator (using `GetInterceptableLocation` — public, checksum-based):

1. **Intercepts `AsQueryable()`** to return a lightweight carrier struct/class implementing
   the original return type (`IQueryable<T>` / `IMongoQueryable<T>`) that just holds the
   `IMongoCollection<T>`.
2. **Intercepts each `Where`/`Select`** call site with a pass-through that returns the
   carrier unchanged (the `Expression<Func<...>>` argument is received and ignored).
3. **Intercepts the terminator** (`ToListAsync`) with a method that runs
   `collection.AggregateAsync<TResult>(s_pipeline_42, ct)` where `s_pipeline_42` is a
   `static readonly` pipeline precomputed by the MQL translator — the fluent
   `Match/Project` form is emitted as readable generated source, satisfying O2. The
   generator can additionally emit the rendered MQL as a doc comment or `.mql` sidecar for
   review.

Parameterized queries (`u.Age > min` capturing a local): the interceptor cannot see the
call site's locals except through its arguments, so captured values are extracted from the
(still-constructed) expression tree by a *shape-known* walk — the generator knows statically
that argument 0 is `MemberExpression(ConstantExpression(closure), field)` — no
`LambdaExpression.Compile()`, no reflection in steady state.

### What it delivers

- **O1**: fully. **O2**: fully (explicit, reviewable pipeline; compile-time errors for
  untranslatable queries).
- **O3**: *partially*. Eliminated: the entire runtime LINQ provider (translation pass,
  provider `ExpressionVisitor`s, intermediate `BsonDocument` churn, `IQueryProvider`
  plumbing). **Not** eliminated: interceptors must be signature-compatible
  (docs/features/interceptors.md, "Signature matching"), so the call site still emits the
  `Expression.Lambda(...)` factory chain and a closure object for capturing lambdas. That
  residual is small and constant per call, but it is not zero.

### Risks / limits

- Only statement-local, straight-line chains are interceptable in practice; chains split
  across locals or methods fall back to the runtime provider (which must remain as the
  fallback path — same "silent fallback" philosophy as T11).
- Generated interceptors are per-call-site artifacts; churn is handled by the incremental
  generator, but generated-code volume scales with query count.
- Requires the driver (or a companion `MongoDB.Driver.Compiler` package) to ship the
  generator; no compiler or SDK coordination needed.

---

## Approach 2 — Approach 1 + one minimal Roslyn feature: elidable interceptor arguments

Approach 1's only unreachable allocation is the expression tree the intercepted call site
still constructs for an argument the interceptor provably ignores. Close exactly that gap
with a **single, narrow compiler feature** — no new object model, no bound trees:

> An interceptor method may mark a parameter `[InterceptedArgumentElided]` (BCL attribute).
> When a call is intercepted by such a method and the corresponding argument at the call
> site is a **lambda-to-`Expression<TDelegate>` conversion**, the compiler skips emitting
> the expression-tree lowering for that argument and passes `default` instead.

- **Why lambda→`Expression<>` only:** it is the one argument kind whose construction is
  guaranteed side-effect-free, so skipping it cannot change observable behavior other than
  the allocation itself. This keeps the language-design surface tiny.
- **v1 restriction:** elide only when the lambda captures nothing, or when the generator
  opted in anyway and recovers captures as in Approach 1 (capturing sites may keep the
  tree). Non-capturing/constant-shaped predicates are the common hot path.
- **Implementation locus:** attribute decoding next to `InterceptsLocationAttribute`
  handling in `SourceMethodSymbolWithAttributes`, plus the same lowering site that already
  rewrites intercepted `BoundCall`s (where the receiver is pushed into arg0 today). On the
  order of hundreds of lines plus tests — an order of magnitude smaller than Approach 3.
- **Constraint compliance:** trivially — the public surface is one BCL attribute and a spec
  rule. Nothing about compiler internals leaks.

### What it delivers

O1/O2 as Approach 1; **O3 near-fully** (zero expression trees at non-capturing sites; a
single small closure object remains at capturing sites unless the tree is kept for value
extraction).

### Risks

- Needs LDM sign-off: an attribute that suppresses argument *evaluation* is novel, even if
  restricted to side-effect-free conversions. Expect debate about whether `default` vs. a
  sentinel is passed and about the capturing-lambda carve-out.
- Ties the driver's floor performance to a compiler version; Approach 1 remains the
  down-level behavior.

---

## Approach 3 — Provider-pluggable compile-time queryable chain rewriter (the T01–T22 plan on this branch)

The general compiler feature already interviewed and task-broken-down in
`docs/goals/compile-time-queryable/tasks/`: a `[CompileTimeQueryable("<rewriter AQN>")]`
attribute on the provider's queryable type; the binder detects a terminated chain
(materializers, aggregates, `foreach`, `await` — T06/T08) after simple local-dataflow
capture (T07), and invokes an analyzer-loaded `IQueryableChainRewriter` (T02/T05/T09).

**MongoDB instantiation:** the driver ships
`[CompileTimeQueryable("MongoDB.Driver.Compiler.MongoChainRewriter, MongoDB.Driver.Compiler")]`
on its queryable type. The rewriter receives a `ChainRewriteContext` — chain-call syntax,
public `SemanticModel`, captured locals, diagnostic reporter, fresh-name generator (T03) —
runs the shared MQL translator, and returns **C# source text**:

```csharp
collection.Aggregate()
    .AppendStage<User>(MongoSchemas.Users.Match_AgeGt(min))   // capture referenced directly
    .AppendStage<string>(MongoSchemas.Users.Project_Name)      // static cached stage
    .ToListAsync(ct)
```

The compiler parses, binds, and type-checks the returned text against the chain's
destination type (T10) and substitutes it at the terminator's binding site; `null` with no
diagnostics means silent fallback to the runtime provider (T11).

### Constraint compliance — by construction

This is the design pillar the whole plan is built on (mirrors `[Quotable]`): the rewriter
sees **syntax + public `SemanticModel` in**, produces **source text out**, which re-enters
the compiler through the front door. Bound trees never cross the boundary in either
direction (T09 explicitly wraps the validated substitute with `Conversion.Identity`
internally; nothing internal is surfaced).

### What it delivers

- **O1**: via the shared schema generator (orthogonal, unchanged).
- **O2**: fully, and better than 1/2 — the substitution happens at *binding*, so the bound
  operator spine (including every `Expression.Lambda` that lowering would have emitted) is
  discarded before emit. The fluent form is what actually compiles.
- **O3**: fully, including parameterized queries — captured locals are referenced directly
  in the emitted source (T07 capture list), so no expression trees *and* no closures ever
  exist for translated chains.
- Generality: Postgres/Dapper (the T15 canonical sample), EF-style providers, and MongoDB
  all plug into one mechanism; MongoDB is just the second rewriter.

### Costs / risks

- The largest Roslyn delta of the three: bind-time orchestration, per-compilation caching,
  `SemanticModel` re-entry, DEBUG `IdentifierMap` interactions, EnC/debugging story — all
  already catalogued in T09's edge-case list. Realistically a multi-milestone
  `[Experimental]` feature with LDM involvement.
- Runs provider code inside the compiler (analyzer-grade trust; unconditional exception
  containment per T09).
- IDE behavior (step-into substituted code, "view emitted query") is explicitly out of
  scope for v1 (T21), so the *visibility* half of O2 relies on the spec'd substitution
  rules plus optional sidecar output from the rewriter, not tooling.

---

## Comparison

| | 1 — Generators + interceptors | 2 — + elidable arguments | 3 — Chain rewriter (T01–T22) |
|---|---|---|---|
| Roslyn delta | none | ~1 attribute + emit-layer tweak | new bind-time feature, 22 tasks |
| O1 schemas | ✅ (shared generator) | ✅ | ✅ |
| O2 explicit pipeline | ✅ generated source | ✅ | ✅ strongest (pre-lowering substitution) |
| O3 expression trees | ⚠️ still constructed, ignored | ✅ gone (non-capturing); ⚠️ capturing | ✅ gone, including captures |
| Parameterized queries | tree-walk extraction | tree kept at capturing sites | captures inlined in source |
| Chain shapes | single statement, straight-line | same | local dataflow + branches (T07/T13) |
| Fallback safety | runtime provider remains | same | silent fallback (T11) |
| Who ships it | driver NuGet only | driver + one compiler release | driver + major compiler feature |
| Time to first ship | weeks | months (LDM) | multi-milestone |

## Recommendation — stage them, share the core

1. **Now:** build the schema generator and the MQL translator, ship them under
   **Approach 1**. Zero compiler risk, immediately delivers O1, O2, and the dominant share
   of O3 (the runtime LINQ provider is the expensive part, not the trees), and — critically
   — proves MQL translation fidelity against a real query corpus before any compiler work
   is on the critical path.
2. **End state:** **Approach 3** (this branch's plan). The same translator becomes
   `MongoChainRewriter`; MongoDB becomes the second canonical rewriter next to the T15
   Postgres sample, which strengthens the feature's LDM case (two independent providers,
   one contract).
3. **Approach 2** is an optional stopgap: pursue it only if profiling Approach 1 in
   production shows expression-tree construction is still a top allocator after the
   provider bypass — plausible in tight loops, unlikely to dominate next to driver I/O.

## Considered and rejected

- **Two-pass build (MSBuild task rewrites sources via public Roslyn APIs, then compiles).**
  Zero compiler changes and full O3, but the binary diverges from the source the IDE
  binds: debugging via `#line`, double compile time, Hot Reload/EnC breakage, analyzers
  running against non-final code. The IDE-fidelity cost exceeds Approach 3's compiler cost.
- **Post-compile IL weaving (Cecil/Fody).** Pattern-matching `Expression.Lambda` factory IL
  is brittle across compiler versions and destroys the debugging story.
- **Exposing a lowering hook over bound trees.** Locked out by the hard constraint; the
  source-text contract of Approach 3 exists precisely to avoid it.
