using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChatClient.Business.Models;

namespace ChatClient.Wpf.ViewModels;

public sealed class ChatMessageItemViewModel
{
    public required string User { get; init; }
    public required string Body { get; init; }
    public required string TimeLabel { get; init; }
    public required Brush BubbleBrush { get; init; }
    public required Brush ForegroundBrush { get; init; }
    public required Brush MetaBrush { get; init; }
    public required HorizontalAlignment Alignment { get; init; }
    public string? FileName { get; init; }
    public ImageSource? ImagePreview { get; init; }
    public bool ShowSender { get; init; }
    public bool IsImage { get; init; }
    public bool IsText => !IsImage;

    public static ChatMessageItemViewModel FromMessage(ChatMessage message, string currentUser)
    {
        var isSystem = string.Equals(message.Type, "system", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(message.Type, "error", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(message.Type, "welcome", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(message.Type, "joined", StringComparison.OrdinalIgnoreCase);
        var isImage = string.Equals(message.Type, "image", StringComparison.OrdinalIgnoreCase);

        var isOwn = !isSystem &&
                    !string.IsNullOrWhiteSpace(message.User) &&
                    string.Equals(message.User, currentUser, StringComparison.Ordinal);

        return new ChatMessageItemViewModel
        {
            User = isSystem ? "System" : (message.User ?? "Unknown"),
            Body = message.Content,
            TimeLabel = $"{message.Timestamp.ToLocalTime():HH:mm}{(message.IsHistory ? "  saved" : string.Empty)}",
            BubbleBrush = ResolveBubbleBrush(isSystem, isOwn),
            ForegroundBrush = isSystem ? Brushes.White : new SolidColorBrush(Color.FromRgb(20, 54, 84)),
            MetaBrush = isSystem ? new SolidColorBrush(Color.FromRgb(221, 241, 255)) : new SolidColorBrush(Color.FromRgb(101, 128, 153)),
            Alignment = isSystem ? HorizontalAlignment.Center : (isOwn ? HorizontalAlignment.Right : HorizontalAlignment.Left),
            ShowSender = !isSystem && !isOwn,
            FileName = message.FileName,
            ImagePreview = isImage ? TryCreateBitmap(message.Content) : null,
            IsImage = isImage
        };
    }

    private static Brush ResolveBubbleBrush(bool isSystem, bool isOwn)
    {
        if (isSystem)
        {
            return new SolidColorBrush(Color.FromRgb(29, 92, 145));
        }

        if (isOwn)
        {
            return new SolidColorBrush(Color.FromRgb(196, 232, 255));
        }

        return new SolidColorBrush(Color.FromRgb(240, 248, 255));
    }

    private static ImageSource? TryCreateBitmap(string base64Content)
    {
        if (string.IsNullOrWhiteSpace(base64Content))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(base64Content);
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
