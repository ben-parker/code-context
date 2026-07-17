# Post-phase-5 correctness and index-hygiene plan

Status: implemented on 2026-07-16; retained as the acceptance record for the hardening pass.

## Outcome

Make CodeContext safe to trust as the first tool for repository orientation while
preserving the compact response economics established after phase 5. The next pass
should eliminate known false negatives, make deeper traversal observable, distinguish
direct tests from indirect test usage, and keep generated or ignored files out of the
graph.

The workstreams below describe the implemented contract and its regression coverage.
The tracked `out/win-x64` cleanup remains intentionally separate from this change.

## Current baseline

The self-hosted review confirmed these improvements:

- compact, exact-first queries bound ambiguous results and make `Process` safe by
  default;
- concrete-class blast radius, repository-relative paths, relationship aggregation,
  direct-test discovery, and API metrics now work;
- `depth=0` provides a genuinely cheap target-only query;
- the installed skill plus measured responses used about 43% fewer tokens than the
  corresponding broad `rg` output, although `rg` remained cheaper after removing the
  ambiguous-name scenario.

The remaining gaps are grouped below in suggested implementation order.

## Workstream 1: honor project `.gitignore` files

### Required behavior

Create one shared repository-file selection service and use it for initial scans, full
refreshes, single-file refreshes, and watcher events. It must:

- discover the root `.gitignore` and nested `.gitignore` files under the indexed root;
- apply rules relative to the directory containing each ignore file;
- honor rule order, nested overrides, negation, anchored patterns, directory-only
  patterns, comments, escaped leading `!`/`#`, and normalized path separators;
- avoid descending into excluded directories when Git semantics make reinclusion
  impossible;
- preserve mandatory safety exclusions such as `.git/`, CodeContext runtime state,
  inaccessible paths, and reparse-point loops. Document which built-in exclusions
  cannot be negated;
- work when Git is not installed and when the root is not a Git repository. Prefer a
  maintained ignore-pattern library or a small independently tested component over
  shelling out to `git check-ignore` for every file;
- cache compiled rules by directory without making scan cost proportional to the
  number of ignore files times the number of source files.

Project-local `.gitignore` files are the first scope. Global Git excludes,
`.git/info/exclude`, and submodule/worktree metadata can be evaluated separately after
the local semantics are correct.

### Reconciliation behavior

- A changed `.gitignore` must invalidate the affected matcher subtree and enqueue a
  reconciliation.
- Newly ignored files must be removed from the committed graph generation.
- Newly included files must be parsed and added.
- Readers must continue seeing the last complete generation while reconciliation is
  running.
- Status should expose the active ignore source count and, optionally, ignored-file
  totals without placing paths or large rule sets in the default response.

### Acceptance tests

- Root rules exclude `out/**`, `bin/**`, and matched individual files.
- Nested rules override parent rules where Git permits it.
- Negation, anchored patterns, escaped markers, spaces, and Windows separators match
  Git behavior on a fixture repository.
- Initial scan, full refresh, single-file refresh, and watcher-created files make the
  same include/exclude decision.
- Editing a `.gitignore` removes newly ignored nodes/edges and adds newly included
  files without exposing a partial graph.
- Generated TypeScript worker copies under this repository's `out/**` never appear as
  duplicate symbols.

### Repository cleanup note

`out/` is now ignored locally, but existing `out/win-x64` files are already tracked, so
Git will continue reporting changes to them. In a future cleanup, decide whether the
canonical publish payload belongs in source control. If not, remove it from the index
in an intentional commit and have CI retain publish artifacts instead. Do not combine
that history-heavy change with the ignore-engine implementation.

## Workstream 2: close semantic call-edge false negatives

### Top-level TypeScript and JavaScript calls

The TypeScript worker currently emits calls only when it can attach them to a declared
caller. A top-level invocation such as `startReadLoop()` therefore disappears from
`usedBy`.

- Represent module/file scope as a normalized caller node, or define an equivalent
  source-file call edge.
- Emit top-level calls, initializers, and other executable module-scope expressions.
- Keep the caller presentation compact: file, line, relation kind, and occurrence
  count are sufficient.
- Ensure source and emitted/published copies do not multiply the edge once ignore
  support lands.

### C# mock and fluent invocation chains

Calls inside NSubstitute setup/verification expressions were not all attached to
`GetCompleteContextAsync`.

- Add focused Roslyn fixtures for `Received()`, `DidNotReceive()`, `Returns(...)`, and
  related fluent/extension chains.
- Inspect symbol and candidate-symbol resolution for the inner invocation rather than
  treating the outer fluent expression as the call target.
- Normalize constructed/overridden/interface method symbols to a stable original
  definition without collapsing unrelated overloads.
- Preserve a relationship kind that lets consumers distinguish a direct production
  call from a test/mock setup if that distinction can be made reliably.

### Acceptance tests

- `usedBy(startReadLoop)` includes its module/file-scope caller and line.
- All source call sites for the caller fixture are represented or explicitly marked as
  unresolved; silent false negatives are not acceptable.
- Interface-dispatch, direct, mock, extension, generic, and overload calls resolve to
  the intended target with no duplicate relationship entries.

## Workstream 3: make traversal depth observable

Depth now affects traversal, but depth 2 and 3 can return byte-identical visible lists
while only a total count changes. An agent cannot tell which additional nodes were
reached or why.

- Define depth 0 as target only and depth 1 as direct relationships.
- For transitive results, include a compact `distance` and relation path/category, or
  return a separate bounded transitive section. Do not mix transitive nodes into a
  direct list without labeling them.
- Deduplicate cycles by stable node identity while preserving the shortest discovered
  path and occurrence totals.
- Make counts unambiguous: distinguish direct total, transitive total, returned count,
  and truncation.
- Apply `maxRelationships` predictably per relationship category or document a shared
  budget. Consider a separate transitive cap if it prevents direct results from being
  crowded out.
- Add a hint when increasing depth changes only data hidden by the current cap.

Acceptance: a fixture with a three-hop chain must produce observably different,
correctly labeled responses at depths 0, 1, 2, and 3, including under truncation and in
the presence of a cycle.

## Workstream 4: state test semantics honestly

> Note (July 2026, .NET 10 upgrade Phase 2): `ILanguageParser` has since been removed;
> language support is now provided exclusively by out-of-process workers. The
> test-semantics concern below still applies to any consumed symbol — read the old
> `ILanguageParser` reference as an illustrative example only.

`GraphUpdateService` direct tests are now reported accurately, but a consumed symbol can
return `isTested=false` despite test fakes and indirect test references. The current
field overstates what a naming/direct-call heuristic can prove.

- Define separate concepts for directly exercised by a test method, referenced by test
  code, implemented/faked in tests, and merely sharing a name with a test file.
- Prefer fields such as `directlyTested`, `testReferences`, and `testImplementers` over
  one coverage-like boolean. Keep any compatibility field explicitly documented.
- Derive direct test methods from graph edges where possible; use naming only as a
  fallback and identify heuristic results.
- Never describe static references as runtime coverage.
- Keep test details opt-in and capped, with total counts and truncation preserved.

Acceptance: direct tests, indirect service tests, test-only interface fakes, helper
methods, and unrelated name matches must be classified consistently, and listed method
counts must agree with the returned method set and source attributes.

## Workstream 5: improve precise disambiguation

Methods with the same name on an interface and implementation currently require
bounded expansion followed by client-side selection.

- Add a stable qualified identifier or filters for containing type, namespace/module,
  signature, and source file.
- Return qualified identity in ambiguous summaries only when needed; do not add it to
  every unambiguous compact node.
- Include a ready-to-use refinement hint derived from the available facets.
- Use the same identity rules in single, multi, REST, and MCP queries.

Acceptance: a caller can select the interface declaration or concrete implementation
of `GetCompleteContextAsync` in one compact request without relying on internal node
IDs.

## Workstream 6: preserve bounded call-site detail explicitly

Aggregated edges now carry occurrence counts and up to three source lines. That is a
good compact default, but the response does not make the line cap independently
controllable.

- Add `maxCallSites` with a small default independent of `maxRelationships`.
- Add a per-edge call-site total or `callSitesTruncated` when not all locations are
  returned.
- Allow `maxCallSites=0` for count-only relationship orientation.
- Keep file/line data grouped with the caller so repeated calls carry information
  rather than recreating duplicate edges.

Acceptance: a caller with seven invocations is one relationship entry with seven
occurrences, a truthful truncation indicator, and zero, three, or seven locations when
requested.

## Workstream 7: make optional Kuzu tests deterministic

The normal Release build is clean, and 455 non-Kuzu managed tests pass. The full suite
currently attempts optional Kuzu integration tests without a provisioned `.venv`, then
the native Python host can crash after setup failures.

- Assign explicit traits/categories to optional Kuzu integration tests.
- Make the default managed test command skip them, or make the test fixture skip with a
  clear prerequisite message when the environment is absent.
- Add a dedicated CI job that provisions the exact Python/Kuzu environment and runs
  those tests in isolation.
- Ensure failed setup cannot leave CPython state that crashes the test host.

Acceptance: the default suite has a deterministic pass/fail result on a clean checkout,
and the dedicated Kuzu job either passes or reports an ordinary test failure rather
than terminating the host.

## Suggested sequence

1. Implement `.gitignore` selection and reconciliation, then remove duplicate publish
   symbols from the self-hosted index.
2. Fix TypeScript module-scope and C# mock/fluent call edges with source-backed
   fixtures.
3. Define and expose depth/distance/count semantics before changing more response
   fields.
4. Split direct and indirect test metadata.
5. Add qualified disambiguation and independent call-site caps.
6. Isolate optional Kuzu integration tests.

After each workstream, republish CodeContext, reinstall the skill if its contract
changed, index this repository from a clean state, and rerun the same CodeContext-versus-
`rg` measurements. Track both correctness and raw response bytes; a semantic fix is not
complete if its default serialization reintroduces unbounded context cost.

## Completion criteria

- Ignored files never enter or remain in the graph across any indexing path.
- Known top-level and mock/fluent calls are represented without duplicates.
- Every depth level has documented, externally observable meaning.
- Test metadata distinguishes direct evidence, indirect references, and heuristics.
- Ambiguous interface/implementation methods can be selected in one bounded request.
- Call-site truncation is explicit and independently configurable.
- A clean checkout builds with zero warnings and has deterministic default tests.
- The self-hosted review has no silent correctness failure, and compact defaults remain
  within the established token budget.
