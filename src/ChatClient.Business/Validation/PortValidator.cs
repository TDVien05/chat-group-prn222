namespace ChatClient.Business.Validation;

public static class PortValidator
{
    public static bool TryParse(string value, out int port)
    {
        return int.TryParse(value, out port) && port is > 0 and <= 65535;
    }
}
