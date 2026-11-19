#!/usr/bin/env bash
# Measures the resident memory of a CodeContext instance (host and worker processes).
#
# Usage: scripts/bench/measure-memory.sh [--path DIR] [--no-cache] [--queries]
#          [--queries-file FILE] [--heap] [--top N] [--settle SECONDS]
#          [--write-timeout SECONDS] [--json] [--binary PATH]
#          [--hibernate SECONDS [--probe-cs FILE] [--probe-ts FILE]]
#
# Starts an instance on DIR (default: the current directory), cached (the default: a
# warm-up start writes a snapshot, then the measured start loads it) or --no-cache,
# waits until the index is ready and verified, then settles (below) and prints Rss,
# Pss_Anon and Pss_File from /proc/<pid>/smaps_rollup for the host and each worker.
# --queries runs a fixed query mix, settles again and reads again; --heap then reports
# the live managed heap of the host and the C# worker with dotnet-gcdump, by type
# (gcdump-types.csx: not .cs, so this repository's own index holds no loose C# file and
# the C# worker builds no fallback compilation). Readings are taken before any gcdump,
# which grows the target's RSS.
#
# Settling, idle reading: first the snapshot the start writes after its scan commits
# (about 10 s after ready; status shows a new cache.snapshotSequence once it is written
# and verified), up to --write-timeout seconds (default 60); then --settle seconds
# (default 20) with no traffic, which covers V8's memory reducer in the TypeScript
# worker (about 12 s after its last work) and the quiet-time memory trim of the host and
# the C# worker (15 s, CodeContext:MemoryTrimSeconds); then until every process's RSS
# moved less than 2 MB over 3 one-second samples (at most 30 s more). After the query
# mix, the same without the snapshot step (queries write nothing). The output's write=
# and stable= are the seconds the first and last waits took (write=-1: no write seen).
#
# --hibernate SECONDS starts the instance with CodeContext:WorkerHibernateSeconds set. After
# the idle reading (and the query mix), it times an edit to a C# and a TypeScript file while
# the workers run (append a probe class, poll `query` until it resolves), waits until every
# worker session is hibernated, settles, reads again (phase hibern), times a query, times the
# same edits while hibernated (each wakes its worker), settles and reads again (phase woken).
# Probe files default to src/CodeContext.Core/CodeGraph.cs and
# tests/CodeContext.Core.Tests/TestFiles/TypeScript/SimpleClass.ts under DIR; each is restored.
#
# The instance is always stopped; a .codecontext directory the script created is removed;
# `git status --short` must be unchanged at the end. Linux only; needs jq, and
# dotnet-gcdump for --heap. The binary defaults to the Release build in this repository
# (override with --binary or CODECONTEXT_BIN).
set -euo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo_dir=$(cd "$script_dir/../.." && pwd)

bin=${CODECONTEXT_BIN:-$repo_dir/src/CodeContext.Api/bin/Release/net10.0/codecontext}
path=.
cache=1
queries=0
queries_file=
heap=0
top=12
settle=20
write_timeout=60
json=0
timeout=300
hibernate=
probe_cs=
probe_ts=

usage() { sed -n '2,31s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"; }

while [ $# -gt 0 ]; do
  case $1 in
    --path) path=$2; shift 2 ;;
    --no-cache) cache=0; shift ;;
    --queries) queries=1; shift ;;
    --queries-file) queries=1; queries_file=$2; shift 2 ;;
    --heap) heap=1; shift ;;
    --top) top=$2; shift 2 ;;
    --settle) settle=$2; shift 2 ;;
    --write-timeout) write_timeout=$2; shift 2 ;;
    --json) json=1; shift ;;
    --binary) bin=$2; shift 2 ;;
    --hibernate) hibernate=$2; shift 2 ;;
    --probe-cs) probe_cs=$2; shift 2 ;;
    --probe-ts) probe_ts=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

die() { echo "measure-memory: $*" >&2; exit 1; }
log() { echo "measure-memory: $*" >&2; }

command -v jq >/dev/null || die "jq is required"
[ -x "$bin" ] || die "no codecontext binary at $bin (build with: dotnet build -c Release)"
root=$(cd "$path" && pwd -P) || die "no such directory: $path"

# The apphost needs DOTNET_ROOT when the runtime is not in a default location.
if [ -z "${DOTNET_ROOT:-}" ] && command -v dotnet >/dev/null; then
  export DOTNET_ROOT=$(dirname "$(readlink -f "$(command -v dotnet)")")
fi
if [ "$heap" = 1 ]; then
  command -v dotnet-gcdump >/dev/null || die "--heap needs dotnet-gcdump (dotnet tool install -g dotnet-gcdump)"
  gcdump_dir=$(dirname "$(readlink -f "$(command -v dotnet-gcdump)")")
  GCDUMP_DLL=$(find "$gcdump_dir" -name dotnet-gcdump.dll -path '*/tools/*' 2>/dev/null | sort | tail -1)
  [ -n "$GCDUMP_DLL" ] || die "cannot find dotnet-gcdump.dll under $gcdump_dir"
  export GCDUMP_DLL
fi

# The fixed query mix: the symbol and tests lines of the repository's query list
# (scripts/bench/queries-<name>.txt, 40 + 20 for this repository). A line is
# "<kind><TAB><identifier>" or a bare identifier (kind symbol); other kinds are skipped.
if [ "$queries" = 1 ]; then
  [ -n "$queries_file" ] || queries_file=$script_dir/queries-$(basename "$root").txt
  [ -f "$queries_file" ] || die "no query list at $queries_file (pass --queries-file)"
fi

if [ -n "$hibernate" ]; then
  export CodeContext__WorkerHibernateSeconds=$hibernate
  [ -n "$probe_cs" ] || probe_cs=$root/src/CodeContext.Core/CodeGraph.cs
  [ -n "$probe_ts" ] || probe_ts=$root/tests/CodeContext.Core.Tests/TestFiles/TypeScript/SimpleClass.ts
  for f in "$probe_cs" "$probe_ts"; do [ -f "$f" ] || die "no probe file $f"; done
fi

work=$(mktemp -d)
created_cc=0
[ -e "$root/.codecontext" ] || created_cc=1
git_before=
in_git=0
if git -C "$root" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  in_git=1
  git_before=$(git -C "$root" status --short)
fi

host_pid() {
  "$bin" list --json 2>/dev/null | jq -r --arg r "$root" '.[] | select(.rootPath == $r) | .pid' | head -1
}

stop_instance() {
  local pid
  pid=$(host_pid)
  [ -n "$pid" ] || return 0
  "$bin" stop --path "$root" >/dev/null 2>&1 || true
  for _ in $(seq 1 60); do
    kill -0 "$pid" 2>/dev/null || return 0
    sleep 0.5
  done
  log "instance $pid did not stop; killing it"
  pkill -P "$pid" 2>/dev/null || true
  kill "$pid" 2>/dev/null || true
}

cleanup() {
  local status=$?
  stop_instance
  [ -f "$work/probe-cs.orig" ] && cp "$work/probe-cs.orig" "$probe_cs"
  [ -f "$work/probe-ts.orig" ] && cp "$work/probe-ts.orig" "$probe_ts"
  if [ "$created_cc" = 1 ]; then rm -rf "$root/.codecontext"; fi
  rm -rf "$work"
  if [ "$in_git" = 1 ] && [ "$(git -C "$root" status --short)" != "$git_before" ]; then
    log "warning: git status --short changed during the run"
    [ "$status" != 0 ] || status=1
  fi
  exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

[ -z "$(host_pid)" ] || die "an instance is already running for $root; stop it first"

start_instance() {
  local args=(start --detach --path "$root")
  [ "$cache" = 1 ] || args+=(--no-cache)
  "$bin" "${args[@]}" >"$work/start.out" 2>&1 || { cat "$work/start.out" >&2; die "start failed"; }
}

# Ready: indexed, status ready, and not serving the snapshot while it is verified.
wait_ready() {
  local deadline=$((SECONDS + timeout)) s
  while [ $SECONDS -lt $deadline ]; do
    s=$("$bin" status --path "$root" 2>/dev/null || true)
    if [ -n "$s" ] && echo "$s" | jq -e '.indexing.indexed == true and .indexing.status == "ready"
        and ((.indexing.cache.servingCached // false) == false)' >/dev/null 2>&1; then
      echo "$s" >"$work/status.json"
      return 0
    fi
    sleep 0.5
  done
  die "index not ready after ${timeout}s"
}

if [ "$cache" = 1 ]; then
  log "warm-up start to write a snapshot"
  start_instance
  wait_ready
  stop_instance
  # The stop writes the snapshot; wait until it exists and its size is stable.
  prev=
  for _ in $(seq 1 60); do
    cur=$(ls -l "$root"/.codecontext/cache/*/graph.*.ccsnap 2>/dev/null || true)
    [ -n "$cur" ] && [ "$cur" = "$prev" ] && break
    prev=$cur
    sleep 1
  done
  [ -n "$cur" ] || die "no snapshot under $root/.codecontext/cache after the warm-up start"
fi

log "measured start ($([ "$cache" = 1 ] && echo cached || echo no-cache))"
t0=$SECONDS
start_instance
wait_ready
ready_s=$((SECONDS - t0))
if [ "$cache" = 1 ]; then
  jq -e '.indexing.cache.verification != null' "$work/status.json" >/dev/null \
    || die "the measured start did not load the snapshot: $(jq -c .indexing.cache "$work/status.json")"
fi
host=$(host_pid)
[ -n "$host" ] || die "cannot find the host process"

# Processes as "<label> <pid>": the host, then its children (the workers) in a fixed
# order: csharp, typescript, then any other worker by name.
processes() {
  echo "host $host"
  local c cmd label
  for c in $(pgrep -P "$host"); do
    cmd=$(tr '\0' ' ' <"/proc/$c/cmdline" 2>/dev/null || true)
    case $cmd in
      *CodeContext.CSharp.Worker*) label="1 csharp" ;;
      *typescript-worker*) label="2 typescript" ;;
      *) label="3 $(basename "${cmd%% *}")" ;;
    esac
    echo "$label $c"
  done | sort | cut -d' ' -f2-
}

# Appends "<phase> <label> <rss_kb> <pss_anon_kb> <pss_file_kb>" per process and a total.
read_memory() {
  local phase=$1 label pid
  processes >"$work/procs"
  while read -r label pid; do
    awk -v p="$phase" -v l="$label" '
      $1 == "Rss:" { r = $2 } $1 == "Pss_Anon:" { a = $2 } $1 == "Pss_File:" { f = $2 }
      END { print p, l, r, a, f }' "/proc/$pid/smaps_rollup"
  done <"$work/procs" >"$work/phase"
  awk '{ r += $3; a += $4; f += $5; print } END { print $1, "total", r, a, f }' "$work/phase" >>"$work/memory"
}

# The newest snapshot sequence status reports, or empty when none was written or loaded.
snapshot_sequence() {
  "$bin" status --path "$root" 2>/dev/null | jq -r '.indexing.cache.snapshotSequence // empty' 2>/dev/null || true
}

# Waits for the snapshot write that follows the start's scan: status reports a sequence
# other than the one at ready (the loaded snapshot's, or none without a cache). Without a
# snapshot service (--no-cache) there is none to wait for. Prints the seconds waited.
wait_snapshot_write() {
  local before=$1 deadline=$((SECONDS + write_timeout)) start=$SECONDS cur
  while [ $SECONDS -lt $deadline ]; do
    cur=$(snapshot_sequence)
    if [ -n "$cur" ] && [ "$cur" != "$before" ]; then
      echo $((SECONDS - start))
      return 0
    fi
    sleep 1
  done
  log "no snapshot written within ${write_timeout}s of ready (sequence ${before:-none}); reading anyway"
  echo -1
}

# Sum of the processes' RSS in kB, one line per process, in a fixed order.
rss_line() {
  local label pid
  processes | while read -r label pid; do
    awk '$1 == "Rss:" { printf "%s ", $2 }' "/proc/$pid/smaps_rollup" 2>/dev/null
  done
}

# Waits until no process's RSS moved more than 2 MB across 3 consecutive one-second
# samples, at most 30 s. Prints the seconds waited.
wait_stable() {
  local start=$SECONDS prev cur stable=0
  prev=$(rss_line)
  while [ $((SECONDS - start)) -lt 30 ]; do
    sleep 1
    cur=$(rss_line)
    if awk -v a="$prev" -v b="$cur" 'BEGIN { n = split(a, x, " "); m = split(b, y, " "); if (n != m) exit 1;
        for (i = 1; i <= n; i++) { d = x[i] - y[i]; if (d < 0) d = -d; if (d > 2048) exit 1 } exit 0 }'; then
      stable=$((stable + 1))
      [ $stable -ge 2 ] && break
    else
      stable=0
    fi
    prev=$cur
  done
  echo $((SECONDS - start))
}

ready_sequence=$(jq -r '.indexing.cache.snapshotSequence // empty' "$work/status.json")
write_wait=-1
[ "$cache" = 1 ] || ready_sequence=
if [ "$cache" = 1 ] || [ "$(jq -r '.indexing.cache // empty' "$work/status.json")" != "" ]; then
  write_wait=$(wait_snapshot_write "$ready_sequence")
fi
sleep "$settle"
stable_wait=$(wait_stable)
read_memory idle

query_count=0
query_failures=0
if [ "$queries" = 1 ]; then
  while IFS=$'\t' read -r kind id; do
    case $kind in ''|'#'*) continue ;; esac
    if [ -z "$id" ]; then id=$kind; kind=symbol; fi
    case $kind in
      symbol) args=() ;;
      tests) args=(--tests) ;;
      *) continue ;;
    esac
    query_count=$((query_count + 1))
    "$bin" query "$id" "${args[@]}" --path "$root" </dev/null >/dev/null 2>&1 || query_failures=$((query_failures + 1))
  done <"$queries_file"
  sleep "$settle"
  stable_wait_queries=$(wait_stable)
  read_memory queries
fi

hibernation=
if [ -n "$hibernate" ]; then
  now_ms() { date +%s%3N; }
  cp "$probe_cs" "$work/probe-cs.orig"
  cp "$probe_ts" "$work/probe-ts.orig"
  probe_n=0
  # Appends a probe class to the file and prints the milliseconds until `query` resolves it.
  probe() {
    local kind=$1 file=$2 name t0 deadline
    probe_n=$((probe_n + 1))
    name=HibernationProbe${kind}${probe_n}
    case $kind in
      Cs) printf '\ninternal static class %s { }\n' "$name" >>"$file" ;;
      Ts) printf '\nexport class %s { }\n' "$name" >>"$file" ;;
    esac
    t0=$(now_ms)
    deadline=$((SECONDS + 120))
    until "$bin" query "$name" --path "$root" </dev/null >/dev/null 2>&1; do
      [ $SECONDS -lt $deadline ] || { echo -1; return 0; }
      sleep 0.05
    done
    echo $(( $(now_ms) - t0 ))
  }
  restore_probes() {
    cp "$work/probe-cs.orig" "$probe_cs"
    cp "$work/probe-ts.orig" "$probe_ts"
  }
  sessions() { "$bin" status --path "$root" 2>/dev/null | jq -r '[.parsers.sessions[]? | select(.state != "notNeeded") | .state] | join(",")'; }

  awake_cs=$(probe Cs "$probe_cs")
  awake_ts=$(probe Ts "$probe_ts")
  restore_probes
  log "edits while awake: C# ${awake_cs} ms, TypeScript ${awake_ts} ms; waiting for hibernation"
  deadline=$((SECONDS + hibernate + 300))
  until [ "$(sessions)" != "" ] && sessions | grep -qvE '(^|,)(ready|indexing|starting|failed|unavailable|stopped)(,|$)'; do
    [ $SECONDS -lt $deadline ] || die "workers did not hibernate (sessions: $(sessions))"
    sleep 2
  done
  hibernated_sessions=$(sessions)
  sleep "$settle"
  wait_stable >/dev/null
  read_memory hibern
  t0=$(now_ms)
  "$bin" query ContextService --path "$root" </dev/null >"$work/hq.out" 2>&1 || die "query while hibernated failed"
  query_ms=$(( $(now_ms) - t0 ))
  query_label=$(grep -c '^cache' "$work/hq.out" || true)
  [ "$(pgrep -P "$host" | wc -l)" = 0 ] || log "warning: worker processes ran during the hibernated query"
  wake_cs=$(probe Cs "$probe_cs")
  wake_ts=$(probe Ts "$probe_ts")
  restore_probes
  sleep "$settle"
  wait_stable >/dev/null
  read_memory woken
  hibernation="hibernate=${hibernate}s  sessions=${hibernated_sessions}  query_hibernated=${query_ms}ms (cache label lines: ${query_label})
edit->answer ms: awake C# ${awake_cs}, TS ${awake_ts}; hibernated C# ${wake_cs}, TS ${wake_ts}"
fi

heap_rows=
if [ "$heap" = 1 ]; then
  while read -r label pid; do
    case $label in host|csharp) ;; *) continue ;; esac
    dotnet-gcdump collect -p "$pid" -o "$work/$label.gcdump" </dev/null >"$work/gcdump.log" 2>&1 \
      || { cat "$work/gcdump.log" >&2; die "gcdump of $label failed"; }
  done <"$work/procs"
  stop_instance
  for label in host csharp; do
    [ -f "$work/$label.gcdump" ] || continue
    dotnet run --file "$script_dir/gcdump-types.csx" -- "$work/$label.gcdump" "$top" \
      >"$work/$label.types" 2>"$work/gcdump-types.log" \
      || { cat "$work/gcdump-types.log" >&2; die "gcdump-types.csx failed"; }
    sed "s/^/$label\t/" "$work/$label.types" >>"$work/heap"
  done
fi

mode=$([ "$cache" = 1 ] && echo cached || echo no-cache)
files=$(jq -r '.indexing.filesTotal' "$work/status.json")
if [ "$json" = 1 ]; then
  jq -n --arg root "$root" --arg mode "$mode" --argjson files "$files" --argjson ready "$ready_s" \
    --argjson settle "$settle" --argjson writeWait "$write_wait" --argjson stableWait "$stable_wait" --argjson queries "$query_count" --argjson failures "$query_failures" \
    --rawfile memory "$work/memory" --rawfile heap "$( [ -f "$work/heap" ] && echo "$work/heap" || echo /dev/null)" '
    { root: $root, mode: $mode, files: $files, readySeconds: $ready, settleSeconds: $settle,
      snapshotWriteWaitSeconds: $writeWait, stableWaitSeconds: $stableWait,
      queries: $queries, queryFailures: $failures,
      memory: [ $memory | split("\n")[] | select(length > 0) | split(" ")
        | { phase: .[0], process: .[1], rssKb: (.[2] | tonumber),
            pssAnonKb: (.[3] | tonumber), pssFileKb: (.[4] | tonumber) } ],
      heap: ([ $heap | split("\n")[] | select(length > 0) | split("\t") ]
        | group_by(.[0]) | map({ (.[0][0]): {
            liveBytes: (map(select(.[1] == "total"))[0][2] | tonumber),
            liveObjects: (map(select(.[1] == "total"))[0][3] | tonumber),
            types: map(select(.[1] != "total")
              | { type: .[3], bytes: (.[1] | tonumber), objects: (.[2] | tonumber) }) } }) | add) }'
  exit 0
fi

printf '%s  %s  files=%s  ready=%ss  write=%ss  settle=%ss  stable=%ss' "$root" "$mode" "$files" "$ready_s" \
  "$write_wait" "$settle" "$stable_wait"
[ "$queries" = 1 ] && printf '  queries=%s (failed %s)' "$query_count" "$query_failures"
[ -n "$hibernation" ] && printf '\n%s' "$hibernation"
printf '\n%-8s %-11s %9s %9s %9s\n' phase process rss_mb anon_mb file_mb
awk '{ printf "%-8s %-11s %9.1f %9.1f %9.1f\n", $1, $2, $3/1024, $4/1024, $5/1024 }' "$work/memory"
if [ -f "$work/heap" ]; then
  awk -F'\t' '
    $2 == "total" { printf "\n%s live heap %.1f MB, %d objects\n", $1, $3/1048576, $4; next }
    { printf "  %9.1f MB %10d  %s\n", $2/1048576, $3, $4 }' "$work/heap"
fi
