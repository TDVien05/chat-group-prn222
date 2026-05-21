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

namespace ChatClient.Wpf.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private const int MaxSelectedImageBytes = 5 * 1024 * 1024;

    private readonly ChatApplicationService _applicationService;
    private readonly IChatHistoryRepository _historyRepository;

    private string _activeRoomTitle = "No room connected";
    private string _connectionBadge = "Offline";
    private string _hostPort = "5000";
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
        SendThumbsUpCommand = new AsyncRelayCommand(() => SendIconAsync("\U0001F44D", "thumbs-up"));
        SendHeartCommand = new AsyncRelayCommand(() => SendIconAsync("\u2764\uFE0F", "heart"));
        SendLaughCommand = new AsyncRelayCommand(() => SendIconAsync("\U0001F602", "laugh"));
        SendSurprisedCommand = new AsyncRelayCommand(() => SendIconAsync("\U0001F62E", "surprised"));
        ChooseImageCommand = new AsyncRelayCommand(ChooseImageAsync);
        ClearSelectedImageCommand = new AsyncRelayCommand(ClearSelectedImageAsync, () => HasPendingImage);
        SendImageCommand = new AsyncRelayCommand(SendImageAsync, () => HasPendingImage);

        _applicationService.MessageReceived += ApplicationService_MessageReceived;
        _applicationService.ConnectionClosed += ApplicationService_ConnectionClosed;
    }

    public ObservableCollection<ChatMessageItemViewModel> Messages { get; } = [];
    public ObservableCollection<string> HostAddresses { get; } = [];

    public AsyncRelayCommand StartServerCommand { get; }
    public AsyncRelayCommand StopServerCommand { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand SendMessageCommand { get; }
    public AsyncRelayCommand SendThumbsUpCommand { get; }
    public AsyncRelayCommand SendHeartCommand { get; }
    public AsyncRelayCommand SendLaughCommand { get; }
    public AsyncRelayCommand SendSurprisedCommand { get; }
    public AsyncRelayCommand ChooseImageCommand { get; }
    public AsyncRelayCommand ClearSelectedImageCommand { get; }
    public AsyncRelayCommand SendImageCommand { get; }

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

    public ImageSource? ViewerImageSource
    {
        get => _viewerImageSource;
        private set
        {
            if (SetProperty(ref _viewerImageSource, value))
            {
                OnPropertyChanged(nameof(IsImageViewerOpen));
            }
        }
    }

    public string ViewerImageFileName
    {
        get => _viewerImageFileName;
        private set => SetProperty(ref _viewerImageFileName, value);
    }

    public bool IsImageViewerOpen => ViewerImageSource is not null;

    private async Task StartServerAsync()
    {
        if (!PortValidator.TryParse(HostPort, out var port))
        {
            StatusText = "A valid host port is required.";
            return;
        }

        try
        {
            await _applicationService.StartServerAsync(new ServerStartRequest { Port = port });
            HostAddresses.Clear();
            foreach (var address in _applicationService.GetShareableAddresses())
            {
                HostAddresses.Add(address);
            }

            StatusText = "Server is running. Share one of the listed IP addresses.";
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
            await _applicationService.ConnectAsync(request);
            ConnectionBadge = "Connected";
            ActiveRoomTitle = $"#{request.RoomName}";
            StatusText = $"Connected to {request.Host}:{request.Port} as {request.UserName}.";
        }
        catch (Exception ex)
        {
            ConnectionBadge = "Offline";
            ActiveRoomTitle = "No room connected";
            StatusText = $"Unable to connect: {ex.Message}";
        }
    }

    private Task DisconnectAsync()
    {
        return _applicationService.DisconnectAsync();
    }

    private async Task SendMessageAsync()
    {
        try
        {
            await _applicationService.SendTextMessageAsync(OutgoingMessage);
            OutgoingMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText = $"Send failed: {ex.Message}";
        }
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

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var fileInfo = new FileInfo(dialog.FileName);
            if (fileInfo.Length > MaxSelectedImageBytes)
            {
                StatusText = "Selected image is too large. Choose a file up to 5 MB.";
                return;
            }

            var bytes = await File.ReadAllBytesAsync(dialog.FileName);
            var mediaType = ResolveMediaType(fileInfo.Extension);
            if (mediaType is null)
            {
                StatusText = "Unsupported image type.";
                return;
            }

            _selectedImageBase64 = Convert.ToBase64String(bytes);
            _selectedImageMediaType = mediaType;
            SelectedImageFileName = fileInfo.Name;
            SelectedImagePreview = CreateBitmap(bytes);
            StatusText = $"Selected {fileInfo.Name}. Review the preview, then send it.";
        }
        catch (Exception ex)
        {
            StatusText = $"Unable to load image: {ex.Message}";
        }
    }

    private Task ClearSelectedImageAsync()
    {
        ClearSelectedImage();
        StatusText = "Selected image removed.";
        return Task.CompletedTask;
    }

    private async Task SendImageAsync()
    {
        if (string.IsNullOrWhiteSpace(_selectedImageBase64) ||
            string.IsNullOrWhiteSpace(_selectedImageMediaType) ||
            string.IsNullOrWhiteSpace(SelectedImageFileName))
        {
            return;
        }

        try
        {
            await _applicationService.SendImageMessageAsync(SelectedImageFileName, _selectedImageMediaType, _selectedImageBase64);
            ClearSelectedImage();
            StatusText = "Image sent.";
        }
        catch (Exception ex)
        {
            StatusText = $"Image send failed: {ex.Message}";
        }
    }

    private async Task SendIconAsync(string glyph, string iconName)
    {
        try
        {
            await _applicationService.SendIconMessageAsync(glyph, iconName);
        }
        catch (Exception ex)
        {
            StatusText = $"Icon send failed: {ex.Message}";
        }
    }

    private void ApplicationService_MessageReceived(object? sender, ChatMessage message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (string.Equals(message.Type, "welcome", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(message.Type, "joined", StringComparison.OrdinalIgnoreCase))
            {
                StatusText = message.Content;
                return;
            }

            if (string.Equals(message.Type, "error", StringComparison.OrdinalIgnoreCase))
            {
                StatusText = message.Content;
            }

            Messages.Add(ChatMessageItemViewModel.FromMessage(message, UserName.Trim()));
        });
    }

    private void ApplicationService_ConnectionClosed(object? sender, string reason)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            ConnectionBadge = "Offline";
            ActiveRoomTitle = "No room connected";
            StatusText = reason;
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

    private static string? ResolveMediaType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => null
        };
    }

    public void OpenImageViewer(ImageSource? imageSource, string? fileName)
    {
        if (imageSource is null)
        {
            return;
        }

        ViewerImageSource = imageSource;
        ViewerImageFileName = string.IsNullOrWhiteSpace(fileName) ? "Image preview" : fileName;
    }

    public void CloseImageViewer()
    {
        ViewerImageSource = null;
        ViewerImageFileName = string.Empty;
    }
}
