namespace FitFilesFixer.Web.Models;

public record RequestLog
{
    public string? Ip { get; init; }
    public string? Country { get; init; }
    public string? City { get; init; }
    public string? FileName { get; init; }
    public int FileSizeKb { get; init; }
    public int TotalPoints { get; init; }
    public int FixedPoints { get; init; }
    public int DroppedTimestamp { get; init; }
    public int DroppedDuplicate { get; init; }
    public int DroppedCorrupt { get; init; }
    public int ProcessingMs { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? SavedFileName { get; init; }
}
