using System.Diagnostics;
using CodeContext.Core.Instances;
using Xunit;

namespace CodeContext.Core.Tests.Instances
{
    public class InstanceRegistryTests : IDisposable
    {
        private readonly string _baseDir;

        public InstanceRegistryTests()
        {
            _baseDir = Path.Combine(Path.GetTempPath(), "cc-registry-tests", Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_baseDir, recursive: true); } catch { }
        }

        private InstanceRegistry CreateRegistry(Func<InstanceRecord, bool>? isInstanceAlive = null)
            => new InstanceRegistry(_baseDir, isInstanceAlive ?? (_ => true));

        private static InstanceRecord Record(string rootPath, int port = 7890, int pid = 1234)
            => new InstanceRecord
            {
                RootPath = rootPath,
                Port = port,
                Pid = pid,
                Backend = "inmemory",
                StartedAt = DateTimeOffset.UtcNow,
            };

        [Fact]
        public void GetAll_WhenNoFileExists_ReturnsEmpty()
        {
            var registry = CreateRegistry();

            Assert.Empty(registry.GetAll());
        }

        [Fact]
        public void Register_ThenGetAll_ReturnsRecord()
        {
            var registry = CreateRegistry();
            var root = Path.Combine(_baseDir, "repoA");

            registry.Register(Record(root, port: 7891));

            var all = registry.GetAll();
            var record = Assert.Single(all);
            Assert.Equal(Path.GetFullPath(root), record.RootPath);
            Assert.Equal(7891, record.Port);
        }

        [Fact]
        public void Register_SameRootTwice_ReplacesExistingEntry()
        {
            var registry = CreateRegistry();
            var root = Path.Combine(_baseDir, "repoA");

            registry.Register(Record(root, port: 7891, pid: 1));
            registry.Register(Record(root, port: 7892, pid: 2));

            var record = Assert.Single(registry.GetAll());
            Assert.Equal(7892, record.Port);
            Assert.Equal(2, record.Pid);
        }

        [Fact]
        public void Unregister_RemovesRecord()
        {
            var registry = CreateRegistry();
            var root = Path.Combine(_baseDir, "repoA");
            registry.Register(Record(root));

            registry.Unregister(root);

            Assert.Empty(registry.GetAll());
        }

        [Fact]
        public void Unregister_WithOlderInstanceId_DoesNotRemoveReplacement()
        {
            var registry = CreateRegistry();
            var root = Path.Combine(_baseDir, "repoA");
            var oldRecord = Record(root, pid: 1);
            oldRecord.InstanceId = "old";
            var replacement = Record(root, pid: 2);
            replacement.InstanceId = "replacement";

            registry.Register(oldRecord);
            registry.Register(replacement);
            registry.Unregister(root, "old");

            var remaining = Assert.Single(registry.GetAll());
            Assert.Equal("replacement", remaining.InstanceId);
        }

        [Fact]
        public void Register_FilesystemRoot_PreservesRootPath()
        {
            var registry = CreateRegistry();
            var root = Path.GetPathRoot(Path.GetFullPath(_baseDir))!;

            registry.Register(Record(root));

            Assert.Equal(root, Assert.Single(registry.GetAll()).RootPath);
            Assert.NotNull(registry.FindForPath(Path.Combine(root, "nested")));
        }

        [Fact]
        public void GetAll_PrunesDeadProcesses()
        {
            var alivePid = Environment.ProcessId;
            var deadPid = 999_999;
            var registry = CreateRegistry(r => r.Pid == alivePid);
            registry.Register(Record(Path.Combine(_baseDir, "alive"), port: 7891, pid: alivePid));
            registry.Register(Record(Path.Combine(_baseDir, "dead"), port: 7892, pid: deadPid));

            var all = registry.GetAll();

            var record = Assert.Single(all);
            Assert.Equal(alivePid, record.Pid);
        }

        [Fact]
        public void Register_RoundTripsInstanceIdentity()
        {
            var registry = CreateRegistry();
            var root = Path.Combine(_baseDir, "repoA");
            var startTime = DateTimeOffset.UtcNow.AddMinutes(-1);
            var record = Record(root);
            record.InstanceId = "abc123";
            record.ProcessStartTime = startTime;

            registry.Register(record);

            var loaded = Assert.Single(CreateRegistry().GetAll());
            Assert.Equal("abc123", loaded.InstanceId);
            Assert.NotNull(loaded.ProcessStartTime);
            Assert.Equal(startTime, loaded.ProcessStartTime!.Value, TimeSpan.FromSeconds(1));
        }

        [Fact]
        public void GetAll_UsesRecordIdentityForPruning_NotJustPid()
        {
            // Two records with the same PID but different identity: only the one the
            // validator accepts survives, proving the record (not the bare PID) is checked.
            var registry = CreateRegistry(r => r.InstanceId == "current");
            var stale = Record(Path.Combine(_baseDir, "stale"), port: 7891, pid: 4242);
            stale.InstanceId = "stale";
            var current = Record(Path.Combine(_baseDir, "current"), port: 7892, pid: 4242);
            current.InstanceId = "current";
            registry.Register(stale);
            registry.Register(current);

            var record = Assert.Single(registry.GetAll());
            Assert.Equal("current", record.InstanceId);
        }

        [Fact]
        public async Task Register_ConcurrentFromMultipleRegistries_LosesNoRecords()
        {
            // Simulates several processes registering at once: the cross-process lock
            // around load-modify-save must not lose any of the updates.
            const int count = 12;
            var tasks = Enumerable.Range(0, count).Select(i => Task.Run(() =>
            {
                var registry = CreateRegistry();
                registry.Register(Record(Path.Combine(_baseDir, $"repo{i}"), port: 7900 + i, pid: 1000 + i));
            }));

            await Task.WhenAll(tasks);

            Assert.Equal(count, CreateRegistry().GetAll().Count);
        }

        [Fact]
        public void FindForPath_ExactRoot_ReturnsRecord()
        {
            var registry = CreateRegistry();
            var root = Path.Combine(_baseDir, "repoA");
            registry.Register(Record(root));

            var found = registry.FindForPath(root);

            Assert.NotNull(found);
            Assert.Equal(Path.GetFullPath(root), found!.RootPath);
        }

        [Fact]
        public void FindForPath_NestedPath_ReturnsAncestorRecord()
        {
            var registry = CreateRegistry();
            var root = Path.Combine(_baseDir, "repoA");
            registry.Register(Record(root));

            var found = registry.FindForPath(Path.Combine(root, "src", "deep"));

            Assert.NotNull(found);
        }

        [Fact]
        public void FindForPath_SiblingWithCommonPrefix_DoesNotMatch()
        {
            // C:\...\repo must not match C:\...\repo2
            var registry = CreateRegistry();
            registry.Register(Record(Path.Combine(_baseDir, "repo")));

            var found = registry.FindForPath(Path.Combine(_baseDir, "repo2"));

            Assert.Null(found);
        }

        [Fact]
        public void FindForPath_MultipleAncestors_LongestRootWins()
        {
            var registry = CreateRegistry();
            var outer = Path.Combine(_baseDir, "outer");
            var inner = Path.Combine(outer, "inner");
            registry.Register(Record(outer, port: 7891));
            registry.Register(Record(inner, port: 7892));

            var found = registry.FindForPath(Path.Combine(inner, "src"));

            Assert.NotNull(found);
            Assert.Equal(7892, found!.Port);
        }

        [Fact]
        public void FindForPath_NoMatch_ReturnsNull()
        {
            var registry = CreateRegistry();
            registry.Register(Record(Path.Combine(_baseDir, "repoA")));

            Assert.Null(registry.FindForPath(Path.Combine(_baseDir, "unrelated")));
        }

        [Fact]
        public void GetAll_CorruptRegistryFile_TreatedAsEmpty()
        {
            Directory.CreateDirectory(_baseDir);
            File.WriteAllText(Path.Combine(_baseDir, "instances.json"), "{ not valid json !!!");
            var registry = CreateRegistry();

            Assert.Empty(registry.GetAll());
        }

        [Fact]
        public void Register_AfterCorruptFile_RecoversAndPersists()
        {
            Directory.CreateDirectory(_baseDir);
            File.WriteAllText(Path.Combine(_baseDir, "instances.json"), "garbage");
            var registry = CreateRegistry();

            registry.Register(Record(Path.Combine(_baseDir, "repoA")));

            Assert.Single(CreateRegistry().GetAll());
        }
    }
}
