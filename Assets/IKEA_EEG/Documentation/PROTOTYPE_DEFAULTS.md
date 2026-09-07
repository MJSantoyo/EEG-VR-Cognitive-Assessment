# IKEA_EEG — prototype defaults

> **These are DEVELOPMENT DEFAULTS, not a validated clinical protocol.**
>
> Every value below was chosen to make the experiment testable end to end. None of it carries a
> normative claim, none of it is derived from a published protocol, and none of it should be
> reported as one. All of it is editable in `Assets/IKEA_EEG/Data/ExperimentConfig_Default.asset`
> without touching code, and whatever it is set to is written into the session record
> (`SESSION_START`, the `AREA_A_ENTER` notes, the session summary CSV and the researcher summary).

## Area 0 — VR familiarization

| Parameter | Prototype default | Where |
|---|---|---|
| Familiarization enabled | on | `enableFamiliarization` |
| Practice objects | sphere, cube, and one large UI button | scene (`PracticeObject`) |
| Practice required to continue | **no** — START EXPERIMENT is always available | by design |
| Controller help | static labelled diagram, Area 0 only | scene (`ControllerDiagram`) |
| Researcher status panel | present, **inactive and empty** | reserved for a future EEG readout |

Area 0 produces no cognitive data. `FAMILIARIZATION_*` and `PRACTICE_OBJECT_SELECTED` are
diagnostic session events and are excluded from chair accuracy, response-time averages and
memory scoring.

## Chair shape categories

| Old (removed) | New | Visible geometry |
|---|---|---|
| Modern | **SOLID** | backrest is one continuous flat panel — no gaps, no curvature |
| Classic | **SLATTED** | backrest is separate horizontal bars with visible gaps |
| Rounded | **CURVED** | all parts cylindrical; the backrest is a curved surface |

"Modern" and "Classic" were aesthetic judgements: two people could reasonably disagree, and a
participant could not derive them from the object. The three replacements name the **structure
of the backrest**, which is the most salient part of each body and visible from the standing
position. They partition cleanly — *is the back round?* separates CURVED; *does the back have
gaps?* separates SLATTED from SOLID.

The enum's integer order is unchanged (Solid=0, Slatted=1, Curved=2), so existing assets and
seeds point at exactly the same geometry as before. **The CSV vocabulary did change** — see the
compatibility note in `Assets/IKEA_EEG/Data/README_DATA_LOCATION.txt`.

## Chair task

| Parameter | Prototype default | Where |
|---|---|---|
| Chair trials per run | **3** | `chairTrialsPerRun` |
| Difficulty sequence | **LOW → MEDIUM → HIGH** | `difficultySequence` |
| LOW distractor profile | `[0,0,0,0,1]` — four distractors share nothing with the target, one shares a single attribute | `difficultyProfiles` |
| MEDIUM distractor profile | `[0,0,1,1,1]` — three distractors share exactly one target attribute | `difficultyProfiles` |
| HIGH distractor profile | `[1,2,2,2,2]` — four near-matches share **two** of the three target attributes | `difficultyProfiles` |
| General task instructions | shown **once per block**, gated by READY, untimed | `areaBGeneralInstructionText` |
| Pre-target interval | 0.75 s blank, before the target appears (excluded from RT) | `preTargetIntervalSeconds` |
| Feedback duration | 1.5 s | `chairFeedbackDurationSeconds` |
| Inter-trial interval | 1.5 s | `interTrialIntervalSeconds` |
| Response deadline | **none** — a trial waits indefinitely for a selection | not configurable yet |
| Per-trial correctness shown | off | `showCorrectnessToParticipant` |
| End-of-block result | "Executive task complete" + "N / M correct" over valid scored trials | `executiveTaskCompleteText` |
| Hover logging | off | `logChairHoverEvents` |
| Chair attribute space | 6 colours × 3 sizes × 3 shapes | `ChairColor` / `ChairSize` / `ChairShape` |
| Chair slots | 6 fixed positions on a 5 m arc, all equidistant from Spawn_B | scene (`ChairSlot`) |

The task itself does **not** change with difficulty: the instruction always names all three
dimensions (colour + size + shape) and exactly one chair always satisfies all three. Only the
similarity of the distractors changes.

## Verbal memory

| Parameter | Prototype default | Where |
|---|---|---|
| Words per set | 5, order significant | `WordListDefinition.wordsPerSet` |
| Active set | `SET_A` — River, Copper, Lantern, Falcon, Sugar | `wordSetIndex` |
| Spare set | `SET_B` — Harbour, Velvet, Compass, Thunder, Pepper | `WordList_Default` |
| Modality | **auditory only** — words are never displayed | enforced in code |
| Area A instruction duration | 8 s | `instructionDurationSeconds` |
| Pre-first-word delay | 1.5 s | `preFirstWordDelaySeconds` |
| Inter-word gap | 0.5 s (pacing otherwise follows each recording's real length) | `interWordGapSeconds` |
| Pre-beep delay | 1 s | `preBeepDelaySeconds` |
| Immediate recall **maximum** | 20 s | `immediateRecallMaxDuration` |
| Area C instruction duration | 7 s | `delayedInstructionDurationSeconds` |
| Delayed recall **maximum** | 20 s | `delayedRecallMaxDuration` |

### Adaptive recall stop

A recall recording normally ends **before** its maximum: once the participant has spoken and
then stayed silent for `recallSilenceStopSeconds`. See "Response time and recall timing" below.

| Parameter | Prototype default | Where |
|---|---|---|
| Post-speech silence that stops recording | **4.0 s** | `recallSilenceStopSeconds` |
| Speech threshold (window peak) | 0.05 (≈ −26 dBFS) | `speechStartThreshold` |
| Silence threshold (window peak) | 0.015 (≈ −36 dBFS) | `silenceThreshold` |
| Voiced audio needed to arm the detector | 0.35 s | `minimumSpeechDuration` |
| Analysis window | 0.1 s | `VoiceRecallManager.m_AnalysisWindowSeconds` |

**None of these amplitude thresholds has been checked against a real Quest microphone.** They
are conservative guesses: the speech threshold sits well above the silence threshold, and the
band between them counts as neither, so noise cannot end a recording. If a participant is cut
off mid-answer, raise `recallSilenceStopSeconds` or lower `silenceThreshold`. If recordings
always run to the maximum, the microphone's level is probably below `speechStartThreshold` —
check `RECALL_RECORDING_STOP`'s `loudest_window_peak` in the CSV before changing anything.

## Randomisation

| Parameter | Prototype default | Where |
|---|---|---|
| Seed mode | New seed each session | `seedMode` |
| Fixed seed (when used) | 20250812 | `fixedSeed` |
| Restart behaviour | **new session, new seed** | `ExperimentManager.RestartTrial` |
| Replay behaviour | new session, **same seed** | IKEA_EEG ▸ Researcher ▸ Replay Same Seed |

## Response time and recall timing — exact definitions

### Chair response time (RT)

> **RT = the interval from the moment the three target attributes become visible, to the moment
> the first valid chair selection is registered.**

* **Zero** is `CHAIR_TARGET_ONSET`, logged one rendered frame after the target text is written to
  the panel. `CHAIR_SELECTION_TIMER_START` is logged on the same frame, and the measured offset
  between the two is recorded on it (`target_onset_to_timer_start_ms`, typically < 0.2 ms).
* **Stop** is the first line of the selection callback, before any logging, audio or material
  work, so feedback cost is never attributed to the participant.
* **Excluded:** the general task instructions and however long they were read for
  (`AREA_B_INSTRUCTIONS_ONSET` → `AREA_B_READY`), the pre-target interval, the previous trial's
  feedback and ITI, and everything before Area B.
* **Included:** the time spent reading the three attributes. That reading is part of the task on
  every trial and is the same demand on every trial.

`CHAIR_INSTRUCTION_ONSET` is emitted at the same instant as `CHAIR_TARGET_ONSET` and now means
target onset. **Semantic change:** before this pass it marked a target that was shown
`chairInstructionDurationSeconds` (5 s) *before* selection opened, so reading time sat outside
the RT. The task explanation is now a separate READY-gated screen, so the target is the stimulus
and the timer starts with it.

### Recall recording

A recording ends at **whichever comes first**:

1. speech detected, then `recallSilenceStopSeconds` of continuous silence → `SILENCE_AFTER_SPEECH`
2. the phase's hard maximum → `MAX_DURATION`

"Speech has started" = `minimumSpeechDuration` of audio has **accumulated** in windows whose peak
reached `speechStartThreshold`. "Silence" = a window whose peak is below `silenceThreshold`; the
run must be **continuous**, and any window at or above the threshold resets it to zero. Silence
before speech never stops anything, so a participant who takes a long time to begin is not cut
off; if they never speak, the maximum duration ends it.

Every recording that starts produces one `RECALL_RECORDING_STOP` carrying
`termination_reason` and `actual_recording_duration_s`. **The stored WAV is never trimmed or
modified** on the basis of any of this.

## What is deliberately NOT configured here

* No response deadline for the chair task.
* No practice/training block.
* No counterbalancing across participants.
* No automatic transcription or automatic recall scoring — recall is scored offline from the WAV
  files using the generated `manual_recall_scoring_<session_id>.csv` form.
* No EEG analysis of any kind (see `EEG_LSL_PIPELINE.md`).
