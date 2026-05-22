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
    public string? TransferId { get; init; }
    public long FileSize { get; init; }
    public ImageSource? ImagePreview { get; init; }
    public bool ShowSender { get; init; }
    public bool IsImage { get; init; }
    public bool IsIcon { get; init; }
    public bool IsFile { get; init; }
    public bool IsFileProgress { get; init; }
    public double FileProgress { get; init; }

    /// <summary>Hiển thị emoji to hơn, text bình thường nhỏ hơn.</summary>
    public double BodyFontSize => IsIcon ? 52 : 15;

    /// <summary>Text / icon dùng chung template nhưng font size khác nhau.</summary>
    public bool IsText => !IsImage && !IsFile && !IsFileProgress;

    public string FormattedFileSize => FormatBytes(FileSize);

    public static ChatMessageItemViewModel FromMessage(ChatMessage message, string currentUser)
    {
        var isSystem   = message.Type is "system" or "error" or "welcome" or "joined";
        var isImage    = string.Equals(message.Type, "image",        StringComparison.OrdinalIgnoreCase);
        var isIcon     = string.Equals(message.Type, "icon",         StringComparison.OrdinalIgnoreCase);
        var isFileReady   = string.Equals(message.Type, "file-ready",   StringComparison.OrdinalIgnoreCase);
        var isFileProgress = string.Equals(message.Type, "file-progress", StringComparison.OrdinalIgnoreCase);

        var isOwn = !isSystem &&
                    !string.IsNullOrWhiteSpace(message.User) &&
                    string.Equals(message.User, currentUser, StringComparison.Ordinal);

        double progressPct = 0;
        if (isFileProgress && int.TryParse(message.Content, out var pct))
            progressPct = pct;

        return new ChatMessageItemViewModel
        {
            User        = isSystem ? "System" : (message.User ?? "Unknown"),
            Body        = isFileProgress
                ? $"Uploading {message.FileName} — {message.Content}%"
                : message.Content,
            TimeLabel   = $"{message.Timestamp.ToLocalTime():HH:mm}{(message.IsHistory ? "  saved" : string.Empty)}",
            BubbleBrush = ResolveBubbleBrush(isSystem, isOwn, isFileReady, isIcon),
            ForegroundBrush = isSystem
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(20, 54, 84)),
            MetaBrush   = isSystem
                ? new SolidColorBrush(Color.FromRgb(221, 241, 255))
                : new SolidColorBrush(Color.FromRgb(101, 128, 153)),
            Alignment   = isSystem || isFileProgress
                ? HorizontalAlignment.Center
                : (isOwn ? HorizontalAlignment.Right : HorizontalAlignment.Left),
            ShowSender  = !isSystem && !isOwn && !isFileProgress,
            FileName    = message.FileName,
            TransferId  = message.TransferId,
            FileSize    = message.FileSize,
            ImagePreview = isImage ? TryCreateBitmap(message.Content) : null,
            IsImage       = isImage,
            IsIcon        = isIcon,
            IsFile        = isFileReady,
            IsFileProgress = isFileProgress,
            FileProgress  = progressPct
        };
    }

    private static Brush ResolveBubbleBrush(bool isSystem, bool isOwn, bool isFileReady, bool isIcon)
    {
        if (isIcon)     return new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)); // trong suốt — chỉ hiện emoji
        if (isSystem)   return new SolidColorBrush(Color.FromRgb(29, 92, 145));
        if (isFileReady) return new SolidColorBrush(Color.FromRgb(16, 120, 80));
        if (isOwn)      return new SolidColorBrush(Color.FromRgb(196, 232, 255));
        return new SolidColorBrush(Color.FromRgb(240, 248, 255));
    }

    private static ImageSource? TryCreateBitmap(string base64Content)
    {
        if (string.IsNullOrWhiteSpace(base64Content)) return null;
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
        catch { return null; }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        <= 0               => string.Empty,
        < 1024             => $"{bytes} B",
        < 1024 * 1024      => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _                  => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };
}
