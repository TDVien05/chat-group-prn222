namespace ChatClient.Business.Interfaces;

public interface ILocalAddressProvider
{
    IReadOnlyList<string> GetIpv4Addresses();
}
