# Operations: troubleshooting, lifecycle, and REST

## Stale or unexpected results

If results look stale or startup/readiness fails, run `codecontext status --path .`.
Before trusting an unexpected empty result, confirm contract version 1, the intended
root and instance, indexing readiness, and the relevant parser session. A `hibernated`
session is healthy: its worker exited while idle (after 2 minutes if it re-reads its files
in under 5 seconds, else 15; `CodeContext__WorkerHibernateSeconds` sets one time for all,
`0` off), answers come from the current index, and the next change or refresh restarts it.
`failed` or `unavailable` is not.

Use `codecontext list --json` to inspect registrations. Only after that, use
`codecontext start --detach --path .` for manual lifecycle troubleshooting.

With `--snapshot` (or `CodeContext__QueryFromSnapshot=true`; `--start` overrides it) and
no instance running, `query` first tries the saved index in its own process. It answers
from it (label `cache snapshot`; `--json` carries `cache.state` `"snapshot"`) only when the
snapshot authenticates, was written by a verified instance, every worker reported what it
read, and nothing of that changed since by file time: the files and folders a scan walks,
every file a worker read outside them (project files, configs, restore data, referenced
assemblies, declaration files, also above the root) still there and no newer, and every
path a worker looked for and did not find still absent. On Linux the time is the status
change time, which `tar -x`, `cp -p` or `touch -d` cannot set back. Each folder's `.gitignore`
is looked at whether or not its own rules ignore it. The saved index must also have been
written with the workers installed now (same ids, versions, extensions and manifest
locations, manifests untouched since), and must not be dated after the present time.
Otherwise stderr says
why (`Not answering from the saved index: ...`) and it starts an instance. It writes
nothing and starts no worker. `CodeContext__CacheEnabled=false` turns it off with the cache.

Not seen: on Windows and macOS, content rewritten in place with an older modification
time set afterwards; an edit landing between a worker's read and the save when the
instance stops before noticing it; a worker's own files rewritten in place without its
manifest or its folder's listing changing; edits made while the clock was set back, once
the clock has passed the saved index's time again.

`query` waits at most 30 seconds for indexing. On timeout it prints "Indexing did not
become ready within 30 seconds" and exits with code 3; check `codecontext status --path .`
and query again once `indexing.status` is `ready`.

A human can pre-warm the index before agent work with `codecontext init --path .`
(add `--wait`) so the first query skips the cold-start scan.

A restarted instance answers from the index it saved under `.codecontext/cache/` while
its startup scan re-checks the files; those answers carry the `cache` label (see SKILL.md)
and status shows `indexing.cache.state` `verifying` with `servingCached` true, until
`indexing.status` is `ready`. `unverified` means the re-check failed: answers stay from the
saved index until `POST /api/index/refresh` (or a restart) re-checks it. `ignored` with a
`reason` means a saved index was refused and the instance scans from scratch (`query`
waits for `ready`). `codecontext start --no-cache` runs without the cache.
Project files and restores are watched, so a changed `.csproj` or a `dotnet restore` takes
effect without a source edit. Projects are read from their files alone: nothing a repository
supplies is ever run, including MSBuild, analyzers and source generators. `Directory.Build.*`
is searched up to the enclosing repository, or the indexed root when there is none. Each
project is analyzed for one configuration and target: `Debug`, or the first it declares, and
its first target framework. Other targets are listed in status as inactive, and code only
they compile is not seen.

## REST API

For filters and operations not exposed by `query`, get the port from
`codecontext list --json` and call the API directly:

```bash
curl "http://localhost:<port>/api/context/complete?identifier=X&type=Method&containingType=C"
curl -X POST "http://localhost:<port>/api/index/refresh"
```

After a refresh, wait for ready status and an `operationId` at least as new as the
refresh response before querying.

The whole-graph queries are `GET` endpoints (MCP: `get_path`, `get_affected`, `get_hubs`,
same parameter names; `/api/status` lists the endpoints under `api.endpoints`):

```bash
curl "http://localhost:<port>/api/graph/path?from=A&to=B&maxDepth=10&relation=CALLS&includeTests=false"
curl "http://localhost:<port>/api/graph/affected?identifier=X&maxDepth=5&limit=20&relation=CALLS&includeTests=false"
curl "http://localhost:<port>/api/graph/hubs?limit=20&within=src/Core&type=Class&relation=CALLS&includeTests=false&includeExternal=false"
```

Diff impact is a `POST` with a JSON body sent as `application/json` (otherwise a bare 415;
MCP: `get_impact`, the same names); a query string is refused:

```bash
git diff $(git merge-base main HEAD) | jq -Rs '{diff: ., maxDepth: 5, limit: 20}' \
  | curl -X POST -H 'Content-Type: application/json' --data-binary @- "http://localhost:<port>/api/graph/impact"
```

`complete` is true only when every changed line mapped and every mapped file matched the
working tree by blob id; `unmapped` (with `unmappedCount`, `unmappedReasons`), `changes`,
`items` and `testFiles` each carry their own count and truncation. A change's
`usedByChanges` (at most five, with `usedByChangeCount`) names the other changes that use
it, since a change is never among `items`. `setAsideFiles` (with `setAsideFileCount`)
lists changed files that cannot change an indexed fact; they do not make the answer
incomplete. A diff that cannot be
read (truncated hunk, garbled line, the same file twice, over 16 MiB or 10,000 files) is a
400 naming its line; a 503 `INDEX_NOT_READY` means no scan has finished yet.

A parameter an endpoint does not take, a value of the wrong kind or out of range is a
400 whose `error.message` says which (MCP: an `isError` result). A 503 `GRAPH_CHANGING`
means the index changed under every attempt to read one generation of it; retry once
indexing settles. Counts follow the context responses: `totalCount`, `returnedCount`,
`truncated` only when something was left out, and `depthLimited` only when something lies
beyond `maxDepth`.
