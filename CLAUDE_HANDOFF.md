# CLAUDE_HANDOFF.md

Continuity document for Claude Code sessions on the **IKEA_EEG** Unity project.
Written 2026-08-21, updated 2026-08-25. Reflects the state of the files on disk at that moment.

> **Status honesty rule used throughout this document.** Every claim is tagged:
> **[VERIFIED]** tested with evidence · **[IMPLEMENTED, UNVERIFIED]** code exists, not exercised ·
> **[PARTIAL]** incomplete · **[PLANNED]** not built.
> Nothing planned is described as done. Where a live result is quoted, the measurement is quoted.

---

## 1. Project Overview

### What it is
A Unity 6000.3.21f1 (URP, OpenXR, XR Interaction Toolkit 3.4.1) VR cognitive experiment for
Meta Quest, run over **Quest Link** from a Windows PC. It combines a **verbal memory task**
(word encoding, immediate recall, delayed recall) with an **executive-function task** (chair
selection by three attributes), and is being extended with **live EEG acquisition and spectral
feature extraction** from an AURA amplifier over LSL.

### Research purpose
Behavioural + EEG measurement during a VR cognitive battery, with the eventual goal (NOT yet
built) of neuroadaptive difficulty driven by frontal theta / posterior alpha.

### Current development stage
- Behavioural VR experiment: **mature and verified.**
- EEG transport, timing and alignment: **verified against live hardware.**
- EEG preprocessing + spectral features: **implemented; extraction verified against a synthetic
  known-good stream. The one live run's values remain suspect for acquisition reasons** (see §10).
- Researcher monitor: **implemented and now has a data source in the scene, but still never
  opened interactively.**
- Neuroadaptation: **[PLANNED] — explicitly out of scope so far.**

### Two machines
| | |
|---|---|
| **Computer A** — 192.168.10.123 | Windows, Unity, Quest Link, `Assets/Plugins/lsl.dll` (liblsl 1.17.7, protocol 1.10) |
| **Computer B** — 192.168.10.158, hostname `laptop-san` | Runs AURA, publishes LSL streams `AURA`, `AURA_Filtered`, `AURA_Power` |

Both need `lsl_api.cfg` (present at `C:\Users\Mariana\lsl_api\lsl_api.cfg`, confirmed loaded by
liblsl's own log line).

---

## 2. Current Unity / Experiment State

### Flow as implemented
Two verbal-memory protocols share the state machine, selected by
`ExperimentConfig.protocolMode` (default **Recognition** since 2026-08-25):

```
FreeRecall   : WordEncoding(spoken)  → ImmediateRecall       → ... → DelayedRecall
Recognition  : WordEncoding(visual)  → ImmediateRecognition  → ... → DelayedRecognition
```

```
LanguageSelection → Area 0 Familiarization → Idle(Area A) → AreaAInstructions
→ WordEncoding → ImmediateRecall|ImmediateRecognition → ReadyForAreaB → [Enter Showroom]
→ AreaBInstructions → (ChairInstruction → ChairSelection → ChairTrialFeedback
   → ChairInterTrialInterval) × N → ReadyForAreaC → [Exit]
→ DelayedRecall|DelayedRecognition → Results → {NEW TRIAL | RESTART | END}
```
`ExperimentState` enum (`Scripts/Experiment/ExperimentState.cs`) also has `Ended` and
`Aborted`, both appended so legacy numbering is preserved.

### Key behaviours [VERIFIED via self-test + Quest sessions]
- **Language selection first** (EN / ES / JA), before any content. RESTART clears the language;
  NEW TRIAL preserves it.
- **Area 0**: practice objects (4 coloured spheres), controller help, narration, SKIP INTRO,
  START EXPERIMENT.
- **Area A**, FreeRecall: 5 spoken words (never displayed — auditory-only), then beep, then
  immediate recall recorded to WAV with adaptive silence-based auto-stop. **Unchanged.**
- **Area A**, Recognition: 15 VISUAL words at 2 s each (never spoken), then immediate
  SEEN BEFORE / NOT SEEN BEFORE recognition. Stimulus words are **DEVELOPMENT PLACEHOLDERS,
  NOT CNS STIMULI**. **[IMPLEMENTED, UNVERIFIED on hardware]**
- **Area B**: N chair trials (default 3), difficulty LOW→MEDIUM→HIGH by distractor similarity,
  seeded reproducible generation, dual-trigger selection.
- **Area C**: delayed recall (FreeRecall) or delayed recognition (Recognition), then results.
- **Run management**: NEW TRIAL (keeps run data, new run_index, advances word set), RESTART
  (discards run, new experiment session, back to language screen), END (terminal, hides all
  run buttons), ABORT (developer only, terminal, invalidates active trial without scoring it).

### Outputs per run
- `events.csv` — **45 columns** (`Scripts/Data/CsvEventSink.cs`). Columns are **append-only**;
  legacy indices never move. The last 10 are the recognition-protocol columns (2026-08-25);
  before those, `platform_language` and `lsl_timestamp`. A FreeRecall run leaves the
  recognition columns empty, so files from both protocols share one schema. **[VERIFIED]**
- `audio/*.wav` — recall recordings.
- `raw_eeg.csv` — per-run EEG when an amplifier is connected (see §4).
- session summary + researcher summary + manual scoring template.

### Protocol vs implementation differences [IMPORTANT]
- The task uses **5 words**; the RAVLT-derived source lists are **15 words**. Every language set
  is a documented consecutive 5-word subset. **It is NOT a standardized RAVLT administration**
  and the code says so in metadata, UI provenance and the CSV.
- English word sets are **prototype, not validated** (`validatedForResearch = false`).
- Spanish/Japanese sets are **research-sourced stimuli under an adapted procedure**, also
  `validatedForResearch = false`.
- Word-set pool sizes: EN 2, ES 12, JA 3. Run N of a sitting uses set `(N-1) % count`; when it
  wraps, reuse is logged as `word_set_reused_in_session=TRUE` and warned in console.

---

## 3. EEG + LSL Architecture

### Pipeline
```
AURA (Computer B)
  → LSL network (UDP discovery, TCP data)
  → LslBinding            reflection bridge, NO compile-time LSL dependency
  → AuraLslReceiver       ONE inlet, resolves stream "AURA" by name
      ├─ remote timestamp (sender clock)
      ├─ + time_correction  → local raw timestamp
      └─ + linear-fit grid  → ANALYSIS timestamp  ← authoritative for analysis
  → RawEegRingBuffer      indexed on ANALYSIS timestamp
  → RawEegRecorder        raw_eeg.csv, background thread
  → EegFeaturePipeline    continuous filter → filtered buffer → Welch PSD → features
  → LatestEegFeatures     read-only snapshot
  → EegResearcherMonitor  EditorWindow, view only
```

### Live stream facts [VERIFIED by pulling full StreamInfo XML]
```
name=AURA  type=EEG  channels=8  nominal_srate=250  format=float32
source_id=AuraLSL-<timestamp>  hostname=laptop-san  desc=<EMPTY>
```
**`<desc/>` is empty on all three AURA streams.** The stream publishes **no channel labels, no
units and no filter state.** Everything in §Montage below is human-verified, not stream-derived.

### The three timestamps [VERIFIED]
| name | meaning | use |
|---|---|---|
| `remoteLslTimestamp` | sender's clock, as `pull_sample` returns it | provenance only |
| `lslTimestamp` | `remote + time_correction` — this machine's LSL clock | cross-machine sync provenance |
| `analysisTimestamp` | de-jittered uniform grid | **authoritative for buffering, windows, epochs** |

Clock correction observed live: **≈ 299911.01 s**, stable to ~1 ms across runs. Formula
`local = remote + correction` is taken from liblsl's own documented semantics (an *addition*),
not inferred from a sign.

### Why the analysis timebase exists [VERIFIED finding]
An 8 s live capture showed AURA delivering **9-sample chunks** whose anchor timestamps jitter:
every anomaly index was a multiple of 9, backward steps to −30 ms, gaps to +62 ms, while the
median interval was **exactly 4.000 ms**. Sample *values* are fine; only chunk-anchor timestamps
are disturbed. Proven to originate **before** clock correction (remote and local series showed
identical anomaly counts and identical deltas).

### Assumptions currently made
- Sampling rate, channel count and format are **always read from stream metadata**, never
  hard-coded. Self-tests deliberately use 256 Hz / 137 Hz / 7 channels to catch any hard-coding.
- Channel **order/labels** are human-verified, not stream-verified — a re-ordered cap would pass
  unnoticed.
- Amplitude **units are unknown**. Everything reports "AURA native units".
- `proc_none` on the inlet — no liblsl post-processing (no dejitter, no monotonize, no clocksync).

### Markers (outbound)
`LslMarkerSink` publishes an outlet `IKEA_EEG_Markers`. Payload format is
`EVENT_TYPE|key=value|...` and is **LOCKED** — do not change.

---

## 4. Live EEG Processing — file by file

### `Scripts/Data/AuraLslReceiver.cs` (630 lines)
Resolves `"AURA"` **by exact name** (never `AURA_Filtered`/`AURA_Power`). Opens one inlet, reads
metadata, obtains `time_correction` on connect (2 s timeout) and refreshes every 5 s (0.2 s
timeout) — **never per sample**. `Drain(max)` polls with a **zero timeout** so it never blocks a
frame. Emits `RawEegSample { lslTimestamp, remoteLslTimestamp, timeCorrection, analysisTimestamp,
channels[] }` via `sampleReceived`. Feeds `buffer` and optional `recorder`.
**Cadence: per sample.** **[VERIFIED live]**

### `Scripts/Data/EegAnalysisTimebase.cs` (243 lines)
Emits a uniform grid: `next = last + secondsPerSample`, where the interval is *steered* toward a
sliding least-squares fit of (index, local raw timestamp) with the adjustment bounded to ±0.4 ×
**nominal** interval, and a fitted slope accepted only within ±10 % of the advertised rate.
Monotonic **by construction**, not by clamping the output. **[VERIFIED synthetically]**: 880
backward steps in → 0 out; steps 2.056–5.738 ms; median 3.9993 ms.
⚠️ **Not re-validated against live AURA after the final fix** — AURA went offline.

### `Scripts/Data/RawEegRingBuffer.cs` (457 lines)
Bounded ring, capacity in **seconds** × advertised rate (60 s × 250 Hz = 15000 samples live).
One flat `double[]`, allocated once. `TryGetWindow(eventTs, pre, post)` returns samples,
timestamps and an honest status: `Complete / MissingStart / MissingEnd / MissingBothEdges /
Empty`. Tracks monotonicity, gaps (tolerance = 4 nominal intervals), largest gap, effective rate.
**Cadence: add per sample; windows on demand.** **[VERIFIED]**

### `Scripts/Data/EegBandpassFilter.cs` (311 lines)
Causal Butterworth band-pass, **8th order overall** = 4th-order HP + 4th-order LP, as **4 biquads
per channel**, Direct Form I, independent state per channel. Coefficients from bilinear transform
with Butterworth section Qs, derived from the **supplied** fs. Reports `SettlingSamples`
(3 × time constant of the HP = **750 samples / 3.00 s at 250 Hz**).
**Causal → NOT phase-linear.** Valid for spectral power, **not** for ERP latency. `filtfilt`
deliberately absent. **[VERIFIED synthetically]**:
`0.2 Hz −55.92 dB · 6 Hz 0.00 · 10 Hz 0.00 · 30 Hz −0.31 · 60 Hz −18.41 · 80 Hz −35.92 dB`

### `Scripts/Data/EegSpectralAnalyzer.cs` (377 lines)
Dependency-free static class: local radix-2 FFT, **periodic** Hann `0.5(1−cos(2πn/N))`, Welch
PSD, trapezoidal band integration. One-sided scaling `|X|²/(fs·Σw²)`, interior bins doubled,
**DC and Nyquist not doubled**, each segment de-meaned before windowing.
`physicalResolutionHz = fs/segmentSamples` reported **separately** from `binSpacingHz = fs/fftLength`.
**[VERIFIED synthetically]**: 6 Hz→theta dominant · 10 Hz→alpha dominant · equal mix ratio 1.000 ·
2× amplitude → ratio 4.000 · white noise 1.405 · band power invariant to FFT length ·
unit sine gives exactly 0.500 = A²/2.

### `Scripts/Data/EegFeaturePipeline.cs` (782 lines) ⚠️ NOT IN THE SCENE
`MonoBehaviour`, `[RequireComponent(AuraLslReceiver)]`. Subscribes to `sampleReceived`, filters
**continuously** (per sample, 4 biquads/channel), stores filtered signal in a **separate** ring
buffer keyed on the same analysis timestamp — raw is never overwritten.
`AnalyzeLatestWindow(seconds)` runs at **window cadence**: cuts a window, assesses quality,
Welch-PSDs each channel, integrates theta (4–8 Hz) and alpha (8–12 Hz), resolves ROIs **by label**
through `AuraMontageConfig`, computes exploratory ratio/log-ratio, applies baseline if captured.
Publishes `LatestEegFeatures`.
`CaptureBaseline(start, end)` requires ≥4 s, settled filter, valid ROI, unflagged window —
**never inferred**. `EnsureSubscribed()` is public because `OnEnable` does not fire for a
component added in **edit** mode.

### `Editor/EegSpectralDiagnostics.cs` (210 lines)
`IKEA_EEG → EEG → Analyze Spectral Window`. Builds a **temporary** hidden host with its own
receiver + pipeline, collects ~8 s, prints one concise report. **[RAN LIVE — see §9/§10]**

### `Editor/EegResearcherMonitor.cs` (423 lines)
`IKEA_EEG → EEG → Researcher EEG Monitor`. `EditorWindow`, desktop only, **read-only**: finds the
existing pipeline/receiver via `FindAnyObjectByType` and **creates no inlet, receiver, buffer,
filter or FFT**. 8 traces from montage labels, last 8 s, ~15 Hz repaint, Raw/Filtered toggle that
only selects which existing buffer to read, display-only downsampling into a reused list.
**[IMPLEMENTED, UNVERIFIED — never opened interactively.]**

### `Scripts/Data/AuraMontageConfig.cs` (384 lines)
ScriptableObject. **No asset exists yet** — code calls `CreateHumanVerifiedDefault()`, whose
serialized defaults carry the verified mapping. `ResolveRoi` is **all-or-nothing**.
Units gated behind `unitsConfirmed = false`.

### `Scripts/Data/RawEegRecorder.cs` (309 lines)
Per-run `raw_eeg.csv` on a background thread, bounded queue (200 000), drop counter.
Columns: `lsl_timestamp_analysis, lsl_timestamp_local_raw, lsl_timestamp_remote_raw,
time_correction, ch1..chN`. Values written with `"R"` round-trip. No filtering/scaling.
Channels **numbered, not named** — no montage claimed in the file.
**[IMPLEMENTED, UNVERIFIED against a real run]**

### `Scripts/Data/LslBinding.cs` (1296) / `LslClock.cs` (138)
Reflection bridge — **no `using LSL;` anywhere**, so the project compiles and runs if liblsl is
removed. Overloads matched on **first parameter**, remaining args filled from the binding's own
declared defaults (C# optional params don't exist at the reflection layer — this was a real bug).
`LslClock.NowString()` stamps every event's `lsl_timestamp`; returns **empty**, never a
substitute clock, when liblsl is absent.

---

## 5. Montage (human-verified, NOT from LSL)

```
CH1=Fp1  CH2=F3  CH3=Fz  CH4=F4  CH5=Cz  CH6=P3  CH7=Pz  CH8=P4
FRONTAL_THETA   = F3, Fz, F4   → sample indices 1,2,3
POSTERIOR_ALPHA = P3, Pz, P4   → sample indices 5,6,7
```
Fp1 and Cz are mapped and available but **not** in any aggregated ROI.
Acquisition filter state, human-verified from the AURA UI: **Notch OFF, Bandpass OFF**
(`EegConfigSource.HumanVerifiedAcquisitionUi`). This is the permission gate for Unity-side
filtering — without it we would risk double-filtering.

---

## 6. Data Integrity and Timing

| property | status |
|---|---|
| Analysis timestamps monotonic | **[VERIFIED]** live: 0 non-monotonic, 0 gaps, median 3.99 ms |
| Raw remote timestamps | **non-monotonic by design of the sender** — 80–90 per 8 s, worst −30 ms |
| Raw timestamps preserved | **[VERIFIED]** all three kept per sample and per CSV row |
| Event ↔ EEG alignment | **[VERIFIED]** event window PASS: 750 samples, Complete, brackets event |
| Sample ordering | **[VERIFIED]** single-threaded, inserted in exact pull order, no sort/queue |
| Dropped samples | **not detectable** — LSL exposes no packet-loss counter; none is invented |
| Unity `Time.time` as a timestamp | **never used** — asserted in self-tests |

### 🔴 OPEN — per-channel spectral outliers (F3 6 decades above neighbours)
A live quiet-window capture reported `F3 theta = 3.6484E+07` against neighbours near `4E+01`,
with `F4 = 1.7449E+03` and `P4 = 4.2842E+04`. **Root cause NOT proven.** What IS established:

| candidate | status | evidence |
|---|---|---|
| Channel association / de-interleave | **ELIMINATED** | §10, synthetic 8-ch stream, band powers exact |
| HP settling transient on AURA's huge DC offsets | **REFUTED [VERIFIED]** | see below |
| Electrode artefact at source (pop, intermittent contact) | **REMAINS — untested** | needs live capture |

**The settling hypothesis was tested and failed.** AURA carries per-channel DC offsets around
10⁵ (measured: `+1.07E+05` on CH2, `−2.19E+05` on CH4) with AC of sd ~50, so a 2000:1 ratio; the
concern was that the 3-time-constant settling rule leaves a decaying low-frequency transient that
would land in theta and scale with each channel's offset. Replaying the **measured** offsets and
AC sizes through the real filter and real Welch (self-test `EEG DC-OFFSET REJECTION`) shows the
opposite: the residual mean in the analysis window is `4E−04 … 7E−02` — **four to six orders of
magnitude below** each channel's own AC — and theta tracks each channel's **AC content**, not its
offset. The 4th-order high-pass removes AURA-scale DC completely. **Settling is adequate; this
explanation is dead.**

What the raw data does show: CH4 and CH8 already carry ~14× the AC of the other channels
(sd 692 and 854 vs 43–103), which is ~2.4 decades of power **at source, before any Unity code**.
Large genuine inter-channel power differences therefore exist. The live anomaly was ~6 decades,
which needs an AC excursion ~1000× the others — consistent with an electrode artefact, but
**not demonstrated**, because AURA stopped transmitting before the anomaly could be captured.

**To close it:** `IKEA_EEG → EEG → Diagnose Channel Integrity` while the anomaly is present.

### ⚠️ The Researcher Monitor cannot show relative amplitude — by construction
`DrawTrace` normalises every trace to **its own** min/max (`norm = (v − min) / span`), so a
channel with a huge excursion and a quiet channel both fill their box identically. This is why
the monitor "showed F3 in roughly the same order as Fz/Cz/P3" while the spectrum disagreed —
**both were correct**; the monitor was never displaying absolute amplitude. The per-trace
`min … max` text in the corner is the only absolute cue. Left unchanged in this pass by explicit
instruction (no UI redesign yet); it is the **first item** for the dashboard pass — a shared or
optional fixed Y scale would have made this investigation unnecessary.

### Quality flags **that exist** (`EegQualityFlags`)
`FilterNotSettled, InsufficientSamples, NaNPresent, InfinityPresent, Flatline, SaturationLike,
AbruptDiscontinuity, ExtremeDynamicRange, RoiUnresolved, IdenticalChannels,
ChannelPowerOutlier, TransientArtifactSuspected` — all thresholds **relative**, none in µV.
Windows are **flagged, never repaired**.

### `ChannelPowerOutlier` / `TransientArtifactSuspected` (added 2026-08-24) **[VERIFIED]**
`1 << 10` and `1 << 11`, appended. Both exist because a window carrying a 6-decade channel
imbalance previously reported `QUALITY: PASS`.

- **`ChannelPowerOutlier`** — a channel more than **2 decades** from the **median** log10 band
  power of the others, judged per band. The median is essential: with a mean-and-SD test the
  outlier inflates both the mean and the SD it is compared against, so it raises its own
  acceptance bar and passes. The self-test pins this (`mean` = 6.1E+06 vs `median` = 41.2 on the
  real failing values), so a future "simplification" to a mean is caught. 2 decades sits far
  above real inter-electrode variation (~0.5–1 decade) and far below the 6-decade failure.
- **`TransientArtifactSuspected`** — first third vs last third RMS differing by ≥3× in either
  direction. Says the window is non-stationary; deliberately does **not** name a cause.
- **ROI contamination** — `roiContamination` is set when a flagged channel is one an ROI actually
  averages, distinguishing "Fp1 broke, ROIs unaffected" from "F3 broke, Frontal Theta is now the
  average of two good electrodes and a ruined one". Nothing is dropped or re-averaged: the
  montage's all-or-nothing rule forbids silently changing what a measurement means.
- `LatestEegFeatures.Explain()` spells the flags out with the electrodes named, because a flag
  name does not tell a researcher which electrode to re-seat.

### Optional 60 Hz notch (added 2026-08-24) **[VERIFIED synthetically]**
`EegBandpassFilter(..., notchEnabled, notchHz, notchQ)`, **OFF by default**; also
`EegFeaturePipeline.ConfigureNotch(...)` and three serialized fields.
RBJ band-stop biquad, zeros **on** the unit circle, normalised by a0, appended as **one**
second-order section **last** in the chain (HP → LP → notch), so enabling it cannot change the
conditioning or settling of the band-pass in front of it. Causal like everything else here:
**not phase-linear**, group delay concentrated near the notch — fine for power, wrong for ERP.
`fs` is always a parameter; a notch at or above Nyquist is **refused**, not aliased.
Measured at 60 Hz / Q 30 / fs 256: **bandwidth 2.000 Hz**, notch alone **−240 dB** at centre,
**0.000 dB at 6 Hz**, **−0.0001 dB at 10 Hz**; whole chain **−99.99 dB** at 60 Hz with 6 and
10 Hz at 0.00 dB.
**Documented as optional and redundant here:** the 40 Hz low-pass alone already gives
**−18.41 dB** at 60 Hz and neither theta nor alpha is near it. It exists because it is part of
the researcher's stated preprocessing protocol — *not* because the 1–40 Hz passband needs it.

### `IdenticalChannels` (added 2026-08-24) **[VERIFIED]**
`1 << 9`, **appended** — no existing flag value moved (asserted in the self-test).
The one check that needs to see every channel at once. Two independent tests:
1. **Samples** — any channel pair bit-identical at every sample in the window.
2. **Band powers** — every channel yielding exactly the same theta *and* alpha. Welch de-means
   each segment, so channels differing **only by a DC offset** produce identical spectra from
   non-identical samples; test 1 alone would miss that case.

Exact equality only — no correlation threshold. Two electrodes that are merely *similar* are a
clinical judgement about common-mode signal and reference placement, which this code has no
basis to make while scaling is unverified; two that are *bit-identical* are an engineering fact.
NaN needs no special case: IEEE inequality means a NaN channel can never read as a duplicate.
Reports `identicalChannelPairs` and `identicalChannelDetail` (naming the electrodes) on
`LatestEegFeatures`; surfaced in the spectral diagnostic and the Researcher Monitor.
Runs in **both** analysis paths, so `CaptureBaseline` also rejects a duplicated-channel baseline.

---

## 7. File-by-File Map

*Path → responsibility → key dependencies → status*

### EEG / LSL
| Path | Responsibility | Depends on | Status |
|---|---|---|---|
| `Scripts/Data/LslBinding.cs` | Reflection bridge to liblsl | `Assets/Plugins/LSL.cs`, `lib/lsl.dll` | VERIFIED |
| `Scripts/Data/LslClock.cs` | `local_clock()` for event stamping | LslBinding | VERIFIED |
| `Scripts/Data/LslMarkerSink.cs` | Outbound marker outlet | LslBinding, EventBus | VERIFIED (loopback) |
| `Scripts/Data/AuraLslReceiver.cs` | The **only** AURA inlet | LslBinding, RingBuffer, Timebase | VERIFIED |
| `Scripts/Data/EegAnalysisTimebase.cs` | De-jittered uniform grid | — | VERIFIED synthetically |
| `Scripts/Data/RawEegRingBuffer.cs` | Bounded history + windows | — | VERIFIED |
| `Scripts/Data/RawEegRecorder.cs` | `raw_eeg.csv` writer | LslBinding (metadata) | UNVERIFIED live |
| `Scripts/Data/EegRunRecorder.cs` | Ties EEG file to run lifecycle | AuraLslReceiver, RawEegRecorder | UNVERIFIED live |
| `Scripts/Data/EegBandpassFilter.cs` | Butterworth SOS band-pass | — | VERIFIED synthetically |
| `Scripts/Data/EegSpectralAnalyzer.cs` | FFT / Hann / Welch / bandpower | — | VERIFIED synthetically |
| `Scripts/Data/EegFeaturePipeline.cs` | Live preprocessing + features | Receiver, Filter, Analyzer, Montage | EXTRACTION VERIFIED (§10) |
| `Scripts/Data/AuraMontageConfig.cs` | Electrode map + provenance | — | VERIFIED (self-test) |

### Experiment core
| Path | Responsibility | Status |
|---|---|---|
| `Scripts/Experiment/ExperimentManager.cs` (3812) | **The** state machine; owns every protocol coroutine | VERIFIED |
| `Scripts/Experiment/ExperimentConfig.cs` | All tunables + narration clips | VERIFIED |
| `Scripts/Experiment/WordListDefinition.cs` | Word sets + provenance fields | VERIFIED |
| `Scripts/Experiment/SessionResults.cs` | Metrics + participant/researcher summaries | VERIFIED |
| `Scripts/Experiment/ChairTrialGenerator.cs` | Seeded chair block generation | VERIFIED |
| `Scripts/Experiment/ExperimentState.cs` | State enum (append-only) | VERIFIED |

### Core / data
| Path | Responsibility | Status |
|---|---|---|
| `Scripts/Core/EventLogger.cs` | Stamps + publishes every event | VERIFIED |
| `Scripts/Core/ExperimentEvent.cs` | 35-column row model | VERIFIED |
| `Scripts/Core/SessionClock.cs` | Monotonic session/interval timers | VERIFIED |
| `Scripts/Data/CsvEventSink.cs` | CSV writer, append-only header | VERIFIED |
| `Scripts/Data/SessionSummaryWriter.cs` | Derived summary files | VERIFIED |

### Memory / audio / UI / XR / interaction
`Scripts/Memory/VoiceRecallManager.cs` (recording + adaptive stop) · `RecallSilenceDetector.cs` ·
`WavUtility.cs` · `Scripts/Audio/ExperimentAudio.cs` (ownership-tagged playback) ·
`Scripts/UI/ExperimentUIController.cs` (passive view) · `Scripts/XR/XRRigTeleporter.cs`,
`DeveloperNavigation.cs` · `Scripts/Interaction/ChairSelectionTask.cs`, `ChairTarget.cs`,
`PracticeObject.cs`, `PracticeColors.cs` — **all VERIFIED.**

### Localization
`Scripts/Localization/` — `LocalizationTable.cs` (all strings × EN/ES/JA), `LocalizationKeys.cs`,
`ExperimentLocalization.cs`, `LocalizedText.cs`. **VERIFIED**, including a completeness audit that
found and fixed 5 real English leaks.

### Editor tools
| Path | Menu | Status |
|---|---|---|
| `Editor/ExperimentSceneBuilder.cs` (2913) | `IKEA_EEG/Build Experiment Scene` | VERIFIED |
| `Editor/ExperimentSelfTest.cs` (7643) | `IKEA_EEG/Run Self Test` — **54 sections** | VERIFIED |
| `Editor/ExperimentAssetBuilder.cs` | Config + word-list assets | VERIFIED |
| `Editor/ResearcherTools.cs` | LSL availability, marker loopback, AURA check | VERIFIED |
| `Editor/LslNetworkDiagnostics.cs` | `LSL/Diagnose Network` | VERIFIED |
| `Editor/EegDiagnostics.cs` (738) | Buffer / Event Window / Timestamp Continuity | VERIFIED |
| `Editor/EegSpectralDiagnostics.cs` | `EEG/Analyze Spectral Window` | RAN LIVE |
| `Editor/EegResearcherMonitor.cs` | `EEG/Researcher EEG Monitor` | UNVERIFIED |
| `Editor/WordClipGenerator.cs`, `NarrationClipGenerator.cs`, `WordSourcePools.cs` | TTS assets | VERIFIED |
| `Editor/ProbeLslStreams.ps1` | Out-of-process LSL probe (control experiment) | VERIFIED |
| `Editor/EegChannelIntegrityDiagnostics.cs` | `EEG/Diagnose Channel Integrity` — every stage per channel | BUILT, awaiting a transmitting AURA |
| `Editor/ProbeAuraChannels.ps1` | Out-of-process **channel-distinctness** probe | VERIFIED (both verdicts) |
| `Editor/SyntheticEegOutlet.ps1` | Synthetic known-good / known-bad LSL source | VERIFIED |

---

## 8. Scene / GameObject Architecture

Scene is **code-generated** — `IKEA_EEG/Build Experiment Scene` rebuilds it. Do not hand-edit and
expect it to survive.

```
Systems/ExperimentSystems/
├── EventLogger              (+ CsvEventSink, UnityConsoleEventSink, LslMarkerSink)
├── ExperimentAudio
├── VoiceRecallManager       (+ NullTranscriptionProvider)
├── ChairSelectionTask
├── XRRigTeleporter
├── EegAcquisition           ← AuraLslReceiver + EegRunRecorder
├── ExperimentUIController
├── DeveloperNavigation      (+ UI_DeveloperNavigation panel)
└── ExperimentManager        ← Bind(...) wires everything
```
World: `Area_0_Familiarization`, `Area_A_Entrance`, `Area_B_Showroom`, `Area_C_Exit`, XR Origin.

### ⚠️ Things that break silently
1. ~~**`EegFeaturePipeline` is NOT added by the scene builder.**~~ **FIXED 2026-08-24** — it is
   now added to the `EegAcquisition` GameObject beside the receiver and recorder, and the
   self-test asserts exactly one receiver and exactly one pipeline on that node. That assertion
   is what will catch a regression if the builder is ever changed again.
2. `ExperimentManager.m_EegRecorder` resolves via `FindAnyObjectByType` if unassigned — fine, but
   a missing `EegAcquisition` node silently disables all EEG with only a warning.
3. `AuraMontageConfig` has **no asset**; defaults come from code. Creating an asset without
   assigning it changes nothing; assigning a *wrong* one silently changes ROI meaning.
4. Serialized private fields set by the builder need `EditorUtility.SetDirty` — already done, but
   new wiring must do the same or it vanishes on save.

---

## 9. Verified Working

| Feature | How verified |
|---|---|
| Full VR experiment flow | Repeated Quest sessions across many passes |
| EN/ES/JA localization | Self-test renders every bound label in all 3 languages; 0 English leaks |
| ES/JA narration + word clips | Generated with native voices (Sabina es-MX, Haruka ja-JP); 17 word-clip folders on disk |
| Recall recording + adaptive stop | Quest-confirmed; detector unit-tested |
| Chair task, RT semantics, seeded generation | Self-test + Quest |
| NEW TRIAL / RESTART / END / ABORT | Self-test asserts state, buttons, file effects |
| Run duration | Fixed and asserted; measured from TRIAL_START to results |
| CSV pipeline, 35 columns | Self-test parses header + rows |
| LSL availability + native dll | `LSL/Check LSL Availability` → available True, local_clock responds |
| Marker loopback | `LSL/Run Marker Loopback Test` → PASS, marker received unchanged |
| AURA discovery + metadata | Full StreamInfo XML pulled live |
| Raw reception | 1000–2200 samples per multi-second capture |
| Clock correction | ≈299911.01 s, stable ~1 ms across 3 runs |
| Analysis timebase | Live: 0 non-monotonic, 0 gaps, median 3.99 ms |
| Event window | **750 samples, 8 ch, Complete, brackets event, PASS** |
| Timestamp continuity diagnostic | Identified the 9-sample chunk signature |
| Filter | Synthetic dB table (§4) |
| Welch PSD / bandpower | 5 synthetic tests + analytic A²/2 check |
| Montage + provenance | Self-test: mapping both directions, all-or-nothing ROI, unit labels |
| Channel separation through the real extraction path | Synthetic 8-ch stream; band powers match A²/2 exactly (§10) |
| `IdenticalChannels` flag | Unit assertions + end-to-end run against a duplicated-channel stream |
| Optional 60 Hz notch | Synthetic: -240 dB at centre, 0.000 dB at 6 Hz, -0.0001 dB at 10 Hz |
| Robust power-outlier + transient flags | Self-test, against the real failing values |
| DC-offset rejection at AURA scale | Measured offsets replayed; residual 4-6 decades below AC |
| **Self-test overall** | **1617 assertions, 0 failures** (`IKEA_EEG/Run Self Test`) |

---

## 10. Known Issues and Risks

### 🟠 P1 — All 8 EEG channels returned identical values (was P0; cause #2 eliminated)
The single live feature run produced **bit-identical** theta (2.2792E−6) and alpha (6.1697E−8) on
**every** electrode; ratio 36.94. Real EEG cannot do this. Three candidate causes were listed:
1. AURA genuinely sending identical channels (electrodes disconnected / test pattern);
2. a channel-extraction bug in `EegFeaturePipeline.AnalyzeLatestWindow` /
   `RawEegRingBuffer.TryGetWindow`;
3. overwhelming common-mode signal.

**Cause 2 is ELIMINATED [VERIFIED 2026-08-24].** AURA was offline, so the question was settled
against a synthetic stream instead of by inspection. `Editor/SyntheticEegOutlet.ps1` published a
float32 8 ch / 250 Hz stream **named `AURA`** from a separate process, each channel carrying a
different frequency *and* a different amplitude. Unity's real path — real receiver, real
reflection marshalling, real filter, real ring buffer, real de-interleave, real Welch — returned
**8 clearly distinct** per-channel band powers, quantitatively exact where the tone sits well
inside a band:

| electrode | injected | expected A²/2 | Unity measured |
|---|---|---|---|
| Fz | 6.5 Hz, A=2.0 | 2.000 | theta **1.9997** |
| P3 | 9.5 Hz, A=3.5 | 6.125 | alpha **6.1232** |
| Pz | 10.5 Hz, A=4.0 | 8.000 | alpha **7.9976** |
| F3 | 5.5 Hz, A=1.5 | 1.125 | theta **1.1248** |

Channels near a band edge (4.5 / 7.5 / 8.5 / 11.5 Hz) recover less, because the 2 s Hann main
lobe spills across the boundary — expected, and itself a sign the numbers are real.
**Our extraction does not collapse channels.** Locked in as a regression test (self-test section
`EEG CHANNEL SEPARATION + IDENTITY FLAG`).

**What is still open:** whether the real AURA was transmitting duplicated channels (cause 1) or
overwhelming common-mode (cause 3). That needs live hardware.
**Until then, the earlier live feature values remain untrustworthy** — but the code is no longer
a suspect, and the run would now be **flagged rather than silently reported as valid**.

**To close it:** with AURA streaming, run
`powershell -ExecutionPolicy Bypass -File Assets\IKEA_EEG\Editor\ProbeAuraChannels.ps1`.
Its verdict logic is verified in **both** directions against synthetic streams (distinct → "the
wire carries genuinely different channels"; duplicated → "the amplifier is sending the same
signal on every channel").

### ✅ RESOLVED — inter-channel identity quality flag
`EegQualityFlags.IdenticalChannels` now exists. See §6. **[VERIFIED]** by unit assertions *and*
end-to-end: the diagnostic run against a deliberately duplicated synthetic stream reproduced the
original P0 signature exactly and reported
`FLAGGED: IdenticalChannels` / `feature validity: False`, naming all 28 electrode pairs — where
the same input would previously have been reported as `PASS` / valid.

### ✅ RESOLVED — `EegFeaturePipeline` absent from the built scene
Added to the existing `EegAcquisition` GameObject in `ExperimentSceneBuilder` (with
`EditorUtility.SetDirty`), scene rebuilt, component confirmed in the saved `.unity` file.
The self-test now asserts exactly one receiver **and** exactly one pipeline, on the same node.

### 🟠 P1 — Researcher Monitor never opened
Compiles; behaviour unknown.

### 🟠 P1 — `raw_eeg.csv` never produced by a real run
The recorder is wired to `BeginSession`/finalise but no VR session has run with EEG connected.

### 🟡 P2 — Amplitude units unknown
Blocks any physical-unit reporting. AURA's UI shows a µV axis; the stream says nothing.

### 🟡 P2 — Montage is not self-verifying
A re-ordered 8-channel cap would pass `Validate()` unnoticed (only channel *count* is checked).

### 🟡 P2 — Analysis timebase not re-validated live after its final fix
The steering-bound fix landed after AURA went offline. Verified synthetically only.

### 🟡 P2 — Filter group delay
Causal → frequency-dependent delay. Fine for power; **wrong for ERP latency**. Documented in code.

### 🟡 P2 — AURA intermittently stops transmitting
Repeatedly resolved and accepted connections while sending zero samples. Proven not to be a Unity
fault (an independent PowerShell process using the same `lsl.dll` also got nothing).

### 🟡 P2 — Word lists not validated instruments
5-word subsets of 15-word forms. Never claim RAVLT equivalence.

### ⚪ Documented, deliberately not fixed
Materials re-serialise on every build · 16 superseded `ExperimentConfig` text fields remain
unused · self-test runs accumulate session folders.

---

## 11. Most Recent Changes

**No git repository exists** (`fatal: not a git repository`), so this is reconstructed from the
session and verified against files on disk.

Newest first:

-6. **Analysis time-base clock-domain fix (2026-09-02).** 🔴 **Affects all EEG epoching.**
    - **ROOT CAUSE.** `RefreshTimeCorrection(initial: true)` has a 2 s timeout; on failure
      `m_TimeCorrection` stays `0`, so `localRaw = remote + 0` — the SENDER's domain. Those
      samples seeded `EegAnalysisTimebase`, which anchors on its first sample and steers with a
      correction clamped to ±0.4 sample intervals. When the correction later arrived, the raw
      column snapped to local, the grid could not follow, saturated its clamp on **27.6 %** of
      samples and free-ran at `nominal − 0.4×nominal = 2.4 ms` = **416.7 Hz** for the whole
      recording — smooth, monotonic and fictional. Recovery would need ~7.8e8 samples (~36 days).
    - **Symptom** (`S_20260902_131219_r01_27eda8`): `lsl_timestamp_analysis` ≈ 1 340 672 s while
      events and `lsl_timestamp_local_raw` ≈ 97 872 s. Every epoch cut on it was empty.
    - **Files modified:** `Scripts/Data/EegAnalysisTimebase.cs` (re-anchor guard, `reanchorCount`
      / `isTracking` / `totalSaturatedRuns`, `InvalidateAnchor`, `Reset` clears history);
      `Scripts/Data/AuraLslReceiver.cs` (calls `InvalidateAnchor` on a clock-domain change,
      `timebaseDomainResets`); `Editor/EegRecordedWindowValidator.cs` +
      `…Window.cs` (integrity guard + "Reconstruct analysis from local_raw");
      `Editor/ExperimentSelfTest.cs` (`BLOCK 11`).
    - **TIME-BASE CONTRACT (now enforced):** `remote_raw` = sender's clock, provenance only,
      never for alignment. `local_raw` = this machine's LSL clock, same domain as
      `events_*.csv` `lsl_timestamp`, may be non-monotonic. `analysis` = **must stay in the local
      domain**, monotonic, mean spacing ≈ 1/nominal, directly comparable with event timestamps.
    - **Reset behaviour:** grid re-anchors on a `time_correction` domain change, on a drift beyond
      `max(5 s, 1000 × sample interval)`, on timestamp restart, and on `Reset()` (new
      stream/reconnect). Re-anchors are **counted and logged**, never silent.
    - **Thresholds are measured, not chosen.** Healthy drift 0.205 / 0.314 s and longest healthy
      saturation run 86 samples across the two recordings; bounds sit an order of magnitude above
      that and five orders below the failure. No session's numbers are hard-coded (asserted).
    - **Validation on both recordings** (replaying `local_raw` through the fixed class):
      previous session 249.9081 Hz, broken session 250.0383 Hz — both monotonic, 0 re-anchors.
      Real onset `PICTURE / IMMEDIATE` (97963.935270) from the broken session after
      reconstruction: **750 samples × 8 ch, Complete, monotonic, 0 gaps, 1.262 ms from t=0.**
    - **Recorded sessions from before this fix are RECOVERABLE** — `local_raw` was always correct.
      Use `IKEA_EEG → EEG → Validate Recognition Event Window` with **Reconstruct analysis from
      local_raw** ticked.
    - Self-test: **2286 assertions, 0 failures** (`BLOCK 11`, 47 assertions).
    - **[IMPLEMENTED, EDITOR-TESTED against two real recordings] — live AURA retest PENDING.**
      Signal quality is NOT addressed; one channel in the new session appears flat/saturated and
      is untouched.

-5. **Recognition progress counter + developer QA cheatsheet (2026-09-01, second pass).**
    - **Recognition progress counter implemented.** Participant-facing "Item X / N", shown with
      the stimulus word and before the buttons arm, in both Immediate (Area A) and Delayed
      (Area C). Two separate labels — `Txt_RecognitionCounter` on `UI_A_Canvas`,
      `Txt_RecognitionCounter_C` on `UI_C_Canvas` — same per-area pattern as the stimulus word,
      at the same world height (1.8115 m) in both areas.
    - **Source is the actual phase sequence length, NOT hard-coded.** `RunRecognitionPhase` passes
      `i + 1` and `items.Count` into `RunRecognitionItem`; `items` is what
      `RecognitionSequence.Build` produced for that phase, so a change to the delayed composition
      is followed automatically. `RecognitionItem.presentationOrder` keeps its 0-based meaning.
    - **Developer cheatsheet implemented.** Shows `DEVELOPER QA / TARGET|LURE / Correct: SEEN
      BEFORE|NOT SEEN BEFORE`, read from `item.itemClass`. Magenta, on its own labels
      (`Txt_DevCheatsheet_A` / `_C`), authored INACTIVE.
    - **Config gate: `ExperimentConfig.enableRecognitionDeveloperCheatsheet`, default `false`.**
      With the gate closed the toggle does nothing and the labels are never populated.
    - **Toggle: RIGHT controller B button (`secondaryButton`)**, rising-edge, press-to-toggle (no
      hold). Chosen because B is bound to nothing anywhere in the project; right A carries the
      XRI asset's `Jump`, and triggers/grip are Select, thumbstick clicks are developer
      navigation. The action is created in code — the shared XRI asset is untouched.
    - **Resets OFF at every phase boundary** (documented choice — the safer option), and on
      results, abort and `ResetUI`. Only toggleable while a recognition item is on screen; the
      two recognition states gate it, so FreeRecall can never show it.
    - **The overlay does not affect scoring or logging.** Asserted: the toggle path contains no
      `m_PendingRecognitionResponse`, `OnRecognitionResponse`, `responseSelected`,
      `m_RecognitionPanel`, `PlayCue`, `Log(`, `item.response`, `reactionTimeMs`, `Arm()` or
      `Disarm()`; the text builder is `static` and assigns no `RecognitionItem` field. No new
      event type or marker was added.
    - Scene rebuilt (4 new labels); `VALIDATION PASSED`. Self-test: **2239 assertions, 0
      failures** (new `BLOCK 10`, 103 assertions).
    - New key `RecognitionItemProgress` is **ENGLISH-ONLY** per the Recognition localization
      policy; the cheatsheet is an English literal and deliberately not localized. **ES/JA PENDING.**
    - **[IMPLEMENTED, EDITOR-TESTED] — Quest validation PENDING.**

-4. **Typed Recognition aggregates + protocol-aware Results screen (2026-09-01).**
    - **Typed Recognition phase aggregates added.** New `RecognitionPhaseResults`
      (`Scripts/Experiment/RecognitionItem.cs`): `hits`, `misses`, `correctRejections`,
      `falseAlarms`, `noResponse`, `itemCount`, `targetCount`, `lureCount`, plus derived
      `totalCorrect`, `totalIncorrect`, `answeredCount`, `hasData`. **Raw counts only — no
      composite, no d′, no CNS score.** `RecognitionPhaseResults.Recount` is now the SINGLE
      counting implementation; `RecognitionSequence.Summarise` is a thin wrapper over it and its
      event-notes string is byte-identical to before.
    - **`SessionResults` now carries Immediate + Delayed Recognition results** as two separate
      `RecognitionPhaseResults` instances, never merged, plus a typed `protocolMode`. Populated by
      `ExperimentManager.CaptureRecognitionResults()` from `m_ImmediateRecognitionItems` /
      `m_DelayedRecognitionItems` — the notes string is never parsed back. Called before
      `FinishAreaC` builds the panel, and again on publish. `PrepareTrial` clears both item stores
      so an aborted run cannot leak into the next.
    - **Results UI is protocol-aware.** Recognition mode shows two separate Recognition sections
      and no longer shows the legacy `StatImmediateRecall` / `StatDelayedRecall` rows
      ("Immediate/Delayed verbal recall: Not available"). FreeRecall keeps every row it had,
      byte-identical (measured: 811/811/909 px, unchanged). Nothing is labelled a score;
      "Correct responses" is over `answeredCount` with the denominator named in the string, and
      NoResponse is still never counted as an error.
    - **Recognition results are VISUAL-ONLY — no narration added.** Asserted: `SessionResults`
      contains no `Speak` / `AudioCue` / `PlayNarration` / `GetWordClip` / `AudioSource` of any kind.
    - **FreeRecall preserved.** Recall rows, `RecordingInfo`, `RecallScorer` and the microphone
      architecture are untouched — routed, not deleted.
    - Derived `session_summary_<session_id>.csv` gained **17 appended columns** (`protocol_mode`
      + 8 per phase, raw counts). **Event CSV schema, LSL marker schema and EEG are UNCHANGED.**
    - New Recognition localization keys are **ENGLISH-ONLY** (ES/JA slots hold English), matching
      the existing Recognition localization policy. **ES/JA PENDING.**
    - Self-test: **2136 assertions, 0 failures** (new `BLOCK 9`, 117 assertions).
    - **[IMPLEMENTED, EDITOR-TESTED] — Quest validation still PENDING.** See the dated addendum in
      `Documentation\RECOGNITION_RESULTS_MARKERS_OUTPUT_AUDIT_2026-08-31.md`.

-3. **Delayed Recognition word display + results/marker audit (2026-08-31).**
    - **Delayed Recognition word-display fix implemented and editor-tested.** `SetWordDisplay`
      wrote only to `m_AreaAWord`, which lives on the Area A canvas — inactive during Area C — so
      the delayed stimulus was set on an invisible object. Added `m_AreaCWord`
      (`Txt_WordDisplay_C` on `UI_C_Canvas`, built by `ExperimentSceneBuilder`); `SetWordDisplay` /
      `ClearWordDisplay` now write both labels, matching the existing four-label `SetWarning`
      idiom. Self-test **2019 assertions, 0 failures** (new `BLOCK 8` section, 36 assertions).
      **[IMPLEMENTED, EDITOR-TESTED] — NOT Quest-tested.** Delayed data collected before this fix
      should be treated as invalid.
    - Recognition per-item data is stored in `m_ImmediateRecognitionItems` and
      `m_DelayedRecognitionItems` (`ExperimentManager`).
    - Full Recognition behavioural data **is** written to `events_<session_id>.csv` — all 10
      recognition columns per item. **[VERIFIED LIVE]**
    - `SessionResults` currently has **no typed Recognition aggregate**. Hit/Miss/CR/FA totals
      exist only as an ephemeral tally string in the `*_RECOGNITION_END` event's `notes`.
    - Final Results UI still shows **legacy FreeRecall fields** in Recognition mode
      ("Immediate / Delayed verbal recall: Not available").
    - Recognition **LSL markers lack phase / class / outcome** — payload carries only `word` and
      `rt_ms`, so classifying an epoch requires joining the CSV on `lsl_timestamp`.
    - Full audit: `Documentation\RECOGNITION_RESULTS_MARKERS_OUTPUT_AUDIT_2026-08-31.md`

-2. **Recognition-memory protocol, Version 8 (2026-08-25).** Added a CNS-DERIVED (never
   CNS-equivalent) recognition task alongside the existing free-recall task, selected by
   `ExperimentConfig.protocolMode` (`VerbalProtocolMode.FreeRecall` / `Recognition`, default
   Recognition). 15 VISUAL encoding words at 2 s each with a transition beep, never spoken;
   immediate and delayed SEEN BEFORE / NOT SEEN BEFORE recognition using
   `XRSimpleInteractable` far-ray selection (no new package). Four-way Hit / Miss / Correct
   Rejection / False Alarm classification, derived not stored. CSV **35 -> 45 columns**, all
   appended. New states `ImmediateRecognition` / `DelayedRecognition` appended so existing
   numbering is untouched. Stimulus words are **DEVELOPMENT PLACEHOLDERS, NOT CNS STIMULI**.
   Self-test **1617 -> 1679 assertions, 0 failures**. See
   `DECISION_RECORD_RECOGNITION_PROTOCOL.md`. The FreeRecall path is unchanged.

-1. **Channel integrity + robust quality + optional notch (2026-08-24, second pass).**
   Created `Editor/EegChannelIntegrityDiagnostics.cs`
   (`IKEA_EEG → EEG → Diagnose Channel Integrity`). Added `ChannelPowerOutlier` and
   `TransientArtifactSuspected` flags, `AssessRoiContamination`, `LatestEegFeatures.Explain()`,
   and `EegFeaturePipeline.Median` / `AnalyzeWindowForDiagnostics`. Added the optional notch to
   `EegBandpassFilter` and `EegFeaturePipeline`. Three new self-test sections. **1584 → 1617
   assertions, 0 failures.** Refuted the settling hypothesis by measurement (§10); root cause of
   the F3 outlier remains **unproven** and needs a live capture.

0. **Identical-channel investigation + identity flag (2026-08-24).** AURA was offline all session,
   so the extraction was cleared against a synthetic stream instead (§10). Created
   `Editor/ProbeAuraChannels.ps1` and `Editor/SyntheticEegOutlet.ps1`. Added
   `EegQualityFlags.IdenticalChannels` (appended, `1 << 9`) with `AssessInterChannelIdentity`
   called from **both** analysis paths in `EegFeaturePipeline`, plus `identicalChannelPairs` /
   `identicalChannelDetail` on `LatestEegFeatures`; surfaced in `EegSpectralDiagnostics` and
   `EegResearcherMonitor`. Added `EegFeaturePipeline` to `ExperimentSceneBuilder` and rebuilt the
   scene. New self-test section `EEG CHANNEL SEPARATION + IDENTITY FLAG` and four new scene
   assertions in `AURA RAW EEG RECEIVER`. **1560 → 1584 assertions, 0 failures.**
   No previously working behaviour was modified — every change is additive.

1. **Live feature pipeline + monitor** — created `EegFeaturePipeline.cs`, `EegSpectralDiagnostics.cs`,
   `EegResearcherMonitor.cs`. Added `EnsureSubscribed()` because `OnEnable` doesn't fire for
   edit-mode `AddComponent` (first diagnostic run processed 0 of 2133 samples).
2. **Spectral core** — created `EegBandpassFilter.cs`, `EegSpectralAnalyzer.cs` + 2 self-test sections.
3. **Montage** — created `AuraMontageConfig.cs` + self-test section.
4. **§0 blocker investigation** — pulled full StreamInfo XML, found empty `<desc/>` on all three
   AURA streams. No files changed.
5. **Analysis timebase fix** — rewrote the emit model in `EegAnalysisTimebase.cs` from
   "evaluate a refitted line" to "incremental steered grid" (the refit made consecutive outputs
   come from different lines → 3 backward steps).
6. **Clock correction** — `LslBinding.TryGetTimeCorrection`, receiver applies `local = remote +
   correction`; `RawEegRecorder` columns renamed to explicit `_analysis/_local_raw/_remote_raw`.
7. **Word stimuli (ES/JA)** — `WordSourcePools.cs`, provenance fields on `WordSet`, per-run set
   advancement, per-language TTS voices, Unicode-safe filenames.
8. **Raw EEG infrastructure** — ring buffer, recorder, `lsl_timestamp` CSV column (35th).

---

## 12. Where We Stopped

*(Updated 2026-08-24.)*

### AURA was OFFLINE for this entire session
`resolve_all` returned **zero streams** on the network, twice, at the start and end of the pass.
liblsl itself was healthy (1.17 / protocol 1.10, `lsl_api.cfg` loaded), Computer B answered ping
in **3 ms**, and this machine held its documented 192.168.10.123. So the network is not the
problem — **the AURA application was simply not publishing.** No live measurement was taken and
**no live result is reported.**

### Immediate task — resolved as far as it can be without hardware
**Why all 8 EEG channels returned bit-identical spectral values.** Because AURA never appeared,
the question was attacked from the other end: instead of asking what the amplifier sent, prove
what our code does with a **known** input. See §10 — cause 2 (our extraction) is **eliminated**
with quantitative evidence; causes 1 and 3 remain open and need live hardware.

### Completed this session
1. **`Editor/ProbeAuraChannels.ps1`** — out-of-process channel-distinctness probe. Opens its own
   inlet through the project's own `lsl.dll`, pulls samples, reports the distinct-values-per-sample
   histogram, per-channel mean/sd/min/max, and every bit-identical channel pair. **No Unity code
   in the path.** Verdict logic verified in **both** directions against synthetic streams.
2. **`Editor/SyntheticEegOutlet.ps1`** — known-good/known-bad LSL source. Healthy mode gives every
   channel a distinct frequency and amplitude; `-IdenticalChannels` and `-OffsetOnlyChannels`
   reproduce the two failure modes. Paces in catch-up bursts because Windows' ~16 ms sleep
   granularity would otherwise cap the stream near 56 Hz — measured **249.7 Hz** effective.
3. **Extraction proven correct** end-to-end through Unity's real path (§10).
4. **`EegQualityFlags.IdenticalChannels`** implemented, in both analysis paths, surfaced in the
   spectral diagnostic and the Researcher Monitor (§6).
5. **`EegFeaturePipeline` wired into `ExperimentSceneBuilder`**, scene rebuilt and verified.
6. **Self-test 1560 → 1584 assertions, 0 failures.** +24, nothing removed, nothing unrelated
   changed.

### Still to be done, in order
1. **With AURA streaming**, run
   `powershell -ExecutionPolicy Bypass -File Assets\IKEA_EEG\Editor\ProbeAuraChannels.ps1`.
   Since our extraction is already cleared, the probe's verdict decides it outright:
   - identical at source → **acquisition problem**; check electrodes, impedance,
     reference/ground and any AURA test-signal mode.
   - distinct at source → **common-mode** (cause 3) is the remaining explanation; investigate
     the reference/ground configuration before trusting absolute power.
2. **Open the Researcher Monitor** (`IKEA_EEG → EEG → Researcher EEG Monitor`) and verify it
   displays live data. Its structural blocker is fixed — a pipeline now exists in the scene for
   it to find — but **interactive display behaviour is still unverified**: it needs a human at
   the Editor GUI and cannot be checked in batch mode.
3. Run one full VR session with EEG connected; confirm `raw_eeg.csv` appears per run.
4. Re-validate the analysis timebase live after its final fix.

### Constraints already decided — do not revisit without asking
- `analysisTimestamp` is authoritative for analysis; raw remote/local are preserved.
- No liblsl post-processing flags (`proc_dejitter`/`proc_monotonize`) — we do our own so raw
  timestamps survive.
- No 60 Hz notch (LP 40 Hz already gives −18.41 dB).
- Units stay "AURA native units" until scaling is independently confirmed.
- ROI resolution is all-or-nothing, by label.
- No neuroadaptation, no ICA, no artefact repair, no ERP.

---

## 13. Recommended Next Steps

### P0 — Needs hardware, do it the moment AURA is TRANSMITTING
> AURA resolving is not the same as AURA transmitting. On 2026-08-24 it advertised all three
> streams while sending zero samples, confirmed out-of-process. Always check with
> `ProbeAuraChannels.ps1` first: it exits 5 and says so explicitly.

1. Run `IKEA_EEG → EEG → Diagnose Channel Integrity` **while the anomaly is present**. It reports
   every stage of the same window per channel and prints `theta/RMS²`, which must be ≤ ~1; a
   value far above 1 would be a genuine channel-association fault, and anything else points at
   the electrodes. Settling and de-interleave are both already eliminated (§10).
2. Run `ProbeAuraChannels.ps1` at the same time for the out-of-process ground truth.

### P1 — Important next
2. Verify the Researcher Monitor interactively (§12 step 2). Requires a human at the Editor GUI.
3. Run one full VR session with EEG connected; confirm `raw_eeg.csv` appears per run with
   plausible sample counts and that `lsl_timestamp` in `events.csv` falls inside its range.
4. Re-validate the analysis timebase live after its final fix.

### P2 — Later / validation
5. Confirm amplitude scaling with AURA source/docs; only then set `unitsConfirmed = true`.
6. Baseline capture wired to an experiment marker (infrastructure exists, no caller).
7. Document reference/ground configuration before interpreting absolute power — this is also
   where cause 3 (common-mode) of the identical-channel question would be settled.
8. Quest validation of ES/JA sessions end to end.
9. Consider a montage checksum/verification prompt at session start.

---

## 14. Important Constraints / Do Not Break

**Principle: do not break verified working functionality while extending the system.**

- **Raw EEG preservation** — amplitudes and all three timestamps kept; filtered signal lives in a
  separate buffer. There must always be a path back to the original samples.
- **`analysisTimestamp` is the analysis clock.** Do not switch buffering/windowing to raw.
- **CSV append-only.** 35 columns; never reorder or redefine an existing one.
- **Marker payload format is frozen** (`EVENT_TYPE|key=value|...`).
- **Reflection-only LSL.** Never add `using LSL;` to experiment code — the project must compile
  and run without liblsl.
- **One AURA inlet.** Never create a second receiver/inlet/raw buffer for the same stream.
- **Researcher Monitor is read-only.** It must never start/stop anything or mutate state.
- **No µV claims** while `unitsConfirmed == false`.
- **No RAVLT-validity claims** for the 5-word subsets.
- **Do not touch** without explicit instruction: XR/OpenXR, ProjectSettings, Packages, shared
  Input Actions, XR Origin/controller prefabs, experiment flow, Areas 0/A/B/C, localization
  architecture, word lists, chair randomization, RT semantics, recording behaviour, NEW TRIAL /
  RESTART / END semantics, run-duration semantics.
- **Self-test is the gate.** `IKEA_EEG/Run Self Test` must stay at 0 failures. If a source-scanning
  assertion fails, check first whether it is matching a *comment* — that has happened repeatedly.

### How to run things
```bash
# Scene build / self test / diagnostics (batch mode; Editor must be closed)
Start-Process "C:\Program Files\Unity\Hub\Editor\6000.3.21f1\Editor\Unity.exe" -ArgumentList `
  '-batchmode','-quit','-projectPath','"C:\Users\Mariana\Unity Projects\IKEA_EEG"', `
  '-executeMethod','IkeaEeg.EditorTools.ExperimentSelfTest.RunFromCommandLine', `
  '-logFile','"<path>.log"' -Wait
```
Useful `-executeMethod` targets: `ExperimentSceneBuilder.BuildFromCommandLine`,
`ExperimentSelfTest.RunFromCommandLine`, `ResearcherTools.RunLoopbackTest`,
`ResearcherTools.CheckAuraStream`, `LslNetworkDiagnostics.DiagnoseFromCommandLine`,
`EegDiagnostics.CheckBufferFromCommandLine`, `EegDiagnostics.TestEventWindowFromCommandLine`,
`EegDiagnostics.DiagnoseContinuityFromCommandLine`,
`EegSpectralDiagnostics.AnalyzeFromCommandLine`.

---

## 15. Instructions for the Next Claude Session

1. **Read this entire file first.**
2. **Inspect the actual files before changing anything.** This document is a map, not the terrain.
3. **The code is the source of truth.** Where it conflicts with this document, trust the code and
   correct the document.
4. **There is no git repository.** You cannot diff or revert — be correspondingly careful, and
   consider proposing `git init` before large changes.
5. **Preserve verified working behaviour** (§9, §14). Run the self-test before and after any
   change to shared code.
6. **Continue from §12 "Where We Stopped"** — the identical-channel investigation is half closed:
   the code is cleared, the remaining half needs live AURA and one command (§13 P0).
7. **Ask only when a genuine research/design ambiguity cannot be resolved from the project** —
   e.g. amplitude scaling, montage re-verification. Routine engineering calls are yours to make.

### First prompt to paste into the new session

```
Continue working on the existing Unity project IKEA_EEG at
C:\Users\Mariana\Unity Projects\IKEA_EEG

Read CLAUDE_HANDOFF.md in the project root completely before doing anything.
It documents the full current state, what is verified vs unverified, and where
we stopped.

BYPASS PERMISSION IS ENABLED. Previously working functionality is LOCKED —
do not modify it unless strictly necessary, and if you must, explain why, make
the smallest possible change, regression-test it and report it.

Continue from the "Where We Stopped" section (§12).

Context: the identical-channel investigation is HALF closed. Our extraction has
been proven correct against a synthetic 8-channel stream, and an
IdenticalChannels quality flag now exists and is verified end to end. What
remains needs live hardware.

First, check whether AURA is transmitting:
  powershell -ExecutionPolicy Bypass -File Assets\IKEA_EEG\Editor\ProbeAuraChannels.ps1

If it IS transmitting, that one command decides the remaining question — report
its verdict and act on it (acquisition fix vs. common-mode investigation).

If AURA is NOT transmitting, say so plainly and do not fabricate live results.
Continue with the offline P1 work instead: Researcher Monitor verification,
raw_eeg.csv from a real run, and live re-validation of the analysis timebase.

Verify with IKEA_EEG/Run Self Test — it must stay at 0 failures
(currently 1679 assertions, 0 failures).
```

---

*End of handoff.*
