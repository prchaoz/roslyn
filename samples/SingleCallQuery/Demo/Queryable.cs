using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace SingleCallQuery.Demo;

/// <summary>
/// A toy "queryable" that mirrors how <c>IQueryable</c> works: the operators take
/// <see cref="Expression{TDelegate}"/> so the *code* of the predicate/projection is
/// captured as data. Building the chain is cheap (we just stash the expression
/// objects); the cost lands in <see cref="ToArray"/>, the terminal operator.
/// </summary>
public sealed class Source<T>
{
    internal enum Kind { Where, Select }
    internal readonly record struct Op(Kind Kind, LambdaExpression Expr);

    // Root is kept as the non-generic IEnumerable so a Select that changes the element
    // type can still thread the *original* sequence through to the terminal operator.
    internal readonly IEnumerable Root;
    internal readonly IReadOnlyList<Op> Ops;

    internal Source(IEnumerable root, IReadOnlyList<Op> ops) => (Root, Ops) = (root, ops);

    private Source<TNext> Add<TNext>(Op op)
    {
        var next = new List<Op>(Ops.Count + 1);
        next.AddRange(Ops);
        next.Add(op);
        return new Source<TNext>(Root, next);
    }

    public Source<T> Where(Expression<Func<T, bool>> predicate)
        => Add<T>(new Op(Kind.Where, predicate));

    public Source<TResult> Select<TResult>(Expression<Func<T, TResult>> selector)
        => Add<TResult>(new Op(Kind.Select, selector));

    /// <summary>Escape hatch used by generated interceptors to reach the original sequence.</summary>
    public IEnumerable<TRoot> GetRoot<TRoot>() => (IEnumerable<TRoot>)Root;

    /// <summary>
    /// The runtime fallback. This is the path the source generator is trying to avoid:
    /// every expression tree is <see cref="LambdaExpression.Compile"/>d and then invoked
    /// per element via reflection (<see cref="System.Delegate.DynamicInvoke"/>).
    /// </summary>
    public T[] ToArray()
    {
        Console.WriteLine($"[runtime ] compiling {Ops.Count} expression tree(s) + DynamicInvoke walk");
        IEnumerable<object?> cur = Root.Cast<object?>();
        foreach (var op in Ops)
        {
            var del = op.Expr.Compile();          // <- allocation + JIT of the tree
            cur = op.Kind == Kind.Where
                ? cur.Where(x => (bool)del.DynamicInvoke(x)!)
                : cur.Select(x => del.DynamicInvoke(x)).ToList();
        }
        return cur.Cast<T>().ToArray();
    }
}

public static class Query
{
    public static Source<T> From<T>(IEnumerable<T> items)
        => new Source<T>(items, Array.Empty<Source<T>.Op>());
}
