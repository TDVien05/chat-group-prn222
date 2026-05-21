using ChatClient.Business.Models;

namespace ChatClient.Business.Validation;

public static class ClientConnectionValidator
{
    public static string? Validate(ClientConnectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Host))
        {
            return "Host IP is required.";
        }

        if (request.Port is <= 0 or > 65535)
        {
            return "A valid port is required.";
        }

        if (string.IsNullOrWhiteSpace(request.RoomName))
        {
            return "Room name is required.";
        }

        if (string.IsNullOrWhiteSpace(request.UserName))
        {
            return "User name is required.";
        }

        return null;
    }
}
