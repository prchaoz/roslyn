# T13 — *(unresolved)* Branched-chain handling

Pick the model for chains that form a DAG (a local consumed by multiple downstream calls):

- per-terminator (each terminated path is its own chain),
- whole-DAG (rewriter sees the prefix-sharing tree),
- linear-only v1 (any branching → fallback per T11).

Blocks T03 (shape of `ChainRewriteContext`) and T09.

Cite — `rewriter-input` *(skipped with note)*: *"I think about the tree of possible chains with branches"*.

## The question

T06 captures any call whose receiver static type is the provider-marked queryable type as part of a chain. T07 extends that capture through method-local dataflow until a terminator is reached. With T07 on, the same local can be the receiver of more than one terminated call — the captured artefact is no longer a list, it is a directed acyclic graph (a tree if every branching local is single-assignment, an SSA-DAG once T07's "stop at second write" rule is relaxed).

The interview answer left this unresolved: *"I think about the tree of possible chains with branches"*. T13 has to decide what shape the compiler hands to `IQueryableChainRewriter.Rewrite` — does the rewriter see one path at a time, the whole prefix-sharing tree at once, or are branched chains simply out of scope for v1?

### Canonical branched-chain example

```csharp
var q = db.Customers
           .Where(c => c.Active)
           .Select(c => new { c.Id, c.Name });

var page = q.OrderBy(x => x.Name).Skip(20).Take(10).ToList();   // terminator A
var total = q.Count();                                          // terminator B
```

`q` is single-assignment, declared and consumed inside one method, no escape — exactly the shape T07 captures. Both terminator paths share the prefix `db.Customers.Where(...).Select(...)`. The branch is benign syntactically (no control flow involved), but the shared prefix is the entire interesting case: it is both the optimization opportunity (rewrite once, parameterize twice) and the source of the rewriter-contract complexity (one bound-tree node now has two consumers).

### Downstream tasks the answer changes

- **T03 (`ChainRewriteContext` shape).** Decides whether `ChainCalls` is `ImmutableArray<ChainCall>` (linear), a tree of `ChainCall` nodes with explicit fan-out (whole-DAG), or stays linear with the branching case excluded by classification (linear-only). T03 has explicitly marked this field "Pending T13".
- **T07 (intra-method dataflow capture).** Decides whether the walker emits one chain artefact per terminator (per-terminator) or one DAG artefact per root local (whole-DAG) or unconditionally bails on the second consumer (linear-only). T07's *Open questions* call this out by name.
- **T09 (rewriter invocation at the binding site).** Decides whether the rewriter is called N times for N terminators or once per DAG. Affects substitution-site count, fresh-name uniqueness, diagnostic deduplication (the rewriter may report the same issue on the shared prefix twice under per-terminator), and the call into `Binder_Conversions`-style materialization (`Binder_Invocation.cs` for chains).

## Options

### Option A — Per-terminator

**Rule.** Every terminated path is its own linear chain. A DAG with N terminators yields N independent linear chains, each starting at the chain root and ending at one terminator. The shared prefix is duplicated in each context.

**`ChainRewriteContext` shape.** Unchanged from T03's placeholder linear draft: `ChainCalls` is `ImmutableArray<ChainCall>`, ordered source→terminator. No fan-out node, no DAG-walking API. The two contexts handed out for the canonical example each carry their own copy of the prefix's `ChainCall` instances (or share interned references, but the API still presents them as flat arrays).

**Worker invocation count.** One `Rewrite` call per terminator. For the canonical example: two calls — one for the `ToList()` path, one for the `Count()` path.

**Runtime fallback (T11).** Engages per terminator. If one terminator declines (returns `null` with no diagnostics) and the other rewrites successfully, the declining path runs against the provider's runtime `IQueryable` plumbing while the other compiles to the rewriter's substituted source. Two independent decisions.

**Worked C# example, before-and-after the rewriter sees.**

Source (same as canonical):
```csharp
var q = db.Customers.Where(c => c.Active).Select(c => new { c.Id, c.Name });
var page = q.OrderBy(x => x.Name).Skip(20).Take(10).ToList();
var total = q.Count();
```

What the rewriter sees, call 1 (`ChainRewriteContext` for the `ToList` terminator):
```
ChainCalls = [
  Customers,                       // root: PropertyAccess on db
  Where(c => c.Active),
  Select(c => new { c.Id, c.Name }),
  OrderBy(x => x.Name),
  Skip(20),
  Take(10),
  ToList()                          // terminator
]
Lambdas = [c => c.Active, c => new { c.Id, c.Name }, x => x.Name]
Captures = []                       // no outer-scope reads in this example
```

What the rewriter sees, call 2 (`ChainRewriteContext` for the `Count` terminator):
```
ChainCalls = [
  Customers,
  Where(c => c.Active),
  Select(c => new { c.Id, c.Name }),
  Count()
]
Lambdas = [c => c.Active, c => new { c.Id, c.Name }]
Captures = []
```

After substitution: each terminator's call site is replaced independently by whatever source text the rewriter returns; `q` itself is no longer referenced from the substituted call sites and is dead-coded by lowering.

### Option B — Whole-DAG

**Rule.** Every connected, terminated DAG rooted at a single chain-root local is one chain. The rewriter sees the prefix-sharing tree as a single artefact, with explicit fan-out nodes at every branching local.

**`ChainRewriteContext` shape.** `ChainCalls` becomes a tree (or DAG once SSA is added) rather than a flat array. Sketch:

```csharp
public sealed class ChainNode
{
    public ImmutableArray<ChainCall> Operators { get; }     // linear run between branches
    public ImmutableArray<ChainNode> Branches { get; }      // empty at terminators; >1 at fan-out
    public ChainTerminator? Terminator { get; }             // non-null only at leaves
}
public sealed class ChainRewriteContext
{
    public ChainNode Root { get; }
    // ...all other T03 members (Lambdas, Captures, Diagnostics, GetFreshName, SemanticModel)
    // are scoped to the whole DAG, not per terminator.
}
```

`Lambdas` and `Captures` cover every lambda / capture across the whole DAG. `GetFreshName` is shared (one counter per DAG), so wrapped-mode (T10) emits names that do not collide across the branches.

**Worker invocation count.** Exactly one `Rewrite` call per chain root. For the canonical example: one call, with `Root` describing the shared prefix `Customers → Where → Select` followed by two `Branches`: one for `OrderBy → Skip → Take → ToList`, one for `Count`.

**Runtime fallback (T11).** One decision for the whole DAG. If the rewriter returns `null`, every terminator in the DAG falls back to runtime; if it returns source text, that source text is responsible for materializing *all* terminators (the rewriter's output replaces both call sites simultaneously, presumably by returning a tuple or by emitting two substitution fragments via an extended T09 contract).

**Worked C# example, before-and-after the rewriter sees.**

Source: same as canonical.

What the rewriter sees, single call:
```
Root = ChainNode {
  Operators = [Customers, Where(c => c.Active), Select(c => new { c.Id, c.Name })],
  Terminator = null,
  Branches = [
    ChainNode {
      Operators = [OrderBy(x => x.Name), Skip(20), Take(10)],
      Terminator = ToList,
      Branches = []
    },
    ChainNode {
      Operators = [],
      Terminator = Count,
      Branches = []
    }
  ]
}
Lambdas = [c => c.Active, c => new { c.Id, c.Name }, x => x.Name]
Captures = []
```

After substitution: T09 must dispatch the rewriter's output to *two* call sites in the original method body. This is the load-bearing surprise — Option B forces T09 to accept either a single source text that materializes both terminators (e.g. a tuple-returning helper plus call-site adaptation) or a per-terminator source-text map keyed by something the rewriter can reference (most naturally the original terminator `InvocationExpressionSyntax`). T03 has to expose that key, and T09 has to gain a "substitute at this list of call sites" path that the linear case never needs.

### Option C — Linear-only v1, branching → fallback per T11

**Rule.** Capture proceeds only as long as each local has exactly one downstream consumer in the captured set. The first time a chain-typed local is referenced by a second downstream call (any call, including a non-terminator chain operator), T07 abandons the entire root and the call sites lower as ordinary `IQueryable` runtime calls.

**`ChainRewriteContext` shape.** Identical to Option A — flat `ChainCalls`. No DAG plumbing in v1.

**Worker invocation count.** One `Rewrite` call per chain root that survived classification. Branched roots produce zero rewriter calls.

**Runtime fallback (T11).** Engages for the whole DAG at classification time, before the rewriter is even invoked. The decision is fully compiler-side. The rewriter never sees a branched chain, so cannot mis-handle one.

**Worked C# example, before-and-after the rewriter sees.**

Source: same as canonical.

What the rewriter sees: **nothing.** Both terminators fall back to runtime. The compiler emits the ordinary `BoundCall` tree for `q.OrderBy(...).Skip(...).Take(...).ToList()` and `q.Count()`, and `q` itself is a normal local of the marked queryable type. The provider's runtime implementation (whatever ships with the marked type — `IQueryable<T>` machinery, a hand-written client-side enumerator, etc.) handles execution.

A subtler variant — `var w = db.Customers.Where(c => c.Active); var x = w.ToList();` where `w` is referenced exactly once downstream — is *not* branched and still gets rewritten under Option C. The fallback fires only when the second reference to a chain-typed local appears.

## Recommendation

Not picking — both axes (simplicity, power) and the risk profile differ in load-bearing ways, and the call belongs to the user.

- **Simplest:** Option C. Reuses T03's placeholder linear shape unchanged, lets T07 ship the conservative "bail on second use" rule already proposed for multi-assignment locals, and removes a whole class of failure modes from T09 and the rewriter contract. The cost is throwing away a real optimization opportunity in the canonical paging+count pattern, which is exactly the case provider authors will demo on day one.
- **Middle ground:** Option A. Same `ChainRewriteContext` shape as Option C, but the rewriter does run for branched chains — it just runs once per terminator and cannot dedupe the shared prefix. The substitution layer (T10) stays simple (one source text per call site). The cost is duplicated work in the rewriter for the prefix and the risk that prefix-stateful translation (e.g. SQL CTE generation) double-emits the shared portion unless the rewriter author manages caching themselves. Per-terminator also produces duplicate diagnostics if the prefix is malformed.
- **Most powerful:** Option B. Matches the user's stated intuition ("tree of possible chains with branches") and lets the rewriter emit a single SQL query with two materializations from one CTE, share a parameter pack across both, etc. The cost is a larger `ChainRewriteContext` surface, a multi-site substitution path in T09 that does not yet exist anywhere in the binder, and a meaningfully harder rewriter author experience (must walk a DAG, must allocate output per terminator). The whole-DAG output also makes T11 silent fallback coarser — a partial-DAG decline (success on one terminator, decline on another) has no clean expression and likely collapses to "decline the whole DAG."

**Risks worth flagging.** Option B is the only option that adds new public surface that cannot be retrofitted compatibly later; A and C share a linear `ChainCalls` API and can grow toward B in v2 without breaking analyzer authors. Option C is the only option that forecloses on the canonical optimization. Option A is the only option that has the diagnostic-duplication failure mode.

## Files to touch under each option

### Option A — Per-terminator

- `src/Compilers/CSharp/Portable/Binder/Binder_Invocation.cs` — at the chain-head assembly site (T06), the per-terminator path iterates over each terminator in the captured DAG and constructs one `BoundCompileTimeQueryableChain`-equivalent node per terminator. Each carries its own materialization context.
- T03 `ChainRewriteContext` (in `src/Compilers/CSharp/Portable/CompileTimeQueryable/`) — ships as drafted: flat `ImmutableArray<ChainCall>` plus a single terminator. No DAG plumbing.
- `src/Compilers/CSharp/Portable/FlowAnalysis/CompileTimeQueryableChainCaptureWalker.cs` (new, owned by T07) — walks once per chain root, emits one chain artefact per terminator. The shared prefix is interned at the bound-tree level but copied into each artefact's `ChainCalls`.
- `src/Compilers/Core/Portable/CommandLine/Feature.cs` — the existing `compile-time-queryable` flag string suffices; no per-option subflag. Gating in `CSharpCompilation.IsCompileTimeQueryableEnabled` short-circuits all of the above.

### Option B — Whole-DAG

- `src/Compilers/CSharp/Portable/Binder/Binder_Invocation.cs` — at chain-head assembly, build one node per chain root that owns every terminator in the DAG. The materialization layer must accept a multi-site output (either a per-terminator source-text map or a single helper-method emission with adapter call sites at each terminator). The Quotable analog `Binder_Conversions.CreateQuotationConversion` substitutes at a single conversion site; the chain analog needs a substitute-N-sites primitive that does not exist today.
- T03 `ChainRewriteContext` — DAG-shaped: add `ChainNode`, expose `Root`, add the per-terminator key the rewriter uses to attribute its output. Keep `Lambdas`, `Captures`, `GetFreshName`, `Diagnostics`, `SemanticModel` at the context level (single instance shared across the DAG).
- `src/Compilers/CSharp/Portable/FlowAnalysis/CompileTimeQueryableChainCaptureWalker.cs` — emits the DAG directly. The walker has to identify branch points (any chain-typed local with >1 downstream consumer) and assemble fan-out nodes accordingly. Coordination with T07's reaching-definitions choice (single-assignment vs SSA-renaming) is heavier under Option B because branches on multi-assignment locals are common.
- T09 (rewriter invocation) — one call per DAG. The invocation site needs the multi-site substitution helper described above; this is new compiler-side machinery.
- `src/Compilers/Core/Portable/CommandLine/Feature.cs` — same flag.

### Option C — Linear-only v1

- `src/Compilers/CSharp/Portable/Binder/Binder_Invocation.cs` — unchanged from the linear baseline. T06 emits one chain per receiver; T07 emits one chain per terminator only if no second consumer is encountered.
- T03 `ChainRewriteContext` — ships as drafted: flat `ImmutableArray<ChainCall>`. The placeholder note in T03 ("ship a placeholder linear shape and revise") becomes the final shape.
- `src/Compilers/CSharp/Portable/FlowAnalysis/CompileTimeQueryableChainCaptureWalker.cs` — the simplest version: a single pass that flags any chain-typed local with reference count > 1 in the captured set and bails the chain root for that local. Reuses the same conservative bail-out infrastructure T07 already lists for multi-assignment, escaping, and ref locals.
- `src/Compilers/Core/Portable/CommandLine/Feature.cs` — same flag. T11 silent fallback fires for branched chains automatically because no rewriter is invoked.

## Edge cases / risks

### Option A — Per-terminator

- **Duplicate diagnostics on the shared prefix.** A malformed lambda in the shared prefix produces N copies of the same diagnostic (one per terminator). Either the rewriter dedupes, or the compiler dedupes by `(diagnostic id, span)` after collection. T03's `Diagnostics` is per-context, so cross-context dedup needs new plumbing.
- **Prefix-stateful translation traps.** SQL CTE / temporary-table emission, parameter-counter increments, alias generation — anything that mutates rewriter-side state per prefix call — runs twice. Most rewriter authors will not anticipate this on day one.
- **Fresh-name collisions across contexts.** Each context has its own `GetFreshName` counter. If both terminators rewrite into the same enclosing method, names must be unique across both. Seed the counter from a per-call-site value (e.g. the terminator `InvocationExpressionSyntax.SpanStart`) so the two contexts pick disjoint name spaces deterministically.
- **N-terminator blow-up.** A DAG with k terminators triggers k invocations and k substituted source-text outputs. With `q` referenced 10× downstream the binder runs the rewriter 10×. For a Postgres-style sample this is fine; for a code-generator-style rewriter that emits large helper methods, the IL footprint is k× the single-terminator case.
- **Partial decline produces mixed runtime/compile-time execution.** One terminator compiles, another falls back. If the rewriter's compiled output assumes some state established by execution of the runtime fallback (e.g. a shared connection), the user has to know not to rely on cross-terminator effects — a documentation hazard, not a correctness bug.

### Option B — Whole-DAG

- **Multi-site substitution is new compiler machinery.** Nothing in the existing binder substitutes one rewriter output into N call sites. The Quotable design substitutes one returned source text into one conversion site. T09 needs either a new bound-node kind that carries N substitution targets, or an emission pattern where the rewriter returns a helper-method declaration plus N call-site replacement fragments. This is the single biggest implementation cost.
- **Partial decline has no clean expression.** "Compile the `ToList` branch, fall back the `Count` branch" is plausible for a rewriter author to want but breaks the "one Rewrite call, one decision" contract. Either the contract widens (rewriter returns a per-terminator map of source-or-null) or partial decline collapses to whole-DAG decline.
- **Walker complexity scales with branching.** T07's "single-assignment local, no escape" rule is enough for linear chains; for Option B's DAG the walker must additionally compute the *least-common-ancestor* node at every branch to know where the fan-out actually starts. Multi-assignment locals push this into SSA territory.
- **Capture scoping across branches.** A capture introduced inside one branch (e.g. a lambda body in `OrderBy(x => x.Name + suffix)` where `suffix` is an outer local) is part of the whole-DAG `Captures` set even though it is referenced only on one branch. Wrapped-mode substitution (T10) on the *other* branch must not over-include it in its typed capture record.
- **API forward-compatibility is one-way.** Once `ChainRewriteContext.Root` ships as a DAG, downgrading to a flat array in v2 is a breaking change to analyzer authors. Going from a flat array (Option A/C) to a DAG in v2 is additive — the linear shape can survive as a degenerate one-branch DAG.

### Option C — Linear-only v1

- **Throws away the canonical paging+count optimization.** The first thing a Postgres-style sample wants to demonstrate is exactly the `var q = ...; q.ToList(); q.Count()` pattern. Under Option C this falls back to runtime, which silently negates the rewriter's value proposition on its most common demo.
- **Branching detection is whole-method.** T07's per-root single-assignment check is local to the assignment; the "second downstream consumer" check requires walking every reference to the local in the method body. That widens T07's walker scope slightly, though still strictly bounded.
- **Silent fallback may surprise users.** With T11 silent, a user adding a second `q.Count()` line silently regresses from compile-time-translated to runtime-evaluated. No diagnostic indicates the transition. A future opt-in diagnostic ("chain dropped due to branching") would help but is out of scope here.
- **Branch-with-only-one-terminator looks branched but is not.** `var w = root.Where(...); var s = w.Select(...);` has `w` consumed once (by `Select`), so the chain rooted at `root` extends through `s`. T07 must distinguish "second downstream operator that itself is part of the chain" (fine — single consumer downstream from `w`) from "second downstream consumer that terminates separately." The rule is "count terminator-rooted consumers, not all references" — easy to get subtly wrong.
- **v2 migration breaks no API but breaks expectations.** Moving to Option A or B in v2 starts rewriting chains that previously fell back, which changes runtime characteristics (latency, error surface, even result equality if the runtime and compile-time paths differ semantically). Users may rely on the v1 fallback shape.
