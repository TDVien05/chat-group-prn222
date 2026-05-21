namespace ChatClient.Infrastructure.Config;

public sealed class HistoryStorageOptions
{
    public string StorageRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChatGroupServer",
        "History");
}
