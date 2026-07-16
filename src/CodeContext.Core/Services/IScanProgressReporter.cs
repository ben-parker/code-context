namespace CodeContext.Core.Services;

public interface IScanProgressReporter
{
    void ReportProgress(int processed, int total, string currentFile);
    void ReportComplete(int totalProcessed, TimeSpan elapsed);
    void ReportError(string filePath, string error);
}

public class ScanProgress
{
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public int FailedFiles { get; set; }
    public int SkippedFiles { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public double PercentComplete => TotalFiles > 0 ? (double)ProcessedFiles / TotalFiles * 100 : 0;
    public DateTime StartTime { get; set; }
    public TimeSpan ElapsedTime => DateTime.UtcNow - StartTime;
}