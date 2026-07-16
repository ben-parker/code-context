---
name: code-context
description: Query a local CodeContext graph or a parser-native syntax tree. Use for callers, implementations, dependencies, blast radius, tests, cross-file relationships, ambiguous symbols, or exact syntax/token/nesting questions. Prefer text search for literals, configuration, comments, and distinctive one-off definitions.
---

# CodeContext

Use the normalized graph for semantic relationships across the repository. Use native
syntax trees only after locating a file or symbol when exact parser structure matters.

## Connect

Find the instance whose `rootPath` contains the working directory:

```bash
codecontext list --json
```

If none exists, start one at the repository root:

```bash
codecontext start --detach --path <repo-root>
```

Capture its `port` and `instanceId`. Poll `http://localhost:<port>/api/status` until
`indexing.status` is `ready`; confirm `system.instanceId` matches. While status is
`scanning`, empty results are inconclusive. If a parser is unavailable, do not infer
that its files have no references.

Check `api.contractVersion` before relying on response semantics. This skill requires
contract version 2 or newer. A missing value means legacy contract version 1; if the
value is absent or older than 2, recommend upgrading CodeContext and do not assume the
relationship, truncation, signature, or match-mode guarantees described below.

## Query the graph

```bash
curl -s "http://localhost:<port>/api/context/complete?identifier=OrderService"
```

The defaults are agent-safe: compact output, exact-first matching with substring
fallback, depth 1, tests/related items/metrics/content off, five ambiguous candidates,
and ten entries per relationship list.

Read every category's total count, returned count, and category-specific truncation
flag. The shared `truncated` flag is only an OR summary across categories; it does not
identify which list was capped. Absence from a capped list is not proof of absence.
Relationship entries include relation kinds, occurrence counts, and up to three source
lines. Direct relationships stay in `uses`/`usedBy`; nodes reached
beyond one hop appear in bounded `transitiveUses`/`transitiveUsedBy` sections with a
`distance` and shortest `relationPath`.

`dependencies` and `dependedBy` are resolved semantic cross-file graph evidence and
are suitable for blast-radius analysis when indexing is ready and the relevant language
parser is available and ready. They exclude namespace proximity and unresolved links.
Do not treat an absent relationship as proof of independence while scanning is
incomplete, a parser is unavailable or failed, or the language is unsupported.

`relatedItems` is heuristic same-file/same-namespace proximity, not dependency
evidence. Request it only with `includeRelated=true` when that proximity is useful.

Refine progressively:

- `type=Class|Method|Interface|Property` narrows ambiguity.
- `exact=true` disables substring fallback.
- Inspect `matchMode`. When an omitted `exact` produces `matchMode=exact` and
  `substringSearchSkipped=true`, retry with `exact=false` if broader discovery was the
  intent.
- `depth=0` returns the cheapest symbol result; increase depth only when transitive
  relationships matter.
- Ambiguous summaries include `qualifiedIdentifier`; pass it back directly, or filter
  with `containingType`, `namespace`, `signature`, or `sourceFile`.
- `expandAmbiguous=true` expands bounded matches when several summaries are relevant.
- `includeTests=true` distinguishes direct calls, indirect static references, test
  implementers/fakes, and naming heuristics. These are graph facts, not coverage.
- `maxMatches` and `maxRelationships` raise explicit caps.
- `maxCallSites` independently caps locations on an aggregated edge; use `0` for
  count-only orientation and inspect `callSiteCount`/`callSitesTruncated`.
- `includeMetrics=true` and `includeContent=true` are opt-in.
- `view=full` is for compatibility or response debugging, not routine agent use.

Ambiguous compact queries intentionally return summaries without relationships. Use a
returned qualified identity or another facet filter to select one match in a single
request; use bounded expansion only when several candidates matter. Repository-relative
and absolute source paths are valid identifiers.

Batch related symbols with one shared type filter:

```bash
curl -s -X POST "http://localhost:<port>/api/context/multi" \
  -H "Content-Type: application/json" \
  -d '{"identifiers":["OrderService","PaymentService"],"type":"Class","depth":1}'
```

Multi-context defaults to three entries per relationship list. Prefer several compact
queries over one widened/full response when only some results need deeper inspection.

## Escalate only for exact syntax

For tokens, modifiers, nesting, accessor/overload form, or other parser-specific
structure, read [Native syntax trees](references/native-syntax.md). Locate the symbol
with the normalized graph first.

## Keep the index trustworthy

The watcher normally handles edits. After a branch switch or large external rewrite,
`POST /api/index/refresh`, then wait until status is `ready` and its `operationId` is at
least the returned operation ID. Leave instances running; idle shutdown handles cleanup.

For an unexpected empty result, check readiness, parser status, spelling/type filters,
and ignored directories before concluding that no relationship exists.

Continue to prefer `rg` for literals, configuration, comments, release artifacts, and
verification of surprising graph results.

Project-local root and nested `.gitignore` files govern scans, refreshes, and watcher
events. `.git/`, `.codecontext/`, configured runtime/build exclusions, inaccessible
paths, system entries, and reparse points are mandatory safety exclusions and cannot be
negated by project rules. Status reports the active ignore-source and ignored-path
counts without returning rule bodies or paths.
