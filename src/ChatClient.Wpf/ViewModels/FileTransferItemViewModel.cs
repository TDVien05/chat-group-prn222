using ChatClient.Wpf.Commands;

namespace ChatClient.Wpf.ViewModels;

public sealed class FileTransferItemViewModel : ViewModelBase
{
    private double _percentage;
    private long _bytesSent;
    private long _bytesReceived;
    private bool _isComplete;
    private bool _isCancelled;
    private string _statusText = string.Empty;

    public FileTransferItemViewModel(string transferId, string fileName, long totalBytes, bool isUpload, Action<string> cancelAction)
    {
        TransferId = transferId;
        FileName = fileName;
        TotalBytes = totalBytes;
        IsUpload = isUpload;
        CancelCommand = new RelayCommand(() => cancelAction(transferId), () => !IsComplete && !IsCancelled);
    }

    public string TransferId { get; }
    public string FileName { get; }
    public long TotalBytes { get; }
    public bool IsUpload { get; }
    public RelayCommand CancelCommand { get; }

    public double Percentage
    {
        get => _percentage;
        set => SetProperty(ref _percentage, value);
    }

    public long BytesSent
    {
        get => _bytesSent;
        set => SetProperty(ref _bytesSent, value);
    }

    public long BytesReceived
    {
        get => _bytesReceived;
        set => SetProperty(ref _bytesReceived, value);
    }

    public bool IsComplete
    {
        get => _isComplete;
        set
        {
            if (SetProperty(ref _isComplete, value))
            {
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCancelled
    {
        get => _isCancelled;
        set
        {
            if (SetProperty(ref _isCancelled, value))
            {
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string FormattedTotalSize => FormatBytes(TotalBytes);
    public string FormattedBytesSent => FormatBytes(BytesSent);
    public string FormattedBytesReceived => FormatBytes(BytesReceived);
    public string DirectionIndicator => IsUpload ? "↑" : "↓";

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }
}
