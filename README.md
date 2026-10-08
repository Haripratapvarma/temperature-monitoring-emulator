# TempLab — temperature monitoring application and instrument emulator

Two independently testable components that talk over localhost TCP. No physical sensors are used.

| | Project 1 — Monitoring & data logging | Project 2 — Instrument emulator & validation |
|---|---|---|
| What | WPF/MVVM desktop app: connect, select channels, acquire, view raw + rolling-mean trends, alerts, record, export, replay | C# console emulator: device state machine, deterministic 4-channel signals, fault injection, framed TCP protocol, console client |
| Emphasis | C# + C++ (native rolling statistics behind a C ABI), concurrency, bounded queues, UI batching, start/stop lifecycle | Protocol framing, state transitions, request matching/timeouts, reconnect + reconciliation, reproducible faults |
| Runs without the other? | Yes — replays recorded CSV sessions | Yes — `ConsoleClient` drives it |

Temperature is a demonstration signal; nothing here claims thermal-control, calibration, image-processing or
semiconductor-equipment expertise.

## Repository layout

```
NativeProcessing/   C++17 rolling mean/min/max library (C ABI), native tests, native micro-benchmark  (CMake)
Protocol/           Wire messages, length-prefixed framing, structured JSON-lines logging
Emulator/           Instrument emulator (console exe): server, sessions, device state machine, signals, faults
ClientLibrary/      InstrumentClient: exact-read framing, request IDs, timeouts, reconnect/backoff, reconciliation
ConsoleClient/      Interactive/scripted console client for the emulator
Core/               Acquisition engine: sources (emulator, CSV replay), bounded queue, worker, P/Invoke, alerts,
                    CSV record/replay, processed export
DesktopApp/
  Presentation/     View models (net10.0, unit-tested without a window)
  Wpf/              WPF shell: XAML, TrendChart control, dispatcher + file-dialog adapters (net10.0-windows)
Tests/              xUnit tests (managed logic, protocol, emulator integration, the six fault scenarios, view models)
Benchmarks/         Sustained-run benchmark, processor throughput, example-session recorder
Docs/               requirements.md, protocol.md, architecture.md, performance.md, scenarios/, examples/
.github/workflows/  CI: native (ASan/UBSan), Linux managed tests, Windows native+WPF+tests+publish, benchmark smoke
```

## Build

Prerequisites: CMake ≥ 3.20 and a C++17 compiler (MSVC 2022, GCC or Clang), .NET 10 SDK. WPF needs Windows.

```bash
# 1. Native library (the managed projects copy it from NativeProcessing/build/bin)
cmake -S NativeProcessing -B NativeProcessing/build -DCMAKE_BUILD_TYPE=Release
cmake --build NativeProcessing/build --config Release
ctest --test-dir NativeProcessing/build -C Release --output-on-failure

# 2a. Windows: everything, including the WPF app
dotnet build TempLab.sln -c Release
dotnet test Tests/TempLab.Tests.csproj -c Release

# 2b. Linux/macOS: everything except WPF
dotnet test TempLab.CrossPlatform.slnf -c Release
```

Offline machines (no nuget.org): the same test sources compile against a small source-compatible xUnit stand-in
and run as an executable —
`dotnet build Tests -p:UseXunitShim=true -p:RestoreConfigFile=$PWD/tools/offline-nuget.config && dotnet Tests/bin/Debug/net10.0/TempLab.Tests.dll`.
CI always uses real xUnit.

## Run

```bash
# Emulator (Project 2) — optional fault scenario, JSON-lines log
dotnet run --project Emulator -- --port 5055 --log emulator.log [--scenario Docs/scenarios/sensor-fault.json]

# Console client: interactive (type 'help') or scripted
dotnet run --project ConsoleClient -- --port 5055 --script "connect; configure 10 42; start; sleep 3; watch; stop; status"

# Desktop app (Project 1, Windows)
dotnet run --project DesktopApp/Wpf
```

## Demonstration script

1. Start the emulator. In the app: **Connect** → device `Idle`.
2. Tick **Record session to CSV**, keep four channels, **Start**. Raw (thin) and rolling-mean (thick) traces update
   10×/s; counters show received/processed/dropped/gaps.
3. Channel 3 spikes to ~37 °C at sequence 150 (15 s) → one **High** alert (hysteresis prevents repeats).
4. Inject a connection loss: restart the emulator with `--scenario Docs/scenarios/disconnect-mid-acquisition.json`,
   **Connect** and **Start** again; the link drops after 50 samples per channel. The app logs `SourceInterrupted`, reconnects with bounded backoff, queries `status` and reports
   the acquisition as `Interrupted` — it does **not** restart it.
5. **Stop**, then **Export processed…** and switch Source to **Replay**: the recording replays through the same
   engine and produces identical statistics.
6. Explain the boundary and threads: `Docs/architecture.md` (C ABI rules, thread-ownership table, overflow policy).

## Verified results (2026-10-07, Windows 11 developer PC)

Machine: Windows 10.0.26300 (Windows 11), x64, 16 logical CPUs, .NET 10.0.12, MSVC (Visual Studio Build Tools 2026), Release builds.

| Check | Result |
|---|---|
| Native tests (MSVC; also GCC/Clang and ASan + UBSan in CI) | 9 tests, 118 checks, all pass |
| Managed tests (xUnit) | 86 tests, all pass |
| WPF desktop app | Builds and runs; live acquisition, alert, record, replay and connection-loss recovery demonstrated end to end |
| Replay reproducibility | Live session and its replay: same 856 samples, same alert (Ch 3, 37.06 °C), same final statistics |
| Sustained 4 ch × 10 Hz × 300 s | 12,004 samples, 0 dropped, 0 gaps, latency p50 / p99 0.028 / 0.062 ms, heap flat (≈17 MiB) |
| Stress 16 ch × 1 kHz × 60 s | 959,424 samples, 0 dropped, 0 gaps, latency p50 / p99 0.46 / 0.94 ms |

Details, method and caveats: `Docs/performance.md`.

## How the plan's requirements map to the code

| Requirement | Where |
|---|---|
| C# and C++ | `Core`, `Emulator`, `ClientLibrary` (C#); `NativeProcessing` (C++17, C ABI, `SafeHandle` wrapper) |
| WPF, MVVM, OOD | `DesktopApp/Wpf` (views), `DesktopApp/Presentation` (view models, commands, service interfaces) |
| Multithreading | Bounded `Channel<T>` hand-off, single-owner worker, lifecycle lock, cancellation + awaited shutdown |
| Debugging / analysis | Correlated JSON-lines logs (requestId, acquisitionId, sessionId), counters, reproducible scenarios |
| Emulation & automated quality | `InstrumentDevice` state machine, `FaultInjector`, `FaultScenarioTests`, CI |
| Instrumentation-style software | Multi-channel acquisition, trends, alerts, recording, replay, export |

## Known limitations

- **No image processing.** These projects do not demonstrate it; prepare that separately if you list it.
- Soft timing only: the emulator paces with `Task.Delay` (values are timing-independent); no hard real-time claims.
- One active emulator client; JSON framing is simple, not bandwidth-optimal (~60 bytes/sample).
- Alert thresholds are fixed for the duration of an acquisition (edit while idle).
- Solo project: no evidence of code review or multi-site collaboration — keep real review history separate.
- Keep this distinct from any CUDA benchmark project.
