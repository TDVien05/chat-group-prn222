using System.Collections.Specialized;
using System.Windows;
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
        {
            return;
        }

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
        {
            viewModel.CloseImageViewer();
        }
    }
}
