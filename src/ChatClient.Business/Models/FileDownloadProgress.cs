namespace ChatClient.Business.Models;

public sealed class FileDownloadProgress
{
    public string TransferId { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public long BytesReceived { get; init; }
    public double Percentage => TotalBytes > 0 ? Math.Round((double)BytesReceived / TotalBytes * 100.0, 1) : 0;
    public bool IsComplete { get; init; }
    public bool IsCancelled { get; init; }
    public string? SavedPath { get; init; }
    public string? ErrorMessage { get; init; }
}
