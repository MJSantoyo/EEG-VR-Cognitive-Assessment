# Researcher Dashboard — architecture assessments

Two forward-looking questions were assessed on 2026-09-17. **Neither is implemented against
real data.** Both would touch a running participant session, so the decision is the
researcher's, not the tool's.

---

## A. Live EEG waveform monitor

**Goal.** Show the most recent 5–10 s of the 8 AURA channels, for visual monitoring only.

**Hard constraint.** The scientific path — `AuraLslReceiver` → `RawEegRingBuffer` →
`EegFeaturePipeline` — must be untouched. No display feature may become a source for
feature computation, and nothing may sit between acquisition and the ring buffer.

### Options compared

| | **A. Status writer publishes a decimated snapshot** | **B. Python tails the recorded `raw_eeg.csv`** | **C. Separate LSL consumer in Python** |
|---|---|---|---|
| **How** | `ResearcherStatusWriter` reads the existing ring buffer via `TryGetWindow`, decimates to ~50 Hz, writes `eeg_window.json` beside `status.json` | Server seeks to the end of the session's `raw_eeg.csv` and parses new lines | A second, independent LSL inlet subscribed to the same `"AURA"` stream, outside Unity |
| **Unity cost per tick** | One `TryGetWindow` on a buffer that already exists + ~400 floats serialised. Sub-millisecond, on the same `Update` already running at 4 Hz | **Zero** | **Zero** |
| **Latency** | ~250 ms (one writer tick) | 1–3 s: `RawEegRecorder` buffers, and the OS flushes when it flushes | ~50 ms |
| **Freshness during recording** | Excellent | Poor and variable — you see what has been flushed, not what is being acquired | Excellent |
| **File contention** | None new. Same atomic temp-and-move already used for `status.json`; the reader never sees a partial file | **Real.** A reader holding the file while `RawEegRecorder` appends is exactly the kind of contention that can surface as a write error mid-session | None |
| **Works with no session recording** | Yes — the ring buffer exists whenever the stream does | **No.** No session, no file, no trace | Yes |
| **Touches acquisition timing** | No — reads an existing buffer, never the inlet | No | **Possibly.** A second consumer changes LSL's delivery pattern, and could contend for the same device stream |
| **Complexity** | Low. ~60 lines in a file that already exists | Medium. Tail state, partial lines, rotation, encoding | High. liblsl bindings in Python, a second clock domain, its own failure modes |
| **Risk to the science** | Lowest | Medium (contention) | **Highest — rejected** |

### Recommendation: **A**, with conditions

Option A reuses the mechanism already proven safe: Unity writes outward, the dashboard reads.
It adds no socket, no second consumer, no new clock, and no contention with the recorder.
Decimation to ~50 Hz is a *display* decision and must be labelled as one — the analysis path
keeps reading the full-rate buffer, as it does today.

**Option C is rejected outright.** A second LSL inlet on the same stream can change delivery
timing for the inlet that matters. That is precisely the class of risk this project's
architecture exists to avoid.

**Option B remains useful for offline review** — reading a finished `raw_eeg.csv` after a
session has ended is safe and is the right basis for a future waveform review of recorded
data. It is a poor basis for a *live* monitor.

**Conditions before A is wired to real data, all researcher decisions:**

1. Confirm the display decimation (simple stride vs. averaging) and that it is labelled as
   display-only wherever the trace appears — a decimated trace aliases, and must never be read
   as the analysed signal.
2. Confirm that reading the ring buffer at 4 Hz from the writer is acceptable. It is a read of
   an existing structure, but it happens on the main thread.
3. Decide whether the panel shows *raw* or *filtered* samples. Raw is honest and cheap;
   filtered would mean either re-filtering for display (a second filter path — avoid) or
   exposing `m_Filtered`, which is analysis state.

**Implemented tonight:** the panel, the incremental wire protocol (`/api/eeg?since=N`) and the
renderer, driven by **synthetic data only**. The live endpoint returns
`available: false` with a reason rather than inventing a trace.

**Measured cost of the wire protocol** (mock): full 10 s window 31.7 KB; incremental poll at
2.5 Hz **≈4 KB**, roughly 10 KB/s on loopback.

---

## B. VR participant view

**Goal.** Let the researcher see what the participant is seeing.

**Hard constraint.** No control path from browser to experiment; no meaningful cost to VR
frame timing; dashboard failure cannot affect the session.

### Options compared

| | **1. Periodic Editor screenshot to disk** | **2. Spectator camera → `RenderTexture` → periodic JPEG** | **3. Local MJPEG stream from Unity** | **4. OS-level window capture, outside Unity** |
|---|---|---|---|---|
| **How** | `ScreenCapture.CaptureScreenshot` every 1–2 s, editor-only, served as a still | A second low-res camera renders to a `RenderTexture`, read back and encoded at 1–2 Hz | Unity opens an HTTP endpoint and pushes frames | A separate process captures the Unity Game window; Unity untouched |
| **VR frame-timing cost** | **High and spiky.** A full-resolution capture stalls the render thread — visible as a hitch in the headset | Moderate and controllable: `ReadPixels` forces a GPU sync. At 1 Hz and 320×240, small but **not free** | Moderate, plus a server in the participant process | **Zero inside Unity** |
| **Control path** | None | None | **Unity becomes a server** — the exact inversion this architecture avoids | None |
| **Failure isolation** | Good | Good | **Poor** — a stuck client is now Unity's problem | Excellent |
| **Complexity** | Low | Medium | High | Medium (platform-specific) |
| **Shows the true HMD view** | Editor game view, not the headset | Whatever the spectator camera is aimed at — a deliberate choice, arguably better | Same as 2 | Whatever the Editor shows |

### Recommendation: **2, at 1 Hz and low resolution — or 4 if any frame-timing cost is unacceptable**

A dedicated low-resolution spectator camera at **1 Hz** is the best balance: it is a
deliberate researcher-facing view rather than an accident of what the Editor is rendering, it
keeps Unity writing outward, and its cost is bounded and measurable. It must be editor-only,
like the status writer, so a Quest build never pays for it.

**Option 3 is rejected**: it makes the participant process a server, which is the one thing
this architecture is built to prevent.

**Option 4 is the fallback** if measurement shows *any* unacceptable hitch: it has literally
zero cost inside Unity, at the price of being platform-specific and capturing the Editor
window rather than a chosen view.

**Not implemented.** The dashboard shows a placeholder card stating plainly that real VR
streaming is not implemented. Before building it, someone must measure the actual frame-time
cost of a `ReadPixels` at the intended resolution during a real VR session — a stutter in the
headset during encoding would be a participant-facing regression, which is out of bounds for
an observational tool.
