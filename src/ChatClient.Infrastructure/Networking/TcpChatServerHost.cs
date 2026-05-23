using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ChatClient.Business.Interfaces;
using ChatClient.Business.Models;
using ChatClient.Infrastructure.Dtos;
using ChatClient.Infrastructure.Logging;
using ChatClient.Infrastructure.Storage;

namespace ChatClient.Infrastructure.Networking;

public sealed class TcpChatServerHost : IChatServerHost
{
    private readonly ConcurrentDictionary<string, ChatRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, ClientSession> _sessions = new();
    private readonly ConcurrentDictionary<string, FileUploadTransfer> _activeTransfers = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IChatHistoryRepository _historyRepository;
    private readonly IFileTransferStorage _fileStorage;
    private readonly IServerLogger? _logger;
    private CancellationTokenSource? _serverCts;
    private TcpListener? _listener;
    private Task? _acceptLoopTask;

    public TcpChatServerHost(
        IChatHistoryRepository historyRepository,
        IFileTransferStorage fileStorage,
        IServerLogger? logger = null)
    {
        _historyRepository = historyRepository;
        _fileStorage = fileStorage;
        _logger = logger;
    }

    public bool IsRunning => _listener is not null;
    public int Port { get; private set; }

    /// <summary>
    /// Thông báo firewall (null = đã mở / không cần; khác null = cần mở thủ công).
    /// Được set sau khi <see cref="StartAsync"/> hoàn thành.
    /// </summary>
    public string? FirewallHint { get; private set; }

    public Task StartAsync(ServerStartRequest request, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            throw new InvalidOperationException("Server is already running.");

        _serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, request.Port);
        _listener.Start();
        Port = request.Port;

        // Tự động mở Windows Firewall cho port này
        FirewallHint = FirewallHelper.EnsurePortOpen(request.Port);
        if (FirewallHint is null)
            _logger?.LogInfo($"Server started on port {request.Port}. Firewall rule OK.");
        else
            _logger?.LogWarning($"Server started on port {request.Port}. {FirewallHint}");

        _acceptLoopTask = AcceptLoopAsync(_serverCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is null || _serverCts is null) return;

        _logger?.LogInfo($"Server stopping. Active sessions: {_sessions.Count}.");
        _serverCts.Cancel();
        _listener.Stop();

        if (_acceptLoopTask is not null)
        {
            try { await _acceptLoopTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }

        // Clean up active transfers
        foreach (var t in _activeTransfers.Values)
            await t.DisposeAsync();
        _activeTransfers.Clear();

        foreach (var session in _sessions.Values)
            await session.DisposeAsync();
        _sessions.Clear();
        _rooms.Clear();

        _listener = null;
        _acceptLoopTask = null;
        _serverCts.Dispose();
        _serverCts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_listener is null) return;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var tcpClient = await _listener.AcceptTcpClientAsync(cancellationToken);
                var session = new ClientSession(tcpClient, _jsonOptions);
                _sessions[session.Id] = session;
                _logger?.LogInfo($"[CONNECT] IP={session.RemoteEndPoint} | SessionId={session.Id:N} | Active={_sessions.Count}");
                _ = HandleClientAsync(session, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger?.LogError("AcceptLoop crashed unexpectedly.", ex);
        }
    }

    private async Task HandleClientAsync(ClientSession session, CancellationToken cancellationToken)
    {
        try
        {
            await session.SendAsync(new ChatEnvelopeDto
            {
                Type = "welcome",
                Content = "Connected. Send a join command first.",
                Timestamp = DateTimeOffset.UtcNow
            }, cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await session.Reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                ChatEnvelopeDto? incoming;
                try { incoming = JsonSerializer.Deserialize<ChatEnvelopeDto>(line, _jsonOptions); }
                catch (JsonException ex)
                {
                    _logger?.LogWarning($"[INVALID_JSON] IP={session.RemoteEndPoint} User={session.UserName ?? "?"} | {ex.Message}");
                    await session.SendAsync(new ChatEnvelopeDto { Type = "error", Content = "Invalid JSON payload.", Timestamp = DateTimeOffset.UtcNow }, cancellationToken);
                    continue;
                }

                if (incoming is null || string.IsNullOrWhiteSpace(incoming.Type))
                {
                    _logger?.LogWarning($"[MISSING_TYPE] IP={session.RemoteEndPoint} User={session.UserName ?? "?"}");
                    await session.SendAsync(new ChatEnvelopeDto { Type = "error", Content = "Message type is required.", Timestamp = DateTimeOffset.UtcNow }, cancellationToken);
                    continue;
                }

                switch (incoming.Type.Trim().ToLowerInvariant())
                {
                    case "join":
                        await HandleJoinAsync(session, incoming, cancellationToken);
                        break;
                    case "message":
                    case "icon":
                    case "image":
                        await HandleBroadcastAsync(session, incoming, cancellationToken);
                        break;
                    case "leave":
                        await RemoveFromRoomAsync(session, notifyRoom: true, cancellationToken);
                        break;
                    case "file-start":
                        await HandleFileStartAsync(session, incoming, cancellationToken);
                        break;
                    case "file-chunk":
                        await HandleFileChunkAsync(session, incoming, cancellationToken);
                        break;
                    case "file-cancel":
                        await HandleFileCancelAsync(session, incoming, cancellationToken);
                        break;
                    case "file-request":
                        _ = HandleFileRequestAsync(session, incoming, cancellationToken);
                        break;
                    default:
                        _logger?.LogWarning($"[UNSUPPORTED_TYPE] IP={session.RemoteEndPoint} User={session.UserName ?? "?"} Type='{incoming.Type}'");
                        await session.SendAsync(new ChatEnvelopeDto
                        {
                            Type = "error",
                            Content = $"Unsupported message type '{incoming.Type}'.",
                            Timestamp = DateTimeOffset.UtcNow
                        }, cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError($"[SESSION_ERROR] IP={session.RemoteEndPoint} User={session.UserName ?? "?"} Room={session.RoomName ?? "?"}", ex);
        }
        finally
        {
            // Clean up any transfers from this session
            var sessionTransfers = _activeTransfers
                .Where(kv => kv.Value.SessionId == session.Id)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var tid in sessionTransfers)
            {
                if (_activeTransfers.TryRemove(tid, out var t))
                    await t.DisposeAsync();
            }
            await RemoveFromRoomAsync(session, notifyRoom: true, CancellationToken.None);
            _logger?.LogInfo($"[DISCONNECT] IP={session.RemoteEndPoint} User={session.UserName ?? "?"} Room={session.RoomName ?? "?"} | Remaining={_sessions.Count - 1}");
            _sessions.TryRemove(session.Id, out _);
            await session.DisposeAsync();
        }
    }

    private async Task HandleJoinAsync(ClientSession session, ChatEnvelopeDto joinRequest, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(joinRequest.Room) || string.IsNullOrWhiteSpace(joinRequest.User))
        {
            _logger?.LogWarning($"[JOIN_FAIL] IP={session.RemoteEndPoint} — missing room or user.");
            await session.SendAsync(new ChatEnvelopeDto { Type = "error", Content = "Join requires both room and user.", Timestamp = DateTimeOffset.UtcNow }, cancellationToken);
            return;
        }

        var roomName = joinRequest.Room.Trim();
        var userName = joinRequest.User.Trim();

        await RemoveFromRoomAsync(session, notifyRoom: true, cancellationToken);

        var room = _rooms.GetOrAdd(roomName, static name => new ChatRoom(name));
        room.Members[session.Id] = session;
        session.RoomName = roomName;
        session.UserName = userName;

        _logger?.LogInfo($"[JOIN] User='{userName}' Room='{roomName}' IP={session.RemoteEndPoint} | Members={room.Members.Count}");

        await session.SendAsync(new ChatEnvelopeDto
        {
            Type = "joined",
            Room = roomName,
            User = userName,
            Content = $"{userName} joined room {roomName}.",
            Timestamp = DateTimeOffset.UtcNow
        }, cancellationToken);

        var history = await _historyRepository.LoadRoomHistoryAsync(roomName, cancellationToken);
        foreach (var message in history)
        {
            var dto = MapToDto(message);
            dto.IsHistory = true;
            await session.SendAsync(dto, cancellationToken);
        }

        var systemMessage = new ChatMessage
        {
            Type = "system",
            Room = roomName,
            User = userName,
            Content = $"{userName} joined the room.",
            Timestamp = DateTimeOffset.UtcNow
        };

        await BroadcastToRoomAsync(room, MapToDto(systemMessage), cancellationToken);
        await _historyRepository.AppendAsync(roomName, systemMessage, cancellationToken);
    }

    private async Task HandleBroadcastAsync(ClientSession session, ChatEnvelopeDto incoming, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.RoomName) || string.IsNullOrWhiteSpace(session.UserName))
        {
            await session.SendAsync(new ChatEnvelopeDto { Type = "error", Content = "Join a room before sending messages.", Timestamp = DateTimeOffset.UtcNow }, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(incoming.Content))
        {
            await session.SendAsync(new ChatEnvelopeDto { Type = "error", Content = "Message content cannot be empty.", Timestamp = DateTimeOffset.UtcNow }, cancellationToken);
            return;
        }

        if (!_rooms.TryGetValue(session.RoomName, out var room))
        {
            await session.SendAsync(new ChatEnvelopeDto { Type = "error", Content = "The room is no longer available.", Timestamp = DateTimeOffset.UtcNow }, cancellationToken);
            return;
        }

        var message = new ChatMessage
        {
            Type = incoming.Type.Trim().ToLowerInvariant(),
            Room = session.RoomName,
            User = session.UserName,
            Content = incoming.Content.Trim(),
            Icon = incoming.Icon,
            FileName = incoming.FileName,
            MediaType = incoming.MediaType,
            Timestamp = DateTimeOffset.UtcNow
        };

        await BroadcastToRoomAsync(room, MapToDto(message), cancellationToken);
        await _historyRepository.AppendAsync(session.RoomName, message, cancellationToken);
    }

    private async Task HandleFileStartAsync(ClientSession session, ChatEnvelopeDto dto, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.RoomName) || string.IsNullOrWhiteSpace(session.UserName))
        {
            _logger?.LogWarning($"[FILE_START_FAIL] IP={session.RemoteEndPoint} — not in a room.");
            await session.SendAsync(new ChatEnvelopeDto { Type = "error", Content = "Join a room before uploading files.", Timestamp = DateTimeOffset.UtcNow }, cancellationToken);
            return;
        }

        if (dto.TransferId is null || dto.FileName is null)
        {
            _logger?.LogWarning($"[FILE_START_FAIL] IP={session.RemoteEndPoint} User={session.UserName} — missing transferId or fileName.");
            await session.SendAsync(new ChatEnvelopeDto { Type = "error", Content = "file-start requires transferId and fileName.", Timestamp = DateTimeOffset.UtcNow }, cancellationToken);
            return;
        }

        _logger?.LogInfo($"[FILE_START] User='{session.UserName}' Room='{session.RoomName}' File='{dto.FileName}' Size={dto.FileSize:N0}B Chunks={dto.TotalChunks} TransferId={dto.TransferId}");

        var tempPath = Path.GetTempFileName();
        var transfer = new FileUploadTransfer
        {
            TransferId = dto.TransferId,
            FileName = dto.FileName,
            MediaType = dto.MediaType ?? "application/octet-stream",
            FileSize = dto.FileSize,
            TotalChunks = dto.TotalChunks,
            TempPath = tempPath,
            SessionId = session.Id,
            RoomName = session.RoomName,
            UserName = session.UserName,
            Stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true)
        };

        _activeTransfers[dto.TransferId] = transfer;

        // Broadcast upload announcement to room
        if (_rooms.TryGetValue(session.RoomName, out var room))
        {
            await BroadcastToRoomAsync(room, new ChatEnvelopeDto
            {
                Type = "file-progress",
                TransferId = dto.TransferId,
                FileName = dto.FileName,
                User = session.UserName,
                Room = session.RoomName,
                FileSize = dto.FileSize,
                TotalChunks = dto.TotalChunks,
                ChunkIndex = 0,
                Content = "0",
                Timestamp = DateTimeOffset.UtcNow
            }, cancellationToken);
        }
    }

    private async Task HandleFileChunkAsync(ClientSession session, ChatEnvelopeDto dto, CancellationToken cancellationToken)
    {
        if (dto.TransferId is null || dto.Content is null) return;
        if (!_activeTransfers.TryGetValue(dto.TransferId, out var transfer)) return;

        byte[] data;
        try { data = Convert.FromBase64String(dto.Content); }
        catch { return; }

        await transfer.Stream!.WriteAsync(data, cancellationToken);
        transfer.ChunksReceived++;
        transfer.BytesReceived += data.Length;

        // Broadcast progress every 5% or on last chunk
        var progressPct = transfer.TotalChunks > 0
            ? (int)((double)transfer.ChunksReceived / transfer.TotalChunks * 100)
            : 0;

        if (dto.IsLastChunk || progressPct >= transfer.LastBroadcastPct + 5)
        {
            transfer.LastBroadcastPct = progressPct;
            if (_rooms.TryGetValue(transfer.RoomName, out var progressRoom))
            {
                await BroadcastToRoomAsync(progressRoom, new ChatEnvelopeDto
                {
                    Type = "file-progress",
                    TransferId = dto.TransferId,
                    FileName = transfer.FileName,
                    User = transfer.UserName,
                    Room = transfer.RoomName,
                    FileSize = transfer.FileSize,
                    TotalChunks = transfer.TotalChunks,
                    ChunkIndex = transfer.ChunksReceived,
                    Content = progressPct.ToString(),
                    Timestamp = DateTimeOffset.UtcNow
                }, cancellationToken);
            }
        }

        if (dto.IsLastChunk)
        {
            await transfer.Stream.FlushAsync(cancellationToken);
            await transfer.Stream.DisposeAsync();
            transfer.Stream = null;

            // Store file permanently
            await _fileStorage.StoreFileAsync(transfer.TempPath, transfer.TransferId, transfer.FileName, transfer.RoomName, cancellationToken);
            _logger?.LogInfo($"[FILE_DONE] User='{transfer.UserName}' Room='{transfer.RoomName}' File='{transfer.FileName}' Size={transfer.BytesReceived:N0}B TransferId={transfer.TransferId}");

            // Broadcast file-ready to room
            if (_rooms.TryGetValue(transfer.RoomName, out var room))
            {
                var readyDto = new ChatEnvelopeDto
                {
                    Type = "file-ready",
                    TransferId = transfer.TransferId,
                    FileName = transfer.FileName,
                    MediaType = transfer.MediaType,
                    User = transfer.UserName,
                    Room = transfer.RoomName,
                    FileSize = transfer.FileSize,
                    Content = transfer.TransferId, // Content = transferId (used as download key)
                    Timestamp = DateTimeOffset.UtcNow
                };
                await BroadcastToRoomAsync(room, readyDto, cancellationToken);

                // Save to history
                var historyMsg = new ChatMessage
                {
                    Type = "file-ready",
                    Room = transfer.RoomName,
                    User = transfer.UserName,
                    Content = transfer.TransferId,
                    FileName = transfer.FileName,
                    MediaType = transfer.MediaType,
                    FileSize = transfer.FileSize,
                    TransferId = transfer.TransferId,
                    Timestamp = DateTimeOffset.UtcNow
                };
                await _historyRepository.AppendAsync(transfer.RoomName, historyMsg, cancellationToken);
            }

            _activeTransfers.TryRemove(dto.TransferId, out _);
        }
    }

    private async Task HandleFileCancelAsync(ClientSession session, ChatEnvelopeDto dto, CancellationToken cancellationToken)
    {
        if (dto.TransferId is null) return;
        if (_activeTransfers.TryRemove(dto.TransferId, out var transfer))
        {
            await transfer.DisposeAsync();
            // Notify room
            if (!string.IsNullOrWhiteSpace(transfer.RoomName) && _rooms.TryGetValue(transfer.RoomName, out var room))
            {
                await BroadcastToRoomAsync(room, new ChatEnvelopeDto
                {
                    Type = "file-cancel",
                    TransferId = dto.TransferId,
                    FileName = transfer.FileName,
                    User = transfer.UserName,
                    Room = transfer.RoomName,
                    Timestamp = DateTimeOffset.UtcNow
                }, cancellationToken);
            }
        }
    }

    private async Task HandleFileRequestAsync(ClientSession session, ChatEnvelopeDto dto, CancellationToken cancellationToken)
    {
        if (dto.TransferId is null) return;

        if (!_fileStorage.FileExists(dto.TransferId))
        {
            _logger?.LogWarning($"[FILE_REQUEST_FAIL] IP={session.RemoteEndPoint} User={session.UserName ?? "?"} TransferId={dto.TransferId} — file not found.");
            await session.SendAsync(new ChatEnvelopeDto
            {
                Type = "error",
                Content = $"File not found: {dto.TransferId}",
                Timestamp = DateTimeOffset.UtcNow
            }, cancellationToken);
            return;
        }

        try
        {
            var (stream, fileSize, fileName) = await _fileStorage.OpenFileAsync(dto.TransferId, cancellationToken);
            _logger?.LogInfo($"[FILE_SEND] User='{session.UserName ?? "?"}' IP={session.RemoteEndPoint} File='{fileName}' Size={fileSize:N0}B TransferId={dto.TransferId}");
            await using var fileStream = stream;
            const int ChunkSize = 2 * 1024 * 1024; // 2 MB — khớp với client upload
            var buffer = new byte[ChunkSize];
            var totalChunks = (int)Math.Ceiling((double)fileSize / ChunkSize);
            int chunkIndex = 0;
            long bytesSent = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await fileStream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0) break;
                bytesSent += bytesRead;
                var isLast = bytesSent >= fileSize;

                await session.SendAsync(new ChatEnvelopeDto
                {
                    Type = "file-chunk",
                    TransferId = dto.TransferId,
                    FileName = fileName,
                    FileSize = fileSize,
                    ChunkIndex = chunkIndex,
                    TotalChunks = totalChunks,
                    Content = Convert.ToBase64String(buffer, 0, bytesRead),
                    IsLastChunk = isLast,
                    Timestamp = DateTimeOffset.UtcNow
                }, cancellationToken);

                chunkIndex++;
                if (isLast) break;
            }
        }
        catch (FileNotFoundException ex)
        {
            _logger?.LogError($"[FILE_SEND_ERROR] IP={session.RemoteEndPoint} TransferId={dto.TransferId}", ex);
            await session.SendAsync(new ChatEnvelopeDto
            {
                Type = "error",
                Content = "File not found on server.",
                Timestamp = DateTimeOffset.UtcNow
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError($"[FILE_SEND_ERROR] IP={session.RemoteEndPoint} User={session.UserName ?? "?"} TransferId={dto.TransferId}", ex);
        }
    }

    private async Task RemoveFromRoomAsync(ClientSession session, bool notifyRoom, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.RoomName)) return;

        var roomName = session.RoomName;
        var userName = session.UserName;

        if (_rooms.TryGetValue(roomName, out var room))
        {
            room.Members.TryRemove(session.Id, out _);
            _logger?.LogInfo($"[LEAVE] User='{userName ?? "?"}' Room='{roomName}' IP={session.RemoteEndPoint} | Remaining members={room.Members.Count}");

            if (notifyRoom && !string.IsNullOrWhiteSpace(userName))
            {
                var msg = new ChatMessage
                {
                    Type = "system",
                    Room = roomName,
                    User = userName,
                    Content = $"{userName} left the room.",
                    Timestamp = DateTimeOffset.UtcNow
                };
                await BroadcastToRoomAsync(room, MapToDto(msg), cancellationToken);
                await _historyRepository.AppendAsync(roomName, msg, cancellationToken);
            }

            if (room.Members.IsEmpty)
            {
                _rooms.TryRemove(room.Name, out _);
                _logger?.LogInfo($"[ROOM_EMPTY] Room '{roomName}' removed (no more members).");
            }
        }

        session.RoomName = null;
        session.UserName = null;
    }

    private static Task BroadcastToRoomAsync(ChatRoom room, ChatEnvelopeDto envelope, CancellationToken cancellationToken)
    {
        var tasks = room.Members.Values.Select(member => member.SendAsync(envelope, cancellationToken));
        return Task.WhenAll(tasks);
    }

    private static ChatEnvelopeDto MapToDto(ChatMessage message) => new()
    {
        Type = message.Type,
        Room = message.Room,
        User = message.User,
        Content = message.Content,
        Icon = message.Icon,
        FileName = message.FileName,
        MediaType = message.MediaType,
        Timestamp = message.Timestamp,
        IsHistory = message.IsHistory,
        FileSize = message.FileSize,
        TransferId = message.TransferId
    };

    private sealed class ChatRoom
    {
        public ChatRoom(string name) { Name = name; }
        public string Name { get; }
        public ConcurrentDictionary<Guid, ClientSession> Members { get; } = new();
    }

    private sealed class FileUploadTransfer : IAsyncDisposable
    {
        public string TransferId { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public string MediaType { get; init; } = string.Empty;
        public long FileSize { get; init; }
        public int TotalChunks { get; init; }
        public string TempPath { get; init; } = string.Empty;
        public Guid SessionId { get; init; }
        public string RoomName { get; init; } = string.Empty;
        public string UserName { get; init; } = string.Empty;
        public FileStream? Stream { get; set; }
        public int ChunksReceived { get; set; }
        public long BytesReceived { get; set; }
        public int LastBroadcastPct { get; set; }

        public async ValueTask DisposeAsync()
        {
            if (Stream is not null)
            {
                await Stream.DisposeAsync();
                Stream = null;
            }
            try { if (File.Exists(TempPath)) File.Delete(TempPath); } catch { }
        }
    }

    private sealed class ClientSession : IAsyncDisposable
    {
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly TcpClient _tcpClient;

        public ClientSession(TcpClient tcpClient, JsonSerializerOptions jsonOptions)
        {
            Id = Guid.NewGuid();
            _tcpClient = tcpClient;
            _jsonOptions = jsonOptions;
            RemoteEndPoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
            var stream = tcpClient.GetStream();
            Reader = new StreamReader(stream, Encoding.UTF8, bufferSize: 65536); // 64 KB buffer
            Writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }

        public Guid Id { get; }
        public string RemoteEndPoint { get; }   // IP:port của client
        public StreamReader Reader { get; }
        public StreamWriter Writer { get; }
        public string? RoomName { get; set; }
        public string? UserName { get; set; }

        public async Task SendAsync(ChatEnvelopeDto envelope, CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.Serialize(envelope, _jsonOptions);
            await _writeLock.WaitAsync(cancellationToken);
            try { await Writer.WriteLineAsync(payload.AsMemory(), cancellationToken); }
            finally { _writeLock.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            Writer.Dispose();
            Reader.Dispose();
            _tcpClient.Dispose();
            _writeLock.Dispose();
            await ValueTask.CompletedTask;
        }
    }
}
