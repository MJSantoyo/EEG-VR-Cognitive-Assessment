# EEG Pipeline — Technical & Scientific Documentation

**PROJECT:**
IKEA_EEG

**DOCUMENT TYPE:**
Independent read-only reconstruction of implemented EEG pipeline

**STATUS:**
Code-derived unless explicitly labelled literature/background.

**Reconstruction date:** 2026-08-24
**Method:** direct inspection of `Assets/IKEA_EEG/Scripts/Data/*.cs`, `Assets/IKEA_EEG/Editor/*.cs`,
`Assets/IKEA_EEG/Editor/*.ps1`, `Assets/Plugins/LSL.cs`. No source file was modified.
**Rule applied throughout:** where the code does not support a claim, the claim is not made.
Where a number appears in this document, its source is named — a line of code, a self-test
assertion, a diagnostic report, or a cited paper.

---

## Reader's guide

Every processing stage below is documented against the same nine questions:

| # | Question |
|---|---|
| 1 | WHAT enters |
| 2 | WHY the stage exists |
| 3 | WHICH code performs it |
| 4 | HOW it works |
| 5 | WHAT mathematics are involved |
| 6 | WHAT parameters are used |
| 7 | WHAT comes out |
| 8 | HOW it has been validated |
| 9 | WHAT assumptions remain / WHAT could invalidate it |

Three labels are used consistently and mean different things:

- **[CODE]** — read directly out of a source file in this project. Reproducible by opening the file.
- **[LIVE]** — a number produced by running a diagnostic against real AURA hardware, recorded in
  `CLAUDE_HANDOFF.md`. Not re-verifiable from source alone; treat as a historical measurement.
- **[LIT]** — background from the scientific literature. Not a property of this codebase.

A fourth label appears where it matters:

- **[GAP]** — something a reader might reasonably expect to exist that does **not** exist in the
  code as inspected.

---

# PART 1 — SYSTEM ARCHITECTURE

## 1.1 The complete path, arrow by arrow

Every arrow below names the exact file and, where useful, the exact member that performs it.

| # | Stage transition | Performed by |
|---|---|---|
| 1 | EEG headset → AURA | External. AURA acquisition application, `hostname=laptop-san` **[LIVE]**. No project code. |
| 2 | AURA → LSL | External. AURA publishes an LSL outlet named exactly `AURA`. |
| 3 | LSL → Unity (library binding) | `Assets/IKEA_EEG/Scripts/Data/LslBinding.cs` — late-bound by reflection over `AppDomain.CurrentDomain.GetAssemblies()`. Native library at `Assets/Plugins/lib/lsl.dll`, managed wrapper `Assets/Plugins/LSL.cs`. |
| 4 | Unity → stream discovery | `LslBinding.ResolveStreamInfo(name, timeout, out detail)` → `resolve_stream("name", "AURA", 1, timeout)`. |
| 5 | discovery → receiver | `Assets/IKEA_EEG/Scripts/Data/AuraLslReceiver.cs`, `Connect(out problem)`. |
| 6 | receiver → raw sample | `AuraLslReceiver.TryPullOne()` → `LslBinding.TryPullNumericSample(...)` → struct `RawEegSample`. |
| 7 | raw sample → clock synchronization | `AuraLslReceiver.RefreshTimeCorrection(bool)` → `LslBinding.TryGetTimeCorrection(...)`; applied in `TryPullOne` as `localRaw = remoteTimestamp + m_TimeCorrection`. |
| 8 | clock sync → analysis timebase | `Assets/IKEA_EEG/Scripts/Data/EegAnalysisTimebase.cs`, `Add(localRawTimestamp)`. |
| 9 | analysis timebase → raw buffer | `Assets/IKEA_EEG/Scripts/Data/RawEegRingBuffer.cs`, fed by `AuraLslReceiver.Accept()` → `buffer?.Add(sample.analysisTimestamp, sample.channels)`. |
| 9b | raw sample → disk | `Assets/IKEA_EEG/Scripts/Data/RawEegRecorder.cs`, `Write(...)`, lifecycle owned by `EegRunRecorder.cs`. |
| 10 | raw sample → preprocessing | `Assets/IKEA_EEG/Scripts/Data/EegFeaturePipeline.cs`, `OnSample(RawEegSample)`, subscribed to `AuraLslReceiver.sampleReceived`. |
| 11 | preprocessing (the filter itself) | `Assets/IKEA_EEG/Scripts/Data/EegBandpassFilter.cs`, `Process(channel, sample)`. |
| 12 | preprocessing → filtered buffer | `EegFeaturePipeline.m_Filtered` — a **second** `RawEegRingBuffer`, indexed on the same `analysisTimestamp`. |
| 13 | filtered buffer → spectral window | `EegFeaturePipeline.AnalyzeLatestWindow(seconds)` → `m_Filtered.TryGetWindow(...)`. |
| 14 | spectral window → Welch PSD | `Assets/IKEA_EEG/Scripts/Data/EegSpectralAnalyzer.cs`, `Welch(samples, fs, segmentSeconds, overlapFraction, fftLength)`. |
| 15 | PSD → theta / alpha | `EegSpectralAnalyzer.BandPower(PsdResult, EegBand)`, with `EegBand.Theta = 4–8 Hz`, `EegBand.Alpha = 8–12 Hz`. |
| 16 | per-channel band power → ROI features | `EegFeaturePipeline.ComputeRoi(features)` using `AuraMontageConfig.ResolveRoi(name, out problem)`. |
| 17 | ROI features → baseline infrastructure | `EegFeaturePipeline.CaptureBaseline(start, end, out detail)` and the `deltaThetaDb` / `deltaAlphaDb` computation inside `AnalyzeLatestWindow`. |
| 18 | features → researcher visualization | `Assets/IKEA_EEG/Editor/EegResearcherMonitor.cs` (live window) and `Assets/IKEA_EEG/Editor/EegSpectralDiagnostics.cs` (one-shot text report). |

**Montage, sitting beside stages 16–18:** `Assets/IKEA_EEG/Scripts/Data/AuraMontageConfig.cs`.

**Event/marker side channel (parallel, carries no EEG):**
`Assets/IKEA_EEG/Scripts/Data/LslClock.cs` stamps every experiment event with `local_clock()`;
`Assets/IKEA_EEG/Scripts/Data/LslMarkerSink.cs` publishes an outbound marker stream
`IKEA_EEG_Markers`. These are what make an EEG window and an experiment event comparable.

## 1.2 ASCII architecture diagram

```
 +--------------------------------------------------------------------------------------+
 | EXTERNAL - not this project                                                          |
 |                                                                                      |
 |   EEG headset (8 electrodes)                                                         |
 |        |  analogue -> ADC                                                            |
 |        v                                                                             |
 |   AURA acquisition app        host = laptop-san            [LIVE]                    |
 |   notch OFF, bandpass OFF     <- HUMAN-VERIFIED from the AURA UI, NOT from the stream|
 |        |                                                                             |
 |        v                                                                             |
 |   LSL outlet  name="AURA"  type="EEG"  8 ch  250 Hz  float32  desc=<EMPTY>           |
 +--------+-----------------------------------------------------------------------------+
          |  liblsl transport, delivered in 9-sample chunks                    [LIVE]
 =========+============== process / machine boundary ===================================
          v
 +--------------------------------------------------------------------------------------+
 | UNITY                                                                                |
 |                                                                                      |
 |  LslBinding.cs -- reflection --> Assets/Plugins/LSL.cs --> lsl.dll                    |
 |      |  ResolveStreamInfo("AURA", 3 s)                                               |
 |      |  TryReadStreamInfo -> StreamMetadata{name,type,channelCount,nominalSrate,      |
 |      |                                      channelFormat,sourceId}                  |
 |      |  CreateInlet       -> StreamInlet  (post-processing NEVER set => proc_none)    |
 |      |  TryGetTimeCorrection(2 s on connect, 0.2 s on refresh, every 5 s)             |
 |      v                                                                               |
 |  +----------------- AuraLslReceiver.cs   (exactly ONE per project) ---------------+   |
 |  |  Drain(max = 64 per frame, pull timeout = 0 s -> never blocks a frame)         |   |
 |  |  TryPullOne():                                                                 |   |
 |  |      remote    <-- pull_sample()                  (AURA's own clock domain)     |   |
 |  |      localRaw   =  remote + timeCorrection        (this machine's LSL clock)    |   |
 |  |      analysis   =  EegAnalysisTimebase.Add(localRaw)   -- uniform grid          |   |
 |  |      channels[] =  copy of scratch buffer (never aliased to the next pull)      |   |
 |  |  => RawEegSample{ remoteLslTimestamp, timeCorrection, lslTimestamp,             |   |
 |  |                   analysisTimestamp, channels[] }                               |   |
 |  +--------+--------------------+---------------------------+---------------------+   |
 |           | Accept()           |                           |                         |
 |           v                    v                           v                         |
 |  RawEegRingBuffer        RawEegRecorder             event sampleReceived              |
 |  RAW, 60 s x 250 Hz      raw_eeg.csv                (the extension point)             |
 |  = 15000 samples         4 time cols + ch1..chN            |                          |
 |  indexed on analysisTs   NO arithmetic on values           |                          |
 |           |                                                |                          |
 |           | TryGetWindow(eventTs, pre, post)               |                          |
 |           |   -> synchronization QA path (1 s pre / 2 s post)                          |
 |           v                                                v                          |
 |  EegDiagnostics.cs              +------ EegFeaturePipeline.cs ---------------------+   |
 |  (Editor menu)                  | OnSample():   PER SAMPLE                         |   |
 |                                 |   EegBandpassFilter.Process(c, x)                |   |
 |                                 |   HP 1 Hz (4th order) + LP 40 Hz (4th order)     |   |
 |                                 |   = 4 biquads, Direct Form I,                    |   |
 |                                 |     independent state per channel                |   |
 |                                 |        |                                         |   |
 |                                 |        v                                         |   |
 |                                 |   FILTERED RingBuffer (30 s)                     |   |
 |                                 |   indexed on the SAME analysisTimestamp          |   |
 |                                 |        |                                         |   |
 |                                 |  AnalyzeLatestWindow():   ON DEMAND ONLY          |   |
 |                                 |        v                                         |   |
 |                                 |   OUTER WINDOW = most recent 4.0 s               |   |
 |                                 |        |                                         |   |
 |                                 |        +-> AssessChannel      (WITHIN a channel) |   |
 |                                 |        |     NaN / Inf / flatline / saturation / |   |
 |                                 |        |     discontinuity / dynamic range       |   |
 |                                 |        |                                         |   |
 |                                 |        +-> EegSpectralAnalyzer.Welch             |   |
 |                                 |        |     segment 2.0 s = 500 samples         |   |
 |                                 |        |     overlap 50% -> step 250 samples     |   |
 |                                 |        |     periodic Hann, de-meaned            |   |
 |                                 |        |     FFT N = 512 (radix-2, zero-padded)  |   |
 |                                 |        |     -> 3 segments averaged              |   |
 |                                 |        |     -> one-sided PSD, 257 bins          |   |
 |                                 |        |                                         |   |
 |                                 |        +-> BandPower (trapezoid, x bin spacing)  |   |
 |                                 |        |     theta 4-8 Hz, alpha 8-12 Hz         |   |
 |                                 |        |     -> thetaPerChannel[8]               |   |
 |                                 |        |        alphaPerChannel[8]               |   |
 |                                 |        |                                         |   |
 |                                 |        +-> AssessInterChannelIdentity            |   |
 |                                 |        |     (BETWEEN channels - the only such)  |   |
 |                                 |        |                                         |   |
 |                                 |        +-> ComputeRoi  <-- AuraMontageConfig     |   |
 |                                 |              FRONTAL_THETA   = mean(F3,Fz,F4)    |   |
 |                                 |              POSTERIOR_ALPHA = mean(P3,Pz,P4)    |   |
 |                                 |                    |                             |   |
 |                                 |              exploratory ratio + log ratio        |   |
 |                                 |              baseline delta dB (only if captured) |   |
 |                                 |                    v                             |   |
 |                                 |              LatestEegFeatures  ----------------+ |   |
 |                                 +------------------------------------------------|-+   |
 +----------------------------------------------------------------------------------|----+
                                                                                     |
 +-----------------------------------------------------------------------------------v---+
 | EDITOR (desktop only, never in the headset) - READ-ONLY VIEWS                           |
 |   EegResearcherMonitor.cs   - FindAnyObjectByType, reads pipeline.latest, 15 Hz repaint |
 |   EegSpectralDiagnostics.cs - builds its OWN temporary receiver+pipeline, one-shot text |
 |   EegDiagnostics.cs         - buffer check / event window / timestamp continuity        |
 +-----------------------------------------------------------------------------------------+

 PARALLEL EVENT CHANNEL (makes epoching possible; carries no EEG)
   LslClock.Now()  -- liblsl local_clock() --> event CSV column `lsl_timestamp`
   LslMarkerSink   -- outlet "IKEA_EEG_Markers" --> payload "EVENT_TYPE|key=value|..."
```

## 1.3 The single most important architectural fact **[CODE]** **[GAP]**

`EegFeaturePipeline.AnalyzeLatestWindow()` has exactly **one caller in the entire project**:
`Assets/IKEA_EEG/Editor/EegSpectralDiagnostics.cs:110`, which is an Editor menu command
(`IKEA_EEG ▸ EEG ▸ Analyze Spectral Window`).

There is no `Update()`, no coroutine, no timer and no experiment hook that calls it at runtime.
Verified by exhaustive search across `Assets/**/*.cs`.

Consequences, stated plainly:

- Per-sample **filtering does** run continuously in a playing scene — `OnSample` is subscribed in
  `OnEnable`, and `ExperimentSceneBuilder` adds `EegFeaturePipeline` to the `EegAcquisition`
  GameObject. The filtered ring buffer genuinely fills.
- **Spectral features are never computed during a live experiment run.** `pipeline.latest` stays
  `null` unless the Editor diagnostic is invoked.
- `EegResearcherMonitor` therefore shows live *traces* (it reads the two ring buffers directly)
  but its SPECTRAL FEATURES panel displays "no window analysed yet" during an ordinary Play-Mode
  session, because nothing has populated `latest`.
- `EegFeaturePipeline.CaptureBaseline(...)` has **zero callers anywhere in the project**. The
  baseline infrastructure exists and is reachable, but nothing in the experiment invokes it.

This is not a defect inside any one file; it is a missing wire between the pipeline and the
experiment lifecycle. It must be understood before reading any statement about "live features"
elsewhere in this document.

## 1.4 Scene wiring **[CODE]**

`Assets/IKEA_EEG/Editor/ExperimentSceneBuilder.cs:1490–1513` constructs, under
`ExperimentRoot`, a single GameObject named `EegAcquisition` carrying three components:

| Component | Configuration set by the builder |
|---|---|
| `AuraLslReceiver` | `resolveTimeoutSeconds = 3`, `receiveContinuously = true`, `bufferSamples = true`, `bufferSeconds = 60` |
| `EegRunRecorder` | `connectOnStart = true`, `recordToDisk = true`, `fileName = "raw_eeg.csv"` |
| `EegFeaturePipeline` | `montage = null` (falls back to `AuraMontageConfig.CreateHumanVerifiedDefault()`), `highPassHz = 1.0`, `lowPassHz = 40.0`, `windowSeconds = 4.0` |

`EegRunRecorder` is found by `ExperimentManager` at `ExperimentManager.cs:3809`
(`FindAnyObjectByType<EegRunRecorder>()`), and receives exactly two lifecycle calls:
`BeginRun(...)` at `ExperimentManager.cs:446` and `EndRun()` at `ExperimentManager.cs:3535`.
The manager knows nothing about streams, filters or spectra.
---

# PART 2 — LSL TRANSPORT

## 2.1 What enters and why the stage exists

**Enters:** nothing from Unity's side except a stream name. **Leaves:** a live `StreamInlet` plus
a `StreamMetadata` record describing the amplifier's actual configuration.

**Why it exists:** Lab Streaming Layer (LSL) is the transport standard for time-synchronised
biosignal streaming. It solves two problems this project would otherwise have to solve badly:
network discovery of a device it does not know the address of, and timestamping in a way that can
later be reconciled across two machines. **[LIT]** The canonical reference is Kothe et al. (2024),
*Lab Streaming Layer*, and the `sccn/liblsl` documentation.

## 2.2 The binding strategy — reflection, not a compile-time reference **[CODE]**

`LslBinding.cs` never contains `using LSL;`. Every LSL type is discovered at run time:

```
Probe()  →  foreach assembly in AppDomain.CurrentDomain.GetAssemblies()
              find a type named "StreamOutlet" that has a method "push_sample"
              then bind alongside it: StreamInfo, StreamInlet, channel_format_t
```

The stated rationale in the file header is that the project must compile, build and run on the
Quest whether or not liblsl is installed. The scientific consequence worth noting: **there is no
compile-time guarantee that the LSL API is present**. Every failure mode is a runtime state, and
each is reported as a distinct string rather than an exception.

Two API layouts are explicitly supported: `LSL.StreamInfo` / `LSL.StreamOutlet`
(liblsl-Csharp 1.13+) and the older `LSL.liblsl.*` nesting.

Late-binding subtlety documented in the file: C# optional parameters do not exist at the
reflection layer, so overload selection matches on the **first** parameter type only and fills
remaining arguments from each parameter's declared default (`ChooseOverload`, `BuildArgs`).

## 2.3 Stream discovery **[CODE]**

`LslBinding.ResolveStreamInfo(streamName, timeoutSeconds, out detail)`:

- Calls the static `resolve_stream` with `args[0] = "name"`, `args[1] = streamName`, a minimum
  count of 1 and the supplied timeout.
- Resolution is **by exact name**. `AuraLslReceiver.StreamName` is the compile-time constant
  `"AURA"`. The header comment states the reason explicitly: AURA also publishes derived streams
  (filtered signals, power spectra) and this project must begin from the raw signal so its own
  preprocessing is known and reportable.
- The timeout is **finite by construction** — `m_ResolveTimeoutSeconds`, a serialized field
  clamped to `[0.5, 15]`, default 3 s, set to 3 s by the scene builder and 4 s by the diagnostics.
  There is no code path that requests `FOREVER`.
- Returns the **first** matching stream (`results.GetValue(0)`). If two machines both publish a
  stream named AURA, the choice is arbitrary and unreported. **[GAP]**

`LslBinding.ResolveAllStreams(timeout, out detail)` exists for diagnostics only: it enumerates
every visible stream so that "AURA was not found" can be reported alongside what *was* found.

## 2.4 StreamInlet architecture **[CODE]**

`LslBinding.CreateInlet(streamInfo, out detail)`:

1. Invokes the `StreamInlet(StreamInfo)` constructor with defaults supplied for every optional
   parameter.
2. If the binding exposes `open_stream`, calls it (also with declared defaults). The comment
   notes this is optional because `pull_sample` opens implicitly.
3. Returns the inlet as a bare `object`; the receiver holds it opaquely.

**Post-processing flags: `proc_none`.** `Assets/Plugins/LSL.cs:541` exposes
`lsl_set_postprocessing`, and `LSL.cs:233–241` defines `proc_clocksync = 1`, `proc_dejitter = 2`,
`proc_monotonize = 4`. **No file under `Assets/IKEA_EEG/` ever calls it.** Verified by grep. The
inlet therefore runs with liblsl's default of no post-processing: timestamps arrive exactly as
`pull_sample` produces them, un-dejittered and un-monotonised. This is deliberate — see PART 3.

## 2.5 Sampling: the drain loop **[CODE]**

`AuraLslReceiver.Update()` → `Drain(m_MaxSamplesPerFrame)` when `m_ReceiveContinuously`.

| Parameter | Value | Where |
|---|---|---|
| Pull timeout | **0 seconds**, always | `Drain` passes `0d` to `TryPullOne` |
| Max samples per frame | 64 (range 1–512) | `m_MaxSamplesPerFrame` |
| Recent diagnostic ring | 64 samples (range 1–1024) | `m_RecentSampleCapacity` |

The zero timeout is the frame-safety guarantee: a poll returns immediately whether or not data is
waiting, so an amplifier that stops mid-session slows nothing down. `TryReceiveOne(timeout, ...)`
exists as a *blocking* variant used only by Editor diagnostics with small timeouts (2.0 s in
`CheckBufferReport`).

`LslBinding.TryPullNumericSample` has a documented design property: **there is no way to request
an indefinite wait through it.** `Math.Max(0d, timeoutSeconds)` is applied and the header states
that LSL's FOREVER on the main thread would hang the Editor.

Two "no data" conditions are treated identically and as **normal, not errors**:
- `pull_sample` returns timestamp `0d`;
- some liblsl builds throw `TimeoutException`, caught and converted to `return false` with an
  empty error string.

## 2.6 What the stream advertises — LSL-derived facts **[CODE] + [LIVE]**

`LslBinding.TryReadStreamInfo` reads six properties by reflection, each from the live
`StreamInfo` object:

| Field | Source getter | Live value **[LIVE]** |
|---|---|---|
| `name` | `name()` | `AURA` |
| `type` | `type()` | `EEG` |
| `channelCount` | `channel_count()` | `8` |
| `nominalSrate` | `nominal_srate()` | `250` |
| `channelFormat` / `channelFormatValue` | `channel_format()` | `cf_float32` (value 1) |
| `sourceId` | `source_id()` | `AuraLSL-<timestamp>` |

`hasRegularRate => nominalSrate > 0d`. A rate of exactly 0.0 is LSL's `IrregularRate` constant.

`TryReadStreamInfo` **fails** (returns false) if `channelCount <= 0`. It does *not* fail on a
zero rate — an irregular stream is handled downstream by falling back to a fixed sample capacity
and disabling gap detection.

### Sample format mapping **[CODE]**

`LslBinding.ChannelElementType(int)`:

| `channel_format_t` value | C# element type | Supported? |
|---|---|---|
| 1 `cf_float32` | `float` | yes — **this is what AURA uses** |
| 2 `cf_double64` | `double` | yes |
| 4 `cf_int32` | `int` | yes |
| 5 `cf_int16` | `short` | yes |
| 3 `cf_string`, 6 `cf_int8`, 7 `cf_int64`, 0 `cf_undefined` | — | **no**, reported as `UnsupportedFormat` |

Self-test assertions at `ExperimentSelfTest.cs:1493–1501` verify all four supported mappings and
both unsupported cases.

Note the precision path: AURA sends **float32**; `TryPullNumericSample` allocates a `float[]`,
then widens each element to `double` via `Convert.ToDouble`. Everything downstream is `double`.
Widening float32→double64 is exact, so no precision is lost — but no precision is *gained*
either: the effective resolution of every value is float32's ~7 decimal digits.

## 2.7 The sample callback **[CODE]**

`AuraLslReceiver` exposes `public event Action<RawEegSample> sampleReceived`, invoked at the end
of `Accept(sample)`. This is the documented extension point and the mechanism by which
`EegFeaturePipeline` attaches without the receiver knowing it exists.

Order of operations inside `Accept`, which matters for reasoning about what sees what:

1. `samplesReceived++`, `lastSample = sample`, `state = Receiving`
2. `buffer?.Add(sample.analysisTimestamp, sample.channels)` — the raw ring
3. `recorder?.Write(analysis, local, remote, correction, channels)` — the CSV
4. `m_Recent.Enqueue(sample)`, trimmed to capacity
5. `sampleReceived?.Invoke(sample)` — the feature pipeline

Consequence: the ring buffer and the disk record are always at least as current as any subscriber.

**Threading note.** `RawEegRingBuffer` takes a lock on every public member and its header
anticipates writes from "whichever thread drains the LSL inlet". In the *current* wiring the
drain happens on Unity's main thread from `Update()`, so there is in practice no cross-thread
contention on the buffer. The one genuinely concurrent component is `RawEegRecorder`, which
pushes onto a `ConcurrentQueue` drained by a dedicated background writer thread.

## 2.8 Marker stream — relevant, and outbound only **[CODE]**

`LslMarkerSink.cs` publishes an **outlet** (not an inlet):

| Property | Value |
|---|---|
| Stream name | `IKEA_EEG_Markers` |
| Stream type | `Markers` |
| Source ID | `IKEA_EEG_Unity_Markers` |
| Channel count | 1 |
| Rate | `IrregularRate` (0.0) |
| Format | `cf_string` |
| Payload | `EVENT_TYPE|key=value|...` |

Set by `ExperimentSceneBuilder.cs:1481`. It is the mechanism by which an external recorder (e.g.
LabRecorder) could capture experiment events in the same XDF file as the EEG. **It is not used by
the in-Unity analysis path** — Unity aligns events to EEG through the `lsl_timestamp` CSV column
produced by `LslClock`, not through this outlet.

`LslMarkerSink` is defensive: a `push_sample` that throws disables further pushes and records the
reason, rather than propagating the exception into the experiment.

## 2.9 What AURA provides, and what it does NOT

### AURA DOES provide (via LSL StreamInfo) **[LIVE, verified by pulling the full StreamInfo XML]**

- stream name `AURA`
- stream type `EEG`
- channel count `8`
- nominal sampling rate `250` Hz
- channel format `cf_float32`
- source ID `AuraLSL-<timestamp>`
- hostname `laptop-san`

### AURA does NOT provide — the `<desc/>` element is EMPTY **[LIVE]**

The recorded live finding is that `<desc/>` is empty on **all three** AURA streams. Therefore the
stream carries **none** of the following:

| Missing metadata | Consequence |
|---|---|
| Channel labels (Fp1, F3, …) | Electrode identity cannot be verified from the stream. The montage is human-typed. |
| Physical units (µV, V, counts) | Amplitude scaling is unknown. See PART 7. |
| Acquisition filter state (notch, bandpass) | Cannot be verified from the stream. Human-typed. See PART 6. |
| Reference / ground electrode | Unknown. No re-referencing is or can be performed. |
| Impedance or contact quality | Unavailable. No impedance-based rejection is possible. |
| Channel order guarantee | Channel *count* is verified; channel *order* is not. |

`AuraLslReceiver`'s `RawEegSample.channels` is documented as "deliberately unlabelled" for exactly
this reason, and `RawEegRecorder` writes `ch1..chN` rather than electrode names — the header
comment calls a guessed montage "the single most damaging thing this file could get wrong."

## 2.10 LSL-derived facts vs. human-verified configuration

This distinction is formalised in code as the enum `EegConfigSource` in `AuraMontageConfig.cs`:

```csharp
Unknown = 0,
LslStreamMetadata = 1,              // travels with the data; cannot drift from it
HumanVerifiedAcquisitionUi = 2,     // read off the AURA UI by a person and typed in
```

| Fact | Source | Self-verifying? |
|---|---|---|
| 8 channels | `LslStreamMetadata` | **Yes** |
| 250 Hz | `LslStreamMetadata` | **Yes** |
| float32 | `LslStreamMetadata` | **Yes** |
| type = EEG | `LslStreamMetadata` | **Yes** |
| CH1=Fp1 … CH8=P4 | `HumanVerifiedAcquisitionUi` | **No** |
| Notch OFF | `HumanVerifiedAcquisitionUi` | **No** |
| Bandpass OFF | `HumanVerifiedAcquisitionUi` | **No** |
| Amplitude units | **Unknown** — neither source establishes them | n/a |

The practical difference: an LSL-derived fact changes automatically if the amplifier is
reconfigured, because it is read from the live stream on every connect. A human-verified fact
becomes **silently wrong** the moment somebody changes the cap or the amplifier settings without
editing `AuraMontageConfig`. `AuraMontageConfig.verificationNote` says this in the asset itself.

The one automatic cross-check that exists is `AuraMontageConfig.Validate(streamChannelCount, ...)`,
which refuses an 8-electrode montage against a 6-channel stream. It catches a change in channel
*count*; it cannot catch a change in channel *order*. **[GAP]**

## 2.11 Validation and remaining assumptions for PART 2

**Validated [CODE]:** format-mapping table (`ExperimentSelfTest.cs:1493–1501`); metadata reading
against a self-created loopback outlet with a deliberately unusual channel count and rate
(`ExperimentSelfTest.cs:1576–1591`, asserting `type == "EEG"`, exact channel count, exact format
string, exact source id); the receiver allocating buffers from `meta.channelCount` rather than a
constant (`ExperimentSelfTest.cs:1448` greps the source for `new double[meta.channelCount]`).

**Validated [LIVE]:** discovery, metadata read, and 1000–2200 samples received per multi-second
capture.

**Assumptions that remain:**
1. The first stream named `AURA` is the right one.
2. `cf_float32` values are numerically meaningful without scaling (see PART 7).
3. No liblsl post-processing is a *choice*, not an oversight — the project performs equivalent
   de-jittering itself (PART 3) precisely so the raw values survive.
4. Channel order in the wire matches the montage. Nothing verifies this.

**What could invalidate it:** AURA renaming its raw stream; AURA populating `<desc/>` with a
*different* channel order than the typed montage (which would then be silently contradicted);
a liblsl version whose `StreamInlet` lacks a bound overload, which surfaces as `InletFailed`
rather than as wrong data.
---

# PART 3 — TIME SYNCHRONIZATION

This is the part of the pipeline with the most engineering behind it, and it is worth
understanding thoroughly, because everything downstream — epoching, windowing, and any future
event-related analysis — inherits its correctness.

## 3.1 What enters, why the stage exists

**Enters:** one `double` per sample — the timestamp `pull_sample` returned.
**Leaves:** three timestamps per sample, carried side by side in `RawEegSample`.

**Why:** two independent problems have to be solved, and the code solves them separately because
they have different causes and different fixes.

| Problem | Symptom | Fix |
|---|---|---|
| The EEG machine and the VR machine keep unrelated clocks | Every event falls outside every EEG window | liblsl `time_correction()` — PART 3.3 |
| AURA's per-sample timestamps are disturbed at chunk boundaries | Non-monotonic timestamps, apparent gaps | A locally-fitted uniform analysis timebase — PART 3.5 |

## 3.2 The three timestamps **[CODE]**

Declared in `AuraLslReceiver.cs`, struct `RawEegSample`:

```csharp
public double lslTimestamp;         // remote + timeCorrection  — this machine's LSL clock
public double remoteLslTimestamp;   // exactly what pull_sample returned — AURA's clock
public double timeCorrection;       // the offset that was added
public double analysisTimestamp;    // the de-jittered uniform grid
public double[] channels;
```

| Name | Definition | Clock domain | Authoritative for |
|---|---|---|---|
| `remoteLslTimestamp` | The value `pull_sample` returned, untouched | AURA's `lsl_local_clock()` | **Provenance only.** The doc-comment says: "It must NEVER be used for alignment." |
| `lslTimestamp` (local raw) | `remote + timeCorrection` | This machine's LSL clock | Cross-machine synchronization provenance; shares a domain with the event CSV's `lsl_timestamp` column |
| `analysisTimestamp` | Uniform grid steered by a least-squares fit to `lslTimestamp` | A *derived* uniform timebase anchored to the local LSL clock | **Buffering, windowing, epoching.** This is what both ring buffers index on. |

All four fields are asserted to exist by `ExperimentSelfTest.cs:1880` (reflection over
`typeof(RawEegSample).GetField(field)`), so a future refactor cannot quietly delete one.

## 3.3 liblsl `time_correction()` — the formula and its provenance

### What liblsl documents **[LIT / API]**

Quoted in `LslBinding.TryGetTimeCorrection`'s doc-comment, from the installed binding:

> `time_correction()` returns "the number that needs to be added to a time stamp that was
> remotely generated via `lsl_local_clock()` to map it into the local clock domain of this
> machine", and `pull_sample` returns "the capture time of the sample ON THE REMOTE MACHINE …
> To remap this time stamp to the local clock, ADD the value returned by `.time_correction()`
> to it."

### The implemented formula **[CODE]**

`AuraLslReceiver.TryPullOne()`:

```csharp
var localRaw = remoteTimestamp + m_TimeCorrection;
```

Formally:

    t_local(n) = t_remote(n) + C

where `C` is liblsl's clock-offset estimate at the time of the pull.

Two properties of this being an *addition of a scalar* matter scientifically:

1. **It preserves ordering.** If `t_remote` is monotonic, so is `t_local`. Adding a constant
   cannot create or remove a backward step.
2. **It preserves intervals.** `Δt_local = Δt_remote` exactly, for a constant `C`.

These two facts are what make the continuity diagnostic in `EegDiagnostics.ContinuityReport()`
able to *attribute* an anomaly: it computes both series and compares them. If the anomaly counts
match, the disturbance was already in what AURA sent; if only the corrected series is disturbed,
`C` is moving between samples and that is a local bug.

The sign is taken from the API documentation rather than inferred from an observed difference.
`ExperimentSelfTest.cs:1849–1861` enforces this at the source level: it asserts the receiver
source contains the literal `remoteTimestamp + m_TimeCorrection` and does **not** contain a list
of wrong forms.

### Refresh lifecycle **[CODE]**

`RefreshTimeCorrection(bool initial)` / `RefreshTimeCorrectionIfDue()`:

| When | Timeout | Rationale in code |
|---|---|---|
| Once at `Connect()`, **before any sample is accepted** | 2.0 s | The first estimate takes a few ms to establish |
| Thereafter, at most every `m_TimeCorrectionRefreshSeconds` = **5 s** (range 1–120) | 0.2 s | Later calls are documented as instantaneous, served from a background update |
| **Never per sample** | — | "at 250 Hz that would put a round-trip estimate on every sample for a value that changes by microseconds" |

A **failed** refresh keeps the previous estimate rather than reverting to zero. Reverting to zero
would throw every subsequent timestamp back into the sender's clock domain — a catastrophic and
silent failure. `ExperimentSelfTest.cs:1874` asserts that the per-sample pull path does **not**
contain a call to `TryGetTimeCorrection`.

The receiver instruments the correction for the report:

- `initialTimeCorrection` — the first estimate on this connection
- `timeCorrection` — the current one
- `correctionUpdates` — how many times it has been refreshed
- `largestCorrectionChange` — the largest jump between consecutive estimates

`largestCorrectionChange` is the quantity to watch: because `C` is added to every sample, a jump
in `C` appears as a jump in the corrected timestamps and could itself manufacture an apparent gap.

### Live measurement **[LIVE]**

- Clock correction observed: **≈ 299 911.01 s**, stable to **~1 ms** across three runs.
- The magnitude is not an error. The two machines' raw LSL clocks are unrelated origins; a
  difference of ~3.5 days is entirely ordinary and is exactly why the correction is essential.
- `ExperimentSelfTest.cs:1900–1926` encodes this as a regression test: a buffer filled with
  *corrected* timestamps yields a 750-sample window bracketing a locally-stamped event, while a
  buffer filled with *uncorrected* timestamps yields **no window at all** — "exactly the failure
  the fix removes."

## 3.4 Measured timestamp jitter — the finding that motivated PART 3.5 **[LIVE]**

An 8-second live capture, analysed by `IKEA_EEG ▸ EEG ▸ Diagnose Timestamp Continuity`, produced:

| Quantity | Measured |
|---|---|
| Median inter-sample interval | **exactly 4.000 ms** (= 250.0 Hz) |
| Chunk structure | **9 samples per chunk** — every anomaly index was a multiple of 9 |
| Worst backward step | **−30 ms** |
| Largest forward gap | **+62 ms** |
| Where the anomaly originates | **Before** clock correction — remote and local series showed identical anomaly counts and identical deltas |

Interpretation, which the code states and this reconstruction confirms is the only interpretation
consistent with the numbers: **the sample values are sound and the underlying sample clock is
uniform. What is disturbed is the timestamp attached to each chunk's anchor sample.** LSL delivers
in chunks; the anchor's timestamp is what liblsl computes at transmission, and it inherits network
and scheduling jitter, which the whole chunk then inherits.

A median of exactly 4.000 ms alongside excursions of −30/+62 ms is the signature of *transport*
jitter superimposed on a *regular* acquisition, not of an irregular amplifier.

## 3.5 Why a separate analysis timebase was required

### The problem with using `lslTimestamp` directly

Spectral segmentation assumes uniform sampling. Welch's method computes an FFT over `N`
consecutive samples and asserts that bin `k` corresponds to frequency `k·fs/N`. That assertion is
only true if the samples really are `1/fs` apart.

With the measured jitter, three things break:

1. **Non-monotonic timestamps** make `TryGetWindow` ambiguous — the ring buffer's scan assumes
   chronological order, and a backward step means "the sample at index i+1 is earlier than the one
   at index i", which no window definition handles cleanly.
2. **Window sample counts vary** unpredictably, so a "4 second window" contains a variable number
   of samples, and the effective `fs` used in the PSD normalisation would be wrong.
3. **`RawEegRingBuffer` counts a gap** whenever a step exceeds 4 nominal intervals (16 ms at
   250 Hz). A +62 ms chunk anchor is a false gap, and would make continuity statistics
   uninterpretable.

### Why liblsl's own `proc_dejitter` was NOT used **[CODE]**

`EegAnalysisTimebase`'s header states the reason directly: `proc_dejitter` **replaces** the
timestamp `pull_sample` returns. That would destroy the raw remote value, which this project needs
for two purposes — provenance (what did the amplifier actually say?) and the continuity diagnostic
(is the disturbance ours or theirs?). Implementing the same fit alongside keeps all three
timestamps.

Similarly `proc_monotonize` is rejected: it forces monotonicity by clamping the *output*, which
"would hide a bad model rather than fix it."

### The implemented algorithm **[CODE]** — `EegAnalysisTimebase.Add(localRawTimestamp)`

Construction: `new EegAnalysisTimebase(nominalRateHz, windowSeconds = 8.0)`. Throws if
`nominalRateHz <= 0` — a uniform grid is meaningless for an irregular stream, and
`AuraLslReceiver.Connect` therefore sets `m_Timebase = null` and logs a warning in that case,
falling back to raw corrected timestamps.

Window size: `m_WindowSize = max(64, ceil(rate × windowSeconds))` = **2000 samples at 250 Hz**.

Per sample:

**Step 1 — record.** Push `(n, t_raw)` into a ring of size `m_WindowSize`, where `n` is a
monotonically increasing sample index.

**Step 2 — ordinary least squares over the window.** Fit `t = a + b·n`:

```
    n̄ = (1/M) Σ nᵢ                    t̄ = (1/M) Σ tᵢ
    b  = Σ (nᵢ − n̄)(tᵢ − t̄) / Σ (nᵢ − n̄)²
    a  = t̄ − b·n̄
```

with `M = m_Count` (up to 2000). For fewer than 2 samples the raw value is returned unchanged.

**Step 3 — slope acceptance guard.** With `T_nom = 1/f_nom`:

```
    accept b  ⟺  0.9·T_nom < b < 1.1·T_nom
```

Only then is `secondsPerSample ← b`. Otherwise the previous value is kept. The code comment
records that an earlier version allowed up to 10× nominal, which let a bad window stretch one step
to 28 ms.

**Step 4 — the fitted value for this sample.**

```
    t_fit(n) = a + b·n
```

**Step 5 — emit incrementally, NOT by evaluating the line.** This is the heart of the design:

```
    t_pred(n)  = t_emit(n−1) + secondsPerSample
    δ          = t_fit(n) − t_pred(n)
    δ_clamped  = clamp(δ, −0.4·T_nom, +0.4·T_nom)
    t_emit(n)  = t_pred(n) + δ_clamped
```

**Why this and not `t = a + b·n`?** The code documents the failure mode of the earlier version in
detail. Both `a` and `b` are refit on every sample, so consecutive outputs came from two
*different* lines and the step between them was

```
    (a[n+1] − a[n]) + b[n+1]·(n+1) − b[n]·n
```

Nothing in that expression is constrained positive. When a badly jittered chunk anchor entered or
left the sliding window, the line shifted and the emitted timestamp went backwards. Measured:
**3 negative steps, worst −4.604 ms** — almost exactly one sample interval, the signature of the
line moving under the evaluation point.

**Why the current version cannot go backwards.** Since `|δ_clamped| ≤ 0.4·T_nom` and
`secondsPerSample ∈ (0.9·T_nom, 1.1·T_nom)`:

```
    Δt_emit = secondsPerSample + δ_clamped
            ∈ (0.9·T_nom − 0.4·T_nom,  1.1·T_nom + 0.4·T_nom)
            = (0.5·T_nom, 1.5·T_nom)
```

Strictly positive **by construction**. Monotonicity is a property of the model, not a clamp
applied to the output. The bound is taken against the **nominal** interval rather than the current
slope, so "a bad slope [cannot] widen its own licence."

The self-test asserts the tighter empirical envelope `[0.45, 1.55]·T_nom`
(`ExperimentSelfTest.cs:2004`).

### What the timebase explicitly does NOT do **[CODE]**

- It does not touch a single EEG amplitude. `ExperimentSelfTest.cs:2025` asserts the *source file*
  does not contain the string `channels` — a structural guarantee, not a promise.
- It does not reorder, insert, drop or interpolate samples.
- It does not clamp the output.

### Instrumentation **[CODE]**

| Property | Meaning |
|---|---|
| `secondsPerSample` | Current fitted interval |
| `fittedRateHz` | `1 / secondsPerSample` |
| `largestResidualSeconds` | max &#124;t_fit − t_raw&#124; — **this is the jitter the fit removes, measured** |
| `largestDriftSeconds` | max &#124;t_emit − t_raw&#124; — how far the grid has been allowed to sit from the jittered clock |
| `samplesSeen` | Total samples processed |

Both are printed by `EegDiagnostics.ContinuityReport()`, so the size of the correction is always
visible beside the result.

## 3.6 How the raw timestamps remain preserved **[CODE]**

Three mechanisms, all structural:

1. **`RawEegSample` carries all three.** `analysisTimestamp` is an *additional* field. The
   doc-comment: "this ADDS a time base, it does not replace or relabel anything."
2. **`proc_none` on the inlet.** liblsl is never asked to modify what `pull_sample` returns.
3. **The CSV keeps all four values.** `RawEegRecorder` writes, per sample:

   ```
   lsl_timestamp_analysis, lsl_timestamp_local_raw, lsl_timestamp_remote_raw, time_correction, ch1..chN
   ```

   with header comments naming which column is authoritative for which purpose.
   `ExperimentSelfTest.cs:1932` asserts all three timestamp column names are present.

Because `time_correction` is stored per sample, the mapping is fully invertible offline:
`t_remote = t_local − C`. Nothing is lost.

`RawEegRecorder` is additionally asserted not to transform the signal: a regex check
(`ExperimentSelfTest.cs:2068`) verifies no arithmetic operator is ever applied to
`sample.channels[c]`, and values are written with `.ToString("R")` for exact round-tripping.

## 3.7 Why the analysis timebase is suitable for spectral segmentation

The argument, stated as a chain:

1. **The acquisition is genuinely uniform.** Median interval exactly 4.000 ms **[LIVE]**. The
   physical sampling clock in the amplifier is regular; only the transmitted anchor timestamps
   are disturbed.
2. **A straight line is therefore the *correct* model, not an approximation.** At a fixed
   sampling rate, true capture time *is* linear in sample index: `t(n) = t₀ + n/fs`. The fit
   recovers the sample clock and the residual is, by construction, the transport jitter. The
   class header makes this explicit and adds that if the stream were irregular "this class would
   be the wrong tool, and it says so by refusing to run without a rate."
3. **The emitted grid is strictly increasing and near-uniform.** Steps confined to
   `[0.5, 1.5]·T_nom` analytically, `[0.45, 1.55]·T_nom` asserted empirically; median measured at
   3.9993 ms against a 4.000 ms target **[synthetic test]**.
4. **Therefore `k·fs/N` is a valid frequency axis** for a window cut on this timebase, and the
   sample count in a window of `W` seconds is `≈ W·fs` with no jitter-induced variability.
5. **The grid remains locked to the physical clock.** It is steered, not free-running. The bound
   is `largestDriftSeconds`, asserted `< 100 ms` in the self-test and measured far below that.

**Crucially: no EEG amplitude is altered by any of this.** The spectrum computed is the spectrum
of the actual samples the amplifier produced. Only the label "when was this taken" is refined.

## 3.8 Validation results **[from diagnostics and self-tests]**

### Synthetic validation — `ExperimentSelfTest.cs:1941–2020` **[CODE, run in-project]**

The test reproduces the live jitter pattern: 2000 samples on a true uniform 250 Hz grid, with a
chunk-anchor displacement of `(rand − 0.35) × 0.060` s applied, matching the live magnitudes
(backward to −30 ms, forward to +60 ms).

| Assertion | Result |
|---|---|
| The synthetic input really is non-monotonic | **880 backward steps in** |
| The analysis timebase is strictly increasing on that input | **0 backward steps out** |
| Every step positive | min step > 0 |
| Steps stay within the steering bound | **2.056 – 5.738 ms** around a 4.000 ms nominal |
| Median analysis interval tracks the real rate | **3.9993 ms** vs 4.000 ms |
| The grid stays close to the raw clock | `largestDriftSeconds` **< 100 ms** |
| The timebase never touches an amplitude | source contains no `channels` |

### Live validation **[LIVE]**

| Check | Result |
|---|---|
| Analysis timebase, live | **0 non-monotonic, 0 gaps, median 3.99 ms** |
| Clock correction | ≈ 299 911.01 s, stable ~1 ms across 3 runs |
| Event window on corrected timestamps | **750 samples, 8 ch, status = Complete, brackets the event — PASS** |
| Timestamp continuity diagnostic | Identified the 9-sample chunk signature |

### The one caveat that must be carried forward **[LIVE]**

`CLAUDE_HANDOFF.md` records: the timebase was **not re-validated against live AURA after the final
(incremental-emission) fix** — AURA went offline before that could be done. The live "0
non-monotonic, 0 gaps, median 3.99 ms" figure predates the final model change. The synthetic
validation of the final model is thorough; the live confirmation of the final model is
**outstanding**. This is a named item in PART 19 and PART 21.

## 3.9 Assumptions and what could invalidate them

| Assumption | Evidence | What would invalidate it |
|---|---|---|
| The amplifier's sample clock is uniform | Median interval exactly 4.000 ms over 8 s **[LIVE]** | A device with a genuinely drifting or irregular clock; sample dropping at the source |
| Chunk-anchor jitter is transport-level, not acquisition-level | Anomalies fall exactly on multiples of 9; identical in remote and local series **[LIVE]** | Anomalies appearing at non-chunk indices |
| `local = remote + correction` | liblsl's own documentation | A liblsl version changing the documented sign convention |
| `C` is effectively constant over seconds | `largestCorrectionChange`, measured ~1 ms across runs **[LIVE]** | Severe clock drift, VM time-warping, or NTP steps during a session |
| A ±10% slope guard is wide enough | Chosen to be tight; not derived | A stream whose true rate differs from its advertised rate by more than 10% — the guard would then permanently reject the true slope and the grid would drift, bounded only by `largestDriftSeconds` |
| Losing samples is visible | Effective rate vs advertised rate is reported | **[GAP]** LSL exposes no packet-loss counter and the code invents none. Sustained loss shows only as an effective-rate shortfall. |

**One structural caution worth recording.** `EegAnalysisTimebase.Reset()` exists but is **never
called by production code** (verified by grep; the only `.Reset()` calls in `Scripts/` are on
unrelated types). The timebase is created fresh in `AuraLslReceiver.Connect()`, so a reconnect
does produce a new one — but `EegRunRecorder.BeginRun()` clears the raw ring buffer *without*
resetting the timebase, so the fitted grid carries across run boundaries. Since the grid is
continuous and monotonic that is harmless for windowing, but it means `largestResidualSeconds` and
`largestDriftSeconds` are session-cumulative, not per-run, and should be read as such.
---

# PART 4 — RAW EEG BUFFER

## 4.1 What enters, why the stage exists

**Enters:** one `(analysisTimestamp, double[] channels)` pair per sample.
**Leaves:** on demand, an `EegWindow` covering a requested time range, plus continuity statistics.

**Why:** nothing can be extracted around a stimulus that was not already being recorded when it
occurred. The buffer runs continuously so the analysis can ask questions afterwards. The class
header states this directly.

## 4.2 Implementation **[CODE]** — `RawEegRingBuffer.cs`

There are **two instances** of this class in a running scene, and confusing them is easy:

| Instance | Owner | Contents | Capacity |
|---|---|---|---|
| `AuraLslReceiver.buffer` | receiver | **RAW**, exactly as received | 60 s (set by `ExperimentSceneBuilder`) |
| `EegFeaturePipeline.filteredBuffer` (`m_Filtered`) | feature pipeline | **FILTERED** output of the band-pass | 30 s (`m_FilteredHistorySeconds`) |

Both are indexed on `analysisTimestamp`, so a window cut from either lines up with the other and
with the event log. Both are the same class with the same guarantees.

### Storage layout

```csharp
readonly double[] m_Samples;      // FLAT: index = slot * channelCount + channel
readonly double[] m_Timestamps;   // one per slot
int m_Count;                      // how many slots are occupied
int m_Head;                       // next slot to write
```

One flat `double[]` of `capacity × channels`, allocated **once** in the constructor and never
grown. Writing a sample copies `channelCount` doubles into it. Nothing is allocated per sample.

`SlotOf(chronologicalIndex)`:

```csharp
var start = m_Count == m_Capacity ? m_Head : 0;
return (start + chronologicalIndex) % m_Capacity;
```

When full, the oldest sample sits at the head; before that, at slot 0.

## 4.3 Capacity and sample-rate dependence **[CODE]**

```csharp
m_Capacity = nominalRateHz > 0d
    ? Math.Max(1, (int)Math.Ceiling(nominalRateHz * Math.Max(0.001, capacitySeconds)))
    : Math.Max(1, fallbackCapacitySamples);   // default 8192
```

Capacity is expressed in **seconds** and converted using the rate the *stream* advertised. There
is no literal `250` and no literal `8` anywhere in the file — an explicit design constraint stated
in the header and enforced by self-tests that deliberately use 256 Hz, 137 Hz and 7 channels.

At the live configuration:

```
    capacity = ceil(250 Hz × 60 s) = 15 000 samples          (raw buffer)
    capacity = ceil(250 Hz × 30 s) =  7 500 samples          (filtered buffer)
```

An irregular-rate stream (`nominalSrate == 0`) falls back to a fixed 8192 samples, because there
is no rate to multiply by.

The constructor also refuses a capacity that would overflow a single array:

```csharp
m_Samples = new double[(long)m_Capacity * m_ChannelCount <= int.MaxValue
    ? m_Capacity * m_ChannelCount
    : throw new ArgumentOutOfRangeException(...)];
```

## 4.4 Number of channels **[CODE]**

`channelCount` is a constructor argument, taken from `meta.channelCount`. The constructor throws
`ArgumentOutOfRangeException` for a non-positive value with the message "channel count must come
from the stream metadata and be positive".

`Add()` silently returns if `channels == null || channels.Length < m_ChannelCount` — a short
sample is dropped rather than partially written.

## 4.5 Timestamp storage and continuity tracking **[CODE]**

Each `Add(lslTimestamp, channels)` updates, under the lock:

| Statistic | Rule |
|---|---|
| `m_FirstTimestamp` | Set on the first sample only |
| `m_LatestTimestamp` | Always the most recent |
| `m_Monotonic` / `m_NonMonotonic` | If `step <= 0`: mark non-monotonic and count. **The sample is still stored** — "the fact that it happened is itself data about the transport." |
| `m_GapCount`, `m_LargestGap`, `m_LargestGapAt` | If `step > gapToleranceSeconds`: count and track |

### Gap tolerance — derived, not fixed **[CODE]**

```csharp
m_GapToleranceSeconds = nominalRateHz > 0d
    ? gapToleranceMultiplier / nominalRateHz     // default multiplier = 4.0
    : double.PositiveInfinity;                   // irregular rate: no expected interval
```

At 250 Hz: **16.0 ms** (four nominal intervals). The header explains why it is derived: at 250 Hz
samples are 4 ms apart and at 128 Hz nearly 8, so any constant threshold would be wrong for one of
them. The multiplier allows for ordinary chunked-transport jitter while still catching a genuine
dropout.

An irregular-rate stream has gap detection *disabled* (`+∞` tolerance) rather than given a guessed
threshold.

### `EegContinuityStats` **[CODE]**

`GetStats()` returns a snapshot including:

```csharp
effectiveRateHz = duration > 0d ? (m_TotalSamples - 1) / duration : 0d;
```

Note `(N − 1)`, not `N`: N samples span N−1 intervals. This is correct and worth pointing out
because the off-by-one is a common source of a small systematic rate error.

`CheckBufferReport()` prints `effectiveRateHz − nominalSrate` and adds the honest caveat:

> "a persistent shortfall here is the honest indication of dropped data. LSL does not expose a
> packet-loss counter, so no loss figure is invented."

## 4.6 Event-window extraction **[CODE]** — `TryGetWindow(eventTimestamp, preSeconds, postSeconds, out window)`

```
    start = eventTimestamp − max(0, preSeconds)
    end   = eventTimestamp + max(0, postSeconds)
```

Algorithm: a single chronological scan finds the first and last indices whose timestamp lies in
`[start, end]`; result arrays are then allocated **exactly once** at the right size and filled.
The scan `break`s as soon as `t > end`, exploiting the chronological ordering that the analysis
timebase guarantees.

Output structure `EegWindow`:

```csharp
double requestedStart, requestedEnd, eventTimestamp;
double firstTimestamp, lastTimestamp;      // what was ACTUALLY returned
int sampleCount, channelCount;
EegWindowStatus status;
double[][] samples;                        // [sample][channel], chronological
double[] timestamps;
```

### The status enum — honesty about partial windows **[CODE]**

```csharp
var missingStart = oldest > start;    // buffer's oldest sample is later than requested start
var missingEnd   = newest < end;      // buffer's newest sample is earlier than requested end

status = missingStart && missingEnd ? MissingBothEdges
       : missingStart               ? MissingStart
       : missingEnd                 ? MissingEnd
       : Complete;
```

`EegWindowStatus` = `{ Complete, MissingStart, MissingEnd, MissingBothEdges, Empty }`.

The design decision documented in the header, and worth defending to a reviewer: **a partial
window still returns `true`**, carrying a status that says which edge is missing. The reason: a
window cut 200 ms after the event *exists*, it just does not yet extend as far forward as was
asked for, and silently returning nothing would hide that. `false` is returned only when **no
sample at all** fell in range.

"Missing" means the *buffer cannot cover the edge*, not that the returned samples happen not to
land exactly on it — an important distinction, since discrete samples almost never land exactly
on a requested boundary.

`ExpectedSampleCount(pre, post)` gives the yardstick `round((pre + post) × nominalRate)`,
documented as "a yardstick for diagnostics, not a promise."

## 4.7 Memory behaviour **[CODE]**

| Property | Behaviour |
|---|---|
| Steady-state allocation | **Zero per sample.** Both arrays are pre-allocated; `Add` copies into them. |
| Total footprint (raw, live config) | `15 000 × 8 × 8 B` = **960 kB** samples + `15 000 × 8 B` = 120 kB timestamps ≈ **1.05 MB** |
| Total footprint (filtered, live config) | `7 500 × 8 × 8 B` + `7 500 × 8 B` ≈ **0.53 MB** |
| Growth over session length | **None.** The ring overwrites the oldest sample when full. |
| Allocation on read | `TryGetWindow` allocates one `double[]` per returned sample plus the jagged outer array. This is the only allocating path, and it runs at window cadence, not sample rate. |
| `EegFeaturePipeline.OnSample` | Allocates one small `double[channelCount]` per sample for the filtered store. At 250 Hz × 8 ch this is 2000 small arrays/s — the one per-sample allocation in the whole pipeline. Not currently a measured problem, but it is the obvious first target if GC pressure ever appears on the Quest. |

`Clear()` empties the buffer and **all** its statistics. Called from `EegRunRecorder.BeginRun()`
on the **raw** buffer only, so that a run's continuity statistics describe that run, and so a
window cut early in a run cannot reach back into the previous one.

**Asymmetry worth recording [CODE] [GAP]:** `BeginRun` does **not** clear
`EegFeaturePipeline.filteredBuffer`. The filtered history therefore carries across run
boundaries. For a 4-second window analysed well into a run this is immaterial; for a window
analysed in the first seconds of run 2 it means filtered samples from run 1 could in principle be
included. Given that `AnalyzeLatestWindow` is currently never called at runtime (PART 1.3), this
has no present effect — but it must be fixed at the same time as the runtime cadence is wired in.

## 4.8 The validated 1 s pre / 2 s post window — and what it is NOT

### What was validated **[LIVE]** and **[CODE]**

`EegDiagnostics.cs` defines:

```csharp
const double k_PreSeconds  = 1.0;
const double k_PostSeconds = 2.0;
```

`TestEventWindowReport()` (`IKEA_EEG ▸ EEG ▸ Test Event Window`) performs an end-to-end
synchronization check:

1. Connect a temporary receiver (`HideAndDontSave`, scene untouched).
2. Collect `1.5 s` of pre-event signal.
3. Stamp a **synthetic researcher marker** with `LslClock.Now()` — liblsl's `local_clock()`, the
   *same function* that stamps every real experiment event in the CSV.
4. Collect `2.5 s` of post-event signal.
5. `TryGetWindow(eventTimestamp, 1.0, 2.0)`.
6. Assert four things: samples returned; the window **brackets** the event
   (`first <= event <= last`); the count is within **±20%** of `ExpectedSampleCount`; and the
   status is `Complete`.

**Live result [LIVE]: 750 samples, 8 channels, status `Complete`, brackets the event — PASS.**

750 is exactly `(1.0 + 2.0) s × 250 Hz`. The yardstick is computed from the advertised rate at run
time, never hard-coded — the diagnostic prints `expected at 250 Hz: ~750 samples`.

The same 3-second geometry is locked into the self-test as a regression
(`ExperimentSelfTest.cs:1908–1926`), including the negative control: with **uncorrected**
timestamps the same event falls outside every window.

### What it is NOT — stated explicitly, as requested

**The 1 s pre / 2 s post window is a SYNCHRONIZATION QA WINDOW. It is NOT the spectral-analysis
window.**

| | QA window | Spectral window |
|---|---|---|
| Length | 3 s (1 pre + 2 post) | **4 s**, entirely retrospective |
| Anchor | An event timestamp | The most recent sample |
| Source buffer | `AuraLslReceiver.buffer` — **RAW, unfiltered** | `EegFeaturePipeline.m_Filtered` — **FILTERED** |
| Purpose | Prove that an event stamped on the LSL clock lands inside a window of EEG samples | Estimate theta and alpha power |
| Defined in | `EegDiagnostics.cs` (Editor) | `EegFeaturePipeline.m_WindowSeconds` (runtime component) |
| Invoked by | `IKEA_EEG ▸ EEG ▸ Test Event Window` | `AnalyzeLatestWindow()` |
| Ever used for spectra? | **No.** No PSD is ever computed from it. | — |

These two windows do not interact, share no code path beyond `RawEegRingBuffer.TryGetWindow`, and
answer different questions. Any statement of the form "the system analyses a 3-second epoch around
each event" would be **false** for the current implementation: no event-locked spectral analysis
exists yet. **[GAP]**

## 4.9 Validation, assumptions, and what could invalidate

**Validated [CODE]:**
- `ExperimentSelfTest.cs:1686–1760` — buffer constructed with non-default channel counts and
  rates; window retrieval; channel count preserved.
- `ExperimentSelfTest.cs:2210–2266` — **de-interleave integrity**: 8 channels each given a value
  that could only come from that channel (`i × 1000 + c`), then extracted with the *exact*
  expression `series[i] = window.samples[i][c]` that `EegFeaturePipeline` uses. Asserts each
  extracted value carries its own channel's signature and differs from every other channel at the
  same instant. This is the test that eliminated one of the three hypotheses in PART 17.

**Validated [LIVE]:** buffer fill, effective rate, monotonicity, gap counts, and the event-window
PASS above.

**Assumptions:**
1. Timestamps arriving at `Add()` are chronological. Guaranteed by the analysis timebase
   (PART 3.5), not by the buffer itself — the buffer *detects* violations but its `TryGetWindow`
   scan assumes ordering.
2. 60 s of raw history is enough for any window that will be requested. True for the current 4 s
   spectral window and 3 s QA window by a very wide margin.
3. A gap tolerance of 4 nominal intervals distinguishes jitter from dropout. Reasonable given the
   measured jitter but not derived from a loss model.

**What could invalidate:**
- A stream whose chunking exceeds 4 nominal intervals in normal operation would produce
  false gap counts on the **raw** timestamps. (The buffer is fed `analysisTimestamp`, so in the
  current wiring the gap statistics describe the *de-jittered* series and are expected to be
  clean — the raw-series statistics live in the continuity diagnostic instead.)
- A very long window request against a nearly-empty buffer returns `MissingStart` rather than
  failing, which a caller that ignores `status` would silently treat as complete.
---

# PART 5 — MONTAGE

## 5.1 What enters, why the stage exists

**Enters:** a 0-based channel index, or an ROI name.
**Leaves:** an electrode label, or a set of channel indices — or an explicit failure.

**Why:** the stream publishes no channel labels (PART 2.9). Without a montage, "channel 3" is all
that is honestly known. Every anatomical claim — "frontal theta", "posterior alpha" — depends
entirely on this file being right.

## 5.2 The mapping **[CODE]** — `AuraMontageConfig.channels`

Serialized defaults in `AuraMontageConfig.cs`:

| Channel (1-based) | `SampleIndex` (0-based) | Electrode label |
|---|---|---|
| CH1 | 0 | **Fp1** |
| CH2 | 1 | **F3** |
| CH3 | 2 | **Fz** |
| CH4 | 3 | **F4** |
| CH5 | 4 | **Cz** |
| CH6 | 5 | **P3** |
| CH7 | 6 | **Pz** |
| CH8 | 7 | **P4** |

`EegChannelMapping.SampleIndex => channelNumber - 1`. The 1-based number is what appears in the
AURA acquisition UI; the 0-based index is what indexes `RawEegSample.channels`.

Standard 10–20 nomenclature **[LIT]**: Jasper (1958); the modern extended system is Oostenveld &
Praamstra (2001). Fp1 = left frontopolar; F3/Fz/F4 = left/midline/right frontal; Cz = vertex;
P3/Pz/P4 = left/midline/right parietal. This is a sparse but sensible 8-channel montage: it spans
frontal-midline and parietal-midline, which is exactly what the two target features need.

## 5.3 The ROIs **[CODE]**

```csharp
public const string FrontalThetaRoi    = "FRONTAL_THETA";
public const string PosteriorAlphaRoi  = "POSTERIOR_ALPHA";

FRONTAL_THETA    → labels { "F3", "Fz", "F4" }     → indices { 1, 2, 3 }
POSTERIOR_ALPHA  → labels { "P3", "Pz", "P4" }     → indices { 5, 6, 7 }
```

Note what is **excluded**: Fp1 (index 0) and Cz (index 4) participate in **no** ROI. Fp1 is the
most ocular-artefact-prone site on the head, and its exclusion from a frontal-theta average is a
defensible methodological choice — it should be stated in any methods section, because a reader
will otherwise assume "frontal" includes the frontopolar channel.
`ExperimentSelfTest.cs:2674` asserts exactly this exclusion.

## 5.4 Why mapping BY LABEL matters **[CODE]**

The file header is emphatic:

> "The processing that follows must resolve electrodes BY LABEL through this mapping — never by
> writing `channels[1]`, `channels[2]`, `channels[3]` and hoping the cap has not moved."

Concretely, `EegFeaturePipeline.ComputeRoi` calls
`m_Montage.ResolveRoi(AuraMontageConfig.FrontalThetaRoi, out var fp)` and receives indices. It
never writes an index literal. The consequence: if the montage is edited — because the cap was
re-wired, or a different amplifier is used — the ROI definition **moves with it automatically**,
because the ROI is defined over the labels `{F3, Fz, F4}`, not over the positions `{1, 2, 3}`.

## 5.5 ROI resolution — all-or-nothing **[CODE]**

`ResolveRoi(roiName, out problem)` returns `null` if **any** of the ROI's electrodes is missing
from the montage, with a message naming the missing electrode. The rationale in the header:

> "A 'frontal theta' averaged over two electrodes because the third could not be resolved is not
> frontal theta with a caveat — it is a different measurement wearing the same name."

`IndexOfLabel(label)` likewise returns **−1**, not a fallback index:

> "a caller that cannot find P3 must stop and say so, not quietly analyse whatever channel
> happened to be nearby."

`EegFeaturePipeline.ComputeRoi` additionally bounds-checks every resolved index against the actual
window's channel count before averaging, and sets `RoiUnresolved` with a specific message if any
index is out of range.

## 5.6 Human-verified provenance **[CODE]**

```csharp
public EegConfigSource mappingSource     = EegConfigSource.HumanVerifiedAcquisitionUi;
public EegConfigSource filterStateSource = EegConfigSource.HumanVerifiedAcquisitionUi;

public string verificationNote =
    "Channel order and filter state read from the AURA acquisition UI by the researcher. The
     LSL stream publishes an empty <desc/> and provides none of this information, so this
     configuration is NOT self-verifying: if the cap or the amplifier configuration changes,
     this asset must be updated by hand.";
```

`DescribeProvenance()` produces a single line intended to be attached to any feature computed
through the montage:

```
montage_source=HumanVerifiedAcquisitionUi; montage=CH1=Fp1,CH2=F3,...,CH8=P4;
filter_state_source=HumanVerifiedAcquisitionUi; acquisition_notch=OFF;
acquisition_bandpass=OFF; acquisition_unfiltered=TRUE; units_confirmed=FALSE;
amplitude_units=AURA native units
```

The static factory `CreateHumanVerifiedDefault()` returns a `CreateInstance<AuraMontageConfig>()`
— i.e. the serialized defaults above. It exists so the pipeline works with **no asset created**,
which is the current state: `ExperimentSceneBuilder` passes `montage: null` deliberately, and
`EegFeaturePipeline.Awake()` / `EnsureSubscribed()` fall back to the factory. The builder's
comment: "Assigning a wrong asset here would silently change what an ROI means."

The verification date recorded in the source is **2026-08-21**.

## 5.7 Why LSL metadata cannot verify channel order

Three distinct things must hold for an ROI to mean what it says:

1. **Channel count matches.** ✅ Verifiable — `Validate(streamChannelCount, out problem)` compares
   `channels.Count` against the live `meta.channelCount` and refuses a mismatch with the message
   "The configuration and the amplifier disagree; do not analyse until this is resolved."
2. **Channel *order* matches.** ❌ **Not verifiable.** The stream's `<desc/>` is empty, so there is
   nothing to compare the typed order against. A cap wired F4-Fz-F3 instead of F3-Fz-F4 produces
   a perfectly valid 8-channel stream, passes every check in the project, and silently mislabels
   every hemisphere-specific claim.
3. **Electrodes are where the labels say.** ❌ Not verifiable by any software. A physical
   placement question.

Item 2 is the specific gap. It is *not* a coding defect — no code can verify metadata that does
not exist — but it is an irreducible methodological limitation of the current setup and must be
declared in any publication.

## 5.8 Risks if the AURA montage changes

| Change | Detected? | Consequence |
|---|---|---|
| Channel **count** changes (8 → 6) | **Yes** — `Validate` fails loudly | Analysis correctly refuses |
| Channel **order** changes, same count | **No** | ROI silently averages the wrong electrodes. Frontal theta might be computed from parietal sites. **Every result becomes wrong while looking healthy.** |
| An electrode is relabelled in the AURA UI | **No** | Same as above |
| Cap physically re-seated / different cap | **No** | Same as above |
| A channel is dead but still transmitting | Partially — `Flatline` or `SaturationLike` may fire | ROI mean is contaminated by one dead site; no per-electrode rejection exists **[GAP]** |

**Mitigations that exist:** the count check; the `IdenticalChannels` flag (PART 16), which would
catch a *duplicated* channel; the verification note; the provenance string.

**Mitigation that does not exist [GAP]:** any procedural or software check that the order is still
correct. The only available control is human: re-read the AURA UI before each session and confirm
it against `AuraMontageConfig`. This belongs on the checklist in PART 21.

## 5.9 Validation **[CODE]** — `ExperimentSelfTest.cs:2611–2680`

| Assertion | What it proves |
|---|---|
| `montage.channels.Count == 8` | The montage has the expected size |
| For every `(channel, label)` pair: `IndexOfLabel(label) == channel - 1` **and** `LabelOfIndex(channel - 1) == label` | The mapping is correct and consistent **in both directions** |
| `montage.Validate(8, out problem)` succeeds | Self-consistency: no duplicate numbers, no duplicate labels, every ROI resolvable |
| `!montage.Validate(6, out mismatch)` | A 6-channel stream is **refused** |
| `IndexOfLabel("Fp1") == 0 && IndexOfLabel("Cz") == 4` | Fp1 and Cz are mapped |
| `!inAnyRoi.Contains(0) && !inAnyRoi.Contains(4)` | …and deliberately excluded from both ROIs |

---

# PART 6 — ACQUISITION FILTER PROVENANCE

## 6.1 The human-verified setting **[CODE]**

```csharp
[Tooltip("TRUE when AURA is applying a mains notch before publishing. Human-verified OFF.")]
public bool acquisitionNotchEnabled;          // default false

[Tooltip("TRUE when AURA is applying a band-pass before publishing. Human-verified OFF.")]
public bool acquisitionBandpassEnabled;       // default false

public string acquisitionFilterDetail =
    "AURA UI shows 'No Notch' and 'No Filtering'. The stream itself states nothing.";
```

| Setting | Value | Source |
|---|---|---|
| Acquisition notch | **OFF** | `HumanVerifiedAcquisitionUi` |
| Acquisition band-pass | **OFF** | `HumanVerifiedAcquisitionUi` |

## 6.2 The permission gate **[CODE]**

```csharp
/// This is the permission gate for Unity-side preprocessing: filtering an already
/// filtered signal compounds the passband edges and attenuates theta and alpha by an
/// unknown amount, while still producing numbers that look entirely plausible.
public bool acquisitionIsUnfiltered =>
    !acquisitionNotchEnabled && !acquisitionBandpassEnabled;
```

## 6.3 Why this permits Unity-side preprocessing

The logic is a chain:

1. AURA applies no filtering → the float32 values on the wire are (up to unknown scaling) the
   digitised signal.
2. Therefore the Unity-side band-pass is the **only** filter in the chain.
3. Therefore its transfer function `H(z)` — measurable, reportable, and characterised in PART 8 —
   is the **complete** frequency-domain description of the preprocessing.
4. Therefore the methods section can state one filter, with one set of cutoffs and one order, and
   a reader can reproduce it.

If AURA *were* filtering, the effective response would be `H_AURA(z) · H_Unity(z)`, and since
`H_AURA` is undocumented (the stream states nothing) the total would be **unknowable**.

## 6.4 The risk of double filtering — quantified

The danger is that double filtering is *silent*. Cascading two band-passes does not produce
obviously broken output; it produces plausible output with attenuated band powers.

Concretely, if AURA applied its own 1–40 Hz band-pass of comparable order, the cascade would have
roughly double the dB attenuation at every frequency. At the passband edges this is severe:

| Frequency | Unity filter alone **[CODE, PART 8]** | Cascaded with an identical AURA filter |
|---|---|---|
| 4 Hz (theta lower edge) | close to 0 dB | close to 0 dB — little change mid-band |
| 1 Hz (HP cutoff) | −3 dB by definition | **−6 dB** — half the amplitude, **quarter the power** |
| 40 Hz (LP cutoff) | −3 dB | **−6 dB** |
| 0.2 Hz | < −30 dB (measured) | < −60 dB |

Theta (4–8 Hz) and alpha (8–12 Hz) sit comfortably inside a 1–40 Hz passband, so the *mid-band*
distortion from an identical cascade would be modest. The real hazards are different:

1. **An AURA band-pass with a higher high-pass corner** (e.g. 5 Hz, common in some consumer
   presets) would remove most of theta before Unity ever sees it. Theta would be measured as
   near-zero and the finding would be reported as "no frontal theta effect."
2. **An AURA notch at 50/60 Hz** would be harmless for theta/alpha but would invalidate the code's
   stated justification for omitting a notch, and would matter for any future beta/gamma work.
3. **Unknown phase.** Two unknown group delays in cascade make any future ERP-latency analysis
   uninterpretable (PART 8.9).

The code's own framing, in `EegFeaturePipeline.DescribePreprocessing()`, prints the acquisition
state beside the Unity state so both appear in the same report:

```
  HP 1.00 Hz, LP 40.00 Hz, notch OFF (60 Hz sits outside the passband)
  filter settled: True (750/750 samples, 3.00 s required)
  acquisition notch OFF, bandpass OFF (source = HumanVerifiedAcquisitionUi)
```

## 6.5 Assumptions and what could invalidate

| Assumption | Basis | Invalidated by |
|---|---|---|
| AURA notch is OFF | A person read the AURA UI on 2026-08-21 | Anyone toggling it; a firmware or software update changing the default; a different AURA installation |
| AURA band-pass is OFF | Same | Same |
| The setting persists across AURA restarts | **Not verified** | An AURA build that resets to a filtered default |

**There is no software check.** `acquisitionIsUnfiltered` reads a serialized boolean that a human
typed; it queries nothing. The `filterStateSource` field records this honestly rather than hiding
it. Re-confirming these two toggles in the AURA UI is a **required pre-session step** and appears
on the PART 21 checklist.

---

# PART 7 — AMPLITUDE UNITS

## 7.1 The current state **[CODE]**

```csharp
[Tooltip("OFF until AURA's source or documentation confirms the scaling of the float32
          values. The UI showing a µV axis is not the same as the stream carrying µV.")]
public bool unitsConfirmed;                                   // default FALSE

public string amplitudeUnitLabel = "AURA native units";

public string PowerUnitLabel =>
    unitsConfirmed ? $"{amplitudeUnitLabel}²" : "AURA-native-units²";

public string PowerSpectralDensityUnitLabel =>
    unitsConfirmed ? $"{amplitudeUnitLabel}²/Hz" : "AURA-native-units²/Hz";
```

The three labels that every report and every UI panel currently carries:

| Quantity | Label |
|---|---|
| Amplitude (a sample value) | **`AURA native units`** |
| Band power (integrated PSD) | **`AURA-native-units²`** |
| Power spectral density | **`AURA-native-units²/Hz`** |

These propagate: `LatestEegFeatures.amplitudeUnits` / `.powerUnits` / `.psdUnits` are populated
from the montage in **both** analysis paths, printed by `EegSpectralDiagnostics` under a `UNITS:`
heading with the line "The stream publishes no scaling, so these are NOT µV", and displayed
beneath every number in `EegResearcherMonitor`.

## 7.2 Why the units remain unresolved

The distinction the project insists on, and which is genuinely the correct one:

> **"The UI showing a µV axis is not the same as the stream carrying µV."**

These are two separate propositions:

| Proposition | Status | Why |
|---|---|---|
| The AURA GUI displays a plot whose axis is labelled µV | Observed by a person | The GUI applies whatever scaling *it* chooses before plotting |
| The float32 values in the LSL stream are numerically equal to microvolts | **UNCONFIRMED** | The stream's `<desc/>` is empty and therefore contains no `<unit>` element **[LIVE]** |

A GUI can display µV while transmitting ADC counts, volts, normalised units, or µV scaled by an
arbitrary gain factor. Nothing in the transmitted metadata resolves this, and no measurement made
*within* the stream can resolve it either — an unknown constant multiplier is invisible to every
statistic computed from the stream alone.

## 7.3 Why this is scientifically decisive, not pedantic

An unknown scale factor `k` propagates as:

```
    x_true[n]  =  k · x_stream[n]
    P_true     =  k² · P_stream
    ΔdB_true   =  10 log₁₀(k²·P_task / k²·P_base)  =  10 log₁₀(P_task / P_base)  =  ΔdB_stream
```

So:

| Analysis | Affected by unknown `k`? |
|---|---|
| Absolute band power, reported as a number | **YES** — meaningless without `k` |
| Comparison to literature µV² values | **YES** — impossible |
| Theta/alpha ratio | **NO** — `k²` cancels |
| Baseline-normalised dB change | **NO** — `k²` cancels |
| Within-participant, within-session contrasts | **NO** — `k` is constant |
| Between-participant comparison | **NO**, *provided* `k` is the same amplifier and configuration |
| Absolute artefact thresholds (e.g. "reject &gt; ±100 µV") | **YES** — cannot be implemented |

The last row is why every quality threshold in `EegFeaturePipeline.AssessChannel` is **relative**
(to the channel's own typical step, its own typical range, or a count) rather than absolute. The
method's doc-comment states this reasoning explicitly:

> "A '±100 µV' rule would be meaningless while the scaling of these float32 values is unverified,
> and would silently reject or accept the wrong windows."

This is a well-made trade: the project loses absolute artefact rejection and gains the guarantee
that no threshold is secretly wrong.

## 7.4 Exactly what evidence would be required to convert to µV

Any **one** of the following would be sufficient, listed strongest first:

1. **AURA populates the LSL `<desc/>` with a unit element.** The canonical XDF/LSL convention is
   `<channels><channel><label>F3</label><unit>microvolts</unit></channel>…</channels>`. This is
   the ideal outcome: the unit then travels with the data and cannot drift from it, and
   `mappingSource` could be upgraded to `EegConfigSource.LslStreamMetadata`.
2. **Manufacturer documentation** stating the scaling of the LSL output explicitly — e.g. "values
   are transmitted in microvolts" or "values are ADC counts; multiply by *G* µV/count". A
   datasheet giving ADC bit depth, reference voltage and total gain permits the constant to be
   derived: `k = V_ref / (2^bits · gain) × 10⁶ µV/V`.
3. **AURA source code** showing the transformation applied between the ADC and `push_sample`.
4. **A calibration measurement**: inject a known sinusoid of known amplitude (e.g. 10 µV pp at
   10 Hz from a signal generator or a calibration box) into the amplifier input and read the
   resulting stream values. Then `k = A_injected_µV / A_measured_stream`. This requires hardware
   the project does not currently document possessing, and requires knowing the input impedance
   and any input-stage attenuation.
5. **A weaker, non-sufficient check**: comparing the AURA GUI's displayed µV value at a given
   instant against the stream value at the same instant. This constrains `k` but does **not**
   confirm it, because the GUI may apply its own filtering or smoothing before display. It should
   be recorded as *suggestive*, never as confirmation.

**Not acceptable as evidence:** the plausibility of the numbers. Resting EEG alpha is commonly
10–50 µV **[LIT]**, and a stream value that happens to fall in that range is not proof — it is
exactly the kind of coincidence that produces a paper with wrong units.

## 7.5 The procedure once units ARE confirmed **[CODE]**

The code is already structured for it. Setting `unitsConfirmed = true` and
`amplitudeUnitLabel = "µV"` on the `AuraMontageConfig` automatically changes every downstream
label to `µV²` and `µV²/Hz`, with **no code change and no numerical change**. If a multiplicative
conversion were also required, that would be a new step — and it should be applied at exactly one
place, ideally at ingestion, and recorded in the raw CSV header as a documented transformation.

Until then, the correct citation form for any figure or table produced by this project is
**"AURA-native-units²"**, with a footnote stating that the amplitude scaling of the LSL stream is
undocumented.

## 7.6 Validation **[CODE]**

`ExperimentSelfTest.cs` (montage section) asserts that no power value can be labelled `µV²/Hz`
while `unitsConfirmed` is false — the honesty of the labelling is itself under test, not merely
the lookup table. The doc-comment for `PowerSpectralDensityUnitLabel` states the reason:

> "labelling a power spectral density with a physical unit it has not been shown to have is
> exactly the kind of claim that survives into a paper unchallenged."
---

# PART 8 — DIGITAL FILTER

Everything in this part is derived from `Assets/IKEA_EEG/Scripts/Data/EegBandpassFilter.cs` as it
currently stands, not from any prior summary.

## 8.1 What enters, why the stage exists

**Enters:** one raw sample value, for one channel, per call.
**Leaves:** one filtered value. The raw value is passed **by value** and returned separately, so
the caller keeps both.

**Why:** raw EEG contains two things that corrupt a band-power estimate:

1. **Slow drift** — electrode polarisation, sweat, movement — with power orders of magnitude
   above the µV-scale oscillations of interest, concentrated below ~1 Hz. Left in, it dominates
   the periodogram and leaks upward into theta.
2. **High-frequency content** — EMG, mains, switching noise — above the bands of interest.

## 8.2 Topology **[CODE, derived]**

```csharp
public const int HalfOrder = 4;
public int overallOrder => HalfOrder * 2;         // = 8
```

`BuildSections(fs, hp, lp)`:

```csharp
var qs = ButterworthQ(HalfOrder);                  // 4/2 = 2 Q values
var sections = new Biquad[qs.Length * 2];          // 4 biquads

for (var k = 0; k < qs.Length; k++)
{
    sections[k]              = HighPass(fs, hp, qs[k]);
    sections[qs.Length + k]  = LowPass (fs, lp, qs[k]);
}
```

| Property | Value |
|---|---|
| Filter family | **Butterworth** (maximally flat magnitude in the passband) |
| Structure | **Cascade of second-order sections (biquads)** |
| High-pass order | **4** |
| Low-pass order | **4** |
| **Overall order** | **8** |
| **Biquad count per channel** | **4** — `sections[0..1]` = HP, `sections[2..3]` = LP |
| Realisation | **Direct Form I** |
| Causality | **Causal, forward-only, single pass** |
| State | **Independent per channel**; `Biquad[channelCount][sectionsPerChannel]` |

The header explains the choice of cascade over a single 8th-order difference equation: "an
8th-order filter expressed as a single difference equation is numerically fragile at these cutoff
ratios (1 Hz at 250 Hz sampling is a pole very close to the unit circle), and a cascade of
second-order sections keeps every coefficient well conditioned." This is standard and correct
**[LIT: Oppenheim & Schafer, *Discrete-Time Signal Processing*]** — the sensitivity of pole
locations to coefficient quantisation grows rapidly with direct-form order.

## 8.3 Cutoffs and parameters **[CODE]**

Defaults in the constructor signature: `highPassHz = 1.0`, `lowPassHz = 40.0`.
Values actually used: set by `EegFeaturePipeline` serialized fields `m_HighPassHz = 1.0`,
`m_LowPassHz = 40.0`, and re-asserted by `ExperimentSceneBuilder`
(`eegPipeline.Configure(montage: null, highPassHz: 1.0, lowPassHz: 40.0, windowSeconds: 4.0)`).

| Parameter | Value | Where set |
|---|---|---|
| High-pass cutoff `f_hp` | **1.0 Hz** | `EegFeaturePipeline.m_HighPassHz` |
| Low-pass cutoff `f_lp` | **40.0 Hz** | `EegFeaturePipeline.m_LowPassHz` |
| Sample rate `f_s` | **From stream metadata** — `m_Receiver.metadata.nominalSrate` | never a constant |
| Channel count | From the first sample's `channels.Length` | never a constant |
| Notch | **NONE** | see below |

### Constructor validation **[CODE]**

```csharp
var nyquist = sampleRateHz * 0.5;
if (lowPassHz >= nyquist) throw ...   // "at or above Nyquist ... choose a lower cutoff
                                      //  or a faster amplifier"
if (highPassHz <= 0d || highPassHz >= lowPassHz) throw ...
if (sampleRateHz <= 0d) throw ...     // "must come from the stream metadata"
if (channelCount <= 0) throw ...
```

At 250 Hz, Nyquist is 125 Hz and a 40 Hz low-pass is comfortably below it.

### Why no notch **[CODE]**

`m_LowPassHz`'s tooltip: "Keeps theta, alpha and low beta; puts 60 Hz mains outside the passband,
which is why no notch is applied." The self-test verifies the claim numerically rather than
accepting it: `filter.GainDbAt(60.0) < -12.0` (`ExperimentSelfTest.cs:2153`), and the measured
time-series attenuation at 60 Hz is asserted below −12 dB as well.

This is a defensible choice **[LIT]**: a notch filter introduces its own ringing and phase
distortion around the notch frequency, and if the mains component is already outside the passband
there is nothing for it to remove. It is worth noting for a reviewer that 60 Hz implies a North
American mains frequency; at 50 Hz the same argument holds (50 Hz is still above a 40 Hz
low-pass), so the design is not mains-region-specific in effect, only in its comment.

## 8.4 Coefficient generation — the mathematics **[CODE, derived]**

### Step 1: Butterworth section Q values

```csharp
static double[] ButterworthQ(int order)
{
    var sections = order / 2;
    var q = new double[sections];
    for (var k = 0; k < sections; k++)
        q[k] = 1.0 / (2.0 * Math.Cos(Math.PI * (2 * k + 1) / (2.0 * order)));
    return q;
}
```

Formally, for order `N`:

```
    Q_k = 1 / ( 2 · cos( π(2k+1) / (2N) ) ),      k = 0 … N/2 − 1
```

For `N = 4`:

| k | `π(2k+1)/8` | `cos` | **Q_k** |
|---|---|---|---|
| 0 | 22.5° | 0.923880 | **0.541196** |
| 1 | 67.5° | 0.382683 | **1.306563** |

**Why these values.** An `N`-th order Butterworth has `N` poles equally spaced on a semicircle of
radius `ω_c` in the left half of the s-plane, at angles `θ_k = π(2k+N+1)/(2N)` from the positive
real axis. Pairing conjugate poles into second-order sections, each section's quality factor is
`Q = 1/(2 cos φ)` where `φ` is the pole's angle from the imaginary axis. These are the standard
Butterworth section Qs and are correct as implemented.

### Step 2: bilinear transform, RBJ biquad form

Both helpers compute, from cutoff `f_0`:

```
    ω₀ = 2π f₀ / f_s
    α  = sin(ω₀) / (2Q)
    a₀ = 1 + α
```

**Low-pass** (`LowPass(fs, f0, q)`), normalised so the leading denominator coefficient is 1:

```
    b₀ = (1 − cos ω₀) / 2 / a₀
    b₁ = (1 − cos ω₀)     / a₀
    b₂ = (1 − cos ω₀) / 2 / a₀
    a₁ = −2 cos ω₀        / a₀
    a₂ = (1 − α)          / a₀
```

**High-pass** (`HighPass(fs, f0, q)`):

```
    b₀ =  (1 + cos ω₀) / 2 / a₀
    b₁ = −(1 + cos ω₀)     / a₀
    b₂ =  (1 + cos ω₀) / 2 / a₀
    a₁ = −2 cos ω₀         / a₀
    a₂ = (1 − α)           / a₀
```

These are the Robert Bristow-Johnson *Audio EQ Cookbook* forms **[LIT]**, which are the bilinear
transform of the corresponding analogue prototype with the standard `tan`-based frequency
pre-warping folded into the `sin`/`cos` terms. They are widely used and correct.

**One technical caveat a reviewer may raise, stated honestly.** The RBJ forms place each section's
−3 dB reference at `f₀` for the *section*. Cascading two sections with Butterworth Qs yields the
correct 4th-order Butterworth **shape**; the composite −3 dB point of the cascade is at `f₀` for a
true Butterworth design, and the measured responses in §8.8 are consistent with that. However, the
code does **not** independently verify the composite −3 dB frequency, and no assertion in the
self-test pins it. What *is* asserted is the behaviour that matters for this project: theta, alpha
and 30 Hz all lie within −3.0…+1.0 dB, drift below 0.2 Hz is attenuated > 30 dB, and 60 Hz is
attenuated > 12 dB.

### Step 3: sample-rate dependence — where `f_s` actually enters

`f_s` appears in **exactly one place**: `ω₀ = 2π f₀ / f_s`. Everything else is a function of `ω₀`
and `Q`. Consequences:

- The filter's **normalised** shape depends only on the ratios `f_hp/f_s` and `f_lp/f_s`.
- A change in `f_s` changes every coefficient, and therefore the settling time in samples, the
  numerical conditioning, and the group delay in samples.
- The header states: "There is no 250 anywhere in this file." Verified by inspection.
- `ExperimentSelfTest.CheckEegFilter` deliberately builds the filter at **256 Hz**, not 250,
  precisely so that any hard-coded 250 fails, and asserts
  `Math.Abs(filter.sampleRateHz - fs) < 1e-9`.

### Numerical example — the dominant high-pass section at f_s = 250 Hz, f_hp = 1 Hz, Q = 1.306563

```
    ω₀   = 2π · 1 / 250            = 0.02513274 rad
    cos ω₀ = 0.99968419
    sin ω₀ = 0.02512909
    α    = 0.02512909 / (2 · 1.306563) = 0.00961629
    a₀   = 1.00961629

    b₀ =  (1 + 0.99968419)/2 / 1.00961629 =  0.99029...
    b₁ = −(1 + 0.99968419)   / 1.00961629 = −1.98059...
    b₂ =  b₀                              =  0.99029...
    a₁ = −2 · 0.99968419     / 1.00961629 = −1.98033...
    a₂ =  (1 − 0.00961629)   / 1.00961629 =  0.98095...
```

This is used in PART 9 to derive the actual settling time.

## 8.5 Direct Form I implementation and state architecture **[CODE]**

```csharp
public class Biquad
{
    public double b0, b1, b2, a1, a2;
    double m_X1, m_X2, m_Y1, m_Y2;      // per-section state. One instance per channel.

    public double Process(double x)
    {
        var y = b0*x + b1*m_X1 + b2*m_X2 - a1*m_Y1 - a2*m_Y2;
        m_X2 = m_X1;  m_X1 = x;
        m_Y2 = m_Y1;  m_Y1 = y;
        return y;
    }
}
```

The difference equation, with `a₀` already normalised to 1:

```
    y[n] = b₀·x[n] + b₁·x[n−1] + b₂·x[n−2] − a₁·y[n−1] − a₂·y[n−2]
```

Transfer function:

```
              b₀ + b₁ z⁻¹ + b₂ z⁻²
    H(z)  =  ──────────────────────
               1 + a₁ z⁻¹ + a₂ z⁻²
```

**Direct Form I** means the two delay chains are kept separately — two past inputs `x[n−1], x[n−2]`
and two past outputs `y[n−1], y[n−2]`, four state variables per section. (Direct Form II would
share a single two-element delay line; DF-I uses more memory but has better overflow behaviour in
fixed point and is trivially readable. In `double` arithmetic the distinction is largely
immaterial, and the choice is a legibility one.)

### Cascade

```csharp
public double Process(int channel, double sample)
{
    if (channel < 0 || channel >= m_ChannelCount) return sample;   // pass through, never crash
    var sections = m_Sections[channel];
    var y = sample;
    for (var s = 0; s < sections.Length; s++) y = sections[s].Process(y);
    return y;
}
```

Signal order: **HP₁ → HP₂ → LP₁ → LP₂**. Overall `H(z) = Π_{s=0}^{3} H_s(z)`.

### State architecture — the guarantee that matters

`m_Sections` is `Biquad[channelCount][]`, built with a **separate call to `BuildSections`** per
channel:

```csharp
for (var c = 0; c < channelCount; c++)
    m_Sections[c] = BuildSections(sampleRateHz, highPassHz, lowPassHz);
```

Every channel gets its own four `Biquad` objects with their own `m_X1, m_X2, m_Y1, m_Y2`. Nothing
is shared. This is asserted directly (`ExperimentSelfTest.cs:2157–2165`): channel 0 is driven with
500 samples of amplitude 1000, then channel 1 is given a zero input, and the output must be
`|y| < 1e-12`. **Result: passes.** This test is directly relevant to PART 17 — it rules out the
filter as a mechanism for collapsing channels together.

### Continuity — state persists across calls **[CODE]**

`Reset()` exists but is documented as "Called ONLY when the stream itself restarts — never per
epoch", and in fact is **never called from production code at all** (verified by grep across
`Assets/IKEA_EEG/Scripts/`). Two structural assertions protect this:

- `ExperimentSelfTest.cs:2180` extracts the body of `Process(int, double)` from the source and
  asserts it does **not** contain the string `Reset` — "filtering a sample never resets state —
  epoch boundaries introduce no transient".
- The rationale, from the class header: "Re-zeroing state at each window boundary would inject a
  transient into the first ~1 s of every epoch — exactly where an event of interest usually sits."

This is an important and correct design decision. Filtering each epoch independently from zero
state is a common and serious methodological error in EEG analysis pipelines.

## 8.6 What a causal IIR filter *is* — background **[LIT]**

**Causal**: the output at time `n` depends only on inputs at times `≤ n` and outputs at times
`< n`. It cannot use the future. This is required for any online/real-time system, because the
future has not happened yet.

**IIR (Infinite Impulse Response)**: the filter feeds its own past outputs back
(`− a₁ y[n−1] − a₂ y[n−2]`). Its impulse response is, in principle, infinitely long — it decays
geometrically but never reaches exactly zero. Contrast with FIR, whose impulse response is finite
by construction and which can be made exactly linear-phase, but which needs far more coefficients
to achieve the same selectivity at a low cutoff.

**The phase consequence.** Any causal filter with a non-trivial magnitude response has a
frequency-dependent phase response; this is not an implementation shortcoming but a mathematical
necessity (the Kramers–Kronig / Hilbert relationship between the log-magnitude and phase of a
causal minimum-phase system). Different frequencies are delayed by different amounts. The relevant
quantity is the **group delay**:

```
    τ_g(ω) = − dφ(ω)/dω
```

For a Butterworth band-pass, `τ_g` is largest near the cutoffs — near the 1 Hz high-pass it is on
the order of tens of milliseconds, as the class header states. The code contains **no group-delay
computation**; `MagnitudeAt` computes only `|H(e^{jω})|`. **[GAP]** — see §8.9.

## 8.7 Why `filtfilt` is not used for the live path

`filtfilt` (zero-phase forward–backward filtering) runs the filter forward over the whole signal,
reverses it, runs it again, and reverses back. The result has exactly zero phase distortion and
squared magnitude response `|H(e^{jω})|²` **[LIT: Gustafsson (1996) on initial conditions for
forward–backward filtering; the method is standard in MATLAB/SciPy]**.

It cannot be used here, for two independent reasons:

1. **It is non-causal.** The backward pass requires the entire signal, including samples *after*
   the point being computed. In a live pipeline those samples do not exist yet. This is not a
   performance objection; it is a physical impossibility for online processing.
2. **It changes the effective filter.** The magnitude response is squared, so the effective order
   doubles (8th → 16th) and the −3 dB points move inward. Any reported filter specification would
   have to describe the squared response, not the designed one.

The code enforces its own decision structurally: `ExperimentSelfTest.cs:2173` asserts the source
file does **not** contain the string `filtfilt`.

**What this means for future offline analysis.** The raw CSV (`raw_eeg.csv`) contains unfiltered
values written with round-trip precision. An offline analysis in MNE-Python, EEGLAB or FieldTrip
is entirely free to apply `filtfilt` to that file and obtain a zero-phase result. The causal
constraint applies to the *live* path only. This is a genuine strength of the architecture: the
online path is constrained, the archived data is not.

## 8.8 Validated frequency response **[CODE — `ExperimentSelfTest.CheckEegFilter`, f_s = 256 Hz]**

The test measures attenuation **from the filtered time series** — the amplitude that actually
survives — rather than reading it off the designed transfer function, "because a design can be
correct on paper and wrong in code". For each probe frequency a *fresh* single-channel filter is
built, driven with `sin(2π f n / f_s)` for `SettlingSamples + 4·f_s` samples, and the peak absolute
output **after** the settling point is converted to dB.

| Probe | Purpose | Asserted range | Verdict |
|---|---|---|---|
| **0.2 Hz** | slow drift — must be strongly attenuated | `< −30 dB` | PASS |
| **6.0 Hz** | theta — must be preserved | `−3.0 … +1.0 dB` | PASS |
| **10.0 Hz** | alpha — must be preserved | `−3.0 … +1.0 dB` | PASS |
| **30.0 Hz** | inside passband | `−6.0 … +1.0 dB` | PASS |
| **60.0 Hz** | mains — must be attenuated | `< −12 dB` | PASS |
| **80.0 Hz** | above cutoff | `< −25 dB` | PASS |

The analytic response is checked separately as a cross-check of the coefficients themselves, via
`GainDbAt(f)` which evaluates `H(z)` on the unit circle:

```csharp
// Biquad.MagnitudeAt(f, fs):  z = e^{jω},  ω = 2π f / fs
numRe = b0 + b1·cos(−ω) + b2·cos(−2ω);   numIm = b1·sin(−ω) + b2·sin(−2ω)
denRe = 1  + a1·cos(−ω) + a2·cos(−2ω);   denIm = a1·sin(−ω) + a2·sin(−2ω)
|H|   = sqrt(numRe² + numIm²) / sqrt(denRe² + denIm²)
```

and the cascade multiplies section magnitudes: `MagnitudeAt(f) = Π_s |H_s(f)|`.

Assertions: `GainDbAt(6) , GainDbAt(10) , GainDbAt(30) ∈ (−3.5, +1.0) dB` and
`GainDbAt(60) < −12 dB`. **Analytic and measured agree.**

That both the *analytic* transfer function and the *measured* time-series attenuation agree is a
meaningful double-check: it confirms the coefficients are right **and** that `Process` implements
the difference equation those coefficients describe.

## 8.9 Suitability for spectral power vs. ERP latency

### Why it is valid for spectral power

Power spectral density depends on `|X(f)|²`. A filter's effect on it is:

```
    S_filtered(f) = |H(f)|² · S_raw(f)
```

**Phase does not appear.** `arg H(f)` — and therefore the group delay — has **no effect whatsoever**
on the PSD or on any band power integrated from it. The filter is characterised for this purpose
entirely by the magnitude table in §8.8, and within 4–12 Hz that table shows `|H| ≈ 1` (−3 … +1 dB).

Furthermore, the analysis window is 4 seconds long and the group delay is on the order of tens of
milliseconds — under 1% of the window. Even the small time-shift the delay introduces is
irrelevant to a stationary power estimate over that span.

### Why it must NOT be used for ERP latency without correction

An event-related potential analysis measures **when** a deflection occurs — the latency of P300,
N400, and so on. A causal filter shifts different frequency components by different amounts, so a
waveform's peak moves, and moves by an amount that depends on its spectral content.

The class header states this and assigns the responsibility explicitly:

> "The delay would matter for ERP latency, which this pass does not compute. Anyone later
> measuring a latency from this filtered signal must account for the group delay or filter
> differently — hence this note rather than a silent assumption."

`Describe()` prints it in every report:

```
  causal forward-only: NOT phase-linear; group delay is frequency-
  dependent. Valid for spectral POWER; not for ERP latency.
```

**Three legitimate options for a future ERP analysis**, in order of preference:

1. **Offline `filtfilt` on `raw_eeg.csv`.** Zero phase by construction. The recommended route,
   since the raw data is archived unfiltered.
2. **Compute and subtract the group delay.** Requires implementing `τ_g(ω)` — currently absent
   **[GAP]** — and is only exact for a narrow band.
3. **Design a linear-phase FIR for the live path.** Constant delay `(M−1)/2` samples, trivially
   correctable, at the cost of a high order for a 1 Hz cutoff.

**A specific and easily-missed caution for the current implementation:** the 1 Hz high-pass is the
component with the largest group delay, and high-pass filters at ≥ 0.5 Hz are known to distort slow
ERP components and can introduce spurious pre-stimulus deflections **[LIT: Acunzo, Mackenzie &
van Rossum (2012); Widmann, Schröger & Maess (2015); Tanner, Morgan-Short & Luck (2015)]**. For a
power analysis in 4–12 Hz this is a non-issue. For any future ERP work it is a first-order concern,
and the 1 Hz cutoff would need to be revisited, not merely phase-corrected.

## 8.10 Assumptions and what could invalidate

| Assumption | Basis | Invalidated by |
|---|---|---|
| AURA applies no filtering of its own | Human-verified UI reading (PART 6) | Anyone toggling AURA's filters — the cascade becomes unknown |
| 1 Hz HP preserves theta | Measured −3…+1 dB at 6 Hz | A change of cutoff without re-running the self-test |
| 40 Hz LP puts mains outside the band | Measured < −12 dB at 60 Hz | A mains harmonic below 40 Hz; a 40 Hz artefact source |
| `double` precision is sufficient | Cascade of biquads keeps conditioning good | A far lower high-pass (e.g. 0.05 Hz) would push poles much closer to the unit circle |
| The filter state is continuous | `Reset()` never called | Any future code that calls `Reset()` per epoch, which would reintroduce the transient the design avoids |
| Phase is irrelevant | True for power; documented | Any future ERP analysis reading `filteredBuffer` instead of `raw_eeg.csv` |

**One implementation observation [CODE]:** `EegFeaturePipeline` maintains a per-channel
`m_TypicalStep[]` running average, updated on every sample
(`m_TypicalStep[c] = m_TypicalStep[c]*0.999 + step*0.001`), but this array is **never read** by
any decision path — `AssessChannel` computes its own local `meanStep` from the window instead.
It is dead computation. Harmless, but worth noting so a future reader does not assume it
participates in quality control.
---

# PART 9 — FILTER SETTLING

This section answers the question directly: **what, exactly, makes this implementation consider
~3 seconds to be its settling period?**

## 9.1 The exact code **[CODE]**

There is one line, in `EegBandpassFilter.cs`:

```csharp
/// <summary>
/// How long the filter needs before its output is trustworthy, in samples.
///
/// Any IIR filter starts from zero state and takes time to settle. Data from before this
/// point is a transient, not signal, and a window cut there would carry it into the
/// spectrum. Derived from the lowest cutoff — the slowest thing the filter must resolve.
/// </summary>
public int SettlingSamples => (int)Math.Ceiling(m_SampleRateHz * 3.0 / m_HighPassHz);
```

That is the entire derivation. Formally:

```
    N_settle = ⌈ f_s · 3 / f_hp ⌉                    [samples]

    T_settle = N_settle / f_s  ≈  3 / f_hp           [seconds]
```

At the live configuration `f_s = 250 Hz`, `f_hp = 1.0 Hz`:

```
    N_settle = ⌈ 250 × 3 / 1 ⌉ = 750 samples
    T_settle = 750 / 250       = 3.00 s
```

**The 3 seconds is not a constant in the code. It is `3 / f_hp`, and it happens to equal 3 s only
because the high-pass cutoff happens to be 1 Hz.** At `f_hp = 0.5 Hz` it would be 6 s; at
`f_hp = 2 Hz`, 1.5 s. At `f_s = 256 Hz` (the self-test rate) with `f_hp = 1 Hz` it is 768 samples,
still 3.00 s.

## 9.2 How the value is used **[CODE]**

`EegFeaturePipeline`:

```csharp
m_SettlingSamples = m_Filter.SettlingSamples;      // captured at filter construction
m_SamplesFiltered = 0;

public bool filterReady => m_Filter != null && m_SamplesFiltered >= m_SettlingSamples;
```

`m_SamplesFiltered` increments once per sample in `OnSample`. In `AnalyzeLatestWindow`:

```csharp
if (!filterReady)
    features.quality |= EegQualityFlags.FilterNotSettled;
```

and since `featureValidity = (quality == None) && roiValid`, an unsettled window is reported as
**NOT VALID** — but the features are still computed and returned, flagged rather than deleted, so a
researcher can see what was rejected and why.

`CaptureBaseline` is stricter and refuses outright:

```csharp
if (!filterReady)
{
    detail = "the filter has not settled; a baseline taken now would be transient";
    return false;
}
```

`EegResearcherMonitor` displays it as a live status line:
`"FILTER SETTLING — features not yet valid"` with `samplesFiltered / settlingSamples`.

`EegSpectralDiagnostics` uses it to size its collection interval — and this is the one place the
figure appears as a bare literal:

```csharp
var settleSeconds   = 3.0 / 1.0;   // 3 time constants at the 1 Hz high-pass
var collectSeconds  = settleSeconds + k_WindowSeconds + 1.0;   // = 8.0 s
```

Note that this diagnostic **hard-codes** `3.0 / 1.0` rather than asking the filter for
`SettlingSamples`. If the high-pass cutoff were ever changed, the diagnostic would collect for the
wrong duration while the pipeline's own gate would remain correct. A minor inconsistency worth
recording. **[GAP]**

## 9.3 So which is it? — categorising the criterion honestly

The question asked was whether the 3 s figure is (a) analytically derived from filter poles,
(b) based on impulse/step response, (c) empirically measured, or (d) an engineering guard interval.

**Answer: (d), an engineering guard interval — with the important qualification that its
*scaling* is correct even though its *constant* is not derived.**

The evidence:

| Candidate origin | Supported by the code? |
|---|---|
| Analytically derived from filter poles | **No.** No pole is computed anywhere. `SettlingSamples` uses only `f_s` and `f_hp`. `Q` never enters; the filter order never enters; the low-pass cutoff never enters. |
| Based on an impulse or step response | **No.** No impulse or step response is computed anywhere in the project. |
| Empirically measured | **No.** No measurement establishes 3.0. The self-test *uses* `SettlingSamples` to decide where to start measuring attenuation (`if (i > settle) peak = max(...)`) — it consumes the value, it does not validate it. The only assertion about it is `filter.SettlingSamples > 0`, which is trivially true. |
| Engineering guard interval | **Yes.** The multiplier `3.0` is a bare literal with no derivation, and the quantity it multiplies is `1/f_hp`, the **period of the cutoff frequency**. |

**Stated plainly, as requested: the settling criterion is an arbitrary, conservative engineering
guard interval of three cycles of the high-pass cutoff frequency. It was not derived from the
filter's poles, was not measured, and is not validated by any test in this project.**

### The specific terminological error in the comments

Both `EegSpectralDiagnostics.cs` ("3 time constants at the 1 Hz high-pass") and
`CLAUDE_HANDOFF.md` ("3 × time constant of the HP") describe `3/f_hp` as three *time constants*.
It is not. For a filter with cutoff `f_c`:

```
    period of the cutoff:   T_c = 1 / f_c              = 1.000 s  at 1 Hz
    first-order time const: τ   = 1 / (2π f_c)         = 0.159 s  at 1 Hz
```

These differ by a factor of `2π ≈ 6.28`. So `3/f_hp` is three *cutoff periods*, which for a
first-order system would be `3 × 2π ≈ 18.85` time constants. The two phrasings are not
interchangeable and the comments should be corrected. (This is a documentation defect, not a
numerical one — the *value* is fine, as §9.5 shows.)

### What the criterion nonetheless gets right

The form `T_settle ∝ 1 / f_hp` is the **correct scaling**. The slowest pole in the cascade belongs
to the high-pass, and its time constant is inversely proportional to `f_hp` (see §9.4). So if the
cutoff were lowered to 0.5 Hz, the true settling time would double — and the implemented formula
doubles with it. The shape of the rule is right; only the constant `3.0` is unjustified.

## 9.4 The theoretical method for determining IIR settling time **[LIT + derivation]**

### 9.4.1 Settling is governed by the pole closest to the unit circle

A discrete IIR filter's zero-input response is a sum of modes, one per pole:

```
    y_ZI[n] = Σ_i  c_i · p_i ⁿ
```

where `p_i` are the poles. Each mode decays as `|p_i|ⁿ`. After enough samples the sum is dominated
by the pole of **largest magnitude** — the one closest to the unit circle. Define:

```
    r_max = max_i |p_i|
```

Convert to a time constant. Writing `|p|ⁿ = e^{n ln|p|}` and demanding `e^{−n/τ_n}`:

```
    τ_n = − 1 / ln(r_max)          [samples]
    τ   = τ_n / f_s                [seconds]
```

### 9.4.2 Settling to a stated tolerance

Choose a tolerance `ε` — the residual transient amplitude as a fraction of its initial value. Then:

```
    r_max ᴺ ≤ ε      ⟹      N ≥ ln(ε) / ln(r_max)
```

Equivalently in time constants: `N/τ_n ≥ ln(1/ε)`.

| Tolerance `ε` | in dB | Time constants `ln(1/ε)` |
|---|---|---|
| 10⁻¹ | −20 dB | 2.30 |
| 10⁻² | −40 dB | 4.61 |
| 10⁻³ | −60 dB | 6.91 |
| 10⁻⁴ | −80 dB | 9.21 |

**This is the correct method, and it makes the criterion explicit and defensible**: one states a
tolerance, and the settling time follows.

### 9.4.3 Extracting the pole radius from a biquad — no factorisation needed

For a normalised biquad `1 + a₁z⁻¹ + a₂z⁻²`, the two poles satisfy `p₁p₂ = a₂`. For a complex
conjugate pair, `p₂ = p̄₁`, so `|p₁|² = a₂`:

```
    r = √a₂
```

A one-line computation from a coefficient the code already stores. (For real poles,
`a₁² ≥ 4a₂`, one must solve the quadratic instead; for the Butterworth sections here the pairs are
always complex, since `Q > 0.5`.)

### 9.4.4 Applying it to THIS filter — the actual numbers

For an RBJ high-pass, `a₂ = (1 − α)/(1 + α)` with `α = sin(ω₀)/(2Q)`.

At `f_s = 250 Hz`, `f_hp = 1 Hz` (`ω₀ = 0.02513274`, `sin ω₀ = 0.02512909`):

| Section | Q | α | `a₂` | `r = √a₂` | `τ_n = −1/ln r` | `τ` (s) |
|---|---|---|---|---|---|---|
| HP₁ | 0.541196 | 0.0232162 | 0.954625 | 0.977050 | 43.1 samples | 0.172 |
| **HP₂** | **1.306563** | **0.0096163** | **0.980950** | **0.990429** | **104.0 samples** | **0.416** |
| LP₁ (40 Hz) | 0.541196 | 0.780034 | 0.123578 | 0.351537 | 0.96 samples | 0.004 |
| LP₂ (40 Hz) | 1.306563 | 0.323106 | 0.511596 | 0.715259 | 2.98 samples | 0.012 |

**The dominant pole is the high-Q high-pass section, HP₂**, at `r = 0.990429`,
`τ = 0.4159 s = 104 samples`. The low-pass sections settle in a handful of samples and are
irrelevant. This confirms the code comment's instinct — "Derived from the lowest cutoff — the
slowest thing the filter must resolve" — even though the arithmetic that follows it does not
implement that derivation.

Cross-check against the analogue prototype: the section's continuous-time pole has real part
`σ = ω_c/(2Q) = 2π·1/(2·1.306563) = 2.4045 s⁻¹`, giving `τ = 1/σ = 0.4159 s`. **Exact agreement**,
confirming both the bilinear-transform coefficients and the pole extraction.

### 9.4.5 What the implemented 750 samples actually buys

```
    N/τ_n = 750 / 104.0 = 7.212 time constants
    ε     = e^(−7.212)  = 7.36 × 10⁻⁴
    in dB = 20 log₁₀(7.36e−4) = −62.7 dB
```

**The implemented guard interval leaves a residual transient of 0.074% of its initial amplitude,
i.e. about −63 dB.** For band-power estimation this is far below any signal of interest and
comfortably conservative.

The properly derived alternatives:

| Criterion | Samples at 250 Hz / 1 Hz | Seconds |
|---|---|---|
| ε = 10⁻² (−40 dB) | 479 | 1.92 |
| ε = 10⁻³ (−60 dB) | 719 | 2.87 |
| **implemented (750)** | **750** | **3.00** |
| ε = 10⁻⁴ (−80 dB) | 958 | 3.83 |

**The implemented value sits essentially exactly at the −60 dB criterion.** That is a genuinely
sensible engineering choice. But it is a coincidence of `3 ≈ 6.91/(2π·0.3827)`, not a derivation,
and it would *not* survive a change of filter order or of the section Qs — both of which
`SettlingSamples` ignores entirely.

### 9.4.6 Where the formula would break

Because `SettlingSamples` ignores `Q` and the order, it silently mis-estimates if either changes:

| Change | True dominant `τ` | `3/f_hp` gives | Verdict |
|---|---|---|---|
| Current: 4th-order HP, Q_max = 1.3066 | 0.416 s | 3.00 s = 7.2 τ | Adequate (−63 dB) |
| 2nd-order HP, Q = 0.7071 | 0.225 s | 3.00 s = 13.3 τ | Very conservative |
| 6th-order HP, Q_max = 1.9319 | 0.615 s | 3.00 s = 4.9 τ | **ε = 7.6×10⁻³, only −42 dB** — noticeably weaker |
| 8th-order HP, Q_max = 2.5629 | 0.816 s | 3.00 s = 3.7 τ | **ε = 2.5×10⁻², only −32 dB** — inadequate |

Raising the filter order — an entirely plausible future change, since higher order means a sharper
transition — would silently erode the settling guarantee. **This is the concrete risk of the
current formula, and the reason to replace it.**

### 9.4.7 A note on what "settling" means for the *statistics*, not the transient

The analysis above concerns the **zero-input transient** from starting at zero state. There is a
second, separate consideration that the tolerance-based method does not capture: a high-pass with
cutoff `f_hp` needs enough samples to *estimate and remove* the low-frequency content it is
rejecting. A rule of thumb often quoted in the EEG methods literature is several cycles of the
cutoff frequency — which is, in fact, precisely what `3/f_hp` expresses. So there is a second,
looser reading under which the implemented rule is defensible on its own terms; it simply is not
the reading the comments give, and it is not tied to any tolerance.

The honest summary for a methods section is: *"a settling guard of three cycles of the high-pass
cutoff (750 samples at 250 Hz) was applied; analytically this corresponds to 7.2 time constants of
the dominant filter pole, leaving a residual start-up transient below −60 dB."* Both the rule as
implemented and its analytic justification are then on the record.

## 9.5 Recommendations for validating and documenting this parameter scientifically

Ordered by cost/benefit. **None of these are changes made by this document — it is read-only.**

### R1 — Compute the settling time from the poles (small change, large gain)

Replace the heuristic with the derivation, keeping a tolerance as an explicit, reportable
parameter:

```
    for each section s:  r_s = √(a₂,s)
    r_max = max_s r_s
    N_settle = ⌈ ln(ε) / ln(r_max) ⌉        with ε a named constant, e.g. 1e-3
```

This automatically tracks changes in order, in Q, in either cutoff and in `f_s`, and turns "3
seconds" into "settled to within 0.1% of the start-up transient", which is a statement a reviewer
can evaluate.

### R2 — Add a step/impulse-response assertion to the self-test

Empirically confirm whatever criterion is adopted:

- Feed a unit step; record `y[n]`; find the smallest `N` with `|y[n] − y_∞| < ε·|y_peak|` for all
  `n > N`; assert `N ≤ SettlingSamples`.
- Feed a unit impulse; assert `|h[n]| < ε` for all `n > SettlingSamples`.
- Feed a 10 Hz sine and assert the envelope reaches within `ε` of its steady-state amplitude
  before `SettlingSamples`.

This converts an assumption into a measurement and would catch a future order change immediately.

### R3 — Assert the guarantee at multiple configurations

Parametrise the test over `(f_s, f_hp, order) ∈ {250, 256, 500} × {0.5, 1.0, 2.0} × {2, 4, 6}` and
assert the settling claim holds in every combination. This is what would have caught the
order-sensitivity in §9.4.6.

### R4 — Report it in the run record

`DescribePreprocessing()` already prints `filter settled: {bool} ({m}/{n} samples, {t} s
required)`. Extend it, and the raw-EEG CSV header, with the *criterion* — the tolerance and the
dominant pole radius — so an archived recording is self-describing about when its filtered signal
became trustworthy.

### R5 — Fix the two terminological errors

`EegSpectralDiagnostics.cs` and `CLAUDE_HANDOFF.md` should say "three cycles of the high-pass
cutoff" rather than "3 time constants". And `EegSpectralDiagnostics` should ask the filter for
`SettlingSamples` rather than hard-coding `3.0 / 1.0`.

### R6 — Consider whether settling should be *per connection* rather than per filter construction

`m_SamplesFiltered` is reset only inside `EnsureInitialised`, which runs only when the filter is
null or the channel count changed. A stream that drops and reconnects with the same channel count
keeps both the filter state and the counter, so `filterReady` remains true. Whether that is right
depends on the gap: for a 50 ms dropout, keeping state is clearly correct; for a 30 s dropout, the
filter state is stale and arguably should be reset and re-settled. The code currently makes no
distinction. This is a design question, not a defect, but it should be answered deliberately.
---

# PART 10 — SPECTRAL WINDOW

## 10.1 The three window parameters **[CODE]**

| Parameter | Value | Where defined |
|---|---|---|
| **OUTER WINDOW** | **4.0 s** | `EegFeaturePipeline.m_WindowSeconds`, set by `ExperimentSceneBuilder`; also `EegSpectralDiagnostics.k_WindowSeconds = 4.0` |
| Minimum permitted window | 2.0 s | `EegFeaturePipeline.m_MinimumWindowSeconds` |
| **WELCH SEGMENT** | **2.0 s** | `EegFeaturePipeline.m_WelchSegmentSeconds` |
| **OVERLAP** | **50 %** | `EegSpectralAnalyzer.Welch(..., overlapFraction = 0.5)` — the default; `EegFeaturePipeline` never overrides it |
| FFT length | **auto** = next power of two ≥ segment | `fftLength = 0` → `NextPowerOfTwo(segmentSamples)` |

How the outer window is cut (`AnalyzeLatestWindow`):

```csharp
var stats = m_Filtered.GetStats();
m_Filtered.TryGetWindow(stats.latestTimestamp - seconds * 0.5,
                        seconds * 0.5, seconds * 0.5, out var window);
```

Centre = `latest − 2.0 s`, half-widths 2.0 s each → the window is
`[latest − 4.0 s, latest]`. It is **entirely retrospective**: it ends at the most recent sample
and looks backwards. It is not centred on any event.

Guards applied before analysis:

```csharp
if (seconds < m_MinimumWindowSeconds) seconds = m_MinimumWindowSeconds;   // never below 2 s
if (window.sampleCount < expected * 0.9)  → InsufficientSamples (flag only, continues)
if (window.sampleCount < WelchSegmentSeconds * fs) → InsufficientSamples, ABORT
```

The last guard is the hard one: fewer samples than one Welch segment means no spectrum is possible
at all, and the method returns early with `featureValidity = false`.

## 10.2 Derived quantities at f_s = 250 Hz **[CODE, computed]**

```
    segmentSamples  = round(2.0 s × 250 Hz)               = 500 samples
    step            = round(500 × (1 − 0.5))              = 250 samples
    fftLength  N    = NextPowerOfTwo(500)                 = 512
    bins            = N/2 + 1                             = 257
    binSpacingHz    = f_s / N       = 250 / 512           = 0.48828125 Hz
    physicalResHz   = f_s / L       = 250 / 500           = 0.5 Hz
    outer window    ≈ 4.0 s × 250 Hz                      ≈ 1000–1001 samples
```

## 10.3 THE CRITICAL DISTINCTION: physical resolution comes from the SEGMENT, not the window

This is the single most commonly-made error in describing a Welch analysis, and this project's
code gets it right and says so explicitly.

**Physical frequency resolution is set by the observation length of a single segment:**

```
    Δf_physical  =  1 / T_segment  =  f_s / L
```

With `T_segment = 2.0 s`:

```
    Δf_physical = 1 / 2.0 s = 0.5 Hz
```

**Therefore: the current configuration achieves ≈ 0.5 Hz physical frequency resolution — NOT
0.25 Hz.**

The claim "a 4-second window gives 0.25 Hz resolution" would be **true only if a single 4-second
FFT were computed**. It is **false** for this implementation, because Welch never transforms the
4-second window as a unit. It transforms three 2-second sub-segments and averages their
periodograms. The longest continuous observation the transform ever sees is 2 seconds, so 2
seconds is what sets the resolution.

The code encodes exactly this, in `PsdResult`:

```csharp
/// PHYSICAL frequency resolution, 1 / segmentDuration.
///
/// Distinct from binSpacingHz and reported separately on purpose: if the FFT is zero-padded,
/// bins get closer together but the observation does not get longer, so two sinusoids closer
/// than this remain unresolvable no matter how fine the grid.
public double physicalResolutionHz;
...
physicalResolutionHz = sampleRateHz / segmentSamples;      // = 1 / T_segment
binSpacingHz         = sampleRateHz / n;                   // = f_s / N_fft
```

and asserts it: `Math.Abs(psdA.physicalResolutionHz - 0.5) < 0.01` with the message *"a 2-s
segment gives 0.5 Hz PHYSICAL resolution"* (`ExperimentSelfTest.cs:2486`).

`EegSpectralDiagnostics` prints it correctly too:

```
WELCH:
  segment 2.0 s, 50% overlap, periodic Hann
  physical resolution ≈ 0.50 Hz (from the 2 s observation, not the FFT length)
```

**Note the numerical near-coincidence that could mislead a reader:** `binSpacingHz = 0.488 Hz` and
`physicalResolutionHz = 0.500 Hz` are almost equal here, because 500 zero-pads only to 512. They
are nonetheless different quantities and would diverge sharply if `fftLength` were set explicitly
(e.g. 2048 → bin spacing 0.122 Hz, physical resolution still 0.5 Hz).

## 10.4 What the 4-second outer window actually contributes

Not resolution. **Variance reduction, through averaging multiple overlapping periodogram
estimates.**

### Exact segment count for 4 s / 2 s / 50 %

The loop is:

```csharp
for (var start = 0; start + segmentSamples <= samples.Length; start += step)
```

so with `N` window samples, segment length `L` and step `S`:

```
    K = ⌊ (N − L) / S ⌋ + 1
```

With `N = 1000`, `L = 500`, `S = 250`:

```
    K = ⌊ (1000 − 500) / 250 ⌋ + 1  =  ⌊2⌋ + 1  =  3
```

**Exactly 3 Welch segments.** Their sample ranges:

| Segment | Samples | Time (relative to window start) |
|---|---|---|
| 1 | 0 … 499 | 0.000 – 2.000 s |
| 2 | 250 … 749 | 1.000 – 3.000 s |
| 3 | 500 … 999 | 2.000 – 4.000 s |

Segment 1 overlaps segment 2 by 50%, segment 2 overlaps segment 3 by 50%, and segments 1 and 3 do
not overlap at all.

(If the window returns 1001 samples — the inclusive-boundary case — `K = ⌊501/250⌋ + 1 = 3` still,
since a fourth segment would need 1250 samples. The count is robust.)

### Why averaging reduces variance **[LIT: Welch (1967); Bartlett (1948)]**

A single periodogram of a Gaussian random process is a **notoriously bad** estimator: its variance
does not decrease as the record lengthens. `Var{Î(f)} ≈ S(f)²` regardless of `N` — the relative
standard deviation is ~100%, and it is asymptotically distributed as `S(f)·χ²₂/2`. Making the
record longer gives *more* estimates that are each just as noisy, not a better estimate.

Averaging `K` **independent** periodograms reduces the variance by `1/K`:

```
    Var{P̂} / S²  ≈  1 / K
```

With 50%-overlapped Hann windows the segments are **not** independent — adjacent segments share
half their samples. The standard correction **[LIT: Welch (1967); Harris (1978)]** uses the
inter-segment correlation `ρ`, which for a Hann window at 50% overlap is `ρ ≈ 1/6 ≈ 0.167`:

```
    Var{P̂}/S²  ≈  (1/K) [ 1 + 2 Σ_{m=1}^{K−1} (1 − m/K) ρ²(m) ]
```

Only adjacent segments overlap, so only `ρ(1)` is non-zero. For `K = 3`:

```
    = (1/3)[ 1 + 2·(1 − 1/3)·(0.167)² ]
    = (1/3)[ 1 + 2·0.6667·0.02778 ]
    = (1/3)(1.0370)
    = 0.3457
```

| Estimator | Relative variance | Relative std. dev. | Equivalent d.o.f. ≈ 2/relvar |
|---|---|---|---|
| Single 4 s periodogram | 1.000 | 100 % | 2 |
| **This configuration (K=3, 50% Hann)** | **0.346** | **58.8 %** | **≈ 5.8** |
| 3 independent 2 s periodograms | 0.333 | 57.7 % | 6 |

**So the 4-second outer window buys roughly a 41% reduction in the standard deviation of each PSD
value, at the cost of halving the physical frequency resolution.** The overlap recovers most of
the benefit of true independence (0.346 vs 0.333) while allowing three segments where
non-overlapping segmentation of 4 s would give only two.

50% overlap with a Hann window is additionally the classic choice because periodic Hann at 50%
overlap **sums to a constant** — every sample contributes equally to the average, with no
amplitude modulation across the record. The `HannWindow` doc-comment states this.

## 10.5 The three-way tradeoff

For a fixed outer window `T` split into segments of length `T_seg` with 50% overlap:

```
    Δf_physical = 1 / T_seg              (frequency resolution — better with LONGER segments)
    K           ≈ 2T/T_seg − 1           (variance reduction — better with SHORTER segments)
    Δt          = T                      (time resolution — better with SHORTER windows)
```

Concretely, for a 4-second outer window:

| `T_seg` | Δf_physical | K | Rel. variance | Cycles at 4 Hz per segment |
|---|---|---|---|---|
| 4.0 s | 0.25 Hz | 1 | 1.00 | 16 |
| **2.0 s** | **0.50 Hz** | **3** | **0.346** | **8** |
| 1.0 s | 1.00 Hz | 7 | 0.148 | 4 |
| 0.5 s | 2.00 Hz | 15 | 0.069 | 2 |

**Assessment of the chosen operating point.**

- *Frequency resolution:* 0.5 Hz across a 4 Hz-wide band (theta 4–8, alpha 8–12) gives 8 bins per
  band. That is enough to integrate a band power meaningfully, and enough to locate an alpha peak
  to within ±0.5 Hz. It would **not** be enough to separate an individual alpha frequency of
  10.2 Hz from one of 10.5 Hz, or to resolve fine spectral structure. For band-power work — which
  is what this project does — it is adequate.
- *Variance:* ~5.8 equivalent degrees of freedom is modest. It is enough that a single window's
  estimate is not wild, but it is not enough for confident single-window inference. Any real
  analysis should average band power over multiple windows within a condition, which effectively
  multiplies the degrees of freedom.
- *Time resolution:* 4 s is coarse for event-related work. It is appropriate for sustained
  cognitive-load states — a trial or a task block — and inappropriate for anything resolving
  sub-second dynamics. Note also that consecutive analyses are not independent unless they are
  spaced ≥ 4 s apart.
- *Cycles at the lowest frequency:* the tooltip's rationale — "4 s gives ~16 cycles at 4 Hz" —
  is about the outer window. Per **segment** it is 8 cycles at 4 Hz, which is the number that
  matters for whether the lowest theta bin is well-estimated. Eight cycles is acceptable; fewer
  would be marginal. This is the constraint that sets the 2 s floor on `m_MinimumWindowSeconds`,
  whose tooltip says "Below 2 s there are too few theta cycles for a meaningful estimate."

**A caveat on the variance figure.** The `1/K` and `ρ`-corrected formulas above assume the signal
is stationary and approximately Gaussian over the window. Real EEG over 4 seconds is
*approximately* stationary in a resting or steady-task state, and markedly non-stationary across a
transition (a stimulus onset, a movement, a blink). The variance estimate should be read as a
best case.

---

# PART 11 — HANN + FFT + WELCH PSD

All of `EegSpectralAnalyzer.cs`. The class is **deliberately dependency-free**: it takes an array
and a sample rate and returns numbers. It knows nothing about LSL, the receiver, Unity scene state
or the experiment — "which is what makes it testable against synthetic signals with known answers
— and that is the only way to be sure a spectrum is right, since a plausible but wrong PSD looks
exactly like a correct one."

## 11.1 The periodic Hann window **[CODE]**

```csharp
public static double[] HannWindow(int length)
{
    var w = new double[length];
    for (var n = 0; n < length; n++)
        w[n] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / length));
    return w;
}
```

```
    w[n] = ½ (1 − cos(2πn / N)),      n = 0 … N−1
```

**Periodic (divisor `N`), not symmetric (divisor `N−1`).** The distinction is small — one sample —
but real, and the code states why:

> "For spectral estimation with overlapping segments the periodic form is correct: it makes the
> window's implied period exactly N samples, which is what the DFT assumes, and 50%-overlapped
> periodic Hann sums to a constant. The symmetric form is for FIR filter design. The two differ by
> one sample and the choice is stated here because it changes the window energy — and therefore
> every absolute PSD value — by a small but real amount."

This is correct and matches the convention used by `scipy.signal.welch` (`sym=False`) and MATLAB's
`hann(N,'periodic')`. **[LIT: Harris (1978)]**

Properties of the Hann window relevant here **[LIT]**: main-lobe width 4 bins (vs 2 for
rectangular), first sidelobe −31.5 dB, sidelobe roll-off −18 dB/octave. The wide main lobe is why
tones near a band edge leak across it (see PART 12.5); the low sidelobes are why a strong
low-frequency component does not contaminate distant bins.

**Verified [CODE]:** `hann[0] == 0` to 1e-12; `hann[4] == 1.0` exactly for `N = 8` — the periodic
form peaks at exactly 1.0 at index `N/2`, which the symmetric form does not
(`ExperimentSelfTest.cs:2460–2464`).

## 11.2 The FFT **[CODE]**

An in-place iterative radix-2 Cooley–Tukey decimation-in-time FFT, written locally:

> "Written locally rather than pulled in as a package: it is forty lines, it is deterministic, and
> adding a third-party dependency to a Quest build for this would be disproportionate."

```csharp
public static void Fft(double[] re, double[] im)
```

Structure:
1. **Bit-reversal permutation** of the input.
2. **Butterfly stages** for `len = 2, 4, 8, … n`, with twiddle factor
   `W = e^{−2πi/len}` advanced by repeated complex multiplication.

The transform computed is the standard unnormalised forward DFT:

```
    X[k] = Σ_{n=0}^{N−1} x[n] · e^{−2πi k n / N}
```

`NextPowerOfTwo(value)` doubles from 1 until `p ≥ value`. `Fft` throws
`ArgumentException("FFT length must be a power of two")` if `(n & (n−1)) != 0`.

**One implementation note.** The twiddle factor is advanced iteratively
(`nextRe = curRe*wRe − curIm*wIm; …`) rather than recomputed with `cos`/`sin` at each `k`. This is
fast, but iterated complex multiplication accumulates rounding error across a stage — for
`N = 512` in `double` precision the accumulated error is on the order of 1e-13 and utterly
negligible here, but it is the kind of thing worth knowing exists.

**Verified [CODE]:** the FFT of a unit impulse `x = [1,0,0,0,0,0,0,0]` is asserted to be exactly
flat and unity in every bin, real part 1.0 and imaginary part 0.0 to 1e-12
(`ExperimentSelfTest.cs:2470–2479`). This is a strong test — it exercises the bit-reversal, every
butterfly stage and every twiddle factor simultaneously, and any indexing error breaks it.

## 11.3 Segmentation and overlap **[CODE]**

```csharp
var segmentSamples = (int)Math.Round(segmentSeconds * sampleRateHz);
if (segmentSamples > samples.Length) segmentSamples = samples.Length;   // graceful shrink
if (segmentSamples < 8) throw new ArgumentException("window too short for a spectrum");

var step = Math.Max(1, (int)Math.Round(segmentSamples * (1.0 - overlapFraction)));

for (var start = 0; start + segmentSamples <= samples.Length; start += step) { ... }
```

Notable behaviours:
- Segment length is derived from the **actual** rate passed in, never a constant.
- If the supplied window is shorter than one segment, the segment shrinks to the window (so a 1.5 s
  window would be analysed as a single 1.5 s segment with 0.67 Hz resolution — silently, though
  `EegFeaturePipeline` prevents this by aborting first).
- A trailing partial segment is **discarded**, not zero-padded into the average. Correct: a partial
  segment would have different window energy and would bias the estimate.
- `if (segments == 0) throw new ArgumentException("no complete segment fitted in the window")` —
  caught per-channel by `EegFeaturePipeline`, which sets `NaN` band powers and raises
  `InsufficientSamples` rather than letting the exception escape.

## 11.4 De-meaning **[CODE]**

```csharp
double mean = 0d;
for (var i = 0; i < segmentSamples; i++) mean += samples[start + i];
mean /= segmentSamples;
...
re[i] = (samples[start + i] - mean) * window[i];
```

**Each segment is de-meaned independently, before windowing.** The stated reason:

> "A residual offset would otherwise leak out of DC across the low bins and contaminate theta,
> which sits only 4 Hz away."

This is correct and important. A DC offset multiplied by the Hann window produces a scaled copy of
the Hann window's own spectrum centred at 0 Hz — whose main lobe spans ±2 bins (±0.98 Hz here) and
whose sidelobes extend further. With the band-pass already removing content below 1 Hz the residual
offset should be tiny, but de-meaning makes it exactly zero at no cost.

**A consequence with real teeth, and one the project has already exploited (PART 16/17):**
de-meaning makes the PSD **invariant to a constant offset**. Two channels that differ *only* by a
DC offset produce **identical** spectra from *non-identical* samples. This is why
`AssessInterChannelIdentity` needs a second, band-power-based test — a sample-comparison test alone
would miss it. `SyntheticEegOutlet.ps1 -OffsetOnlyChannels` exists specifically to generate that
case.

Note the order: **de-mean, then window** — the correct order. Windowing first and de-meaning after
would remove the mean of the *windowed* segment, which is not the same thing and would not cancel
the DC leakage.

## 11.5 Zero padding **[CODE]**

```csharp
var n = fftLength > 0 ? fftLength : NextPowerOfTwo(segmentSamples);
if (n < segmentSamples) n = NextPowerOfTwo(segmentSamples);
...
Array.Clear(re, 0, n);  Array.Clear(im, 0, n);
for (var i = 0; i < segmentSamples; i++) re[i] = (samples[start + i] - mean) * window[i];
// "Anything past segmentSamples stays zero: that is the zero padding, and it
//  interpolates the spectrum without adding physical resolution."
```

At the live configuration: `500 → 512`, so only 12 samples of padding — a 1.024× oversampling of
the frequency grid.

### Why zero padding does NOT improve physical resolution **[LIT]**

Zero-padding a length-`L` record to length `N` computes:

```
    X_pad[k] = Σ_{n=0}^{L−1} x[n] w[n] e^{−2πi k n / N}
```

This is the **same continuous-frequency function** `X(f) = Σ x[n]w[n]e^{−2πifn}` evaluated at a
denser set of points. No new data has been added — the extra samples are zeros, which contain no
information about the signal.

Formally: windowing in time is convolution in frequency with the window's transfer function
`W(f)`. The Hann main-lobe width is `4/(L·T_s) = 4·Δf_physical`. Two sinusoids closer than roughly
one main-lobe width produce a single merged peak. **Adding zeros does not narrow `W(f)`** — `W(f)`
depends only on `L`, the number of *real* samples. Denser sampling of a merged peak still shows a
merged peak; it is merely drawn more smoothly.

What zero padding *does* buy: (a) a power-of-two length for the radix-2 FFT — the operative reason
here; (b) reduced *scalloping loss*, so a tone falling between bin centres is measured with less
amplitude error; (c) better visual peak localisation by interpolation.

**Verified [CODE]** — the project asserts both halves of this:
- `psdA.binSpacingHz < psdA.physicalResolutionHz` with the message *"zero padding makes bins closer
  than the physical resolution — it interpolates, it does not add information"*.
- Re-running the same 6 Hz signal with `fftLength: 2048` gives a genuinely finer bin grid
  (`0.122 Hz` vs `0.488 Hz`) while the theta band power changes by **< 10 %** — asserted at
  `ExperimentSelfTest.cs:2560`. Band power is a property of the signal, not of the FFT length.

## 11.6 PSD normalisation — the implemented equation **[CODE]**

```csharp
double windowEnergy = 0d;
foreach (var w in window) windowEnergy += w * w;
...
for (var k = 0; k < bins; k++)
{
    var power = re[k]*re[k] + im[k]*im[k];
    if (k > 0 && k < n / 2) power *= 2.0;                       // one-sided folding
    accumulated[k] += power / (sampleRateHz * windowEnergy);
}
...
psd[k] = accumulated[k] / segments;
frequencies[k] = k * sampleRateHz / n;
```

The complete implemented estimator:

```
                     1     K−1        c_k · |X_j[k]|²
    P̂[k]   =        ───   Σ      ─────────────────────
                     K     j=0       f_s · Σ_n w[n]²

    where   X_j[k] = FFT_N{ (x_j[n] − x̄_j) · w[n] }
            c_k    = 2  for  0 < k < N/2
            c_k    = 1  for  k = 0 (DC) and k = N/2 (Nyquist)
            f[k]   = k · f_s / N
```

### Why each factor is there

| Factor | Purpose |
|---|---|
| `Σ w[n]²` | Compensates the window's energy loss. For Hann, `Σw² ≈ 0.375·L`. Dividing by `Σw²` rather than by `L` or `L²` is what makes the result a *correctly scaled* density — this is the standard "power normalisation" (as opposed to `(Σw)²`, the "amplitude/coherent-gain normalisation" used for peak amplitude estimation). |
| `f_s` | Converts power **per bin** into power **per hertz**. Without it the result would change whenever the FFT length changed. |
| `c_k = 2` for interior bins | One-sided folding: bin `k` and bin `N−k` carry the same magnitude for a real signal, so the negative-frequency energy is folded into the positive-frequency bin. |
| `1/K` | Welch averaging over segments. |

The doc-comment states the normalisation explicitly, "because an unstated one is unreproducible" —
a good practice that this document endorses.

### DC and Nyquist are NOT doubled **[CODE]**

```csharp
if (k > 0 && k < n / 2) power *= 2.0;
```

Bin 0 (DC) and bin `N/2` (Nyquist) have **no distinct negative-frequency partner** — they are their
own mirror images. Doubling them is "a common error that inflates both ends."

**Verified structurally** (`ExperimentSelfTest.cs:2592`): the test greps the source for the literal
`k > 0 && k < n / 2` and asserts its presence, so a refactor cannot silently change the folding
rule. Similarly `sampleRateHz * windowEnergy` (line 2595) and `- mean)` (line 2598) are asserted
present.

In this pipeline DC is doubly irrelevant — it is de-meaned to zero *and* removed by the 1 Hz
high-pass — but correctness at the edges matters if the class is reused.

### One-sided output shape

`bins = N/2 + 1 = 257`, frequencies `0, 0.488, 0.977, …, 125.0 Hz`. Bin 256 is Nyquist = 125 Hz.

## 11.7 Verification of the whole spectral chain **[CODE — `CheckEegSpectral`, f_s = 250 Hz, 4 s]**

| Test | Signal | Assertion | Result |
|---|---|---|---|
| A | 6 Hz sine, A=1 | peak within ±0.5 Hz of 6.0 | PASS |
| A | 6 Hz sine | `theta > 20 × alpha` | PASS |
| A | — | `physicalResolutionHz == 0.5 ± 0.01` | PASS |
| A | — | `binSpacing < physicalResolution` | PASS |
| B | 10 Hz sine, A=1 | peak within ±0.5 Hz of 10.0 | PASS |
| B | 10 Hz sine | `alpha > 20 × theta` | PASS |
| C | 6 Hz + 10 Hz, equal amplitude | theta/alpha ratio ∈ (0.7, 1.4) | PASS |
| **D** | **6 Hz A=2 + 10 Hz A=1** | **theta/alpha ratio ∈ (3.4, 4.6)** — power scales as amplitude **squared** | PASS |
| — | 6 Hz sine, `fftLength = 2048` | theta power within 10 % of the unpadded estimate | PASS |
| E | uniform white noise | theta/alpha ratio ∈ (0.5, 2.0) — two equally wide bands, no dominant peak | PASS |

**Test D is the quantitatively strongest.** Doubling the amplitude of the 6 Hz component must
quadruple its power, giving a ratio of exactly 4 against the unchanged 10 Hz component. Getting
4.0 rather than 2.0 or 8.0 confirms simultaneously: the magnitude-squared, the window-energy
normalisation, the one-sided folding and the band integration. A pipeline with an error in any one
of those would fail this test.

**Additional quantitative validation against absolute amplitude [LIVE-synthetic]:** the
`SyntheticEegOutlet.ps1` run recorded in `CLAUDE_HANDOFF.md` injected known tones and compared the
recovered band power against the analytic `A²/2` for a sinusoid:

| Electrode | Injected | Expected `A²/2` | Unity measured | Error |
|---|---|---|---|---|
| Fz | 6.5 Hz, A = 2.0 | 2.000 | **1.9997** | −0.015 % |
| P3 | 9.5 Hz, A = 3.5 | 6.125 | **6.1232** | −0.029 % |
| Pz | 10.5 Hz, A = 4.0 | 8.000 | **7.9976** | −0.030 % |
| F3 | 5.5 Hz, A = 1.5 | 1.125 | **1.1248** | −0.018 % |

Agreement to better than 0.03% for tones well inside a band. **This is an absolute-scale
validation of the entire chain — receiver, marshalling, filter, ring buffer, de-interleave, Welch
and band power — not merely a relative one.** It is the single strongest piece of evidence in the
project that the spectral estimator is correct.

---

# PART 12 — BANDPOWER

## 12.1 Band definitions **[CODE]**

```csharp
public static readonly EegBand Theta = new EegBand("theta", 4.0, 8.0);
public static readonly EegBand Alpha = new EegBand("alpha", 8.0, 12.0);
```

| Band | Range | Width |
|---|---|---|
| **Theta** | **4.0 – 8.0 Hz** | 4 Hz |
| **Alpha** | **8.0 – 12.0 Hz** | 4 Hz |

Both are conventional **[LIT]**, though not universal: alpha is often defined 8–13 Hz. Using 8–12
excludes 12–13 Hz, which in some individuals carries genuine alpha power. Both bands being exactly
4 Hz wide is convenient — it makes the theta/alpha ratio a comparison of equal-bandwidth
integrals, and it is why the white-noise self-test can assert a ratio near 1.

Note that 8.0 Hz is simultaneously the theta upper edge and the alpha lower edge. Because the
integration only includes bins whose centre falls strictly within `[low, high]` and no bin centre
lands exactly on 8.0 Hz at this grid (8 / 0.48828125 = 16.384), **no bin is double-counted**. This
is fortuitous rather than guaranteed: at a different FFT length a bin centre could land exactly on
8.0 Hz and would then be counted in both bands. **[GAP]** — a minor latent issue.

## 12.2 The implemented integration **[CODE]**

```csharp
public static double BandPower(PsdResult psd, double lowHz, double highHz)
{
    double total = 0d;
    var previousIndex = -1;

    for (var k = 0; k < psd.frequencies.Length; k++)
    {
        var f = psd.frequencies[k];
        if (f < lowHz || f > highHz) continue;

        if (previousIndex >= 0)
        {
            var df = f - psd.frequencies[previousIndex];
            total += 0.5 * (psd.psd[k] + psd.psd[previousIndex]) * df;
        }
        previousIndex = k;
    }

    // A band narrower than one bin contains no interval to integrate over.
    if (previousIndex >= 0 && total == 0d)
        total = psd.psd[previousIndex] * psd.binSpacingHz;

    return total;
}
```

## 12.3 The mathematics

Band power is the **integral of the power spectral density over the band**:

```
                 f_high
    P_band  =    ∫       S(f) df
                 f_low
```

Since `S(f)` is sampled at discrete bin centres, the integral is approximated by the **trapezoidal
rule**:

```
                 k_high−1
    P̂_band  =    Σ        ½ ( P̂[k] + P̂[k+1] ) · (f[k+1] − f[k])
                 k=k_low

              =  Δf · [ ½P̂[k_low] + P̂[k_low+1] + … + P̂[k_high−1] + ½P̂[k_high] ]
```

for uniform bin spacing `Δf`.

**Dimensional check:**

```
    [P̂]  =  units² / Hz
    [df] =  Hz
    ⟹  [P_band]  =  units²      ✔  a POWER, not a density
```

## 12.4 Why bin spacing must be accounted for **[CODE]**

The doc-comment is explicit:

> "FREQUENCY-AWARE: it multiplies by the bin spacing, so the result is a power in (input units)²
> and does not change if the FFT length changes. Summing bare bin values would give a number that
> silently doubled whenever the FFT was zero-padded further."

The failure mode, made concrete. Suppose one summed bin values without `df`:

```
    naïve  =  Σ_{k in band} P̂[k]
```

Doubling the FFT length halves `Δf`, so the band contains **twice as many bins**, each holding
roughly the same *density*. The naïve sum therefore **doubles**, purely from a change in the
transform length. The number would look like a signal change and be entirely an artefact.

Multiplying by `df` restores invariance: twice as many bins, each contributing half the width, same
integral. **Verified [CODE]:** `ExperimentSelfTest.cs:2560` re-runs the identical signal with
`fftLength: 2048` (bin spacing 0.122 vs 0.488 Hz) and asserts the theta power changes by less than
10 %.

Two implementation details worth noting:

- The code computes `df = f[k] − f[previousIndex]` **per interval** rather than assuming uniform
  spacing. Correct and more general than necessary, since the grid *is* uniform.
- The `total == 0d` fallback handles a band narrower than one bin by returning
  `P̂[k] × binSpacing` — "the honest approximation". It also fires, harmlessly, in the degenerate
  case where a band's PSD really is zero.

## 12.5 The band-edge truncation — a real, quantifiable bias **[CODE, derived]** **[GAP]**

The loop includes only bins whose **centre** falls within `[low, high]`. It does **not**
interpolate the PSD at the band edges. At `Δf = 0.48828125 Hz`:

| Band | First bin | Last bin | Bins | Integrated span | Nominal span | Coverage |
|---|---|---|---|---|---|---|
| Theta 4–8 Hz | k=9, 4.3945 Hz | k=16, 7.8125 Hz | 8 | **3.418 Hz** | 4.000 Hz | **85.4 %** |
| Alpha 8–12 Hz | k=17, 8.3008 Hz | k=24, 11.7188 Hz | 8 | **3.418 Hz** | 4.000 Hz | **85.4 %** |

So the reported band power is the integral over a span **~15 % narrower than the nominal band**.
Consequences:

- **Absolute band power is systematically biased low** by roughly 15 % (exactly how much depends on
  the PSD's shape near the edges). This matters if the numbers are ever compared to values from
  another toolbox.
- **The bias is nearly identical for theta and alpha** here (both lose the same 0.582 Hz), so the
  **theta/alpha ratio is largely unaffected**.
- **The bias is constant across channels and conditions**, so it cancels entirely in
  baseline-normalised dB values and in any within-subject contrast.
- **It would change if `f_s` or the FFT length changed**, since the bin grid would move relative to
  the band edges. Two recordings at different sample rates are therefore not exactly comparable in
  absolute band power.

A proper fix — used by, e.g., `scipy.integrate.trapezoid` over an interpolated edge, or by adding
partial end trapezoids at exactly `f_low` and `f_high` — would remove the bias. It is not
implemented. This is worth recording in a methods section as a known, bounded, systematic offset
rather than treating the absolute values as exact.

## 12.6 Expected units

```
    P̂[k]     :  AURA-native-units² / Hz          ( psdUnits )
    Δf       :  Hz
    P_band   :  AURA-native-units²               ( powerUnits )
```

Both `LatestEegFeatures.powerUnits` and `.psdUnits` are populated from the montage in both analysis
paths, and would automatically become `µV²` / `µV²/Hz` if `unitsConfirmed` were ever set (PART 7).

The exploratory ratio `frontalTheta / posteriorAlpha` is **dimensionless** — the units cancel — and
is therefore the one derived quantity that is meaningful even with the scaling unknown. So is
`deltaThetaDb`. This is worth emphasising to a researcher: **the unresolved units do not block
within-subject relative analyses; they block only absolute reporting.**

## 12.7 `PeakFrequency` — a validation helper **[CODE]**

```csharp
public static double PeakFrequency(PsdResult psd, double lowHz, double highHz)
```

Returns the frequency of the largest PSD value in a range. Documented "For validation" and used
only by the self-test (to assert a 6 Hz sine peaks at 6 Hz). It is **not** part of the feature set
— there is no individual-alpha-frequency estimation in the pipeline. **[GAP]**, and a natural
future addition, since individual alpha frequency varies by several Hz across people and fixed
band edges can misattribute a person's alpha to the theta band **[LIT: Klimesch (1999)]**.
---

# PART 13 — ROI FEATURES

## 13.1 The two PRIMARY features **[CODE]** — `EegFeaturePipeline.ComputeRoi`

```csharp
var frontal   = m_Montage.ResolveRoi(AuraMontageConfig.FrontalThetaRoi,   out var fp);
var posterior = m_Montage.ResolveRoi(AuraMontageConfig.PosteriorAlphaRoi, out var pp);

// [bounds checks on every index, all-or-nothing]

double theta = 0d, alpha = 0d;
foreach (var index in frontal)   theta += features.thetaPerChannel[index];
foreach (var index in posterior) alpha += features.alphaPerChannel[index];

features.frontalTheta    = theta / frontal.Length;
features.posteriorAlpha  = alpha / posterior.Length;
features.roiValid        = true;
```

Formally:

```
    FrontalTheta    =  ⅓ [ P_theta(F3) + P_theta(Fz) + P_theta(F4) ]

    PosteriorAlpha  =  ⅓ [ P_alpha(P3) + P_alpha(Pz) + P_alpha(P4) ]
```

| Feature | Definition | Electrodes | Band | Units |
|---|---|---|---|---|
| **FrontalTheta** | arithmetic mean of per-channel theta band power | F3, Fz, F4 (indices 1, 2, 3) | 4–8 Hz | AURA-native-units² |
| **PosteriorAlpha** | arithmetic mean of per-channel alpha band power | P3, Pz, P4 (indices 5, 6, 7) | 8–12 Hz | AURA-native-units² |

**Important properties of the implementation:**

- The mean is taken over **power**, not over amplitude and not over log-power. Averaging power is
  the right choice for combining independent estimates of a band's energy; averaging
  log-power would produce a geometric mean and would be a different (also defensible, but
  different) statistic. This should be stated explicitly in a methods section.
- Fp1 and Cz are excluded from both ROIs (PART 5.3).
- Resolution is **by label**, so re-mapping the montage moves the ROI automatically.
- Resolution is **all-or-nothing**: if any of the three electrodes cannot be resolved or is out of
  range for the current window, `roiValid = false`, `RoiUnresolved` is flagged, and `frontalTheta`
  / `posteriorAlpha` remain `double.NaN`.
- Per-channel values are **always kept** in `thetaPerChannel[]` / `alphaPerChannel[]` alongside
  `channelLabels[]` — "never collapsed away". A researcher can always inspect the constituents.

## 13.2 The EXPLORATORY derived features **[CODE]**

```csharp
if (features.roiValid && features.frontalTheta > 0d && features.posteriorAlpha > 0d)
{
    features.thetaAlphaRatio = features.frontalTheta / features.posteriorAlpha;
    features.logThetaAlpha   = Math.Log(features.frontalTheta) - Math.Log(features.posteriorAlpha);
    features.derivedValid    = true;
}
```

```
    ThetaAlphaRatio  =  FrontalTheta / PosteriorAlpha

    LogThetaAlpha    =  ln(FrontalTheta) − ln(PosteriorAlpha)  =  ln(ThetaAlphaRatio)
```

**Both are implemented.** Note that `logThetaAlpha` uses the **natural** logarithm
(`Math.Log`), not log₁₀ — so it is *not* a decibel quantity and must not be reported as one. It is
`ln(ratio)`; multiply by `10/ln(10) ≈ 4.343` to convert to dB if that is ever wanted.

**Why a log form is computed at all [LIT]:** power ratios are strictly positive and strongly
right-skewed. The log transform makes the distribution far closer to symmetric/Gaussian, which is a
prerequisite for most parametric statistics. Log-transforming EEG power before analysis is standard
practice **[LIT: Gasser, Bächer & Möcks (1982)]**. Reporting a raw ratio and running a t-test on it
would be poor practice; the log form exists to avoid that.

Both are guarded against non-positive inputs, so no `log(0)` or `log(negative)` can occur, and
`derivedValid` distinguishes "computed" from "unavailable".

## 13.3 PRIMARY vs EXPLORATORY — the distinction as the code makes it

The code marks the distinction in four separate places, which is a good sign that it is intentional
rather than incidental:

1. **In the data model** — `LatestEegFeatures`:
   ```csharp
   /// <summary>EXPLORATORY. Not a validated workload score.</summary>
   public double thetaAlphaRatio = double.NaN;
   public double logThetaAlpha   = double.NaN;
   ```
2. **In the text diagnostic** — `EegSpectralDiagnostics`:
   ```
   DERIVED (EXPLORATORY — not a validated workload score):
     theta/alpha ratio = ...
     log theta - log alpha = ...
   ```
3. **In the researcher UI** — `EegResearcherMonitor` renders the ROI values as primary content with
   their units, and puts the ratio under a separate `EXPLORATORY` heading followed by the literal
   line `"not a validated score"`.
4. **In the validity flag** — `derivedValid` is separate from `roiValid` and from
   `featureValidity`.

| Tier | Feature | Status |
|---|---|---|
| **PRIMARY** | `frontalTheta` | Directly measured band power over a defined ROI. Interpretable as "mean frontal theta power in this window". |
| **PRIMARY** | `posteriorAlpha` | Same, parietal alpha. |
| **PRIMARY** | `thetaPerChannel[]`, `alphaPerChannel[]` | Per-electrode constituents. Always retained. |
| **EXPLORATORY** | `thetaAlphaRatio` | A dimensionless index. Not validated in this project. |
| **EXPLORATORY** | `logThetaAlpha` | Its natural-log form. Not validated in this project. |
| **CONDITIONAL** | `deltaThetaDb`, `deltaAlphaDb` | Computed only when a baseline exists. See PART 15. |

## 13.4 Explicit statement: no ratio here is a validated cognitive-load classifier

**Stated plainly, as requested: the theta/alpha ratio computed by this project is NOT a validated
cognitive-load classifier, and must not be described as one.**

The reasons are cumulative and each is sufficient on its own:

1. **No validation study has been run in this project.** No participant data has been collected,
   no condition contrast has been computed, and no relationship between the ratio and any task
   manipulation has been tested. There is no evidence *within this project* that the number tracks
   anything.
2. **No classifier exists.** There is no threshold, no training, no cross-validation, no
   discriminative model of any kind in the codebase. "Classifier" would imply a decision rule; the
   code produces a scalar.
3. **The ratio is not currently computed at runtime at all** (PART 1.3), so it has never been
   observed under experimental conditions.
4. **The one live observation of it is untrustworthy.** The single live run produced a ratio of
   **36.94** with bit-identical values on all eight channels — the anomaly of PART 17.
5. **The units are unresolved**, so while the ratio itself is dimensionless and therefore immune to
   the unknown scale factor, its constituents cannot be reported in physical units.
6. **[LIT]** Even in the published literature, theta/alpha ratios are used as *indices* whose
   relationship to load is population-level, task-dependent and confounded by arousal, drowsiness
   and individual alpha frequency. They are not established single-trial classifiers.

The honest description for a methods section: *"an exploratory dimensionless index computed as the
ratio of mean frontal theta power to mean posterior alpha power, retained for hypothesis
generation."*

## 13.5 Validation, assumptions, and what could invalidate

**Validated [CODE]:** ROI resolution both directions and all-or-nothing behaviour
(`ExperimentSelfTest.cs:2611–2680`); the arithmetic itself is a three-term mean and is not
separately tested, but its inputs (`thetaPerChannel[]`) are validated to 0.03 % against analytic
`A²/2` in the synthetic-stream run (PART 11.7).

**Assumptions:**
1. F3, Fz, F4 are actually at those scalp locations — inherits the montage caveat (PART 5.7).
2. An unweighted mean across three electrodes is the right aggregation. Alternatives (Laplacian
   re-referencing, weighting by signal quality, taking the median for robustness to one bad
   channel) are not implemented. **[GAP]**
3. All three electrodes in an ROI are of comparable quality. There is **no per-electrode rejection**
   — a flatlined F4 would drag `frontalTheta` down by a third and only raise a window-level flag,
   not exclude the channel. **[GAP]**
4. Averaging power (rather than log-power) is appropriate.

**What could invalidate:** a re-ordered montage; a single bad electrode; a change of reference
scheme at the amplifier (which would alter what "frontal" even means, since all EEG is a
difference measurement and the reference is undocumented).

---

# PART 14 — SCIENTIFIC RATIONALE

**This entire part is [LIT] except where explicitly marked.** It describes what the literature
generally reports. It does **not** describe anything IKEA_EEG has demonstrated.

## 14.1 Frontal midline theta

**What the literature generally reports.** Theta-band (≈4–8 Hz) activity recorded over frontal
midline sites — Fz most prominently, with F3 and F4 contributing — increases in power with
increasing working-memory load and sustained attentional demand. It is often referred to as
"frontal midline theta" (FMθ) and source-localisation work commonly attributes it to anterior
cingulate cortex and adjacent medial prefrontal regions.

Representative findings:

- Gevins et al. (1997) reported increasing frontal midline theta with increasing n-back working
  memory load, concurrent with decreasing parietal alpha.
- Klimesch (1999), in a broad review, described theta synchronisation as associated with encoding
  of new information and with increasing task demands — in explicit contrast to alpha's behaviour.
- Jensen & Tesche (2002) showed frontal theta amplitude increasing parametrically with memory load
  in a Sternberg task (MEG).
- Cavanagh & Frank (2014) reviewed frontal theta as a signature of cognitive control, arguing it
  indexes the need for control rather than load *per se*.
- Onton, Delorme & Makeig (2005) reported frontal midline theta increasing with the number of items
  held in working memory.

**Why this montage supports it.** F3/Fz/F4 spans the frontal midline and its immediate flanks,
which is where FMθ is maximal. Averaging the three is a reasonable, conventional ROI. The exclusion
of Fp1 is appropriate, since frontopolar sites are dominated by ocular artefact.

**Caveats the literature itself records.** FMθ is not present in all individuals at detectable
levels; it overlaps spectrally with drowsiness-related theta, which moves in the *opposite*
direction with respect to engagement; and frontal electrodes are the ones most contaminated by
blinks, saccades and frontalis EMG.

## 14.2 Posterior / parietal alpha

**What the literature generally reports.** Alpha-band (≈8–12/13 Hz) power recorded over parietal
and occipital sites typically **decreases** (event-related desynchronisation, ERD) with increasing
task engagement, visual attention and working-memory load — the classic inverse relationship to
frontal theta. Alpha is maximal with eyes closed and in relaxed wakefulness (Berger, 1929) and
attenuates on eye opening and on task engagement.

Representative findings:

- Berger (1929) — the original description of the alpha rhythm and its blocking.
- Pfurtscheller & Lopes da Silva (1999) — the canonical account of event-related
  desynchronisation/synchronisation, including parietal alpha ERD with task engagement.
- Klimesch (1999) — alpha desynchronisation with attentional and semantic memory demands;
  emphasises individual alpha frequency as a moderating variable.
- Gevins et al. (1997) — parietal alpha decreasing as frontal theta increases across n-back loads.
- Jensen & Mazaheri (2010) — the "gating by inhibition" framework, in which alpha power reflects
  functional inhibition of task-irrelevant cortical regions.

**Important nuance, often lost.** Alpha does not *only* decrease with load. In tasks requiring
suppression of distracting visual input or internal retention, **posterior alpha can increase** —
consistent with the inhibition account. So the direction of an alpha effect is task-dependent, and
a hypothesis of "alpha goes down under load" is not universally safe.

**Why this montage supports it.** P3/Pz/P4 spans the parietal midline and flanks, where alpha is
prominent. Occipital sites (O1/Oz/O2) would show alpha even more strongly but are not present in
this 8-channel montage — a limitation to declare.

## 14.3 The two together as a cognitive-load index

**What the literature generally reports.** The reciprocal pattern — frontal theta up, posterior
alpha down — has been proposed repeatedly as a workload index, and the theta/alpha ratio has been
used in operator-workload and adaptive-automation research.

Representative sources:

- Gevins & Smith (2003) — neurophysiological measures of cognitive workload for adaptive systems.
- Holm et al. (2009) — the "Task Load Index" based on frontal theta and parietal alpha.
- Borghini et al. (2014) — a review of EEG/EOG/ECG measures of operator workload, drowsiness and
  fatigue.
- Antonenko et al. (2010) — EEG measures in the cognitive-load-theory tradition.

**Caveats that must accompany any use of it:**

1. Effects are typically **group-level and within-subject**; between-subject absolute values vary
   enormously with skull thickness, electrode impedance and individual anatomy.
2. Individual alpha frequency varies by several Hz **[LIT: Klimesch, 1999]**; fixed 8–12 Hz edges
   can misattribute one person's alpha into the theta band. This project uses fixed edges and does
   **not** estimate individual alpha frequency **[GAP]**.
3. Theta and alpha both respond to **arousal and drowsiness**, which are confounded with load in
   any long session.
4. Muscle and ocular artefact contaminate both bands, at the frontal sites especially.
5. VR adds specific confounds: head movement, neck EMG from a headset's weight, altered visual
   input, and reduced blink rate.

## 14.4 What IKEA_EEG has actually validated — the essential separation

**Stated plainly, as requested: IKEA_EEG has NOT demonstrated a cognitive-load effect. It has not
yet attempted to.**

| Claim | Established by this project? | Evidence |
|---|---|---|
| The system can receive real EEG from AURA over LSL | **YES** | 1000–2200 samples per multi-second capture **[LIVE]** |
| EEG samples and experiment events share one clock | **YES** | Event-window test PASS, 750 samples, brackets the event **[LIVE]** |
| The band-pass has the intended frequency response | **YES** | Measured dB table, 6 probe frequencies **[CODE]** |
| The FFT is correct | **YES** | Unit-impulse test exact to 1e-12 **[CODE]** |
| The Welch PSD is correctly normalised | **YES** | Five synthetic tests + absolute `A²/2` agreement to 0.03 % **[CODE + synthetic LIVE]** |
| Band power integrates a density correctly | **YES** | FFT-length invariance **[CODE]** |
| Channels stay separate through the extraction path | **YES** | 8-channel synthetic stream, 8 distinct band powers **[synthetic LIVE]** |
| **Frontal theta increases with cognitive load** | **NO** | No participant data collected |
| **Posterior alpha decreases with cognitive load** | **NO** | No participant data collected |
| **The theta/alpha ratio tracks task difficulty** | **NO** | No participant data collected |
| **The measured values reflect cortical activity** | **NOT ESTABLISHED** | The one live feature run was anomalous (PART 17); the units are unresolved (PART 7) |
| **Features are computed during an experiment run** | **NO** | `AnalyzeLatestWindow` is never called at runtime (PART 1.3) |

The project has validated a **measurement instrument** to a good standard. It has not yet performed
a **measurement of a phenomenon**. Those are different achievements, and conflating them is the
specific risk this section exists to prevent.

**The correct current statement:** *"A real-time EEG acquisition and spectral-feature pipeline was
implemented and validated against synthetic signals with analytically known spectra, and its
time-synchronisation was validated against live hardware. No participant data has yet been
collected, and no cognitive-load effect has been tested."*

---

# PART 15 — BASELINE

## 15.1 What exists in code **[CODE]**

`EegFeaturePipeline`:

```csharp
public const double MinimumBaselineSeconds = 4.0;

double m_BaselineFrontalTheta   = double.NaN;
double m_BaselinePosteriorAlpha = double.NaN;
double m_BaselineSeconds;
string m_BaselineDetail = "no baseline captured";

/// <summary>True when a baseline has been explicitly captured. NEVER inferred.</summary>
public bool baselineAvailable =>
    !double.IsNaN(m_BaselineFrontalTheta) && !double.IsNaN(m_BaselinePosteriorAlpha) &&
    m_BaselineFrontalTheta > 0d && m_BaselinePosteriorAlpha > 0d;
```

`CaptureBaseline(startTimestamp, endTimestamp, out detail)` — five rejection gates, in order:

| # | Gate | Message on failure |
|---|---|---|
| 1 | `seconds >= MinimumBaselineSeconds` (4.0 s) | "a baseline must be at least 4 s; X was requested" |
| 2 | `filterReady` | "the filter has not settled; a baseline taken now would be transient" |
| 3 | The interval is still in the filtered buffer, and holds ≥ one Welch segment | "the requested baseline interval is not (or no longer) in the buffer" |
| 4 | `features.roiValid` | "the baseline window has no valid ROI: …" |
| 5 | **`features.quality == EegQualityFlags.None`** | "the baseline window is flagged (…); a contaminated baseline would distort every later dB value" |

Gate 5 is the strict one and is well-judged: **a flagged baseline is refused outright**, including
one flagged for `IdenticalChannels`. The code comment notes this closes the door on a duplicated-
channel baseline becoming the reference for every later dB value.

Two further design properties:

```csharp
var saved = latest;
var features = AnalyzeWindow(window, m_Receiver.metadata.nominalSrate);
latest = saved;   // a baseline capture must not overwrite the live snapshot
```

and:

> "Never inferred. Silently taking the first seconds of a run would make every later dB value
> relative to whatever the participant happened to be doing while the headset settled — which is
> not a baseline, it is an accident."

`ClearBaseline()` sets everything back to `NaN` and later dB values become unavailable again.

## 15.2 The normalisation **[CODE]**

Inside `AnalyzeLatestWindow`:

```csharp
features.baselineAvailable = baselineAvailable;

if (baselineAvailable && features.roiValid)
{
    features.deltaThetaDb = 10.0 * Math.Log10(features.frontalTheta   / m_BaselineFrontalTheta);
    features.deltaAlphaDb = 10.0 * Math.Log10(features.posteriorAlpha / m_BaselinePosteriorAlpha);
}
```

```
    Δθ_dB  =  10 · log₁₀ ( FrontalTheta_task   / FrontalTheta_baseline )

    Δα_dB  =  10 · log₁₀ ( PosteriorAlpha_task / PosteriorAlpha_baseline )
```

This matches the requested formula exactly: `delta_dB = 10 log10(P_task / P_baseline)`.

The factor **10**, not 20, is correct: these are **power** quantities, and the decibel of a power
ratio is `10 log₁₀`. (`20 log₁₀` applies to amplitude ratios. The filter's `GainDbAt` correctly
uses 20, because a magnitude response is an amplitude ratio. Both are right in their own place.)

Interpretation:

| Δ dB | Power ratio | Meaning |
|---|---|---|
| +3 dB | ×2.00 | Task power is double the baseline |
| +1 dB | ×1.26 | +26 % |
| **0 dB** | **×1.00** | **Identical to baseline** |
| −1 dB | ×0.79 | −21 % |
| −3 dB | ×0.50 | Half the baseline |
| −10 dB | ×0.10 | One tenth |

## 15.3 Absolute task power vs. task/baseline normalisation — the conceptual difference

### Absolute task power

`FrontalTheta = 2.28 × 10⁻⁶ AURA-native-units²`. What can be done with it?

- Compared with the same participant's other windows in the same session: **yes**.
- Compared with another participant: **only if** electrode impedance, cap fit, skull thickness,
  amplifier gain and reference are equivalent — which they are not.
- Compared with a published µV² value: **no** (units unresolved, PART 7).
- Interpreted as "high" or "low" theta: **no**. There is no scale on which to judge it.

The dominant sources of variance in an absolute EEG power value are almost entirely **not
neural**: skull and scalp conductivity, electrode–skin impedance, exact electrode position, hair,
amplifier gain, and reference placement. Between-subject variance in absolute alpha power spans
more than an order of magnitude in healthy adults **[LIT]**.

### Task/baseline normalisation

`Δθ_dB = +2.1 dB` says: *this participant's frontal theta during the task was 62 % higher than the
same participant's frontal theta during their own reference period, measured minutes earlier with
the same cap, the same electrodes and the same amplifier gain.*

Every one of the nuisance factors above is a **multiplicative constant** shared by task and
baseline, and so cancels exactly in the ratio:

```
    Δ_dB  =  10 log₁₀ ( k²·P_task / k²·P_base )  =  10 log₁₀ ( P_task / P_base )
```

This is precisely why the unresolved amplitude units (PART 7) **do not block** baseline-normalised
analysis.

## 15.4 What baseline normalisation provides

1. **A within-participant reference.** Each participant becomes their own control. This is the
   single largest reduction in nuisance variance available in EEG.
2. **Cancellation of between-subject and between-session scaling.** Impedance, cap fit, anatomy and
   gain all divide out.
3. **Interpretation as change relative to a defined state.** "+2.1 dB relative to eyes-open rest"
   is a statement with a referent; "2.28 × 10⁻⁶ units²" is not.
4. **Comparability across sessions for the same participant**, provided the baseline protocol is
   identical.
5. **A symmetric, approximately normally-distributed measure.** The log transform makes increases
   and decreases symmetric (+3 dB and −3 dB are equal-and-opposite factors of 2), which raw ratios
   are not, and brings the distribution closer to the assumptions of parametric statistics
   **[LIT: Gasser et al., 1982]**.

## 15.5 Limitations of baseline normalisation

1. **The result depends entirely on the baseline state chosen.** Eyes-closed rest has alpha several
   times larger than eyes-open rest, so the same task window normalised against the two produces
   completely different Δα values. The baseline is not a neutral reference; it is a condition.
2. **Baseline drift.** Impedance changes, the participant becomes drowsy, the cap settles. A
   baseline taken once at the start of a 45-minute session becomes progressively less
   representative. A ratio can then reflect drift rather than task.
3. **Regression to the mean.** If the baseline window happens to be unusually high or low by
   chance, every subsequent normalised value is biased in the opposite direction. Longer baselines
   reduce this; the code's 4 s minimum is short in this respect (see 15.6).
4. **A single scalar reference per feature.** The implementation stores one number per ROI. It
   stores no variance, so no per-participant z-scoring or confidence interval is possible, and no
   test of "is this window unusual for this participant" can be performed. **[GAP]**
5. **It cannot rescue a bad baseline.** Gate 5 refuses a flagged window, which is the right
   protection, but a baseline that is *technically clean* while the participant was, say, silently
   rehearsing the word list is unrecoverable.
6. **It does not remove artefact.** If both baseline and task windows contain blink artefact, the
   ratio is a ratio of contaminated quantities.
7. **Only two features are baselined.** `thetaPerChannel[]` / `alphaPerChannel[]` have no
   per-channel baseline, so no topographic normalisation is possible. **[GAP]**

## 15.6 The experimental protocol is NOT finalised — decisions still required

**Stated explicitly: the experimental baseline protocol has not been finalised, and this document
does not invent one.**

Evidence from the code, not opinion:
- `CaptureBaseline` has **zero callers** anywhere in the project.
- No `ExperimentState` value corresponds to a baseline or rest period. The states are: `Idle`,
  `LanguageSelection`, `Familiarization`, `AreaAInstructions`, `WordEncoding`, `ImmediateRecall`,
  `ReadyForAreaB`, `AreaBInstructions`, `ChairInstruction`, `ChairSelection`, `ChairTrialFeedback`,
  `ChairInterTrialInterval`, `ReadyForAreaC`, `DelayedRecall`, `Results`, `Ended`, `Aborted`.
- No configuration field anywhere specifies a baseline duration, eye state or scene.
- The only baseline parameter in existence is `MinimumBaselineSeconds = 4.0`, which is a
  *technical floor* for spectral stability, not a protocol decision.

### The methodological decisions still required

| # | Decision | Options and considerations |
|---|---|---|
| **1** | **Baseline duration** | The code's 4 s floor is a technical minimum, not a recommendation. 4 s yields only 3 Welch segments (~5.8 d.o.f.) and is vulnerable to regression to the mean. 60–120 s is common for resting-state baselines. Trade-off: statistical stability vs. participant time vs. drowsiness onset in longer rests. |
| **2** | **Eyes open or eyes closed** | The most consequential single choice. Eyes-closed alpha is several times larger; eyes-open is more comparable to a VR task state. Many protocols record **both** (e.g. 2 × 60 s) and use eyes-open for task normalisation while retaining eyes-closed as a data-quality check (alpha *should* rise on eye closure — a useful positive control that the electrodes are working). |
| **3** | **Visual scene during baseline** | In VR the participant is always seeing *something*. Options: a neutral grey field; a fixation cross; the IKEA room with no task; the headset display off. Each has a different visual drive and therefore a different alpha level. The choice must be documented and held constant. |
| **4** | **Movement** | Head and body movement generate neck EMG that contaminates the higher bands and motion artefact that contaminates the lower. A still baseline is cleaner but less comparable to an active VR task. Decide, and instruct participants explicitly. |
| **5** | **Speech** | The task includes spoken recall (`VoiceRecallManager`). Speech produces severe jaw/facial EMG. A silent baseline is not comparable to a speaking task epoch. This may require a *second*, speech-matched baseline, or exclusion of speech epochs from analysis. |
| **6** | **Per run or per session** | One baseline per session is simpler; one per run tracks drift better. Note the code's current limitation: `EegRunRecorder.BeginRun` clears the raw buffer but not the filtered buffer, and nothing clears the baseline — so a per-run baseline would need an explicit `ClearBaseline()` call that does not currently exist. |
| **7** | **Position in the session** | Before the task (clean, but unfamiliar), after the task (participant is habituated but fatigued), or both (permits drift estimation, doubles the cost). Bracketing is methodologically strongest. |
| **8** | **Relation to the VR context** | Should the baseline be recorded inside the headset or outside it? Inside is more comparable (same weight, same EMG, same visual medium); outside is cleaner but measures a different physical state. Given that the entire task is in VR, inside is the defensible choice — but it must be a choice, not a default. |
| **9** | **Rejection and re-recording criteria** | Gate 5 refuses a flagged window, but there is no defined procedure for what a researcher should then do. How many retries? What is the criterion for abandoning a session? |
| **10** | **What is stored** | Currently a single scalar per ROI. Should the per-channel baseline, the full baseline PSD, or the baseline variance also be stored? Each enables different analyses and each is a code change. |
| **11** | **Whether baseline normalisation is the primary analysis at all** | Alternatives include condition-vs-condition contrasts within the task (which need no baseline and avoid decisions 1–8 entirely), or mixed-effects models with participant as a random effect. A within-task contrast may be methodologically stronger than baseline normalisation for this design. |

**Recommendation on sequencing:** decisions 1–3 and 8 must be made before any participant is run,
because they change what is recorded. Decisions 9–11 are analysis-time and can follow. None of
them should be made implicitly by whoever writes the first `CaptureBaseline` call.
---

# PART 16 — QUALITY CONTROL

## 16.1 The flag enum **[CODE]** — `EegQualityFlags`, `[Flags]`

> **Superseded in part — see 16.5.** Four flags have been appended since this was written
> (`ChannelPowerOutlier`, `TransientArtifactSuspected`, `NearIdenticalChannels`,
> `ChannelDegraded`). The list below stops at `1 << 9` and is no longer complete. The
> append-only contract it describes still holds and is still asserted by the self-test.

```csharp
None                 = 0
FilterNotSettled     = 1 << 0   //   1
InsufficientSamples  = 1 << 1   //   2
NaNPresent           = 1 << 2   //   4
InfinityPresent      = 1 << 3   //   8
Flatline             = 1 << 4   //  16
SaturationLike       = 1 << 5   //  32
AbruptDiscontinuity  = 1 << 6   //  64
ExtremeDynamicRange  = 1 << 7   // 128
RoiUnresolved        = 1 << 8   // 256
IdenticalChannels    = 1 << 9   // 512
```

The values are treated as an **append-only** contract, because they are compared and combined
across sessions and reports. `ExperimentSelfTest.cs:2272–2277` asserts `Flatline == 16`,
`RoiUnresolved == 256` and `IdenticalChannels == 512` — i.e. that the newest flag was *appended*
as `1 << 9` and not inserted among existing values.

Three properties of the whole design, stated in the code and worth endorsing:

1. **Flags, never repairs.** The enum's doc-comment: "Why a window's features may not be
   trustworthy. Flags, never repairs." No window is modified, interpolated or discarded. A flagged
   window keeps its data so a researcher can inspect what was rejected and why.
2. **No thresholds carry physical units.** Every threshold is relative — to the channel's own
   variation, or to a count. This is a direct consequence of PART 7: "A '±100 µV' rule would be
   meaningless while the scaling of these float32 values is unverified, and would silently reject
   or accept the wrong windows."
3. **One aggregate answer.** `featureValidity = (quality == None) && roiValid`, so a consumer can
   ask a single question, while `QualityText()` enumerates every raised flag by name.

## 16.2 Every flag, explained

### `FilterNotSettled` (1) — cross-window, filter-lifetime

**Test:** `m_SamplesFiltered >= m_SettlingSamples` (750 at 250 Hz / 1 Hz HP).
**Detects:** windows analysed before the IIR start-up transient has decayed.
**Why:** an IIR filter starting from zero state produces a decaying transient that is not signal.
Its power sits mostly at the low end — exactly where theta is.
**Analysed in full in PART 9.**
**Behaviour:** flags but continues; features are still computed. `CaptureBaseline` refuses outright.

### `InsufficientSamples` (2) — window-level

Raised in **five** distinct situations, which is worth knowing when reading a report:

| Situation | Consequence |
|---|---|
| `m_Filtered == null \|\| m_Filter == null` (pipeline not initialised) | abort, invalid |
| `TryGetWindow` returns false, or `sampleCount == 0` | abort, invalid |
| `sampleCount < expected × 0.9` — more than 10 % of expected samples missing | **flag only**, analysis continues |
| `sampleCount < WelchSegmentSeconds × fs` — fewer than one full Welch segment | abort, invalid |
| A `Welch(...)` call throws for a channel | flag; that channel's band powers set to `NaN` |

The 10 % tolerance is the one soft threshold. At 4 s / 250 Hz it means fewer than 900 of ~1000
expected samples triggers the flag.

### `NaNPresent` (4) and `InfinityPresent` (8) — per sample, per channel

```csharp
if (double.IsNaN(v))      flags |= NaNPresent;
if (double.IsInfinity(v)) flags |= InfinityPresent;
if (double.IsNaN(v) || double.IsInfinity(v)) continue;   // excluded from min/max/step stats
```

**Detects:** non-finite values anywhere in the window.
**Why:** a single NaN propagates through the FFT and poisons every bin of the resulting PSD;
`Infinity` does the same and additionally breaks the min/max scaling. Both are unambiguous
evidence of a defect upstream — in the amplifier, the transport, or (if the filter ever went
unstable) in the filter itself.
**Note:** non-finite values are *skipped* when accumulating min/max/step statistics, so one NaN
does not also spuriously trigger `AbruptDiscontinuity`.

### `Flatline` (16) — per channel

```csharp
var range = max - min;
if (range <= 0d) flags |= Flatline;
```

**Detects:** a channel that does not vary at all across the entire window (`max == min`).
**Why:** a disconnected electrode, a dead amplifier channel, or a completely saturated input. Real
EEG from a connected electrode always varies at the noise floor if nothing else.
**Note:** the test is exact equality of max and min. A channel varying by 1e-30 would not be
flagged, though `SaturationLike` would likely catch it instead.

### `SaturationLike` (32) — per channel

```csharp
if (v == series[i - 1]) repeats++;              // bit-identical consecutive values
...
if (repeats > series.Length / 2) flags |= SaturationLike;
```

**Detects:** the same value repeating for more than half the window's samples.
**Why:** "a live amplifier essentially never repeats a float exactly, so a run of them means a
stuck ADC or a rail." A saturated input clips at a constant value; a stuck ADC latches its last
reading.
**Threshold:** 50 % of samples. Deliberately a *count*, not a physical level — because the rail
voltage is unknown.
**Subtlety:** the values counted are the **filtered** ones. A genuinely rail-clipped raw signal
passing through a band-pass would no longer be constant, so this flag is more likely to catch a
stuck *stream* than a saturated *analogue front end*. Both are worth catching; the naming is
slightly optimistic.

### `AbruptDiscontinuity` (64) — per channel, relative

```csharp
var meanStep = sumStep / Math.Max(1, series.Length - 1);
if (meanStep > 0d && maxStep > meanStep * 20.0) flags |= AbruptDiscontinuity;
```

**Detects:** a single sample-to-sample step more than **20×** the channel's own mean absolute step
within this window.
**Why:** a lead briefly disconnecting, an electrode being bumped, a transport glitch, or a large
movement artefact. All produce a step far outside the signal's own scale.
**Why relative:** "A single step an order of magnitude beyond this channel's own typical step is a
discontinuity, whatever the units happen to be." The threshold is dimensionless, so it is valid
regardless of the unknown scaling.
**Caveat:** a window containing *many* discontinuities raises `meanStep` and can therefore mask
them — the test compares the worst step to the average of all steps including the bad ones. A
median-based robust scale would be more sensitive. **[GAP]**

### `ExtremeDynamicRange` (128) — per channel, relative to its own history

```csharp
if (m_TypicalRange[channel] <= 0d)
    m_TypicalRange[channel] = range;                                  // first window: initialise
else
{
    if (range > m_TypicalRange[channel] * 10.0) flags |= ExtremeDynamicRange;
    m_TypicalRange[channel] = m_TypicalRange[channel] * 0.9 + range * 0.1;   // EMA update
}
```

**Detects:** a window whose peak-to-peak range exceeds **10×** an exponential moving average of the
same channel's recent ranges (smoothing factor 0.1).
**Why:** a large movement artefact, a jaw clench, or an electrode pop — events that are enormous
relative to the same channel's normal behaviour.
**Why relative:** compared against this channel's own recent history rather than an absolute
figure, so no unit is needed.
**Three caveats worth knowing:**
1. **The first window can never be flagged** — it initialises the reference instead. The very first
   analysis of a session has no dynamic-range check at all.
2. **The EMA is contaminated by the windows it fails to reject.** A flagged window still updates
   the reference (the update is outside the `if`), so a run of large artefacts progressively
   raises the threshold and eventually stops flagging them.
3. **`m_TypicalRange` is only updated when `AnalyzeLatestWindow` runs.** Since that currently never
   happens at runtime (PART 1.3), in a live session this reference would be built only from
   whatever Editor diagnostics were invoked.

### `RoiUnresolved` (256) — montage-level

Raised in four situations: no montage configured; `ResolveRoi` returns null for either ROI (a
required electrode label is absent from the montage); a resolved index is negative; or a resolved
index is ≥ the window's channel count. Each carries a specific `roiProblem` string naming the
missing electrode or the index conflict.

**Why:** an ROI averaged over the wrong number of electrodes is a different measurement wearing the
same name (PART 5.5). Also the correct response to a montage/stream channel-count mismatch.

### `IdenticalChannels` (512) — **BETWEEN channels**

**This is the one flag that compares channels with each other, and it exists.** See 16.3.

## 16.3 Inter-channel identity quality control — IT DOES EXIST **[CODE]**

The question posed was whether inter-channel identity/correlation quality control exists, and to
flag it as a gap if it does not.

**Finding: an inter-channel IDENTITY check exists and is thorough. An inter-channel CORRELATION
check does not exist, and that absence is deliberate and argued.**

### The implementation — `EegFeaturePipeline.AssessInterChannelIdentity(window, features)`

Called from **both** analysis paths — `AnalyzeLatestWindow` (live) and `AnalyzeWindow` (baseline)
— after the per-channel loop, so it has both the samples and the band powers available, and the
labels are already resolved so the report can name electrodes.

**Test 1 — bit-identical SAMPLES.**

```csharp
for (var a = 0; a < channels; a++)
    for (var b = a + 1; b < channels; b++)
    {
        var identical = true;
        for (var i = 0; i < window.sampleCount; i++)
            if (window.samples[i][a] != window.samples[i][b]) { identical = false; break; }
        if (identical) duplicates.Add($"{LabelOf(features, a)}={LabelOf(features, b)}");
    }
```

Every unordered pair, exact equality at every sample. The inner loop exits on the first difference,
so a healthy 8-channel window costs 28 comparisons rather than 28 × 1000.

**Test 2 — identical BAND POWERS.**

```csharp
var powersIdentical = true;
for (var c = 1; c < channels && powersIdentical; c++)
    if (features.thetaPerChannel[c] != features.thetaPerChannel[0] ||
        features.alphaPerChannel[c] != features.alphaPerChannel[0])
        powersIdentical = false;
```

**Why a second test is genuinely necessary** — and this is the sharpest piece of reasoning in the
file. Welch **de-means every segment** (PART 11.4). Therefore channels differing **only by a
constant DC offset** produce *bit-identical spectra* from *non-identical samples*. Test 1 would
report zero duplicate pairs while every band power was the same. Test 2 catches it. This is not
hypothetical: `SyntheticEegOutlet.ps1 -OffsetOnlyChannels` exists to generate exactly that case,
and the self-test asserts the flag fires while `identicalChannelPairs == 0`.

**Outputs written onto the features object:**

```csharp
public int    identicalChannelPairs;        // 0 is the only healthy value
public string identicalChannelDetail;       // names the electrodes, e.g. "F3=Fz, F3=F4, ..."
```

The detail string is composed to name *which* channels matched and, if applicable, to add
"all 8 channels produced exactly the same theta and alpha".

### Why EXACT equality and no correlation threshold — the argument **[CODE]**

> **Superseded — see 16.5.** A configurable correlation check now exists alongside the
> bit-identity test. The reasoning below records why identity alone was chosen at the time and
> remains a fair statement of that decision; it is no longer a description of current behaviour.

The doc-comment states it directly:

> "EXACT EQUALITY, deliberately. No tolerance, no correlation threshold. Two electrodes that are
> merely very similar are a clinical judgement about common-mode signal and reference placement,
> and this class has no basis for making it while the amplitude scaling is unverified. Two
> electrodes that are bit-identical are an engineering fact. Flagging only the fact keeps this
> consistent with the rest of the quality checks, which are all relative or exact and none of them
> in µV."

This is a defensible position. Nearby EEG electrodes sharing a reference are *genuinely* highly
correlated — r = 0.8–0.95 between adjacent sites is entirely normal physiology, not a defect. Any
correlation threshold would be a clinical judgement requiring knowledge of the reference scheme,
which is undocumented (PART 2.9).

**The NaN case is handled without a special case:** IEEE 754 says `NaN != NaN`, so a channel full
of NaN can never be reported as identical to anything, including another NaN channel. The
`NaNPresent` flag already describes such a window, and calling it a duplicate would be a second,
misleading claim about the same defect. The self-test asserts this behaviour explicitly.

**Single-channel streams:** `if (channels < 2 || window.sampleCount < 1) return None;` — a
one-channel stream has no pair and is never flagged. Also asserted.

### Validation of the identity flag **[CODE — `CheckEegChannelIdentity`]**

| Case | Input | Asserted outcome | Result |
|---|---|---|---|
| Healthy | 8 channels, `sin(i·0.01·(c+1))`, distinct band powers | `flags == None`, `identicalChannelPairs == 0` | PASS |
| One duplicated pair | channel 4 given channel 1's signal | flag raised, `pairs == 1`, detail names **CH2** and **CH5** | PASS |
| **The observed P0 failure** | all 8 channels identical; band powers set to the exact live values `2.2792E−6` / `6.1697E−8` | flag raised, `pairs == 28` (= 8·7/2) | PASS |
| **Offset-only** | `c·10 + sin(i·0.01)`; samples differ, spectra identical | flag raised **via test 2**, `pairs == 0` | PASS |
| All-NaN band powers | healthy samples, NaN powers | flag **NOT** raised | PASS |
| Single channel | 1 channel | flag **NOT** raised | PASS |

Additionally validated **end-to-end** against a deliberately duplicated synthetic stream: the
diagnostic reproduced the original signature and reported `FLAGGED: IdenticalChannels` /
`feature validity: False`, naming all 28 pairs — "where the same input would previously have been
reported as `PASS` / valid" **[synthetic LIVE]**.

## 16.4 Gaps in quality control **[GAP]**

| Missing check | Why it would matter |
|---|---|
| ~~**Inter-channel correlation** (as opposed to identity)~~ | **CLOSED — see 16.5.** Implemented as a configurable near-identity check on filtered signal. |
| ~~**Per-electrode rejection**~~ | **CLOSED differently — see 16.5.** Per-ROI validity now exists: a flagged electrode invalidates only the ROIs it belongs to. The electrode is still never *excluded* from an ROI mean, by design. |
| **Impedance / contact quality** | Not available from the stream (PART 2.9). No software fix possible. |
| **Ocular artefact detection** | No EOG channel, no blink detection, no ICA. Fp1 is present but unused. Frontal theta is exactly the feature most vulnerable to this. |
| **EMG / muscle detection** | No high-frequency power check. The 40 Hz low-pass removes much of the EMG band but not its low-frequency tail, and in VR (headset weight, neck tension, speech) EMG is a live concern. |
| **Robust scale estimates** | `AbruptDiscontinuity` uses a mean-based scale that its own outliers inflate (16.2). A median absolute deviation would be more sensitive. |
| **Line-noise monitoring** | No check for a 50/60 Hz peak, which is the standard early-warning sign of a bad electrode contact. The low-pass removes it from the analysed band, so a deteriorating contact would go unnoticed. |
| **Dynamic-range reference contamination** | The EMA updates even on flagged windows (16.2). |
| **First-window blind spot** | `ExtremeDynamicRange` cannot fire on the first analysed window. |
| **A persisted quality record** | **PARTIALLY CLOSED — see 16.5.** Channel health transitions are exported by the offline analyzer as `eeg_qc_transitions.csv`, and the pipeline keeps them in `qcTransitions`. Live per-window flags are still not written to the run's CSV or session summary. |

## 16.5 Current QC implementation (2026-09) **[CODE]**

**What QC means here.** Quality control in this project decides whether a computed feature may be
trusted. It never changes the signal. Nothing is interpolated, re-referenced, artefact-rejected or
substituted anywhere in the pipeline; a suspect window keeps its data and gains a flag, so what was
rejected stays inspectable.

The rules live in one place — `Scripts/Data/EegChannelQualityRules.cs` (`EegChannelQualityRules`,
`EegChannelHealthTracker`, `EegQualityThresholds`) — and are called by both the live
`EegFeaturePipeline` and `Editor/OfflineSessionAnalyzer.cs`, so a recording replayed offline is
judged by exactly the rules the live session applied.

| Check | Protects against | On failure |
|---|---|---|
| Flatline | A dead or disconnected lead reading a constant | Flag `Flatline`; ROI invalid if the electrode is in one |
| Saturation / clipping | A stuck ADC or a railed amplifier | Flag `SaturationLike`; ROI invalid if in one |
| Non-finite samples | NaN or infinity reaching a spectrum | Flags `NaNPresent` / `InfinityPresent`; feature validity false |
| Abrupt discontinuity | Electrode pops, connector events, amplifier steps | Flag `AbruptDiscontinuity` |
| Extreme dynamic range | A window wildly unlike the channel's own history | Flag `ExtremeDynamicRange` |
| Channel power outlier | One channel orders of magnitude off the others | Flag `ChannelPowerOutlier`, channel named |
| Transient artefact | Non-stationary windows (movement, settling) | Flag `TransientArtifactSuspected` |
| Bit-identical channels | Duplicated leads, amplifier test patterns | Flag `IdenticalChannels`, pairs named |
| Near-identical channels | Acquisition, reference or common-mode problems | Flag `NearIdenticalChannels`, pairs and *r* named |
| Channel degradation / dropout | An electrode that worked and then failed mid-session | Flag `ChannelDegraded`; `Debug.LogWarning` naming electrode, time and reason; ROI invalid |
| ROI validity | A regional mean containing a known-bad electrode | `frontalThetaValid` / `posteriorAlphaValid` set false, each independently, with a reason |
| Filter settling / sample count | Analysing start-up transient or a short window | Flags `FilterNotSettled` / `InsufficientSamples`; window excluded from health baselines |
| Timing gaps | Epochs silently spanning missing data | `RawEegRingBuffer` treats a step beyond 4 nominal sample intervals as a gap; the offline analyzer counts gaps and marks affected windows invalid |

**Degradation and recovery** are the only stateful checks. Each channel accumulates a baseline from
its own clean windows, then must fail `degradationConsecutiveWindows` in a row to be called degraded
and pass `recoveryWindows` in a row to be called recovered — deliberately harder to clear than to
condemn, because a prematurely cleared channel silently re-enters an ROI mean. Every transition is
recorded with electrode, timestamp, reason and affected ROI, exposed as
`EegFeaturePipeline.qcTransitions` and written by the offline analyzer to `eeg_qc_transitions.csv`.

**ROI invalidation is per region and never substitutes.** A failure at P3 invalidates posterior
alpha and leaves frontal theta untouched. The bad electrode is not dropped and the ROI is not
re-averaged over the survivors: an ROI over two electrodes instead of three is a different
measurement wearing the same name. The value is still computed and reported, marked unusable.

### Thresholds: engineering heuristics, not physiological criteria **[GAP]**

Every threshold below is an **engineering heuristic**. None is a scientifically validated
physiological criterion, none is derived from a published method, and none is in µV — each is a
correlation, a ratio against the channel's own history, or a count of windows, which is what lets
them mean anything while the amplitude scaling stays unverified (PART 7). They are serialized on
`EegFeaturePipeline` and configurable in the inspector.

| Threshold | Default | Meaning |
|---|---|---|
| `nearIdenticalCorrelation` | 0.99 | Pearson *r* on filtered signal above which a pair is flagged |
| `nearIdenticalMinimumPairs` | 1 | Pairs required before the window is flagged |
| `degradationDecades` | 2.0 | Distance from a channel's own baseline band power |
| `degradationConsecutiveWindows` | 3 | Consecutive failures before "degraded" |
| `recoveryWindows` | 5 | Consecutive clean windows before "recovered" |
| `excursionRangeRatio` | 8.0 | Window range against the channel's own baseline range |
| `variabilityCollapseRatio` | 0.1 | Range collapse against the channel's own baseline |
| `saturationRepeatFraction` | 0.5 | Fraction of bit-identical consecutive samples |
| `discontinuityStepRatio` | 20.0 | Largest step against the channel's own mean step |
| `baselineWindows` | 5 | Clean windows before a channel's baseline is usable |

**No threshold here should be tuned to make a session pass.** Out-of-range values fall back to the
documented defaults rather than silently disabling a check.

### Why the near-identity check exists **[CODE]**

It is **not** a requirement that EEG channels be statistically independent. Neighbouring scalp
electrodes sharing a reference are genuinely and legitimately correlated, and that is ordinary
physiology, not a defect.

What the check looks for is *suspiciously near-identical temporal dynamics* — traces carrying so
little independent variance that the likely explanation lies upstream of the scalp: an acquisition,
reference or common-mode problem, a montage error, or a duplicated channel. The bit-identity test
could not see this, because such channels are not bit-equal; they differ in the last few bits and by
a small gain.

The check runs on **filtered** signal, and that is essential rather than incidental: on raw AURA
samples a shared DC offset and slow drift dominate the correlation, so nearly any pair would clear
nearly any threshold and the result would carry no information.

It is a **quality-control flag only**. It triggers no correction, no re-referencing and no channel
removal, and it does not identify a cause — it reports that a recording looks wrong in a specific,
measurable way and leaves the diagnosis to the hardware.

### Acquisition validation note **[VALIDATED]**

Acquisition testing compared the AURA internal recording, the AURA LSL stream captured
independently with LabRecorder, and Unity's `raw_eeg.csv`.

**Validated conclusion: Unity preserved the LSL EEG samples without channel mixing or numerical
modification.** This concerns the transport and recording path only. It says nothing about
electrode placement, reference configuration or signal quality.

### Reference configuration observation **[ENGINEERING OBSERVATION — NOT FINAL]**

Changing from a shared ear-clip REF/GND to separated mastoid REF and GND produced a substantially
more heterogeneous inter-channel correlation structure during engineering validation.

This is a strong engineering observation, not a settled result. It comes from development testing
rather than a controlled comparison, and no causal claim is made: montage, electrode placement and
preparation all varied together. The reference configuration is **not** scientifically finalised on
the basis of this test, and continued validation across repeated sessions is required before any
recording from this setup can support a regional or spatial interpretation.


---

# PART 17 — CURRENT LIVE ANOMALY

## 17.1 The observation **[LIVE]**

A single live feature run produced **bit-identical** theta and alpha values on **all eight
channels**:

| Quantity | Value |
|---|---|
| Theta, every channel | `2.2792 × 10⁻⁶` AURA-native-units² |
| Alpha, every channel | `6.1697 × 10⁻⁸` AURA-native-units² |
| Theta/alpha ratio | 36.94 |
| Channels affected | **8 of 8** |

At the time, **every quality check passed** and the window was reported as valid. That is itself
the important secondary finding: each per-channel check looks at one channel in isolation, and each
of the eight channels was, individually, a perfectly well-behaved signal. Only a comparison
*between* channels could see the problem. This directly motivated `IdenticalChannels` (PART 16.3).

## 17.2 Why this is not physiologically plausible

Eight electrodes on a real head **cannot** produce bit-identical float32 sequences. The reasons are
independent and each is individually sufficient:

1. **Different spatial locations.** F3, Fz, F4, P3, Pz, P4 are centimetres apart over different
   cortical regions with different underlying generators. The measured potential differs by
   construction.
2. **Different electrode–skin impedances.** No two electrodes achieve identical contact. Impedance
   differences alone produce different noise amplitudes and different filtering of the source.
3. **Independent thermal and amplifier noise.** Each channel has its own front end contributing
   its own uncorrelated Johnson and shot noise. Even a hypothetical perfectly uniform brain would
   yield different digitised values.
4. **Different distances to the reference.** All EEG is a difference measurement. Sites at different
   distances from the reference electrode see different fractions of the common signal.
5. **Volume conduction is smoothing, not cloning.** Volume conduction makes nearby channels highly
   *correlated* — r = 0.8–0.95 is normal — but correlation is not identity. Identity to the last
   bit of a float32 mantissa is an engineering signature, not a physiological one.
6. **The probability argument.** Two independent float32 values agreeing exactly across ~1000
   consecutive samples is, for any non-degenerate source, effectively zero.

**Additional observation about the reported ratio.** A theta/alpha ratio of 36.94 means theta power
was ~37× alpha power. For frontal *or* parietal EEG in an awake adult that is extreme; alpha
normally dominates in a resting posterior recording. Combined with the identity, this points to a
signal that is not cortical EEG — for example a slow drift or a low-frequency test waveform, whose
power sits at the low end of the passband and therefore lands in theta.

## 17.3 Hypotheses — the cause is NOT assumed

Three candidate causes were enumerated, and one has since been eliminated.

### H1 — AURA is transmitting identical channel values (an ACQUISITION problem)

The amplifier itself puts the same signal on all eight channels. Sub-causes:

- **H1a** — All electrodes disconnected, so every channel reads the same reference/floating value.
- **H1b** — An amplifier test-signal or demo mode broadcasting one waveform on every channel.
- **H1c** — A configuration in which channels are internally shorted or duplicated.
- **H1d** — AURA duplicating one channel across the transmitted layout (a sender-side software bug).

**Status: OPEN. Not yet tested against live hardware.**

### H2 — An interleaving / indexing / window-extraction bug in the Unity path (a CODE problem)

The stream carries eight distinct channels but the Unity path collapses them: a transposed
de-interleave (`samples[c][i]` instead of `samples[i][c]`), a flat-array stride error, a shared
filter state, or an aliased scratch buffer.

**Status: ELIMINATED [VERIFIED 2026-08-24].**

How it was eliminated, given that AURA was offline: `Editor/SyntheticEegOutlet.ps1` published a
float32 / 8-channel / 250 Hz stream **named `AURA`** from a **separate process containing no Unity
code**, each channel carrying a different frequency *and* a different amplitude. Unity's real path
— real receiver, real reflection marshalling, real filter, real ring buffer, real de-interleave,
real Welch — returned **8 clearly distinct** band powers, quantitatively exact where the tone sits
well inside a band:

| Electrode | Injected | Expected `A²/2` | Unity measured | Error |
|---|---|---|---|---|
| Fz | 6.5 Hz, A=2.0 | 2.000 | **1.9997** | −0.015 % |
| P3 | 9.5 Hz, A=3.5 | 6.125 | **6.1232** | −0.029 % |
| Pz | 10.5 Hz, A=4.0 | 8.000 | **7.9976** | −0.030 % |
| F3 | 5.5 Hz, A=1.5 | 1.125 | **1.1248** | −0.018 % |

(Tones near a band edge — 4.5 / 7.5 / 8.5 / 11.5 Hz — recover less, because the 2 s Hann main lobe
spills across the boundary. Expected, and itself evidence the numbers are real.)

Locked in as a permanent regression test: `ExperimentSelfTest` section
`EEG CHANNEL SEPARATION + IDENTITY FLAG`, PART 1 (`ExperimentSelfTest.cs:2210–2266`), which drives
8 channels with per-channel signatures `i·1000 + c` through the exact `RawEegRingBuffer` +
de-interleave path the pipeline uses and asserts every extracted value carries its own channel's
signature and differs from every other channel at the same instant.

Also relevant: the filter's per-channel state independence is separately asserted
(`ExperimentSelfTest.cs:2157–2165`), ruling out the filter as a collapsing mechanism.

**Our extraction does not collapse channels.** The code is no longer a suspect.

### H3 — Common-mode domination

A signal common to all electrodes — mains pickup, a floating reference, a poorly-connected ground —
so large that it dwarfs the differential cortical signal on every channel.

**Status: OPEN.** Note that H3 in its pure form predicts channels that are *nearly* identical
rather than *bit*-identical, since each channel would still carry its own noise. Bit-identity is
more consistent with H1. But an ADC whose input is railed or clipped by a huge common-mode signal
could produce identical digitised values, so H3 cannot be excluded on that basis alone.

### H4 — Another implementation issue not yet enumerated

Kept open explicitly. Candidates that have **not** been tested:

- **H4a** — Something in `AuraLslReceiver`/`LslBinding`'s *reflection marshalling* specific to the
  real AURA sender. The synthetic control used the same Unity code, so this is largely covered —
  but the synthetic stream was `float32` from a P/Invoke publisher, and an untested marshalling
  path cannot be entirely ruled out.
- **H4b** — A liblsl chunk-transfer behaviour that differs between the synthetic outlet (which
  pushes sample-by-sample) and AURA (which delivers 9-sample chunks).
- **H4c** — The observation predating a code fix. The run is described as "the single live feature
  run"; the exact code revision it was produced from is not recorded. Some of the intervening
  changes (the analysis-timebase rewrite) touched the ingestion path. This should be checked before
  concluding anything about the hardware.

## 17.4 The exact tests required to distinguish the hypotheses

### T1 — OUT-OF-PROCESS CHANNEL PROBE — the decisive test **[EXISTS]**

**Script:** `Assets/IKEA_EEG/Editor/ProbeAuraChannels.ps1`

```bash
powershell -ExecutionPolicy Bypass -File Assets\IKEA_EEG\Editor\ProbeAuraChannels.ps1
```

**Why it is decisive.** It loads the same native `lsl.dll` by P/Invoke, opens its own inlet and
pulls its own samples with **no Unity code anywhere in the path**. Unity is the thing under
suspicion, so the control must not contain Unity. LSL is multi-consumer by design, so opening a
second inlet is safe and does not disturb the Unity receiver.

**Read-only:** it opens an inlet and pulls; it sends nothing, writes no project file and changes no
setting.

**What it reports:**
- Stream metadata: channel count, rate, format code, source **hostname**
- A **distinct-values-per-sample histogram** — for each sample, how many of the 8 values were unique
- Per channel: mean, standard deviation, min, max (as pulled, untransformed)
- The first sample, raw
- Every bit-identical channel pair

**Verdict logic, from the script's own header:**

| Observation | Conclusion |
|---|---|
| `distinct = 8/8` on every sample | The wire carries 8 different channels. **If Unity still shows identical values, the bug is OURS** — reopen H2/H4. |
| `distinct = 1/8` | The amplifier sends one signal on 8 channels. **H1 confirmed** — an ACQUISITION problem. Check electrodes, reference, ground, test-signal mode. |
| Specific identical pairs listed | Those particular channels duplicate each other — a wiring or channel-mapping fault. |
| `distinct` mostly 8/8 but per-channel `sd` nearly equal and means nearly equal | Consistent with **H3**, common-mode domination — channels differ but only in small noise. |
| "STREAM NOT FOUND" / "ADVERTISING BUT NOT TRANSMITTING" | No conclusion possible; fix acquisition first. |

The probe's verdict logic is itself verified in **both** directions against synthetic streams
(distinct → "genuinely different channels"; duplicated → "the same signal on every channel").

### T2 — Positive control for the receiving path **[EXISTS]**

```bash
powershell -ExecutionPolicy Bypass -File Assets\IKEA_EEG\Editor\SyntheticEegOutlet.ps1 -StreamName AURA -Seconds 45
```

Publishes a known-good 8-channel stream with distinct frequencies and amplitudes, named `AURA` so
the Unity receiver resolves it. Then run `IKEA_EEG ▸ EEG ▸ Analyze Spectral Window`. Expect **8
distinct** band powers matching `A²/2`. Already run once and passed; re-run it in the same session
as T1 to confirm the receiving path is healthy *on that machine, that day, that build*.

The script's own safety notes: `-StreamName` defaults to `SYNTHETIC_EEG`, naming it `AURA` is
deliberate and opt-in, and `source_id` always says SYNTHETIC so no recording can later be mistaken
for hardware. **Never run T2 at the same time as real AURA** — two streams named AURA would make
the resolve non-deterministic.

### T3 — Negative controls for the identity flag **[EXISTS]**

```bash
powershell -ExecutionPolicy Bypass -File ...\SyntheticEegOutlet.ps1 -StreamName AURA -IdenticalChannels
powershell -ExecutionPolicy Bypass -File ...\SyntheticEegOutlet.ps1 -StreamName AURA -OffsetOnlyChannels
```

Reproduce the fault deliberately and confirm the flag fires. Already passed; a check that never
fires on the failure it was written for is not a check.

### T4 — Physical acquisition checks, if T1 says `distinct = 1/8`

Not software. In order:

1. Confirm all electrodes are physically connected to the cap and the amplifier.
2. Confirm the **reference** and **ground** electrodes are attached and have good contact.
3. Check impedances in the AURA UI if it reports them.
4. Confirm AURA is **not** in a test-signal / demo / simulation mode.
5. Confirm the AURA channel configuration matches an 8-channel montage.
6. Re-read the notch and band-pass toggles (PART 6) while in the UI.
7. Re-run T1 after each change, one change at a time.

### T5 — Distinguishing H1 from H3, if T1 shows `distinct = 8/8` but the values are near-identical

- Compare the per-channel **standard deviations** from T1. Under H3 all channels have similar,
  large sd (dominated by the common signal); under healthy operation they differ.
- Compute pairwise correlations offline from `raw_eeg.csv`. Under H3, r ≈ 0.99+ on *all* pairs
  including distant ones (F3–P4). Under healthy operation, r falls with distance.
- Deliberately disconnect one electrode and re-probe. Its behaviour should change markedly;
  if all eight change together, the reference/ground is implicated.

### T6 — Rule out H4c, the stale-observation hypothesis

Confirm which code revision produced the anomalous run. If it predates the analysis-timebase
rewrite, the observation should be treated as unreproduced until a fresh live run is taken. This is
a records check, not an experiment, and should be done **before** T4, since it is free.

## 17.5 Current status summary

| Hypothesis | Status | Next action |
|---|---|---|
| H1 — AURA sends identical channels | **OPEN** | T1 |
| H2 — extraction bug in Unity | **ELIMINATED** | none; locked as a regression test |
| H3 — common-mode domination | **OPEN** | T1, then T5 |
| H4 — another implementation issue | **OPEN, partially covered** | T6, then T2 |

**Until T1 is run, the earlier live feature values remain untrustworthy.** What has changed is that
the code is no longer a suspect, and that the same run today would be **flagged rather than
silently reported as valid**.

---

# PART 18 — RESEARCHER MONITOR

## 18.1 How it is opened and where it appears **[CODE]**

```csharp
[MenuItem("IKEA_EEG/EEG/Researcher EEG Monitor", false, 144)]
public static void Open()
{
    var window = GetWindow<EegResearcherMonitor>("EEG Monitor");
    window.minSize = new Vector2(820f, 560f);
    window.Show();
}
```

Menu path: **`IKEA_EEG ▸ EEG ▸ Researcher EEG Monitor`**.

It is a `UnityEditor.EditorWindow` in the `IkeaEeg.EditorTools` namespace, in
`Assets/IKEA_EEG/Editor/`. Therefore:

- It appears as a **dockable window in the Unity Editor on the desktop**.
- It is **never** in the headset, and is **never** compiled into a build — `Assets/*/Editor/` code
  is excluded from player builds by Unity convention.
- The researcher watches it on the desktop while the participant is in VR. This is the correct
  architecture for a VR experiment: the observer's display must not be in the participant's field
  of view.

Minimum size 820 × 560. Layout: a status bar across the top, traces on the left, a 260 px-wide
feature panel on the right, a footer below, all inside a scroll view.

## 18.2 What object it reads, and whether it creates anything **[CODE]**

```csharp
static EegFeaturePipeline FindPipeline() => Object.FindAnyObjectByType<EegFeaturePipeline>();
static AuraLslReceiver    FindReceiver() => Object.FindAnyObjectByType<AuraLslReceiver>();
```

**It creates nothing.** `FindAnyObjectByType`, not construction. The class header is emphatic:

> "It does not open an LSL inlet, construct a receiver, allocate a raw buffer, run a filter or
> compute a spectrum of its own — it finds the one `EegFeaturePipeline` already in the scene and
> reads what that pipeline has already produced. A second inlet would double the network load and
> give two disagreeing views of the same signal; a second filter would disagree in its state."

**Verified by inspection:** no `new AuraLslReceiver`, no `AddComponent`, no `LslBinding` call, no
`EegBandpassFilter`, no `EegSpectralAnalyzer` call anywhere in the file. The only mutation it
performs is on its own private plotting fields.

> "It starts nothing, stops nothing, triggers no event, writes no data and cannot alter the
> experiment, the difficulty, the timestamps or a single sample."

When no pipeline is present it shows an info box directing the researcher to enter Play Mode or use
the one-shot diagnostic — it does not offer to create one.

**Contrast with `EegSpectralDiagnostics`**, which *does* build its own temporary
`AuraLslReceiver` + `EegFeaturePipeline` on a `HideAndDontSave` GameObject and destroys it in a
`finally` block. That is a one-shot measurement tool, not a monitor, and the two must not be run
simultaneously against live AURA — that would open two inlets.

## 18.3 Raw vs filtered plotting **[CODE]**

```csharp
m_ShowFiltered = GUILayout.Toggle(m_ShowFiltered, "Filtered", EditorStyles.miniButtonLeft, ...);
m_ShowFiltered = !GUILayout.Toggle(!m_ShowFiltered, "Raw", EditorStyles.miniButtonRight, ...);

var buffer = m_ShowFiltered ? pipeline.filteredBuffer : receiver.buffer;
```

A **view** toggle: it selects which of the **two already-existing** ring buffers to read.
"it selects which existing buffer to read, and never re-filters."

Default: **Filtered** (`m_ShowFiltered = true`).

| Toggle | Buffer read | Trace colour |
|---|---|---|
| **Filtered** | `EegFeaturePipeline.filteredBuffer` — output of the 1–40 Hz band-pass | light blue `(0.55, 0.85, 1.0)` |
| **Raw** | `AuraLslReceiver.buffer` — samples exactly as received | yellow `(0.85, 0.85, 0.55)` |

This is genuinely useful: switching to Raw shows drift and mains that the filter removes, which is
the fastest visual check that electrodes are behaving.

## 18.4 Plotted duration and refresh rate **[CODE]**

```csharp
const double k_TraceSeconds     = 8.0;
const int    k_MaxPointsPerTrace = 400;
const double k_RepaintHz         = 15.0;
```

| Parameter | Value |
|---|---|
| **Plotted duration** | **8.0 seconds** (`[latest − 8 s, latest]`, retrospective) |
| **Refresh rate** | **~15 Hz** |
| Max drawn points per trace | 400 |
| Traces drawn | one per channel — 8 at the live configuration |

Note that the 8 s trace is **twice** the 4 s spectral window. The traces show more context than the
analysis uses; a researcher should not assume the visible waveform is what was analysed.

Each trace is 52 px high, auto-scaled independently to its own min/max over the window, with the
numeric range printed at the right in scientific notation and **never labelled µV** (PART 7). The
electrode label comes from `montage.LabelOfIndex(c)`, falling back to `CH{c+1}`.

## 18.5 Features displayed **[CODE]** — `DrawFeaturePanel`

Reads `pipeline.latest` — a `LatestEegFeatures` snapshot — **and nothing else**. This is the value
object's purpose: "nothing outside the pipeline reaches into filter state, ring buffers or LSL
internals."

| Element | Source | Rendering |
|---|---|---|
| `VALID` / `NOT VALID` | `features.featureValidity` | Bold, green or orange |
| Quality text | `features.QualityText()` | `PASS` or `FLAGGED: Flag1, Flag2, …` |
| **Identical-channel warning** | `features.identicalChannelDetail` | A red `MessageType.Error` HelpBox, raised **above** the numbers |
| Frontal Theta | `features.frontalTheta` | `E4` format + `features.powerUnits` |
| Posterior Alpha | `features.posteriorAlpha` | `E4` format + `features.powerUnits` |
| ROI problem | `features.roiProblem` | Warning HelpBox when `!roiValid` |
| EXPLORATORY ratio + log | `thetaAlphaRatio`, `logThetaAlpha` | Under an `EXPLORATORY` heading, followed by `"not a validated score"` |
| Baseline Δθ / Δα | `deltaThetaDb`, `deltaAlphaDb` | `F2` format + `dB`, or `"unavailable"` |

The identical-channel box is deliberately placed above the numbers, with the text:

> "Identical channels: … Real electrodes cannot produce bit-identical signals. Check connection,
> impedance, reference/ground and any amplifier test-signal mode."

The rationale in the code: "it invalidates everything below it and the cause is at the electrodes,
not in the analysis." Good design — the researcher sees the invalidating condition before the
numbers it invalidates.

**Status bar** (`DrawStatusBar`) shows: AURA connected/not; stream name; rate; channel count;
`LSL synced` / `LSL NOT synced` (`receiver.hasTimeCorrection`); `timebase OK` /
`timebase NON-MONOTONIC` (`stats.timestampsMonotonic`); and `filter settled` /
`FILTER SETTLING — features not yet valid` with `samplesFiltered / settlingSamples`.

## 18.6 Experiment phase display **[CODE]** — `DrawFooter`

```csharp
var manager = Object.FindAnyObjectByType<ExperimentManager>();
EditorGUILayout.LabelField("PHASE:", ...);
EditorGUILayout.LabelField(manager != null ? manager.state.ToString() : "no experiment", ...);
```

Displays `ExperimentManager.state` — one of the `ExperimentState` values (`WordEncoding`,
`ChairSelection`, `DelayedRecall`, …). Read-only: "Experiment state is READ from the existing
manager. Nothing is duplicated and nothing is written back."

Also in the footer: `window {seconds} s / {sampleCount} samples` and `units: {amplitudeUnits}`.

This is the feature that makes the window a *research* monitor rather than a signal viewer — it
lets the observer see which experimental phase the participant is in alongside the EEG.

## 18.7 Performance safeguards **[CODE]**

| Safeguard | Implementation |
|---|---|
| **Repaint throttling** | `EditorApplication.update += Tick`, and `Tick` returns early unless `now − m_LastRepaint >= 1/15 s`. Repaints at 15 Hz, **not** at the 250 Hz sample rate. |
| **Display downsampling** | `stride = max(1, sampleCount / 400)`. At 8 s × 250 Hz = 2000 samples, stride = 5 → 400 drawn points. "DOWNSAMPLE FOR DISPLAY ONLY. This copy is thrown away each repaint; the analysis data is untouched and unaware of it." |
| **Reused allocation** | `readonly List<Vector3> m_PlotPoints = new List<Vector3>(400)`, `Clear()`ed and refilled each repaint rather than reallocated. |
| **No signal processing** | Zero FFTs, zero filtering, zero spectral work. It reads finished numbers. |
| **Graceful absence** | Null pipeline, null buffer, `bufferedSamples < 2`, or an incomplete trace window each produce an informational box rather than an exception. |
| **Non-finite handling** | NaN/Infinity samples are skipped when computing min/max and substituted with `min` when plotting, so one bad sample cannot destroy the scale or the polyline. |
| **Subscription lifecycle** | `EditorApplication.update` handler added in `OnEnable`, removed in `OnDisable`. |

One residual cost: `FindAnyObjectByType` is called **twice per `OnGUI`**, i.e. ~30 scene searches
per second, plus a third for `ExperimentManager` in the footer. On a large scene this is not free.
Caching the references would be a cheap improvement. Minor.

## 18.8 What is compiled but NOT yet live-validated **[CLEARLY LABELLED, as requested]**

| Element | Status |
|---|---|
| The window opens and renders | **NOT CONFIRMED LIVE.** `CLAUDE_HANDOFF.md` lists "Researcher Monitor never opened" as an open P1 item. It compiles; nobody has recorded opening it against live hardware. |
| Live trace plotting (raw and filtered) | **NOT LIVE-VALIDATED.** The code path is straightforward and reads validated buffers, but has not been exercised against a real stream. |
| **The SPECTRAL FEATURES panel** | **CANNOT CURRENTLY POPULATE DURING A RUN.** It reads `pipeline.latest`, which is only written by `AnalyzeLatestWindow`, which has no runtime caller (PART 1.3). In Play Mode the panel will show *"no window analysed yet"*. This is the single most important caveat about this window. |
| Baseline Δ dB display | **CANNOT POPULATE.** `CaptureBaseline` has no callers, so `baselineAvailable` is always false and the panel always shows "unavailable". |
| Identical-channel error box | **NOT LIVE-VALIDATED** in this window, though the underlying flag is validated end-to-end via `EegSpectralDiagnostics` **[synthetic LIVE]**. |
| Experiment phase display | **NOT LIVE-VALIDATED** in combination with EEG. |
| Status bar (connection, sync, settling) | **NOT LIVE-VALIDATED**, but reads properties confirmed live by the diagnostics. |

**Summary for a researcher:** as it stands, this window is a **live signal viewer** with a
**non-functional features panel**. It will show real traces and a real status bar. It will not show
theta or alpha during a run until `AnalyzeLatestWindow` is called on a timer. Both facts should be
known before it is relied on in a session.
---

# PART 19 — VALIDATION HISTORY

Confidence levels used: **HIGH** = validated against live hardware or against analytically known
answers; **MEDIUM** = validated synthetically but not live, or live but not since the last change;
**LOW** = compiles and is unit-reachable, but no meaningful validation exists.

| Component | Validation performed | Result | Current confidence | Remaining validation |
|---|---|---|---|---|
| **LSL library binding** | `IKEA_EEG ▸ LSL ▸ Check LSL Availability`; format-mapping unit assertions (`ChannelElementType` for cf_float32/double64/int32/int16 and three unsupported cases) | Available **True**, `local_clock` responds; all mappings PASS | **HIGH** | Behaviour against a liblsl version that lacks an expected overload |
| **LSL stream discovery** | `IKEA_EEG ▸ LSL ▸ Check AURA Stream`; `Diagnose Network`; full StreamInfo XML pulled from the live sender | `name=AURA type=EEG 8 ch 250 Hz float32 source_id=AuraLSL-… host=laptop-san`; **`<desc/>` EMPTY on all three AURA streams** | **HIGH** | Behaviour when two machines publish a stream named AURA |
| **Stream metadata reading** | Self-test against a self-created loopback outlet with deliberately unusual channel count/rate; asserts type, count, format string, source id | PASS | **HIGH** | — |
| **Real sample reception** | `IKEA_EEG ▸ EEG ▸ Check Raw EEG Buffer` against live AURA, repeated captures | **1000–2200 samples per multi-second capture**; blocking pull and non-blocking poll both succeed | **HIGH** | Sustained multi-minute reception; behaviour across a mid-session dropout |
| **Clock correction (`time_correction`)** | Live measurement across 3 runs; source-level assertions that the formula is `remote + correction` and is never called per sample | **≈ 299 911.01 s, stable ~1 ms**; formula assertions PASS | **HIGH** | Behaviour if the correction jumps mid-session |
| **Clock-correction necessity** | Self-test: corrected buffer yields a 750-sample window bracketing a locally-stamped event; **uncorrected buffer yields none** | PASS (both directions) | **HIGH** | — |
| **Analysis timebase — synthetic** | Self-test reproducing the live jitter pattern (2000 samples, chunk-anchor displacement matching live magnitudes) | **880 backward steps in → 0 out**; steps 2.056–5.738 ms; median **3.9993 ms**; drift < 100 ms; source contains no `channels` | **HIGH** (as a synthetic result) | — |
| **Analysis timebase — live** | `Diagnose Timestamp Continuity` against live AURA | **0 non-monotonic, 0 gaps, median 3.99 ms** | **MEDIUM** | ⚠ **Not re-validated live after the final incremental-emission fix** — AURA went offline. This is a named outstanding item. |
| **Timestamp continuity diagnostic** | Live 8 s capture, remote vs local vs analysis series compared | Identified the **9-sample chunk signature**; anomalies proven to originate **before** clock correction | **HIGH** | Re-run to characterise the final timebase model |
| **Raw ring buffer** | Self-test with non-default channel counts and rates; window retrieval; capacity derivation; live buffer statistics | PASS; live capacity 15 000 samples (60 s × 250 Hz) | **HIGH** | Behaviour at buffer wrap-around under live conditions |
| **Event window (1 s pre / 2 s post)** | `IKEA_EEG ▸ EEG ▸ Test Event Window` against live AURA, with a synthetic marker from `LslClock.Now()` | **750 samples, 8 ch, status `Complete`, brackets the event — PASS** | **HIGH** | Against a *real* experiment marker rather than a synthetic one |
| **Channel de-interleave integrity** | Self-test: 8 channels with per-channel signatures through the exact ring-buffer + extraction path the pipeline uses | PASS — each channel's own samples returned; all 8 mutually distinct | **HIGH** | — |
| **Band-pass filter — design** | Self-test at **256 Hz** (not 250, to catch hard-coding): order, section count, rate propagation | 8th order, 4 sections, rate exact — PASS | **HIGH** | Composite −3 dB point is not independently asserted |
| **Band-pass filter — response** | Measured time-series attenuation at 6 probe frequencies, plus analytic `GainDbAt` cross-check | 0.2 Hz < −30 dB; 6/10 Hz within −3…+1 dB; 30 Hz within −6…+1 dB; 60 Hz < −12 dB; 80 Hz < −25 dB — **all PASS**, analytic agrees | **HIGH** | Group delay is never computed **[GAP]** |
| **Filter state independence** | Drive channel 0 with 500 × amplitude-1000 samples, then read channel 1 | Channel 1 output `< 1e-12` — PASS | **HIGH** | — |
| **Filter continuity (no per-epoch reset)** | Source-level assertions: `Process` body contains no `Reset`; file contains no `filtfilt` | PASS | **HIGH** | — |
| **Filter settling parameter** | Only `SettlingSamples > 0` is asserted | Trivially PASS | **LOW** | **No impulse/step response test exists.** The 3 s figure is an unvalidated engineering guard (PART 9). Analytically it corresponds to 7.2 τ / −63 dB, but nothing in the project establishes this. |
| **Hann window** | `hann[0] == 0`; `hann[N/2] == 1.0` exactly for the periodic form | PASS to 1e-12 | **HIGH** | — |
| **FFT** | Unit impulse → flat unity spectrum, real and imaginary parts | PASS to 1e-12 | **HIGH** | — |
| **Welch PSD** | 5 synthetic tests: pure 6 Hz, pure 10 Hz, equal 6+10, **amplitude 2:1 → power 4:1**, white noise; plus physical-resolution and bin-spacing assertions | All PASS; ratio test returned within (3.4, 4.6) as required | **HIGH** | Behaviour on genuinely non-stationary input |
| **Welch normalisation, structural** | Source assertions: `k > 0 && k < n / 2` (one-sided folding), `sampleRateHz * windowEnergy` (density), `- mean)` (de-meaning) | PASS | **HIGH** | — |
| **Band power** | FFT-length invariance (512 vs 2048 → < 10 % change); trapezoidal × bin spacing | PASS | **HIGH** | Band-edge truncation bias (~15 %) is **not** corrected or asserted **[GAP]** |
| **Absolute spectral scale** | Synthetic 8-channel `AURA`-named stream with known tones; measured band power vs analytic `A²/2` | **0.015 – 0.030 % error** on four electrodes | **HIGH** | Repeat against live hardware once T1 is resolved |
| **Montage + provenance** | Self-test: mapping both directions for all 8 electrodes; `Validate(8)` passes, `Validate(6)` fails; ROI all-or-nothing; Fp1/Cz excluded; unit labels refuse µV² | PASS | **HIGH** | **Channel *order* cannot be verified** — no LSL metadata exists to check against **[GAP]** |
| **Quality flags — per channel** | Flag values asserted stable (Flatline=16, RoiUnresolved=256, IdenticalChannels=512) | PASS | **MEDIUM** | Individual detectors (flatline, saturation, discontinuity, dynamic range) have **no dedicated unit tests** |
| **`IdenticalChannels` flag** | 6 unit cases (healthy, one pair, all-8, offset-only, all-NaN, single channel) **plus** end-to-end against a deliberately duplicated synthetic stream | All PASS; end-to-end reproduced the original P0 signature and reported `FLAGGED` / `validity: False`, naming all 28 pairs | **HIGH** | — |
| **Out-of-process channel probe** | Verdict logic verified in **both** directions against synthetic streams | PASS | **HIGH** (as a tool) | ⚠ **Never run against live AURA.** This is the decisive outstanding test (PART 17/21). |
| **Live spectral processing** | One live feature run | **ANOMALOUS** — bit-identical theta (2.2792E−6) / alpha (6.1697E−8) on all 8 channels, ratio 36.94 | **LOW / UNTRUSTWORTHY** | PART 17 tests T1, T2, T6 |
| **Runtime feature cadence** | — | **`AnalyzeLatestWindow` has no runtime caller** | **NOT IMPLEMENTED** | Wire it to a timer or an experiment hook, then validate |
| **Baseline capture** | Compiles; five rejection gates present; `AnalyzeWindow` shared with the live path | **No callers anywhere** | **LOW** | Everything: a protocol (PART 15.6), a call site, and live validation |
| **Researcher monitor** | Compiles; scene-builder places exactly one pipeline and one receiver | **Never opened against live hardware** (open P1 item); features panel cannot populate at runtime | **LOW** | Open it in Play Mode with live AURA |
| **Raw EEG recording to disk** | Source assertions: no arithmetic on channel values; `"R"` round-trip formatting; no `Mathf.`; all four timestamp columns present | PASS | **MEDIUM** | Verify a written file end-to-end against the event CSV |
| **Marker outlet** | `IKEA_EEG ▸ LSL ▸ Run Marker Loopback Test` | **PASS** — marker received unchanged | **HIGH** | Capture in a real XDF alongside AURA |
| **Scene wiring** | Self-test asserts exactly one `AuraLslReceiver` and exactly one `EegFeaturePipeline`, on the same node | PASS; component confirmed in the saved `.unity` file | **HIGH** | — |
| **Self-test overall** | `IKEA_EEG ▸ Run Self Test` | **1584 assertions, 0 failures** | **HIGH** | — |

---

# PART 20 — CURRENT PROJECT STATE

Deliberately conservative. An item is **VALIDATED** only if there is evidence in the project — a
passing assertion, a diagnostic transcript, or a live measurement — that it works as described.

## 20.1 VALIDATED

**Transport and synchronisation**
1. LSL library binding by reflection, with honest failure states when liblsl is absent.
2. Discovery of the `AURA` stream by exact name, with a finite timeout.
3. Reading channel count, rate, format and source ID from live stream metadata — never assumed.
4. Sample-format → element-type mapping, including refusal of unsupported formats.
5. Non-blocking, frame-safe sample reception; no code path can block on EEG.
6. Real sample reception from live AURA (1000–2200 samples per multi-second capture).
7. `time_correction` acquisition and the `local = remote + correction` mapping, with correct
   lifecycle (once on connect, every 5 s thereafter, never per sample).
8. That clock correction is **necessary** — demonstrated in both directions.
9. All three timestamps preserved per sample and written to disk under unambiguous names.
10. The analysis timebase is monotonic by construction on realistically jittered input (synthetic).
11. Event-window extraction on the shared LSL clock: 750 samples, `Complete`, brackets the event.
12. Outbound marker outlet, loopback-verified.

**Buffering**
13. Ring buffer sizing derived from stream metadata; flat pre-allocated arrays; no per-sample
    allocation.
14. Window extraction with honest `Complete`/`Missing*` status.
15. Continuity statistics: effective rate, monotonicity, gap counts, largest gap.
16. **De-interleave integrity** — 8 distinct channels in, 8 distinct channels out.

**Signal processing**
17. 8th-order Butterworth band-pass as 4 biquads, coefficients from the supplied sample rate.
18. Measured frequency response at six probe frequencies, agreeing with the analytic response.
19. Independent filter state per channel.
20. Filter state persists across calls — no per-epoch transient.
21. Periodic Hann window, correct definition.
22. Radix-2 FFT, exact on a unit impulse.
23. Welch PSD with stated normalisation, correct one-sided folding, per-segment de-meaning.
24. Physical resolution correctly distinguished from bin spacing.
25. Band power as a density integral, invariant to FFT length.
26. **Absolute spectral accuracy to 0.03 %** against analytic `A²/2` through the full live path.

**Configuration and quality**
27. Montage mapping, both directions, with all-or-nothing ROI resolution.
28. Channel-count validation against the live stream.
29. Refusal to label power as µV² while units are unconfirmed.
30. `IdenticalChannels` inter-channel check — unit-tested in six cases and end-to-end.

**Project hygiene**
31. Scene contains exactly one receiver and exactly one pipeline, on one GameObject.
32. Self test: 1584 assertions, 0 failures.

## 20.2 IMPLEMENTED BUT NOT YET FULLY VALIDATED

1. **Live per-sample filtering during an experiment run.** The subscription exists and the scene is
   wired; no transcript confirms it running for a full session.
2. **The filtered ring buffer under live conditions.** Same.
3. **The analysis timebase against live AURA after the final fix.** Live-validated *before* the
   incremental-emission change; not after.
4. **The filter-settling parameter.** Compiles and gates correctly, but the 3 s value is an
   unvalidated engineering guard (PART 9).
5. **Per-channel quality detectors** — flatline, saturation, discontinuity, dynamic range. Present
   and reasoned; **no dedicated unit tests**.
6. **`ExtremeDynamicRange` reference behaviour.** Cannot fire on the first window; its EMA is
   updated even by flagged windows.
7. **ROI aggregation on live data.** The arithmetic is trivial and its inputs are validated; it has
   never produced a trustworthy live value.
8. **Exploratory ratio and log-ratio.** Computed; never validated against anything.
9. **Baseline capture.** Five sound rejection gates, shared analysis path — **zero callers**.
10. **Baseline dB normalisation.** Formula correct; unreachable in practice.
11. **The researcher monitor.** Compiles; never opened against live hardware; its features panel
    cannot populate at runtime.
12. **Raw EEG file writing.** Structurally asserted not to transform data; no end-to-end check that
    a written file aligns with the event CSV.
13. **The out-of-process AURA channel probe.** Verdict logic verified both ways; **never run
    against live AURA**.
14. **`EegSpectralDiagnostics` against live hardware.** Run once; produced the anomaly of PART 17.

## 20.3 NOT IMPLEMENTED / FUTURE

**Missing wiring (highest priority — these are small changes with large consequences)**
1. **A runtime cadence for `AnalyzeLatestWindow`.** Nothing computes features during a run.
2. **A call site for `CaptureBaseline`.** Nothing captures a baseline.
3. **Clearing the filtered buffer on `BeginRun`.** The raw buffer is cleared; the filtered one is not.
4. **Persisting features and quality flags.** Flags exist only in memory; a finished session leaves
   no record of which windows were flagged.

**Missing analysis capability**
5. Event-locked spectral analysis (epoching around experiment markers). The QA window exists; no
   spectral analysis is anchored to an event.
6. Time-frequency analysis (spectrogram, wavelet, Hilbert). Only fixed-window Welch exists.
7. Individual alpha frequency estimation. `PeakFrequency` exists but is used only by tests.
8. Any band other than theta and alpha — no delta, beta, gamma, no broadband/aperiodic estimate.
9. Aperiodic (1/f) separation, e.g. FOOOF-style parameterisation. Band power currently conflates
   oscillatory and aperiodic components **[LIT: Donoghue et al., 2020]**.
10. Re-referencing of any kind — average reference, Laplacian, REST. The reference is undocumented.
11. Artefact correction — ICA, regression-based EOG removal, ASR.
12. Per-electrode rejection or interpolation.
13. Group delay computation for the filter.
14. Any statistical machinery — no per-participant variance, no confidence intervals, no tests.

**Missing quality control** (PART 16.4)
15. Inter-channel correlation (deliberately omitted, with an argument).
16. Ocular artefact detection.
17. EMG / muscle detection.
18. Line-noise monitoring.
19. Robust (median-based) scale estimates.
20. Impedance monitoring (not available from the stream).

**Missing methodology**
21. **A finalised baseline protocol** (PART 15.6 lists 11 open decisions).
22. **Confirmed amplitude units** (PART 7).
23. **Verified channel order** (PART 5.7).
24. Any participant data.
25. Any tested hypothesis about cognitive load.
26. Neuroadaptive feedback — the pipeline is explicitly one-directional; nothing reads EEG to
    change the experiment.

**Missing validation**
27. Resolution of the identical-channel anomaly (PART 17).
28. Live re-validation of the analysis timebase.
29. A settling-time impulse/step test.
30. Sustained multi-minute live acquisition.
31. Behaviour across a mid-session amplifier dropout and reconnection.

---

# PART 21 — NEXT EXPERIMENTAL TEST: RESEARCHER CHECKLIST

**Objective: isolate the identical-channel anomaly (PART 17). Determine whether AURA transmits
eight distinct channels.**

This checklist changes no project file. It is a sequence of observations. Record every result,
including negative ones.

## Phase 0 — Before touching the hardware (5 minutes, free)

- [ ] **0.1** Record today's date, the operator's name, the Unity project revision and the AURA
      application version.
- [ ] **0.2** **Records check (test T6).** Determine which code revision produced the anomalous run
      recorded in `CLAUDE_HANDOFF.md`. If it predates the analysis-timebase rewrite, note that the
      anomaly may be unreproduced. This does not change the tests below, but it changes their
      interpretation.
- [ ] **0.3** Confirm no other application is holding an LSL inlet on AURA.
- [ ] **0.4** Confirm **no synthetic stream is running**. A `SyntheticEegOutlet.ps1` process named
      `AURA` would make discovery non-deterministic. Check for stray PowerShell processes.
- [ ] **0.5** Run `IKEA_EEG ▸ Run Self Test`. Expect **0 failures**. If it fails, stop and fix that
      first — a broken build invalidates everything below.

## Phase 1 — Establish the baseline software state (10 minutes, no hardware)

- [ ] **1.1** Start the positive control:
      ```bash
      powershell -ExecutionPolicy Bypass -File Assets\IKEA_EEG\Editor\SyntheticEegOutlet.ps1 -StreamName AURA -Seconds 60
      ```
- [ ] **1.2** In Unity, run `IKEA_EEG ▸ EEG ▸ Analyze Spectral Window`.
- [ ] **1.3** **Expect 8 clearly distinct band powers** matching `A²/2` for the injected tones, and
      `QUALITY: PASS`. Save the full Console output.
- [ ] **1.4** If this FAILS, **stop**. The receiving path is broken on this machine/build and no
      conclusion about AURA is possible. Investigate before continuing.
- [ ] **1.5** Stop the synthetic outlet. **Verify the process has exited.**

## Phase 2 — Physical setup (30 minutes, the part that matters most)

- [ ] **2.1** Fit the cap. Record cap size and the placement landmark used (nasion–inion, or
      whatever the lab standard is).
- [ ] **2.2** **Confirm the REFERENCE electrode is attached and has good contact.** Record its
      location.
- [ ] **2.3** **Confirm the GROUND electrode is attached and has good contact.** Record its location.
- [ ] **2.4** Confirm all 8 signal electrodes are physically connected to both cap and amplifier.
- [ ] **2.5** Apply gel/saline per the normal procedure. Record impedances **for every electrode**
      if the AURA UI reports them. This is the single most valuable number you can write down.
- [ ] **2.6** **Confirm AURA is NOT in a test-signal, demo, calibration or simulation mode.** Search
      the UI for any such setting and photograph the screen.
- [ ] **2.7** **Re-read and record the notch and band-pass toggles** (PART 6). Expected:
      **No Notch**, **No Filtering**. If either is ON, record it and **do not proceed to spectral
      analysis** until the discrepancy with `AuraMontageConfig` is resolved.
- [ ] **2.8** **Re-read and record the channel order** in the AURA UI. Compare against
      `CH1=Fp1, CH2=F3, CH3=Fz, CH4=F4, CH5=Cz, CH6=P3, CH7=Pz, CH8=P4`. Photograph the screen.
      This is the only available check on the montage (PART 5.7).
- [ ] **2.9** Have the participant (or the operator, if testing on themselves) sit still, eyes open,
      relaxed, not speaking.

## Phase 3 — THE DECISIVE TEST (5 minutes)

- [ ] **3.1** With AURA streaming and **Unity closed or at least not connected**, run:
      ```bash
      powershell -ExecutionPolicy Bypass -File Assets\IKEA_EEG\Editor\ProbeAuraChannels.ps1 -Samples 2000
      ```
- [ ] **3.2** **Save the complete output verbatim.** It is the primary evidence.
- [ ] **3.3** Record specifically:
      - the reported `channels`, `rate`, `format_code`, `host`
      - the **distinct-values-per-sample histogram**
      - **per-channel mean, sd, min, max** for all 8 channels
      - the first raw sample
      - any listed identical pairs
- [ ] **3.4** **Read the verdict:**

| Observation | Conclusion | Go to |
|---|---|---|
| `distinct = 8/8` on ~100 % of samples, per-channel sd clearly different | The wire carries 8 genuinely different channels | Phase 4 |
| `distinct = 1/8` on most samples | **H1 CONFIRMED — acquisition problem.** AURA sends one signal on 8 channels | Phase 5 |
| Specific pairs listed as identical | Those channels duplicate each other — wiring or channel-mapping fault | Phase 5 |
| `distinct = 8/8` but all sd nearly equal and all means nearly equal | Possible **H3, common-mode domination** | Phase 4, then Phase 6 |
| "STREAM NOT FOUND" | AURA is not on the network | Fix networking; no conclusion |
| "ADVERTISING BUT NOT TRANSMITTING" | Inlet opened, no samples | Fix acquisition; no conclusion |

- [ ] **3.5** Repeat 3.1 **twice more** to confirm the result is stable and not a one-off.

## Phase 4 — Unity comparison, only if Phase 3 showed distinct channels

- [ ] **4.1** Run `IKEA_EEG ▸ EEG ▸ Check Raw EEG Buffer`. Record: samples received, effective rate
      vs advertised, monotonicity, gap count, largest gap, and the single printed latest sample —
      **check whether its 8 channel values differ**.
- [ ] **4.2** Run `IKEA_EEG ▸ EEG ▸ Diagnose Timestamp Continuity`. This also re-validates the
      analysis timebase live, which is an outstanding item in its own right (PART 19). Record:
      fitted rate, largest fit residual, largest grid drift, non-monotonic counts in all three
      series, gap counts.
- [ ] **4.3** Run `IKEA_EEG ▸ EEG ▸ Test Event Window`. Expect 750 samples / `Complete` / brackets.
- [ ] **4.4** Run `IKEA_EEG ▸ EEG ▸ Analyze Spectral Window`. Record the **full per-channel table**,
      the QUALITY line and any `inter-channel identity` line.
- [ ] **4.5** **Compare 4.4 against 3.3.**

| Probe (3.3) | Unity (4.4) | Conclusion |
|---|---|---|
| distinct | **distinct** | ✅ Anomaly not reproduced. Both paths healthy. Investigate what differed in the original run (see 0.2). |
| distinct | **identical** | 🔴 **The bug is OURS.** Reopen H2/H4. This is the case the synthetic control could not produce — collect both transcripts and treat it as a P0. |
| identical | identical | Consistent with H1 — acquisition. Proceed to Phase 5. |

## Phase 5 — Acquisition troubleshooting, only if channels are identical at the source

Change **one thing at a time** and re-run the Phase 3 probe after each.

- [ ] **5.1** Re-seat the reference electrode. Re-probe.
- [ ] **5.2** Re-seat the ground electrode. Re-probe.
- [ ] **5.3** Re-gel every electrode; re-check impedances. Re-probe.
- [ ] **5.4** Deliberately **disconnect one signal electrode** (e.g. P4). Re-probe. If only P4's
      statistics change → the other channels are independent. If **all** channels change → the
      reference/ground path is implicated.
- [ ] **5.5** Check the AURA channel configuration for internally shorted or duplicated channels.
- [ ] **5.6** Restart the AURA application; re-probe before touching anything else.
- [ ] **5.7** If available, test with a different cap or amplifier.
- [ ] **5.8** Record which single change (if any) restored distinct channels.

## Phase 6 — Distinguishing common-mode domination (H3), if needed

- [ ] **6.1** From the Phase 3 per-channel table, compare standard deviations. Under H3 all eight
      are similar and large.
- [ ] **6.2** Have the participant blink deliberately 10 times, then clench the jaw 5 times. Re-probe.
      Frontal channels should respond far more than parietal ones. If all eight respond identically,
      the channels are not spatially independent.
- [ ] **6.3** Record ~60 s to `raw_eeg.csv` (enter Play Mode) and compute pairwise Pearson
      correlations offline. Under H3, r ≈ 0.99+ on **all** pairs including F3–P4. Healthy EEG shows
      r falling with inter-electrode distance.
- [ ] **6.4** Check for nearby mains-frequency sources; try moving the setup or unplugging the
      laptop charger; re-probe.

## Phase 7 — Physiological positive control, only once channels are distinct

This is the check that the signal is actually EEG rather than merely eight different noise sources.

- [ ] **7.1** Participant sits still, **eyes OPEN**, 60 s. Run `Analyze Spectral Window`. Record
      `PosteriorAlpha`.
- [ ] **7.2** Participant sits still, **eyes CLOSED**, 60 s. Run it again. Record `PosteriorAlpha`.
- [ ] **7.3** **Expect posterior alpha to increase substantially with eyes closed** — this is the
      most robust effect in all of EEG (Berger, 1929) **[LIT]**. A clear increase is strong evidence
      that P3/Pz/P4 are recording real cortical activity.
- [ ] **7.4** Record the ratio. If alpha does **not** increase, the signal is not trustworthy as EEG
      regardless of what the channel probe said.
- [ ] **7.5** Optionally repeat with the headset **on** vs **off** to characterise the VR-specific
      EMG contribution.

## Phase 8 — Documentation

- [ ] **8.1** Save every console transcript and probe output with a timestamped filename.
- [ ] **8.2** Update `CLAUDE_HANDOFF.md` §10 with the P1 outcome: H1 confirmed, H3 confirmed, or
      anomaly not reproduced.
- [ ] **8.3** If Phase 2.7 or 2.8 revealed a discrepancy, update `AuraMontageConfig` **and** its
      `verificationNote` with today's date.
- [ ] **8.4** If Phase 4.2 produced clean live timebase statistics, record them — that closes the
      outstanding live re-validation item.
- [ ] **8.5** Note explicitly which of the PART 19 "remaining validation" items this session closed.

### What NOT to do

- ❌ Do not run `SyntheticEegOutlet.ps1 -StreamName AURA` while real AURA is streaming.
- ❌ Do not run `Analyze Spectral Window` and the Researcher Monitor's own diagnostic path
      simultaneously — `EegSpectralDiagnostics` creates a second receiver and inlet.
- ❌ Do not change AURA's filter settings during a session without recording it.
- ❌ Do not interpret any band power until Phase 3 has returned `distinct = 8/8`.
- ❌ Do not modify project source during this session. If a code change is needed, record it and
      make it as a separate, reviewed change.

---

# PART 22 — REFERENCES

**Separation of concerns.** Everything in PARTS 1–13 and 16–21 marked **[CODE]** is derived from
this project's source files and requires no citation. The references below support only the
**[LIT]** material — the general signal-processing and neuroscience background in PARTS 8, 9, 10,
11, 14 and 15. **No reference below is offered as evidence for anything IKEA_EEG has measured.**

## 22.1 Digital filtering of EEG

- Oppenheim, A. V. & Schafer, R. W. *Discrete-Time Signal Processing*, 3rd ed. Pearson, 2009.
  — Standard reference for IIR filter design, the bilinear transform, cascade realisation,
  coefficient sensitivity, group delay, and pole-based stability and settling analysis. Supports
  PARTS 8 and 9.
- Bristow-Johnson, R. *Cookbook Formulae for Audio EQ Biquad Filter Coefficients.*
  — The RBJ biquad forms implemented verbatim in `EegBandpassFilter.LowPass` / `.HighPass`.
- Widmann, A., Schröger, E. & Maess, B. (2015). Digital filter design for electrophysiological
  data — a practical approach. *Journal of Neuroscience Methods*, 250, 34–46.
  — The standard practical guide for EEG filtering: causal vs zero-phase, cutoff selection,
  filter-order effects and reporting requirements. Directly relevant to PARTS 8.7 and 8.9.
- Acunzo, D. J., MacKenzie, G. & van Rossum, M. C. W. (2012). Systematic biases in early ERP and
  ERF components as a result of high-pass filtering. *Journal of Neuroscience Methods*, 209,
  212–218. — Why a ≥ 0.5 Hz high-pass distorts ERP components. Supports the PART 8.9 caution.
- Tanner, D., Morgan-Short, K. & Luck, S. J. (2015). How inappropriate high-pass filters can
  produce artifactual effects and incorrect conclusions in ERP studies. *Psychophysiology*, 52,
  997–1009.
- Gustafsson, F. (1996). Determining the initial states in forward-backward filtering.
  *IEEE Transactions on Signal Processing*, 44(4), 988–992.
  — The `filtfilt` initial-condition problem. Supports PART 8.7.
- Luck, S. J. *An Introduction to the Event-Related Potential Technique*, 2nd ed. MIT Press, 2014.
  — General EEG methodology, filtering, artefact handling and reporting standards.

## 22.2 Spectral analysis and Welch's method

- Welch, P. D. (1967). The use of Fast Fourier Transform for the estimation of power spectra: a
  method based on time averaging over short, modified periodograms. *IEEE Transactions on Audio
  and Electroacoustics*, 15(2), 70–73.
  — **The primary reference for the method implemented in `EegSpectralAnalyzer.Welch`**, including
  the segment-averaging variance reduction and the treatment of overlap correlation used in
  PART 10.4.
- Bartlett, M. S. (1948). Smoothing periodograms from time series with continuous spectra.
  *Nature*, 161, 686–687. — Non-overlapping segment averaging, the predecessor to Welch.
- Harris, F. J. (1978). On the use of windows for harmonic analysis with the discrete Fourier
  transform. *Proceedings of the IEEE*, 66(1), 51–83.
  — The definitive window-function reference: periodic vs symmetric definitions, coherent gain,
  equivalent noise bandwidth, scalloping loss, main-lobe width and sidelobe levels, and overlap
  correlation for Hann. Supports PARTS 11.1, 11.5 and 10.4.
- Cooley, J. W. & Tukey, J. W. (1965). An algorithm for the machine calculation of complex Fourier
  series. *Mathematics of Computation*, 19(90), 297–301.
  — The radix-2 FFT implemented in `EegSpectralAnalyzer.Fft`.
- Percival, D. B. & Walden, A. T. *Spectral Analysis for Physical Applications.* Cambridge
  University Press, 1993. — Rigorous treatment of periodogram bias and variance, zero padding vs
  resolution, and multitaper alternatives.

## 22.3 Frontal theta and cognitive load

- Gevins, A., Smith, M. E., McEvoy, L. & Yu, D. (1997). High-resolution EEG mapping of cortical
  activation related to working memory: effects of task difficulty, type of processing, and
  practice. *Cerebral Cortex*, 7(4), 374–385.
  — Frontal theta increasing and parietal alpha decreasing with n-back load.
- Klimesch, W. (1999). EEG alpha and theta oscillations reflect cognitive and memory performance:
  a review and analysis. *Brain Research Reviews*, 29(2–3), 169–195.
  — The central review distinguishing theta synchronisation from alpha desynchronisation; also the
  key source on individual alpha frequency (PARTS 12.7, 14.3).
- Jensen, O. & Tesche, C. D. (2002). Frontal theta activity in humans increases with memory load in
  a working memory task. *European Journal of Neuroscience*, 15(8), 1395–1399.
- Onton, J., Delorme, A. & Makeig, S. (2005). Frontal midline EEG dynamics during working memory.
  *NeuroImage*, 27(2), 341–356.
- Cavanagh, J. F. & Frank, M. J. (2014). Frontal theta as a mechanism for cognitive control.
  *Trends in Cognitive Sciences*, 18(8), 414–421.

## 22.4 Posterior alpha

- Berger, H. (1929). Über das Elektrenkephalogramm des Menschen. *Archiv für Psychiatrie und
  Nervenkrankheiten*, 87, 527–570. — The original description of the alpha rhythm and its blocking
  on eye opening. Basis for the Phase 7 positive control in PART 21.
- Pfurtscheller, G. & Lopes da Silva, F. H. (1999). Event-related EEG/MEG synchronization and
  desynchronization: basic principles. *Clinical Neurophysiology*, 110(11), 1842–1857.
  — The canonical ERD/ERS framework.
- Jensen, O. & Mazaheri, A. (2010). Shaping functional architecture by oscillatory alpha activity:
  gating by inhibition. *Frontiers in Human Neuroscience*, 4, 186.
  — Why alpha can *increase* under some load conditions; the nuance in PART 14.2.
- Klimesch, W., Sauseng, P. & Hanslmayr, S. (2007). EEG alpha oscillations: the
  inhibition–timing hypothesis. *Brain Research Reviews*, 53(1), 63–88.

## 22.5 Combined theta/alpha workload indices

- Gevins, A. & Smith, M. E. (2003). Neurophysiological measures of cognitive workload during
  human–computer interaction. *Theoretical Issues in Ergonomics Science*, 4(1–2), 113–131.
- Holm, A., Lukander, K., Korpela, J., Sallinen, M. & Müller, K. M. I. (2009). Estimating brain
  load from the EEG. *The Scientific World Journal*, 9, 639–651.
- Borghini, G., Astolfi, L., Vecchiato, G., Mattia, D. & Babiloni, F. (2014). Measuring
  neurophysiological signals in aircraft pilots and car drivers for the assessment of mental
  workload, fatigue and drowsiness. *Neuroscience & Biobehavioral Reviews*, 44, 58–75.
- Antonenko, P., Paas, F., Grabner, R. & van Gog, T. (2010). Using electroencephalography to
  measure cognitive load. *Educational Psychology Review*, 22(4), 425–438.

## 22.6 Baseline normalisation and power transformation

- Gasser, T., Bächer, P. & Möcks, J. (1982). Transformations towards the normal distribution of
  broad band spectral parameters of the EEG. *Electroencephalography and Clinical
  Neurophysiology*, 53(1), 119–124.
  — Why log-transforming EEG power is standard. Supports PARTS 13.2 and 15.4.
- Grandchamp, R. & Delorme, A. (2011). Single-trial normalization for event-related spectral
  decomposition reduces sensitivity to noisy trials. *Frontiers in Psychology*, 2, 236.
  — Comparison of dB, percent-change, z-score and gain-model baseline normalisations, and their
  differing sensitivities. Directly relevant to the open decisions in PART 15.6.
- Cohen, M. X. *Analyzing Neural Time Series Data: Theory and Practice.* MIT Press, 2014.
  — Chapter-level treatment of baseline normalisation choices, including
  `dB = 10·log₁₀(P_task/P_base)`, and of time-frequency methods generally.
- Pfurtscheller & Lopes da Silva (1999), above — the ERD% formulation, the main alternative to a dB
  baseline.

## 22.7 Aperiodic components (relevant to a known limitation)

- Donoghue, T., Haller, M., Peterson, E. J., et al. (2020). Parameterizing neural power spectra
  into periodic and aperiodic components. *Nature Neuroscience*, 23(12), 1655–1665.
  — Why raw band power conflates oscillatory and aperiodic (1/f) activity, and why a change in
  band power is not necessarily a change in an oscillation. Supports PART 20.3 item 9.

## 22.8 Electrode nomenclature

- Jasper, H. H. (1958). The ten-twenty electrode system of the International Federation.
  *Electroencephalography and Clinical Neurophysiology*, 10, 371–375.
- Oostenveld, R. & Praamstra, P. (2001). The five percent electrode system for high-resolution EEG
  and ERP measurements. *Clinical Neurophysiology*, 112(4), 713–719.

## 22.9 Lab Streaming Layer

- Kothe, C., Shirazi, S. Y., Stenner, T., et al. (2024). The Lab Streaming Layer for
  Synchronized Multimodal Recording. *bioRxiv*, 2024.02.13.580071.
- `sccn/liblsl` documentation — the source of the `time_correction()` semantics quoted verbatim in
  `LslBinding.cs` and reproduced in PART 3.3.

## 22.10 A note on citation integrity

Every reference above is a real, checkable publication. Author names, years, journals and
volume/page details have been given as accurately as possible from established knowledge; readers
should nonetheless **verify each citation against the original source before using it in a
publication**, as this document is a code reconstruction and not a literature review, and no
reference here was retrieved from a database during its writing.

**Where the literature is used in this document, it justifies a *method* (why Welch, why a Hann
window, why log-transform a power ratio) or provides *background expectation* (what frontal theta
generally does). It is never used to support a claim about what IKEA_EEG has measured.**

---

# OPEN QUESTIONS

## Blocking — must be answered before any participant data is collected

**Q1. Does AURA transmit eight distinct channels?**
The single live feature run produced bit-identical values on all eight electrodes. The Unity
extraction path has been eliminated as a cause; the amplifier has not been tested.
→ **Resolved by:** `ProbeAuraChannels.ps1` against live hardware (PART 21, Phase 3).

**Q2. Is the human-verified channel order still correct?**
Nothing in software can check it. The stream publishes no labels.
→ **Resolved by:** re-reading and photographing the AURA UI (PART 21, Phase 2.8).

**Q3. Are AURA's notch and band-pass still OFF?**
Same problem: human-verified, not self-verifying. If either is ON, the Unity filter is a second
filter in an unknown cascade.
→ **Resolved by:** re-reading the AURA UI (PART 21, Phase 2.7).

**Q4. Is the signal actually cortical EEG?**
Even eight distinct channels could be eight distinct noise sources.
→ **Resolved by:** the eyes-open/eyes-closed alpha control (PART 21, Phase 7).

## Important — must be answered before results are reported

**Q5. What are the amplitude units?**
Currently unknown. Blocks absolute reporting; does not block within-subject relative analysis.
→ **Resolved by:** the evidence listed in PART 7.4.

**Q6. When should spectral features be computed during a run?**
`AnalyzeLatestWindow` has no runtime caller. Continuously on a timer? Once per trial? Locked to
markers? This determines what data the experiment actually produces.

**Q7. What is the baseline protocol?**
Eleven decisions remain open (PART 15.6). At minimum, duration, eye state, visual scene and
per-run-vs-per-session must be fixed before the first participant.

**Q8. Should analysis be baseline-normalised or condition-contrasted?**
A within-task difficulty contrast would avoid Q7 entirely and may be methodologically stronger for
this design.

**Q9. Does the analysis timebase still behave correctly against live AURA?**
Live-validated before the final incremental-emission fix; not after.
→ **Resolved by:** `Diagnose Timestamp Continuity` (PART 21, Phase 4.2).

## Methodological — should be answered before publication

**Q10. Is the settling criterion defensible as stated?**
It is an undocumented guard interval that happens to land at −63 dB (PART 9). Either derive it or
document it honestly as a heuristic with its analytic equivalent.

**Q11. Should the band-edge truncation bias (~15 %) be corrected?**
It cancels in ratios and dB values but biases absolute power (PART 12.5).

**Q12. Is a fixed 8–12 Hz alpha band adequate?**
Individual alpha frequency varies by several Hz. `PeakFrequency` exists but is unused.

**Q13. Should aperiodic (1/f) activity be separated from band power?**
Current band power conflates oscillatory and aperiodic components.

**Q14. How should ocular and muscle artefact be handled?**
No detection or correction of any kind exists. Frontal theta is the feature most at risk.

**Q15. What happens on a mid-session amplifier dropout?**
The filter state and settling counter survive a reconnect with the same channel count. Whether
that is correct depends on the gap length; the code makes no distinction.

**Q16. Should the reference scheme be documented, and is re-referencing needed?**
The reference is undocumented, which limits the interpretation of every channel and blocks any
principled inter-channel correlation check.

---

# METHODOLOGICAL DECISIONS REQUIRED

Grouped by who must make them and when.

## Before the next hardware session (researcher)

| # | Decision |
|---|---|
| D1 | The exact pre-session verification procedure: which AURA settings are read and recorded, and by whom |
| D2 | Whether impedance is recorded per electrode, and the acceptance threshold |
| D3 | Whether the eyes-open/eyes-closed alpha control becomes a mandatory per-session check |
| D4 | Where session transcripts and probe outputs are archived |

## Before the first participant (researcher + developer, jointly)

| # | Decision | Reference |
|---|---|---|
| D5 | Baseline duration | PART 15.6 #1 |
| D6 | Eyes open, eyes closed, or both | PART 15.6 #2 |
| D7 | Visual scene during baseline | PART 15.6 #3 |
| D8 | Movement and posture instructions | PART 15.6 #4 |
| D9 | How speech epochs are handled | PART 15.6 #5 |
| D10 | Baseline per run or per session | PART 15.6 #6 |
| D11 | Baseline before, after, or bracketing the task | PART 15.6 #7 |
| D12 | Baseline inside or outside the headset | PART 15.6 #8 |
| D13 | Retry and abandonment criteria for a flagged baseline | PART 15.6 #9 |
| D14 | What is stored as the baseline (scalar, per-channel, full PSD, variance) | PART 15.6 #10 |
| D15 | Whether baseline normalisation is the primary analysis at all | PART 15.6 #11 |
| D16 | The feature computation cadence during a run | Q6 |
| D17 | Whether features are event-locked, and to which markers | PART 20.3 #5 |
| D18 | Which features are persisted, where, and with what quality metadata | PART 20.3 #4 |

## Before results are reported (analyst)

| # | Decision |
|---|---|
| D19 | Whether absolute power is reported at all, given unresolved units |
| D20 | Whether band power is log-transformed before statistics (recommended) |
| D21 | Whether individual alpha frequency is estimated and bands adjusted per participant |
| D22 | Whether aperiodic activity is separated |
| D23 | Artefact rejection criteria, and whether any offline correction is applied |
| D24 | Whether the theta/alpha ratio appears in results at all, and if so how it is labelled |
| D25 | The statistical model — within-subject contrasts, mixed effects, participant as random effect |

## Code changes implied (developer) — none made by this document

| # | Change | Priority |
|---|---|---|
| C1 | Call `AnalyzeLatestWindow` on a defined cadence | **P0** — nothing works without it |
| C2 | Add a call site for `CaptureBaseline`, once D5–D15 are decided | **P0** |
| C3 | Clear `filteredBuffer` in `BeginRun`, alongside the raw buffer | **P1** |
| C4 | Persist features and quality flags to the run record | **P1** |
| C5 | Derive `SettlingSamples` from the pole radius and a named tolerance | **P2** |
| C6 | Add impulse/step-response settling assertions | **P2** |
| C7 | Fix the "3 time constants" wording; make `EegSpectralDiagnostics` ask the filter | **P3** |
| C8 | Add unit tests for the per-channel quality detectors | **P2** |
| C9 | Interpolate PSD at band edges to remove the truncation bias | **P3** |
| C10 | Cache `FindAnyObjectByType` results in the monitor | **P3** |
| C11 | Remove or use the dead `m_TypicalStep` array | **P3** |
| C12 | Consider a group-delay computation, if ERP work is ever planned | **P3** |

---

# NEXT VALIDATION CHECKPOINT

## The checkpoint

**Run PART 21 Phases 0–4 against live AURA hardware, in a single session, and record every
transcript.**

## Entry criteria

- AURA hardware available and streaming
- Self test passes with 0 failures
- No synthetic outlet running
- A researcher available to read and photograph the AURA UI

## The one question this checkpoint answers

> **Does AURA transmit eight genuinely distinct channels, and does Unity see them as distinct?**

## Exit criteria — all four must hold to declare the checkpoint PASSED

1. `ProbeAuraChannels.ps1` reports `distinct = 8/8` on ~100 % of samples, reproduced across three
   runs.
2. `Analyze Spectral Window` reports **8 distinct** per-channel theta and alpha values, with
   `QUALITY: PASS` and no `IdenticalChannels` flag.
3. `Diagnose Timestamp Continuity` reports 0 non-monotonic steps and 0 gaps on the **analysis**
   series, closing the outstanding live re-validation of the final timebase model.
4. `Test Event Window` reports ~750 samples, status `Complete`, brackets the event.

## What each outcome unlocks or blocks

| Outcome | Meaning | Next |
|---|---|---|
| **All four pass** | The instrument is trustworthy end to end. The PART 17 anomaly is resolved as either a hardware condition since corrected, or a stale observation. | Proceed to Phase 7 (alpha control), then to D5–D18 and code changes C1–C2. |
| **Probe distinct, Unity identical** | 🔴 **A code defect the synthetic control could not reproduce.** | Stop. Treat as P0. Collect both transcripts and bisect against the revision identified in Phase 0.2. |
| **Probe identical** | Acquisition problem (H1). | Phase 5 troubleshooting. No band power is interpretable until resolved. |
| **Probe distinct but near-identical statistics** | Possible common-mode domination (H3). | Phase 6. |
| **Continuity shows non-monotonic analysis timestamps** | The final timebase model has a live failure mode the synthetic test missed. | Independent P1; blocks any windowing claim. |

## Explicitly NOT in scope for this checkpoint

- Participant recruitment
- Baseline protocol decisions (D5–D15)
- Any code change (C1–C12)
- Any cognitive-load hypothesis
- Any statement about what the EEG *means*

**This checkpoint validates the instrument. It does not measure a phenomenon. Those remain
different achievements, and the second cannot honestly begin until the first is complete.**
