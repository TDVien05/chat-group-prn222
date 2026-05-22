using System.Windows.Input;
using System.Windows.Media;

namespace ChatClient.Wpf.ViewModels;

/// <summary>Một nút emoji trong thanh nhập liệu.</summary>
public sealed class EmojiItemViewModel
{
    public required string Glyph { get; init; }
    public required string Name { get; init; }
    public required Brush ButtonBackground { get; init; }
    public required ICommand Command { get; init; }
}
