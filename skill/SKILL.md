---
name: code-context
description: Use BEFORE grep/Read whenever the question is about a symbol's relationships in C# or TypeScript/JavaScript code — who calls, implements, or depends on it, the blast radius of changing or deleting it, which tests cover it — or exact syntax structure. Grep finds text matches; this answers dependency questions from a live index in one query. ONLY worker-supported source files are indexed — for shell scripts, SQL, Python, YAML/config, docs, or any literal-string search, use grep instead.
---

# CodeContext

**Scope:** the bundled index contains C# (`.cs`) and TypeScript/JavaScript (`.ts`,
`.tsx`, `.mts`, `.cts`, `.js`, `.jsx`, `.mjs`, `.cjs`) plus JavaScript/TypeScript
embedded in Vue SFCs (`.vue`). Symbols defined anywhere else — shell scripts, SQL, Python,
YAML/JSON config, markdown, csproj/MSBuild — are absent by design, so a query for
them proves nothing; use `rg`. Even in supported languages, `rg` remains the primary
tool for literal strings, comments, configuration values, filenames, and docs.

## Query

```bash
codecontext query ContextService            # relationships, depth 1
codecontext query ContextService --tests    # add static test evidence (off by default)
codecontext query multi Foo Bar             # several identifiers, one round trip
```

`query` auto-discovers (or starts) the repo's instance, waits for indexing, and
prints compact agent-oriented text; progress goes to stderr. Options: `--depth N`
(`0` = cheap identity lookup), `--relation CALLS,REFERENCES`, `--exact`,
`--limit N` (entries per relationship list, 1-100, default 10), `--path DIR`,
`--human`, `--json`.

Reading results:

- A returned `target.identifier` is canonical — pass it back unchanged for an
  unambiguous follow-up. Without `--exact`, simple names match exact-first with
  substring fallback; check `matchMode` and ambiguity hints. `Type.Member` and
  longer dotted suffixes of a qualified name also match.
- Every emitted section shows returned/total counts and marks truncation — check
  them before treating an omission as absence, and raise `--limit` for the rest of
  a truncated list. `usedBy` is always printed; other zero-count sections are
  omitted.
- `relationships not loaded` means nothing was fetched: the name was ambiguous
  (re-query with a target's identifier) or the depth was 0. It says nothing about
  whether the symbol has users.
- Empty relationships and truncated results are still successful queries; an
  unmatched identifier exits 2. `query multi` reduces round trips, not response
  size, and preserves identifier order.

## Interpret relationships

- `uses` is the selected symbol's own outgoing behavior; sibling implementations do
  not lend it theirs.
- Method `usedBy` unifies statically known interface, implementation, and override
  callers; `bindings` identifies the called family role. This is potential
  dispatch, not runtime certainty.
- `transitiveUses` / `transitiveUsedBy` are nodes beyond distance one.
- `fileDependencies` / `fileDependents` aggregate resolved semantic edges crossing
  the selected file, excluding unresolved, same-file, and proximity-only links.
  They describe the whole file named after `of`, not the queried member: a file
  listed there is not evidence that it uses that member.
- `relatedItems` is proximity, not dependency evidence. Test evidence is static
  graph evidence, not runtime coverage; `directlyTested` means a static call to
  the symbol or, for a type, one of its members.

## What is recorded

An empty `usedBy` is evidence of no users only for usage the index records. For
anything else, cross-check with `rg -w <Name>`.

- **C#** records calls, construction, type references, inheritance, method groups,
  attribute usage, and reads and writes of properties, fields, events and enum
  members (as `REFERENCES`). Code in constructors, accessors, initializers, operators
  and top-level statements is attributed to that member, or to the file's `<Main>`.
- **C# does not record** use of user-defined operators and implicit conversions,
  deconstruction, implicit base constructor calls, or the calls `using`, `foreach` and
  collection initializers imply. A read of a virtual or interface property is recorded
  on the member named at the read, not on its overrides or implementations. The
  analysis sees repository source, the .NET libraries and, per project, the SDK's default
  implicit usings, not NuGet or engine assemblies or custom `<Using>` items. Usage reached
  through a value of such a type is missing: `GetNode<Item>("x").Weigh()` records the
  reference to `Item` but not the call to `Weigh`, and the same goes for a local holding
  that value or a lambda parameter of that type.
- **C# is blind to these, so an empty `usedBy` proves nothing here; run `rg -w <Name>`:**
  - **`#if` branches.** Files are parsed with no preprocessor symbols beyond the file's
    own `#define`, so code under `#if DEBUG`, `#if TOOLS` or `#if UNITY_EDITOR` is unseen
    and the `#else` branch is recorded instead.
  - **Usage from non-C# files**: `.razor` markup, XAML, Godot `.tscn` signal connections,
    Unity serialized references in scenes and prefabs.
  - **Same-named types in different projects.** All projects share one analysis, so two
    types with the same namespace and name become one node: `usedBy` is the union of
    both, and a caller can be attributed to the wrong file.
  - **Name clashes the build never sees.** When a repository type shares its name with a
    .NET type its project cannot see, because the project targets an older framework or
    the other type comes from another project's `global using`, uses of the name are
    recorded but members reached through it are not. An extension method clashing the
    same way is not recorded at all.
- **TypeScript/JavaScript** records calls, construction, imports and inheritance. It
  **does not record** property reads and writes or type references.

## Going deeper

- Stale results, readiness failures, instance lifecycle, index refresh, REST
  endpoints: read [Operations](references/operations.md).
- Exact tokens, modifiers, nesting, overloads, accessors, spans: read
  [Native syntax trees](references/native-syntax.md).
