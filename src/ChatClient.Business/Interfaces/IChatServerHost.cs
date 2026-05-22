using ChatClient.Business.Models;

namespace ChatClient.Business.Interfaces;

public interface IChatServerHost : IAsyncDisposable
{
    bool IsRunning { get; }
    int Port { get; }

    /// <summary>
    /// Hướng dẫn mở firewall thủ công nếu không tự mở được.
    /// <c>null</c> nghĩa là firewall đã được xử lý hoặc không cần thiết.
    /// </summary>
    string? FirewallHint { get; }

    Task StartAsync(ServerStartRequest request, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
