# Single-call query generator (interceptor prototype)

A proof-of-concept answer to: *"Can `query.Select(…).Where(…).ToArray()` compile to a
single call instead of building an expression tree and walking it at runtime?"*

**Yes — when the provider is known at the call site.** This sample shows it using C#
**interceptors** plus a source generator. The whole `Where(…).Select(…).ToArray()`
chain is replaced, at the terminal `ToArray()` call site, by one generated method that
is a single fused loop with the lambda *bodies inlined as ordinary code*. No expression
tree is compiled, no provider walks anything at runtime.

## Layout

| File | Role |
|------|------|
| `Demo/Queryable.cs` | Toy `Source<T>` whose `Where`/`Select` take `Expression<Func<>>` (mirrors `IQueryable`). Its `ToArray()` is the **runtime fallback**: it `Compile()`s every expression tree and invokes it per-element via `DynamicInvoke` — the slow, late-bound path. |
| `Demo/Program.cs` | A `Query.From(people).Where(…).Select(…).ToArray()` chain — the interception target. |
| `Generator/SingleCallGenerator.cs` | An `IIncrementalGenerator` that finds the chain, reads the lambda bodies from syntax, and emits one interceptor. |

## How the generator works

1. Find every `…ToArray()` whose symbol is `Source<T>.ToArray` (ignores `List.ToArray()` etc.).
2. Walk the receiver chain inward collecting `Where`/`Select` stages.
3. Rewrite each lambda's parameter to a cursor placeholder (a `CSharpSyntaxRewriter`) so
   stages can be threaded without name collisions.
4. Ask Roslyn for a stable call-site reference: `SemanticModel.GetInterceptableLocation(call)`.
5. Emit one interceptor that loops the original sequence once, inlining the bodies:

```csharp
[global::System.Runtime.CompilerServices.InterceptsLocation(1, "….base64.…")]
public static global::Name[] ToArray_0(this global::SingleCallQuery.Demo.Source<global::Name> __q)
{
    var __out = new global::System.Collections.Generic.List<global::Name>();
    foreach (var __e in __q.GetRoot<global::Person>())
    {
        if (!(__e.Age >= 18)) continue;             // inlined Where body
        var __v0 = new Name(__e.First, __e.Age);    // inlined Select body
        __out.Add(__v0);
    }
    return __out.ToArray();
}
```

## Build & run

> Requires the .NET 10 SDK. (It could not be built in the authoring sandbox because the
> SDK download host was blocked by network policy — build locally.)

```bash
cd samples/SingleCallQuery
dotnet run --project Demo
```

Expected output (note which path executes):

```
[gen     ] single fused pass, zero expression trees compiled
  -> Ann (30)
  -> Cy (40)
```

Comment out the `ProjectReference` to `Generator` in `Demo/Demo.csproj` and rerun to see
the fallback instead:

```
[runtime ] compiling 2 expression tree(s) + DynamicInvoke walk
  -> Ann (30)
  -> Cy (40)
```

The generated `.g.cs` is written under `Demo/obj/.../generated/` (EmitCompilerGeneratedFiles).

## What this proves — and the boundary

**Achieved:** the terminal operator no longer compiles or walks expression trees; the
chain collapses to one loop selected at *compile time*. This is exactly how EF Core's
precompiled queries use interceptors.

**The honest limit — and the real design lesson:** an interceptor replaces the *call
target*, not *argument construction*. Because `Where`/`Select` take `Expression<Func<>>`,
the compiler still emits the expression-tree-building code at those call sites, so the
trees are still *allocated* (the interceptor just never compiles or walks them). To remove
even that allocation you'd have to intercept argument lowering too, or have the operators
take plain `Func<>` — i.e. it stops being a library concern and becomes a language feature.

Two further constraints fall out of "provider must be known at the call site":

- **Provider must be static.** Works on `Source<T>`; a bare `IQueryable<T>` passed through
  abstractions can't be specialized — it degrades to the runtime path.
- **No captured locals.** This prototype inlines bodies that reference only the lambda
  parameter. `x => x.Age > threshold` (capturing `threshold`) can't be inlined as-is,
  because an interceptor can't add parameters to thread the capture through. Real systems
  carry captures on the query object; here they fall back to runtime.

These aren't accidental gaps — they're the dual of *why* `IQueryable` defers to runtime in
the first place.

## Going further: true whole-chain fusion

The interceptor only swaps the terminal call. To collapse the *entire* chain into one fused
loop (no intermediate objects, no delegates, no trees) you need a compiler feature, not a
generator — because lowering can consume the whole expression spine before delegates/trees
are materialized. See [`LOWERING-DESIGN.md`](LOWERING-DESIGN.md) for a concrete Roslyn
`BoundFusedQuery` lowering design grounded in this repo's binder and `LocalRewriter`.
