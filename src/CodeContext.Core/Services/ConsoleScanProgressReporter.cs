using Microsoft.Extensions.Logging;

namespace CodeContext.Core.Services;

public class ConsoleScanProgressReporter : IScanProgressReporter
{
    private readonly ILogger<ConsoleScanProgressReporter> _logger;
    private DateTime _lastReportTime = DateTime.MinValue;
    private readonly TimeSpan _reportInterval = TimeSpan.FromSeconds(1);

    public ConsoleScanProgressReporter(ILogger<ConsoleScanProgressReporter> logger)
    {
        _logger = logger;
    }

    public void ReportProgress(int processed, int total, string currentFile)
    {
        var now = DateTime.UtcNow;
        if (now - _lastReportTime < _reportInterval && processed < total)
        {
            return;
        }

        _lastReportTime = now;
        var percentage = total > 0 ? (double)processed / total * 100 : 0;
        _logger.LogInformation("Scan progress: {Processed}/{Total} ({Percentage:F1}%) - Current: {File}", 
            processed, total, percentage, Path.GetFileName(currentFile));
    }

    public void ReportComplete(int totalProcessed, TimeSpan elapsed)
    {
        _logger.LogInformation("Scan completed: {TotalProcessed} files processed in {Elapsed:mm\\:ss}", 
            totalProcessed, elapsed);
    }

    public void ReportError(string filePath, string error)
    {
        _logger.LogError("Failed to process {FilePath}: {Error}", filePath, error);
    }
}