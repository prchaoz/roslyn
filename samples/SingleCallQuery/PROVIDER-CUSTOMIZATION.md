# Making the lowering provider-customizable

`LOWERING-DESIGN.md` fuses a chain into an **in-memory loop** — fixed, provider-agnostic
semantics. But `IQueryable`'s whole point is that different providers (EF SqlServer, Cosmos,
a REST API) translate the *same* chain differently. This document is the plan for making the
compile-time rewrite **customizable per `QueryProvider`**.

## The constraint that dictates the architecture

Roslyn phase ordering: **`LocalRewriter` runs at emit time; source generators run earlier and
can only add source.** So the compiler's lowering *cannot call into a provider at lower-time*.
Consequence: provider-specific translation must be **generator-driven**. The language's job is
to **standardize the hand-off and do the dispatch**, not to host a provider's SQL generator
inside the compiler.

| Concern | Where it lives | Why |
|---|---|---|
| Recognize chain, build normalized query IR | Shared analyzer **SDK** (Roslyn-API library) | Every provider generator reuses it instead of re-parsing C# |
| Pick which provider applies | **Static dispatch** on the root's type | Only thing known at the call site |
| Translate IR → emitted code | **Provider-shipped generator** | Providers run as generators; compiler can't run them at lower-time |
| Remove residual runtime tree allocation | **The one real language change** | Only the compiler can elide `Expression<>` lowering |

## 1. Dispatch — provider selected at compile time

```csharp
[StaticQueryRoot(typeof(EfSqlServer.Translator))]   // on DbSet<T> / provider IQueryable
public sealed class DbSet<T> : IQueryable<T> { … }
```

The SDK reads that attribute off the **static type** of the chain's source. `DbSet<User>` →
`EfSqlServer.Translator`. A bare `IQueryable<T>` with no static provider → **fall back to the
runtime path**. Different call sites can resolve to different providers; dispatch is the
static type. This is the "provider must be statically known" constraint as the routing key.

## 2. The contract — normalized Query IR

The SDK hands the provider generator a stable, provider-agnostic model:

```
QueryIR {
  ProviderTranslator : INamedTypeSymbol      // EfSqlServer.Translator
  Root  : { EntityType, RootAccessExpr }      // how to reach the connection/DbSet at runtime
  Ops   : [ { Kind: Where|Select|OrderBy|Join|…, Lambda: <bound structure> } ]
  Site  : InterceptableLocation               // for the interceptor hand-off
}
```

The lambda shape is preserved as **build-time data the generator walks** (so SQL can read the
structure of `p => p.Age >= 18`), never as a runtime allocation.

## 3. Provider extension API

The SDK owns recognition, IR construction, interception wiring, capability checks, and
fallback. The provider implements only the mapping:

```csharp
public sealed class Translator : QueryTranslatorBase
{
    protected override OperatorSet Supported =>
        OperatorSet.Where | OperatorSet.Select | OperatorSet.OrderBy | OperatorSet.Take;

    protected override void Emit(QueryIR ir, CodeBuilder cb)
    {
        var sql = SqlBuilder.From(ir);                 // provider's existing translator
        cb.EmitInterceptor(ir.Site, body: $$"""
            using var __cmd = {{cb.RootConn(ir)}}.CreateCommand();
            __cmd.CommandText = {{sql.Text.AsLiteral()}};
            {{sql.EmitParameterBinds()}}
            var __out = new List<{{cb.ElementType(ir)}}>();
            using var __r = __cmd.ExecuteReader();
            while (__r.Read()) __out.Add({{cb.Materialize(ir)}});
            return __out.ToArray();
            """);
    }
}
```

Emitted runtime code = baked SQL + materialization loop. **No expression tree built at
runtime, no provider walk at runtime** — both moved to build time. Constants are parameterized;
SQL is fixed at the call site. A translation failure becomes a **compile-time diagnostic**.

## 4. Strategy-dispatched lowering (ties to BoundFusedQuery)

- **No static provider (`IEnumerable`)** → real `LocalRewriter` lowering: the in-memory fused
  loop from `LOWERING-DESIGN.md`. Lives in the compiler.
- **Static provider (`IQueryable`)** → SDK + provider generator emit the translated single
  call. The compiler's role shrinks to recognition + routing.

Capability negotiation: an operator outside the provider's `Supported` set → SDK falls back to
the ordinary runtime `IQueryable` path (or emits a diagnostic — provider's choice). Semantics
are always preserved; the feature is an optimization that degrades to today's behavior.

## 5. The one genuine language change + the honest limit

Everything above is buildable today on generators + a shared SDK, with **one residual cost**:
because `Where`/`Select` take `Expression<Func<>>`, the trees are still *allocated* at runtime
(the generator consumed them at build time; the interceptor ignores them). Removing that needs
a new language primitive — a compile-time expression parameter:

```csharp
public static IQueryable<T> Where<T>(this IQueryable<T> q, [Comptime] Expression<Func<T,bool>> pred);
```

`[Comptime]` = *when a static translator exists at this call site, do not lower the lambda to a
runtime tree; it was consumed at build time.* This is the dual of interceptors (which replace
the call target but not argument construction) and is the missing piece for a true zero-overhead
single call.

Deeper limit, stated plainly: the provider still maintains a **build-time translator** (analyzer
code). "Write the provider once, works identically at compile time and runtime" would need a
general `comptime` execution engine Roslyn doesn't have — so EF-style providers keep two
translators (runtime + build-time). That is the real cost of the feature.
