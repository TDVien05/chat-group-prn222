using ChatClient.Business.Interfaces;
using ChatClient.Business.Models;
using ChatClient.Business.Validation;

namespace ChatClient.Business.Services;

public sealed class ChatApplicationService : IAsyncDisposable
{
    private readonly IChatClient _chatClient;
    private readonly IChatServerHost _chatServerHost;
    private readonly ILocalAddressProvider _localAddressProvider;
    private readonly INgrokTunnelService _ngrokTunnel;

    public ChatApplicationService(
        IChatClient chatClient,
        IChatServerHost chatServerHost,
        ILocalAddressProvider localAddressProvider,
        INgrokTunnelService ngrokTunnel)
    {
        _chatClient = chatClient;
        _chatServerHost = chatServerHost;
        _localAddressProvider = localAddressProvider;
        _ngrokTunnel = ngrokTunnel;

        _chatClient.MessageReceived += (_, message) => MessageReceived?.Invoke(this, message);
        _chatClient.ConnectionClosed += (_, reason) => ConnectionClosed?.Invoke(this, reason);
        _chatClient.FileUploadProgressChanged += (_, progress) => FileUploadProgressChanged?.Invoke(this, progress);
        _chatClient.FileDownloadProgressChanged += (_, progress) => FileDownloadProgressChanged?.Invoke(this, progress);
    }

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<string>? ConnectionClosed;
    public event EventHandler<FileUploadProgress>? FileUploadProgressChanged;
    public event EventHandler<FileDownloadProgress>? FileDownloadProgressChanged;

    public bool IsConnected => _chatClient.IsConnected;
    public bool IsServerRunning => _chatServerHost.IsRunning;
    public int RunningPort => _chatServerHost.Port;
    public string? FirewallHint => _chatServerHost.FirewallHint;

    // ── ngrok Tunnel ─────────────────────────────────────────────────────────

    public bool IsTunnelRunning => _ngrokTunnel.IsRunning;
    public string? TunnelAddress => _ngrokTunnel.PublicAddress;

    /// <summary>
    /// Khởi động tunnel ngrok để bạn bè ngoài mạng nội bộ có thể kết nối.
    /// Trả về địa chỉ public (ví dụ "0.tcp.ngrok.io:12345") hoặc null nếu thất bại.
    /// </summary>
    public async Task<string?> StartTunnelAsync(CancellationToken cancellationToken = default)
    {
        if (!IsServerRunning)
            throw new InvalidOperationException("Hãy khởi động server trước khi tạo tunnel.");
        return await _ngrokTunnel.StartAsync(RunningPort, cancellationToken);
    }

    /// <summary>Dừng tunnel ngrok.</summary>
    public Task StopTunnelAsync() => _ngrokTunnel.StopAsync();

    // ── Addresses ─────────────────────────────────────────────────────────────

    public IReadOnlyList<string> GetShareableAddresses()
    {
        return _localAddressProvider.GetIpv4Addresses()
            .Select(address => $"{address}:{RunningPort}")
            .ToList();
    }

    public async Task StartServerAsync(ServerStartRequest request, CancellationToken cancellationToken = default)
    {
        var validationError = ServerStartValidator.Validate(request);
        if (validationError is not null)
            throw new InvalidOperationException(validationError);
        await _chatServerHost.StartAsync(request, cancellationToken);
    }

    public Task StopServerAsync(CancellationToken cancellationToken = default)
        => _chatServerHost.StopAsync(cancellationToken);

    public async Task ConnectAsync(ClientConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var validationError = ClientConnectionValidator.Validate(request);
        if (validationError is not null)
            throw new InvalidOperationException(validationError);
        await _chatClient.ConnectAsync(request, cancellationToken);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => _chatClient.DisconnectAsync(cancellationToken);

    public Task SendTextMessageAsync(string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Task.CompletedTask;
        return _chatClient.SendTextMessageAsync(content.Trim(), cancellationToken);
    }

    public Task SendIconMessageAsync(string glyph, string iconName, CancellationToken cancellationToken = default)
        => _chatClient.SendIconMessageAsync(glyph, iconName, cancellationToken);

    public Task SendImageMessageAsync(string fileName, string mediaType, string base64Content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(mediaType) || string.IsNullOrWhiteSpace(base64Content))
            return Task.CompletedTask;
        return _chatClient.SendImageMessageAsync(fileName, mediaType, base64Content, cancellationToken);
    }

    public Task SendFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return Task.CompletedTask;
        return _chatClient.SendFileAsync(filePath, cancellationToken);
    }

    public Task RequestFileDownloadAsync(string transferId, string savePath, CancellationToken cancellationToken = default)
        => _chatClient.RequestFileDownloadAsync(transferId, savePath, cancellationToken);

    public void CancelFileTransfer(string transferId)
        => _chatClient.CancelFileTransfer(transferId);

    public async ValueTask DisposeAsync()
    {
        await _ngrokTunnel.StopAsync();
        await _chatClient.DisposeAsync();
        await _chatServerHost.DisposeAsync();
    }
}
