namespace CodeContext.Core.Services;

public class CompositeScanProgressReporter : IScanProgressReporter
{
    private readonly IScanProgressReporter[] _reporters;

    public CompositeScanProgressReporter(params IScanProgressReporter[] reporters)
    {
        _reporters = reporters;
    }

    public void ReportProgress(int processed, int total, string currentFile)
    {
        foreach (var reporter in _reporters) reporter.ReportProgress(processed, total, currentFile);
    }

    public void ReportComplete(int totalProcessed, TimeSpan elapsed)
    {
        foreach (var reporter in _reporters) reporter.ReportComplete(totalProcessed, elapsed);
    }

    public void ReportError(string filePath, string error)
    {
        foreach (var reporter in _reporters) reporter.ReportError(filePath, error);
    }
}
