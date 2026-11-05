"""Measure CodeContext cold start, memory, query latency and edit latency on one repository.

The persistent-fact-cache plan (docs/PERSISTENT_FACT_CACHE_PLAN.md, section 6) defines every
number taken here. Phase 0 runs this against the v0.10.0 release asset and phase 11 runs it
unchanged against the branch build; rows that need the cache or worker hibernation check for
the feature at run time and record "not applicable" when the build lacks it.

Usage:
  python scripts/bench/measure-cache.py --exe <codecontext.exe> --repo <dir> --label <name>
      --config scripts/bench/<repo>.json [--runs 5] [--warmup 1] [--rows ...] [--out f.jsonl]

Output: progress lines on stderr; one JSON line per run ("type": "run") and one summary line
("type": "summary", median/min/max per number) on stdout and appended to --out.

Commit detection: watcher updates do not advance indexing.operationId, so a committed edit is
seen as a change in database.nodeCount/edgeCount with status "ready". Every timed edit
therefore adds or removes a fact: the body edit inserts one call (one CALLS edge), the
top-level edit adds one method (one node).

Safety: refuses to run while any registered instance is on the repository or an ancestor of
it (query would pick that instance); waits for that to clear up to --instance-wait minutes,
then exits 3. Checks `git status --porcelain -- <edited files>` before and after and fails
with exit code 2 on a difference. Edits are reverted, and the instance this script started is
stopped by --path, in finally blocks. Never stops anything else.

Standard library only; Windows only (process memory and listing use the Win32 API, the same
counters Get-Process reports as WorkingSet64 and PrivateMemorySize64).
"""
import argparse, ctypes, ctypes.wintypes as wt, datetime, http.client, json, os, re, statistics
import subprocess, sys, threading, time

NA = "not applicable in this build"
BUSY = re.compile(r"^(dotnet|testhost.*|vstest.*|msbuild|vbcscompiler|"
                  r"codecontext\.(core\.tests|fakeworker))(\.exe)?$", re.I)
ALL_ROWS = ["cold", "memory", "first_edit", "queries", "edits", "cold_changed", "first_edit_root",
            "snapshot", "hibernation"]


def log(msg):
    print(f"# {time.strftime('%H:%M:%S')} {msg}", file=sys.stderr, flush=True)


class Fatal(Exception):
    def __init__(self, msg, code=1):
        super().__init__(msg)
        self.code = code


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
    psapi.GetProcessMemoryInfo.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.DWORD]


def processes():
    """[(pid, exe name)] for every process on the machine."""
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    out = []
    try:
        e = PROCESSENTRY32W()
        e.dwSize = ctypes.sizeof(e)
        ok = k32.Process32FirstW(snap, ctypes.byref(e))
        while ok:
            out.append((e.th32ProcessID, e.szExeFile))
            ok = k32.Process32NextW(snap, ctypes.byref(e))
    finally:
        k32.CloseHandle(snap)
    return out


def busy_processes():
    return sorted({name for _, name in processes() if BUSY.match(name)})


def pid_alive(pid):
    return any(p == pid for p, _ in processes())


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


def creation_time(pid):
    """Process creation time as a Unix timestamp, or None."""
    h = k32.OpenProcess(0x1000, False, pid)
    if not h:
        return None
    try:
        t = [wt.FILETIME() for _ in range(4)]
        if not k32.GetProcessTimes(h, *[ctypes.byref(x) for x in t]):
            return None
        v = (t[0].dwHighDateTime << 32) | t[0].dwLowDateTime
        return v / 1e7 - 11644473600
    finally:
        k32.CloseHandle(h)


class BusyWatch:
    """Samples the process list every 2 s and remembers any build/test process seen."""

    def __init__(self):
        self.seen, self._stop = set(), threading.Event()
        self._t = threading.Thread(target=self._run, daemon=True)

    def _run(self):
        while not self._stop.wait(2.0):
            self.seen.update(busy_processes())

    def __enter__(self):
        self.seen.update(busy_processes())
        self._t.start()
        return self

    def __exit__(self, *a):
        self._stop.set()
        self._t.join()


# ---------------------------------------------------------------- CodeContext helpers

def norm(p):
    return os.path.normcase(os.path.abspath(p)).rstrip("\\/")


class Instance:
    def __init__(self, exe, repo):
        self.exe, self.repo = exe, repo
        self.port = self.pid = None
        self._conn = None

    def list(self):
        p = subprocess.run([self.exe, "list", "--json"], capture_output=True, text=True,
                           timeout=60)
        out = p.stdout
        try:
            return json.loads(out[out.index("["):out.rindex("]") + 1])
        except ValueError:
            return []

    def conflicting(self):
        """Registered instances on this root or an ancestor of it (query would use them)."""
        root = norm(self.repo)
        hits = []
        for r in self.list():
            rp = norm(r.get("rootPath", ""))
            if rp and (root == rp or root.startswith(rp + os.sep)):
                hits.append(r)
        return hits

    def find_own(self):
        root = norm(self.repo)
        for r in self.list():
            if norm(r.get("rootPath", "")) == root:
                return r
        return None

    def attach(self, rec):
        self.port, self.pid = rec["port"], rec["pid"]
        self._conn = None

    def get(self, path, timeout=30):
        """GET path, reusing one keep-alive connection; returns (status, parsed json)."""
        for attempt in (0, 1):
            if self._conn is None:
                self._conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=timeout)
            try:
                self._conn.request("GET", path)
                r = self._conn.getresponse()
                body = r.read()
                return r.status, json.loads(body) if body else None
            except (OSError, http.client.HTTPException):
                self._conn.close()
                self._conn = None
                if attempt:
                    raise

    def status(self):
        return self.get("/api/status", timeout=10)[1]

    def stop(self):
        """Stop the instance on this exact root by path and wait for its processes to exit."""
        pids = [self.pid] if self.pid else []
        try:
            for s in (self.status() or {}).get("parsers", {}).get("sessions", []):
                if s.get("pid"):
                    pids.append(s["pid"])
        except Exception:
            pass
        if self._conn:
            self._conn.close()
            self._conn = None
        subprocess.run([self.exe, "stop", "--path", self.repo], capture_output=True, text=True,
                       timeout=120)
        deadline = time.time() + 60
        while time.time() < deadline and any(pid_alive(p) for p in pids):
            time.sleep(0.2)
        left = [p for p in pids if pid_alive(p)]
        if left:
            log(f"warning: processes still alive after stop: {left}")
        self.port = self.pid = None


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


# ---------------------------------------------------------------- edits

def read_text(path):
    with open(path, encoding="utf-8", newline="") as f:
        return f.read()


def write_text(path, text):
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)


BODY_ANCHOR = r"\n(\s+)public [^\n]*\)\s*\r?\n\s+\{\r?\n"
TOP_ANCHOR = r"\n(\s*)public ((?:abstract |partial |sealed |static )*)class [^\n]*\r?\n\s*\{\r?\n"


def body_edit(target):
    # The first matching method body: insert a call after its opening brace, so the edit
    # adds exactly one CALLS edge and the commit is observable.
    def mutate(src):
        m = re.search(target.get("anchor", BODY_ANCHOR), src)
        if not m:
            raise Fatal(f"body anchor not found in {target['file']}")
        nl = "\r\n" if "\r\n" in src else "\n"
        return src[:m.end()] + m.group(1) + "    " + target["statement"] + nl + src[m.end():]
    return mutate


def top_edit(target):
    # Add a public method right after the class's opening brace (static in a static class).
    def mutate(src):
        m = re.search(target.get("anchor", TOP_ANCHOR), src)
        if not m:
            raise Fatal(f"top-level anchor not found in {target['file']}")
        nl = "\r\n" if "\r\n" in src else "\n"
        static = "static " if "static" in (m.group(2) if m.lastindex >= 2 else "") else ""
        decl = target.get("declaration", f"public {static}void ProbeMethodForMeasurement() {{ }}")
        return src[:m.end()] + m.group(1) + "    " + decl + nl + src[m.end():]
    return mutate


class Edits:
    """Owns the original text of every edit target and restores it on close.

    A file is only ever written when it holds exactly the text this script expects (the
    original before an apply, our edit before a revert); anything else means someone else
    changed it, and the run fails without touching it.
    """

    def __init__(self, repo, targets):
        self.repo, self.targets = repo, targets
        self.paths = {k: os.path.join(repo, t["file"]) for k, t in targets.items()}
        if len(set(map(norm, self.paths.values()))) != len(self.paths):
            raise Fatal("each edit target must be a different file")
        self.original, self.edited = {}, {}
        self.porcelain_before = self.porcelain()
        for k, p in self.paths.items():
            self.original[p] = read_text(p)
        for k, t in targets.items():
            p = self.paths[k]
            self.edited[k] = (top_edit if t["kind"] == "top" else body_edit)(t)(self.original[p])
            assert self.edited[k] != self.original[p]

    def porcelain(self):
        files = sorted({t["file"] for t in self.targets.values()})
        p = subprocess.run(["git", "-C", self.repo, "status", "--porcelain", "--"] + files,
                           capture_output=True, text=True, timeout=120)
        if p.returncode:
            raise Fatal(f"git status failed in {self.repo}: {p.stderr.strip()}")
        return p.stdout

    def apply(self, key):
        p = self.paths[key]
        if read_text(p) != self.original[p]:
            raise Fatal(f"{p} changed outside this script; not editing it", 2)
        write_text(p, self.edited[key])

    def revert(self, key):
        p = self.paths[key]
        if read_text(p) != self.edited[key]:
            raise Fatal(f"{p} changed outside this script while edited; not reverting it", 2)
        write_text(p, self.original[p])

    def restore_all(self):
        for k, p in self.paths.items():
            try:
                now = read_text(p)
                if now == self.edited[k]:
                    write_text(p, self.original[p])
                    log(f"restored {p}")
                elif now != self.original[p]:
                    log(f"ERROR: {p} holds neither the original nor this script's edit; "
                        "left as is")
            except OSError as e:
                log(f"ERROR restoring {p}: {e}")

    def check(self):
        after = self.porcelain()
        if after != self.porcelain_before:
            raise Fatal("git status of edited files changed during the run:\n"
                        f"before:\n{self.porcelain_before}after:\n{after}", 2)


# ---------------------------------------------------------------- measurement steps

def wait_counts_change(inst, before, timeout=900, interval=0.05):
    t0 = time.perf_counter()
    while time.perf_counter() - t0 < timeout:
        try:
            s = inst.status()
            if s["indexing"]["status"] == "ready" and counts(s) != before:
                return time.perf_counter() - t0, s
        except Exception:
            pass
        time.sleep(interval)
    raise Fatal(f"graph counts did not change from {before} within {timeout}s")


def timed_edit(inst, edits, key):
    before = counts(inst.status())
    t = time.perf_counter()
    edits.apply(key)
    apply_s, s = wait_counts_change(inst, before)
    apply_s = time.perf_counter() - t
    after = counts(s)
    t = time.perf_counter()
    edits.revert(key)
    _, s = wait_counts_change(inst, after)
    revert_s = time.perf_counter() - t
    if counts(s) != before:
        raise Fatal(f"revert did not restore counts: {before} -> {counts(s)}")
    return round(apply_s, 3), round(revert_s, 3), after[0] - before[0], after[1] - before[1]


def spawn_query(exe, repo, ident):
    return subprocess.Popen([exe, "query", ident, "--path", repo], stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, text=True, encoding="utf-8",
                            errors="replace")


def cold_start(inst, ident, changed_counts_from=None, timeout=1800):
    """Spawn `query <ident>` with no instance running and time everything from that instant.

    Returns a dict with: query exit code and time (re-spawning on a readiness timeout, exit
    3, until one exits 0), whether stdout began with a `cache` line, time to status ready,
    time until the csharp session appears and until it has a pid, worker creation time from
    the OS relative to the host's, and a compact timeline of the csharp session.
    If changed_counts_from is given, also the time until status is ready with counts that
    differ from those (the corrected answer after an offline edit).
    """
    t0 = time.perf_counter()
    wall0 = time.time()
    q = spawn_query(inst.exe, inst.repo, ident)
    res = {"queryAttempts": []}
    outs = {}

    def reader(proc, idx):
        outs[idx] = proc.communicate()

    threads = [threading.Thread(target=reader, args=(q, 0), daemon=True)]
    threads[0].start()
    attempt_start = t0
    rec = None
    sess_seen = pid_seen = ready_at = corrected_at = None
    timeline, last = [], None
    first_status = None
    answered = None
    while time.perf_counter() - t0 < timeout:
        now = time.perf_counter() - t0
        # Query process bookkeeping: on exit 3 (readiness timeout) spawn another, as an
        # agent would; stop at the first exit 0 or any other code.
        if answered is None and q.poll() is not None:
            threads[-1].join()
            out, err = outs[len(threads) - 1]
            res["queryAttempts"].append({"exit": q.returncode,
                                         "s": round(time.perf_counter() - attempt_start, 3)})
            if q.returncode == 0:
                answered = time.perf_counter() - t0
                first = out.splitlines()[0] if out else ""
                res["cacheLine"] = first.startswith("cache\t")
                res["cacheLineText"] = first if res["cacheLine"] else None
                res["queryStdoutLines"] = len(out.splitlines())
            elif q.returncode == 3 and len(threads) < 20:
                attempt_start = time.perf_counter()
                q = spawn_query(inst.exe, inst.repo, ident)
                threads.append(threading.Thread(target=reader, args=(q, len(threads)),
                                                daemon=True))
                threads[-1].start()
            else:
                raise Fatal(f"query exited {q.returncode}: {err.strip()[-400:]}")
        if rec is None:
            rec = inst.find_own()
            if rec:
                inst.attach(rec)
                res["registered_s"] = round(now, 3)
            else:
                time.sleep(0.1)
                continue
        try:
            s = inst.status()
        except Exception:
            s = None
        if s:
            if first_status is None:
                first_status = round(now, 3)
                res["firstStatus_s"] = first_status
                res["cacheAtFirstStatus"] = cache_of(s)
            cs = session(s, "csharp")
            key = (cs.get("state"), cs.get("pid"), cs.get("message")) if cs else None
            if key != last:
                timeline.append([round(now, 3)] + (list(key) if key else [None]))
                last = key
            # On 0.10.0 the csharp session is listed from the first status as "notNeeded"
            # with no pid; it turns "starting" when enumeration has routed files to it.
            if cs and sess_seen is None and cs.get("state") not in (None, "notNeeded"):
                sess_seen = now
            if cs and cs.get("pid") and pid_seen is None:
                pid_seen = now
                res["workerPid"] = cs["pid"]
            ix = s["indexing"]
            if ready_at is None and ix.get("status") == "ready":
                ready_at = now
                res["readyStatus"] = s
            if (changed_counts_from is not None and corrected_at is None
                    and ix.get("status") == "ready" and counts(s) != changed_counts_from):
                corrected_at = now
            done = ready_at is not None and answered is not None
            if changed_counts_from is not None:
                done = done and corrected_at is not None
            if done:
                break
        time.sleep(0.05)
    else:
        raise Fatal(f"cold start did not finish within {timeout}s")
    for t in threads:
        t.join(timeout=30)
    res["answered_s"] = round(answered, 3)
    res["firstQueryExit"] = res["queryAttempts"][0]["exit"]
    res["firstQuery_s"] = res["queryAttempts"][0]["s"]
    res["ready_s"] = round(ready_at, 3)
    res["sessionStarting_s"] = round(sess_seen, 3) if sess_seen is not None else None
    res["workerPidSeen_s"] = round(pid_seen, 3) if pid_seen is not None else None
    if corrected_at is not None:
        res["corrected_s"] = round(corrected_at, 3)
    res["csharpTimeline"] = timeline
    host_ct = creation_time(inst.pid) if inst.pid else None
    if host_ct:
        res["hostCreated_s"] = round(host_ct - wall0, 3)
    wpid = res.get("workerPid")
    worker_ct = creation_time(wpid) if wpid else None
    if host_ct and worker_ct:
        # Host file enumeration, read from the OS: host process creation to worker creation.
        res["enumerationOs_s"] = round(worker_ct - host_ct, 3)
    return res


def sample_memory(inst, s=None):
    s = s or inst.status()
    out = {"host": memory_mb(inst.pid)}
    for x in s.get("parsers", {}).get("sessions", []) or []:
        out[x["parserId"]] = memory_mb(x.get("pid")) if x.get("pid") else None
    return out


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
    from urllib.parse import urlencode
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
        code, body = inst.get(path, timeout=120)
        lat.append(time.perf_counter() - t)
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


def snapshot_figures(inst, repo, wait_s=180, after_sequence=None):
    """Cache numbers from status, once indexing.cache.lastWriteMs is set (and, when
    after_sequence is given, snapshotSequence has moved past it), plus on-disk sizes."""
    deadline = time.time() + wait_s
    c = None
    while time.time() < deadline:
        c = cache_of(inst.status())
        if c and c.get("lastWriteMs") is not None and (
                after_sequence is None or (c.get("snapshotSequence") or 0) > after_sequence):
            break
        time.sleep(0.25)
    else:
        return {"timedOut": True, "cache": c}
    disk = {"ccsnapBytes": 0, "stateBytes": 0, "files": 0}
    base = os.path.join(repo, ".codecontext", "cache")
    for d, _, fs in os.walk(base):
        for f in fs:
            size = os.path.getsize(os.path.join(d, f))
            if f.endswith(".ccsnap"):
                disk["ccsnapBytes"] += size
                disk["files"] += 1
            elif f.endswith(".state"):
                disk["stateBytes"] += size
                disk["files"] += 1
    keep = ("state", "sizeBytes", "workerStateBytes", "lastWriteMs", "lastCaptureMs",
            "snapshotSequence", "lastWriteError")
    return {"cache": {k: c.get(k) for k in keep}, "disk": disk}


def supports_hibernation(exe):
    p = subprocess.run([exe, "start", "--help"], capture_output=True, text=True, timeout=60)
    return "--worker-idle-timeout" in (p.stdout + p.stderr)


def hibernation(inst, edits, key, timeout=900):
    """Start with --worker-idle-timeout 1, wait for the csharp session to hibernate, read
    memory, then write the body edit and time the wake. Needs a build with hibernation."""
    t0 = time.perf_counter()
    p = subprocess.run([inst.exe, "start", "--path", inst.repo, "--detach", "--idle-timeout", "0",
                        "--worker-idle-timeout", "1"], capture_output=True, text=True,
                       timeout=600)
    if p.returncode:
        raise Fatal(f"start --worker-idle-timeout failed: {p.stderr.strip()[-400:]}")
    out = p.stdout
    inst.attach(json.loads(out[out.index("{"):out.rindex("}") + 1]))
    res = {}
    while True:
        s = inst.status()
        if s["indexing"]["status"] == "ready":
            break
        if time.perf_counter() - t0 > timeout:
            raise Fatal("hibernation start did not become ready")
        time.sleep(0.1)
    res["ready_s"] = round(time.perf_counter() - t0, 3)
    worker_pid = (session(s, "csharp") or {}).get("pid")
    res["workerBefore"] = memory_mb(worker_pid)
    t1 = time.perf_counter()
    while True:
        s = inst.status()
        cs = session(s, "csharp") or {}
        if cs.get("state") == "hibernated":
            break
        if time.perf_counter() - t1 > timeout:
            raise Fatal("csharp session did not hibernate")
        time.sleep(0.5)
    res["hibernateAfter_s"] = round(time.perf_counter() - t1, 3)
    time.sleep(5)
    res["workerProcessGone"] = not (worker_pid and pid_alive(worker_pid))
    res["hostWhileHibernated"] = memory_mb(inst.pid)
    before = counts(s)
    t = time.perf_counter()
    edits.apply(key)
    ready_at = delta_at = None
    while time.perf_counter() - t < timeout and (ready_at is None or delta_at is None):
        s = inst.status()
        now = time.perf_counter() - t
        if delta_at is None and s["indexing"]["status"] == "ready" and counts(s) != before:
            delta_at = now
        if ready_at is None and (session(s, "csharp") or {}).get("state") == "ready":
            ready_at = now
        time.sleep(0.05)
    res["wakeToDelta_s"] = round(delta_at, 3) if delta_at is not None else None
    res["wakeToSessionReady_s"] = round(ready_at, 3) if ready_at is not None else None
    after = counts(s)
    edits.revert(key)
    wait_counts_change(inst, after)
    return res


# ---------------------------------------------------------------- one run

def one_run(args, cfg, queries, edits, rows, baseline_counts):
    inst = Instance(args.exe, args.repo)
    r = {"type": "run", "label": args.label, "repo": cfg["name"],
         "startedAt": datetime.datetime.now().isoformat(timespec="seconds")}
    e = cfg["edits"]
    try:
        # ---- cold start, unchanged
        log("cold start")
        c = cold_start(inst, cfg["coldSymbol"])
        s = c.pop("readyStatus")
        r["cold"] = c
        r["files"] = s["indexing"].get("filesTotal")
        r["scanDurationReported"] = s["indexing"].get("scanDuration")
        r["counts"] = counts(s)
        r["capabilities"] = {"cacheStatus": cache_of(s) is not None or
                             c.get("cacheAtFirstStatus") is not None}
        baseline_counts.setdefault("counts", r["counts"])
        log(f"  answered {c['answered_s']}s ready {c['ready_s']}s worker pid seen "
            f"{c['workerPidSeen_s']}s (attempts {c['queryAttempts']})")
        if "memory" in rows:
            time.sleep(5)
            r["memoryAfterReady"] = sample_memory(inst)
        if "snapshot" in rows:
            if r["capabilities"]["cacheStatus"]:
                r["snapshotAfterScan"] = snapshot_figures(inst, args.repo)
            else:
                r["snapshotAfterScan"] = NA
        if "first_edit" in rows and "leafBody" in e:
            a, b, dn, de = timed_edit(inst, edits, "leafBody")
            r["firstEditLeaf"] = {"apply_s": a, "revert_s": b, "delta": [dn, de]}
            log(f"  first edit (leaf) apply {a}s revert {b}s")
        if "queries" in rows:
            r["queries"] = run_queries(inst, queries)
            log(f"  queries total {r['queries']['total_s']}s p50 {r['queries']['p50_ms']}ms "
                f"p95 {r['queries']['p95_ms']}ms misses {len(r['queries']['misses'])}")
            if "memory" in rows:
                r["memoryAfterQueries"] = sample_memory(inst)
        if "edits" in rows:
            seq = None
            if r["capabilities"]["cacheStatus"]:
                seq = (cache_of(inst.status()) or {}).get("snapshotSequence")
            for key, name in (("body", "bodyEdits"), ("top", "topEdits")):
                vals = {"apply_s": [], "revert_s": [], "delta": None}
                for i in range(args.rounds):
                    a, b, dn, de = timed_edit(inst, edits, key)
                    vals["apply_s"].append(a)
                    vals["revert_s"].append(b)
                    vals["delta"] = [dn, de]
                    log(f"  {key} edit round {i}: apply {a}s revert {b}s delta {dn},{de}")
                r[name] = vals
                if key == "body" and "memory" in rows:
                    r["memoryAfterBodyEdits"] = sample_memory(inst)
            if "memory" in rows:
                r["memoryAfterEdits"] = sample_memory(inst)
            if "snapshot" in rows:
                r["snapshotAfterEdit"] = (snapshot_figures(inst, args.repo, after_sequence=seq)
                                          if r["capabilities"]["cacheStatus"] else NA)
        inst.stop()

        # ---- first edit in the root project after its own unchanged cold start
        if "first_edit_root" in rows:
            log("cold start for first root edit")
            c2 = cold_start(inst, cfg["coldSymbol"])
            c2.pop("readyStatus")
            a, b, dn, de = timed_edit(inst, edits, "body")
            r["firstEditRoot"] = {"apply_s": a, "revert_s": b, "delta": [dn, de],
                                  "coldReady_s": c2["ready_s"]}
            log(f"  first edit (root) apply {a}s revert {b}s")
            inst.stop()

        # ---- cold start with one file changed while stopped
        if "cold_changed" in rows:
            for key, ident in (("body", e["body"]["method"]), ("top", e["top"]["symbol"])):
                log(f"cold start with {key} file changed")
                edits.apply(key)
                try:
                    c3 = cold_start(inst, ident, changed_counts_from=baseline_counts["counts"])
                    st = c3.pop("readyStatus")
                    r[f"coldChanged_{key}"] = {
                        "answered_s": c3["answered_s"], "corrected_s": c3.get("corrected_s"),
                        "ready_s": c3["ready_s"], "cacheLine": c3.get("cacheLine"),
                        "queryAttempts": c3["queryAttempts"], "counts": counts(st)}
                    log(f"  answered {c3['answered_s']}s corrected {c3.get('corrected_s')}s")
                    before = counts(inst.status())
                finally:
                    edits.revert(key)
                if before != baseline_counts["counts"]:
                    wait_counts_change(inst, before)
                inst.stop()

        # ---- hibernation (needs --worker-idle-timeout)
        if "hibernation" in rows:
            if supports_hibernation(args.exe):
                log("hibernation")
                r["hibernation"] = hibernation(inst, edits, "body")
                inst.stop()
            else:
                r["hibernation"] = NA
    finally:
        edits.restore_all()
        if inst.port is not None or inst.find_own():
            inst.stop()
    return r


# ---------------------------------------------------------------- summary

def flatten(d, prefix=""):
    out = {}
    if isinstance(d, dict):
        for k, v in d.items():
            if k in ("csharpTimeline", "queryAttempts", "misses", "matchModeMismatches",
                     "cacheAtFirstStatus", "delta", "counts", "workerPid", "firstQueryExit",
                     "queryStdoutLines"):
                continue
            out.update(flatten(v, f"{prefix}{k}."))
    elif isinstance(d, list) and d and all(isinstance(x, (int, float)) for x in d):
        out[prefix[:-1]] = list(d)
    elif isinstance(d, (int, float)) and not isinstance(d, bool):
        out[prefix[:-1]] = [d]
    return out


def summarize(runs, label, repo):
    pooled = {}
    for r in runs:
        for k, v in flatten(r).items():
            pooled.setdefault(k, []).extend(v)
    out = {"type": "summary", "label": label, "repo": repo, "runs": len(runs), "numbers": {}}
    for k, v in sorted(pooled.items()):
        out["numbers"][k] = {"median": round(statistics.median(v), 3), "min": min(v),
                             "max": max(v), "n": len(v)}
    na = sorted({k for r in runs for k, v in r.items() if v == NA})
    out["notApplicable"] = na
    out["nearestComparable"] = {k: "cold.ready_s" for k in na}
    return out


def emit(obj, out_path):
    line = json.dumps(obj)
    print(line, flush=True)
    if out_path:
        with open(out_path, "a", encoding="utf-8") as f:
            f.write(line + "\n")


def wait_idle(minutes):
    deadline = time.time() + minutes * 60
    while True:
        b = busy_processes()
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


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--exe", required=True)
    ap.add_argument("--repo", required=True)
    ap.add_argument("--label", required=True)
    ap.add_argument("--config", required=True, help="per-repository JSON config")
    ap.add_argument("--runs", type=int, default=5)
    ap.add_argument("--warmup", type=int, default=1, help="discarded runs first")
    ap.add_argument("--rounds", type=int, default=3, help="apply/revert rounds per edit kind")
    ap.add_argument("--rows", default=",".join(ALL_ROWS),
                    help="comma-separated subset of: " + ",".join(ALL_ROWS))
    ap.add_argument("--out", help="append JSON lines here as well")
    ap.add_argument("--idle-wait", type=float, default=60, help="minutes to wait for idle")
    ap.add_argument("--instance-wait", type=float, default=20,
                    help="minutes to wait for a foreign instance on the root to go away")
    ap.add_argument("--max-retries", type=int, default=3, help="repeats of a disturbed run")
    ap.add_argument("--allow-busy", action="store_true",
                    help="functional dry run: ignore build/test processes (numbers invalid)")
    args = ap.parse_args()
    if os.name != "nt":
        raise SystemExit("measure-cache.py uses the Win32 API; run it on Windows")
    args.exe = os.path.abspath(args.exe)
    args.repo = os.path.abspath(args.repo)
    rows = [x.strip() for x in args.rows.split(",") if x.strip()]
    bad = [x for x in rows if x not in ALL_ROWS]
    if bad:
        raise SystemExit(f"unknown rows {bad}")
    cfg_path = os.path.abspath(args.config)
    cfg = json.load(open(cfg_path, encoding="utf-8"))
    queries = load_queries(os.path.join(os.path.dirname(cfg_path), cfg["queries"]))
    version = subprocess.run([args.exe, "--version"], capture_output=True, text=True).stdout.strip()
    log(f"{args.label}: {version} on {args.repo}, {len(queries)} queries, rows {rows}")
    inst = Instance(args.exe, args.repo)
    edits = Edits(args.repo, cfg["edits"])
    baseline = {}
    runs = []
    code = 0
    try:
        total = args.warmup + args.runs
        i = retries = 0
        while i < total:
            wait_no_instance(inst, args.instance_wait)
            if not args.allow_busy:
                wait_idle(args.idle_wait)
            with BusyWatch() as bw:
                r = one_run(args, cfg, queries, edits, rows, baseline)
            edits.check()
            r["version"] = version
            r["warmup"] = i < args.warmup
            r["busyProcessesSeen"] = sorted(bw.seen)
            if bw.seen and not args.allow_busy:
                retries += 1
                log(f"run disturbed by {sorted(bw.seen)}; discarding ({retries})")
                if retries > args.max_retries:
                    raise Fatal("too many disturbed runs", 4)
                continue
            emit(r, args.out)
            if not r["warmup"]:
                runs.append(r)
            i += 1
        emit(summarize(runs, args.label, cfg["name"]), args.out)
    except Fatal as ex:
        log(f"FATAL: {ex}")
        code = ex.code
    finally:
        edits.restore_all()
        try:
            edits.check()
        except Fatal as ex:
            log(f"FATAL: {ex}")
            code = ex.code
    return code


if __name__ == "__main__":
    sys.exit(main())
