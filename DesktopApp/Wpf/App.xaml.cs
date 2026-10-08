using System.IO;
using System.Windows;
using TempLab.Acquisition;
using TempLab.DesktopApp.Services;
using TempLab.Presentation;
using TempLab.Protocol;

namespace TempLab.DesktopApp;

public partial class App : Application
{
    private MainViewModel? _viewModel;
    private FileLogSink? _logSink;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TempLab", "logs");
        _logSink = new FileLogSink(Path.Combine(logDir, $"desktop-{DateTime.UtcNow:yyyyMMdd}.log"));
        var log = new StructuredLogger("desktop", _logSink);

        var engine = new AcquisitionEngine(log.ForComponent("engine"));
        _viewModel = new MainViewModel(engine, new WpfDispatcher(Dispatcher), new FileDialogService(), log: log);
        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Cancel and await workers, close sockets, files and the native handle before the process ends.
        if (_viewModel is not null)
        {
            Task.Run(async () => await _viewModel.DisposeAsync()).Wait(TimeSpan.FromSeconds(5));
        }
        _logSink?.Dispose();
        base.OnExit(e);
    }
}
