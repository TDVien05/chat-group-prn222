using System.Text.Json;
using ChatClient.Business.Interfaces;
using ChatClient.Business.Models;
using ChatClient.Infrastructure.Config;

namespace ChatClient.Infrastructure.Repositories;

public sealed class FileChatHistoryRepository : IChatHistoryRepository
{
    private readonly HistoryStorageOptions _options;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileChatHistoryRepository(HistoryStorageOptions options)
    {
        _options = options;
        Directory.CreateDirectory(_options.StorageRoot);
    }

    public string StorageRoot => _options.StorageRoot;

    public async Task<IReadOnlyList<ChatMessage>> LoadRoomHistoryAsync(string roomName, CancellationToken cancellationToken = default)
    {
        var path = GetRoomFilePath(roomName);
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var messages = await JsonSerializer.DeserializeAsync<List<ChatMessage>>(stream, _jsonOptions, cancellationToken);
        return messages ?? [];
    }

    public async Task AppendAsync(string roomName, ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(roomName))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            var path = GetRoomFilePath(roomName);
            List<ChatMessage> messages;

            if (File.Exists(path))
            {
                await using var readStream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                messages = await JsonSerializer.DeserializeAsync<List<ChatMessage>>(readStream, _jsonOptions, cancellationToken) ?? [];
            }
            else
            {
                messages = [];
            }

            messages.Add(message);

            await using var writeStream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(writeStream, messages, _jsonOptions, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetRoomFilePath(string roomName)
    {
        var safeName = string.Join("_", roomName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "default-room";
        }

        return Path.Combine(_options.StorageRoot, $"{safeName.ToLowerInvariant()}.json");
    }
}
