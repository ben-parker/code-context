namespace CodeContext.Core.Services;

/// <summary>Single wall-clock timestamp captured while constructing the host.</summary>
public sealed record ApplicationStartTime(DateTimeOffset Value);
