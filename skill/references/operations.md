# Operations: troubleshooting, lifecycle, and REST

## Stale or unexpected results

If results look stale or startup/readiness fails, run `codecontext status --path .`.
Before trusting an unexpected empty result, confirm contract version 1, the intended
root and instance, indexing readiness, and the relevant parser session.

Use `codecontext list --json` to inspect registrations. Only after that, use
`codecontext start --detach --path .` for manual lifecycle troubleshooting.

A human can pre-warm the index before agent work with `codecontext init --path .`
(add `--wait`) so the first query skips the cold-start scan.

## REST API

For filters and operations not exposed by `query`, get the port from
`codecontext list --json` and call the API directly:

```bash
curl "http://localhost:<port>/api/context/complete?identifier=X&type=Method&containingType=C"
curl -X POST "http://localhost:<port>/api/index/refresh"
```

After a refresh, wait for ready status and an `operationId` at least as new as the
refresh response before querying.
