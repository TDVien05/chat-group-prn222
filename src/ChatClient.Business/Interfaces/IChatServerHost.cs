using ChatClient.Business.Models;

namespace ChatClient.Business.Interfaces;

public interface IChatServerHost : IAsyncDisposable
{
    bool IsRunning { get; }
    int Port { get; }
    Task StartAsync(ServerStartRequest request, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
