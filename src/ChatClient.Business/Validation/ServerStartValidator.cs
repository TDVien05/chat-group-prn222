using ChatClient.Business.Models;

namespace ChatClient.Business.Validation;

public static class ServerStartValidator
{
    public static string? Validate(ServerStartRequest request)
    {
        return request.Port is > 0 and <= 65535 ? null : "A valid host port is required.";
    }
}
