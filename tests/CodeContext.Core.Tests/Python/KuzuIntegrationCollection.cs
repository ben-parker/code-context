namespace CodeContext.Core.Tests.Python;

/// <summary>
/// CPython is process-global. Keeping the optional Kuzu fixtures serial prevents one
/// fixture's teardown from racing another fixture's native calls.
/// </summary>
[CollectionDefinition("KuzuIntegration", DisableParallelization = true)]
public sealed class KuzuIntegrationCollection;
