---
name: code-context
description: Use for C#, TypeScript/JavaScript and Vue questions about how symbols relate - who calls, renders, implements, or depends on something, the impact of changing or deleting it, which tests cover it - or exact syntax structure. One query on a live index replaces many greps, so ask it BEFORE grep/Read when those relationships are unknown; skip it for an edit confined to a file you already know. Not for literal strings, comments, config values, filenames, docs, or non-indexed files (shell, SQL, Python, YAML/JSON, markdown, csproj): use grep/rg.
---

# CodeContext

**Scope:** the bundled index contains C# (`.cs`) and TypeScript/JavaScript (`.ts`,
`.tsx`, `.mts`, `.cts`, `.js`, `.jsx`, `.mjs`, `.cjs`) plus Vue SFCs (`.vue`): their
script blocks and what their templates use from them. Symbols defined anywhere else —
shell scripts, SQL, Python, YAML/JSON config, markdown, csproj/MSBuild — are absent by
design, so a query for them proves nothing; use `rg`. Even in supported languages, `rg`
remains the primary tool for literal strings, comments, configuration values, filenames,
and docs.

## Choose the tool

- **Query the index** when the answer depends on how symbols relate and you do not know
  yet: the callers of a helper, the views that render a component, what a signature
  change or a deletion reaches, which tests touch a symbol.
- **Read the file** when the edit is local and its scope is clear. A query adds nothing
  to "put active items first in this handler".
- **Use `rg`** for filenames, route strings, comments, config values and other literal
  text, and to cross-check an empty result before a decision rests on it.
- **Use the [native syntax tree](references/native-syntax.md)** when exact structure
  matters: overloads, modifiers, spans.

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
- **TypeScript/JavaScript** records calls, construction, imports, inheritance, and a JSX
  render of a component (`<Button />`, as `CALLS`). A function handed over rather than
  called is a `REFERENCES`: as an argument (`items.map(format)`), a JSX attribute
  (`onClick={save}`), an object property (`{ run: save }`, `{ save }`) or an array
  element. It **does not record** property reads and writes, type references, a function
  assigned to a variable or returned, or a class passed as a value.
- **Vue** files are indexed through their `<script>` blocks, and a component is its file:
  `query DownloadQueue` matches `components/DownloadQueue.vue`. From the template, a
  component tag (`<DownloadQueue>`, `<download-queue>`, `<component :is="X">`), an event
  handler given by name (`@click="save"`) and a call in an expression
  (`{{ format(x) }}`) are each a `CALLS` from the rendering file's module to whatever the
  script binds that name to.
- **Vue templates are blind to these; run `rg -w <Name>`:** a component the script never
  imports (globally registered, or auto-imported by Nuxt or `unplugin-vue-components`);
  Options API members (`methods`, `computed`, `data`) used from the template; a handler
  reached through an object (`@click="store.save"`); a `pug` template; a dotted tag
  (`<Icons.Close>`).
- **Import aliases** (`@/`, `~/`) resolve from the root `tsconfig.json` or
  `jsconfig.json`, the configs it extends or references, and the `.nuxt/tsconfig.json`
  Nuxt generates. An alias declared only in a bundler config (`vite.config`, webpack)
  does not resolve, and an import through it is missing from `usedBy` and
  `fileDependents`.

## Going deeper

- Stale results, readiness failures, instance lifecycle, index refresh, REST
  endpoints: read [Operations](references/operations.md).
- Exact tokens, modifiers, nesting, overloads, accessors, spans: read
  [Native syntax trees](references/native-syntax.md).
