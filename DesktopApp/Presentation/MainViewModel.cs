using System.Collections.ObjectModel;
using System.Globalization;
using TempLab.Acquisition;
using TempLab.Acquisition.Export;
using TempLab.Acquisition.Sources;
using TempLab.Client;
using TempLab.Protocol;

namespace TempLab.Presentation;

public enum SourceMode
{
    Emulator,
    Replay,
}

/// <summary>
/// Main window view model. Threading: every public member is used on the UI thread. Background events
/// (client connection changes) are marshalled through <see cref="IUiDispatcher"/>. Processed data is pulled
/// from the engine by <see cref="OnUiTick"/> (the view calls it ~10×/s), so the UI is updated in batches
/// rather than once per sample.
/// </summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    public const int ChartHistoryPerChannel = 600; // 60 s at 10 Hz
    public const int MaxAlertsShown = 200;
    public const int MaxLogLines = 200;
    public const int MaxSamplesPerTick = 20_000;

    private readonly AcquisitionEngine _engine;
    private readonly IUiDispatcher _ui;
    private readonly IFileDialogService _dialogs;
    private readonly StructuredLogger _log;
    private readonly Func<ClientOptions, InstrumentClient> _clientFactory;
    private InstrumentClient? _client;

    private readonly List<ProcessedSample> _drain = new(4096);
    private readonly List<AlertEvent> _alertDrain = new();
    private readonly List<EngineEvent> _eventDrain = new();
    private readonly Dictionary<int, List<ProcessedSample>> _perChannel = new();

    private SourceMode _mode = SourceMode.Emulator;
    private string _host = "127.0.0.1";
    private int _port = 5055;
    private string? _replayPath;
    private double _replaySpeed = 1.0;
    private double _rateHz = 10;
    private int _seed = 42;
    private int _windowSize = 20;
    private bool _recordEnabled;
    private string? _recordingPath;
    private AlertSource _alertSource = AlertSource.Raw;
    private string _connectionStatus = "Disconnected";
    private string _deviceState = "—";
    private string _statusMessage = "Ready";
    private EngineCounters _counters = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private bool _busy;
    private string? _lastRecording;

    public MainViewModel(AcquisitionEngine engine, IUiDispatcher ui, IFileDialogService dialogs,
        Func<ClientOptions, InstrumentClient>? clientFactory = null, StructuredLogger? log = null)
    {
        _engine = engine;
        _ui = ui;
        _dialogs = dialogs;
        _log = log ?? StructuredLogger.Null;
        _clientFactory = clientFactory ?? (o => new InstrumentClient(o, _log.ForComponent("client")));

        var defaults = new AcquisitionSettings();
        for (int i = 0; i < 4; i++)
        {
            var ch = new ChannelViewModel(i, ChartHistoryPerChannel);
            if (defaults.Alerts.TryGetValue(i, out var a))
            {
                ch.Low = a.Low;
                ch.High = a.High;
            }
            // Start depends on at least one channel being selected, so re-evaluate commands on selection changes.
            ch.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChannelViewModel.IsSelected)) RefreshCommands();
            };
            Channels.Add(ch);
            _perChannel[i] = new List<ProcessedSample>();
        }

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !_busy && _client is null && Mode == SourceMode.Emulator, ReportError);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => !_busy && _client is not null, ReportError);
        StartCommand = new AsyncRelayCommand(StartAsync, CanStart, ReportError);
        StopCommand = new AsyncRelayCommand(StopAsync, () => !_busy && IsAcquiring, ReportError);
        ResetDeviceCommand = new AsyncRelayCommand(ResetDeviceAsync, () => !_busy && IsConnected && IsIdle, ReportError);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => !_busy && IsIdle, ReportError);
        BrowseReplayCommand = new RelayCommand(BrowseReplay, () => CanEditSettings);
        BrowseRecordingCommand = new RelayCommand(BrowseRecording, () => CanEditSettings);
    }

    // ---------- bindable state ----------

    public ObservableCollection<ChannelViewModel> Channels { get; } = new();
    public ObservableCollection<AlertViewModel> Alerts { get; } = new();
    public ObservableCollection<string> EventLog { get; } = new();

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand ResetDeviceCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public RelayCommand BrowseReplayCommand { get; }
    public RelayCommand BrowseRecordingCommand { get; }

    public IReadOnlyList<SourceMode> SourceModes { get; } = Enum.GetValues<SourceMode>();
    public IReadOnlyList<AlertSource> AlertSources { get; } = Enum.GetValues<AlertSource>();

    public bool IsIdle => _engine.State == EngineState.Idle;
    public bool IsAcquiring => _engine.State is EngineState.Running or EngineState.Starting or EngineState.Faulted;
    public bool IsConnected => _client?.State == ConnectionState.Connected;
    public bool CanEditSettings => IsIdle && !_busy;

    public SourceMode Mode { get => _mode; set => SetSetting(ref _mode, value); }
    public string Host { get => _host; set => SetSetting(ref _host, value); }
    public int Port { get => _port; set => SetSetting(ref _port, value); }
    public string? ReplayPath { get => _replayPath; set => SetSetting(ref _replayPath, value); }
    public double ReplaySpeed { get => _replaySpeed; set => SetSetting(ref _replaySpeed, value); }
    public double RateHz { get => _rateHz; set => SetSetting(ref _rateHz, value); }
    public int Seed { get => _seed; set => SetSetting(ref _seed, value); }
    public int WindowSize { get => _windowSize; set => SetSetting(ref _windowSize, value); }
    public bool RecordEnabled { get => _recordEnabled; set => SetSetting(ref _recordEnabled, value); }
    public string? RecordingPath { get => _recordingPath; set => SetSetting(ref _recordingPath, value); }
    public AlertSource AlertSource { get => _alertSource; set => SetSetting(ref _alertSource, value); }

    public string ConnectionStatus { get => _connectionStatus; private set => Set(ref _connectionStatus, value); }
    public string DeviceState { get => _deviceState; private set => Set(ref _deviceState, value); }
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public string EngineStateText => _engine.State.ToString();
    public EngineCounters Counters { get => _counters; private set => Set(ref _counters, value); }

    /// <summary>Settings are only editable while idle. A rejected change re-notifies so the view reverts.</summary>
    private void SetSetting<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!CanEditSettings)
        {
            OnPropertyChanged(name);
            return;
        }
        if (Set(ref field, value, name)) RefreshCommands();
    }

    // ---------- commands ----------

    private bool CanStart()
    {
        if (_busy || !IsIdle || !Channels.Any(c => c.IsSelected)) return false;
        return Mode == SourceMode.Emulator ? IsConnected : !string.IsNullOrEmpty(ReplayPath);
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        _busy = true;
        RefreshAll();
        try
        {
            await action();
        }
        finally
        {
            _busy = false;
            RefreshAll();
        }
    }

    private Task ConnectAsync() => RunBusyAsync(async () =>
    {
        var client = _clientFactory(new ClientOptions { Host = Host, Port = Port });
        client.StateChanged += s => _ui.Post(() => OnConnectionStateChanged(s));
        StatusMessage = $"Connecting to {Host}:{Port}…";
        try
        {
            var status = await client.ConnectAsync();
            _client = client;
            DeviceState = status.State.ToString();
            ConnectionStatus = "Connected";
            StatusMessage = $"Connected to {Host}:{Port}";
            AddLog($"connected; device {status.State}");
        }
        catch
        {
            await client.DisposeAsync();
            ConnectionStatus = "Disconnected";
            throw;
        }
    });

    private void OnConnectionStateChanged(ConnectionState s)
    {
        ConnectionStatus = s.ToString();
        if (s == ConnectionState.Disconnected) DeviceState = "—";
        // After an automatic reconnect, ask the device for its state again instead of leaving "—".
        // (The initial connect sets DeviceState itself; _client is only assigned once that succeeds.)
        if (s == ConnectionState.Connected && _client is not null) _ = RefreshDeviceStateAsync();
        RefreshAll();
    }

    private Task DisconnectAsync() => RunBusyAsync(async () =>
    {
        if (!IsIdle) await _engine.StopAsync();
        var c = _client;
        _client = null;
        if (c is not null) await c.DisposeAsync();
        ConnectionStatus = "Disconnected";
        DeviceState = "—";
        StatusMessage = "Disconnected";
    });

    public AcquisitionSettings BuildSettings() => new()
    {
        Channels = Channels.Where(c => c.IsSelected).Select(c => c.Id).ToArray(),
        RateHz = RateHz,
        Seed = Seed,
        WindowSize = WindowSize,
        AlertSource = AlertSource,
        Alerts = Channels.ToDictionary(c => c.Id, c => new ChannelAlertSettings(c.Low, c.High)),
        RecordingPath = RecordEnabled ? RecordingPath ?? DefaultRecordingPath() : null,
    };

    private static string DefaultRecordingPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TempLab",
        $"session-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.csv");

    private Task StartAsync() => RunBusyAsync(async () =>
    {
        var settings = BuildSettings();
        var problem = settings.Validate();
        if (problem is not null)
        {
            StatusMessage = "Invalid settings: " + problem;
            return;
        }
        IAcquisitionSource source = Mode == SourceMode.Emulator
            ? new EmulatorSource(_client ?? throw new InvalidOperationException("not connected"), _log.ForComponent("source"))
            : new CsvReplaySource(ReplayPath!) { Speed = ReplaySpeed };

        foreach (var c in Channels) c.ClearHistory();
        Alerts.Clear();
        await _engine.StartAsync(source, settings);
        _lastRecording = settings.RecordingPath;
        if (Mode == SourceMode.Emulator && IsConnected) DeviceState = Protocol.DeviceState.Acquiring.ToString();
        StatusMessage = $"Acquiring from {source.Description}" + (settings.RecordingPath is null ? "" : $" → recording {Path.GetFileName(settings.RecordingPath)}");
        AddLog($"started session {_engine.SessionId:N} acquisition {_engine.AcquisitionId}");
    });

    private Task StopAsync() => RunBusyAsync(async () =>
    {
        await _engine.StopAsync();
        OnUiTick(); // flush anything processed before the stop
        await RefreshDeviceStateAsync();
        var c = _engine.Counters;
        StatusMessage = $"Stopped: {c.Processed} processed, {c.Dropped} dropped, {c.GapSamples} gap samples";
        AddLog(StatusMessage + (_lastRecording is null ? "" : $"; recorded to {_lastRecording}"));
        if (_lastRecording is not null && Mode == SourceMode.Emulator)
        {
            _replayPath = _lastRecording; // offer the new recording for replay (bypasses the busy guard)
            OnPropertyChanged(nameof(ReplayPath));
        }
    });

    private Task ResetDeviceAsync() => RunBusyAsync(async () =>
    {
        if (_client is null) return;
        await _client.ResetAsync();
        await RefreshDeviceStateAsync();
        AddLog("device reset");
    });

    private async Task RefreshDeviceStateAsync()
    {
        if (_client?.State != ConnectionState.Connected) return;
        try
        {
            DeviceState = (await _client.GetStatusAsync()).State.ToString();
        }
        catch (Exception ex) when (ex is InstrumentException or OperationCanceledException or ObjectDisposedException)
        {
            AddLog("status failed: " + ex.Message);
        }
    }

    private Task ExportAsync() => RunBusyAsync(async () =>
    {
        var input = _lastRecording is not null && File.Exists(_lastRecording) ? _lastRecording : _dialogs.PickOpenCsv("Recorded session to export");
        if (input is null) return;
        var output = _dialogs.PickSaveCsv("Save processed results", Path.GetFileNameWithoutExtension(input) + "-processed.csv");
        if (output is null) return;
        var r = await ProcessedExporter.ExportAsync(input, output, BuildSettings() with { RecordingPath = null });
        StatusMessage = $"Exported {r.Rows} rows ({r.Alerts} alert rows) to {Path.GetFileName(output)}";
        AddLog(StatusMessage);
    });

    private void BrowseReplay()
    {
        var p = _dialogs.PickOpenCsv("Open recorded session");
        if (p is not null)
        {
            ReplayPath = p;
            Mode = SourceMode.Replay;
        }
    }

    private void BrowseRecording()
    {
        var p = _dialogs.PickSaveCsv("Record session to", Path.GetFileName(DefaultRecordingPath()));
        if (p is not null)
        {
            RecordingPath = p;
            RecordEnabled = true;
        }
    }

    // ---------- periodic UI update ----------

    /// <summary>Drains processed samples, alerts and events from the engine. Call on the UI thread (~10 Hz).</summary>
    public void OnUiTick()
    {
        _drain.Clear();
        _engine.DrainDisplay(_drain, MaxSamplesPerTick);
        if (_drain.Count > 0)
        {
            foreach (var l in _perChannel.Values) l.Clear();
            foreach (var s in _drain)
                if (_perChannel.TryGetValue(s.Channel, out var list)) list.Add(s);
            foreach (var c in Channels) c.Apply(_perChannel[c.Id]);
        }

        _alertDrain.Clear();
        _engine.DrainAlerts(_alertDrain);
        foreach (var a in _alertDrain)
        {
            Alerts.Insert(0, AlertViewModel.From(a));
            if (Alerts.Count > MaxAlertsShown) Alerts.RemoveAt(Alerts.Count - 1);
        }

        _eventDrain.Clear();
        _engine.DrainEvents(_eventDrain);
        bool completed = false;
        foreach (var e in _eventDrain)
        {
            AddLog($"{e.Kind}: {e.Message}");
            if (e.Kind == EngineEventKind.SourceCompleted) completed = true;
            if (e.Kind == EngineEventKind.DeviceFault) DeviceState = Protocol.DeviceState.Faulted.ToString();
            if (e.Kind == EngineEventKind.SourceInterrupted) StatusMessage = "Acquisition interrupted: " + e.Message;
        }

        Counters = _engine.Counters;
        OnPropertyChanged(nameof(EngineStateText));
        if (completed && StopCommand.CanExecute(null)) _ = StopCommand.ExecuteAsync();
    }

    private void AddLog(string line)
    {
        EventLog.Insert(0, $"{DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture)}  {line}");
        if (EventLog.Count > MaxLogLines) EventLog.RemoveAt(EventLog.Count - 1);
    }

    private void ReportError(Exception ex)
    {
        StatusMessage = "Error: " + ex.Message;
        AddLog("error: " + ex.Message);
        _log.Error("ui_command_failed", ("error", ex.ToString()));
    }

    private void RefreshCommands()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ResetDeviceCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
        BrowseReplayCommand.RaiseCanExecuteChanged();
        BrowseRecordingCommand.RaiseCanExecuteChanged();
    }

    private void RefreshAll()
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsAcquiring));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(EngineStateText));
        RefreshCommands();
    }

    public async ValueTask DisposeAsync()
    {
        await _engine.StopAsync();
        if (_client is not null) await _client.DisposeAsync();
    }
}
