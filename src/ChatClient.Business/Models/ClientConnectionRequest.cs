namespace ChatClient.Business.Models;

public sealed class ClientConnectionRequest
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public string RoomName { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
}
