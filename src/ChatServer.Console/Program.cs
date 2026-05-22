using System.IO;
using ChatClient.Business.Models;
using ChatClient.Infrastructure.Config;
using ChatClient.Infrastructure.Networking;
using ChatClient.Infrastructure.Repositories;
using ChatClient.Infrastructure.Storage;

const int defaultPort = 5000;

var port = defaultPort;
if (args.Length > 0 && int.TryParse(args[0], out var parsedPort) && parsedPort is > 0 and <= 65535)
{
    port = parsedPort;
}

var historyRepository = new FileChatHistoryRepository(new HistoryStorageOptions());
var addressProvider = new LocalAddressProvider();

var filesRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ChatGroupServer", "Files");
var fileStorage = new FileTransferStorage(filesRoot);

await using var server = new TcpChatServerHost(historyRepository, fileStorage);

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

await server.StartAsync(new ServerStartRequest { Port = port }, shutdown.Token);

Console.WriteLine($"Chat server is listening on port {port}.");
Console.WriteLine($"Files stored at: {filesRoot}");
Console.WriteLine("Share one of these IPv4 addresses with clients on the same network:");
foreach (var address in addressProvider.GetIpv4Addresses())
{
    Console.WriteLine($"  {address}:{port}");
}

Console.WriteLine("Press Ctrl+C to stop.");

try
{
    await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
}
catch (OperationCanceledException)
{
}
finally
{
    await server.StopAsync();
}
