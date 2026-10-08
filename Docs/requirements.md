# Requirements, data contract and acceptance criteria

Milestone 1 of the build plan. Everything later in the repository is checked against this file.

## Scope rule

Temperature is a concrete demonstration signal. Nothing here claims thermal-control,
sensor-calibration, PID, image-processing or semiconductor-equipment expertise.
No physical sensors are used: data comes from the emulator (Project 2) or a recorded CSV session.

## User workflow (Project 1)

1. Choose a source: **Emulator** (host/port) or **Replay file** (CSV recorded earlier).
2. Connect (emulator) — status shows `Disconnected → Connected`, device state shows `Idle`.
3. Select channels (0–3), sample rate (default 10 Hz/channel), rolling window size and
   per-channel low/high thresholds. Settings are editable only while acquisition is idle.
4. Optionally enable recording (choose an output CSV).
5. Start. Raw and smoothed (rolling mean) traces update ~10 times per second; alerts appear in the alert list;
   counters show received / processed / dropped / gap / invalid samples.
6. Stop. Workers are cancelled and awaited; the recording file is closed.
7. Export processed results (raw + rolling mean/min/max + alert flags) from a recorded file.
8. Replay the recorded file through the same processing path; results are identical.

## Sample record

| Field | Type | Notes |
|---|---|---|
| `session_id` | GUID | Created by the desktop client per recording/monitoring session. |
| `acquisition_id` | GUID | Created by the device on `start`; changes on every start. |
| `channel_id` | int32 | 0-based. Default 4 channels. |
| `sequence` | int64 | Per channel, per acquisition, starts at 0, +1 per sample. Gaps are detected. |
| `elapsed_s` | double | Device-side monotonic time since acquisition start = `sequence / rate_hz`. |
| `temperature_c` | double | Degrees Celsius. `NaN` allowed only with quality `Invalid`. |
| `quality` | enum | `Good`, `Suspect`, `Invalid`. |

Time policy: rates and latencies use monotonic clocks (`Stopwatch`, device `elapsed_s`).
UTC wall-clock timestamps are used only for session metadata and logs.

## Signals (deterministic)

Each channel value at sequence `n` (time `t = n / rate`) is

```
value = baseline + ramp_per_s * t + amplitude * sin(2π t / period_s) + noise(seed, channel, n) + spike(n)
```

- `noise` is Gaussian from a per-channel SplitMix64 stream seeded by `(seed, channel)`, consumed strictly in
  sequence order, so the same configuration always produces the same values regardless of timer jitter.
- `spike(n) = spike_magnitude` when `spike_every > 0 && n > 0 && n % spike_every == 0`, else 0.
- Timer scheduling only paces delivery; it never changes values.

Defaults (all saved in session metadata):

| Channel | Baseline | Ramp | Periodic | Noise σ | Spikes |
|---|---|---|---|---|---|
| 0 | 25.0 °C | 0 | — | 0.05 | — |
| 1 | 20.0 °C | +0.05 °C/s | — | 0.05 | — |
| 2 | 30.0 °C | 0 | ±2.0 °C, 20 s | 0.05 | — |
| 3 | 22.0 °C | 0 | — | 0.05 | +15 °C every 150 samples |

## Processing

- Per-channel rolling window of the last `W` **valid** samples (default `W = 20`, `1 ≤ W ≤ 100000`).
- Outputs: mean, min, max, count, `is_warm` (`count == W`).
- **Warm-up:** statistics are reported from the first sample but flagged not-warm until `W` samples.
  Alerts based on the rolling mean are suppressed during warm-up; raw-value alerts are not.
- **Invalid-data policy:** a sample is invalid if quality is `Invalid`, or the value is NaN/±∞, or outside
  `[-273.15, 2000]` °C. Invalid samples are counted, recorded, shown as gaps in the trace, and **not** added to
  the window; the last valid statistics are kept.
- **Sequence gaps:** if `sequence > expected`, the missing count is added to the gap counter and an event is
  logged. Out-of-order/duplicate sequences are counted as invalid.

## Alerts

- Per channel `low` and `high` thresholds and a hysteresis `h` (default 0.5 °C); source is raw value
  (default) or rolling mean.
- **High:** raised when the value goes from `≤ high` to `> high` while armed. Re-arms when value `< high − h`.
- **Low:** raised when the value goes from `≥ low` to `< low` while armed. Re-arms when value `> low + h`.
- Both alerts start armed, so a first reading already beyond a threshold raises an alert.
- A value exactly equal to a threshold does not alert.
- A continuously high reading therefore raises exactly one alert.
- Thresholds are read when acquisition starts; edits are only possible while idle.

## Acceptance criteria

| # | Criterion | Verified by |
|---|---|---|
| A1 | Native rolling stats match a naive reference on random sequences, window 1, warm-up, reset, invalid values. | `NativeProcessing/tests`, `Tests/RollingStatsTests` |
| A2 | No C++ exception crosses the C ABI; all entry points return status codes. | Native tests (bad args, null pointers) |
| A3 | Duplicate start is rejected; 20 repeated start/stop cycles leave no running worker. | `AcquisitionEngineTests` |
| A4 | Queue overflow follows the documented policy (drop newest batch) and is counted. | `AcquisitionEngineTests.SlowConsumer...` |
| A5 | CSV round-trips under a non-invariant culture; replay reproduces live statistics exactly. | `CsvSessionTests`, `ReplayTests` |
| A6 | Every protocol command has a documented ack/error in every device state. | `Docs/protocol.md`, `DeviceStateMachineTests` |
| A7 | Each of the six required fault scenarios is reproducible and its recovery outcome is asserted. | `FaultScenarioTests` |
| A8 | Settings cannot change while acquiring; chart history is capped. | `MainViewModelTests` |
| A9 | A sustained run reports throughput, loss, latency and memory trend on stated hardware. | `Benchmarks`, `Docs/performance.md` |
