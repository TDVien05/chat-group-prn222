using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ChatClient.Business.Interfaces;
using ChatClient.Business.Models;
using ChatClient.Infrastructure.Dtos;

namespace ChatClient.Infrastructure.Networking;

public sealed class TcpChatServerHost : IChatServerHost
{
    private readonly ConcurrentDictionary<string, ChatRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, ClientSession> _sessions = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IChatHistoryRepository _historyRepository;
    private CancellationTokenSource? _serverCts;
    private TcpListener? _listener;
    private Task? _acceptLoopTask;

    public TcpChatServerHost(IChatHistoryRepository historyRepository)
    {
        _historyRepository = historyRepository;
    }

    public bool IsRunning => _listener is not null;
    public int Port { get; private set; }

    public Task StartAsync(ServerStartRequest request, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Server is already running.");
        }

        _serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, request.Port);
        _listener.Start();
        Port = request.Port;
        _acceptLoopTask = AcceptLoopAsync(_serverCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is null || _serverCts is null)
        {
            return;
        }

        _serverCts.Cancel();
        _listener.Stop();

        if (_acceptLoopTask is not null)
        {
            try
            {
                await _acceptLoopTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }

        _sessions.Clear();
        _rooms.Clear();

        _listener = null;
        _acceptLoopTask = null;
        _serverCts.Dispose();
        _serverCts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var tcpClient = await _listener.AcceptTcpClientAsync(cancellationToken);
                var session = new ClientSession(tcpClient, _jsonOptions);
                _sessions[session.Id] = session;
                _ = HandleClientAsync(session, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
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
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ChatEnvelopeDto? incoming;
                try
                {
                    incoming = JsonSerializer.Deserialize<ChatEnvelopeDto>(line, _jsonOptions);
                }
                catch (JsonException)
                {
                    await session.SendAsync(new ChatEnvelopeDto
                    {
                        Type = "error",
                        Content = "Invalid JSON payload.",
                        Timestamp = DateTimeOffset.UtcNow
                    }, cancellationToken);
                    continue;
                }

                if (incoming is null || string.IsNullOrWhiteSpace(incoming.Type))
                {
                    await session.SendAsync(new ChatEnvelopeDto
                    {
                        Type = "error",
                        Content = "Message type is required.",
                        Timestamp = DateTimeOffset.UtcNow
                    }, cancellationToken);
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
                    default:
                        await session.SendAsync(new ChatEnvelopeDto
                        {
                            Type = "error",
                            Content = $"Unsupported message type '{incoming.Type}'.",
                            Room = session.RoomName,
                            User = session.UserName,
                            Timestamp = DateTimeOffset.UtcNow
                        }, cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await RemoveFromRoomAsync(session, notifyRoom: true, CancellationToken.None);
            _sessions.TryRemove(session.Id, out _);
            await session.DisposeAsync();
        }
    }

    private async Task HandleJoinAsync(ClientSession session, ChatEnvelopeDto joinRequest, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(joinRequest.Room) || string.IsNullOrWhiteSpace(joinRequest.User))
        {
            await session.SendAsync(new ChatEnvelopeDto
            {
                Type = "error",
                Content = "Join requires both room and user.",
                Timestamp = DateTimeOffset.UtcNow
            }, cancellationToken);
            return;
        }

        var roomName = joinRequest.Room.Trim();
        var userName = joinRequest.User.Trim();

        await RemoveFromRoomAsync(session, notifyRoom: true, cancellationToken);

        var room = _rooms.GetOrAdd(roomName, static name => new ChatRoom(name));
        room.Members[session.Id] = session;
        session.RoomName = roomName;
        session.UserName = userName;

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
            await session.SendAsync(new ChatEnvelopeDto
            {
                Type = "error",
                Content = "Join a room before sending messages.",
                Timestamp = DateTimeOffset.UtcNow
            }, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(incoming.Content))
        {
            await session.SendAsync(new ChatEnvelopeDto
            {
                Type = "error",
                Content = "Message content cannot be empty.",
                Room = session.RoomName,
                User = session.UserName,
                Timestamp = DateTimeOffset.UtcNow
            }, cancellationToken);
            return;
        }

        if (!_rooms.TryGetValue(session.RoomName, out var room))
        {
            await session.SendAsync(new ChatEnvelopeDto
            {
                Type = "error",
                Content = "The room is no longer available.",
                Room = session.RoomName,
                User = session.UserName,
                Timestamp = DateTimeOffset.UtcNow
            }, cancellationToken);
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

    private async Task RemoveFromRoomAsync(ClientSession session, bool notifyRoom, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.RoomName))
        {
            return;
        }

        var roomName = session.RoomName;
        var userName = session.UserName;

        if (_rooms.TryGetValue(roomName, out var room))
        {
            room.Members.TryRemove(session.Id, out _);

            if (notifyRoom && !string.IsNullOrWhiteSpace(userName))
            {
                var systemMessage = new ChatMessage
                {
                    Type = "system",
                    Room = roomName,
                    User = userName,
                    Content = $"{userName} left the room.",
                    Timestamp = DateTimeOffset.UtcNow
                };

                await BroadcastToRoomAsync(room, MapToDto(systemMessage), cancellationToken);
                await _historyRepository.AppendAsync(roomName, systemMessage, cancellationToken);
            }

            if (room.Members.IsEmpty)
            {
                _rooms.TryRemove(room.Name, out _);
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

    private static ChatEnvelopeDto MapToDto(ChatMessage message)
    {
        return new ChatEnvelopeDto
        {
            Type = message.Type,
            Room = message.Room,
            User = message.User,
            Content = message.Content,
            Icon = message.Icon,
            FileName = message.FileName,
            MediaType = message.MediaType,
            Timestamp = message.Timestamp,
            IsHistory = message.IsHistory
        };
    }

    private sealed class ChatRoom
    {
        public ChatRoom(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public ConcurrentDictionary<Guid, ClientSession> Members { get; } = new();
    }

    private sealed class ClientSession
    {
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly TcpClient _tcpClient;

        public ClientSession(TcpClient tcpClient, JsonSerializerOptions jsonOptions)
        {
            Id = Guid.NewGuid();
            _tcpClient = tcpClient;
            _jsonOptions = jsonOptions;

            var stream = tcpClient.GetStream();
            Reader = new StreamReader(stream, Encoding.UTF8);
            Writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }

        public Guid Id { get; }
        public StreamReader Reader { get; }
        public StreamWriter Writer { get; }
        public string? RoomName { get; set; }
        public string? UserName { get; set; }

        public async Task SendAsync(ChatEnvelopeDto envelope, CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.Serialize(envelope, _jsonOptions);
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await Writer.WriteLineAsync(payload);
            }
            finally
            {
                _writeLock.Release();
            }
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
