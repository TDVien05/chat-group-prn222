using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChatClient.Infrastructure.Networking;

/// <summary>
/// Tự động thêm rule Windows Firewall để cho phép client kết nối vào port server.
/// Chỉ hoạt động trên Windows; trên OS khác là no-op.
///
/// Quy trình 2 bước:
///   1. Thử chạy netsh trực tiếp (thành công nếu app đã có quyền Administrator).
///   2. Nếu thất bại → hiện hộp thoại UAC để xin quyền admin chỉ cho lệnh netsh.
///      Nếu người dùng từ chối UAC → trả về hướng dẫn mở thủ công.
/// </summary>
public static class FirewallHelper
{
    private const string RulePrefix = "ChatGroupServer";

    /// <summary>
    /// Đảm bảo có rule inbound TCP cho <paramref name="port"/>.
    /// Trả về <c>null</c> nếu thành công; trả về chuỗi hướng dẫn nếu cần mở thủ công.
    /// </summary>
    public static string? EnsurePortOpen(int port)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null; // Linux / macOS: người dùng tự mở port

        var ruleName = $"{RulePrefix} port {port}";

        try
        {
            // Rule đã tồn tại → không cần làm gì thêm
            if (RuleExists(ruleName))
                return null;

            var addArgs = BuildAddRuleArgs(ruleName, port);

            // Bước 1: thử trực tiếp (thành công khi app đang chạy với quyền admin)
            var result = RunNetsh(addArgs);
            if (result.Success) return null;

            // Bước 2: xin quyền qua hộp thoại UAC của Windows
            if (TryRunElevated(addArgs)) return null;

            // Người dùng từ chối hoặc UAC thất bại → hiện hướng dẫn thủ công
            return BuildManualGuide(port);
        }
        catch
        {
            return BuildManualGuide(port);
        }
    }

    /// <summary>
    /// Xóa rule đã tạo khi server dừng.
    /// </summary>
    public static void RemoveRule(int port)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{RulePrefix} port {port}\"");
        }
        catch { /* ignore */ }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string BuildAddRuleArgs(string ruleName, int port) =>
        $"advfirewall firewall add rule " +
        $"name=\"{ruleName}\" " +
        $"dir=in action=allow protocol=TCP localport={port} " +
        $"description=\"Opened automatically by Chat Group Server\"";

    private static bool RuleExists(string ruleName)
    {
        var result = RunNetsh($"advfirewall firewall show rule name=\"{ruleName}\"");
        return result.Success && result.Output.Contains(ruleName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Chạy netsh không cần admin — dùng pipe để đọc output.</summary>
    private static (bool Success, string Output) RunNetsh(string arguments)
    {
        var psi = new ProcessStartInfo("netsh", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process = Process.Start(psi);
        if (process is null) return (false, string.Empty);

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode == 0, output);
    }

    /// <summary>
    /// Chạy lại cùng lệnh netsh với quyền admin qua hộp thoại UAC.
    /// Windows sẽ hiện "Do you want to allow this app to make changes?" cho netsh.exe.
    /// Trả về <c>true</c> nếu người dùng đồng ý và lệnh thành công.
    /// </summary>
    private static bool TryRunElevated(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName        = "netsh.exe",
                Arguments       = arguments,
                UseShellExecute = true,    // bắt buộc khi dùng Verb
                Verb            = "runas", // kích hoạt hộp thoại UAC
                WindowStyle     = ProcessWindowStyle.Hidden  // ẩn cửa sổ cmd sau khi UAC duyệt
            };

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            // Chờ tối đa 30s: thời gian người dùng tương tác UAC + netsh chạy xong
            proc.WaitForExit(30_000);
            return proc.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Người dùng nhấn "No" / hủy UAC
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildManualGuide(int port) =>
        $"Firewall: chạy lệnh sau với quyền Administrator để mở port {port}:\n" +
        $"  netsh advfirewall firewall add rule name=\"{RulePrefix} port {port}\" " +
        $"dir=in action=allow protocol=TCP localport={port}";
}
