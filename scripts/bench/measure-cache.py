"""Measure CodeContext cold start, memory, query latency and edit latency on one repository.

The persistent-fact-cache plan (docs/PERSISTENT_FACT_CACHE_PLAN.md, section 6) defines every
number taken here. Phase 0 ran this against the v0.10.0 and v0.10.7 release assets; phase 11
runs it against v0.10.7 and the branch build back to back. Rows that need the cache or worker
hibernation run only when the build has the capability (`--no-cache` and
`--worker-idle-timeout` in `start --help`); otherwise they are recorded as "not applicable".
With --require-cache / --require-hibernation a missing capability, or a missing expected
state while the row runs, is fatal instead.

Usage:
  python scripts/bench/measure-cache.py --exe <codecontext.exe> --repo <dir> --label <name>
      --config scripts/bench/<repo>.json [--runs 5] [--warmup 1] [--rows ...] [--out f.jsonl]

Output: progress lines on stderr; one JSON line per run ("type": "run"; a disturbed run is
written with "discarded": true and its reason) and one summary line ("type": "summary",
median/min/max per number, value counts for booleans, nulls and strings) on stdout and
appended to --out. With --allow-busy every line carries "untimed": true.

Commit detection: watcher updates do not advance indexing.operationId, so a committed edit is
seen as a change in database.nodeCount/edgeCount with status "ready". Every timed edit
therefore adds or removes a fact: the body edit inserts one call (one CALLS edge), the
top-level edit adds one method (one node). Status is polled every 5 ms while an edit is
pending; each detection keeps the bracket [last negative poll sent, first positive poll
answered] and reports its midpoint.

Safety:
- Refuses to run while any registered instance is on the repository or an ancestor of it
  (query would pick that instance); waits up to --instance-wait minutes, then exits 3.
- An instance is "ours" only while `list --json` shows a record with exactly this root, the
  pid and instanceId seen at attach, and the OS reports the same process creation time; it
  must also have been created after the spawn that should have started it. Only such an
  instance is stopped, through its own `/api/shutdown?instanceId=`; anything else is logged
  and left alone. `stop --all` is never used.
- Edit targets must be clean in git (or named with --allow-dirty-target) and must not already
  hold the probe text. The original bytes are saved under %TEMP%\\ccbench\\recover before the
  first write; a leftover recovery file from a killed run makes the script refuse to start
  and print how to restore. Writes go to a temporary file in the same directory and replace
  the target atomically. `git status --porcelain -- <edited files>` must be the same before
  and after (exit 2 otherwise). git runs with GIT_OPTIONAL_LOCKS=0.

Standard library only; Windows only (process memory, CPU and listing use the Win32 API, the
same counters Get-Process reports as WorkingSet64 and PrivateMemorySize64).
"""
import argparse, collections, ctypes, ctypes.wintypes as wt, datetime, hashlib, http.client
import json, os, re, shutil, statistics, subprocess, sys, tempfile, threading, time
from urllib.parse import urlencode

NA = "not applicable in this build"
BUSY = re.compile(r"^(dotnet|testhost.*|vstest.*|msbuild|vbcscompiler|"
                  r"codecontext\.(core\.tests|fakeworker))(\.exe)?$", re.I)
CC_HOST = "codecontext.exe"
CC_WORKERS = {"codecontext.csharp.worker.exe"}
ALL_ROWS = ["cold", "memory", "first_edit", "queries", "edits", "cold_changed", "first_edit_root",
            "snapshot", "hibernation"]
# Rows recorded as "not applicable" and the figure the document compares them with.
NEAREST_COMPARABLE = {
    "cachedColdStart": "cold.ready_s",
    "snapshotFirstScan": None,
    "snapshotAfterEdit": None,
    "hibernation": "memoryAfterReady.settled.host (resident); bodyEdits.apply_s (wake)",
}
SETTLE_S = 5.0                 # before every memory sample and every first-edit row
EDIT_POLL_S = 0.005            # status poll interval while an edit is pending
COLD_POLL_S = 0.05             # status poll interval during a cold start
STATUS_RERESOLVE_S = 10.0      # status failing this long: re-read the registry
STATUS_FAIL_LIMIT_S = 120.0    # status failing this long: fatal
SNAPSHOT_STABLE_S = 15.0       # no newer write for this long (quiet period is 10 s)
RECOVER_DIR = os.path.join(tempfile.gettempdir(), "ccbench", "recover")
TMP_SUFFIX = ".ccbench-tmp"
DEFAULT_DECL_MARKER = "ProbeMethodForMeasurement"
GIT_ENV = dict(os.environ, GIT_OPTIONAL_LOCKS="0")

OWN = {}  # pid -> creation time of every process this harness spawned


def log(msg):
    print(f"# {time.strftime('%H:%M:%S')} {msg}", file=sys.stderr, flush=True)


class Fatal(Exception):
    def __init__(self, msg, code=1):
        super().__init__(msg)
        self.code = code


class Disturbed(Exception):
    """The run cannot be a measurement (a foreign instance appeared); discard and retry."""


# ---------------------------------------------------------------- Win32 process helpers

class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("th32DefaultHeapID", ctypes.c_void_p), ("th32ModuleID", wt.DWORD),
                ("cntThreads", wt.DWORD), ("th32ParentProcessID", wt.DWORD),
                ("pcPriClassBase", ctypes.c_long), ("dwFlags", wt.DWORD),
                ("szExeFile", ctypes.c_wchar * 260)]


class PROCESS_MEMORY_COUNTERS_EX(ctypes.Structure):
    _fields_ = [("cb", wt.DWORD), ("PageFaultCount", wt.DWORD),
                ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
                ("QuotaNonPagedPoolUsage", ctypes.c_size_t), ("PagefileUsage", ctypes.c_size_t),
                ("PeakPagefileUsage", ctypes.c_size_t), ("PrivateUsage", ctypes.c_size_t)]


if os.name == "nt":
    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    psapi = ctypes.WinDLL("psapi", use_last_error=True)
    k32.CreateToolhelp32Snapshot.restype = wt.HANDLE
    k32.OpenProcess.restype = wt.HANDLE
    k32.Process32FirstW.argtypes = [wt.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
    k32.Process32NextW.argtypes = [wt.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
    k32.CloseHandle.argtypes = [wt.HANDLE]
    k32.GetProcessTimes.argtypes = [wt.HANDLE] + [ctypes.POINTER(wt.FILETIME)] * 4
    k32.GetExitCodeProcess.argtypes = [wt.HANDLE, ctypes.POINTER(wt.DWORD)]
    k32.GetSystemTimes.argtypes = [ctypes.POINTER(wt.FILETIME)] * 3
    psapi.GetProcessMemoryInfo.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.DWORD]


def _ft(t):
    return (t.dwHighDateTime << 32) | t.dwLowDateTime


def processes():
    """[(pid, exe name, parent pid)] for every process on the machine."""
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    out = []
    try:
        e = PROCESSENTRY32W()
        e.dwSize = ctypes.sizeof(e)
        ok = k32.Process32FirstW(snap, ctypes.byref(e))
        while ok:
            out.append((e.th32ProcessID, e.szExeFile, e.th32ParentProcessID))
            ok = k32.Process32NextW(snap, ctypes.byref(e))
    finally:
        k32.CloseHandle(snap)
    return out


def _times(pid):
    """(creation as Unix time, kernel+user CPU in 100 ns units, still running) or None."""
    h = k32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not h:
        return None
    try:
        t = [wt.FILETIME() for _ in range(4)]
        if not k32.GetProcessTimes(h, *[ctypes.byref(x) for x in t]):
            return None
        code = wt.DWORD()
        running = bool(k32.GetExitCodeProcess(h, ctypes.byref(code))) and code.value == 259
        return _ft(t[0]) / 1e7 - 11644473600, _ft(t[2]) + _ft(t[3]), running
    finally:
        k32.CloseHandle(h)


def creation_time(pid):
    """Process creation time as a Unix timestamp, or None."""
    t = _times(pid) if pid else None
    return t[0] if t else None


def process_alive(pid, created):
    """True while the process with this pid AND this creation time is running (pid reuse
    and exited processes whose handles are still open both read as not alive)."""
    t = _times(pid) if pid else None
    return bool(t and t[2] and created is not None and abs(t[0] - created) < 0.001)


def memory_mb(pid):
    """{workingSetMB, privateMB} like Get-Process WorkingSet64 / PrivateMemorySize64."""
    if not pid:
        return None
    h = k32.OpenProcess(0x1000 | 0x0010, False, pid)  # QUERY_LIMITED_INFORMATION | VM_READ
    if not h:
        return None
    try:
        c = PROCESS_MEMORY_COUNTERS_EX()
        c.cb = ctypes.sizeof(c)
        if not psapi.GetProcessMemoryInfo(h, ctypes.byref(c), c.cb):
            return None
        return {"workingSetMB": round(c.WorkingSetSize / 2**20, 1),
                "privateMB": round(c.PrivateUsage / 2**20, 1)}
    finally:
        k32.CloseHandle(h)


def system_times():
    """(idle, kernel, user) in 100 ns units, summed over all processors; kernel includes idle."""
    i, k, u = wt.FILETIME(), wt.FILETIME(), wt.FILETIME()
    if not k32.GetSystemTimes(ctypes.byref(i), ctypes.byref(k), ctypes.byref(u)):
        return None
    return _ft(i), _ft(k), _ft(u)


def own_tree(procs, roots, ctime=None):
    """Pids in `procs` that are `roots` ({pid: creation}) or descend from them. A live root
    counts only with its recorded creation time (a reused pid is someone else's), and a child
    only if it was created after its parent, so a reused parent pid does not adopt it."""
    ctime = ctime or creation_time
    children = collections.defaultdict(list)
    live = set()
    for pid, _, ppid in procs:
        live.add(pid)
        if pid != ppid:
            children[ppid].append(pid)
    out, stack = set(), []
    for p, c in roots.items():
        if p in live:
            ct = ctime(p)
            if ct is None or c is None or abs(ct - c) > 0.01:
                continue
            out.add(p)
        stack.append((p, c))
    while stack:
        p, pc = stack.pop()
        for ch in children.get(p, ()):
            if ch in out:
                continue
            cc = ctime(ch)
            if cc is not None and (pc is None or cc >= pc):
                out.add(ch)
                stack.append((ch, cc))
    return out


def remember_own(pid):
    OWN[pid] = creation_time(pid) or time.time()


def foreign_codecontext(procs, own):
    """CodeContext hosts and workers not in `own` (TypeScript workers are node.exe children of
    a codecontext host)."""
    names = {pid: name.lower() for pid, name, _ in procs}
    hits = []
    for pid, name, ppid in procs:
        n = name.lower()
        is_cc = n == CC_HOST or n in CC_WORKERS or (n == "node.exe" and names.get(ppid) == CC_HOST)
        if is_cc and pid not in own:
            hits.append(f"{name}:{pid}")
    return hits


def run_cli(cmd, timeout=120, env=None):
    """subprocess.run that records the child as ours (for the disturbance check)."""
    p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                         encoding="utf-8", errors="replace", env=env)
    remember_own(p.pid)
    try:
        out, err = p.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        p.kill()
        p.communicate()
        raise Fatal(f"{' '.join(cmd[:3])} did not finish within {timeout}s")
    return subprocess.CompletedProcess(cmd, p.returncode, out, err)


class BusyWatch:
    """Samples every 2 s: build/test processes, CodeContext processes that are not ours, and
    system CPU not used by this harness's own process tree (GetSystemTimes minus the
    GetProcessTimes of our processes), as a percentage of all logical processors."""

    def __init__(self, own_roots):
        self.own_roots = own_roots          # callable -> {pid: creation}
        self.seen = set()
        self.cpu_other, self.cpu_total = [], []
        self._cpu = {}                      # (pid, creation) -> last cpu
        self._sys = None
        self._stop = threading.Event()
        self._t = threading.Thread(target=self._run, daemon=True)

    def _sample(self):
        procs = processes()
        roots = {os.getpid(): creation_time(os.getpid()) or 0.0}
        roots.update(OWN)
        roots.update(self.own_roots())
        own = own_tree(procs, roots)
        self.seen.update(name for _, name, _ in procs if BUSY.match(name))
        self.seen.update(foreign_codecontext(procs, own))
        own_delta = 0
        for pid in own:
            t = _times(pid)
            if not t:
                continue
            key = (pid, round(t[0], 3))
            own_delta += t[1] - self._cpu.get(key, 0 if self._sys and t[0] > self._sys[0] else t[1])
            self._cpu[key] = t[1]
        st = system_times()
        if st and self._sys:
            _, (i0, k0, u0) = self._sys
            i, k, u = st
            total = (k - k0) + (u - u0)
            if total > 0:
                busy = total - (i - i0)
                self.cpu_total.append(100.0 * busy / total)
                self.cpu_other.append(max(0.0, 100.0 * (busy - own_delta) / total))
        if st:
            self._sys = (time.time(), st)

    def _run(self):
        while not self._stop.wait(2.0):
            try:
                self._sample()
            except Exception as e:  # sampling must never break a run
                log(f"warning: disturbance sample failed: {e}")

    def report(self):
        def r(v):
            return round(v, 1) if v is not None else None
        return {"processes": sorted(self.seen),
                "cpuOtherMeanPct": r(statistics.mean(self.cpu_other)) if self.cpu_other else None,
                "cpuOtherMaxPct": r(max(self.cpu_other)) if self.cpu_other else None,
                "cpuTotalMeanPct": r(statistics.mean(self.cpu_total)) if self.cpu_total else None,
                "samples": len(self.cpu_other)}

    def __enter__(self):
        self._sample()
        self._t.start()
        return self

    def __exit__(self, *a):
        self._stop.set()
        self._t.join()


# ---------------------------------------------------------------- CodeContext helpers

def norm(p):
    return os.path.normcase(os.path.abspath(p)).rstrip("\\/")


def parse_list(stdout):
    """Records from `list --json`; Fatal on anything that is not a JSON array of objects, so
    an unreadable list can never read as "no instances"."""
    try:
        v = json.loads(stdout)
    except ValueError:
        try:
            v = json.loads(stdout[stdout.index("["):stdout.rindex("]") + 1])
        except ValueError:
            raise Fatal(f"cannot parse `list --json` output: {stdout.strip()[:300]!r}")
    if not isinstance(v, list) or not all(isinstance(x, dict) for x in v):
        raise Fatal(f"`list --json` is not an array of records: {stdout.strip()[:300]!r}")
    return v


def same_or_ancestor(record_root, root):
    rp = norm(record_root or "")
    return bool(rp) and (rp == root or root.startswith(rp + os.sep))


def find_record(records, root, pid, instance_id):
    """The record that is exactly our instance: same root, pid and instanceId."""
    for r in records:
        if (norm(r.get("rootPath", "")) == root and r.get("pid") == pid
                and r.get("instanceId") == instance_id):
            return r
    return None


def conflicting(records, root):
    """Records on this root or an ancestor of it (query and start would use them)."""
    return [r for r in records if same_or_ancestor(r.get("rootPath"), root)]


def check_created_after(created, spawned_after, what):
    if created is None:
        raise Fatal(f"{what}: process is gone or unreadable")
    if spawned_after is not None and created < spawned_after:
        raise Fatal(f"instance not ours: {what} was created "
                    f"{spawned_after - created:.3f}s before the harness spawned its start")


class Instance:
    def __init__(self, exe, repo, has_cache=False):
        self.exe, self.repo, self.root = exe, repo, norm(repo)
        self.has_cache = has_cache
        self.spawned_after = None
        self.commits = 0
        self.workers = {}            # worker pid -> creation time
        self._conn = None
        self.port = self.pid = self.instance_id = self.created = None
        # Snapshot bookkeeping: pending_write is set by a commit (or a fresh scan) that the
        # snapshot on disk does not hold yet; seq_mark is the sequence seen at that point.
        self.pending_write, self.seq_mark, self.seq_at_ready = False, None, None
        self.snapshot_expected = False
        self.snapshotted_counts = self.last_counts = None

    def list(self):
        p = run_cli([self.exe, "list", "--json"], timeout=60)
        if p.returncode:
            raise Fatal(f"`list --json` exited {p.returncode}: {p.stderr.strip()[-300:]}")
        return parse_list(p.stdout)

    def conflicting(self):
        return conflicting(self.list(), self.root)

    def find_own(self, records=None):
        """A record on exactly this root whose process was created after our last spawn."""
        if self.spawned_after is None:
            return None
        for r in self.list() if records is None else records:
            if norm(r.get("rootPath", "")) == self.root:
                ct = creation_time(r.get("pid"))
                if ct is not None and ct >= self.spawned_after:
                    return r
        return None

    def attach(self, rec, spawned_after):
        if norm(rec.get("rootPath", "")) != self.root:
            raise Fatal(f"instance not ours: it is registered for {rec.get('rootPath')}, "
                        f"not {self.repo}")
        created = creation_time(rec.get("pid"))
        check_created_after(created, spawned_after, f"host pid {rec.get('pid')}")
        if not rec.get("instanceId"):
            raise Fatal(f"record for {self.repo} has no instanceId; cannot prove it is ours")
        self.port, self.pid, self.instance_id = rec["port"], rec["pid"], rec["instanceId"]
        self.created, self.commits, self.workers = created, 0, {}
        self._conn = None
        OWN[self.pid] = created

    def detach(self):
        if self._conn:
            self._conn.close()
        self._conn = None
        self.port = self.pid = self.instance_id = self.created = None
        self.workers = {}

    def is_ours(self):
        if not self.pid:
            return False
        return (find_record(self.list(), self.root, self.pid, self.instance_id) is not None
                and process_alive(self.pid, self.created))

    def request(self, method, path, timeout=30):
        """Request over one keep-alive connection; returns (status, raw body bytes)."""
        for attempt in (0, 1):
            if self._conn is None:
                self._conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=timeout)
            try:
                self._conn.request(method, path)
                r = self._conn.getresponse()
                return r.status, r.read()
            except (OSError, http.client.HTTPException):
                self._conn.close()
                self._conn = None
                if attempt:
                    raise

    def status(self):
        code, body = self.request("GET", "/api/status", timeout=10)
        s = json.loads(body) if body else None
        if code != 200 or not isinstance(s, dict):
            raise OSError(f"status answered {code}")
        iid = (s.get("system") or {}).get("instanceId")
        if iid is not None and iid != self.instance_id:
            raise Fatal(f"port {self.port} is answered by instance {iid}, not ours "
                        f"({self.instance_id}); not touching it")
        for x in (s.get("parsers") or {}).get("sessions") or []:
            pid = x.get("pid")
            if pid and pid not in self.workers:
                ct = creation_time(pid)
                if ct is not None:
                    self.workers[pid] = ct
                    OWN[pid] = ct
        return s

    def stop(self):
        """Shut down our instance through its own port, after proving it is still ours."""
        if not self.pid:
            return
        pid, created, root = self.pid, self.created, self.repo
        try:
            self.status()  # refresh worker pids
        except Fatal:
            raise
        except Exception:
            pass
        workers = dict(self.workers)
        if not self.is_ours():
            log(f"not stopping: the instance for {root} (pid {pid}) is no longer the one this "
                "harness started; leaving whatever is registered alone")
            self.detach()
            return
        try:
            code, _ = self.request("POST", "/api/shutdown?" + urlencode(
                {"instanceId": self.instance_id}), timeout=10)
            if code != 202:
                log(f"warning: /api/shutdown answered {code}")
        except (OSError, http.client.HTTPException) as e:
            log(f"warning: /api/shutdown failed: {e}")
        if self._conn:
            self._conn.close()
            self._conn = None
        deadline = time.time() + 60
        while time.time() < deadline and process_alive(pid, created):
            time.sleep(0.1)
        if process_alive(pid, created):
            # Still ours and still running: `stop --path` resolves the closest record, which
            # is ours because it is registered on exactly this root; it re-validates identity
            # before killing.
            if self.is_ours():
                log(f"warning: pid {pid} ignored /api/shutdown; stop --path")
                run_cli([self.exe, "stop", "--path", self.repo], timeout=120)
            deadline = time.time() + 30
            while time.time() < deadline and process_alive(pid, created):
                time.sleep(0.2)
        deadline = time.time() + 60
        while time.time() < deadline and any(process_alive(p, c) for p, c in workers.items()):
            time.sleep(0.2)
        left = [p for p, c in [(pid, created)] + list(workers.items()) if process_alive(p, c)]
        if left:
            log(f"warning: processes still alive after stop: {left}")
        self.detach()


def counts(s):
    db = s["database"]
    return [db.get("nodeCount"), db.get("edgeCount")]


def session(s, parser):
    for x in (s or {}).get("parsers", {}).get("sessions", []) or []:
        if x.get("parserId") == parser:
            return x
    return None


def cache_of(s):
    return ((s or {}).get("indexing") or {}).get("cache")


CACHE_KEEP = ("state", "servingCached", "verification", "snapshotSequence", "lastWriteError",
              "reason")


def cache_fields(c):
    return {k: c.get(k) for k in CACHE_KEEP} if isinstance(c, dict) else None


def check_cached_start(states_seen, ready_cache):
    """A cold start that should have loaded a snapshot really did: the cache was seen
    `verifying` or `verified`, and the C# verification outcome is recorded at ready."""
    if not ({"verifying", "verified"} & set(states_seen)):
        raise Fatal(f"expected a cached cold start; cache states seen: {sorted(states_seen, key=str)}"
                    f", at ready: {ready_cache}")
    v = ((ready_cache or {}).get("verification") or {}).get("csharp")
    if v in (None, "pending"):
        raise Fatal(f"cached cold start: verification.csharp not recorded at ready ({ready_cache})")


def snapshot_progress(c, seq_mark):
    """For a status cache object: 'done' when a snapshot newer than seq_mark is written and
    verified, 'wait' otherwise; Fatal on a write error."""
    if not isinstance(c, dict):
        return "wait"
    if c.get("lastWriteError"):
        raise Fatal(f"snapshot write failed: {c['lastWriteError']}")
    seq = c.get("snapshotSequence")
    if seq is None or (seq_mark is not None and seq <= seq_mark):
        return "wait"
    return "done" if c.get("state") == "verified" else "wait"


def capabilities_from_help(text):
    return {"cache": "--no-cache" in text, "hibernation": "--worker-idle-timeout" in text}


def require_capabilities(caps, require_cache, require_hibernation):
    if require_cache and not caps["cache"]:
        raise Fatal("--require-cache: this build has no cache (`--no-cache` absent from start --help)")
    if require_hibernation and not caps["hibernation"]:
        raise Fatal("--require-hibernation: this build has no `--worker-idle-timeout`")


def session_starting(state):
    """A csharp session state that means the worker is being started (not listed only)."""
    return state not in (None, "notNeeded", "hibernated")


# ---------------------------------------------------------------- git

def git(repo, *cmd, timeout=120):
    p = subprocess.run(["git", "-C", repo] + list(cmd), capture_output=True, text=True,
                       timeout=timeout, env=GIT_ENV, encoding="utf-8", errors="replace")
    if p.returncode:
        raise Fatal(f"git {' '.join(cmd[:2])} failed in {repo}: {p.stderr.strip()}")
    return p.stdout


def git_porcelain(repo, files):
    return git(repo, "status", "--porcelain", "--", *files)


def repo_state(repo):
    head = git(repo, "rev-parse", "HEAD").strip()
    lines = git(repo, "status", "--porcelain").splitlines()
    kinds = collections.Counter(l[:2] for l in lines)
    return {"head": head, "porcelainLines": len(lines), "porcelainKinds": dict(kinds),
            "porcelainSha1": hashlib.sha1("\n".join(lines).encode()).hexdigest()}


# ---------------------------------------------------------------- edits

BODY_ANCHOR = r"\n(\s+)public [^\n]*\)\s*\r?\n\s+\{\r?\n"
TOP_ANCHOR = r"\n(\s*)public ((?:abstract |partial |sealed |static )*)class [^\n]*\r?\n\s*\{\r?\n"
METHOD_NAME = re.compile(r"(\w+)\s*(?:<[^()<>]*(?:<[^()<>]*>[^()<>]*)*>)?\s*\(")
TYPE_DECL = re.compile(r"\b(?:class|struct|record|interface)\s+(\w+)")


CODE_NOISE = re.compile(r'//[^\n]*|/\*.*?\*/|@"(?:[^"]|"")*"|"(?:[^"\\\n]|\\.)*"|'
                        r"'(?:[^'\\\n]|\\.)'", re.S)


def enclosing_type(src, pos):
    """The innermost type whose braces contain `pos` (comments and string literals blanked
    first, so braces inside them are not counted)."""
    code = CODE_NOISE.sub(lambda m: " " * len(m.group(0)), src)
    best = None
    for m in TYPE_DECL.finditer(code, 0, pos):
        open_at = code.find("{", m.end())
        if open_at < 0 or open_at > pos:
            continue
        depth, i = 0, open_at
        while i < len(code):
            ch = code[i]
            if ch == "{":
                depth += 1
            elif ch == "}":
                depth -= 1
                if depth == 0:
                    break
            i += 1
        if i > pos:
            best = m.group(1)
    return best


def body_edit(target):
    # The first matching method body: insert a call after its opening brace, so the edit
    # adds exactly one CALLS edge and the commit is observable. The anchor must land in the
    # configured Type.Method, or a moved repository would silently measure something else.
    def mutate(src):
        m = re.search(target.get("anchor", BODY_ANCHOR), src)
        if not m:
            raise Fatal(f"body anchor not found in {target['file']}")
        mm = METHOD_NAME.search(m.group(0))
        found = f"{enclosing_type(src, m.start())}.{mm.group(1) if mm else None}"
        want = target["method"].split(".")[-2:]
        if found.split(".") != want:
            raise Fatal(f"body anchor in {target['file']} lands in {found}, "
                        f"config says {target['method']}")
        nl = "\r\n" if "\r\n" in src else "\n"
        return src[:m.end()] + m.group(1) + "    " + target["statement"] + nl + src[m.end():]
    return mutate


def top_edit(target):
    # Add a public method right after the class's opening brace (static in a static class).
    def mutate(src):
        m = re.search(target.get("anchor", TOP_ANCHOR), src)
        if not m:
            raise Fatal(f"top-level anchor not found in {target['file']}")
        cls = TYPE_DECL.search(m.group(0))
        if not cls or cls.group(1) != target["symbol"].split(".")[-1]:
            raise Fatal(f"top-level anchor in {target['file']} lands in class "
                        f"{cls.group(1) if cls else None}, config says {target['symbol']}")
        nl = "\r\n" if "\r\n" in src else "\n"
        static = "static " if "static" in (m.group(2) if m.lastindex >= 2 else "") else ""
        decl = target.get("declaration", f"public {static}void {DEFAULT_DECL_MARKER}() {{ }}")
        return src[:m.end()] + m.group(1) + "    " + decl + nl + src[m.end():]
    return mutate


def probe_marker(target):
    if target["kind"] == "top":
        return target.get("declaration", DEFAULT_DECL_MARKER)
    return target["statement"]


def read_bytes(path):
    with open(path, "rb") as f:
        return f.read()


def atomic_write(path, data):
    """Write bytes to a sibling temporary file, flush it to disk, then replace the target, so
    a kill leaves either the old or the new content, never a truncated file."""
    d, name = os.path.split(path)
    tmp = os.path.join(d, "." + name + TMP_SUFFIX)
    with open(tmp, "wb") as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())
    for attempt in range(20):
        try:
            os.replace(tmp, path)
            return
        except PermissionError:
            if attempt == 19:
                os.remove(tmp)
                raise
            time.sleep(0.05)


def recovery_paths(recover_dir, path):
    h = hashlib.sha1(norm(path).encode("utf-8")).hexdigest()[:16]
    return os.path.join(recover_dir, h + ".orig"), os.path.join(recover_dir, h + ".json")


def restore_hint(path, orig, manifest):
    return (f"A recovery copy of {path} exists: {orig} (manifest {manifest}). A previous run "
            "was stopped with an edit applied. Compare it with the file, restore it with\n"
            f"  Copy-Item -LiteralPath '{orig}' -Destination '{path}'\n"
            "if the file still holds the harness's edit, then delete both recovery files.")


def is_strict_prefix(now, of):
    return len(now) < len(of) and of.startswith(now)


class Edits:
    """Owns the original bytes of every edit target and restores them on close.

    A file is only ever written when it holds exactly the bytes this script expects (the
    original before an apply, our edit before a revert); anything else means someone else
    changed it, and the run fails without touching it.
    """

    def __init__(self, repo, targets, allow_dirty=(), recover_dir=RECOVER_DIR,
                 porcelain=git_porcelain):
        self.repo, self.targets, self.recover_dir = repo, targets, recover_dir
        self._porcelain = porcelain
        self.paths = {k: os.path.join(repo, t["file"]) for k, t in targets.items()}
        if len(set(map(norm, self.paths.values()))) != len(self.paths):
            raise Fatal("each edit target must be a different file")
        allowed = {norm(os.path.join(repo, f)) for f in allow_dirty}
        for k, p in self.paths.items():
            orig, manifest = recovery_paths(recover_dir, p)
            if os.path.exists(orig) or os.path.exists(manifest):
                raise Fatal(restore_hint(p, orig, manifest), 2)
            tmp = os.path.join(os.path.dirname(p), "." + os.path.basename(p) + TMP_SUFFIX)
            if os.path.exists(tmp):
                os.remove(tmp)  # only this script creates files with this name
                log(f"removed a leftover temporary file {tmp}")
        self.porcelain_before = self.porcelain()
        for k, t in targets.items():
            p = self.paths[k]
            line = self._porcelain(repo, [t["file"]])
            if line.strip() and norm(p) not in allowed:
                raise Fatal(f"{t['file']} is not clean in git ({line.strip()}); refusing to edit "
                            "it. Name it with --allow-dirty-target if that is intended.", 2)
        self.original, self.edited, self.saved = {}, {}, set()
        for k, t in targets.items():
            p = self.paths[k]
            data = read_bytes(p)
            try:
                text = data.decode("utf-8")
            except UnicodeDecodeError:
                raise Fatal(f"{p} is not UTF-8; the harness edits UTF-8 files only")
            if probe_marker(t) in text:
                raise Fatal(f"{p} already contains {probe_marker(t)!r}: an earlier run probably "
                            "left its edit there. Restore the file (git diff) first.", 2)
            self.original[p] = data
            self.edited[k] = (top_edit if t["kind"] == "top" else body_edit)(t)(text).encode("utf-8")
            assert self.edited[k] != data

    def porcelain(self):
        return self._porcelain(self.repo, sorted({t["file"] for t in self.targets.values()}))

    def _save_recovery(self, p):
        if p in self.saved:
            return
        os.makedirs(self.recover_dir, exist_ok=True)
        orig, manifest = recovery_paths(self.recover_dir, p)
        atomic_write(orig, self.original[p])
        atomic_write(manifest, json.dumps({
            "path": p, "sha256": hashlib.sha256(self.original[p]).hexdigest(),
            "savedAt": datetime.datetime.now().isoformat(timespec="seconds"),
            "harnessPid": os.getpid()}, indent=1).encode("utf-8"))
        self.saved.add(p)

    def _drop_recovery(self, p):
        for f in recovery_paths(self.recover_dir, p):
            if os.path.exists(f):
                os.remove(f)
        self.saved.discard(p)

    def apply(self, key):
        p = self.paths[key]
        if read_bytes(p) != self.original[p]:
            raise Fatal(f"{p} changed outside this script; not editing it", 2)
        self._save_recovery(p)
        atomic_write(p, self.edited[key])

    def revert(self, key):
        p = self.paths[key]
        if read_bytes(p) != self.edited[key]:
            raise Fatal(f"{p} changed outside this script while edited; not reverting it", 2)
        atomic_write(p, self.original[p])

    def restore_all(self):
        """Put every target back; True when all hold their original bytes afterwards. The
        recovery copy is used for a file that is empty or a strict prefix of the original or
        of the edit (a write cut short). Anything else is left alone and reported."""
        ok = True
        for k, p in self.paths.items():
            try:
                now = read_bytes(p)
                if now == self.original[p]:
                    pass
                elif now == self.edited[k]:
                    atomic_write(p, self.original[p])
                    log(f"restored {p}")
                elif p in self.saved and (not now or is_strict_prefix(now, self.original[p])
                                          or is_strict_prefix(now, self.edited[k])):
                    orig, _ = recovery_paths(self.recover_dir, p)
                    saved = read_bytes(orig)
                    if saved != self.original[p]:
                        raise OSError(f"recovery copy {orig} does not match the original")
                    atomic_write(p, saved)
                    log(f"restored {p} from its recovery copy (it held a partial write)")
                else:
                    ok = False
                    log(f"ERROR: {p} holds neither the original nor this script's edit; left "
                        "as is." + (" Its recovery copy is kept. " + restore_hint(
                            p, *recovery_paths(self.recover_dir, p)) if p in self.saved else ""))
                    continue
                self._drop_recovery(p)
            except OSError as e:
                ok = False
                log(f"ERROR restoring {p}: {e}")
        return ok

    def check(self):
        after = self.porcelain()
        if after != self.porcelain_before:
            raise Fatal("git status of edited files changed during the run:\n"
                        f"before:\n{self.porcelain_before}after:\n{after}", 2)


# ---------------------------------------------------------------- measurement steps

def wait_counts_change(inst, before, t_ref, timeout=900, interval=EDIT_POLL_S):
    """Poll until status is ready with counts different from `before`. Returns the detection
    bracket relative to t_ref: lo = when the last poll that saw no change was sent (the commit
    came after the server read that), hi = when the first poll that saw it was answered."""
    lo = 0.0
    while time.perf_counter() - t_ref < timeout:
        sent = time.perf_counter()
        try:
            s = inst.status()
        except Fatal:
            raise
        except Exception:
            s = None
        if s and s["indexing"]["status"] == "ready" and counts(s) != before:
            hi = time.perf_counter() - t_ref
            inst.commits += 1
            inst.pending_write = True
            inst.seq_mark = (cache_of(s) or {}).get("snapshotSequence")
            return {"s": round((lo + hi) / 2, 4), "bracket": [round(lo, 4), round(hi, 4)],
                    "status": s}
        if s:
            lo = sent - t_ref
        time.sleep(interval)
    raise Fatal(f"graph counts did not change from {before} within {timeout}s")


def timed_edit(inst, edits, key):
    """Apply, wait for the commit, revert at once (the file stays edited only for as long as
    the apply takes to commit), wait again."""
    before = counts(inst.status())
    t = time.perf_counter()
    edits.apply(key)
    a = wait_counts_change(inst, before, t)
    after = counts(a["status"])
    t = time.perf_counter()
    edits.revert(key)
    b = wait_counts_change(inst, after, t)
    if counts(b["status"]) != before:
        raise Fatal(f"revert did not restore counts: {before} -> {counts(b['status'])}")
    return {"apply_s": a["s"], "revert_s": b["s"], "applyBracket": a["bracket"],
            "revertBracket": b["bracket"], "delta": [after[0] - before[0], after[1] - before[1]]}


def parse_query_output(out, ident):
    """Validate compact `query` output: an optional leading `cache` line, no `missing` or
    `matches` (ambiguous or non-exact) line, and exactly one `target` line naming `ident`."""
    lines = out.splitlines()
    res = {"cacheLine": False, "cacheState": None, "lines": len(lines)}
    rest = lines
    if lines and lines[0].startswith("cache\t"):
        res["cacheLine"] = True
        res["cacheState"] = (lines[0].split("\t") + [None])[1]
        rest = lines[1:]
    for l in rest:
        if l.startswith("missing\t") or l.startswith("matches\t"):
            raise Fatal(f"query {ident!r} did not answer with one exact target: {l!r}")
    targets = [l.split("\t")[1] for l in rest if l.startswith("target\t")]
    if len(targets) != 1:
        raise Fatal(f"query {ident!r}: expected one target line, got {len(targets)}")
    name = targets[0].split("(")[0]
    while re.search(r"<[^<>]*>", name):
        name = re.sub(r"<[^<>]*>", "", name)
    if not re.search(r"(^|[.:])" + re.escape(ident) + r"$", name):
        raise Fatal(f"query {ident!r} answered with target {targets[0]!r}")
    res["target"] = targets[0]
    return res


def spawn_query(exe, repo, ident):
    p = subprocess.Popen([exe, "query", ident, "--path", repo], stdout=subprocess.PIPE,
                         stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")
    remember_own(p.pid)
    return p


def cold_start(inst, ident, changed_counts_from=None, expect_cached=False, timeout=1800):
    """Spawn `query <ident>` with no instance running and time everything from that instant.

    Returns a dict with: each query attempt's exit code and time (re-spawning on a readiness
    timeout, exit 3, until one exits 0; the exit instant is taken in the thread that waits on
    the process), the validated answer and its `cache` line, time to status ready, the csharp
    session timeline, worker creation time from the OS relative to the host's (the headline
    "host start to worker process" figure), and the cache fields at first status, at the
    answer and at ready. If changed_counts_from is given, also the time until status is ready
    with counts that differ from those (the corrected answer after an offline edit).
    """
    hits = inst.conflicting()
    if hits:
        raise Disturbed(f"instance registered on {[h.get('rootPath') for h in hits]} before a "
                        "cold start")
    wall0 = time.time()
    inst.spawned_after = wall0
    t0 = time.perf_counter()
    procs, done_at, threads = [], {}, []
    res = {"queryAttempts": []}

    def spawn():
        p = spawn_query(inst.exe, inst.repo, ident)
        idx = len(procs)
        procs.append((p, time.perf_counter()))

        def waiter():
            out, err = p.communicate()
            done_at[idx] = (p.returncode, out, err, time.perf_counter())
        th = threading.Thread(target=waiter, daemon=True)
        th.start()
        threads.append(th)

    try:
        spawn()
        rec = None
        sess_seen = pid_seen = ready_at = corrected_at = answered = None
        first_status = None
        timeline, last = [], None
        cache_states, cache_timeline, last_cache = set(), [], None
        fail_since = reresolved = None
        while True:
            now = time.perf_counter() - t0
            if now > timeout:
                raise Fatal(f"cold start did not finish within {timeout}s")
            # Query process bookkeeping: on exit 3 (readiness timeout) spawn another, as an
            # agent would; stop at the first exit 0 or any other code.
            idx = len(procs) - 1
            if answered is None and idx in done_at:
                code, out, err, tend = done_at[idx]
                res["queryAttempts"].append({"exit": code, "s": round(tend - procs[idx][1], 3)})
                if code == 0:
                    answered = tend - t0
                    q = parse_query_output(out, ident)
                    res["cacheLine"], res["cacheLineState"] = q["cacheLine"], q["cacheState"]
                    res["queryStdoutLines"], res["queryTarget"] = q["lines"], q["target"]
                elif code == 3 and len(procs) < 20:
                    spawn()
                else:
                    raise Fatal(f"query exited {code}: {err.strip()[-400:]}")
            if rec is None:
                records = inst.list()  # one read for both tests, so our own new record
                rec = inst.find_own(records)  # cannot read as a foreign one
                if rec:
                    inst.attach(rec, wall0)
                    res["registered_s"] = round(now, 3)
                else:
                    if conflicting(records, inst.root):
                        raise Disturbed("an instance on an ancestor or another start appeared "
                                        "during the cold start")
                    time.sleep(0.1)
                    continue
            try:
                s = inst.status()
                fail_since = None
            except Fatal:
                raise
            except Exception:
                s = None
                fail_since = fail_since if fail_since is not None else now
                if now - fail_since > STATUS_FAIL_LIMIT_S:
                    raise Fatal(f"status of pid {inst.pid} unreachable for {STATUS_FAIL_LIMIT_S}s")
                if now - fail_since > STATUS_RERESOLVE_S and reresolved != fail_since:
                    # Possibly a stale record: re-read the registry.
                    reresolved = fail_since
                    again = inst.find_own()
                    if not again or again.get("pid") != inst.pid or \
                            again.get("instanceId") != inst.instance_id:
                        log("status unreachable and the record changed; re-resolving")
                        inst.detach()
                        rec, fail_since = None, None
                        continue
            if s:
                c = cache_fields(cache_of(s))
                state = (c or {}).get("state")
                cache_states.add(state)
                if c != last_cache:
                    cache_timeline.append([round(now, 3), c])
                    last_cache = c
                cs = session(s, "csharp")
                if first_status is None:
                    first_status = now
                    res["firstStatus_s"] = round(now, 3)
                    res["cacheAtFirstStatus"] = c
                    res["sessionStartingAtFirstStatus"] = bool(cs and session_starting(cs.get("state")))
                    res["workerPidAtFirstStatus"] = bool(cs and cs.get("pid"))
                if answered is not None and "cacheAtAnswer" not in res:
                    res["cacheAtAnswer"] = c
                key = (cs.get("state"), cs.get("pid"), cs.get("message")) if cs else None
                if key != last:
                    timeline.append([round(now, 3)] + (list(key) if key else [None]))
                    last = key
                # On 0.10.0 the csharp session is listed from the first status as "notNeeded"
                # with no pid; it turns "starting" when enumeration has routed files to it.
                if cs and sess_seen is None and session_starting(cs.get("state")):
                    sess_seen = now
                if cs and cs.get("pid") and pid_seen is None:
                    pid_seen = now
                    res["workerPid"] = cs["pid"]
                ix = s["indexing"]
                if ready_at is None and ix.get("status") == "ready":
                    ready_at = now
                    res["readyStatus"] = s
                    res["cacheAtReady"] = c
                if (changed_counts_from is not None and corrected_at is None
                        and ix.get("status") == "ready" and counts(s) != changed_counts_from):
                    corrected_at = now
                finished = ready_at is not None and answered is not None and "cacheAtAnswer" in res
                if changed_counts_from is not None:
                    finished = finished and corrected_at is not None
                if finished:
                    break
            time.sleep(COLD_POLL_S)
    except BaseException:
        for p, _ in procs:
            if p.poll() is None:
                p.kill()
        raise
    finally:
        for th in threads:
            th.join(timeout=30)
    res["answered_s"] = round(answered, 3)
    res["firstQueryExit"] = res["queryAttempts"][0]["exit"]
    res["firstQuery_s"] = res["queryAttempts"][0]["s"]
    res["ready_s"] = round(ready_at, 3)
    # A state other than notNeeded at the first status means the session started before we
    # could look: the figure would be the polling floor, so it is not recorded.
    res["sessionStarting_s"] = (None if res["sessionStartingAtFirstStatus"] or sess_seen is None
                                else round(sess_seen, 3))
    res["workerPidSeen_s"] = round(pid_seen, 3) if pid_seen is not None else None
    if corrected_at is not None:
        res["corrected_s"] = round(corrected_at, 3)
    res["csharpTimeline"] = timeline
    res["cacheTimeline"] = cache_timeline
    if inst.created:
        res["hostCreated_s"] = round(inst.created - wall0, 3)
    wpid = res.get("workerPid")
    worker_ct = inst.workers.get(wpid) if wpid else None
    if inst.created and worker_ct:
        # Host start to worker process, read from the OS: the headline enumeration figure.
        res["enumerationOs_s"] = round(worker_ct - inst.created, 3)
    if inst.has_cache:
        if expect_cached:
            check_cached_start(cache_states, res.get("cacheAtReady"))
        inst.seq_at_ready = (res.get("cacheAtReady") or {}).get("snapshotSequence")
        inst.seq_mark = inst.seq_at_ready
        # After an unchanged cached start the snapshot already holds the graph.
        inst.pending_write = not (expect_cached and changed_counts_from is None)
    return res


def ensure_snapshot_current(inst, expected_counts, timeout=600):
    """Before a stop that precedes a cold start, on a build with the cache: wait until the
    snapshot holds the current committed state (a newer sequence than the one seen at the
    last commit, verified, no write error, and no further write for SNAPSHOT_STABLE_S, so a
    write that was already in flight at the commit cannot pass), with the graph at the
    expected unchanged counts. Fatal on timeout. Returns what was observed, or None."""
    if not inst.has_cache:
        inst.last_counts = counts(inst.status())
        return None
    t0 = time.perf_counter()
    s = inst.status()
    if counts(s) != expected_counts:
        raise Fatal(f"graph is at {counts(s)}, expected the unchanged {expected_counts} before "
                    "the snapshot check")
    stable_seq = stable_since = None
    while inst.pending_write:
        if time.perf_counter() - t0 > timeout:
            raise Fatal(f"no snapshot of the current graph within {timeout}s "
                        f"(last cache {cache_fields(cache_of(s))}, mark {inst.seq_mark})")
        c = cache_of(s)
        if snapshot_progress(c, inst.seq_mark) == "done":
            seq = c.get("snapshotSequence")
            if seq != stable_seq:
                stable_seq, stable_since = seq, time.perf_counter()
            elif time.perf_counter() - stable_since >= SNAPSHOT_STABLE_S:
                inst.pending_write = False
                break
        time.sleep(0.25)
        s = inst.status()
        if counts(s) != expected_counts:
            raise Fatal(f"graph moved to {counts(s)} while waiting for the snapshot")
    c = cache_of(s)
    if not isinstance(c, dict) or c.get("lastWriteError"):
        raise Fatal(f"snapshot not usable before stop: {cache_fields(c)}")
    inst.snapshot_expected = True
    inst.snapshotted_counts = inst.last_counts = counts(s)
    return {"waited_s": round(time.perf_counter() - t0, 3), "cache": cache_fields(c)}


def sample_memory(inst, s=None):
    s = s or inst.status()
    out = {"host": memory_mb(inst.pid)}
    for x in s.get("parsers", {}).get("sessions", []) or []:
        out[x["parserId"]] = memory_mb(x.get("pid")) if x.get("pid") else None
    return out


def settled_memory(inst):
    """An immediate sample, then the same SETTLE_S as "5 s after ready", then another; with
    the number of commits this process has made, which is what the row is "after"."""
    immediate = sample_memory(inst)
    time.sleep(SETTLE_S)
    return {"immediate": immediate, "settled": sample_memory(inst), "commits": inst.commits}


def load_queries(path):
    qs = []
    for line in open(path, encoding="utf-8"):
        line = line.rstrip("\n")
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        kind, ident = line.split("\t", 1)
        qs.append((kind.strip(), ident.strip()))
    return qs


def query_params(kind, ident):
    p = {"identifier": ident, "depth": 3 if kind == "depth3" else 1}
    if kind == "tests":
        p["includeTests"] = "true"
    if kind not in ("symbol", "tests", "depth3", "file", "substring"):
        raise Fatal(f"unknown query kind {kind!r}")
    return "/api/context/complete?" + urlencode(p)


def run_queries(inst, queries):
    lat, misses, mismatched = [], [], []
    want = {"file": "filePath", "substring": "substring"}
    t0 = time.perf_counter()
    for kind, ident in queries:
        path = query_params(kind, ident)
        t = time.perf_counter()
        code, raw = inst.request("GET", path, timeout=120)
        lat.append(time.perf_counter() - t)  # body read; parsing is not part of the latency
        body = json.loads(raw) if raw else None
        if code != 200 or not body or not body.get("totalMatches"):
            misses.append(f"{kind}:{ident}")
        elif body.get("matchMode") != want.get(kind, "exact"):
            mismatched.append(f"{kind}:{ident}:{body.get('matchMode')}")
    total = time.perf_counter() - t0
    ms = sorted(x * 1000 for x in lat)
    return {"count": len(lat), "total_s": round(total, 3),
            "p50_ms": round(pctl(ms, 50), 2), "p95_ms": round(pctl(ms, 95), 2),
            "max_ms": round(ms[-1], 2), "misses": misses, "matchModeMismatches": mismatched}


def pctl(sorted_vals, p):
    if not sorted_vals:
        return None
    k = (len(sorted_vals) - 1) * p / 100
    lo = int(k)
    hi = min(lo + 1, len(sorted_vals) - 1)
    return sorted_vals[lo] + (sorted_vals[hi] - sorted_vals[lo]) * (k - lo)


SNAP_FILE = re.compile(r"\.(\d+)\.[0-9A-Za-z]+\.(ccsnap|state)$")


def snapshot_disk(base):
    """Sizes of the newest sequence's .ccsnap and .state files under the cache directory."""
    found = []
    for d, _, fs in os.walk(base):
        for f in fs:
            m = SNAP_FILE.search(f)
            if m:
                found.append((int(m.group(1)), m.group(2), os.path.getsize(os.path.join(d, f))))
    if not found:
        raise Fatal(f"no .ccsnap or .state file under {base}")
    newest = max(x[0] for x in found)
    disk = {"sequence": newest, "ccsnapBytes": 0, "stateBytes": 0, "files": 0}
    for seq, kind, size in found:
        if seq == newest:
            disk["ccsnapBytes" if kind == "ccsnap" else "stateBytes"] += size
            disk["files"] += 1
    if not disk["ccsnapBytes"]:
        raise Fatal(f"newest sequence {newest} under {base} has no .ccsnap file")
    return disk


def snapshot_figures(inst, repo, wait_s=300):
    """Cache numbers once a snapshot newer than the one seen at ready (or at the last commit)
    is written and verified, plus on-disk sizes of that sequence. Fatal on timeout."""
    mark = inst.seq_mark
    deadline = time.time() + wait_s
    while True:
        c = cache_of(inst.status())
        if snapshot_progress(c, mark) == "done":
            break
        if time.time() > deadline:
            raise Fatal(f"no verified snapshot newer than {mark} within {wait_s}s: {cache_fields(c)}")
        time.sleep(0.25)
    disk = snapshot_disk(cache_dir(repo))
    keep = ("state", "sizeBytes", "workerStateBytes", "lastWriteMs", "lastCaptureMs",
            "snapshotSequence", "lastWriteError")
    return {"cache": {k: c.get(k) for k in keep}, "disk": disk,
            "diskSequenceMatches": disk["sequence"] == c.get("snapshotSequence")}


def cache_dir(repo):
    return os.path.join(repo, ".codecontext", "cache")


def hibernation(inst, edits, key, expected_counts, timeout=900):
    """Start with --worker-idle-timeout 1, wait for the csharp session to hibernate, read
    memory, then write the body edit and time the wake. Needs a build with hibernation."""
    hits = inst.conflicting()
    if hits:
        raise Disturbed(f"instance registered on {[h.get('rootPath') for h in hits]}")
    wall0 = time.time()
    inst.spawned_after = wall0
    t0 = time.perf_counter()
    p = run_cli([inst.exe, "start", "--path", inst.repo, "--detach", "--idle-timeout", "0",
                 "--worker-idle-timeout", "1"], timeout=600)
    if p.returncode:
        raise Fatal(f"start --worker-idle-timeout failed: {p.stderr.strip()[-400:]}")
    out = p.stdout
    try:
        rec = json.loads(out[out.index("{"):out.rindex("}") + 1])
    except ValueError:
        raise Fatal(f"cannot parse `start --detach` output: {out.strip()[:300]!r}")
    # `start` returns an existing same-root or ancestor record with exit 0; only a process
    # created by this start, on exactly this root, may be timed and stopped.
    inst.attach(rec, wall0)
    res = {}
    while True:
        s = inst.status()
        if s["indexing"]["status"] == "ready":
            break
        if time.perf_counter() - t0 > timeout:
            raise Fatal("hibernation start did not become ready")
        time.sleep(0.1)
    res["ready_s"] = round(time.perf_counter() - t0, 3)
    inst.seq_at_ready = inst.seq_mark = (cache_of(s) or {}).get("snapshotSequence")
    inst.pending_write = False if inst.snapshot_expected else True
    time.sleep(SETTLE_S)
    cs = session(inst.status(), "csharp") or {}
    worker_pid = cs.get("pid")
    worker_ct = inst.workers.get(worker_pid)
    if not worker_pid or worker_ct is None:
        raise Fatal(f"no csharp worker process after ready: {cs}")
    res["workerBefore"] = memory_mb(worker_pid)
    t1 = time.perf_counter()
    while True:
        s = inst.status()
        cs = session(s, "csharp") or {}
        if cs.get("state") == "hibernated":
            break
        if time.perf_counter() - t1 > timeout:
            raise Fatal(f"csharp session did not hibernate within {timeout}s: {cs.get('state')}")
        time.sleep(0.5)
    res["hibernateAfter_s"] = round(time.perf_counter() - t1, 3)
    time.sleep(SETTLE_S)
    if process_alive(worker_pid, worker_ct):
        raise Fatal(f"session is hibernated but worker pid {worker_pid} is still running")
    if cs.get("pid"):
        raise Fatal(f"hibernated session still reports pid {cs.get('pid')}")
    res["workerProcessGone"] = True
    res["hostWhileHibernated"] = memory_mb(inst.pid)
    before = counts(s)
    t = time.perf_counter()
    edits.apply(key)
    ready_at = delta = None
    lo = 0.0
    while ready_at is None or delta is None:
        now = time.perf_counter() - t
        if now > timeout:  # one_run's finally restores the file
            raise Fatal(f"wake did not finish within {timeout}s (delta {delta}, "
                        f"session ready {ready_at})")
        sent = time.perf_counter() - t
        s = inst.status()
        if delta is None:
            if s["indexing"]["status"] == "ready" and counts(s) != before:
                hi = time.perf_counter() - t
                delta = {"s": round((lo + hi) / 2, 4), "bracket": [round(lo, 4), round(hi, 4)]}
                inst.commits += 1
            else:
                lo = sent
        if ready_at is None and (session(s, "csharp") or {}).get("state") == "ready":
            ready_at = time.perf_counter() - t
        time.sleep(EDIT_POLL_S)
    res["wakeToDelta_s"], res["wakeToDeltaBracket"] = delta["s"], delta["bracket"]
    res["wakeToSessionReady_s"] = round(ready_at, 3)
    after = counts(s)
    t = time.perf_counter()
    edits.revert(key)
    wait_counts_change(inst, after, t)
    res["snapshotBeforeStop"] = ensure_snapshot_current(inst, expected_counts)
    return res


def fresh_cache_segment(inst, ident, repo, expected_counts):
    """Cold start with the cache directory absent: the "after the first scan" snapshot
    figures. The caller guarantees the directory is one the harness may delete."""
    d = cache_dir(repo)
    if os.path.lexists(d):
        remove_cache_dir(d)
    inst.snapshot_expected = False
    c = cold_start(inst, ident, expect_cached=False)
    c.pop("readyStatus")
    inst.pending_write = True
    fig = snapshot_figures(inst, repo)
    fig["coldReady_s"] = c["ready_s"]
    fig["cacheAtReady"] = c.get("cacheAtReady")
    return fig


def remove_cache_dir(d):
    if os.path.islink(d) or (hasattr(os.path, "isjunction") and os.path.isjunction(d)):
        raise Fatal(f"{d} is a link; not deleting it")
    shutil.rmtree(d)


# ---------------------------------------------------------------- one run

def one_run(args, cfg, queries, edits, rows, caps):
    inst = Instance(args.exe, args.repo, has_cache=caps["cache"])
    inst.snapshot_expected = args.state.get("snapshot_expected", False)
    r = {"type": "run", "label": args.label, "repo": cfg["name"],
         "startedAt": datetime.datetime.now().isoformat(timespec="seconds"),
         "repoState": repo_state(args.repo), "capabilities": dict(caps)}
    e = cfg["edits"]
    try:
        # ---- cold start, unchanged
        log("cold start")
        c = cold_start(inst, cfg["coldSymbol"], expect_cached=inst.has_cache and inst.snapshot_expected)
        s = c.pop("readyStatus")
        r["cold"] = c
        r["files"] = s["indexing"].get("filesTotal")
        r["scanDurationReported"] = s["indexing"].get("scanDuration")
        r["counts"] = unchanged = counts(s)
        if not inst.has_cache:
            r["cachedColdStart"] = NA
        log(f"  answered {c['answered_s']}s ready {c['ready_s']}s host-to-worker (OS) "
            f"{c.get('enumerationOs_s')}s (attempts {c['queryAttempts']}, cache line "
            f"{c.get('cacheLine')})")
        immediate = sample_memory(inst) if "memory" in rows else None
        time.sleep(SETTLE_S)  # the same settle before every first-edit row and memory sample
        if "memory" in rows:
            r["memoryAfterReady"] = {"immediate": immediate, "settled": sample_memory(inst),
                                     "commits": inst.commits}
        if "first_edit" in rows and "leafBody" in e:
            r["firstEditLeaf"] = timed_edit(inst, edits, "leafBody")
            log(f"  first edit (leaf) apply {r['firstEditLeaf']['apply_s']}s")
        if "queries" in rows:
            r["queries"] = run_queries(inst, queries)
            log(f"  queries total {r['queries']['total_s']}s p50 {r['queries']['p50_ms']}ms "
                f"p95 {r['queries']['p95_ms']}ms misses {len(r['queries']['misses'])}")
            if "memory" in rows:
                r["memoryAfterQueries"] = settled_memory(inst)
        if "edits" in rows:
            for key, name in (("body", "bodyEdits"), ("top", "topEdits")):
                # One discarded priming round: the first edit of a kind in this process is
                # slower (first edit in that project) and is recorded apart.
                prime = timed_edit(inst, edits, key)
                vals = {"prime": {k: prime[k] for k in ("apply_s", "revert_s")},
                        "apply_s": [], "revert_s": [], "applyBracket": [], "revertBracket": [],
                        "delta": prime["delta"]}
                log(f"  {key} edit prime: apply {prime['apply_s']}s revert {prime['revert_s']}s")
                for i in range(args.rounds):
                    x = timed_edit(inst, edits, key)
                    for k in ("apply_s", "revert_s", "applyBracket", "revertBracket"):
                        vals[k].append(x[k])
                    vals["delta"] = x["delta"]
                    log(f"  {key} edit round {i}: apply {x['apply_s']}s revert {x['revert_s']}s "
                        f"delta {x['delta']}")
                r[name] = vals
                if "memory" in rows:
                    r["memoryAfterBodyEdits" if key == "body" else "memoryAfterAllEdits"] = \
                        settled_memory(inst)
        # Waits for the write after the quiet period, so the after-edit figure holds them all.
        r["snapshotBeforeStop"] = ensure_snapshot_current(inst, unchanged)
        if "edits" in rows and "snapshot" in rows:
            r["snapshotAfterEdit"] = snapshot_figures(inst, args.repo) if inst.has_cache else NA
        inst.stop()

        # ---- first edit in the root project after its own unchanged cold start
        if "first_edit_root" in rows:
            log("cold start for first root edit")
            c2 = cold_start(inst, cfg["coldSymbol"], expect_cached=inst.has_cache)
            c2.pop("readyStatus")
            time.sleep(SETTLE_S)
            x = timed_edit(inst, edits, "body")
            x["coldReady_s"] = c2["ready_s"]
            r["firstEditRoot"] = x
            log(f"  first edit (root) apply {x['apply_s']}s revert {x['revert_s']}s")
            ensure_snapshot_current(inst, unchanged)
            inst.stop()

        # ---- cold start with one file changed while stopped. The file stays edited from
        # just before the spawn until both the answer and the corrected graph are seen.
        if "cold_changed" in rows:
            for key, ident in (("body", e["body"]["method"]), ("top", e["top"]["symbol"])):
                log(f"cold start with {key} file changed")
                base = inst.snapshotted_counts if inst.has_cache else inst.last_counts
                edits.apply(key)
                try:
                    c3 = cold_start(inst, ident, changed_counts_from=base,
                                    expect_cached=inst.has_cache)
                finally:
                    t = time.perf_counter()
                    edits.revert(key)
                st = c3.pop("readyStatus")
                r[f"coldChanged_{key}"] = {
                    "answered_s": c3["answered_s"], "corrected_s": c3.get("corrected_s"),
                    "ready_s": c3["ready_s"], "cacheLine": c3.get("cacheLine"),
                    "cacheAtFirstStatus": c3.get("cacheAtFirstStatus"),
                    "cacheAtAnswer": c3.get("cacheAtAnswer"), "cacheAtReady": c3.get("cacheAtReady"),
                    "queryAttempts": c3["queryAttempts"], "counts": counts(st), "base": base}
                log(f"  answered {c3['answered_s']}s corrected {c3.get('corrected_s')}s")
                now = counts(inst.status())
                if now != base:
                    wait_counts_change(inst, now, t)
                ensure_snapshot_current(inst, unchanged)
                inst.stop()

        # ---- hibernation (needs --worker-idle-timeout)
        if "hibernation" in rows:
            if caps["hibernation"]:
                log("hibernation")
                r["hibernation"] = hibernation(inst, edits, "body", unchanged)
                inst.stop()
            else:
                r["hibernation"] = NA

        # ---- snapshot after the first scan: a dedicated start with no cache directory
        if "snapshot" in rows:
            if inst.has_cache:
                log("cold start with the cache directory absent")
                r["snapshotFirstScan"] = fresh_cache_segment(inst, cfg["coldSymbol"], args.repo,
                                                             unchanged)
                ensure_snapshot_current(inst, unchanged)
                inst.stop()
            else:
                r["snapshotFirstScan"] = NA
    finally:
        edits.restore_all()
        if inst.pid:
            inst.stop()
        else:
            rec = inst.find_own()  # started by our last spawn but never attached
            if rec:
                try:
                    inst.attach(rec, inst.spawned_after)
                    inst.stop()
                except Fatal as ex:
                    log(f"not stopping {rec.get('rootPath')} pid {rec.get('pid')}: {ex}")
        args.state["snapshot_expected"] = inst.snapshot_expected
    return r


# ---------------------------------------------------------------- summary

SKIP_KEYS = {"csharpTimeline", "cacheTimeline", "queryAttempts", "misses", "matchModeMismatches",
             "delta", "counts", "base", "workerPid", "queryTarget", "applyBracket",
             "revertBracket", "bracket", "wakeToDeltaBracket", "startedAt", "type", "label",
             "repo", "repoState", "porcelainKinds", "version"}


def flatten(d, prefix=""):
    """{dotted key: [values]} for numbers, booleans, nulls and strings."""
    out = {}
    if isinstance(d, dict):
        for k, v in d.items():
            if k in SKIP_KEYS:
                continue
            out.update(flatten(v, f"{prefix}{k}."))
    elif isinstance(d, list):
        if d and all(isinstance(x, (int, float)) and not isinstance(x, bool) for x in d):
            out[prefix[:-1]] = list(d)
    else:
        out[prefix[:-1]] = [d]
    return out


def summarize(runs, label, repo):
    pooled = {}
    for r in runs:
        for k, v in flatten(r).items():
            pooled.setdefault(k, []).extend(v)
    out = {"type": "summary", "label": label, "repo": repo, "runs": len(runs), "numbers": {},
           "values": {}}
    for k, v in sorted(pooled.items()):
        nums = [x for x in v if isinstance(x, (int, float)) and not isinstance(x, bool)]
        if nums and len(nums) == len(v):
            out["numbers"][k] = {"median": round(statistics.median(nums), 4), "min": min(nums),
                                 "max": max(nums), "n": len(nums)}
        else:
            # Booleans, nulls and strings (and mixed keys) are shown, never dropped.
            cnt = collections.Counter(json.dumps(x) for x in v)
            out["values"][k] = dict(sorted(cnt.items()))
    na = sorted({k for r in runs for k, v in r.items() if v == NA})
    out["notApplicable"] = na
    out["nearestComparable"] = {k: NEAREST_COMPARABLE.get(k, "cold.ready_s") for k in na}
    return out


def emit(obj, out_path, untimed):
    if untimed:
        obj = dict(obj, untimed=True)
    line = json.dumps(obj)
    print(line, flush=True)
    if out_path:
        with open(out_path, "a", encoding="utf-8") as f:
            f.write(line + "\n")


def busy_now(own_roots):
    procs = processes()
    roots = {os.getpid(): creation_time(os.getpid()) or 0.0}
    roots.update(OWN)
    roots.update(own_roots)
    own = own_tree(procs, roots)
    return sorted({n for _, n, _ in procs if BUSY.match(n)} | set(foreign_codecontext(procs, own)))


def wait_idle(minutes):
    deadline = time.time() + minutes * 60
    while True:
        b = busy_now({})
        if not b:
            return
        if time.time() > deadline:
            raise Fatal(f"machine not idle after {minutes} min: {b}", 4)
        log(f"waiting for idle machine: {b}")
        time.sleep(15)


def wait_no_instance(inst, minutes):
    deadline = time.time() + minutes * 60
    while True:
        hits = inst.conflicting()
        if not hits:
            return
        if time.time() > deadline:
            raise Fatal("an instance belonging to someone else is registered on "
                        f"{[h.get('rootPath') for h in hits]}; not touching it", 3)
        log(f"instance registered on {[h.get('rootPath') for h in hits]}; waiting")
        time.sleep(30)


class CacheDirGuard:
    """Makes the repository's cache directory one the harness may delete, and puts the
    starting state back at the end: a directory that existed before is moved aside (with
    --fresh-cache-ok only) to a sibling and moved back; one that did not exist is removed."""

    def __init__(self, repo, needed, fresh_ok, recover_dir=RECOVER_DIR):
        self.dir, self.aside, self.existed = cache_dir(repo), None, False
        self.manifest = recovery_paths(recover_dir, self.dir)[1].replace(".json", ".cache.json")
        self.active = needed
        if not needed:
            return
        if os.path.exists(self.manifest):
            raise Fatal(f"{self.manifest} says the cache directory of {repo} was moved aside "
                        "by a run that did not finish; move it back (see the manifest) first", 2)
        self.existed = os.path.lexists(self.dir)
        if self.existed:
            if not fresh_ok:
                raise Fatal(f"{self.dir} exists and the snapshot row needs a start without it. "
                            "Pass --fresh-cache-ok to let the harness move it aside for the "
                            "invocation and move it back afterwards.", 2)
            self.aside = self.dir + ".ccbench-aside"
            if os.path.lexists(self.aside):
                raise Fatal(f"{self.aside} already exists; resolve it first", 2)
            os.makedirs(os.path.dirname(self.manifest), exist_ok=True)
            with open(self.manifest, "w", encoding="utf-8") as f:
                json.dump({"cacheDir": self.dir, "movedTo": self.aside}, f)
            os.rename(self.dir, self.aside)
            log(f"moved {self.dir} aside to {self.aside}")

    def restore(self):
        if not self.active:
            return True
        try:
            if os.path.lexists(self.dir):
                remove_cache_dir(self.dir)
            if self.aside:
                os.rename(self.aside, self.dir)
                os.remove(self.manifest)
                log(f"moved {self.aside} back to {self.dir}")
            return True
        except (OSError, Fatal) as e:
            log(f"ERROR restoring the cache directory {self.dir}: {e}")
            return False


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--exe", required=True)
    ap.add_argument("--repo", required=True)
    ap.add_argument("--label", required=True)
    ap.add_argument("--config", required=True, help="per-repository JSON config")
    ap.add_argument("--runs", type=int, default=5)
    ap.add_argument("--warmup", type=int, default=1, help="discarded runs first")
    ap.add_argument("--rounds", type=int, default=3,
                    help="timed apply/revert rounds per edit kind, after one priming round")
    ap.add_argument("--rows", default=",".join(ALL_ROWS),
                    help="comma-separated subset of: " + ",".join(ALL_ROWS))
    ap.add_argument("--out", help="append JSON lines here as well")
    ap.add_argument("--idle-wait", type=float, default=60, help="minutes to wait for idle")
    ap.add_argument("--instance-wait", type=float, default=20,
                    help="minutes to wait for a foreign instance on the root to go away")
    ap.add_argument("--max-retries", type=int, default=3, help="repeats of a disturbed run")
    ap.add_argument("--cpu-threshold", type=float, default=10.0,
                    help="discard a run when CPU used outside this harness's processes "
                         "averages above this percentage of all logical processors")
    ap.add_argument("--allow-busy", action="store_true",
                    help="functional run: skip the idle and disturbance gates; every result "
                         "is marked \"untimed\": true and is not a measurement")
    ap.add_argument("--allow-dirty-target", action="append", default=[], metavar="FILE",
                    help="repository-relative edit target that may have uncommitted changes")
    ap.add_argument("--require-cache", action="store_true",
                    help="fail when the build has no cache or a cache row misses its state")
    ap.add_argument("--require-hibernation", action="store_true",
                    help="fail when the build has no worker hibernation")
    ap.add_argument("--fresh-cache-ok", action="store_true",
                    help="let the snapshot row move an existing .codecontext/cache aside")
    args = ap.parse_args(argv)
    if os.name != "nt":
        raise SystemExit("measure-cache.py uses the Win32 API; run it on Windows")
    args.exe = os.path.abspath(args.exe)
    args.repo = os.path.abspath(args.repo)
    args.state = {}
    rows = [x.strip() for x in args.rows.split(",") if x.strip()]
    bad = [x for x in rows if x not in ALL_ROWS]
    if bad:
        raise SystemExit(f"unknown rows {bad}")
    cfg_path = os.path.abspath(args.config)
    cfg = json.load(open(cfg_path, encoding="utf-8"))
    queries = load_queries(os.path.join(os.path.dirname(cfg_path), cfg["queries"]))
    edits = guard = None
    code = 0
    try:
        version = run_cli([args.exe, "--version"], timeout=60).stdout.strip()
        caps = capabilities_from_help(run_cli([args.exe, "start", "--help"], timeout=60).stdout)
        require_capabilities(caps, args.require_cache, args.require_hibernation)
        if args.require_hibernation and "hibernation" not in rows:
            raise Fatal("--require-hibernation needs the hibernation row")
        log(f"{args.label}: {version} on {args.repo}, {len(queries)} queries, rows {rows}, "
            f"capabilities {caps}" + (", UNTIMED (--allow-busy)" if args.allow_busy else ""))
        inst = Instance(args.exe, args.repo)
        wait_no_instance(inst, args.instance_wait)
        edits = Edits(args.repo, cfg["edits"], args.allow_dirty_target)
        guard = CacheDirGuard(args.repo, caps["cache"] and "snapshot" in rows, args.fresh_cache_ok)
        runs = []
        total = args.warmup + args.runs
        i = retries = 0
        while i < total:
            wait_no_instance(inst, args.instance_wait)
            if not args.allow_busy:
                wait_idle(args.idle_wait)
            reason = None
            with BusyWatch(lambda: {}) as bw:
                try:
                    r = one_run(args, cfg, queries, edits, rows, caps)
                except Disturbed as ex:
                    r = {"type": "run", "label": args.label, "repo": cfg["name"]}
                    reason = str(ex)
            edits.check()
            r["version"] = version
            r["warmup"] = i < args.warmup
            r["disturbance"] = bw.report()
            if reason is None and not args.allow_busy:
                d = r["disturbance"]
                if d["processes"]:
                    reason = f"processes {d['processes']}"
                elif (d["cpuOtherMeanPct"] or 0) > args.cpu_threshold:
                    reason = (f"CPU outside the harness averaged {d['cpuOtherMeanPct']}% "
                              f"(threshold {args.cpu_threshold}%)")
            if reason is not None:
                r["discarded"], r["discardReason"] = True, reason
                emit(r, args.out, args.allow_busy)
                retries += 1
                log(f"run disturbed: {reason}; discarding ({retries})")
                if retries > args.max_retries:
                    raise Fatal("too many disturbed runs", 4)
                continue
            emit(r, args.out, args.allow_busy)
            if not r["warmup"]:
                runs.append(r)
            i += 1
        emit(summarize(runs, args.label, cfg["name"]), args.out, args.allow_busy)
    except Fatal as ex:
        log(f"FATAL: {ex}")
        code = ex.code
    finally:
        if edits is not None:
            if not edits.restore_all():
                code = code or 2
            try:
                edits.check()
            except Fatal as ex:
                log(f"FATAL: {ex}")
                code = ex.code
        if guard is not None and not guard.restore():
            code = code or 2
    return code


if __name__ == "__main__":
    sys.exit(main())
