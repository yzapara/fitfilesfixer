using System.Collections.Generic;

namespace FitFilesFixer.Web.Models;

public record FitProcessResult
{
    public int TotalPoints { get; init; }
    public int FixedPoints { get; init; }
    public int NullCoords { get; init; }
    public int JumpCoords { get; init; }
    public int DroppedTimestamp { get; init; }
    public int DroppedDuplicate { get; init; }
    public int DroppedCorrupt { get; init; }
    public int ProcessingMs { get; init; }
    public string OutputPath { get; init; } = string.Empty;
    public string OutputName { get; init; } = string.Empty;
    public List<TrackPoint> TrackPoints { get; init; } = new();
}
