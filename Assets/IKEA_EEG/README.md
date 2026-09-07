# IKEA_EEG — VR Cognitive Assessment Prototype

Unity 6000.3.21f1 · URP 17.3 · XR Interaction Toolkit 3.4.1 · OpenXR 1.16.1
Target: **Meta Quest over Quest Link, tested by pressing Play in the Unity Editor.**

---

## 1. Running the experiment

1. Connect the Quest with the Link cable and start Quest Link.
2. Open `Assets/IKEA_EEG/Scenes/IKEA_EEG_Experiment.unity`.
3. Press **Play**.
4. You start in **Area A**. Point at **START** with either controller and pull the trigger.

The trial then runs itself:

| Phase | What happens | Advanced by |
|---|---|---|
| Area A instructions | Task instructions shown | timer (8 s) |
| Word encoding | Five words **spoken aloud**, one at a time. Screen shows only "Listen carefully." — the words are never displayed | timer |
| Immediate recall | Beep, then microphone records | timer (20 s) |
| — | **ENTER SHOWROOM** button appears | **you** |
| Area B instruction | "Select the LARGE, BLUE, MODERN chair." | timer (5 s) |
| Area B selection | Response timer running, chairs selectable | **you** (select a chair) |
| — | **EXIT SHOWROOM** button appears | **you** |
| Area C instruction | Delayed recall instruction | timer (7 s) |
| Delayed recall | Beep, then microphone records | timer (20 s) |
| Results | Total trial duration + summary, **RESTART** / **END** | **you** |

Minimum trial duration with no participant hesitation: **~68 s**.

---

## 2. Where the data goes

```
C:\Users\<you>\AppData\LocalLow\DefaultCompany\IKEA_EEG\IKEA_EEG_Data\<session_id>\
    events_<session_id>.csv
    audio\T001_immediate_recall.wav
    audio\T001_delayed_recall.wav
```

This is `Application.persistentDataPath`. The absolute path is printed to the Unity Console
at `SESSION_START`, so you never have to guess it.

One CSV per **session**. Pressing RESTART keeps the same session and CSV and starts a new
`trial_id` (`T001`, `T002`, …). Pressing END closes the CSV.

### CSV columns

`timestamp_absolute, timestamp_relative, session_id, trial_id, experiment_state, room,
event_type, object_id, target_color, target_size, target_shape, selected_color, selected_size,
selected_shape, correct, response_time_ms, word_index, expected_word, recall_phase, transcript,
elapsed_trial_time, notes`

* `timestamp_relative` — seconds since `SESSION_START`, from a monotonic `Stopwatch`
  (**not** `Time.time`, **not** `DateTime.Now`). This is the column to align EEG against.
* `elapsed_trial_time` — seconds since the current `TRIAL_START`.
* An **empty cell means the column does not apply to that event type**.
* Free text is RFC-4180 escaped, so commas/quotes/newlines in `notes` or `transcript` cannot
  corrupt the file.
* The stream is flushed after every row — stopping Play mid-trial loses nothing.

---

## 3. Architecture

```
ExperimentManager  (the ONLY thing that owns state and timing)
   │  tells:  ExperimentUIController · ChairSelectionTask · VoiceRecallManager
   │          ExperimentAudio · XRRigTeleporter
   ▼
EventLogger.Log(EventTypes.X, …)      ← every experimental event, one entry point
   ▼
EventBus.Publish
   ├──► CsvEventSink           → the CSV
   ├──► UnityConsoleEventSink  → the Unity Console
   └──► LslMarkerSink          → INERT PLACEHOLDER, see §5
```

`ExperimentManager` is the only script that runs a coroutine or schedules a delay. Nothing
polls the state to decide its own behaviour.

### Response-time contract

The Area B response timer starts **only** at `CHAIR_SELECTION_TIMER_START`, which fires after
`CHAIR_INSTRUCTION_COMPLETE`. It is stopped on the first line of the selection callback —
before any logging, audio or material work — so feedback cost is never counted as response
time. Duplicate selections are impossible: there is a latch on the chair *and* a latch on the
task.

---

## 4. Configuring the protocol

Everything tunable lives in two assets. No words, timings or targets are hard-coded in logic
or UI.

* `Assets/IKEA_EEG/Data/WordList_Default.asset` — the word sets.
  Ships with *Set A*: River, Copper, Lantern, Falcon, Sugar (plus a spare parallel set).
  Add or replace sets freely; `wordsPerSet` is validated at start-up.
* `Assets/IKEA_EEG/Data/ExperimentConfig_Default.asset` — timings, target chair, all
  participant-facing text, and `showCorrectnessToParticipant` (off: the participant is not
  told whether they were right, but the CSV still records it).

### The six chairs

| Chair | Size | Colour | Shape | Shares with target |
|---|---|---|---|---|
| Chair_01 | Small | Red | Classic | 0/3 |
| Chair_02 | Large | Blue | Classic | 2/3 |
| Chair_03 | Large | Green | Modern | 2/3 |
| **Chair_04** | **Large** | **Blue** | **Modern** | **3/3 — target** |
| Chair_05 | Small | Blue | Modern | 2/3 |
| Chair_06 | Medium | Yellow | Rounded | 0/3 |

Three distractors share exactly two target attributes, so the task requires conjunctive
search rather than spotting the only blue object. If you change `targetChair`, start-up
validation errors out unless **exactly one** chair matches.

---

## 5. Adding LSL / EEG later

`Assets/IKEA_EEG/Scripts/Data/LslMarkerSink.cs` is the integration point. It is currently a
no-op with a `Send Markers` toggle that is off, no dependency and no fake data.

1. Add an LSL package (LSL4Unity / liblsl-Csharp).
2. Uncomment the two `TODO(LSL)` regions in that file.
3. Tick **Send Markers** on the `LslMarkerSink` component
   (Systems ▸ ExperimentSystems ▸ EventLogger).

> The toggle is called `Send Markers` and **not** `Enabled` on purpose: a serialized field
> named `m_Enabled` collides with the one every MonoBehaviour already inherits, which is what
> produced the repeated "same field name is serialized multiple times" warning. The self-test
> now fails if any script reintroduces that collision.

No experiment code changes. The marker strings are the same `EventTypes` constants written to
the CSV, so the marker stream and the behavioural file join on identical labels.

---

## 5b. Interaction, recenter and comfort settings

All configured on the **scene rig instance**; the Starter Assets prefab asset is untouched.

| Setting | Value | Why |
|---|---|---|
| Ray dynamics | `Traditional`, resting length **10 m**, no retract | Was `RetractOnHitLoss` @ 0.25 m — the cause of the "whip to extend" behaviour |
| Near casting | off | Chairs are pointed at, never grabbed |
| Poke interactors | disabled | They sat ahead of the ray in each controller's interaction group |
| Select binding | `<XRController>{Hand}/triggerPressed` | Index trigger, not the side grip |
| Haptics | all `SimpleHapticFeedback` + `HapticImpulsePlayer` disabled | No somatosensory stimulus during EEG |
| Locomotion | move/teleport/climb/jump off; turn + gravity on | Area changes only via buttons |

**Recenter** is available in all three areas (`Btn_Recenter_A/B/C`) and logs a `RECENTER` event.
It yaws the rig around the current head position so the participant's present facing becomes
the room's forward, then places them on the spawn mark. Physical height and room-scale tracking
are untouched — the tracking origin mode is never changed. The same alignment runs automatically
on entering each area, and start-up now waits for a genuinely tracked head pose first.

---

## 6. Microphone device selection

Over Quest Link, Windows exposes **both** the PC microphone and the headset microphone. On
this machine Unity currently sees:

```
[0] "マイク配列 (デジタルマイク向けインテル® スマート・サウンド・テクノロジー)"   (48000 Hz)
[1] "Headset Microphone (Oculus Virtual Audio Device)"                        (48000 Hz)
```

The first version of this code took `Microphone.devices[0]` — the laptop array. It now
chooses deliberately, on `VoiceRecallManager`:

| Selection Mode | Behaviour |
|---|---|
| **PreferHeadset** *(default)* | Picks the first device whose name matches a headset keyword (`oculus`, `quest`, `meta`, `rift`, `headset`, `vr `, `link`). On this machine that selects **"Headset Microphone (Oculus Virtual Audio Device)"** — verified. |
| **ExplicitDevice** | Uses `Preferred Device`. Partial, case-insensitive match, so typing `Oculus` is enough. |
| **SystemDefault** | The old `devices[0]` behaviour, kept only for reproducibility. |

Whatever happens, it is never silent:

* the **full device list with sample-rate capabilities** is printed at start-up;
* the chosen device and the **reason** are logged as `Debug.Log`;
* a fallback is a `Debug.LogWarning`, never a quiet substitution;
* `Fail If Preferred Device Missing` makes a missing device a hard failure instead;
* a `MICROPHONE_DEVICE_SELECTED` row goes into the CSV with the device, the mode, the reason,
  the fallback flag and the full device list;
* every `RECORDING_SAVED` row also carries `device=` and `device_is_fallback=`.

### Signal quality — "captured" now means "audible"

A recording used to be called successful if the sample buffer was non-empty. A device that is
muted at the OS level returns a full buffer of **zeros**, which passed that check and produced
a WAV containing nothing. Every recording is now measured:

* **peak**, **RMS**, **peak dBFS**, **RMS dBFS**, **% near-zero samples** → Console + CSV.
* `audioCaptured` is TRUE only when the WAV was written **and** the signal is above threshold.
* Below threshold → **`MICROPHONE_SIGNAL_SILENT`** row with device, peak, RMS, dBFS, duration
  and sample rate. The WAV is still written for debugging.
* Threshold: peak < **0.01** (≈ −40 dBFS), configurable. Quieter than any real speech, far
  louder than digital silence.

A **1.5 s microphone test** also runs at session start and logs `MICROPHONE_TEST`. If the
Quest device is selected but returns silence you get:
*"Quest microphone selected but no usable signal detected."* — check the headset mic is not
muted and that Windows microphone privacy access is enabled. (A genuinely silent room can also
trigger this, so it is a warning, not a hard block.)

**Sample rate:** both devices report `48000-48000 Hz`, i.e. 16 kHz is not natively supported.
The requested rate is now clamped to the device's capability, so recordings are written at
**48 kHz** (~1.9 MB per 20 s recall) instead of asking the driver to resample.

---

## 7. Audio: three separate things, only two of which Unity can see

Do not treat these as one question. Every past confusion came from collapsing them.

| Layer | What it means | Can Unity verify it? |
|---|---|---|
| **1. Clip playback** | A valid, non-empty AudioClip was scheduled on a live AudioSource and `isPlaying` became true after its DSP onset. | **Yes** — this is what `audio_confirmed=TRUE` means, and *only* this. |
| **2. Windows output routing** | Unity's output is going to the Quest endpoint rather than the laptop speakers. | **No.** Unity has no API to read or set the OS playback endpoint. Always reported as unverified. |
| **3. Physical auditory onset** | Sound actually reached the participant's ears at a known time. | **No.** Requires a microphone/loopback measurement — see the EEG validation note below. |

`audio_confirmed=TRUE` therefore means layer 1 only. The CSV notes say `unity_playback_only=TRUE`
on every stimulus row so this can never be misread later.

### The Windows device to select

Your machine has exactly two ACTIVE playback endpoints:

```
スピーカー   [Realtek(R) Audio]                 <- laptop speakers
ヘッドホン   [Oculus Virtual Audio Device]      <- THE QUEST HEADSET, select this one
```

Set the Windows default output to **「ヘッドホン」(Oculus Virtual Audio Device)** — on an
English Windows this reads *Headphones (Oculus Virtual Audio Device)*. If it is left on
スピーカー, Unity will report `audio_confirmed=TRUE` for every stimulus and the headset will
still be silent. The session banner prints this reminder on every run.

---

## 7b. If a beep is logged but not heard

The `IMMEDIATE_RECALL_BEEP` event firing does **not** mean the participant heard anything, and
the code no longer pretends it does.

**What now happens at every auditory stimulus:**

1. The cue is scheduled on the DSP clock with `PlayScheduled`, not `PlayOneShot`, so onset is
   sample-accurate rather than quantised to the frame that happened to call it.
2. The state machine **waits for the real onset**, then verifies the output path.
3. The onset event is logged with `audio_confirmed=TRUE|FALSE`, the scheduled and confirmed
   DSP times, and the clip length.
4. If it was not delivered, an **`AUDIO_STIMULUS_FAILED`** row is written and a `Debug.LogError`
   names the reason. One `event_type` query finds every compromised trial.

For word encoding, the audio cue is scheduled *first* and the word is revealed once its onset
has actually passed, so visual and auditory onsets coincide to within one frame.

**Startup diagnostics** (`AUDIO_DIAGNOSTICS` row + Console) report: AudioListener count and
whether it is active, `AudioListener.volume` and `pause`, Unity's output sample rate / speaker
mode / DSP buffer, the Game view **Mute Audio** toggle, and every cue source and clip.

**Checklist when you hear nothing**, in the order they actually go wrong:

1. **Windows default playback device.** This is outside Unity and is the most likely cause on
   a Link setup. Unity plays to the Windows default output; if that is the laptop speakers,
   the headset gets nothing. Set the default output to the Oculus/Quest device.
   *The diagnostics say so explicitly — Unity cannot see past its own output device, and the
   Console message states that.*
2. **Game view "Mute Audio" toggle.** Now checked automatically and reported as an error.
3. AudioListener missing/duplicated, or `AudioListener.volume == 0`. Now checked automatically.
4. Audio disabled in Project Settings ▸ Audio. Shows as `sampleRate 0`.

Verified on this machine while the scene was loaded: one AudioListener (on Main Camera),
Unity output initialised at 48000 Hz Stereo, Mute Audio OFF. So items 2–4 were already fine
and item 1 is the remaining candidate.

**Spoken words:** there is no text-to-speech. Words are presented **visually plus a tone cue**.
`ExperimentAudio` reads its clips from Inspector fields and only generates a tone when a field
is empty, so dropping in recorded or TTS clips replaces a cue with no call-site change.

---

## 8. The spoken target words

The five words are an **auditory-only stimulus**. They are never shown on screen: during
encoding the panel displays a single static prompt ("Listen carefully.") that does not change
per word. That is deliberate on two counts — a visual reveal would turn a verbal memory task
into a reading task, and a per-word visual change would superimpose a visual evoked response
on the auditory one the EEG work will be measuring.

`ExperimentUIController` has **no method** that can put a target word on screen, and the
self-test asserts that it doesn't.

### Where the audio comes from

Runtime text-to-speech was rejected: the Windows speech API plays through the OS mixer rather
than an `AudioSource`, so `WORD_PRESENTED` could not be tied to a clip onset, and none of it
exists on a standalone Quest build.

Instead, speech is synthesised **once, at authoring time**, by the local Windows voice
(`Microsoft Zira Desktop`, en-US) and committed as ordinary WAV assets. Offline — no cloud
service, no API key, no network call. The running experiment has zero TTS dependency.

```
Assets/IKEA_EEG/Audio/Words/Set_A_prototype/01_River.wav … 05_Sugar.wav
Assets/IKEA_EEG/Audio/Words/Set_B_parallel_spare/01_Harbour.wav … 05_Pepper.wav
```

Regenerate with **`IKEA_EEG ▸ Generate Spoken Word Clips (local TTS)`**. It synthesises every
word of every set, trims them, imports them and assigns them to the word set. To use human
recordings instead, just drop WAVs onto the set's `Word Clips` list — nothing else changes.

### Onset accuracy

The raw TTS output carried **118–235 ms of leading silence, differing per word**. Stamping
`WORD_PRESENTED` at clip onset would therefore have put the marker a different distance ahead
of the actual voice for every item — ~117 ms of jitter, larger than the auditory ERP
components of interest. The generator trims each clip to a **uniform 10 ms pre-roll** (with a
4 ms fade so trimming cannot introduce a click):

| Word | Before | After | Lead |
|---|---|---|---|
| River | 1404 ms | 505 ms | 118 → 10 ms |
| Copper | 1474 ms | 561 ms | 132 → 10 ms |
| Lantern | 1614 ms | 719 ms | 124 → 10 ms |
| Falcon | 1739 ms | 741 ms | 235 → 10 ms |
| Sugar | 1484 ms | 584 ms | 118 → 10 ms |

Clips import as **PCM, Decompress On Load, preloaded**, so a scheduled clip cannot start late.
`WORD_PRESENTED` is logged after waiting for the clip's real DSP onset and carries
`audio_confirmed`, the scheduled/confirmed DSP times, and the clip name in `object_id`.

Encoding pacing follows the recordings, not a fixed display time. Current mean
stimulus-onset asynchrony is **~1.12 s** (3.11 s of speech + 0.5 s gaps). Raise
`Inter Word Gap Seconds` on the config if you want slower encoding — 0.9 s gives ~1.5 s SOA.

---

## 9. Speech-to-text status

**Not implemented — deliberately.** `NullTranscriptionProvider` is active.

What *does* work now: microphone audio is recorded to timestamped WAV, onset/offset are
logged against the session clock, and each recording is tagged with session/trial/phase.

`RecallScorer` (number correct, order correct, intrusions) is **fully implemented and tested**
— it simply has nothing to score yet. Plug in any `ISpeechTranscriptionProvider` and the
scoring, logging and CSV columns start populating with no other change.

Windows' `DictationRecognizer` was **not** used: it requires Windows "Online speech
recognition", which routes audio to Microsoft servers, so it is not a local solution.

---

## 10. Editor tools

| Menu | Does |
|---|---|
| `IKEA_EEG ▸ Build Experiment Scene` | Regenerates the whole scene from code and rewires every reference |
| `IKEA_EEG ▸ Validate Experiment Scene` | Component counts, chair uniqueness, ray reach, chair occlusion, locomotion state |
| `IKEA_EEG ▸ Print Scene Hierarchy` | Structural dump to the Console |
| `IKEA_EEG ▸ Run Self Test` | Serialized-field collisions, reference completeness, config, chair logic, recall scoring, CSV round-trip, audio, and live audio/microphone hardware configuration |

**Rebuilding the scene discards manual edits to it.** Materials and the two config assets are
*not* overwritten once they exist.

`Run Self Test` writes a throwaway session folder into the data directory — delete it so it is
not mistaken for participant data.

---

## 11. What was changed in the existing project

* **Added:** everything under `Assets/IKEA_EEG/`.
* **Modified:** `ProjectSettings/EditorBuildSettings.asset` — the new scene was appended to
  the build list. `SampleScene` remains.
* **Untouched:** all XR/OpenXR settings, the URP config, the input actions, the XR Origin
  prefab, `SampleScene`, `BasicScene`, `VRTemplateAssets`, and every package.

Locomotion is disabled **on the scene's rig instance only** — the prefab asset is not
modified. Disabled: move, teleport, climb, jump, grab-move. Kept: snap/continuous turn and
gravity. Room-scale physical walking is unaffected; area changes happen only via the buttons.
