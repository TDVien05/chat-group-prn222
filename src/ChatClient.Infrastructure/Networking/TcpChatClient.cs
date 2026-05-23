using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ChatClient.Business.Interfaces;
using ChatClient.Business.Models;
using ChatClient.Infrastructure.Dtos;

namespace ChatClient.Infrastructure.Networking;

public sealed class TcpChatClient : IChatClient
{
    private const int FileChunkSize = 2 * 1024 * 1024; // 2 MB per chunk — tốt hơn cho video lớn

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Per-connection state (re-created on each ConnectAsync)
    private Channel<ChatEnvelopeDto>? _priorityChannel;
    private Channel<ChatEnvelopeDto>? _fileChannel;
    private SemaphoreSlim? _sendSignal;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeUploadCts = new();
    private readonly ConcurrentDictionary<string, FileDownloadState> _activeDownloads = new();

    private TcpClient? _tcpClient;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _connectionCts;
    private Task? _receiveLoopTask;
    private Task? _sendLoopTask;
    private bool _isClosing;

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<string>? ConnectionClosed;
    public event EventHandler<FileUploadProgress>? FileUploadProgressChanged;
    public event EventHandler<FileDownloadProgress>? FileDownloadProgressChanged;

    public bool IsConnected => _tcpClient?.Connected == true;

    public async Task ConnectAsync(ClientConnectionRequest request, CancellationToken cancellationToken = default)
    {
        if (IsConnected)
            throw new InvalidOperationException("Client is already connected.");

        // Create fresh channels and signal for this connection
        _priorityChannel = Channel.CreateUnbounded<ChatEnvelopeDto>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _fileChannel = Channel.CreateBounded<ChatEnvelopeDto>(
            new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        _sendSignal = new SemaphoreSlim(0, int.MaxValue);

        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(request.Host, request.Port, cancellationToken);

        var stream = _tcpClient.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8, bufferSize: 4 * 1024 * 1024); // 4 MB — chứa được Base64 của 2 MB chunk
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _connectionCts = new CancellationTokenSource();

        _sendLoopTask = SendLoopAsync(_connectionCts.Token);
        _receiveLoopTask = ReceiveLoopAsync(_connectionCts.Token);

        await EnqueuePriorityAsync(new ChatEnvelopeDto
        {
            Type = "join",
            Room = request.RoomName,
            User = request.UserName
        }, cancellationToken);
    }

    public Task SendTextMessageAsync(string content, CancellationToken cancellationToken = default)
        => EnqueuePriorityAsync(new ChatEnvelopeDto { Type = "message", Content = content }, cancellationToken);

    public Task SendIconMessageAsync(string iconGlyph, string iconName, CancellationToken cancellationToken = default)
        => EnqueuePriorityAsync(new ChatEnvelopeDto { Type = "icon", Content = iconGlyph, Icon = iconName }, cancellationToken);

    public Task SendImageMessageAsync(string fileName, string mediaType, string base64Content, CancellationToken cancellationToken = default)
        => EnqueuePriorityAsync(new ChatEnvelopeDto { Type = "image", Content = base64Content, FileName = fileName, MediaType = mediaType }, cancellationToken);

    public async Task SendFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var priorityChannel = _priorityChannel ?? throw new InvalidOperationException("Not connected.");
        var fileChannel = _fileChannel ?? throw new InvalidOperationException("Not connected.");
        var sendSignal = _sendSignal ?? throw new InvalidOperationException("Not connected.");

        var transferId = Guid.NewGuid().ToString("N");
        var fileName = Path.GetFileName(filePath);
        var fileInfo = new FileInfo(filePath);
        var fileSize = fileInfo.Length;
        var mediaType = ResolveMediaType(Path.GetExtension(filePath)) ?? "application/octet-stream";
        var totalChunks = (int)Math.Ceiling((double)fileSize / FileChunkSize);

        var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeUploadCts[transferId] = uploadCts;

        try
        {
            // Announce file upload start
            await priorityChannel.Writer.WriteAsync(new ChatEnvelopeDto
            {
                Type = "file-start",
                TransferId = transferId,
                FileName = fileName,
                MediaType = mediaType,
                FileSize = fileSize,
                TotalChunks = totalChunks,
                Timestamp = DateTimeOffset.UtcNow
            }, uploadCts.Token);
            sendSignal.Release();

            FireUploadProgress(transferId, fileName, fileSize, 0, false, false);

            // Stream file in chunks into the low-priority channel
            await using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: FileChunkSize, useAsync: true);

            var buffer = new byte[FileChunkSize];
            int chunkIndex = 0;
            long bytesSent = 0;

            while (!uploadCts.Token.IsCancellationRequested)
            {
                var bytesRead = await stream.ReadAsync(buffer, uploadCts.Token);
                if (bytesRead == 0) break;

                bytesSent += bytesRead;
                var isLast = bytesSent >= fileSize;

                // This write will block if file channel is full (back-pressure)
                await fileChannel.Writer.WriteAsync(new ChatEnvelopeDto
                {
                    Type = "file-chunk",
                    TransferId = transferId,
                    ChunkIndex = chunkIndex,
                    TotalChunks = totalChunks,
                    Content = Convert.ToBase64String(buffer, 0, bytesRead),
                    IsLastChunk = isLast,
                    FileSize = fileSize,
                    Timestamp = DateTimeOffset.UtcNow
                }, uploadCts.Token);
                sendSignal.Release();

                FireUploadProgress(transferId, fileName, fileSize, bytesSent, isLast, false);
                chunkIndex++;

                if (isLast) break;
            }
        }
        catch (OperationCanceledException)
        {
            // Notify server of cancellation
            try
            {
                await priorityChannel.Writer.WriteAsync(new ChatEnvelopeDto
                {
                    Type = "file-cancel",
                    TransferId = transferId,
                    Timestamp = DateTimeOffset.UtcNow
                });
                sendSignal.Release();
            }
            catch { }

            FireUploadProgress(transferId, fileName, fileInfo.Length, 0, false, true);
        }
        finally
        {
            if (_activeUploadCts.TryRemove(transferId, out var cts))
                cts.Dispose();
        }
    }

    public async Task RequestFileDownloadAsync(string transferId, string savePath, CancellationToken cancellationToken = default)
    {
        var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var state = new FileDownloadState
        {
            TransferId = transferId,
            SavePath = savePath,
            CancellationTokenSource = downloadCts
        };
        _activeDownloads[transferId] = state;

        await EnqueuePriorityAsync(new ChatEnvelopeDto
        {
            Type = "file-request",
            TransferId = transferId,
            Timestamp = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    public void CancelFileTransfer(string transferId)
    {
        if (_activeUploadCts.TryGetValue(transferId, out var uploadCts))
            uploadCts.Cancel();
        if (_activeDownloads.TryRemove(transferId, out var state))
        {
            state.CancellationTokenSource.Cancel();
            state.FileStream?.Dispose();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected) return;
        try
        {
            await EnqueuePriorityAsync(new ChatEnvelopeDto { Type = "leave" }, cancellationToken);
            await Task.Delay(100, cancellationToken);
        }
        catch { }
        await CloseAsync("Disconnected.");
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync("Disconnected.");
    }

    private async Task EnqueuePriorityAsync(ChatEnvelopeDto envelope, CancellationToken cancellationToken = default)
    {
        var channel = _priorityChannel ?? throw new InvalidOperationException("Not connected.");
        var signal = _sendSignal ?? throw new InvalidOperationException("Not connected.");
        await channel.Writer.WriteAsync(envelope, cancellationToken);
        signal.Release();
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        var priorityChannel = _priorityChannel!;
        var fileChannel = _fileChannel!;
        var sendSignal = _sendSignal!;

        try
        {
            while (!cancellationToken.IsCancellationRequested && _writer is not null)
            {
                bool didWork = false;

                // Drain ALL priority messages first (chat messages always win)
                while (priorityChannel.Reader.TryRead(out var msg))
                {
                    await WriteEnvelopeAsync(msg, cancellationToken);
                    didWork = true;
                }

                // Send one file chunk (then recheck priority on next loop)
                if (fileChannel.Reader.TryRead(out var fileMsg))
                {
                    await WriteEnvelopeAsync(fileMsg, cancellationToken);
                    didWork = true;
                }

                if (!didWork)
                {
                    // Wait for a signal that something was enqueued
                    await sendSignal.WaitAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task WriteEnvelopeAsync(ChatEnvelopeDto envelope, CancellationToken cancellationToken)
    {
        if (_writer is null) return;
        var payload = JsonSerializer.Serialize(envelope, _jsonOptions);
        await _writer.WriteLineAsync(payload.AsMemory(), cancellationToken);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _reader is not null)
            {
                var line = await _reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                ChatEnvelopeDto? envelope;
                try { envelope = JsonSerializer.Deserialize<ChatEnvelopeDto>(line, _jsonOptions); }
                catch (JsonException) { continue; }

                if (envelope is null) continue;

                await HandleIncomingAsync(envelope, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await CloseAsync("Connection closed.", fromReceiveLoop: true);
        }
    }

    private async Task HandleIncomingAsync(ChatEnvelopeDto envelope, CancellationToken cancellationToken)
    {
        switch (envelope.Type.ToLowerInvariant())
        {
            case "file-chunk":
                await HandleIncomingFileChunkAsync(envelope, cancellationToken);
                break;
            default:
                MessageReceived?.Invoke(this, MapToBusinessMessage(envelope));
                break;
        }
    }

    private async Task HandleIncomingFileChunkAsync(ChatEnvelopeDto envelope, CancellationToken cancellationToken)
    {
        if (envelope.TransferId is null || envelope.Content is null) return;
        if (!_activeDownloads.TryGetValue(envelope.TransferId, out var state)) return;

        var data = Convert.FromBase64String(envelope.Content);

        if (state.FileStream is null)
        {
            state.FileName = envelope.FileName ?? "downloaded_file";
            state.TotalBytes = envelope.FileSize;
            state.FileStream = new FileStream(state.SavePath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 65536, useAsync: true);
        }

        await state.FileStream.WriteAsync(data, cancellationToken);
        state.BytesReceived += data.Length;

        if (envelope.IsLastChunk)
        {
            await state.FileStream.FlushAsync(cancellationToken);
            await state.FileStream.DisposeAsync();
            state.FileStream = null;

            _activeDownloads.TryRemove(envelope.TransferId, out _);
            state.CancellationTokenSource.Dispose();

            FileDownloadProgressChanged?.Invoke(this, new FileDownloadProgress
            {
                TransferId = envelope.TransferId,
                FileName = state.FileName,
                TotalBytes = state.TotalBytes,
                BytesReceived = state.BytesReceived,
                IsComplete = true,
                SavedPath = state.SavePath
            });
        }
        else
        {
            FileDownloadProgressChanged?.Invoke(this, new FileDownloadProgress
            {
                TransferId = envelope.TransferId,
                FileName = state.FileName,
                TotalBytes = state.TotalBytes,
                BytesReceived = state.BytesReceived
            });
        }
    }

    private async Task CloseAsync(string reason, bool fromReceiveLoop = false)
    {
        if (_isClosing) return;
        _isClosing = true;

        _connectionCts?.Cancel();

        // Release send signal to unblock the send loop
        try { _sendSignal?.Release(); } catch { }

        // Cancel all active transfers
        foreach (var cts in _activeUploadCts.Values) try { cts.Cancel(); } catch { }
        foreach (var dl in _activeDownloads.Values)
        {
            try { dl.CancellationTokenSource.Cancel(); } catch { }
            dl.FileStream?.Dispose();
        }
        _activeUploadCts.Clear();
        _activeDownloads.Clear();

        if (!fromReceiveLoop)
        {
            try { await Task.WhenAll(_receiveLoopTask ?? Task.CompletedTask, _sendLoopTask ?? Task.CompletedTask); }
            catch { }
        }

        _reader?.Dispose();
        _writer?.Dispose();
        _tcpClient?.Dispose();
        _connectionCts?.Dispose();
        _sendSignal?.Dispose();

        _reader = null;
        _writer = null;
        _tcpClient = null;
        _connectionCts = null;
        _priorityChannel = null;
        _fileChannel = null;
        _sendSignal = null;
        _receiveLoopTask = null;
        _sendLoopTask = null;
        _isClosing = false;

        ConnectionClosed?.Invoke(this, reason);
    }

    private void FireUploadProgress(string transferId, string fileName, long totalBytes, long bytesSent, bool isComplete, bool isCancelled)
    {
        FileUploadProgressChanged?.Invoke(this, new FileUploadProgress
        {
            TransferId = transferId,
            FileName = fileName,
            TotalBytes = totalBytes,
            BytesSent = bytesSent,
            IsComplete = isComplete,
            IsCancelled = isCancelled
        });
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
            IsHistory = envelope.IsHistory,
            FileSize = envelope.FileSize,
            TransferId = envelope.TransferId,
            TotalChunks = envelope.TotalChunks,
            ChunkIndex = envelope.ChunkIndex
        };
    }

    private static string? ResolveMediaType(string extension) =>
        extension.ToLowerInvariant() switch
        {
            // Ảnh
            ".png"  => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif"  => "image/gif",
            ".bmp"  => "image/bmp",
            ".webp" => "image/webp",
            // Tài liệu
            ".pdf"  => "application/pdf",
            // Video (thêm đầy đủ cho assignment)
            ".mp4"  => "video/mp4",
            ".mkv"  => "video/x-matroska",
            ".avi"  => "video/x-msvideo",
            ".mov"  => "video/quicktime",
            ".wmv"  => "video/x-ms-wmv",
            ".webm" => "video/webm",
            ".flv"  => "video/x-flv",
            ".m4v"  => "video/x-m4v",
            ".ts"   => "video/mp2t",
            // Audio
            ".mp3"  => "audio/mpeg",
            ".aac"  => "audio/aac",
            ".wav"  => "audio/wav",
            ".flac" => "audio/flac",
            // Nén
            ".zip"  => "application/zip",
            ".rar"  => "application/x-rar-compressed",
            ".7z"   => "application/x-7z-compressed",
            _ => null
        };

    private sealed class FileDownloadState
    {
        public string TransferId { get; init; } = string.Empty;
        public string SavePath { get; init; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long TotalBytes { get; set; }
        public long BytesReceived { get; set; }
        public CancellationTokenSource CancellationTokenSource { get; init; } = new();
        public FileStream? FileStream { get; set; }
    }
}
