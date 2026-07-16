using System.Text.Json;

namespace CodeContext.Core.Instances
{
    public class InstanceRegistry : IInstanceRegistry
    {
        private const int WriteRetries = 3;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(50);

        private readonly string _registryPath;
        private readonly string _lockPath;
        private readonly Func<InstanceRecord, bool> _isInstanceAlive;
        private readonly StringComparison _pathComparison =
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public InstanceRegistry(string? baseDirectory = null, Func<InstanceRecord, bool>? isInstanceAlive = null)
        {
            var baseDir = baseDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codecontext");
            _registryPath = Path.Combine(baseDir, "instances.json");
            _lockPath = Path.Combine(baseDir, "instances.lock");
            _isInstanceAlive = isInstanceAlive ?? InstanceIdentity.Matches;
        }

        public IReadOnlyList<InstanceRecord> GetAll()
        {
            return Transact(document =>
            {
                var live = document.Instances.Where(_isInstanceAlive).ToList();
                var pruned = live.Count != document.Instances.Count;
                document.Instances = live;
                return (result: (IReadOnlyList<InstanceRecord>)live, save: pruned);
            });
        }

        public void Register(InstanceRecord record)
        {
            var root = NormalizePath(record.RootPath);
            record.RootPath = root;

            Transact(document =>
            {
                document.Instances.RemoveAll(i => PathsEqual(i.RootPath, root));
                document.Instances.Add(record);
                return (result: true, save: true);
            });
        }

        public void Unregister(string rootPath, string? instanceId = null)
        {
            var root = NormalizePath(rootPath);
            Transact(document =>
            {
                var removed = document.Instances.RemoveAll(i =>
                    PathsEqual(i.RootPath, root)
                    && (instanceId is null || string.Equals(i.InstanceId, instanceId, StringComparison.Ordinal))) > 0;
                return (result: removed, save: removed);
            });
        }

        public InstanceRecord? FindForPath(string path)
        {
            var target = NormalizePath(path);
            return GetAll()
                .Where(i => IsSameOrAncestor(i.RootPath, target))
                .OrderByDescending(i => i.RootPath.Length)
                .FirstOrDefault();
        }

        private bool IsSameOrAncestor(string root, string path)
        {
            if (PathsEqual(root, path)) return true;
            // Boundary check so C:\repo does not match C:\repo2.
            var directoryPrefix = Path.EndsInDirectorySeparator(root)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (path.StartsWith(directoryPrefix, _pathComparison)) return true;

            if (Path.DirectorySeparatorChar == Path.AltDirectorySeparatorChar) return false;
            var alternatePrefix = root.EndsWith(Path.AltDirectorySeparatorChar)
                ? root
                : root + Path.AltDirectorySeparatorChar;
            return path.StartsWith(alternatePrefix, _pathComparison);
        }

        private bool PathsEqual(string a, string b) => string.Equals(a, b, _pathComparison);

        private static string NormalizePath(string path)
            => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        /// <summary>
        /// Runs a load-modify-save sequence under a cross-process exclusive lock so that
        /// concurrent registrations/shutdowns from different processes cannot lose each
        /// other's updates. Proceeding without the lock would violate the transaction
        /// guarantee and can silently lose another process's update, so lock acquisition
        /// fails closed.
        /// </summary>
        private T Transact<T>(Func<InstanceRegistryDocument, (T result, bool save)> operation)
        {
            using var lockHandle = AcquireLock();
            var document = Load();
            var (result, save) = operation(document);
            if (save) Save(document);
            return result;
        }

        private FileStream AcquireLock()
        {
            var directory = Path.GetDirectoryName(_lockPath)!;
            Directory.CreateDirectory(directory);

            var deadline = DateTimeOffset.UtcNow + LockTimeout;
            while (true)
            {
                try
                {
                    return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) when (DateTimeOffset.UtcNow < deadline)
                {
                    Thread.Sleep(LockRetryDelay);
                }
                catch (IOException ex)
                {
                    throw new IOException(
                        $"Timed out acquiring the CodeContext instance-registry lock '{_lockPath}'.", ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new IOException(
                        $"Cannot acquire the CodeContext instance-registry lock '{_lockPath}'.", ex);
                }
            }
        }

        private InstanceRegistryDocument Load()
        {
            try
            {
                if (!File.Exists(_registryPath)) return new InstanceRegistryDocument();
                var json = WithRetries(() => File.ReadAllText(_registryPath));
                return JsonSerializer.Deserialize(json, InstanceRegistryJsonContext.Default.InstanceRegistryDocument)
                    ?? new InstanceRegistryDocument();
            }
            catch (JsonException)
            {
                return new InstanceRegistryDocument();
            }
        }

        private void Save(InstanceRegistryDocument document)
        {
            var directory = Path.GetDirectoryName(_registryPath)!;
            Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(document, InstanceRegistryJsonContext.Default.InstanceRegistryDocument);
            var tempPath = Path.Combine(directory, $"instances.{Guid.NewGuid():N}.tmp");
            WithRetries(() =>
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _registryPath, overwrite: true);
                return true;
            });
        }

        private static T WithRetries<T>(Func<T> action)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return action();
                }
                catch (IOException) when (attempt < WriteRetries)
                {
                    Thread.Sleep(RetryDelay);
                }
            }
        }
    }
}
