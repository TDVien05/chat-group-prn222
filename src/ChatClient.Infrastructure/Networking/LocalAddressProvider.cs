using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ChatClient.Business.Interfaces;

namespace ChatClient.Infrastructure.Networking;

public sealed class LocalAddressProvider : ILocalAddressProvider
{
    public IReadOnlyList<string> GetIpv4Addresses()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Where(ip => ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address))
            .Select(ip => ip.Address.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ip => ip, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return addresses.Count > 0 ? addresses : ["127.0.0.1"];
    }
}
