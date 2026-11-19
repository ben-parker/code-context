using System;
using System.Runtime.CompilerServices;
using CodeContext.Core.Memory;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Workers;

namespace CodeContext.Core.Tests;

/// <summary>
/// Runs once when the test assembly loads. The cache is on by default in the product, and the
/// default key is the real <c>~/.codecontext/cache.key</c>; no test may create or read it. A test
/// that composes a host with the cache on must give its own key path (and a temp root), or turn the
/// cache off: resolving the default key path throws here, so such a test fails instead of touching
/// the real profile or writing <c>.codecontext/</c> under a checked-out fixture.
/// </summary>
internal static class TestAssemblyGuards
{
    [ModuleInitializer]
    internal static void ForbidTheRealCacheKey()
        => SnapshotServiceSettings.DefaultKeyPath = () => throw new InvalidOperationException(
            "A test turned the snapshot cache on without its own key path. Set CodeContextOptions.CacheKeyPath " +
            "to a temp file (and index a temp root), or turn the cache off with CodeContext:CacheEnabled=false.");

    /// <summary>
    /// The hosts tests compose with the product builders read configuration from the environment, and the default is a
    /// quiet-time trim on real time: a blocking, compacting collection of the whole test process 15 s after a
    /// host's last activity, which can land inside an unrelated timing-sensitive test. Off for every host
    /// the tests build and every worker they start (which inherit the variable); the trim's own tests drive it
    /// with explicit settings on fake time.
    /// </summary>
    [ModuleInitializer]
    internal static void TurnTheQuietTimeTrimOff()
        => Environment.SetEnvironmentVariable(QuietTimeTrimSettings.EnvironmentVariable, "0");

    /// <summary>
    /// Worker hibernation is on by default with a long idle time on real time; a host a test composes must not stop its
    /// workers in the middle of an unrelated test. Off for every host the tests build; hibernation's own tests set it.
    /// </summary>
    [ModuleInitializer]
    internal static void TurnWorkerHibernationOff()
        => Environment.SetEnvironmentVariable(WorkerHibernationSettings.EnvironmentVariable, "0");
}
