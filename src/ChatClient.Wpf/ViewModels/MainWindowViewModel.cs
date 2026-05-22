using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChatClient.Business.Interfaces;
using ChatClient.Business.Models;
using ChatClient.Business.Services;
using ChatClient.Business.Validation;
using ChatClient.Wpf.Commands;
using Microsoft.Win32;
using System.Globalization;

namespace ChatClient.Wpf.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private const int MaxSelectedImageBytes = 5 * 1024 * 1024;

    private readonly ChatApplicationService _applicationService;
    private readonly IChatHistoryRepository _historyRepository;

    private string _activeRoomTitle = "No room connected";
    private string _connectionBadge = "Offline";
    private string _hostPort = "5000";
    private bool _isEmojiPickerOpen;
    private bool _isConnectionLostDialogOpen;
    private bool _isConnected;
    private bool _userInitiatedDisconnect;
    private string _lastConnectedHost = string.Empty;
    private int _lastConnectedPort;
    private string _lastConnectedRoom = string.Empty;
    private string _lastConnectedUser = string.Empty;
    private string _connectionLostReason = string.Empty;
    private string _currentTheme = "Light";
    private readonly List<ChatMessage> _rawMessages = [];
    private string _outgoingMessage = string.Empty;
    private string _roomName = "general";
    private string _selectedImageFileName = string.Empty;
    private string? _selectedImageBase64;
    private string? _selectedImageMediaType;
    private ImageSource? _selectedImagePreview;
    private string _viewerImageFileName = string.Empty;
    private ImageSource? _viewerImageSource;
    private string _serverAddress = "127.0.0.1";
    private string _serverPort = "5000";
    private string _statusText = "Start the server or connect to an existing host.";
    private string _userName = Environment.UserName;

    public MainWindowViewModel(ChatApplicationService applicationService, IChatHistoryRepository historyRepository)
    {
        _applicationService = applicationService;
        _historyRepository = historyRepository;

        HostAddresses.Add("Server is offline.");

        StartServerCommand = new AsyncRelayCommand(StartServerAsync);
        StopServerCommand = new AsyncRelayCommand(StopServerAsync);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync);
        SendMessageCommand = new AsyncRelayCommand(SendMessageAsync, () => !string.IsNullOrWhiteSpace(OutgoingMessage));
        ChooseImageCommand = new AsyncRelayCommand(ChooseImageAsync);
        ClearSelectedImageCommand = new AsyncRelayCommand(ClearSelectedImageAsync, () => HasPendingImage);
        SendImageCommand = new AsyncRelayCommand(SendImageAsync, () => HasPendingImage);
        SendFileCommand = new AsyncRelayCommand(SendFileAsync);
        ToggleEmojiPickerCommand = new RelayCommand(() => IsEmojiPickerOpen = !IsEmojiPickerOpen);
        ReconnectCommand = new AsyncRelayCommand(ReconnectAsync);
        DismissConnectionLostCommand = new RelayCommand(() => IsConnectionLostDialogOpen = false);
        SetLightThemeCommand    = new RelayCommand(() => ApplyTheme("Light"));
        SetDarkThemeCommand     = new RelayCommand(() => ApplyTheme("Dark"));
        SetMidnightThemeCommand = new RelayCommand(() => ApplyTheme("Midnight"));

        // Xây danh sách emoji — mỗi item mang sẵn command đã capture glyph + name
        Emojis = EmojiDefs
            .Select(static e => (e.Glyph, e.Name, e.Hex))
            .Select(e => new EmojiItemViewModel
            {
                Glyph           = e.Glyph,
                Name            = e.Name,
                ButtonBackground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(e.Hex)),
                Command = new AsyncRelayCommand(
                    () => SendIconAsync(e.Glyph, e.Name))
            })
            .ToList()
            .AsReadOnly();

        _applicationService.MessageReceived += ApplicationService_MessageReceived;
        _applicationService.ConnectionClosed += ApplicationService_ConnectionClosed;
        _applicationService.FileUploadProgressChanged += ApplicationService_FileUploadProgressChanged;
        _applicationService.FileDownloadProgressChanged += ApplicationService_FileDownloadProgressChanged;
    }

    // ── Emoji definitions: (glyph, name, hex color) ─────────────────────────
    private static readonly (string Glyph, string Name, string Hex)[] EmojiDefs =
    [
        // Cảm xúc tích cực
        ("👍", "Thích",        "#43A047"),
        ("❤️", "Trái tim",     "#E91E63"),
        ("🔥", "Bùng cháy",    "#FB8C00"),
        ("🎉", "Ăn mừng",      "#8E24AA"),
        ("💯", "100 điểm",     "#C62828"),
        ("⭐", "Sao",          "#F9A825"),
        // Mặt cười
        ("😂", "Cười",         "#F9A825"),
        ("🤣", "Lăn ra cười",  "#F57F17"),
        ("🥰", "Yêu",          "#AD1457"),
        ("😎", "Ngầu",         "#00897B"),
        ("🤩", "Choáng ngợp",  "#7B1FA2"),
        ("😄", "Vui vẻ",       "#2E7D32"),
        // Cảm xúc khác
        ("😢", "Buồn",         "#1E88E5"),
        ("😡", "Tức giận",     "#B71C1C"),
        ("😮", "Bất ngờ",      "#6A1B9A"),
        ("🤔", "Suy nghĩ",     "#EF6C00"),
        ("😴", "Ngủ",          "#283593"),
        ("🤮", "Ghê",          "#558B2F"),
        ("😏", "Nhếch mép",    "#4E342E"),
        ("😇", "Thiên thần",   "#0277BD"),
        // Hành động & cử chỉ
        ("👏", "Vỗ tay",       "#E65100"),
        ("🙏", "Cảm ơn",       "#2E7D32"),
        ("💪", "Mạnh mẽ",      "#BF360C"),
        ("👎", "Không thích",  "#E53935"),
        // Vật thể & biểu tượng
        ("✅", "Đồng ý",       "#1B5E20"),
        ("❌", "Không",        "#B71C1C"),
        ("🚀", "Rocket",       "#0D47A1"),
        ("💡", "Ý tưởng",     "#E65100"),
        ("🎵", "Âm nhạc",      "#4A148C"),
        ("🌈", "Cầu vồng",     "#00838F"),
    ];
    // ─────────────────────────────────────────────────────────────────────────

    public ObservableCollection<ChatMessageItemViewModel> Messages { get; } = [];
    public ObservableCollection<string> HostAddresses { get; } = [];
    public ObservableCollection<FileTransferItemViewModel> ActiveTransfers { get; } = [];
    public IReadOnlyList<EmojiItemViewModel> Emojis { get; }

    public AsyncRelayCommand StartServerCommand { get; }
    public AsyncRelayCommand StopServerCommand { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand SendMessageCommand { get; }
    public AsyncRelayCommand ChooseImageCommand { get; }
    public AsyncRelayCommand ClearSelectedImageCommand { get; }
    public AsyncRelayCommand SendImageCommand { get; }
    public AsyncRelayCommand SendFileCommand { get; }
    public RelayCommand ToggleEmojiPickerCommand { get; }
    public AsyncRelayCommand ReconnectCommand { get; }
    public RelayCommand DismissConnectionLostCommand { get; }
    public RelayCommand SetLightThemeCommand    { get; }
    public RelayCommand SetDarkThemeCommand     { get; }
    public RelayCommand SetMidnightThemeCommand { get; }

    public string HostPort
    {
        get => _hostPort;
        set => SetProperty(ref _hostPort, value);
    }

    public string ServerAddress
    {
        get => _serverAddress;
        set => SetProperty(ref _serverAddress, value);
    }

    public string ServerPort
    {
        get => _serverPort;
        set => SetProperty(ref _serverPort, value);
    }

    public string RoomName
    {
        get => _roomName;
        set => SetProperty(ref _roomName, value);
    }

    public string UserName
    {
        get => _userName;
        set => SetProperty(ref _userName, value);
    }

    public string OutgoingMessage
    {
        get => _outgoingMessage;
        set
        {
            if (SetProperty(ref _outgoingMessage, value))
            {
                SendMessageCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(IsOutgoingMessageEmpty));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string ConnectionBadge
    {
        get => _connectionBadge;
        set => SetProperty(ref _connectionBadge, value);
    }

    public string ActiveRoomTitle
    {
        get => _activeRoomTitle;
        set => SetProperty(ref _activeRoomTitle, value);
    }

    public string HistoryFolder => _historyRepository.StorageRoot;

    public ImageSource? SelectedImagePreview
    {
        get => _selectedImagePreview;
        private set
        {
            if (SetProperty(ref _selectedImagePreview, value))
            {
                OnPropertyChanged(nameof(HasPendingImage));
                ClearSelectedImageCommand.RaiseCanExecuteChanged();
                SendImageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectedImageFileName
    {
        get => _selectedImageFileName;
        private set => SetProperty(ref _selectedImageFileName, value);
    }

    public bool HasPendingImage => SelectedImagePreview is not null;
    public bool HasActiveTransfers => ActiveTransfers.Count > 0;
    public bool IsLightTheme    => _currentTheme == "Light";
    public bool IsDarkTheme     => _currentTheme == "Dark";
    public bool IsMidnightTheme => _currentTheme == "Midnight";
    public bool IsOutgoingMessageEmpty => string.IsNullOrEmpty(_outgoingMessage);

    public ImageSource? ViewerImageSource
    {
        get => _viewerImageSource;
        private set
        {
            if (SetProperty(ref _viewerImageSource, value))
                OnPropertyChanged(nameof(IsImageViewerOpen));
        }
    }

    public string ViewerImageFileName
    {
        get => _viewerImageFileName;
        private set => SetProperty(ref _viewerImageFileName, value);
    }

    public bool IsImageViewerOpen => ViewerImageSource is not null;

    public bool IsEmojiPickerOpen
    {
        get => _isEmojiPickerOpen;
        set => SetProperty(ref _isEmojiPickerOpen, value);
    }

    public bool IsConnectionLostDialogOpen
    {
        get => _isConnectionLostDialogOpen;
        set => SetProperty(ref _isConnectionLostDialogOpen, value);
    }

    public string ConnectionLostReason
    {
        get => _connectionLostReason;
        private set => SetProperty(ref _connectionLostReason, value);
    }

    /// <summary>IP:Port hiển thị trong dialog để người dùng biết đang kết nối lại vào đâu.</summary>
    public string LastConnectionInfo =>
        string.IsNullOrWhiteSpace(_lastConnectedHost) ? string.Empty
        : $"{_lastConnectedHost}:{_lastConnectedPort}  ·  #{_lastConnectedRoom}";

    private async Task StartServerAsync()
    {
        if (!PortValidator.TryParse(HostPort, out var port))
        {
            StatusText = "A valid host port is required.";
            return;
        }
        try
        {
            StatusText = "Đang khởi động server — có thể xuất hiện hộp thoại xin quyền Firewall…";
            await _applicationService.StartServerAsync(new ServerStartRequest { Port = port });
            HostAddresses.Clear();
            foreach (var address in _applicationService.GetShareableAddresses())
                HostAddresses.Add(address);

            // Nếu firewall chưa được mở tự động → hiện hướng dẫn
            StatusText = _applicationService.FirewallHint is not null
                ? $"⚠️ Server đã chạy nhưng cần mở Firewall thủ công:\n{_applicationService.FirewallHint}"
                : "Server is running. Share one of the listed IP addresses.";
        }
        catch (Exception ex)
        {
            StatusText = $"Unable to start server: {ex.Message}";
        }
    }

    private async Task StopServerAsync()
    {
        await _applicationService.StopServerAsync();
        HostAddresses.Clear();
        HostAddresses.Add("Server is offline.");
        StatusText = "Server stopped.";
    }

    private async Task ConnectAsync()
    {
        if (!PortValidator.TryParse(ServerPort, out var port))
        {
            StatusText = "A valid server port is required.";
            return;
        }
        var request = new ClientConnectionRequest
        {
            Host = ServerAddress.Trim(),
            Port = port,
            RoomName = RoomName.Trim(),
            UserName = UserName.Trim()
        };
        try
        {
            Messages.Clear();
            _rawMessages.Clear();
            await _applicationService.ConnectAsync(request);

            // Lưu thông tin kết nối để dùng cho chức năng kết nối lại
            _isConnected = true;
            _userInitiatedDisconnect = false;
            _lastConnectedHost = request.Host;
            _lastConnectedPort = port;
            _lastConnectedRoom = request.RoomName;
            _lastConnectedUser = request.UserName;
            OnPropertyChanged(nameof(LastConnectionInfo));

            ConnectionBadge = "Connected";
            ActiveRoomTitle = $"#{request.RoomName}";
            StatusText = $"Connected to {request.Host}:{request.Port} as {request.UserName}.";
        }
        catch (Exception ex)
        {
            _isConnected = false;
            ConnectionBadge = "Offline";
            ActiveRoomTitle = "No room connected";
            StatusText = $"Unable to connect: {ex.Message}";
        }
    }

    private async Task DisconnectAsync()
    {
        _userInitiatedDisconnect = true; // Tránh hiện dialog khi người dùng tự ngắt
        await _applicationService.DisconnectAsync();
    }

    private async Task SendMessageAsync()
    {
        try
        {
            await _applicationService.SendTextMessageAsync(OutgoingMessage);
            OutgoingMessage = string.Empty;
        }
        catch (Exception ex) { StatusText = $"Send failed: {ex.Message}"; }
    }

    private async Task ChooseImageAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an image",
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var fileInfo = new FileInfo(dialog.FileName);
            if (fileInfo.Length > MaxSelectedImageBytes)
            {
                StatusText = "Selected image is too large. Choose a file up to 5 MB, or use 'Send File' for larger files.";
                return;
            }
            var bytes = await File.ReadAllBytesAsync(dialog.FileName);
            var mediaType = ResolveImageMediaType(fileInfo.Extension);
            if (mediaType is null) { StatusText = "Unsupported image type."; return; }
            _selectedImageBase64 = Convert.ToBase64String(bytes);
            _selectedImageMediaType = mediaType;
            SelectedImageFileName = fileInfo.Name;
            SelectedImagePreview = CreateBitmap(bytes);
            StatusText = $"Selected {fileInfo.Name}. Review the preview, then send it.";
        }
        catch (Exception ex) { StatusText = $"Unable to load image: {ex.Message}"; }
    }

    private Task ClearSelectedImageAsync()
    {
        ClearSelectedImage();
        StatusText = "Selected image removed.";
        return Task.CompletedTask;
    }

    private async Task SendImageAsync()
    {
        if (string.IsNullOrWhiteSpace(_selectedImageBase64) || string.IsNullOrWhiteSpace(_selectedImageMediaType) || string.IsNullOrWhiteSpace(SelectedImageFileName))
            return;
        try
        {
            await _applicationService.SendImageMessageAsync(SelectedImageFileName, _selectedImageMediaType, _selectedImageBase64);
            ClearSelectedImage();
            StatusText = "Image sent.";
        }
        catch (Exception ex) { StatusText = $"Image send failed: {ex.Message}"; }
    }

    private async Task SendFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a file to send",
            Filter = "All Files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        var filePath = dialog.FileName;
        var fileName = Path.GetFileName(filePath);
        var fileSize = new FileInfo(filePath).Length;

        StatusText = $"Starting upload: {fileName} ({FormatBytes(fileSize)})";

        var cts = new CancellationTokenSource();
        _ = _applicationService.SendFileAsync(filePath, cts.Token)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                        StatusText = $"File upload failed: {t.Exception?.GetBaseException().Message}");
                }
                cts.Dispose();
            }, TaskContinuationOptions.None);
    }

    public void DownloadFile(string transferId, string fileName)
    {
        var saveDialog = new SaveFileDialog
        {
            Title = "Save file",
            FileName = fileName,
            Filter = "All Files|*.*"
        };
        if (saveDialog.ShowDialog() != true) return;

        var savePath = saveDialog.FileName;
        StatusText = $"Downloading {fileName}...";

        _ = _applicationService.RequestFileDownloadAsync(transferId, savePath)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                        StatusText = $"Download failed: {t.Exception?.GetBaseException().Message}");
                }
            });
    }

    private async Task SendIconAsync(string glyph, string iconName)
    {
        try
        {
            await _applicationService.SendIconMessageAsync(glyph, iconName);
            IsEmojiPickerOpen = false; // Đóng picker sau khi gửi emoji
        }
        catch (Exception ex) { StatusText = $"Icon send failed: {ex.Message}"; }
    }

    private async Task ReconnectAsync()
    {
        IsConnectionLostDialogOpen = false;

        if (string.IsNullOrWhiteSpace(_lastConnectedHost) || _lastConnectedPort <= 0) return;

        var request = new ClientConnectionRequest
        {
            Host     = _lastConnectedHost,
            Port     = _lastConnectedPort,
            RoomName = _lastConnectedRoom,
            UserName = _lastConnectedUser
        };
        try
        {
            StatusText = $"Đang kết nối lại {request.Host}:{request.Port}…";
            Messages.Clear();
            _rawMessages.Clear();
            await _applicationService.ConnectAsync(request);

            _isConnected = true;
            _userInitiatedDisconnect = false;
            ConnectionBadge = "Connected";
            ActiveRoomTitle = $"#{request.RoomName}";
            StatusText = $"Đã kết nối lại {request.Host}:{request.Port} với tư cách {request.UserName}.";
        }
        catch (Exception ex)
        {
            _isConnected = false;
            ConnectionBadge = "Offline";
            ActiveRoomTitle = "No room connected";
            StatusText = $"Kết nối lại thất bại: {ex.Message}";
        }
    }

    private void ApplicationService_MessageReceived(object? sender, ChatMessage message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (string.Equals(message.Type, "welcome", StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(message.Type, "joined", StringComparison.OrdinalIgnoreCase))
            {
                StatusText = message.Content;
                return;
            }
            if (string.Equals(message.Type, "error", StringComparison.OrdinalIgnoreCase))
                StatusText = message.Content;

            // Update file-progress messages in-place (replace last progress for same transfer)
            if (string.Equals(message.Type, "file-progress", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(message.TransferId))
            {
                var existing = Messages.LastOrDefault(m => m.IsFileProgress && m.TransferId == message.TransferId);
                if (existing is not null)
                {
                    var idx = Messages.IndexOf(existing);
                    Messages[idx] = ChatMessageItemViewModel.FromMessage(message, UserName.Trim());
                    var rawIdx = _rawMessages.FindLastIndex(m =>
                        string.Equals(m.Type, "file-progress", StringComparison.OrdinalIgnoreCase) &&
                        m.TransferId == message.TransferId);
                    if (rawIdx >= 0) _rawMessages[rawIdx] = message;
                    else _rawMessages.Add(message);
                    return;
                }
            }

            Messages.Add(ChatMessageItemViewModel.FromMessage(message, UserName.Trim()));
            _rawMessages.Add(message);
        });
    }

    private void ApplicationService_ConnectionClosed(object? sender, string reason)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var wasConnected = _isConnected;
            var wasUserInitiated = _userInitiatedDisconnect;

            _isConnected = false;
            _userInitiatedDisconnect = false;

            ConnectionBadge = "Offline";
            ActiveRoomTitle = "No room connected";
            StatusText = reason;

            // Chỉ hiện dialog khi mất kết nối bất ngờ (không phải do người dùng chủ động ngắt)
            if (wasConnected && !wasUserInitiated)
            {
                ConnectionLostReason = reason;
                IsConnectionLostDialogOpen = true;
            }
        });
    }

    private void ApplicationService_FileUploadProgressChanged(object? sender, FileUploadProgress progress)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var existing = ActiveTransfers.FirstOrDefault(t => t.TransferId == progress.TransferId);
            if (existing is null)
            {
                existing = new FileTransferItemViewModel(
                    progress.TransferId,
                    progress.FileName,
                    progress.TotalBytes,
                    isUpload: true,
                    cancelAction: id => _applicationService.CancelFileTransfer(id));
                ActiveTransfers.Add(existing);
                OnPropertyChanged(nameof(HasActiveTransfers));
            }

            existing.Percentage = progress.Percentage;
            existing.BytesSent = progress.BytesSent;

            if (progress.IsCancelled)
            {
                existing.IsCancelled = true;
                existing.StatusText = "Cancelled";
                StatusText = $"Upload cancelled: {progress.FileName}";
            }
            else if (progress.IsComplete)
            {
                existing.IsComplete = true;
                existing.Percentage = 100;
                existing.StatusText = "Upload complete";
                StatusText = $"Upload complete: {progress.FileName}";
                _ = Task.Delay(3000).ContinueWith(_ =>
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        ActiveTransfers.Remove(existing);
                        OnPropertyChanged(nameof(HasActiveTransfers));
                    }));
            }
            else
            {
                existing.StatusText = $"{existing.FormattedBytesSent} / {existing.FormattedTotalSize}";
                StatusText = $"Uploading {progress.FileName}: {progress.Percentage:F0}%";
            }
        });
    }

    private void ApplicationService_FileDownloadProgressChanged(object? sender, FileDownloadProgress progress)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var existing = ActiveTransfers.FirstOrDefault(t => t.TransferId == progress.TransferId);
            if (existing is null)
            {
                existing = new FileTransferItemViewModel(
                    progress.TransferId,
                    progress.FileName,
                    progress.TotalBytes,
                    isUpload: false,
                    cancelAction: id => _applicationService.CancelFileTransfer(id));
                ActiveTransfers.Add(existing);
                OnPropertyChanged(nameof(HasActiveTransfers));
            }

            existing.Percentage = progress.Percentage;
            existing.BytesReceived = progress.BytesReceived;

            if (progress.IsCancelled)
            {
                existing.IsCancelled = true;
                existing.StatusText = "Cancelled";
            }
            else if (progress.IsComplete)
            {
                existing.IsComplete = true;
                existing.Percentage = 100;
                existing.StatusText = $"Saved to {progress.SavedPath}";
                StatusText = $"Download complete: {progress.FileName}";
                _ = Task.Delay(5000).ContinueWith(_ =>
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        ActiveTransfers.Remove(existing);
                        OnPropertyChanged(nameof(HasActiveTransfers));
                    }));
            }
            else
            {
                existing.StatusText = $"{existing.FormattedBytesReceived} / {existing.FormattedTotalSize}";
                StatusText = $"Downloading {progress.FileName}: {progress.Percentage:F0}%";
            }
        });
    }

    private void ClearSelectedImage()
    {
        _selectedImageBase64 = null;
        _selectedImageMediaType = null;
        SelectedImageFileName = string.Empty;
        SelectedImagePreview = null;
    }

    private static ImageSource CreateBitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string? ResolveImageMediaType(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => null
        };

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };

    private void ApplyTheme(string themeName)
    {
        _currentTheme = themeName;

        var resources = Application.Current.Resources.MergedDictionaries;
        var themeDict = resources.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("/Themes/") == true);
        if (themeDict is not null)
            resources.Remove(themeDict);

        resources.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{themeName}.xaml", UriKind.Absolute)
        });

        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsMidnightTheme));

        RebuildMessages();
    }

    private void RebuildMessages()
    {
        var currentUserName = UserName.Trim();
        Messages.Clear();
        foreach (var msg in _rawMessages)
            Messages.Add(ChatMessageItemViewModel.FromMessage(msg, currentUserName));
    }

    public void OpenImageViewer(ImageSource? imageSource, string? fileName)
    {
        if (imageSource is null) return;
        ViewerImageSource = imageSource;
        ViewerImageFileName = string.IsNullOrWhiteSpace(fileName) ? "Image preview" : fileName;
    }

    public void CloseImageViewer()
    {
        ViewerImageSource = null;
        ViewerImageFileName = string.Empty;
    }
}
