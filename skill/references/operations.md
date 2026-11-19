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
change time, which `tar -x`, `cp -p` or `touch -d` cannot set back. Otherwise stderr says
why (`Not answering from the saved index: ...`) and it starts an instance. It writes
nothing and starts no worker. `CodeContext__CacheEnabled=false` turns it off with the cache.

Not seen: on Windows and macOS, content rewritten in place with an older modification
time set afterwards; an edit landing between a worker's read and the save when the
instance stops before noticing it; a change of configured ignore patterns or of the
installed workers.

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
