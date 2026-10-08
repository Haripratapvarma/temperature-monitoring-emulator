using TempLab.Acquisition;
using TempLab.Acquisition.Recording;
using TempLab.Presentation;
using TempLab.Protocol;

namespace TempLab.Tests;

internal sealed class FakeDialogs : IFileDialogService
{
    public string? Open { get; set; }
    public string? Save { get; set; }
    public string? PickOpenCsv(string title) => Open;
    public string? PickSaveCsv(string title, string suggestedName) => Save;
}

public class MainViewModelTests
{
    private static string WriteSession(TempDir dir, int samplesPerChannel, int spikeAt = -1)
    {
        var path = dir.File("session.csv");
        using var w = new CsvSessionWriter(path, new SessionMetadata());
        for (int n = 0; n < samplesPerChannel; n++)
            for (int ch = 0; ch < 4; ch++)
                w.Write("a1", new Sample(ch, n, n / 10.0, n == spikeAt && ch == 3 ? 40 : 25 + ch, SampleQuality.Good));
        return path;
    }

    [Fact]
    public async Task View_model_is_testable_without_a_window_replay_to_completion()
    {
        using var dir = new TempDir();
        var path = WriteSession(dir, 800, spikeAt: 300);
        await using var vm = new MainViewModel(new AcquisitionEngine(), new InlineDispatcher(), new FakeDialogs());
        vm.Mode = SourceMode.Replay;
        vm.ReplayPath = path;
        vm.ReplaySpeed = 0;
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.False(vm.ConnectCommand.CanExecute(null)); // emulator-only command

        await vm.StartCommand.ExecuteAsync();
        await Wait.UntilAsync(() =>
        {
            vm.OnUiTick();
            return vm.IsIdle; // SourceCompleted triggers an automatic stop
        });

        Assert.Equal(3200L, vm.Counters.Processed);
        Assert.All(vm.Channels, c => Assert.Equal(MainViewModel.ChartHistoryPerChannel, c.Raw.Count)); // capped history
        var alert = Assert.Single(vm.Alerts);
        Assert.Equal("Ch 3", alert.Channel);
        Assert.Contains(vm.EventLog, l => l.Contains("SourceCompleted"));
    }

    [Fact]
    public async Task Settings_cannot_change_while_acquiring()
    {
        using var dir = new TempDir();
        var path = WriteSession(dir, 2000);
        await using var vm = new MainViewModel(new AcquisitionEngine(), new InlineDispatcher(), new FakeDialogs());
        vm.Mode = SourceMode.Replay;
        vm.ReplayPath = path;
        vm.ReplaySpeed = 0.5; // slow enough to still be running
        vm.WindowSize = 10;
        await vm.StartCommand.ExecuteAsync();
        Assert.True(vm.IsAcquiring);
        Assert.False(vm.CanEditSettings);
        Assert.False(vm.StartCommand.CanExecute(null));

        int raised = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.WindowSize)) raised++; };
        vm.WindowSize = 99;
        vm.RateHz = 500;
        Assert.Equal(10, vm.WindowSize);
        Assert.Equal(10.0, vm.RateHz, 0.0);
        Assert.Equal(1, raised); // re-notified so a bound view reverts

        await vm.StopCommand.ExecuteAsync();
        Assert.True(vm.CanEditSettings);
        vm.WindowSize = 99;
        Assert.Equal(99, vm.WindowSize);
    }

    [Fact]
    public async Task Connect_start_stop_against_the_emulator()
    {
        await using var emu = new EmulatorFixture();
        using var dir = new TempDir();
        await using var vm = new MainViewModel(new AcquisitionEngine(), new InlineDispatcher(), new FakeDialogs())
        {
            Port = emu.Port,
            RateHz = 100,
            RecordEnabled = true,
            RecordingPath = dir.File("vm.csv"),
        };
        Assert.False(vm.StartCommand.CanExecute(null)); // must connect first
        await vm.ConnectCommand.ExecuteAsync();
        Assert.Equal("Connected", vm.ConnectionStatus);
        Assert.Equal("Idle", vm.DeviceState);

        vm.Channels[2].IsSelected = false;
        await vm.StartCommand.ExecuteAsync();
        await Wait.UntilAsync(() =>
        {
            vm.OnUiTick();
            return vm.Counters.Processed >= 60;
        });
        await vm.StopCommand.ExecuteAsync();
        Assert.True(vm.IsIdle);
        Assert.Equal("Idle", vm.DeviceState);
        Assert.Equal(0, vm.Channels[2].Raw.Count);
        Assert.True(vm.Channels[0].Raw.Count > 0);
        Assert.Equal(dir.File("vm.csv"), vm.ReplayPath); // recording offered for replay

        var rows = CsvSessionReader.Read(dir.File("vm.csv")).Rows;
        Assert.DoesNotContain(rows, r => r.Sample.Channel == 2);
        await vm.DisconnectCommand.ExecuteAsync();
        Assert.Equal("Disconnected", vm.ConnectionStatus);
    }

    [Fact]
    public async Task Command_errors_are_reported_not_thrown()
    {
        await using var vm = new MainViewModel(new AcquisitionEngine(), new InlineDispatcher(), new FakeDialogs()) { Port = 1 };
        await vm.ConnectCommand.ExecuteAsync();
        Assert.StartsWith("Error:", vm.StatusMessage);
        Assert.Equal("Disconnected", vm.ConnectionStatus);
        Assert.True(vm.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public void Chart_series_is_a_bounded_ring()
    {
        var s = new ChartSeries(3);
        for (int i = 0; i < 5; i++) s.Add(i, i * 10);
        Assert.Equal(3, s.Count);
        Assert.Equal(new[] { (2.0, 20.0), (3.0, 30.0), (4.0, 40.0) }, s.Points().ToArray());
        Assert.Equal(5L, s.Version);
    }
}
