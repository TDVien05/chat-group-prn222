using ChatClient.Business.Models;

namespace ChatClient.Business.Interfaces;

public interface IChatClient : IAsyncDisposable
{
    event EventHandler<ChatMessage>? MessageReceived;
    event EventHandler<string>? ConnectionClosed;

    bool IsConnected { get; }
    Task ConnectAsync(ClientConnectionRequest request, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task SendTextMessageAsync(string content, CancellationToken cancellationToken = default);
    Task SendIconMessageAsync(string iconGlyph, string iconName, CancellationToken cancellationToken = default);
    Task SendImageMessageAsync(string fileName, string mediaType, string base64Content, CancellationToken cancellationToken = default);
}
