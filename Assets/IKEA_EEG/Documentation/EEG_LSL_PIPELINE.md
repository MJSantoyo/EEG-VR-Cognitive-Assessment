# IKEA_EEG — marker transport and the future EEG pipeline

This document covers what the project does with experimental markers **today**, what has to be
installed for markers to actually be transmitted, and where AURA/EEG connects **later**.

It deliberately does not describe any EEG analysis. No theta, no alpha, no cognitive-load
estimation, no EEG-driven difficulty. Those are later milestones and nothing in this codebase
implements or pretends to implement them.

---

## 1. The pipeline

```
Meta Quest / Unity experiment
        |
        v
Experiment EventBus                     (one event in, N sinks out, synchronous)
        |
        +--> CsvEventSink               events_<session_id>.csv        [authoritative record]
        +--> UnityConsoleEventSink      live view for the operator
        +--> LslMarkerSink  ------------+
                                        |
                                        v
                                  LSL ecosystem
                                        |
                          +-------------+-------------+
                          |                           |
                          v                           v
                    AURA EEG stream            LabRecorder / viewer
                          |
                          v
             synchronised recording (.xdf)
```

One experimental event produces **one CSV row and one LSL marker**, from the same
`ExperimentEvent`, in the same frame. The marker string always begins with the same
`EventTypes` constant that appears in the CSV's `event_type` column, so the two records join on
identical labels.

---

## 2. Stream metadata

| Property | Value |
|---|---|
| Name | `IKEA_EEG_Markers` |
| Type | `Markers` |
| Channel count | `1` |
| Nominal sampling rate | `0` (irregular) |
| Channel format | `string` (`cf_string`) |
| Source ID | `IKEA_EEG_Unity_Markers` |

Configured on the `LslMarkerSink` component (on the `EventLogger` GameObject) and set by the
scene builder.

---

## 3. Marker format

```
EVENT_TYPE[|key=value]...
```

The **first token is always the unmodified event name**. The optional tail carries only the few
identifiers that make a marker self-describing. Free-text notes are never transmitted — they
belong in the CSV. Markers are capped at 256 characters.

Examples:

```
SESSION_START|seed=1723456789
WORD_PRESENTED|word_index=2|word=Copper
IMMEDIATE_RECALL_BEEP|phase=IMMEDIATE
CHAIR_TRIAL_START|trial=2|diff=MEDIUM
CHAIR_SELECTED|trial=2|chair=Chair_04|correct=1
CHAIR_CORRECT|trial=2|chair=Chair_04|correct=1|rt_ms=1843.2
AREA_C_ENTER
```

Recognised tail keys: `trial`, `diff`, `word_index`, `word`, `phase`, `chair`, `correct` (1/0),
`rt_ms`, `seed`. `|` and `=` inside a value are replaced with `_`.

The tail can be switched off entirely (`Include Compact Payload`), which reduces every marker to
the bare event name.

---

## 4. Current status: is LSL actually transmitting?

**As shipped, no.** This project contains **no LSL library**, because none was present and none
was downloaded. The sink is real, not a placeholder — it binds to liblsl at run time and starts
transmitting the moment the library is installed — but with no library present it reports:

```
LSL_UNAVAILABLE — no markers will be transmitted this session.
```

This is logged to the Console **and** recorded in the CSV as an `LSL_STATUS` event with
`lsl_state=UNAVAILABLE`. Nothing anywhere claims a marker was sent when it was not:
`markersPushed` is only incremented when liblsl accepts a sample.

An unavailable marker stream never affects the behavioural session. CSV and WAV data are
written exactly as before.

### Checking the current state

* Menu: **IKEA_EEG ▸ LSL ▸ Check LSL Availability**
* Menu: **IKEA_EEG ▸ LSL ▸ Run Marker Loopback Test**
* Console at session start: either the `LSL OUTLET CREATED` banner or the `LSL_UNAVAILABLE`
  warning.
* Data: the `LSL_STATUS` row of `events_<session_id>.csv`.

---

## 5. Installing LSL (what has to happen before markers transmit)

The sink binds by **reflection**, so no recompilation, no code edit and no scene rebuild is
needed. It looks for a type named `StreamOutlet` with a `push_sample(string[])` method, plus
`StreamInfo` and the `channel_format_t` enum, in any loaded assembly. Both API layouts that have
shipped are supported:

* `LSL.StreamInfo` / `LSL.StreamOutlet` — liblsl-Csharp 1.13+ and current LSL4Unity
* `LSL.liblsl.StreamInfo` / `LSL.liblsl.StreamOutlet` — older LSL4Unity bundles

### Recommended dependency

**LSL4Unity** — the Unity wrapper maintained by the LSL organisation. It contains the C# binding
*and* the native `liblsl` binaries, and handles the plugin import settings per platform.

* Source: `https://github.com/labstreaminglayer/LSL4Unity`
* Install via Package Manager ▸ **Add package from git URL**:
  `https://github.com/labstreaminglayer/LSL4Unity.git`

Alternative, if you prefer to vendor the pieces yourself: `liblsl-Csharp` (the managed binding)
plus a matching native `liblsl` binary from
`https://github.com/sccn/liblsl/releases`, dropped into `Assets/Plugins/`.

### Before installing — inspect, do not trust

1. Confirm the repository is under the `labstreaminglayer` / `sccn` organisation and that the
   release you take is the published one, not a fork.
2. Check the native binaries in the package against the checksums on the `sccn/liblsl` release
   page.
3. Note the version you installed in the study record. `liblsl` is what timestamps the markers;
   which build produced a recording is part of that recording's provenance.

### Platform notes

| Target | Native binary | Notes |
|---|---|---|
| Windows (Quest **Link**) | `liblsl64.dll` / `lsl.dll` | The experiment runs on the PC, so this is the only binary needed for Link sessions. |
| Android (standalone Quest build) | `liblsl.so` (arm64-v8a) | Only needed if the experiment is ever built to run **on** the headset. Standalone builds also need network permission and both machines on the same subnet for LSL discovery. |

Set the plugin's platform settings so the desktop binary is **not** included in an Android build
and vice versa.

### After installing

1. Reopen the project (or let Unity reload the domain).
2. Run **IKEA_EEG ▸ LSL ▸ Check LSL Availability** — it must report `available: True`.
3. Run **IKEA_EEG ▸ LSL ▸ Run Marker Loopback Test** — it must report `PASS`.
4. Enter Play Mode. The Console must show the `LSL OUTLET CREATED` banner, and the CSV's
   `LSL_STATUS` row must read `lsl_state=ACTIVE`.

No other change is required. The scene, the config assets and the experiment code stay as they
are.

---

## 6. Testing markers WITHOUT any EEG hardware

Three levels, in increasing realism:

1. **In-process loopback** — `IKEA_EEG ▸ LSL ▸ Run Marker Loopback Test`. Creates a real outlet,
   resolves it with a real inlet, pushes a marker and reads it back. Proves the transport works
   end to end with no hardware at all. Skipped, with an explicit message, when liblsl is absent.

2. **External viewer** — run LabRecorder (or any LSL viewer) on the same machine, start the
   experiment, and confirm a stream named `IKEA_EEG_Markers` appears and that markers arrive as
   the participant progresses.

3. **Recorded file** — record with LabRecorder to an `.xdf` and check offline that the marker
   sequence matches `events_<session_id>.csv` row for row. The event names are identical in both
   files, so this comparison is exact.

None of this validates EEG synchronisation. See section 8.

---

## 7. Where AURA connects later

AURA publishes its EEG signal to LSL as its own stream. Nothing in Unity needs to consume it.

```
Unity  ->  IKEA_EEG_Markers  (this project)   \
                                              >--  LabRecorder  ->  .xdf
AURA   ->  EEG signal stream                  /
```

The recorder subscribes to both streams and writes them into one file with a common time base;
alignment happens in the recording layer and in offline analysis, not in Unity. That is why this
project sends markers and nothing else: Unity has no business acquiring, buffering or processing
EEG.

Explicitly **not** implemented, and not to be inferred from anything here: EEG acquisition in
Unity, AURA SDK signal processing, theta or alpha computation, cognitive-load estimation, and
EEG-driven adaptive difficulty.

---

## 8. What is still unproven

| Claim | Status |
|---|---|
| Markers are formatted correctly and the sink degrades safely | **Automatically tested** (self test) |
| A real outlet transmits and can be received | **Testable locally once liblsl is installed** (loopback test). Not yet run — no library present. |
| Markers appear in LabRecorder | **Requires a manual run** with LabRecorder |
| Markers align with the AURA EEG stream | **Requires the real recording pipeline.** Not validated. Do not claim EEG synchronisation until markers and EEG have been recorded together and checked against the CSV. |
| End-to-end latency between a Unity event and its marker's LSL timestamp | **Not characterised.** Needs measurement with a hardware trigger or a photodiode before any latency-sensitive analysis. |
