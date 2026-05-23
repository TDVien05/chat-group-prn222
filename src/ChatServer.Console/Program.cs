using System.IO;
using ChatClient.Business.Models;
using ChatClient.Infrastructure.Config;
using ChatClient.Infrastructure.Networking;
using ChatClient.Infrastructure.Repositories;
using ChatClient.Infrastructure.Storage;
using ChatServer.Console;

const int defaultPort = 5000;

var port = defaultPort;
if (args.Length > 0 && int.TryParse(args[0], out var parsedPort) && parsedPort is > 0 and <= 65535)
{
    port = parsedPort;
}

// ── Logger setup ─────────────────────────────────────────────────────────────
var logDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ChatGroupServer", "Logs");
var logFile = Path.Combine(logDir, $"server-{DateTime.Now:yyyy-MM-dd}.log");
using var logger = new ServerLogger(logFile);
// ─────────────────────────────────────────────────────────────────────────────

var historyRepository = new FileChatHistoryRepository(new HistoryStorageOptions());
var addressProvider = new LocalAddressProvider();

var filesRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ChatGroupServer", "Files");
var fileStorage = new FileTransferStorage(filesRoot);

await using var server = new TcpChatServerHost(historyRepository, fileStorage, logger);

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

await server.StartAsync(new ServerStartRequest { Port = port }, shutdown.Token);

Console.WriteLine($"Chat server is listening on port {port}.");
Console.WriteLine($"Files stored at: {filesRoot}");
Console.WriteLine($"Logs  stored at: {logFile}");

// Hiển thị firewall hint nếu cần
if (server.FirewallHint is not null)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"\n⚠  {server.FirewallHint}\n");
    Console.ResetColor();
}

Console.WriteLine("Share one of these IPv4 addresses with clients on the same network:");
foreach (var address in addressProvider.GetIpv4Addresses())
{
    Console.WriteLine($"  {address}:{port}");
}

await using var ngrok = new NgrokTunnelService();
Console.WriteLine("\nStarting Ngrok tunnel (for Internet access)...");
var publicUrl = await ngrok.StartAsync(port, shutdown.Token);
if (!string.IsNullOrEmpty(publicUrl))
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"\n[INTERNET] Public URL for remote clients: {publicUrl}\n");
    Console.ResetColor();
}
else
{
    Console.WriteLine("\nNgrok tunnel could not be started. If you want internet access, ensure ngrok is installed and in your PATH.\n");
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
    logger.LogInfo("Server stopped. Goodbye.");
}
