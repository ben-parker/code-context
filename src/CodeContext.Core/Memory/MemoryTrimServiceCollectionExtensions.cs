using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeContext.Core.Memory
{
    public static class MemoryTrimServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the host's <see cref="QuietTimeTrim"/>. Its quiet period is read by hand from
        /// <c>CodeContext:MemoryTrimSeconds</c> (Native-AOT safe): absent means 15 s, 0 turns trimming off, a value that
        /// is not a non-negative integer keeps the default and is logged. The coordinator, the snapshot writer and the
        /// REST and MCP request paths mark their work on it.
        /// </summary>
        public static IServiceCollection AddCodeContextMemoryTrim(this IServiceCollection services)
        {
            services.AddSingleton(sp =>
            {
                var logger = sp.GetService<ILoggerFactory>()?.CreateLogger(typeof(QuietTimeTrim).FullName!);
                var settings = Read(sp.GetService<IConfiguration>(), logger);
                return new QuietTimeTrim(settings, TimeProvider.System, trimmed: result => logger?.LogInformation(
                    "Returned memory after a quiet period: working set {Before:F0} MB -> {After:F0} MB, pause {Pause:F0} ms.",
                    result.WorkingSetBefore / 1048576.0, result.WorkingSetAfter / 1048576.0, result.Pause.TotalMilliseconds));
            });
            return services;
        }

        internal static QuietTimeTrimSettings Read(IConfiguration? configuration, ILogger? logger = null)
        {
            var text = configuration?[QuietTimeTrimSettings.ConfigurationKey];
            if (QuietTimeTrimSettings.FromSeconds(text) is { } settings) return settings;
            logger?.LogWarning("Ignoring configuration {Key} = '{Value}': not a valid value; keeping {Kept}.",
                QuietTimeTrimSettings.ConfigurationKey, text, QuietTimeTrimSettings.DefaultQuietSeconds);
            return new QuietTimeTrimSettings();
        }
    }
}
