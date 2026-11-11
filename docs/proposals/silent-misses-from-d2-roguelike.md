---
title: Silent misses found while using the skill on d2-roguelike
status: closed (all eight items addressed in 8efd796)
date: 2026-10-31
---

# Silent misses found while using the skill on d2-roguelike

An agent working in `d2-roguelike` (a Godot C# game, about 30k lines) compared `codecontext query` with `rg` on two
caller lookups, then dug into why one of them came back empty. The biggest problem is that several kinds of C# usage
are never recorded. A query for them succeeds and reports nothing, so an agent that trusts the skill's "use BEFORE
grep" wording concludes there are no users. The fixes below are ordered by how badly each one misleads.

## What happened

| Lookup | codecontext | `rg` |
|---|---|---|
| Callers of `Wilderness.Generate` (a static method) | Right: 8 calling methods. The compact text printed 3 of 8 marked `truncated`; `--json` had all 8. | 10 call sites in one call |
| Readers of `ChunkDef.Interior` (a property on a record) | Target found, no `usedBy` section at all. `--relation REFERENCES` gave the same. | 7 reads in 5 files |
| `ChunkDef`, `AreaPlan`, `WildernessLayout` (records), `Edges` (enum) | `missing`, exit 2 | found at once |
| `PackSize` (two methods share the name) | Both targets printed with `relationships none` | all call sites |
| `PackSize` by canonical identifier | 5 callers | |
| `Wilderness.Generate` (dotted name) | `missing` | |

The first query of a session took 8.7 s (start and index), later ones about 0.2 s.

## 1. Members other than methods have no incoming edges (the property miss)

`CSharpWorkspaceAnalyzer` emits `CALLS` for invocations and `REFERENCES` only from `AddTypeReference`, which returns
unless the symbol is an `INamedTypeSymbol`. Reads and writes of properties, fields, events and enum members produce
no edge at all, so `usedBy` for any of them is always empty.

Fix: emit an edge for every `IPropertySymbol`, `IFieldSymbol`, `IEventSymbol` (and enum member) resolved from an
`IdentifierNameSyntax` or `MemberAccessExpressionSyntax` whose definition is in source. A separate kind
(`READS` / `WRITES`, or `ACCESSES` with a `write` flag) would let `--relation` filter them; `REFERENCES` alone would do
for a first cut. Include them in `usedBy` by default.

## 2. Records, structs and enums are not indexed as symbols

The visitor overrides only `VisitClassDeclaration` and `VisitInterfaceDeclaration`, and `IsIndexedSourceType` accepts
only `ClassDeclarationSyntax` or `InterfaceDeclarationSyntax`. A record, record struct, struct or enum is therefore
`missing`, and references to it are dropped even from classes that are indexed. Methods declared inside a record are
indexed (`SpawnDef.PackSize` shows up as a caller), so the type is the gap, not its members.

Modern C# leans on records heavily; in d2-roguelike most of the rules data is records. Fix: visit
`RecordDeclarationSyntax`, `StructDeclarationSyntax` and `EnumDeclarationSyntax` (or `BaseTypeDeclarationSyntax`
generally) and widen `IsIndexedSourceType` to match. Positional record parameters are properties and should be
indexed as such once item 1 exists.

## 3. Edges from code outside methods and properties are dropped or misattributed

`FindEnclosingIndexedSymbol` looks for an enclosing method, then a property, then falls back to the type. Constructors,
operators, accessors of indexers and events and field initialisers are credited to the containing type, and top-level statements (`Program.cs` in a tool) have no enclosing type, return `null`, and lose their edges
entirely. Fix: walk up to the nearest `BaseMethodDeclarationSyntax`, accessor, or `GlobalStatementSyntax`, and give top-level
statements a synthetic `<Main>` node per file.

## 4. An ambiguous name prints `relationships none`

When a simple name matches two symbols, each target is printed with `relationships none`. `Threat.PackSize` has
five callers; asked for by canonical identifier, all five appear. "None" reads as a fact. Fix: for an ambiguous match,
print `relationships not loaded (ambiguous: pass the identifier)` or load relationships for each match, but never
print "none" for data that was not fetched.

## 5. The compact text truncates harder than the API

The compact text showed `usedBy 3/8 truncated` while `--json` for the same query held all 8 (the API default
`maxRelationships` is 10). Whatever caps the text output, the agent has no flag to lift it, so a full list costs a
second call and 5 KB of JSON. Fix: add `--limit N` (or `--all`) to `query`, and default the text output to the API's
cap. The text format is already compact per line; printing 10 entries instead of 3 costs a few hundred bytes.

## 6. Dotted names do not match

`Wilderness.Generate` and `ChunkDef.Interior` returned `missing`; `Generate` worked. Agents and people write
`Type.Member` by habit. Fix: when the input contains a dot and no exact match exists, split on the last dot and match
member name plus containing type (the API already accepts `containingType`).

## 7. File sections on a member query look like answers

For a method or property, `fileDependencies` / `fileDependents` describe the whole containing file. On the empty
`Interior` query they were the only output, and listed nine files, which is easy to misread as "nine users". Fix: on a
member query, label them `fileDependents (of Wilderness.cs)` or omit them unless asked, and print an explicit
`usedBy 0/0` line when the member has no recorded users.

## 8. Changes to the skill text

The description says "Use BEFORE grep/Read whenever the question is about a symbol's relationships". With items 1 and
2 open, that sends agents to a tool that cannot see property, field, record or enum usage. Until those land:

- Say which usage is not recorded: "Calls between methods, type references from classes and interfaces, inheritance.
  Not recorded: reads and writes of properties, fields and events; records, structs and enums."
- Tell the agent what an empty `usedBy` means: "An empty or missing `usedBy` is not evidence of no users unless the
  target is a method or a class. Cross-check with `rg -w <Name>`."
- Tell it that `relationships none` on an ambiguous match means "not loaded", and to re-query with the identifier.
- Point to `--json` when a list is truncated (or `--limit` once it exists).
- Narrow the trigger to where it beats text search: common or overloaded names, interface and override dispatch,
  callers of callers (`--depth 2`), file dependency and test reach. For a distinctive name in a small repo, one `rg`
  call is cheaper and complete.

Once items 1 to 4 are fixed, the "use before grep" wording is fair again for C# and can go back in.

## Tests to add

- A property read, a field write, an event subscription and an enum member use each produce an incoming edge.
- A record, a record struct, a struct and an enum are each found by `query --exact`.
- A call inside a constructor, a field initialiser and a top-level statement is attributed to that
  member (or the file's `<Main>`).
- An ambiguous query never prints `relationships none`.
