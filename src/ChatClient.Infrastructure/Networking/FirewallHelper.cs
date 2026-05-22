using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChatClient.Infrastructure.Networking;

/// <summary>
/// Tự động thêm rule Windows Firewall để cho phép client kết nối vào port server.
/// Chỉ hoạt động trên Windows; trên OS khác là no-op.
/// Cần quyền Administrator để thêm rule; nếu không đủ quyền sẽ trả về thông báo hướng dẫn.
/// </summary>
public static class FirewallHelper
{
    private const string RulePrefix = "ChatGroupServer";

    /// <summary>
    /// Thêm rule inbound TCP cho <paramref name="port"/>.
    /// Trả về <c>null</c> nếu thành công, chuỗi gợi ý nếu cần thao tác thủ công.
    /// </summary>
    public static string? EnsurePortOpen(int port)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null; // Linux/macOS: người dùng tự mở port

        var ruleName = $"{RulePrefix} port {port}";

        try
        {
            // Kiểm tra rule đã tồn tại chưa
            if (RuleExists(ruleName))
                return null;

            // Thêm rule mới
            var result = RunNetsh(
                $"advfirewall firewall add rule " +
                $"name=\"{ruleName}\" " +
                $"dir=in action=allow protocol=TCP localport={port} " +
                $"description=\"Opened automatically by Chat Group Server\"");

            return result.Success ? null : BuildManualGuide(port);
        }
        catch
        {
            return BuildManualGuide(port);
        }
    }

    /// <summary>
    /// Xóa rule đã tạo khi server dừng (tùy chọn).
    /// </summary>
    public static void RemoveRule(int port)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        try
        {
            var ruleName = $"{RulePrefix} port {port}";
            RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
        }
        catch { /* ignore */ }
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private static bool RuleExists(string ruleName)
    {
        var result = RunNetsh($"advfirewall firewall show rule name=\"{ruleName}\"");
        return result.Success && result.Output.Contains(ruleName, StringComparison.OrdinalIgnoreCase);
    }

    private static (bool Success, string Output) RunNetsh(string arguments)
    {
        var psi = new ProcessStartInfo("netsh", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process is null) return (false, string.Empty);

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode == 0, output);
    }

    private static string BuildManualGuide(int port) =>
        $"Firewall: chạy lệnh sau với quyền Administrator để mở port {port}:\n" +
        $"  netsh advfirewall firewall add rule name=\"{RulePrefix} port {port}\" " +
        $"dir=in action=allow protocol=TCP localport={port}";
}
