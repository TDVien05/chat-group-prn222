using System.Windows.Media;

namespace ChatClient.Wpf.ViewModels;

public sealed class ParticipantItemViewModel : ViewModelBase
{
    private bool _isOnline;

    public required string Name { get; init; }
    public string Initial => !string.IsNullOrWhiteSpace(Name) ? Name.Substring(0, 1).ToUpper() : "?";

    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            if (SetProperty(ref _isOnline, value))
            {
                OnPropertyChanged(nameof(StatusColor));
            }
        }
    }

    public Brush StatusColor => IsOnline ? new SolidColorBrush(Color.FromRgb(34, 197, 94)) : new SolidColorBrush(Color.FromRgb(156, 163, 175)); // Green / Gray
}
