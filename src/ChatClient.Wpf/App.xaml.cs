using System.Windows;
using ChatClient.Business.Services;
using ChatClient.Infrastructure.Config;
using ChatClient.Infrastructure.Networking;
using ChatClient.Infrastructure.Repositories;
using ChatClient.Wpf.ViewModels;
using ChatClient.Wpf.Views;

namespace ChatClient.Wpf;

public partial class App : Application
{
    private ChatApplicationService? _applicationService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var historyOptions = new HistoryStorageOptions();
        var historyRepository = new FileChatHistoryRepository(historyOptions);
        var applicationService = new ChatApplicationService(
            new TcpChatClient(),
            new TcpChatServerHost(historyRepository),
            new LocalAddressProvider());

        _applicationService = applicationService;

        var viewModel = new MainWindowViewModel(applicationService, historyRepository);
        var mainWindow = new MainWindow
        {
            DataContext = viewModel
        };

        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_applicationService is not null)
        {
            await _applicationService.DisposeAsync();
        }

        base.OnExit(e);
    }
}
