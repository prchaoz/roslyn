# T07 — Intra-method dataflow chain capture

Extend chain capture through simple local-variable dataflow within a single method, until a terminator is reached. No cross-method tracking.

Cite — `chain-boundary`: *"(b) Follow simple local-variable dataflow within a method until terminator"*.

## Outcome

Once T06 has tagged a call as belonging to a chain of the marked queryable type, capture must follow assignments through method-local variables until a terminator (T08) consumes the chain. Concretely:

- A forward, intra-method bound-tree walk seeded at each "root" expression of the marked type — the producer call (e.g. `Db.Customers`), a parameter, a field/property read, or a previously captured chain local's reference. The walk threads reaching-definitions of locals whose declared or inferred type is the marked queryable type.
- Only **single-write locals** participate: the local has exactly one textual write (the declaration, or one subsequent assignment) that lexically precedes every read of the local. Multi-write locals (including `q = q.Where(...)` rebinding) require either SSA-style renaming or the simpler conservative rule "stop the chain at the second textual write" — pick the simple rule for v1 and emit no chain for the prior reads.
- Locals whose address is taken, locals declared with `ref` / `ref readonly`, locals captured by a nested lambda, anonymous method, or local function, and locals used as `out`/`ref` arguments are excluded from the participating set — their value can flow outside the binder's view and the chain identity becomes unverifiable.
- The walker records, per chain-rooted expression, the ordered list of operator calls `[op1, op2, …, opN]` that flow into the terminator, plus the original source spans of any traversed locals (the terminator needs them to assemble `ChainRewriteContext`).
- Bail-out is the safe fallback per T11: if any condition below is detected the chain is abandoned and the original `BoundCall` tree is preserved unchanged, so the runtime IQueryable plumbing executes normally.

The walk is **not** a full reaching-definitions analysis; the v1 contract is "name appears, declared in this method, type matches, single assignment, no escape — fold it; else give up." This mirrors how nullable-flow gives up on locals it cannot track precisely.

## Files to touch

- `src/Compilers/CSharp/Portable/Binder/Binder_Invocation.cs` — chain assembly. The chain head produced by T06 needs a follow-up step that, when the immediate receiver is a `BoundLocal`, consults the new helper to discover the local's reaching definition. Plumb the result into the `BoundCompileTimeQueryableChain` node introduced in T06.
- `src/Compilers/CSharp/Portable/FlowAnalysis/` — new helper, e.g. `CompileTimeQueryableChainCaptureWalker.cs`, modeled on the `BoundTreeWalker` pattern used by `DataFlowsInWalker` / `DefiniteAssignment` rather than a full `AbstractFlowPass` subclass. It does not need the lattice machinery — a single-pass walk of the enclosing method body, indexed by local symbol, is enough because we bail out on any structural complication.
- Coordinate with T06's `BoundCompileTimeQueryableChain` node: T07 extends that node with the resolved root expression and the operator-call list. The node type, factory, and `BoundNodeKind` entries are owned by T06; T07 only adds fields.
- Treat `SemanticModel.AnalyzeDataFlow` as off-limits — it re-enters the binder, and T07 itself runs during binding (in `Binder_Invocation`). The helper must be a direct `BoundTreeWalker` over the enclosing method body to avoid binder-reentrant recursion.

## Dependencies

- **Built on T06** — needs the chain-detection predicate and the `BoundCompileTimeQueryableChain` carrier node.
- **Feeds T08** (terminators) — terminator recognition consumes the captured operator list and ends the chain.
- **Feeds T09** (rewriter invocation) — invocation site receives the captured chain via `ChainRewriteContext`.
- **Blocked on T13** (branched chains) for the data-model question — see *Open questions*. The shape of the captured artefact (per-terminator path vs whole DAG) determines whether the walker emits one chain per terminator or one DAG per root.
- **Sensitive to T12** (IAsyncEnumerable scope) — if a marked queryable type can transition mid-stream into `IAsyncEnumerable<T>`, the walker either has to keep following or has to truncate at the transition.

## Edge cases / risks

- `var q = ...; q = q.Where(...);` — multi-assignment to the same local of the marked type. The simple v1 rule treats the second write as a chain break; smarter handling requires SSA versioning.
- `var q = cond ? a.Where(...) : b.Where(...);` — conditional initializer. Single assignment, but the RHS is a `BoundConditionalOperator` whose branches each produce a sub-chain. Either fold both branches into a joined chain (lattice-style) or bail out.
- Assignment inside a loop (`while` / `for` / `foreach`) — the local is textually single-write but executes many times per call, so reaching defs at the use site form a control-flow merge with the loop back-edge; bail out.
- `using var q = ...;` declarations — the local is single-assignment, but its disposal at scope end is observable. Capture is safe only if the terminator runs before the implicit `Dispose`; otherwise the dispose semantics of the marked type matter.
- Pattern variables (`is Marked q`, `case Marked q:`) — the local is conditionally assigned. Treat as a fresh single-assignment root inside the pattern's true-branch, or bail out.
- `out` parameter assignment (`TryGetQuery(out var q)`) — the assignment crosses a method boundary; cross-method tracking is explicitly out of scope. Bail out.
- Closures: locals captured by a nested lambda / anonymous method / local function — the lambda may mutate or observe `q` outside the visible flow. Bail out unconditionally.
- `yield return` of a chain piece — the value flows out of the method and back in on resumption. Bail out.
- `ref` / `scoped ref` locals of the marked type — bail out; the binder cannot prove the alias does not escape.
- Tuple deconstruction (`var (q, _) = …`) targeting the marked type — treat as a single assignment if the RHS is a literal tuple constructor; otherwise bail out.
- Re-entry via `goto` / labelled `break` / `continue` (the new feature in this branch) — control-flow merges defeat the single-write invariant. Bail out on any back-edge into the dominator.

## Open questions

- **T13 (branched chains)** — if a single local is consumed by two downstream terminators (`var q = ...; var a = q.ToList(); var b = q.Count();`), is the captured artefact two independent linear chains, one DAG shared by both terminators, or unconditionally fallback? T07's walker shape depends on this and cannot be finalized until T13 lands.
- **T12 (IAsyncEnumerable)** — if a chain rooted at the marked type transitions mid-stream into `IAsyncEnumerable<T>` (e.g. via an `AsAsyncEnumerable()`-style operator), does dataflow continue across the transition, or does the chain terminate at the transition boundary?
