namespace ChatClient.Infrastructure.Dtos;

public sealed class ChatEnvelopeDto
{
    public string Type { get; set; } = string.Empty;
    public string? Room { get; set; }
    public string? User { get; set; }
    public string? Content { get; set; }
    public string? Icon { get; set; }
    public string? FileName { get; set; }
    public string? MediaType { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public bool IsHistory { get; set; }
    // File transfer fields
    public string? TransferId { get; set; }
    public int ChunkIndex { get; set; }
    public int TotalChunks { get; set; }
    public long FileSize { get; set; }
    public bool IsLastChunk { get; set; }
}
