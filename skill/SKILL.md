---
name: code-context
description: Use BEFORE grep/Read whenever the question is about a symbol's relationships — who calls, implements, or depends on it, the blast radius of changing or deleting it, which tests cover it — or exact syntax structure. Grep finds text matches; this answers dependency questions from a live index in one query.
---

# CodeContext

**Pre-read:** Run `codecontext query <identifier>`. Add `--tests` when
you need test evidence; tests are omitted by default.

## Fast path

```bash
codecontext query ContextService --tests
codecontext query multi ContextService StartCommandHandler
```

`query` selects the closest registered ancestor of `--path` (the current directory by
default), starts a detached instance when needed, waits for indexing, and calls the
compact context API. The default is compact agent-oriented text; use `--human` for
expanded human output or `--json` for the exact API response. Progress goes to stderr.
Options include `--depth N`, `--relation
CALLS,REFERENCES`, `--exact`, and `--path DIR`.

A returned `target.identifier` is canonical: pass it back unchanged for an
unambiguous follow-up. Simple names use exact-first, substring-fallback matching when
`--exact` is absent. Inspect `matchMode`, ambiguity hints, every total/returned count,
and truncation flag before treating an omission as absence. `query multi` preserves
identifier order and duplicates; it reduces HTTP round trips, not response size.
Zero-only relationship sections are omitted from compact text; every emitted section
shows returned/total counts and marks truncation.

Compact results default to depth one with tests, related items, metrics, and content
off. Add `--tests` when test evidence matters. Empty relationships and truncated
results are still successful queries; an unmatched identifier exits 2.

## Interpret relationships

- `uses` belongs to the selected symbol; sibling implementations do not lend it their
  outgoing behavior.
- Method `usedBy` unifies statically known interface, implementation, and override
  callers. `bindings` identifies the called family role. This is potential dispatch,
  not runtime certainty.
- `transitiveUses` and `transitiveUsedBy` are nodes beyond distance one.
- `fileDependencies` and `fileDependents` aggregate resolved semantic edges crossing
  the selected file. They exclude unresolved, same-file, and proximity-only links.
- `relatedItems` is proximity, not dependency evidence. Test evidence is static graph
  evidence, not runtime coverage; `directlyTested` means a static call to the symbol
  or, for a type, one of its members.

Use `--depth 0` for cheap identity lookup. Use `rg` as the cross-check and as the
primary tool for literal strings, configuration, comments, filenames, and docs.

## Troubleshooting and advanced operations

If results look stale or startup/readiness fails, run `codecontext status --path .`.
Use `codecontext list --json` to inspect registrations, and only then use
`codecontext start --detach --path .` for manual lifecycle troubleshooting. A human can
also run `codecontext init --path .` (add `--wait`) to pre-warm the index before agent
work so the first query skips the cold-start scan. Confirm
contract version 1, the intended root and instance, indexing readiness, and the
relevant parser session before trusting an unexpected empty result.

For filters and operations not exposed by `query`, get the port from `list --json`
and use REST, for example:

```bash
curl "http://localhost:<port>/api/context/complete?identifier=X&type=Method&containingType=C"
curl -X POST "http://localhost:<port>/api/index/refresh"
```

After refresh, wait for ready status and an `operationId` at least as new as the
refresh response. For exact tokens, modifiers, nesting, overloads, accessors, and
spans, read [Native syntax trees](references/native-syntax.md).
