# Compile-time chain fusion via Roslyn lowering — design

Goal: lower a fusible fluent chain such as

```csharp
src.Where(p => p.Age >= 18).Select(p => p.Name).ToArray()
```

into a **single fused loop** at compile time — no intermediate `Source`/enumerators, no
delegates, no expression trees. This is a pattern-based feature in the same family as
`foreach`, `await`, query expressions, and interpolated-string handlers: the compiler
*recognizes* the pattern in the binder and *rewrites* it in `LocalRewriter`.

File paths below are real locations in this repo (`src/Compilers/...`); the code is an
illustrative skeleton, not a compiling patch.

---

## 1. Opt-in contract (BCL side)

Operators opt into fusion by implementing a recognized transducer/sink pattern. The
compiler weaves whatever stages it finds, so third-party operators work too.

```csharp
// streaming stage: return false to drop the element
public interface IFusedStage<TIn, TOut> { bool Apply(TIn input, out TOut output); }

// terminal fold with short-circuit (First/Any/Take need the bool)
public interface IFusedSink<TIn, TResult> {
    void Init();
    bool Accept(TIn input);   // false => stop the loop
    TResult Finish();
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class FusibleAttribute : Attribute { }
```

`Where` → stage copying input→output, returning the predicate. `Select` → always true,
`output = selector(input)`. `ToArray` → a sink that appends and `Finish`es to an array.

---

## 2. Feature gate + well-known symbols

- **`src/Compilers/CSharp/Portable/Errors/MessageID.cs`** — add an entry and gate it:

  ```csharp
  IDS_FeatureFusedQueries = MessageID.IDS_FeatureFirst + NNN,
  // in LanguageVersion check:
  case MessageID.IDS_FeatureFusedQueries: return LanguageVersion.Preview;
  ```

- **`src/Compilers/Core/Portable/WellKnownTypes.cs` / `WellKnownMembers.cs`** — register
  `System.Runtime.CompilerServices.FusibleAttribute`, `IFusedStage\`2`, `IFusedSink\`2` and
  their members (`Apply`, `Init`, `Accept`, `Finish`), mirroring how
  `System_Runtime_CompilerServices_DefaultInterpolatedStringHandler` is registered
  (WellKnownTypes.cs:318 / :693).

---

## 3. New bound nodes

**`src/Compilers/CSharp/Portable/BoundTree/BoundNodes.xml`** — add (schema matches the
existing `BoundForEachStatement` at line 1261). Mark `DoesNotSurvive="LocalRewriting"`
because the node is fully rewritten away, exactly like `BoundUsingStatement` (line 1294).

```xml
<Node Name="BoundFusedQuery" Base="BoundExpression" DoesNotSurvive="LocalRewriting">
  <!-- Source bound through the foreach collection pattern, so we inherit the
       array / Span<T> / IEnumerable fast paths from ForEachEnumeratorInfo. -->
  <Field Name="Source" Type="BoundExpression"/>
  <Field Name="EnumeratorInfoOpt" Type="ForEachEnumeratorInfo?"/>
  <Field Name="ElementPlaceholder" Type="BoundValuePlaceholder" SkipInVisitor="true"/>
  <Field Name="Stages" Type="ImmutableArray&lt;BoundFusedStage&gt;"/>
  <Field Name="Sink" Type="BoundFusedSink"/>
</Node>

<Node Name="BoundFusedStage" Base="BoundNode" DoesNotSurvive="LocalRewriting">
  <Field Name="IsWhere" Type="bool"/>
  <!-- Unconverted lambda: kept so the body can be inlined, parameter substituted. -->
  <Field Name="Lambda" Type="BoundLambda"/>
</Node>

<Node Name="BoundFusedSink" Base="BoundNode" DoesNotSurvive="LocalRewriting">
  <Field Name="InitMethod"   Type="MethodSymbol"/>
  <Field Name="AcceptMethod" Type="MethodSymbol"/>
  <Field Name="FinishMethod" Type="MethodSymbol"/>
  <Field Name="BuilderType"  Type="TypeSymbol"/>   <!-- e.g. PooledArrayBuilder<string> -->
</Node>
```

Regenerating `BoundNodes.xml` produces the node classes + visitor hooks automatically.

---

## 4. Binder recognition

**New `src/Compilers/CSharp/Portable/Binder/Binder_FusedQuery.cs`**, called from
`Binder_Invocation.cs` when binding the *outermost* invocation of an expression spine.

```csharp
// Pseudocode skeleton.
private BoundExpression? TryBindFusedQuery(InvocationExpressionSyntax node, BoundCall terminal)
{
    // 1. Terminal must be a [Fusible] sink-shaped method (ToArray/Count/First...).
    if (!IsFusibleSink(terminal.Method, out var sink)) return null;

    // 2. Walk the receiver spine; each must be a [Fusible] stage (Where/Select).
    var stages = ArrayBuilder<BoundFusedStage>.GetInstance();
    BoundExpression current = terminal.ReceiverOpt;
    while (current is BoundCall op && IsFusibleStage(op.Method, out bool isWhere))
    {
        // Argument must be a lambda *literal* so we can inline its body; otherwise bail.
        if (op.Arguments[0] is not BoundLambda lambda) { stages.Free(); return null; }
        stages.Add(new BoundFusedStage(op.Syntax, isWhere, lambda));
        current = op.ReceiverOpt;
    }
    if (stages.Count == 0) { stages.Free(); return null; }
    stages.ReverseContents();

    // 3. Bind the root via the existing foreach collection pattern -> ForEachEnumeratorInfo,
    //    so lowering can reuse the array/Span/IEnumerable fast paths.
    if (!TryGetForEachInfo(current, out var info, out var element)) return null;

    CheckFeatureAvailability(node, MessageID.IDS_FeatureFusedQueries, diagnostics);
    return new BoundFusedQuery(node, current, info, element, stages.ToImmutableAndFree(), sink);
}
```

Key points:
- Lambda arguments are kept **unconverted** (no `Func<>`/`Expression<>` conversion), so the
  bodies survive for inlining — analogous to how expression-tree lambdas are kept as bound
  nodes for `ExpressionLambdaRewriter`.
- They're still bound as ordinary code, so normal diagnostics and nullable analysis run.
- **Fallback is mandatory**: any failed precondition (`return null`) means the normal
  `BoundCall` chain is bound instead. The feature is a pure, semantics-preserving optimization.

`NullableWalker` / `DefiniteAssignmentPass` get `VisitFusedQuery` overrides that visit the
children (source, lambda bodies, sink args) so flow analysis sees through the node.

---

## 5. Lowering — the actual fusion

**New `src/Compilers/CSharp/Portable/Lowering/LocalRewriter/LocalRewriter_FusedQuery.cs`.**
Uses the real `SyntheticBoundNodeFactory` (`_factory`) idioms found in this repo:
`Block(locals, statements)` (line 471), `SynthesizedLocal` (595), `Assignment` (416),
`If` (948), `Goto`/`Label` (1149/1154). The source loop is produced by reusing the
foreach rewriter (`LocalRewriter_ForEachStatement.cs:34`).

```csharp
public override BoundNode VisitFusedQuery(BoundFusedQuery node)
{
    var F = _factory;

    // builder local: var __acc = sink.Init-shape;
    var acc = F.SynthesizedLocal(node.Sink.BuilderType);
    var init = F.Assignment(F.Local(acc), F.New(node.Sink.BuilderType)); // sink.Init

    // Build the per-element body: inline each stage's lambda body, substituting the param.
    BoundExpression cur = node.ElementPlaceholder;          // the foreach element
    var body = ArrayBuilder<BoundStatement>.GetInstance();
    foreach (var stage in node.Stages)
    {
        var inlined = InlineLambdaBody(stage.Lambda, cur);  // param -> cur (see §6)
        if (stage.IsWhere)
            // if (!cond) continue;
            body.Add(F.If(F.Not(inlined), F.Goto(continueLabel)));
        else
        {
            var v = F.SynthesizedLocal(stage.Lambda.Symbol.ReturnType);
            body.Add(F.Assignment(F.Local(v), inlined));
            cur = F.Local(v);
        }
    }
    // sink.Accept(cur): if it returns false, break.
    body.Add(F.If(F.Not(F.Call(F.Local(acc), node.Sink.AcceptMethod, cur)),
                  F.Goto(breakLabel)));

    // Wrap body in a foreach over the source, reusing the existing rewriter so we inherit
    // array / Span<T> / IEnumerable fast paths.
    BoundStatement loop = MakeFusedForEach(node, F.Block(body.ToImmutableAndFree()),
                                           continueLabel, breakLabel);

    // var result = __acc.Finish();  -> the whole thing is a BoundSequence expression.
    var result = F.Call(F.Local(acc), node.Sink.FinishMethod);
    return F.Sequence(
        locals: ImmutableArray.Create(acc),
        sideEffects: ImmutableArray.Create<BoundExpression>(F.Sequence(init), loop-as-expr),
        result: result);
}
```

Lowered shape (conceptually) for the running example:

```csharp
{
    var __acc = new PooledArrayBuilder<string>();   // sink.Init
    foreach (var __e in src)                        // foreach-pattern lowering (fast paths)
    {
        if (!(__e.Age >= 18)) continue;             // Where body inlined, p -> __e
        var __v = __e.Name;                         // Select body inlined
        if (!__acc.AddAndContinue(__v)) break;      // sink.Accept (short-circuit aware)
    }
    /* value */ __acc.MoveToArray()                 // sink.Finish
}
```

One pass; zero intermediate `Source` objects, enumerators, delegates, or expression trees.
Emit then proceeds normally.

---

## 6. Inlining lambda bodies (`InlineLambdaBody`)

A small `BoundTreeRewriter` that replaces the lambda's `BoundParameter` with the current
value (a temp or placeholder), and hoists any lambda-local declarations into the loop body.

```csharp
private BoundExpression InlineLambdaBody(BoundLambda lambda, BoundExpression arg)
{
    var p = lambda.Symbol.Parameters[0];
    // Spill arg into a temp if it isn't already a simple local, to preserve single-eval.
    return new ParameterInliner(p, arg).Visit(lambda.Body.Statements.SingleExpression());
}

private sealed class ParameterInliner : BoundTreeRewriter
{
    public override BoundNode? VisitParameter(BoundParameter node)
        => node.ParameterSymbol == _p ? _arg : node;
}
```

**Why this solves the capture problem the interceptor couldn't:** inlining happens at the
call site, where any variables the lambda captured are already in scope. `x => x.Age > threshold`
just lowers to `__e.Age > threshold` — no closure, no threading.

---

## 7. Edge cases (the real work)

| Case | Handling |
|------|----------|
| Short-circuit terminals (`First`/`Any`/`Take`) | `Accept` returns `false` → loop `break`s. |
| Buffering operators (`OrderBy`/`Distinct`/`GroupBy`) | **Fusion barrier**: fuse up to it, materialize, fuse the next segment. Needs a "stateful stage" kind. |
| Non-literal `Func<>` argument | Can't inline → emit a *call* to it inside the fused loop (still fuses the loop), or fall back. |
| Evaluation order / side effects | Source evaluated once (spilled to temp); left-to-right preserved. |
| Exceptions | A throwing predicate throws at the same logical point — inlining preserves it. |
| Block-bodied lambdas | Hoist statements into the loop, write result to a temp. |
| Anonymous types / generics | Fine — it's all in-method lowering, no signature to name (unlike interceptors). |
| Debuggability | Attach the lambda's syntax to inlined nodes so sequence points/breakpoints map back. |

---

## 8. Tests

- `src/Compilers/CSharp/Test/Semantic` — recognition, fallback, feature-gate diagnostics,
  nullable flow through the node.
- `src/Compilers/CSharp/Test/Emit` — **IL baselines** asserting a single loop and no
  allocations. That baseline is the proof the feature does what it claims.

---

## 9. Why this is the right layer

A source generator / interceptor can only replace the terminal call target — the
`Where`/`Select` calls and their expression-tree arguments still execute. Lowering operates
on the **whole expression spine before delegates/trees are materialized**, so it can consume
the entire chain *and* inline captured-variable bodies. That combination is only reachable
inside the compiler — which is exactly why this belongs in `LocalRewriter`, not a generator.
