# Operations: troubleshooting, lifecycle, and REST

## Stale or unexpected results

If results look stale or startup/readiness fails, run `codecontext status --path .`.
Before trusting an unexpected empty result, confirm contract version 1, the intended
root and instance, indexing readiness, and the relevant parser session.

Use `codecontext list --json` to inspect registrations. Only after that, use
`codecontext start --detach --path .` for manual lifecycle troubleshooting.

A human can pre-warm the index before agent work with `codecontext init --path .`
(add `--wait`) so the first query skips the cold-start scan.

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
