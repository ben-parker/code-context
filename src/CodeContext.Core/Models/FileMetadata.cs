namespace CodeContext.Core.Models;

public class FileMetadata
{
    public string FilePath { get; set; } = string.Empty;
    public DateTime LastModified { get; set; }
    public DateTime LastScanned { get; set; }
    public FileProcessingStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
}

public enum FileProcessingStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
    Skipped
}