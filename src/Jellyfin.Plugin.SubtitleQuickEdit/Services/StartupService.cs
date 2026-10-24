using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubtitleQuickEdit.Core.Storage;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Services;

/// <summary>
/// Logs, once at startup, where the plugin keeps its data and whether this platform is fully supported.
/// </summary>
public sealed class StartupService : IHostedService
{
    private readonly ILogger<StartupService> logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public StartupService(ILogger<StartupService> logger)
    {
        this.logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Subtitle Quick Edit started. Backups are kept in {BackupFolder}.",
            Plugin.Instance?.BackupFolder ?? "(plugin not initialised)");

        string? notice = PlatformServices.Current.StartupNotice;
        if (notice is not null)
        {
            logger.LogWarning("Subtitle Quick Edit: {Notice}", notice);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
