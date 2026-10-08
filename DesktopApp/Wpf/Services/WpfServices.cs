using System.Windows.Threading;
using Microsoft.Win32;
using TempLab.Presentation;

namespace TempLab.DesktopApp.Services;

public sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}

public sealed class FileDialogService : IFileDialogService
{
    private const string Filter = "CSV session (*.csv)|*.csv|All files (*.*)|*.*";

    public string? PickOpenCsv(string title)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = Filter, CheckFileExists = true };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? PickSaveCsv(string title, string suggestedName)
    {
        var dlg = new SaveFileDialog { Title = title, Filter = Filter, FileName = suggestedName, AddExtension = true, DefaultExt = ".csv" };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
