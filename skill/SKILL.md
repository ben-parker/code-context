---
name: code-context
description: Use BEFORE grep/Read whenever the question is about a symbol's relationships in C# or TypeScript/JavaScript code — who calls, implements, or depends on it, the blast radius of changing or deleting it, which tests cover it — or exact syntax structure. Grep finds text matches; this answers dependency questions from a live index in one query. ONLY worker-supported source files are indexed — for shell scripts, SQL, Python, YAML/config, docs, or any literal-string search, use grep instead.
---

# CodeContext

**Scope:** the bundled index contains C# (`.cs`) and TypeScript/JavaScript (`.ts`,
`.tsx`, `.mts`, `.cts`, `.js`, `.jsx`, `.mjs`, `.cjs`). Symbols defined anywhere else — shell scripts, SQL, Python,
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
`--path DIR`, `--human`, `--json`.

Reading results:

- A returned `target.identifier` is canonical — pass it back unchanged for an
  unambiguous follow-up. Without `--exact`, simple names match exact-first with
  substring fallback; check `matchMode` and ambiguity hints.
- Every emitted section shows returned/total counts and marks truncation — check
  them before treating an omission as absence. Zero-count sections are omitted.
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
- `relatedItems` is proximity, not dependency evidence. Test evidence is static
  graph evidence, not runtime coverage; `directlyTested` means a static call to
  the symbol or, for a type, one of its members.

## Going deeper

- Stale results, readiness failures, instance lifecycle, index refresh, REST
  endpoints: read [Operations](references/operations.md).
- Exact tokens, modifiers, nesting, overloads, accessors, spans: read
  [Native syntax trees](references/native-syntax.md).
