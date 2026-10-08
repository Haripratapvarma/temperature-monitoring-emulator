# Measured performance

All numbers below were measured on **2026-10-07** in the environment stated with each table. They are
measurements of this build on that machine, not guarantees. Re-run them on your own hardware with the commands
shown; the CI job `benchmark-smoke` publishes a shorter run on every push.

**Environment:** Ubuntu 24.04.5 LTS container, x64, **2 logical CPUs**, .NET 8.0.31, workstation GC, GCC 13.3,
Release builds. The emulator ran in-process and streamed over localhost TCP (real sockets, framing and JSON).
This measures the acquisition/processing pipeline only — WPF rendering is **not** included (it cannot run on
Linux); on Windows the UI tick drains the same buffers.

## Windows developer PC (2026-10-07)

**Environment:** Windows 10.0.26300 (Windows 11), x64, **16 logical CPUs**, .NET 10.0.12, workstation GC,
MSVC native library, Release builds. Emulator in-process over localhost TCP; queue 256 batches, rolling window 20,
emulator batch interval 50 ms, no recording, a UI-like drain every 100 ms.

| Workload | Duration | Received = processed | Dropped | Gaps | Latency p50 / p99 / max | Managed heap start → end (2nd-half slope) | Working set start → end | GC 0/1/2 |
|---|---:|---:|---:|---:|---|---|---|---|
| **4 ch × 10 Hz** (plan baseline) | 300 s | 12,004 | 0 | 0 | 0.028 / 0.062 / 5.30 ms | 9.0 → 17.3 MiB (+4.3 KiB/s) | 38.6 → 52.4 MiB | 3/3/2 |
| 16 ch × 1 000 Hz (stress) | 60 s | 959,424 | 0 | 0 | 0.463 / 0.939 / 11.3 ms | 10.9 → 28.3 MiB (−104 KiB/s) | 39.8 → 73.2 MiB | 68/25/24 |

- **No sample loss** in either workload: every received sample was processed, nothing was dropped, and per-channel
  sequence numbers had no gaps.
- **Stress throughput** was 15,975 samples/s against a nominal 16,000. The difference (≈0.2 %) is emulator pacing
  (soft timing with `Task.Delay`) plus start-up inside the 60.1 s window — not loss: dropped = 0 and gaps = 0.
- **Max latency** values are one-off start-up/JIT and GC pauses; p99 is the representative figure.
- **Memory:** the heap slope over the second half is near zero or negative, i.e. no sustained growth. A 60 s run at
  10 Hz is too short to judge memory (only one GC occurs); use the 300 s run.

## Earlier Linux measurements

## Sustained runs

```
dotnet Benchmarks/bin/Release/net10.0/TempLab.Benchmarks.dll sustained --channels C --rate R --seconds S
```

| Workload | Duration | Processed | Dropped | Gaps | Latency p50 / p99 / max | Managed heap start → end | Working set start → end |
|---|---:|---:|---:|---:|---|---|---|
| **4 ch × 10 Hz** (plan baseline) | 300 s | 12,004 | 0 | 0 | 0.039 / 0.112 / 8.19 ms | 8.7 → 18.1 MiB | 46.1 → 72.6 MiB |
| 4 ch × 1 000 Hz | 60 s | 240,040 | 0 | 0 | 0.085 / 0.293 / 8.21 ms | 9.0 → 21.9 MiB | 46.4 → 71.3 MiB |
| 16 ch × 1 000 Hz | 60 s | 959,696 | 0 | 0 | 0.177 / 0.763 / 16.7 ms | 10.5 → 20.2 MiB | 48.0 → 91.4 MiB |

- **Sample loss:** none in any documented workload (`Received = Processed`, `Dropped = 0`, no sequence gaps).
  Received counts match `duration × rate × channels` (e.g. 300 s × 10 Hz × 4 = 12,000; +4 for the sample at t = 0).
- **Latency** is per batch, from the client receiving a frame to the worker finishing it (queue wait + native
  statistics + alert evaluation). The max values coincide with start-up/JIT and GC pauses.
- **Memory trend:** the managed-heap least-squares slope over the second half of each run was ≤ 0
  (−37.5, −54.7 and −315.7 KiB/s), i.e. no growth; chart history, display buffers and queues are bounded by design.
  Working set grows during warm-up (JIT, socket buffers, GC segments) and then plateaus.
- Configuration: queue 256 batches, rolling window 20, emulator batch interval 50 ms, no recording, a UI-like
  drain every 100 ms.

## Overload behaviour (verified by tests, not benchmarks)

With the processing worker deliberately slowed (200 ms per batch) and a 2-batch queue at 4 × 400 Hz, the engine
drops the newest batches and counts them; after stop `Received = Processed + Dropped` holds exactly
(`FaultScenarioTests.Slow_consumer_drops_newest_batches_with_counters_and_conserves_samples`).

## Native rolling statistics

```
NativeProcessing/build/bin/bench_rolling_stats                  # C++ only, tl_push_batch, 4 channels
dotnet Benchmarks/bin/Release/net10.0/TempLab.Benchmarks.dll processors   # through P/Invoke from C#
```

| Path | Window | Throughput |
|---|---:|---:|
| C++ direct | 1 | 36.6 M samples/s |
| C++ direct | 20 | 25.2 M samples/s |
| C++ direct | 200 | 23.5 M samples/s |
| C++ direct | 2 000 | 30.7 M samples/s |
| C# → P/Invoke → C++ (`NativeRollingStatsProcessor`, batch 4096) | 20 | 22.9 M samples/s (43.7 ns/sample) |
| C# naive reference (`ManagedReferenceProcessor`, O(W)) | 20 | 4.3 M samples/s (230 ns/sample) |

Throughput is roughly independent of window size because push is O(1) amortised. Batching keeps P/Invoke
overhead small (one transition per batch). The managed reference is deliberately naive — it exists to check
correctness, not as a performance comparison against optimised C#.

## Reproducing

```
cmake -S NativeProcessing -B NativeProcessing/build -DCMAKE_BUILD_TYPE=Release && cmake --build NativeProcessing/build -j
dotnet build Benchmarks -c Release
dotnet Benchmarks/bin/Release/net10.0/TempLab.Benchmarks.dll sustained --channels 4 --rate 10 --seconds 300
```

Record machine, workload and configuration alongside any number you quote.
