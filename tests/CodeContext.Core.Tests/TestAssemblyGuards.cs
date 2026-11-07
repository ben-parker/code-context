using System;
using System.Runtime.CompilerServices;
using CodeContext.Core.Snapshots;

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
}
