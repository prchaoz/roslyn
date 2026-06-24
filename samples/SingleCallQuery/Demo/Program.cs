using System;
using SingleCallQuery.Demo;

var people = new[]
{
    new Person("Ann", 30),
    new Person("Bob", 15),
    new Person("Cy",  40),
    new Person("Di",  17),
};

// This whole chain is the target. Without the generator it runs the runtime
// fallback (compile + walk expression trees). With the generator, the call site
// below is intercepted and replaced by ONE generated method = a single fused loop.
Name[] adults = Query.From(people)
    .Where(p => p.Age >= 18)
    .Select(p => new Name(p.First, p.Age))
    .ToArray();

foreach (var n in adults)
    Console.WriteLine($"  -> {n.First} ({n.Age})");

public sealed record Person(string First, int Age);
public sealed record Name(string First, int Age);
