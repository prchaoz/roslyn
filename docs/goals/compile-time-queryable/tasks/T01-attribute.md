# T01 — Add `[CompileTimeQueryable]` attribute

Add `[CompileTimeQueryable(string rewriterAssemblyQualifiedName)]` in `System.Runtime.CompilerServices`, applicable to class types. Mirrors the `[Quotable]` design: a single positional string carrying the assembly-qualified name of the rewriter type that the analyzer-loaded registry (T05) will resolve.

Cite — `trigger`: *"(a) Attribute on the source/provider type (e.g., `[CompileTimeQueryable("…Rewriter")]` on Postgres9Table) — mirrors `[Quotable]`"*.

## Outcome

After this task, the C# compiler can recognize `[System.Runtime.CompilerServices.CompileTimeQueryable("...")]` on a provider/source class type and surface it through `WellKnownType` / `WellKnownMember` lookups. The attribute itself ships from `dotnet/runtime` in a separate PR; this task only reserves the well-known-type slot in Roslyn and registers the constructor descriptor + `AttributeDescription` so binder code (T06) can probe a `NamedTypeSymbol` for the attribute and read the AQN string. No conversion, no diagnostic, no chain detection — purely the symbol-level handle. Externally visible: a new well-known type the compiler can resolve from a referenced runtime, and (in the dotnet/runtime PR) a new public BCL type annotated `[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]` and `[Experimental("RSEXPERIMENTAL00N")]`.

## Files to touch

- `src/Compilers/Core/Portable/WellKnownTypes.cs` — add `System_Runtime_CompilerServices_CompileTimeQueryableAttribute` enum entry before `NextAvailable`, plus matching string in `s_metadataNames`. Enum value will be above `ExtSentinel` (255).
- `src/Compilers/Core/Portable/WellKnownMember.cs` — add `System_Runtime_CompilerServices_CompileTimeQueryableAttribute__ctor`.
- `src/Compilers/Core/Portable/WellKnownMembers.cs` — add 6-byte ctor descriptor: `MemberFlags.Constructor` + `ExtSentinel, (byte)(TargetType - ExtSentinel)` + arity `0` + signature `(void, string)`.
- `src/Compilers/Core/Portable/Symbols/Attributes/AttributeDescription.cs` — `internal static readonly AttributeDescription CompileTimeQueryableAttribute = new(...);`.
- `src/Compilers/CSharp/Test/Symbol/Symbols/MissingSpecialMember.cs` — add skip-list entries in both the `WellKnownType` switch (~line 609, near the existing `System_Runtime_CompilerServices_NullableAttribute` case) and the `WellKnownMember` switch (~line 974, near the existing `System_Runtime_CompilerServices_NullableAttribute__ctorByte` case).
- `src/Tools/SemanticSearch/ReferenceAssemblies/Apis/Microsoft.CodeAnalysis.CSharp.txt` — build auto-updates this; commit the regenerated file.
- *(Separate dotnet/runtime PR, not in this repo)* — actual `CompileTimeQueryableAttribute` class declaration with `[AttributeUsage(...)]` and `[Experimental("RSEXPERIMENTAL00N")]`.

No entries in `src/Compilers/CSharp/Portable/PublicAPI.Unshipped.txt` for this task — the attribute lives in the BCL, and the Roslyn-side `IQueryableChainRewriter` / `ChainRewriteContext` types are T02/T03.

## Dependencies

- **Blocks T05** (discovery): the registry keys off the AQN string this attribute carries — its ctor signature is the contract T05 reads.
- **Blocks T06** (chain detection): the binder identifies provider-marked types via `attribute.IsTargetAttribute(AttributeDescription.CompileTimeQueryableAttribute)` on the receiver's `NamedTypeSymbol`.
- **Blocks T16** (compiler tests): every test fixture needs the well-known type resolvable in the test compilation (typically via an inline source declaration of the attribute, the same pattern existing well-known attributes use in `CSharpTestBase`).
- **Depends on nothing else.** Independent of T02 (rewriter interface), T03 (context), T04 (flag); ordering is "do this first or in parallel with T02/T04."
- **Independent of T20** (no shared plumbing with `[Quotable]`): the well-known-type slot, descriptor, and `AttributeDescription` field are sibling entries, not shared code.

## Edge cases / risks

- **`AttributeUsage` settings are load-bearing.** Picking `Inherited = false` matches `[Quotable]` and avoids "did the rewriter implicitly apply to a derived provider type?" ambiguity that T06's receiver-static-type rule cannot answer cheaply. `AllowMultiple = false` is the v1 assumption; lifting it later would require a list-valued registry key in T05.
- **AQN string is brittle.** Renaming the rewriter's assembly or strong-name silently breaks every consuming compilation. Same trade-off `[Quotable]` accepts; surface it in the spec (T17) rather than papering over.
- **Generic provider types.** A receiver of type `MyTable<TRow>` carries the attribute on the open generic definition; T06's lookup must resolve through constructed types. The attribute shape is fine; the risk is in T06, but T01 should not pick a representation (e.g., per-instantiation AQN) that forecloses that.
- **`ExtSentinel` encoding.** The new `WellKnownType` value will cross the 256 boundary, so the `WellKnownMembers.cs` byte stream must use `(byte)WellKnownType.ExtSentinel, (byte)(TargetType - ExtSentinel)`. Forgetting this corrupts the descriptor and surfaces as `MissingSpecialMember` test failures rather than as a clear diagnostic.
- **Forgetting the second `MissingSpecialMember` skip list.** `MissingSpecialMember.cs` has separate switches for `WellKnownType` and `WellKnownMember`; missing the member-side entry trips `AllWellKnownTypeMembers` even after the type-side entry passes.
- **Until the dotnet/runtime PR merges**, every Roslyn test must declare the attribute inline in source — same pattern as other BCL types still in flight. The Roslyn-side registration must therefore tolerate the attribute being defined in the user's compilation (not just `mscorlib`/`System.Runtime`).

## Open questions

- **T12 (`IAsyncEnumerable<T>` scope).** No effect on the attribute *shape* — a class can implement either or both regardless. But if T12 resolves to "async chains stay captured," the spec (T17) should note that `[CompileTimeQueryable]` on a type implementing `IAsyncEnumerable<T>` is meaningful; this task doesn't need to change.
- **T13 (branched chains).** Whichever model T13 picks, the attribute remains a single positional AQN. Flagged only because if T13 lands on "whole-DAG visibility for the rewriter," some teams may want a second parameter on the attribute (e.g., a capability bitfield); explicitly out of scope here, but worth noting before locking the ctor signature.
- **T14 (BCL helpers scope).** If BCL helpers are in scope, they would co-ship with the attribute from `dotnet/runtime` — possibly bundled in the same PR. No Roslyn-side change either way; surfaces only as a deliverables/coordination question.
