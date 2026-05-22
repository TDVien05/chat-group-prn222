namespace ChatClient.Business.Models;

public sealed class ChatMessage
{
    public string Type { get; init; } = string.Empty;
    public string? Room { get; init; }
    public string? User { get; init; }
    public string Content { get; init; } = string.Empty;
    public string? Icon { get; init; }
    public string? FileName { get; init; }
    public string? MediaType { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public bool IsHistory { get; init; }
    // File transfer fields
    public long FileSize { get; init; }
    public string? TransferId { get; init; }
    public int TotalChunks { get; init; }
    public int ChunkIndex { get; init; }
}
