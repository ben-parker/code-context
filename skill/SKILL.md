---
name: code-context
description: Use for C#, TypeScript/JavaScript and Vue questions about how symbols relate - who calls, renders, implements, or depends on something, how one symbol reaches another, the impact of changing or deleting it, which symbols the most code depends on, which tests cover it - or exact syntax structure. One query on a live index replaces many greps, so ask it BEFORE grep/Read when those relationships are unknown; skip it for an edit confined to a file you already know. Not for literal strings, comments, config values, filenames, docs, or non-indexed files (shell, SQL, Python, YAML/JSON, markdown, csproj) - use grep/rg for those.
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
- **Use a whole-graph query** (below) when the question spans many hops: how A reaches B,
  everything a change could break, the load-bearing symbols of an area. One call replaces
  a chain of `query` calls stitched together by hand. To review or prepare a change, give
  its diff to `query impact`.
- **Use the [native syntax tree](references/native-syntax.md)** when exact structure
  matters: overloads, modifiers, spans.

## Query

```bash
codecontext query ContextService            # relationships, depth 1
codecontext query ContextService --tests    # add static test evidence (off by default)
codecontext query multi Foo Bar             # several identifiers, one round trip, in order
```

`query` uses the repo's running instance, or starts one and waits up to 30 seconds for
indexing (exit code 3 on timeout; see [operations](references/operations.md)). It prints compact
agent-oriented text; progress goes to stderr. Options: `--depth N`
(`0` = cheap identity lookup), `--relation CALLS,REFERENCES`, `--exact`,
`--limit N` (entries per relationship list, 1-100, default 10), `--external` (include
library symbols), `--path DIR`, `--human`, `--json`, `--snapshot` (with no instance
running, answer in this process from the saved index when nothing it records changed,
leaving nothing running; see [operations](references/operations.md)).

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
- `cache snapshot` (only with `--snapshot`; `--json`: `"cache":{"state":"snapshot",...}`)
  means no instance was running and the answer was read from the saved index after a
  check by file times of what it was made from (see [operations](references/operations.md));
  no worker re-analysed anything. It can miss a change that check cannot see. Before
  editing on it or concluding absence, confirm with `rg` or query without `--snapshot`.
- `relationships not loaded` means nothing was fetched: the name was ambiguous
  (re-query with a target's identifier) or the depth was 0. It says nothing about
  whether the symbol has users.
- An unmatched identifier exits 2, labelled or not; empty or truncated results are
  otherwise successful.

## Whole-graph queries

```bash
codecontext query path StartCommandHandler ContextService   # how does one reach the other
codecontext query affected ContextService.FindPathAsync     # what could break if it changes
codecontext query hubs --within src/CodeContext.Core        # the most depended-on symbols there
git diff $(git merge-base main HEAD) | codecontext query impact   # what this branch could break
```

- Identifiers match **exactly**: a canonical identifier, an exact or qualified name
  (`Type.Member`), or a file path; there is no substring fallback. An unknown name prints
  `missing`, a name with several symbols `ambiguous` with their identifiers; both exit 2.
  Pass a returned identifier back unchanged. A type stands for itself and its members, a
  file for its symbols. One declaration indexed more than once (the same file, span, name,
  type and signature under several identifiers, as a worker reports a file it sees through
  several projects or workspaces) is not ambiguous: all its copies are searched together,
  whichever identifier you pass, and a `sameDeclaration` line lists them.
- **`path`** prints one shortest chain, a line per hop: `start`; the relationship and the
  line it is recorded at (`CALLS line 42`); `dispatch IMPLEMENTS_MEMBER` or `OVERRIDES_MEMBER`
  where a call through an interface or base member, or the starting member itself, reaches
  an implementation (never after a statically bound `base.M()` or `super.m()` call);
  `member` and `memberOf` at a type endpoint. A use of a type never leads into its members
  mid-path. `--depth N` is the most
  hops (1-20, default 10). Test files are not passed through without `--tests`. `path none`
  comes with an `explanation`: `excludedBy relation` or `tests` means your own filter hid
  a path that exists; a depth note means the search stopped at its limit with more to
  explore (raise `--depth`); "swap from and to" means the path runs the other way.
  Otherwise nothing recorded connects them, which proves nothing about runtime: `rg`.
- **`affected`** lists what depends on the symbol or file, nearest first: the distance,
  the relation, `via` the symbol it depends on, and `through` the interface or base member
  a call names when it reaches the seed by dispatch. Impact moves along recorded uses; to
  implementations and overrides of a changed declaration (not under `--relation`); and to
  callers of a member the seed implements or overrides, unless statically bound. It never
  moves to siblings, containers, or same-file or same-namespace symbols. `--depth N`
  (1-20, default 5; `more beyond` says the limit cut it off), `--limit N` (1-100, default
  20; `truncated` with the total), `--tests` lists the test-file dependents counted in
  `testsHidden` and lets impact move on from them (without it, a test double never makes
  its callers' callers affected).
- **`impact`** reads a unified diff from stdin or `--diff FILE` and answers `affected` for
  everything it changes, in one walk: `changed` lists the declarations its changed lines
  fall in (`member`, or `container` for a type's or module's own lines: its header, its
  closing line, between members; impact is then walked from the type alone), each with its
  lines and the dependents attributed to it; `affected` lists them as `affected` does, each
  `from` the change that reached it first; `tests` lists the test files in reach. The
  diff's new side must be the working tree: diff against a base (`git diff $(git
  merge-base main HEAD)`), or from a base to `HEAD` with nothing uncommitted; `git diff
  --cached` matches only files with no unstaged change, and untracked files appear only
  after `git add -N`. A file is mapped only when its content
  is the diff's new side (its blob id, so keep git's `index` lines) and the instance read
  it after it last changed; `impact` waits for a finished scan. The first line says
  `complete` or `incomplete`, and `incomplete` exits 2: every part that maps to nothing is
  listed under `unmapped` with its reason, among them `outsideDeclarations` (imports,
  usings, a namespace line; in an added file only a `global using`, an assembly attribute
  or a statement, since its own usings bind only its declarations, all changed), `removedBetweenDeclarations` (removed or replaced lines outside
  a member: a deleted, renamed or re-signed declaration exists only on the old side, so what
  used it is not in the index; `rg` its old name), `deletedFile`, `renamed`, `binary`,
  `notIndexed` (a file no worker parses: a project or config file can still matter),
  `contentMismatch` (the working tree is not the diff's new side), `notYetIndexed` (retry),
  `outsideRoot` and `invalidPath`. Blank lines and comment lines no member holds change
  nothing (`ignored`), except a `/*` or `*/` that comments unchanged lines out or back in
  (`blockCommentBoundary`). `--depth`, `--limit` (per list), `--tests` and `--relation` mean what
  they mean for `affected`. The tool never runs git: you run it. In a repository you do not
  trust, `git diff` itself runs what its `.git/config` names (an external diff, a textconv
  or clean filter, an fsmonitor hook), and its config can reshape the output (colour,
  prefixes, relative paths). There, commit or check out the new side with a clean work
  tree and diff two commits: `git -c core.fsmonitor=false diff --no-ext-diff --no-textconv
  --no-color --no-relative --src-prefix=a/ --dst-prefix=b/ --full-index <base> HEAD`. A diff
  against the working tree (`git diff <base>`) runs the repository's clean filters whatever
  the flags.
- **`hubs`** ranks symbols by distinct direct dependents, then by their files. A type's
  count is uses of the type itself, not of its members (`--type Method` ranks members).
  `--within DIR` (repository-relative), `--type`, `--limit`, `--relation` (kinds counted),
  `--tests` (rank test files and count their uses), `--external` (rank library symbols).
- `--relation` takes CALLS, MOCK_CALLS, REFERENCES, IMPLEMENTS, INHERITS, EXTENDS, IMPORTS,
  USES; dispatch is followed whatever it says. An option a subcommand does not read
  (`--exact`, `--within` on `path`, ...) is refused.
- A test file, here and for `--tests` evidence, is one named as test runners expect, judged
  on its path under the root: a directory or file-name part (between `.`, `-`, `_`) that
  is `test`, `tests`, `spec` or `specs` (`tests/`, `__tests__/`, `Widget.Tests/`,
  `widget.test.ts`, `widget.spec.ts`, `widget_test.go`, `test_widget.py`), or that ends in
  `Test` or `Tests` (`WidgetTests.cs`, `Widget.UnitTests/`), or `conftest.py`. A name that
  only contains the letters (`Latest.cs`, `WorkerLaunchSpec.cs`, `inspector.ts`) is not
  one; test code named otherwise (`TestHelpers.cs` outside a test directory) is not
  recognized, so check with `rg` when the split matters.
- To query a symbol named `path`, `affected`, `hubs`, `impact` or `multi`, put it after `--`:
  `codecontext query --depth 2 -- path`.
- A graph query walks the graph on every call: milliseconds on a repository of 100,000
  edges, seconds on one of millions. The CLI waits up to 60 seconds for one (5 for
  `query`), then says it stopped and how to narrow the query; the instance stops the walk
  when the caller goes away.
- The answers are static evidence with the blind spots listed below; an empty `affected`
  or `path none` is not proof of no dependency.

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
  component tag (`<DownloadQueue>`, `<download-queue>`, `<component :is="X">`) and a call
  in an expression (`{{ format(x) }}`, `@click="save()"`) are each a `CALLS` from the
  rendering file's module to whatever the script binds that name to. An event handler
  given by name (`@click="save"`) is a `REFERENCES`, as `onClick={save}` is in JSX. A component tag no script imports binds to what the project
  declares for every template: the `components.d.ts` that Nuxt (under `.nuxt/`) or
  `unplugin-vue-components` generates, or `app.component('Name', X)`. In an Options API
  component, a method under `methods` or `computed` is bound from the template (a call
  as `CALLS`, a handler by name or a read such as `{{ total }}` as `REFERENCES`) and from
  `this.name()` in its other members.
- **Vue templates are blind to these; run `rg -w <Name>`:** an auto-imported component
  before its declaration file exists (run `nuxi prepare`; status then carries a
  diagnostic) or whose file is generated somewhere other than the project's directory;
  Options API `data` and `props`, and a member not written as a method (`save: function
  () {}`, a `get`/`set` computed); a handler
  reached through an object (`@click="store.save"`); a `pug` template; a dotted tag
  (`<Icons.Close>`).
- **Import aliases** (`@/`, `~/`) resolve from the `tsconfig.json` or `jsconfig.json`
  nearest the importing file, then the root one, the configs it extends or references,
  and the `.nuxt/tsconfig.json` Nuxt generates. So `@/` in each package of a monorepo
  means that package. An alias declared only under `resolve.alias` in `vite.config` or
  `webpack.config` resolves when its target is written out (a string,
  `path.resolve(__dirname, 'src')`, `fileURLToPath(new URL('./src', import.meta.url))`).
  The config is never run, so an alias whose target it computes does not resolve: status
  names it in a diagnostic, and an import through it is missing from `usedBy` and
  `fileDependents`.

## Going deeper

- Stale results, readiness failures, instance lifecycle, index refresh, REST
  endpoints: read [Operations](references/operations.md).
- Exact tokens, modifiers, nesting, overloads, accessors, spans: read
  [Native syntax trees](references/native-syntax.md).
