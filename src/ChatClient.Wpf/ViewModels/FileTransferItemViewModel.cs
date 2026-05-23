using ChatClient.Wpf.Commands;

namespace ChatClient.Wpf.ViewModels;

public sealed class FileTransferItemViewModel : ViewModelBase
{
    // Sliding-window speed: giữ N mốc (timestamp, bytes) để tính tốc độ trung bình
    private const int SpeedWindowSize = 6;
    private readonly record struct SpeedSample(DateTimeOffset Time, long Bytes);
    private readonly Queue<SpeedSample> _speedWindow = new();

    private double _percentage;
    private long _bytesSent;
    private long _bytesReceived;
    private bool _isComplete;
    private bool _isCancelled;
    private string _statusText = string.Empty;
    private double _speedBytesPerSecond;

    public FileTransferItemViewModel(string transferId, string fileName, long totalBytes, bool isUpload, Action<string> cancelAction)
    {
        TransferId = transferId;
        FileName   = fileName;
        TotalBytes = totalBytes;
        IsUpload   = isUpload;
        CancelCommand = new RelayCommand(() => cancelAction(transferId), () => !IsComplete && !IsCancelled);
    }

    public string TransferId { get; }
    public string FileName   { get; }
    public long   TotalBytes { get; }
    public bool   IsUpload   { get; }
    public RelayCommand CancelCommand { get; }

    public double Percentage
    {
        get => _percentage;
        set => SetProperty(ref _percentage, value);
    }

    public long BytesSent
    {
        get => _bytesSent;
        set
        {
            if (SetProperty(ref _bytesSent, value))
                RecordSample(value);
        }
    }

    public long BytesReceived
    {
        get => _bytesReceived;
        set
        {
            if (SetProperty(ref _bytesReceived, value))
                RecordSample(value);
        }
    }

    public bool IsComplete
    {
        get => _isComplete;
        set
        {
            if (SetProperty(ref _isComplete, value))
                CancelCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsCancelled
    {
        get => _isCancelled;
        set
        {
            if (SetProperty(ref _isCancelled, value))
                CancelCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    // ── Tốc độ & ETA ─────────────────────────────────────────────────────────

    /// <summary>Tốc độ truyền hiện tại, ví dụ "18.4 MB/s".</summary>
    public string SpeedText
    {
        get
        {
            if (_speedBytesPerSecond <= 0) return string.Empty;
            return $"{FormatBytes((long)_speedBytesPerSecond)}/s";
        }
    }

    /// <summary>Thời gian còn lại ước tính, ví dụ "~1m 23s".</summary>
    public string EtaText
    {
        get
        {
            if (_speedBytesPerSecond <= 0 || TotalBytes <= 0) return string.Empty;
            var transferred = IsUpload ? _bytesSent : _bytesReceived;
            var remaining   = TotalBytes - transferred;
            if (remaining <= 0) return string.Empty;
            var etaSeconds = remaining / _speedBytesPerSecond;
            return $"~{FormatEta(etaSeconds)}";
        }
    }

    // ── Formatted helpers ─────────────────────────────────────────────────────

    public string FormattedTotalSize     => FormatBytes(TotalBytes);
    public string FormattedBytesSent     => FormatBytes(BytesSent);
    public string FormattedBytesReceived => FormatBytes(BytesReceived);
    public string DirectionIndicator     => IsUpload ? "↑" : "↓";

    // ── Private ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Ghi nhận mốc mới và tính lại tốc độ dựa trên sliding window.
    /// </summary>
    private void RecordSample(long currentBytes)
    {
        var now = DateTimeOffset.UtcNow;
        _speedWindow.Enqueue(new SpeedSample(now, currentBytes));

        // Giữ tối đa SpeedWindowSize mốc
        while (_speedWindow.Count > SpeedWindowSize)
            _speedWindow.Dequeue();

        // Cần ít nhất 2 mốc để tính tốc độ
        if (_speedWindow.Count < 2)
            return;

        var oldest = _speedWindow.Peek();
        var elapsed = (now - oldest.Time).TotalSeconds;
        if (elapsed < 0.1) return; // tránh chia cho 0

        _speedBytesPerSecond = (currentBytes - oldest.Bytes) / elapsed;

        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(EtaText));
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024             => $"{bytes} B",
        < 1024 * 1024      => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _                  => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };

    private static string FormatEta(double seconds)
    {
        if (seconds < 60)  return $"{(int)seconds}s";
        if (seconds < 3600) return $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s";
        return $"{(int)(seconds / 3600)}h {(int)(seconds % 3600 / 60)}m";
    }
}
