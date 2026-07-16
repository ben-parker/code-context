---
name: code-context
description: Query a local CodeContext graph or parser-native syntax tree for callers, implementations, dependencies, blast radius, tests, ambiguous symbols, or exact syntax.
---

# CodeContext

Use the normalized graph for semantic relationships. Prefer `rg` for literals,
configuration, comments, and checking surprising results. Use native trees only for
exact parser structure after locating the symbol or file.

## Connect

Run `codecontext list --json` and choose the instance whose `rootPath` contains the
working directory. If absent, run `codecontext start --detach --path <repo-root>`.
Before trusting graph results, poll `http://localhost:<port>/api/status` until
`indexing.status` is `ready`; confirm the instance ID, relevant parser is ready, and
`api.contractVersion == 1`.

## Query

```bash
curl -s "http://localhost:<port>/api/context/complete?identifier=OrderService"
```

`identifier` accepts a returned canonical `target.identifier`, a repository-relative
or absolute file path, or a simple name. Returned identifiers round-trip unchanged
through the same parameter and resolve before search. Simple names use exact-first,
substring-fallback matching when `exact` is omitted; inspect `matchMode` and set
`exact=false` to force broader discovery despite an exact hit. Refine with `type`,
`containingType`, `namespace`, `signature`, or `sourceFile`; use `maxMatches` and
`expandAmbiguous` for bounded ambiguity. `relation=CALLS,MOCK_CALLS` (compact view
only, case-insensitive; valid kinds CALLS, MOCK_CALLS, REFERENCES, IMPLEMENTS,
INHERITS, EXTENDS, IMPORTS, USES) filters `uses`/`usedBy` to those edge kinds — an
unknown kind is rejected with the valid list, and the count/truncation fields then
describe the filtered set.

Compact output defaults to depth one with tests, related items, metrics, and content
off. Inspect each category's total, returned, and truncation fields before treating
absence as meaningful. `maxRelationships`, `maxCallSites`,
`maxTestFiles`, and `maxTestMethods` are independent. Zero requests count-only output
for the corresponding call-site or test list.

- `uses` is scoped to the selected symbol; implementations never borrow sibling
  implementations' outgoing behavior.
- Method `usedBy` unifies statically known interface, implementation, and override
  callers. Callers are deduplicated, occurrences/sites aggregated, and `bindings`
  identifies the statically called family role. This is potential dispatch evidence,
  not runtime certainty.
- `transitiveUses` and `transitiveUsedBy` contain nodes beyond distance one; unified
  method callers seed inbound traversal.
- `fileDependencies` and `fileDependents` aggregate resolved semantic edges crossing
  the selected source file for every symbol in that file. They exclude unresolved,
  same-file, and proximity-only relationships.
- `relatedItems` is optional same-file/namespace proximity, not dependency evidence.
  Test evidence is static graph evidence, not coverage. `directlyTested` is true when a
  test method statically calls the symbol or, for types, one of their members
  (`memberCall` evidence).

Use `depth=0` for cheap identity lookup and raise caps only when counts require it.
`view=full` exposes parser/debug details, method-family members, and bound targets.
`/api/context/multi` reduces HTTP round trips, not response tokens, and defaults
`maxRelationships` to three.

For tokens, modifiers, nesting, or overload/accessor form, read
[Native syntax trees](references/native-syntax.md).

## Keep results trustworthy

After a branch switch or large rewrite, `POST /api/index/refresh`, then wait for ready
status with `operationId` at least the returned value. For unexpected empty results,
check readiness, parser state, spelling/type filters, and ignore rules before concluding
that no relationship exists.
