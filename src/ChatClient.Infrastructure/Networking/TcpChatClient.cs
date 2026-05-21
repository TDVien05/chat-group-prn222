using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ChatClient.Business.Interfaces;
using ChatClient.Business.Models;
using ChatClient.Infrastructure.Dtos;

namespace ChatClient.Infrastructure.Networking;

public sealed class TcpChatClient : IChatClient
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _tcpClient;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoopTask;
    private bool _isClosing;

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<string>? ConnectionClosed;

    public bool IsConnected => _tcpClient?.Connected == true;

    public async Task ConnectAsync(ClientConnectionRequest request, CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            throw new InvalidOperationException("Client is already connected.");
        }

        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(request.Host, request.Port, cancellationToken);

        var stream = _tcpClient.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _receiveCts = new CancellationTokenSource();
        _receiveLoopTask = ReceiveLoopAsync(_receiveCts.Token);

        await SendAsync(new ChatEnvelopeDto
        {
            Type = "join",
            Room = request.RoomName,
            User = request.UserName
        }, cancellationToken);
    }

    public Task SendTextMessageAsync(string content, CancellationToken cancellationToken = default)
    {
        return SendAsync(new ChatEnvelopeDto
        {
            Type = "message",
            Content = content
        }, cancellationToken);
    }

    public Task SendIconMessageAsync(string iconGlyph, string iconName, CancellationToken cancellationToken = default)
    {
        return SendAsync(new ChatEnvelopeDto
        {
            Type = "icon",
            Content = iconGlyph,
            Icon = iconName
        }, cancellationToken);
    }

    public Task SendImageMessageAsync(string fileName, string mediaType, string base64Content, CancellationToken cancellationToken = default)
    {
        return SendAsync(new ChatEnvelopeDto
        {
            Type = "image",
            Content = base64Content,
            FileName = fileName,
            MediaType = mediaType
        }, cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            await SendAsync(new ChatEnvelopeDto { Type = "leave" }, cancellationToken);
        }
        catch
        {
        }

        await CloseAsync("Disconnected.");
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync("Disconnected.");
        _writeLock.Dispose();
    }

    private async Task SendAsync(ChatEnvelopeDto envelope, CancellationToken cancellationToken)
    {
        if (_writer is null)
        {
            throw new InvalidOperationException("Client is not connected.");
        }

        var payload = JsonSerializer.Serialize(envelope, _jsonOptions);
        await _writeLock.WaitAsync(cancellationToken);

        try
        {
            await _writer.WriteLineAsync(payload);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _reader is not null)
            {
                var line = await _reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ChatEnvelopeDto? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<ChatEnvelopeDto>(line, _jsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (envelope is not null)
                {
                    MessageReceived?.Invoke(this, MapToBusinessMessage(envelope));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await CloseAsync("Connection closed.", fromReceiveLoop: true);
        }
    }

    private async Task CloseAsync(string reason, bool fromReceiveLoop = false)
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        _receiveCts?.Cancel();

        if (!fromReceiveLoop && _receiveLoopTask is not null)
        {
            try
            {
                await _receiveLoopTask;
            }
            catch
            {
            }
        }

        _reader?.Dispose();
        _writer?.Dispose();
        _tcpClient?.Dispose();
        _receiveCts?.Dispose();

        _reader = null;
        _writer = null;
        _tcpClient = null;
        _receiveCts = null;
        _receiveLoopTask = null;
        _isClosing = false;

        ConnectionClosed?.Invoke(this, reason);
    }

    private static ChatMessage MapToBusinessMessage(ChatEnvelopeDto envelope)
    {
        return new ChatMessage
        {
            Type = envelope.Type,
            Room = envelope.Room,
            User = envelope.User,
            Content = envelope.Content ?? string.Empty,
            Icon = envelope.Icon,
            FileName = envelope.FileName,
            MediaType = envelope.MediaType,
            Timestamp = envelope.Timestamp,
            IsHistory = envelope.IsHistory
        };
    }
}
