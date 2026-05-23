using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ChatClient.Business.Interfaces;

namespace ChatClient.Infrastructure.Networking;

public sealed class LocalAddressProvider : ILocalAddressProvider
{
    // Loại adapter vật lý thực sự (Ethernet dây + WiFi)
    private static readonly HashSet<NetworkInterfaceType> PhysicalTypes =
    [
        NetworkInterfaceType.Ethernet,
        NetworkInterfaceType.Wireless80211,
        NetworkInterfaceType.FastEthernetFx,
        NetworkInterfaceType.FastEthernetT,
        NetworkInterfaceType.GigabitEthernet,
    ];

    private static readonly string[] VirtualKeywords =
    [
        "virtual", "wsl", "hyper-v", "vethernet", "vmware",
        "virtualbox", "bluetooth", "miniport", "loopback"
    ];

    public IReadOnlyList<string> GetIpv4Addresses()
    {
        var addresses = GetFromPhysicalAdapters();

        // Fallback: nếu lọc vật lý quá chặt (ví dụ máy chỉ dùng USB network adapter),
        // thử lấy tất cả adapter đang UP nhưng loại bỏ Loopback và virtual keywords.
        if (addresses.Count == 0)
            addresses = GetFromAllActiveAdapters();

        return addresses.Count > 0 ? addresses : ["127.0.0.1"];
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static List<string> GetFromPhysicalAdapters()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic =>
                nic.OperationalStatus == OperationalStatus.Up &&
                PhysicalTypes.Contains(nic.NetworkInterfaceType) &&
                !IsVirtual(nic))
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Where(ip =>
                ip.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(ip.Address))
            .Select(ip => ip.Address.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ip => ip, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> GetFromAllActiveAdapters()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic =>
                nic.OperationalStatus == OperationalStatus.Up &&
                nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                !IsVirtual(nic))
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Where(ip =>
                ip.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(ip.Address))
            .Select(ip => ip.Address.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ip => ip, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Kiểm tra adapter có phải virtual/ảo không dựa vào tên và mô tả.
    /// </summary>
    private static bool IsVirtual(NetworkInterface nic) =>
        VirtualKeywords.Any(kw =>
            nic.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
            nic.Description.Contains(kw, StringComparison.OrdinalIgnoreCase));
}
