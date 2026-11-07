---
name: code-context
description: Use BEFORE grep/Read for C# and TypeScript/JavaScript questions about a symbol's relationships - who calls, implements, or depends on it, the impact of changing or deleting it, which tests cover it - or exact syntax structure. One query on a live index replaces many greps. Not for literal strings, comments, config values, filenames, docs, or non-indexed files (shell, SQL, Python, YAML/JSON, markdown, csproj): use grep/rg.
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
codecontext query multi Foo Bar             # several identifiers, one round trip, in order
```

`query` auto-discovers (or starts) the repo's instance, waits for indexing, and
prints compact agent-oriented text; progress goes to stderr. Options: `--depth N`
(`0` = cheap identity lookup), `--relation CALLS,REFERENCES`, `--exact`,
`--limit N` (entries per relationship list, 1-100, default 10), `--external` (include
library symbols), `--path DIR`, `--human`, `--json`.

Reading results:

- A returned `target.identifier` is canonical — pass it back unchanged for an
  unambiguous follow-up. Without `--exact`, simple names match exact-first with
  substring fallback; check `matchMode` and ambiguity hints. `Type.Member` and
  longer dotted suffixes of a qualified name also match.
- Sections show returned/total counts and mark truncation: check them before treating
  an omission as absence, and raise `--limit`. `usedBy` is always printed; other
  zero-count sections are omitted.
- A `cache verifying` or `cache unverified` line (first, or after each `query` line in
  `query multi`; REST/MCP: a `cache` object with `state` and `snapshotAt`) means the
  answer comes from the index saved by an earlier run, not yet checked against the
  files. A file changed while the instance was stopped can make it wrong until
  verification finishes, and a `missing` or empty `usedBy` is not conclusive. No label
  means this caveat does not apply.
  - `verifying`: the re-check is running. Hits are fine for orientation; before editing
    or concluding absence, wait until `codecontext status --path .` shows
    `indexing.status` `ready`, then query again.
  - `unverified`: the re-check failed (`indexing.status` `error`) and the answers stay
    saved ones. Do not wait: confirm with `rg -w <Name>` or by reading the file, or run
    `POST /api/index/refresh` (or restart) and query again once `ready`.
- `relationships not loaded` means nothing was fetched: the name was ambiguous
  (re-query with a target's identifier) or the depth was 0. It says nothing about
  whether the symbol has users.
- An unmatched identifier exits 2, labelled or not; empty or truncated results are
  otherwise successful.

## Interpret relationships

- `uses` is the selected symbol's own outgoing behavior; sibling implementations do
  not lend it theirs.
- Method `usedBy` unifies statically known interface, implementation, and override
  callers; `bindings` identifies the called family role. This is potential
  dispatch, not runtime certainty.
- `fileDependencies` / `fileDependents` aggregate resolved semantic edges crossing
  the selected file, excluding unresolved, same-file, and proximity-only links.
  They describe the whole file named after `of`, not the queried member: a file
  listed there is not evidence that it uses that member.
- `relatedItems` is proximity, not dependency evidence. Test evidence is static, not
  runtime coverage; `directlyTested` means a static call to the symbol or, for a
  type, one of its members.

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
  analysis compiles each project as a build would, reading its project file and restore
  data: its own references, constants, target framework and usings. Files in no project
  bind against the .NET libraries and the projects around them.
- **Library symbols** are queryable by name or identifier, so `query Node3D` lists the
  project's uses of a library type, marked `external <assembly>`. Only symbols the code
  uses exist; nothing inside a library is indexed. `uses` leaves them out and prints
  `usesExternal` with a count, and a name that matches project code leaves out library
  symbols of that name and prints `externalMatches`. `--external` lists them.
- **Same-named symbols in two projects** are separate, and their identifiers name the
  project, as in `csharp:ToolA#Program.Main()`. A bare identifier then returns all of them
  as ambiguous.
- **A `coverage` line** on a `missing` or empty `usedBy` result means some project was
  analyzed without all it declares, usually because it was never restored, so that answer
  may be short. `codecontext status --path .` lists each project's state, its missing
  references, and how many names failed to bind. Run `dotnet restore`, then `rg -w <Name>`
  as the cross-check.
- **C# is blind to these, so an empty `usedBy` proves nothing here; run `rg -w <Name>`:**
  - **Code the active build leaves out.** Each project is analyzed for `Debug`, or the
    first configuration it declares, and its first target framework, with that project's
    constants. The `#else` of `#if DEBUG` and code for another target are unseen, as they
    are greyed out in an IDE.
  - **Usage from non-C# files**: `.razor` markup, XAML, Godot `.tscn` signal connections,
    Unity serialized references in scenes and prefabs.
  - **Generated code.** Analyzers and source generators are never run, so members they
    would add do not exist here and whatever is reached through them is lost. Status
    counts names that failed to bind and lists the commonest.
  - **Files in no project.** They bind against the workspace's projects together, so a
    name two projects declare is ambiguous there and is not guessed.
- **TypeScript/JavaScript** records calls, construction, imports and inheritance. It
  **does not record** property reads and writes or type references.

## Going deeper

- Stale results, readiness failures, instance lifecycle, index refresh, REST
  endpoints: read [Operations](references/operations.md).
- Exact tokens, modifiers, nesting, overloads, accessors, spans: read
  [Native syntax trees](references/native-syntax.md).
