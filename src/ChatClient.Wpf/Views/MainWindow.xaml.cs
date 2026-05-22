using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ChatClient.Wpf.ViewModels;

namespace ChatClient.Wpf.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.Messages.CollectionChanged += Messages_CollectionChanged;
        }
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (MessagesList.Items.Count > 0)
        {
            MessagesList.ScrollIntoView(MessagesList.Items[^1]);
        }
    }

    private void MessageImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        if (sender is FrameworkElement element &&
            element.DataContext is ChatMessageItemViewModel message &&
            message.ImagePreview is not null)
        {
            viewModel.OpenImageViewer(message.ImagePreview, message.FileName);
        }
    }

    private void CloseImageViewer_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            viewModel.CloseImageViewer();
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not string address) return;
        Clipboard.SetText(address);
        var original = btn.Content;
        btn.Content = "✓";
        var timer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(1200) };
        timer.Tick += (s, _) =>
        {
            btn.Content = original;
            ((System.Windows.Threading.DispatcherTimer)s!).Stop();
        };
        timer.Start();
    }

    private void DownloadFile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        if (sender is FrameworkElement element &&
            element.Tag is ChatMessageItemViewModel message &&
            message.TransferId is not null &&
            message.FileName is not null)
        {
            viewModel.DownloadFile(message.TransferId, message.FileName);
        }
    }
}
