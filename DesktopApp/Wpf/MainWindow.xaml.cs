using System.Windows;
using System.Windows.Threading;
using TempLab.Presentation;

namespace TempLab.DesktopApp;

public partial class MainWindow : Window
{
    // One UI update batch every 100 ms, independent of the sample rate.
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };

    public MainWindow()
    {
        InitializeComponent();
        _tick.Tick += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.OnUiTick();
                Chart.Refresh();
            }
        };
        Loaded += (_, _) => _tick.Start();
        Closed += (_, _) => _tick.Stop();
    }
}
