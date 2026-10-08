# Instrument emulator protocol (v1)

## Framing

```
+----------------------+-------------------------------+
| length: uint32 (LE)  | UTF-8 JSON, exactly `length`  |
+----------------------+-------------------------------+
```

- `1 ≤ length ≤ 1 048 576` (1 MiB, `FrameCodec.DefaultMaxFrameBytes`). A zero or larger length is a protocol
  error; the receiver rejects it **before allocating** and closes the connection.
- Readers loop until exactly 4 header bytes and exactly `length` body bytes are read (partial reads are normal).
  EOF before any header byte is a clean close; EOF mid-frame is a protocol error.
- A sender writes header and body as one buffer through a single writer, so frames never interleave.

## Envelope

```json
{ "v": 1, "type": "start", "id": "c-17", "payload": { } }
```

| Field | Meaning |
|---|---|
| `v` | Protocol version. Server rejects anything other than `1` with `UnsupportedVersion`. |
| `type` | Message type (below). |
| `id` | Request ID chosen by the client; echoed by the response. `null` for events. |
| `payload` | Type-specific object (may be omitted). |

### Message types

| Direction | Type | Payload |
|---|---|---|
| C→S | `configure` | `DeviceConfiguration` (channels, rate, seed, per-channel signal settings) |
| C→S | `start` | none |
| C→S | `stop` | none |
| C→S | `status` | none |
| C→S | `reset` | none |
| C→S | `setFaults` | `FaultScenario` |
| S→C | `response` | `{ ok, error?: { code, message }, result? }` — same `id` as the request |
| S→C | `samples` | `{ acquisitionId, samples: [ { ch, seq, t, v, q } ] }` (event) |
| S→C | `deviceFault` | `{ acquisitionId, code, message }` (event) |

## Device states and command table

Device states: `Idle`, `Acquiring`, `Faulted`. Transport state (`Connected`/`Disconnected`) is tracked
separately by the client and never stored as a device state.

| Command | Idle | Acquiring | Faulted |
|---|---|---|---|
| `configure` | validate → apply → `ok` (stays Idle); invalid → `InvalidConfiguration` | `InvalidState` | `InvalidState` |
| `start` | → Acquiring, `ok { acquisitionId }` | `InvalidState` (no duplicate start) | `InvalidState` |
| `stop` | `ok { wasAcquiring: false }` | cancel generator, **await it**, → Idle, then `ok { wasAcquiring: true, samplesSent }` | `ok { wasAcquiring: false }` (stays Faulted) |
| `status` | `ok { status }` | `ok { status }` | `ok { status }` (includes fault) |
| `reset` | `ok` (stays Idle) | cancel + await generator, → Idle, `ok` | clear fault → Idle, `ok` |
| `setFaults` | validate → `ok` | `InvalidState` | `InvalidState` |
| unknown type | `UnknownCommand` | `UnknownCommand` | `UnknownCommand` |

Because `stop` acknowledges only after the generator task has completed, no `samples` frame for that
acquisition can arrive after the stop response.

Configuration is retained across `reset`. A new `acquisitionId` is issued on every `start`.

### Error codes

`InvalidState`, `InvalidConfiguration`, `UnknownCommand`, `UnsupportedVersion`, `BadRequest`, `Busy`, `Internal`.

### Configuration validation

| Field | Rule |
|---|---|
| `channels` | 1–16 distinct ids in `0..15`; each with a signal |
| `rateHz` | 1–1000 |
| `noiseStd` | ≥ 0 |
| `periodS` | > 0 when `amplitude ≠ 0` |
| `spikeEvery` | ≥ 0 |
| `seed` | any int32 |

## Connections

- One active client. A second client receives `response { ok:false, error: Busy }` with `id: null` and is closed.
- **Disconnect while Acquiring:** the device cancels and awaits the generator, returns to `Idle` and logs
  `acquisition_abandoned`. It never resumes that acquisition.
- **Client recovery:** reconnect with bounded exponential backoff (`ReconnectPolicy`), send `status`,
  reconcile. If the device is not acquiring the client's acquisition ID, the client marks the acquisition
  `Interrupted` and records a gap. The client **never** re-sends `start` automatically.

## Fault injection

A `FaultScenario` (JSON, see `Docs/scenarios/`) has a `name`, `seed` and a list of faults. Triggers are
expressed as sample sequence numbers so they are deterministic.

| Fault `kind` | Parameters | Device behaviour |
|---|---|---|
| `DelayReply` | `command`, `delayMs`, `count` | Response to the next `count` matching commands is sent after `delayMs`. Other traffic is not blocked. |
| `Disconnect` | `afterSequence` | Closes the socket once `afterSequence` samples per channel have been sent. |
| `AcquisitionFault` | `afterSequence`, `code` | Enters `Faulted`, sends `deviceFault`, stops streaming. |
| `MalformedFrame` | `afterSequence` | Sends a correctly framed body that is not JSON. |
| `OversizedFrame` | `afterSequence` | Sends a header announcing a 64 MiB body, then closes. |
| `InvalidReadings` | `channel`, `afterSequence`, `count` | That channel reports `NaN` with quality `Invalid`. |
| `SkipSequences` | `afterSequence`, `count` | Sequence numbers are skipped (gap) on all channels. |

Scenarios can be loaded at emulator start (`--scenario file.json`) or with `setFaults`; the active scenario
is logged at the start of each acquisition so a failing run can be repeated exactly.
