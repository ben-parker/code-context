using System.Globalization;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Snapshots
{
    public static class SnapshotServiceCollectionExtensions
    {
        /// <summary>The shortest and longest snapshot delay configuration may set, in seconds.</summary>
        internal const int MinConfiguredDelaySeconds = 1;
        internal const int MaxConfiguredDelaySeconds = 86_400;

        /// <summary>
        /// Registers the snapshot writer as a hosted service. Call after the coordinator's hosted service
        /// and before the watcher's: hosted services stop in reverse order, so the final capture runs
        /// while the coordinator loop and the workers are still alive. The service does nothing unless
        /// <see cref="CodeContextOptions.CacheEnabled"/> is set, which is read from configuration by hand
        /// (<c>CodeContext:CacheEnabled</c>, <c>CodeContext:SnapshotQuietSeconds</c>,
        /// <c>CodeContext:SnapshotMaxDelaySeconds</c>) so binding stays Native-AOT safe.
        /// </summary>
        public static IServiceCollection AddCodeContextSnapshots(this IServiceCollection services)
        {
            services.AddSingleton<IConfigureOptions<CodeContextOptions>>(sp =>
                new ConfigureOptions<CodeContextOptions>(options => ApplyConfiguration(
                    options, sp.GetService<IConfiguration>(),
                    sp.GetService<ILoggerFactory>()?.CreateLogger(typeof(SnapshotService).FullName!))));
            services.AddSingleton(sp =>
            {
                var options = sp.GetRequiredService<IOptions<CodeContextOptions>>().Value;
                return new SnapshotService(
                    sp.GetRequiredService<IndexCoordinator>(),
                    (sp.GetRequiredService<IRepositoryFactory>() as InMemoryRepositoryFactory)?.Database,
                    sp.GetRequiredService<IScanStateService>(),
                    SnapshotServiceSettings.From(options),
                    TimeProvider.System,
                    sp.GetRequiredService<ILogger<SnapshotService>>());
            });
            services.AddHostedService(sp => sp.GetRequiredService<SnapshotService>());
            return services;
        }

        /// <summary>
        /// Reads the three settings. Integers are parsed as <see cref="NumberStyles.Integer"/> in the invariant
        /// culture; the two delays are clamped to 1 s .. 1 day. A value that does not parse, or a negative
        /// delay, keeps the default and is logged once (options are built once) at Warning.
        /// </summary>
        internal static void ApplyConfiguration(CodeContextOptions options, IConfiguration? configuration, ILogger? logger = null)
        {
            if (configuration is null) return;

            const string EnabledKey = "CodeContext:CacheEnabled";
            if (configuration[EnabledKey] is { } enabledText)
            {
                if (bool.TryParse(enabledText, out var enabled)) options.CacheEnabled = enabled;
                else Ignored(logger, EnabledKey, enabledText, options.CacheEnabled ? "true" : "false");
            }
            if (ReadDelay(configuration, "CodeContext:SnapshotQuietSeconds", options.SnapshotQuietSeconds, logger) is { } quiet)
                options.SnapshotQuietSeconds = quiet;
            if (ReadDelay(configuration, "CodeContext:SnapshotMaxDelaySeconds", options.SnapshotMaxDelaySeconds, logger) is { } maxDelay)
                options.SnapshotMaxDelaySeconds = maxDelay;
        }

        private static int? ReadDelay(IConfiguration configuration, string key, int current, ILogger? logger)
        {
            if (configuration[key] is not { } text) return null;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
            {
                Ignored(logger, key, text, current.ToString(CultureInfo.InvariantCulture));
                return null;
            }
            return Math.Clamp(seconds, MinConfiguredDelaySeconds, MaxConfiguredDelaySeconds);
        }

        private static void Ignored(ILogger? logger, string key, string value, string kept)
            => logger?.LogWarning("Ignoring configuration {Key} = '{Value}': not a valid value; keeping {Kept}.", key, value, kept);
    }
}
