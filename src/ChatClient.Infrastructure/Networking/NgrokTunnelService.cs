using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using ChatClient.Business.Interfaces;

namespace ChatClient.Infrastructure.Networking;

/// <summary>
/// Triển khai tunnel ngrok: khởi động tiến trình ngrok và lấy địa chỉ public qua HTTP API local.
/// </summary>
public sealed class NgrokTunnelService : INgrokTunnelService, IAsyncDisposable
{
    // ngrok expose REST API ở cổng này khi đang chạy
    private static readonly Uri NgrokApiBase = new("http://127.0.0.1:4040");

    private readonly HttpClient _http = new() { BaseAddress = NgrokApiBase };
    private Process? _process;
    private string? _publicAddress;

    public bool IsRunning => _process is { HasExited: false };
    public string? PublicAddress => _publicAddress;

    /// <inheritdoc/>
    public async Task<string?> StartAsync(int port, CancellationToken cancellationToken = default)
    {
        // Dừng tunnel cũ nếu có
        await StopAsync();

        var startInfo = new ProcessStartInfo
        {
            FileName  = "ngrok",
            Arguments = $"tcp {port}",
            // Chạy ẩn, không mở cửa sổ console
            UseShellExecute  = false,
            CreateNoWindow   = true,
        };

        try
        {
            _process = Process.Start(startInfo);
        }
        catch (Exception ex) when (
            ex is System.ComponentModel.Win32Exception ||
            ex is FileNotFoundException)
        {
            // ngrok chưa được cài đặt
            return null;
        }

        if (_process is null) return null;

        // Polling REST API của ngrok (tối đa 10 giây) để lấy địa chỉ tunnel
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try { await Task.Delay(500, cancellationToken); }
            catch (OperationCanceledException) { break; }

            // Nếu ngrok crash sớm (ví dụ: chưa auth) → dừng luôn
            if (_process.HasExited)
                return null;

            var address = await QueryPublicAddressAsync(cancellationToken);
            if (address is not null)
            {
                _publicAddress = address;
                return address;
            }
        }

        // Không lấy được địa chỉ sau 10 giây → coi là thất bại
        await StopAsync();
        return null;
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        _publicAddress = null;

        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { /* process có thể đã thoát */ }
        }

        _process?.Dispose();
        _process = null;
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Gọi API /api/tunnels của ngrok để lấy địa chỉ TCP public.
    /// Trả về "host:port" hoặc null nếu ngrok chưa sẵn sàng.
    /// </summary>
    private async Task<string?> QueryPublicAddressAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Timeout ngắn để không chặn polling loop
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            var json = await _http.GetStringAsync("/api/tunnels", cts.Token);
            using var doc = JsonDocument.Parse(json);

            foreach (var tunnel in doc.RootElement.GetProperty("tunnels").EnumerateArray())
            {
                if (tunnel.TryGetProperty("proto", out var proto) &&
                    proto.GetString() == "tcp" &&
                    tunnel.TryGetProperty("public_url", out var urlEl))
                {
                    var url = urlEl.GetString(); // "tcp://0.tcp.ngrok.io:12345"
                    if (url?.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) == true)
                        return url["tcp://".Length..]; // → "0.tcp.ngrok.io:12345"
                }
            }
        }
        catch
        {
            // ngrok API chưa sẵn sàng → thử lại lần sau
        }

        return null;
    }
}
