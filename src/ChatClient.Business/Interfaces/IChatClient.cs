using ChatClient.Business.Models;

namespace ChatClient.Business.Interfaces;

public interface IChatClient : IAsyncDisposable
{
    event EventHandler<ChatMessage>? MessageReceived;
    event EventHandler<string>? ConnectionClosed;
    event EventHandler<FileUploadProgress>? FileUploadProgressChanged;
    event EventHandler<FileDownloadProgress>? FileDownloadProgressChanged;

    bool IsConnected { get; }
    Task ConnectAsync(ClientConnectionRequest request, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task SendTextMessageAsync(string content, CancellationToken cancellationToken = default);
    Task SendIconMessageAsync(string iconGlyph, string iconName, CancellationToken cancellationToken = default);
    Task SendImageMessageAsync(string fileName, string mediaType, string base64Content, CancellationToken cancellationToken = default);
    Task SendFileAsync(string filePath, CancellationToken cancellationToken = default);
    Task RequestFileDownloadAsync(string transferId, string savePath, CancellationToken cancellationToken = default);
    void CancelFileTransfer(string transferId);
}
