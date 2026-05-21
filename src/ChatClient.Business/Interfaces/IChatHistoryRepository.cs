using ChatClient.Business.Models;

namespace ChatClient.Business.Interfaces;

public interface IChatHistoryRepository
{
    string StorageRoot { get; }
    Task<IReadOnlyList<ChatMessage>> LoadRoomHistoryAsync(string roomName, CancellationToken cancellationToken = default);
    Task AppendAsync(string roomName, ChatMessage message, CancellationToken cancellationToken = default);
}
