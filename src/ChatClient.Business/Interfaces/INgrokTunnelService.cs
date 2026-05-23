namespace ChatClient.Business.Interfaces;

/// <summary>
/// Dịch vụ tạo tunnel ngrok để cho phép kết nối từ ngoài mạng nội bộ (ví dụ mạng trường).
/// </summary>
public interface INgrokTunnelService
{
    /// <summary>Tunnel đang chạy hay không.</summary>
    bool IsRunning { get; }

    /// <summary>Địa chỉ public dạng "host:port" do ngrok cấp (null nếu chưa chạy).</summary>
    string? PublicAddress { get; }

    /// <summary>
    /// Khởi động tunnel TCP trỏ vào <paramref name="port"/> trên máy local.
    /// Trả về địa chỉ public (ví dụ "0.tcp.ngrok.io:12345") hoặc null nếu thất bại.
    /// </summary>
    Task<string?> StartAsync(int port, CancellationToken cancellationToken = default);

    /// <summary>Dừng tunnel (nếu đang chạy).</summary>
    Task StopAsync();
}
