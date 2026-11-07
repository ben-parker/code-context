"""Tests of measure-cache.py's pure logic; no CodeContext instance is started.

Run: python -m unittest discover -s scripts/bench -p "test_*.py"
"""
import importlib.util, json, os, shutil, subprocess, tempfile, unittest
from unittest import mock

_spec = importlib.util.spec_from_file_location(
    "measure_cache", os.path.join(os.path.dirname(os.path.abspath(__file__)), "measure-cache.py"))
mc = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mc)

SRC = ("using System;\n\nnamespace N\n{\n    public class Widget\n    {\n"
       "        public int Size()\n        {\n            return 1;\n        }\n    }\n}\n")
BODY = {"kind": "body", "file": "Widget.cs", "statement": "_ = Size();", "method": "Widget.Size"}
TOP = {"kind": "top", "file": "Other.cs", "symbol": "Widget"}


def clean(repo, files):
    return ""


class Tmp(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="mc-test-")
        self.repo = os.path.join(self.dir, "repo")
        self.recover = os.path.join(self.dir, "recover")
        os.makedirs(self.repo)

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    def put(self, name, data):
        p = os.path.join(self.repo, name)
        with open(p, "wb") as f:
            f.write(data)
        return p

    def edits(self, targets, porcelain=clean, allow=()):
        return mc.Edits(self.repo, targets, allow, recover_dir=self.recover, porcelain=porcelain)


class EditBytes(Tmp):
    def roundtrip(self, data):
        p = self.put("Widget.cs", data)
        e = self.edits({"body": BODY})
        e.apply("body")
        edited = mc.read_bytes(p)
        self.assertIn(b"_ = Size();", edited)
        self.assertEqual(len(os.listdir(self.recover)), 2)  # .orig and manifest
        e.revert("body")
        self.assertEqual(mc.read_bytes(p), data)
        self.assertTrue(e.restore_all())
        self.assertEqual(os.listdir(self.recover), [])
        return edited

    def test_lf(self):
        edited = self.roundtrip(SRC.encode())
        self.assertNotIn(b"\r\n", edited)

    def test_crlf(self):
        edited = self.roundtrip(SRC.replace("\n", "\r\n").encode())
        self.assertEqual(edited.count(b"\n"), edited.count(b"\r\n"))

    def test_bom(self):
        edited = self.roundtrip(b"\xef\xbb\xbf" + SRC.encode())
        self.assertTrue(edited.startswith(b"\xef\xbb\xbf"))

    def test_atomic_write_leaves_no_temp(self):
        p = self.put("a.cs", b"old")
        mc.atomic_write(p, b"new \r\n bytes\xef\xbb\xbf")
        self.assertEqual(mc.read_bytes(p), b"new \r\n bytes\xef\xbb\xbf")
        self.assertEqual(os.listdir(self.repo), ["a.cs"])

    def test_leftover_temp_file_is_removed(self):
        self.put("Widget.cs", SRC.encode())
        tmp = self.put(".Widget.cs" + mc.TMP_SUFFIX, b"partial")
        self.edits({"body": BODY})
        self.assertFalse(os.path.exists(tmp))


class Recovery(Tmp):
    def test_existing_recovery_file_refuses_with_hint(self):
        p = self.put("Widget.cs", SRC.encode())
        orig, _ = mc.recovery_paths(self.recover, p)
        os.makedirs(self.recover)
        with open(orig, "wb") as f:
            f.write(SRC.encode())
        with self.assertRaises(mc.Fatal) as cm:
            self.edits({"body": BODY})
        self.assertEqual(cm.exception.code, 2)
        self.assertIn("Copy-Item", str(cm.exception))

    def test_partial_write_is_restored_from_recovery(self):
        p = self.put("Widget.cs", SRC.encode())
        e = self.edits({"body": BODY})
        e.apply("body")
        with open(p, "wb") as f:
            f.write(SRC.encode()[:20])  # a write cut short
        self.assertTrue(e.restore_all())
        self.assertEqual(mc.read_bytes(p), SRC.encode())
        self.assertEqual(os.listdir(self.recover), [])

    def test_empty_file_is_restored_from_recovery(self):
        p = self.put("Widget.cs", SRC.encode())
        e = self.edits({"body": BODY})
        e.apply("body")
        open(p, "wb").close()
        self.assertTrue(e.restore_all())
        self.assertEqual(mc.read_bytes(p), SRC.encode())

    def test_foreign_content_is_left_alone_and_reported(self):
        p = self.put("Widget.cs", SRC.encode())
        e = self.edits({"body": BODY})
        e.apply("body")
        self.put("Widget.cs", b"someone else's text")
        self.assertFalse(e.restore_all())
        self.assertEqual(mc.read_bytes(p), b"someone else's text")
        self.assertEqual(len(os.listdir(self.recover)), 2)  # kept for the person

    def test_edit_left_by_a_kill_is_restored(self):
        p = self.put("Widget.cs", SRC.encode())
        e = self.edits({"body": BODY})
        e.apply("body")
        self.assertTrue(e.restore_all())
        self.assertEqual(mc.read_bytes(p), SRC.encode())

    def test_apply_refuses_a_file_changed_by_someone_else(self):
        self.put("Widget.cs", SRC.encode())
        e = self.edits({"body": BODY})
        self.put("Widget.cs", SRC.encode() + b"//")
        with self.assertRaises(mc.Fatal):
            e.apply("body")


class Refusals(Tmp):
    def test_dirty_target_refused(self):
        self.put("Widget.cs", SRC.encode())
        with self.assertRaises(mc.Fatal) as cm:
            self.edits({"body": BODY}, porcelain=lambda r, f: " M Widget.cs\n")
        self.assertIn("--allow-dirty-target", str(cm.exception))

    def test_dirty_target_allowed_by_name(self):
        self.put("Widget.cs", SRC.encode())
        self.edits({"body": BODY}, porcelain=lambda r, f: " M Widget.cs\n", allow=["Widget.cs"])

    def test_leftover_statement_refused(self):
        self.put("Widget.cs", SRC.replace("return 1;", "_ = Size();\n            return 1;").encode())
        with self.assertRaises(mc.Fatal) as cm:
            self.edits({"body": BODY})
        self.assertIn("already contains", str(cm.exception))

    def test_leftover_declaration_refused(self):
        self.put("Other.cs", SRC.replace("public int Size()",
                                         "public void ProbeMethodForMeasurement() { }\n"
                                         "        public int Size()").encode())
        with self.assertRaises(mc.Fatal):
            self.edits({"top": TOP})

    def test_non_utf8_refused(self):
        self.put("Widget.cs", b"\xff\xfe" + SRC.encode("utf-16-le"))
        with self.assertRaises(mc.Fatal):
            self.edits({"body": BODY})


class Anchors(unittest.TestCase):
    def test_body_anchor_must_land_in_configured_method(self):
        with self.assertRaises(mc.Fatal):
            mc.body_edit(dict(BODY, method="Widget.Other"))(SRC)
        with self.assertRaises(mc.Fatal):
            mc.body_edit(dict(BODY, method="Gadget.Size"))(SRC)
        self.assertIn("_ = Size();", mc.body_edit(BODY)(SRC))

    def test_nested_one_line_class_is_not_the_enclosing_type(self):
        src = SRC.replace("    public class Widget\n    {\n",
                          "    public class Widget\n    {\n"
                          "        private sealed class Dot { public int X; }\n"
                          "        // class Fake { in a comment\n"
                          "        private string s = \"class Str {\";\n")
        self.assertIn("_ = Size();", mc.body_edit(BODY)(src))

    def test_generic_return_type_method_name(self):
        src = SRC.replace("public int Size()", "public Task<List<int>> Size<T>(int a)")
        self.assertIn("_ = Size();", mc.body_edit(BODY)(src))

    def test_top_anchor_must_land_in_configured_class(self):
        with self.assertRaises(mc.Fatal):
            mc.top_edit(dict(TOP, symbol="Gadget"))(SRC)
        self.assertIn("ProbeMethodForMeasurement", mc.top_edit(TOP)(SRC))


class Registry(unittest.TestCase):
    root = mc.norm(r"C:\work\repo")

    def test_list_parse_failure_is_fatal(self):
        for bad in ("", "not json", '{"a": 1}', "[1, 2]"):
            with self.assertRaises(mc.Fatal):
                mc.parse_list(bad)
        self.assertEqual(mc.parse_list("[]"), [])
        self.assertEqual(mc.parse_list('note\n[{"rootPath": "x"}]'), [{"rootPath": "x"}])

    def test_list_nonzero_exit_is_fatal(self):
        saved = mc.run_cli
        mc.run_cli = lambda *a, **k: subprocess.CompletedProcess(a, 1, "[]", "registry locked")
        try:
            with self.assertRaises(mc.Fatal):
                mc.Instance("cc.exe", r"C:\work\repo").list()
        finally:
            mc.run_cli = saved

    def test_identity(self):
        own = {"rootPath": r"C:\work\repo", "pid": 10, "instanceId": "a"}
        reused_pid = {"rootPath": r"C:\work\repo", "pid": 10, "instanceId": "b"}
        other_pid = {"rootPath": r"C:\work\repo", "pid": 11, "instanceId": "a"}
        ancestor = {"rootPath": r"C:\work", "pid": 10, "instanceId": "a"}
        self.assertIs(mc.find_record([ancestor, reused_pid, own], self.root, 10, "a"), own)
        self.assertIsNone(mc.find_record([ancestor, reused_pid, other_pid], self.root, 10, "a"))

    def test_conflicting_matches_same_and_ancestor_only(self):
        recs = [{"rootPath": r"C:\work"}, {"rootPath": r"C:\work\repo"},
                {"rootPath": r"C:\work\repo2"}, {"rootPath": r"C:\work\repo\sub"},
                {"rootPath": ""}]
        self.assertEqual([r["rootPath"] for r in mc.conflicting(recs, self.root)],
                         [r"C:\work", r"C:\work\repo"])

    def test_instance_created_before_spawn_is_not_ours(self):
        with self.assertRaises(mc.Fatal) as cm:
            mc.check_created_after(100.0, 101.0, "host pid 1")
        self.assertIn("not ours", str(cm.exception))
        with self.assertRaises(mc.Fatal):
            mc.check_created_after(None, 101.0, "host pid 1")
        mc.check_created_after(101.5, 101.0, "host pid 1")

    def test_attach_refuses_ancestor_record(self):
        inst = mc.Instance("cc.exe", r"C:\work\repo")
        with self.assertRaises(mc.Fatal):
            inst.attach({"rootPath": r"C:\work", "pid": 1, "port": 1, "instanceId": "a"}, 0)

    def test_own_tree(self):
        procs = [(1, "python.exe", 0), (2, "codecontext.exe", 1), (3, "codecontext.exe", 2),
                 (4, "CodeContext.CSharp.Worker.exe", 3), (5, "node.exe", 3),
                 (6, "codecontext.exe", 99), (7, "codecontext.exe", 77)]
        ct = {1: 10.0, 2: 11.0, 3: 12.0, 4: 13.0, 5: 13.0, 6: 50.0, 7: 5.0}.get
        # 99 is an own pid that exited (its child 6 came later, so it is ours); 77 is an own
        # pid recorded at 20.0 whose "child" 7 predates it (a reused parent pid).
        own = mc.own_tree(procs, {1: 10.0, 99: 40.0, 77: 20.0}, ctime=ct)
        self.assertEqual(own, {1, 2, 3, 4, 5, 6})
        # A live root whose creation does not match is someone else's process.
        self.assertEqual(mc.own_tree(procs, {2: 99.0}, ctime=ct), set())
        self.assertEqual(sorted(mc.foreign_codecontext(procs, own)), ["codecontext.exe:7"])


class StatusLogic(unittest.TestCase):
    def cache(self, state, csharp=None, seq=None, err=None, serving=None):
        c = {"state": state, "servingCached": serving, "snapshotSequence": seq,
             "lastWriteError": err}
        if csharp:
            c["verification"] = {"csharp": csharp}
        return c

    def test_capabilities_from_help(self):
        self.assertEqual(mc.capabilities_from_help("--path <p>  --idle-timeout"),
                         {"cache": False, "hibernation": False})
        caps = mc.capabilities_from_help("--no-cache  --worker-idle-timeout <m>")
        self.assertEqual(caps, {"cache": True, "hibernation": True})
        none = {"cache": False, "hibernation": False}
        mc.require_capabilities(none, False, False)
        with self.assertRaises(mc.Fatal):
            mc.require_capabilities(none, True, False)
        with self.assertRaises(mc.Fatal):
            mc.require_capabilities(none, False, True)
        mc.require_capabilities(caps, True, True)

    def test_cached_start_without_cache_field_is_fatal(self):
        with self.assertRaises(mc.Fatal):
            mc.check_cached_start({None}, None)

    def test_cached_start_ignored_is_fatal(self):
        with self.assertRaises(mc.Fatal):
            mc.check_cached_start({None, "ignored"}, self.cache("ignored", serving=False))

    def test_cached_start_verifying_then_verified(self):
        mc.check_cached_start({None, "verifying", "verified"}, self.cache("verified", "baseline"))
        mc.check_cached_start({"verified"}, self.cache("verified", "replaced"))

    def test_cached_start_without_verification_outcome_is_fatal(self):
        with self.assertRaises(mc.Fatal):
            mc.check_cached_start({"verifying", "verified"}, self.cache("verified"))
        with self.assertRaises(mc.Fatal):
            mc.check_cached_start({"verifying"}, self.cache("verifying", "pending"))

    def test_snapshot_progress(self):
        self.assertEqual(mc.snapshot_progress(None, None), "wait")
        self.assertEqual(mc.snapshot_progress(self.cache("verified", seq=4), 4), "wait")
        self.assertEqual(mc.snapshot_progress(self.cache("verifying", seq=5), 4), "wait")
        self.assertEqual(mc.snapshot_progress(self.cache("verified", seq=5), 4), "done")
        self.assertEqual(mc.snapshot_progress(self.cache("verified", seq=1), None), "done")
        self.assertEqual(mc.snapshot_progress(self.cache("verified"), None), "wait")
        with self.assertRaises(mc.Fatal):
            mc.snapshot_progress(self.cache("verified", seq=5, err="disk full"), 4)

    def test_session_states(self):
        self.assertFalse(mc.session_starting(None))
        self.assertFalse(mc.session_starting("notNeeded"))
        self.assertFalse(mc.session_starting("hibernated"))
        self.assertTrue(mc.session_starting("starting"))
        self.assertTrue(mc.session_starting("ready"))

    def test_wait_counts_change_bracket(self):
        seq = [{"indexing": {"status": "ready"}, "database": {"nodeCount": 1, "edgeCount": 1}}] * 3 \
            + [{"indexing": {"status": "scanning"}, "database": {"nodeCount": 1, "edgeCount": 2}},
               {"indexing": {"status": "ready"}, "database": {"nodeCount": 1, "edgeCount": 2},
                "indexing_": None}]

        class Fake:
            commits, pending_write, seq_mark = 0, False, None

            def __init__(self):
                self.it = iter(seq)

            def status(self):
                return next(self.it)
        f = Fake()
        r = mc.wait_counts_change(f, [1, 1], mc.time.perf_counter(), interval=0.001)
        lo, hi = r["bracket"]
        self.assertLessEqual(lo, r["s"])
        self.assertLessEqual(r["s"], hi)
        self.assertEqual(mc.counts(r["status"]), [1, 2])
        self.assertEqual((f.commits, f.pending_write), (1, True))


class QueryOutput(unittest.TestCase):
    def test_target_with_cache_line(self):
        out = ("cache\tverifying\t2026-11-04T10:12:03Z\tanswers come from the saved index\n"
               "target\tcsharp:N.ContextService\tClass\tsrc/C.cs:15\nusedBy\t3/3\n")
        r = mc.parse_query_output(out, "ContextService")
        self.assertEqual((r["cacheLine"], r["cacheState"]), (True, "verifying"))

    def test_method_target_with_parameters(self):
        out = "coverage\tx\ntarget\tcsharp:N.ContextService.Get(System.String)\tMethod\n"
        self.assertFalse(mc.parse_query_output(out, "ContextService.Get")["cacheLine"])

    def test_missing_ambiguous_and_wrong_target_are_fatal(self):
        for out in ("missing\tFoo\n", "cache\tverifying\nmissing\tFoo\n",
                    "matches\t2/2\texact\ntarget\tcsharp:A.Foo\tClass\ntarget\tcsharp:B.Foo\tClass\n",
                    "target\tcsharp:N.FooBar\tClass\n", "usedBy\t1/1\n"):
            with self.assertRaises(mc.Fatal):
                mc.parse_query_output(out, "Foo")


class Summary(unittest.TestCase):
    def test_booleans_nulls_and_strings_are_shown(self):
        runs = [{"cold": {"answered_s": 1.0, "cacheLine": False, "sessionStarting_s": None},
                 "hibernation": mc.NA, "warmup": False},
                {"cold": {"answered_s": 3.0, "cacheLine": True, "sessionStarting_s": 0.5},
                 "hibernation": mc.NA, "warmup": False}]
        s = mc.summarize(runs, "x", "r")
        self.assertEqual(s["numbers"]["cold.answered_s"]["median"], 2.0)
        self.assertEqual(s["values"]["cold.cacheLine"], {"false": 1, "true": 1})
        self.assertEqual(s["values"]["cold.sessionStarting_s"], {"0.5": 1, "null": 1})
        self.assertEqual(s["values"]["warmup"], {"false": 2})
        self.assertEqual(s["notApplicable"], ["hibernation"])
        self.assertIn("bodyEdits.apply_s", s["nearestComparable"]["hibernation"])

    def test_nearest_comparable_matches_the_document(self):
        self.assertEqual(mc.NEAREST_COMPARABLE["cachedColdStart"], "cold.ready_s")
        self.assertIsNone(mc.NEAREST_COMPARABLE["snapshotFirstScan"])


class Disk(Tmp):
    def test_newest_sequence_only(self):
        d = os.path.join(self.repo, ".codecontext", "cache", "default")
        os.makedirs(d)
        for name, size in (("graph.0000000041.aa.ccsnap", 100), ("worker-csharp.0000000041.aa.state", 50),
                           ("graph.0000000042.bb.ccsnap", 10), ("worker-csharp.0000000042.bb.state", 5),
                           ("graph.0000000043.cc.tmp", 7), ("writer.lock", 0)):
            with open(os.path.join(d, name), "wb") as f:
                f.write(b"x" * size)
        disk = mc.snapshot_disk(mc.cache_dir(self.repo))
        self.assertEqual(disk, {"sequence": 42, "ccsnapBytes": 10, "stateBytes": 5, "files": 2})

    def test_no_files_is_fatal(self):
        os.makedirs(mc.cache_dir(self.repo))
        with self.assertRaises(mc.Fatal):
            mc.snapshot_disk(mc.cache_dir(self.repo))

    def test_existing_cache_dir_needs_fresh_cache_ok(self):
        d = mc.cache_dir(self.repo)
        os.makedirs(d)
        with open(os.path.join(d, "keep"), "w") as f:
            f.write("theirs")
        with self.assertRaises(mc.Fatal):
            mc.CacheDirGuard(self.repo, True, False, recover_dir=self.recover)
        g = mc.CacheDirGuard(self.repo, True, True, recover_dir=self.recover)
        self.assertFalse(os.path.exists(d))
        os.makedirs(d)  # what the harness's own runs create
        self.assertTrue(g.restore())
        self.assertEqual(os.listdir(d), ["keep"])
        self.assertFalse(os.path.exists(g.manifest))

    def test_inactive_guard_touches_nothing(self):
        d = mc.cache_dir(self.repo)
        os.makedirs(d)
        g = mc.CacheDirGuard(self.repo, False, False, recover_dir=self.recover)
        self.assertTrue(g.restore())
        self.assertTrue(os.path.exists(d))


class TempFiles(Tmp):
    def test_failed_replace_removes_the_temp_file(self):
        p = self.put("a.cs", b"old")

        def locked(src, dst):
            raise PermissionError("locked")
        with mock.patch.object(mc.os, "replace", locked), mock.patch.object(mc.time, "sleep"):
            with self.assertRaises(PermissionError):
                mc.atomic_write(p, b"new")
        self.assertEqual(os.listdir(self.repo), ["a.cs"])
        self.assertEqual(mc.read_bytes(p), b"old")

    def test_failed_flush_removes_the_temp_file(self):
        p = self.put("a.cs", b"old")

        def full(fd):
            raise OSError("disk full")
        with mock.patch.object(mc.os, "fsync", full):
            with self.assertRaises(OSError):
                mc.atomic_write(p, b"new")
        self.assertEqual(os.listdir(self.repo), ["a.cs"])

    def test_other_replace_error_removes_the_temp_file(self):
        p = self.put("a.cs", b"old")

        def gone(src, dst):
            raise FileNotFoundError("gone")
        with mock.patch.object(mc.os, "replace", gone):
            with self.assertRaises(FileNotFoundError):
                mc.atomic_write(p, b"new")
        self.assertEqual(os.listdir(self.repo), ["a.cs"])

    def test_restore_all_removes_and_reports_a_leftover(self):
        self.put("Widget.cs", SRC.encode())
        e = self.edits({"body": BODY})
        tmp = self.put(".Widget.cs" + mc.TMP_SUFFIX, b"partial")
        with mock.patch.object(mc, "log") as log:
            self.assertTrue(e.restore_all())
        self.assertFalse(os.path.exists(tmp))
        self.assertTrue(any(tmp in c.args[0] for c in log.call_args_list))

    def test_restore_all_fails_when_a_leftover_cannot_be_removed(self):
        self.put("Widget.cs", SRC.encode())
        e = self.edits({"body": BODY})
        self.put(".Widget.cs" + mc.TMP_SUFFIX, b"partial")

        def refuse(path):
            raise PermissionError("in use")
        with mock.patch.object(mc.os, "remove", refuse):
            self.assertFalse(e.restore_all())


class Identity(unittest.TestCase):
    live = {7: 100.0}.get

    def test_spawned_from(self):
        spawns = {7: 100.0}
        self.assertTrue(mc.spawned_from(7, 101.0, spawns, ctime=self.live))
        # The parent exited: its pid reads as gone, which is the normal case for `query`.
        self.assertTrue(mc.spawned_from(7, 101.0, spawns, ctime=lambda p: None))

    def test_foreign_parent_is_not_ours(self):
        self.assertFalse(mc.spawned_from(8, 101.0, {7: 100.0}, ctime=self.live))
        self.assertFalse(mc.spawned_from(None, 101.0, {7: 100.0}, ctime=self.live))

    def test_child_older_than_the_spawn_is_not_ours(self):
        self.assertFalse(mc.spawned_from(7, 99.0, {7: 100.0}, ctime=self.live))

    def test_reused_parent_pid_is_not_ours(self):
        self.assertFalse(mc.spawned_from(7, 101.0, {7: 100.0}, ctime={7: 100.5}.get))

    def test_unknown_creation_proves_nothing(self):
        self.assertFalse(mc.spawned_from(7, 101.0, {7: None}, ctime=self.live))
        self.assertFalse(mc.spawned_from(7, None, {7: 100.0}, ctime=self.live))

    def test_instance_on_our_root_started_by_someone_else_is_not_adopted(self):
        inst = mc.Instance("cc.exe", r"C:\work\repo")
        inst.spawned_after, inst.spawns = 100.0, {7: 100.0}
        rec = {"rootPath": r"C:\work\repo", "pid": 20, "port": 1, "instanceId": "a"}
        procs = [(20, "codecontext.exe", 99), (7, "codecontext.exe", 1)]
        ctimes = {20: 100.1, 7: 100.0}.get
        with mock.patch.object(mc, "processes", lambda: procs), \
                mock.patch.object(mc, "creation_time", ctimes):
            self.assertIsNone(inst.find_own([rec]))
            with self.assertRaises(mc.Fatal) as cm:
                inst.attach(rec, 100.0)
            self.assertIn("not ours", str(cm.exception))
            self.assertIsNone(inst.pid)
            # The same record with our spawn as its parent is ours.
            procs[0] = (20, "codecontext.exe", 7)
            self.assertIs(inst.find_own([rec]), rec)
            inst.attach(rec, 100.0)
            self.assertEqual(inst.pid, 20)


class Stop(unittest.TestCase):
    """stop() against a fake registry and port; the process exits when shutdown arrives."""

    class Host(mc.Instance):
        def __init__(self, records, exits=True):
            super().__init__("cc.exe", r"C:\work\repo")
            self.records, self.exits = records, exits
            self.running, self.posts = True, []
            self.pid, self.port, self.instance_id, self.created = 10, 5000, "abc", 1.0

        def list(self):
            if self.records is None:
                raise mc.Fatal("`list --json` exited 1")
            return self.records

        def status(self):
            return {}

        def request(self, method, path, timeout=30):
            self.posts.append(path)
            if self.exits:
                self.running = False
            return 202, b""

    def stop(self, host):
        with mock.patch.object(mc, "process_alive", lambda p, c: host.running), \
                mock.patch.object(mc.time, "sleep"), mock.patch.object(mc, "log") as log, \
                mock.patch.object(mc, "run_cli") as cli:
            host.stop()
        return [c.args[0] for c in log.call_args_list], cli

    def own(self):
        return {"rootPath": r"C:\work\repo", "pid": 10, "instanceId": "abc"}

    def test_normal_stop(self):
        h = self.Host([self.own()])
        logs, _ = self.stop(h)
        self.assertEqual(h.posts, ["/api/shutdown?instanceId=abc"])
        self.assertIsNone(h.pid)
        self.assertFalse(any("ERROR" in x or "warning" in x for x in logs))

    def test_failed_list_does_not_leak_the_instance(self):
        h = self.Host(None)
        logs, _ = self.stop(h)
        self.assertEqual(h.posts, ["/api/shutdown?instanceId=abc"])
        self.assertEqual(sum("`list --json` failed" in x for x in logs), 5)  # retried

    def test_missing_record_does_not_leak_the_instance(self):
        h = self.Host([])
        self.stop(h)
        self.assertEqual(h.posts, ["/api/shutdown?instanceId=abc"])

    def test_dead_process_gets_no_shutdown(self):
        h = self.Host([self.own()])
        h.running = False
        self.stop(h)
        self.assertEqual(h.posts, [])
        self.assertIsNone(h.pid)

    def test_giving_up_names_pid_port_and_instance(self):
        h = self.Host(None, exits=False)
        real = mc.time.time
        ticks = iter(range(0, 100000, 40))
        with mock.patch.object(mc.time, "time", lambda: real() + next(ticks)):
            logs, cli = self.stop(h)
        cli.assert_not_called()  # `stop --path` needs our record in the registry
        last = [x for x in logs if "ERROR" in x][-1]
        for part in ("pid 10", "port 5000", "instanceId abc"):
            self.assertIn(part, last)

    def test_stop_path_only_with_our_record(self):
        h = self.Host([self.own()], exits=False)
        real = mc.time.time
        ticks = iter(range(0, 100000, 40))
        with mock.patch.object(mc.time, "time", lambda: real() + next(ticks)):
            _, cli = self.stop(h)
        cli.assert_called_once()
        self.assertIn("--path", cli.call_args.args[0])
        self.assertNotIn("--all", cli.call_args.args[0])

    def test_list_retry_recovers(self):
        h = self.Host(None)
        answers = iter([mc.Fatal("x"), mc.Fatal("y"), []])

        def flaky():
            a = next(answers)
            if isinstance(a, Exception):
                raise a
            return a
        h.list = flaky
        with mock.patch.object(mc.time, "sleep"), mock.patch.object(mc, "log"):
            self.assertEqual(h.list_retry(), [])


class DeadInstance(unittest.TestCase):
    class Fake:
        pid, commits, pending_write, seq_mark = 10, 0, False, None

        def __init__(self, alive):
            self.is_alive, self.calls = alive, 0

        def status(self):
            self.calls += 1
            raise ConnectionRefusedError("refused")

        def alive(self):
            return self.is_alive

    def test_death_fails_at_once(self):
        f = self.Fake(False)
        with self.assertRaises(mc.Fatal) as cm:
            mc.wait_counts_change(f, [1, 1], mc.time.perf_counter(), timeout=60, interval=0.001)
        self.assertEqual(f.calls, 1)
        self.assertIn("died", str(cm.exception))

    def test_a_status_hiccup_of_a_live_instance_keeps_waiting(self):
        f = self.Fake(True)
        with self.assertRaises(mc.Fatal) as cm:
            mc.wait_counts_change(f, [1, 1], mc.time.perf_counter(), timeout=0.02, interval=0.001)
        self.assertGreater(f.calls, 1)
        self.assertIn("did not change", str(cm.exception))


class CacheAssertions(unittest.TestCase):
    def test_ignored_is_fatal_even_when_verified_later(self):
        ready = {"state": "verified", "verification": {"csharp": "replaced"}}
        with self.assertRaises(mc.Fatal) as cm:
            mc.check_cached_start({None, "ignored", "verified"}, ready)
        self.assertIn("ignored", str(cm.exception))

    # Cache objects as recorded by real runs (results11, code-context, branch build).
    UNCACHED_READY = {"state": "verified", "servingCached": False, "verification": None,
                      "snapshotSequence": None, "lastWriteError": None, "reason": None}
    CACHED_READY = {"state": "verified", "servingCached": False,
                    "verification": {"csharp": "baseline", "typescript": "replaced"},
                    "snapshotSequence": 1, "lastWriteError": None, "reason": None}

    def test_uncached_start_passes_on_real_uncached_status(self):
        mc.check_uncached_start({None, "verified"}, self.UNCACHED_READY, False)
        mc.check_uncached_start(set(), None, False)  # --no-cache: no cache object at all

    def test_uncached_start_rejects_cached_start_that_read_null_at_first_status(self):
        # A cached start whose first status showed no cache (seen in the raw data), then
        # went straight to verified with a verification outcome.
        with self.assertRaises(mc.Fatal):
            mc.check_uncached_start({None, "verified"}, self.CACHED_READY, False)
        # The same start seen verifying, and with the cache line on its answer.
        with self.assertRaises(mc.Fatal):
            mc.check_uncached_start({None, "verifying", "verified"}, self.CACHED_READY, True)
        with self.assertRaises(mc.Fatal):
            mc.check_uncached_start({"verified"}, self.UNCACHED_READY, True)
        with self.assertRaises(mc.Fatal):
            mc.check_uncached_start({None, "ignored", "verified"}, self.UNCACHED_READY, False)

    def test_warmup_of_zero_is_refused(self):
        with mock.patch("sys.stderr"), self.assertRaises(SystemExit) as cm:
            mc.main(["--exe", "x", "--repo", "y", "--label", "z", "--config", "c", "--warmup", "0"])
        self.assertEqual(cm.exception.code, 2)


class CacheGuard(Tmp):
    def test_failed_rename_leaves_no_manifest(self):
        d = mc.cache_dir(self.repo)
        os.makedirs(d)

        def refuse(a, b):
            raise PermissionError("in use")
        with mock.patch.object(mc.os, "rename", refuse), self.assertRaises(mc.Fatal):
            mc.CacheDirGuard(self.repo, True, True, recover_dir=self.recover)
        self.assertEqual(os.listdir(self.recover), [])
        self.assertTrue(os.path.isdir(d))
        # So the next run is not refused for a move that never happened.
        g = mc.CacheDirGuard(self.repo, True, True, recover_dir=self.recover)
        self.assertTrue(os.path.exists(g.manifest))
        self.assertTrue(g.restore())

    def test_kept_manifest_during_the_move_refuses_a_second_run(self):
        os.makedirs(mc.cache_dir(self.repo))
        mc.CacheDirGuard(self.repo, True, True, recover_dir=self.recover)
        with self.assertRaises(mc.Fatal):
            mc.CacheDirGuard(self.repo, True, True, recover_dir=self.recover)

    def test_directories_the_runs_created_are_removed(self):
        g = mc.CacheDirGuard(self.repo, True, False, recover_dir=self.recover)
        os.makedirs(os.path.join(mc.cache_dir(self.repo), "default"))
        self.assertTrue(g.restore())
        self.assertEqual(os.listdir(self.repo), [])

    def test_an_existing_codecontext_directory_stays(self):
        parent = os.path.dirname(mc.cache_dir(self.repo))
        os.makedirs(parent)
        g = mc.CacheDirGuard(self.repo, True, False, recover_dir=self.recover)
        os.makedirs(mc.cache_dir(self.repo))
        self.assertTrue(g.restore())
        self.assertEqual(os.listdir(parent), [])

    def test_something_else_in_a_new_codecontext_directory_is_reported(self):
        g = mc.CacheDirGuard(self.repo, True, False, recover_dir=self.recover)
        d = mc.cache_dir(self.repo)
        os.makedirs(d)
        keep = os.path.join(os.path.dirname(d), "theirs")
        with open(keep, "w") as f:
            f.write("x")
        with mock.patch.object(mc, "log"):
            self.assertFalse(g.restore())
        self.assertTrue(os.path.exists(keep))


if __name__ == "__main__":
    unittest.main()
