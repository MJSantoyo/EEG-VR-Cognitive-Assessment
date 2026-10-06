# CLAUDE.md — IKEA_EEG

Durable project context. Keep this file short. Deep detail lives in the docs listed under "Where to look".
If documentation, protocol, and code disagree, do not silently choose one as correct.

Treat:
- code as evidence of currently implemented behaviour
- protocol/scientific documentation as evidence of intended experimental behaviour

Flag the discrepancy explicitly before modifying anything.

## 1. Project purpose
Unity 6 (6000.3.21f1, URP, OpenXR, XR Interaction Toolkit) VR research prototype. A participant does a
visual word-recognition memory task, an unrelated chair-selection task in a virtual showroom, then a delayed
recognition test, while EEG from an AURA amplifier is streamed over LSL and recorded with behavioural events
on a common timeline. The current goal is trustworthy data collection, not inference.
Not a medical device, not clinically validated, one developer, no pilot cohort.

## 2. Target hardware
- Headset: **Meta Quest 2**, run over Quest Link from a Windows PC (not a standalone Android build).
- Quest 2 is the performance budget: keep draw calls, texture sizes and lights modest.
- Two machines: Computer A (Unity, Quest Link, `Assets/Plugins/lsl.dll`) and Computer B (runs AURA, publishes LSL).

## 3. Multi-scene architecture
Scenes live in `Assets/IKEA_EEG/Scenes/` and are all in Build Settings:
- `IKEA_EEG_Bootstrap.unity` — every persistent system, no room content.
- `IKEA_EEG_AreaA.unity` — Area 0 (practice room) and Area A (showroom entrance, encoding, immediate recognition).
- `IKEA_EEG_AreaB.unity` — showroom chair task.
- `IKEA_EEG_AreaC.unity` — exit lounge, delayed recognition, results.
- `IKEA_EEG_Experiment.unity` — legacy single combined scene, still present. Do not delete or "clean up".
Rooms keep their world coordinates (Area 0 x=-100, A x=0, B x=+100, C x=+200); never rebase them.
Room switching: `Scripts/SceneFlow/AreaSceneLoader` (mechanism) driven by `ExperimentManager` (decides when).
Scene generation: `Assets/IKEA_EEG/Editor/ExperimentSceneBuilder*.cs` (menus under `IKEA_EEG/`).

## 4. Experiment flow (default protocol: Recognition)
Language select (EN/ES/JA) -> Area 0 practice -> Area A: 15 visual words, then immediate recognition
(SEEN BEFORE / NOT SEEN BEFORE) -> Area B: 3 chair trials (colour/size/shape, LOW->MEDIUM->HIGH) ->
Area C: delayed recognition -> results -> NEW TRIAL / RESTART / END.
- State machine: `Scripts/Experiment/ExperimentManager.cs`, `ExperimentState.cs`.
- Output per run: `events.csv` (45 columns, append-only, legacy indices never move), `raw_eeg.csv`, summaries.
- Word lists are development placeholders / adapted sets (`validatedForResearch = false`); not a standard RAVLT.

## 5. Persistent vs room-specific
- **Persistent (Bootstrap):** ExperimentManager, config, LSL/AURA receiver, ring buffer, recorder, feature
  pipeline, Shadow Mode, CSV sinks, XR rig, dashboard status writer, UI/localization services, VoiceRecallManager.
  Exactly one of each; room scenes must not contain copies (the validators check this).
- **Room-specific:** environment art (`AreaX_Visuals` prefabs, props, lighting), spawn points, room UI canvases,
  chair targets/slots (Area B).
- Legacy FreeRecall/voice code is dormant but a startup mic test still runs. Protected: do not delete, disable
  or restore it.

## 6. EEG / AURA facts
- AURA: 8 channels, 250 Hz, via LSL streams `AURA`, `AURA_Filtered`, `AURA_Power`.
- Montage (hardware-verified 2026-09-15): CH1 Fp1, CH2 F3, CH3 Fz, CH4 F4, CH5 Cz, CH6 P3, CH7 Pz, CH8 P4.
- Path: `LslBinding` (reflection, no compile-time LSL dep) -> `AuraLslReceiver` -> `RawEegRingBuffer` ->
  `RawEegRecorder` / `EegFeaturePipeline` (filter, Welch PSD, band power) -> `LatestEegFeatures`.
- Analysis timestamp (clock-corrected) is authoritative; do not substitute remote or raw timestamps.
- Amplitudes are in AURA native units; microvolt scaling is unverified. Never compare to published uV figures.
- Usable EEG data is scarce (~12 of ~543 recorded sessions have EEG). Check a session folder actually holds
  `raw_eeg*.csv` and the analysis clock domain before promising analysis. Near-identical channels were seen in
  recent recordings; that is unresolved.
- QC thresholds are heuristics. Never tune them to make a session pass.
- Details: `EEG_PIPELINE_TECHNICAL_DOCUMENTATION.md`, `Assets/IKEA_EEG/Documentation/EEG_LSL_PIPELINE.md`.

## 7. Shadow Mode limitations
`Scripts/Neuro/ShadowModeController.cs` is observe-only (phase 1). It records what the feature pipeline sees
to `shadow_decisions.csv` and changes nothing about the experiment.
- It does **not** perform live adaptation. Difficulty, timing, stimuli and UI are never altered.
- Every window is INDETERMINATE: no normalization method is approved (`NormalizationApproved = false`), no
  baseline protocol is approved, and no code path emits LOW/MODERATE/HIGH.
- It must not hold references to experiment types (enforced by `ExperimentSelfTest`). Do not add any.
- Neuroadaptive control is a long-term goal, not an existing feature. Never describe it as working.

## 8. Scientific safety and validation rules
- Do not claim scientific validation that has not been run. State what was tested, how, and what was not.
- The behavioural experiment is frozen: flow, phase order, item counts, scoring, timings, LSL markers,
  CSV schema, chair logic, XR interaction. Change only on explicit request.
- No edits to the acquisition path (receiver, ring buffer, timestamps, recorder) for display or convenience.
- Do not alter CSV column order or meaning; append only.
- Do not invent thresholds, normalization, ROI definitions, or workload cut-offs.
- Do not present synthetic-stream or single-session results as validation of the method.
- Visual work is additive: new roots, prefabs, materials. No Canvas / TrackedDeviceGraphicRaycaster /
  XRSimpleInteractable / SpawnPoint / ChairTarget / ChairSlot / PracticeObject in decorative content.

## 9. Git safety rules
- Branch: `main`. Work on a feature branch for anything non-trivial.
- Never `git add .` / `git add -A`. Stage named files only; review `git status` and `git diff --stat` first.
- Never force-push (`--force`, `--force-with-lease`) and never rewrite published history.
- Do not commit or push unless the user asks. Do not amend; make a new commit.
- Do not touch backups (any backup folder, archive, or `*.bak`-style copy) unless explicitly asked.
- Do not stage `Demo_Captures/`, `Library/`, `Temp/`, `Logs/`, `UserSettings/` or `.claude/settings.local.json`
  unless asked.
- Before checking out or resetting a scene file, look at its diff: uncommitted authored work can be destroyed.

## 10. Unity scene / build safety
- `IKEA_EEG/Build Experiment Scene` and `IKEA_EEG/Scene Split/*` rebuild scenes destructively: hand-added
  objects are lost. Do not run them without explicit approval and a clean git state.
- Validators assert exact component counts (e.g. 12 TrackedDeviceGraphicRaycaster, 10 XRSimpleInteractable).
  Do not add objects that change them.
- Editor builders contain `MarkSceneDirty` + `SaveScene` pairs. After ANY `-batchmode -executeMethod` run, check
  `git diff --numstat -- Assets/IKEA_EEG/Scenes/` before trusting results or committing. A past run moved a room
  root 5.36 m and both validators still passed.
- Fix one bad transform by editing that line, not by checking out the whole scene.
- Do not edit `.unity`, `.prefab`, `.meta` or `ProjectSettings` by hand unless the task requires it; never
  delete `.meta` files. Do not open Unity (editor or batch) while another instance holds the project.
- Batch captures do not show runtime UI: world-space canvases are inactive in authored scenes, so a render
  cannot disprove occlusion. Use Game-view FOV (60 degrees) reasoning for framing questions.

## 11. Third-party asset dependency warning
Area A and B visuals reference gitignored Asset Store packs: `Assets/JeffamazedDev`,
`Assets/Urban_Props_Pack_Rozity`, `Assets/YughuesFreeArchitecturalMaterials` (plus an unused
`Assets/Realistic Metal Texture`). A fresh clone opens with missing props, materials and textures, and
"pushed to main" is not a full backup of the visuals.
- Recovery and exact file lists: `Assets/IKEA_EEG/Documentation/THIRD_PARTY_ASSET_DEPENDENCIES.md`.
- Do not commit or redistribute pack files (the JeffamazedDev license forbids it) without the user's decision.
- When adding a scene/prefab/material, count its references into these packs and update that document.
- Editor builders load some pack assets by path; renaming or moving pack folders breaks them.

## 12. Dashboard / frontend boundaries
`Tools/ResearcherDashboard/` (Python stdlib server, browser UI) is a **read-only** researcher monitor.
- Unity writes status outward (`Scripts/Dashboard/ResearcherStatusWriter.cs`); the dashboard only reads. Every
  handler is a GET; there must be no control for thresholds, filters, baselines, workload level or difficulty.
- Not an EEG viewer; raw EEG is watched in AURA. The waveform panel was deliberately removed; do not re-add it.
- No second LSL inlet, and nothing between acquisition and the ring buffer.
- Mock mode (`serve.py --mock`) must stay visibly labelled as simulated.
- Docs: `Tools/ResearcherDashboard/README.md`, `ARCHITECTURE.md`.

## 13. Runtime vs static validation language
Always say which kind of check was done; never let one stand in for the other.
- **Compile/static:** compiles, scene validator passes, self-test passes, GUID/reference audit, code reading,
  git diff. Proves structure only.
- **Runtime:** Play Mode in the Editor, Quest Link session, live AURA stream, a recorded session inspected.
- Write "compiles / passes static validation" or "verified at runtime on <what>". Never write "works" or "verified"
  for something only compiled. If runtime was not run, say so in the same sentence.
- Passing validators do not prove correctness (see the scene-mutation note in section 10).

## 14. Resource-efficiency rules
- One task per session. Finish it, report, stop.
- Long session on the same task: recommend `/compact`. Switching domain (art -> EEG -> dashboard -> docs):
  recommend `/clear`.
- Prefer targeted searches (Grep/Glob on a named folder) over reading directories. Avoid whole-repo re-audits
  unless the task needs one.
- Do not read `Library/`, `Temp/`, `Logs/`, `Demo_Captures/`, `Presentation_Evidence/` or large `.unity` files
  whole. Read large files by line range.
- Do not restate project history in replies; link to the doc instead.
- Use the least expensive capable model and effort for the task (small edits and doc work: low effort;
  scene/EEG pipeline changes: higher).
- Do not spawn subagents unless asked.

## 15. Current stable milestone (main)
- Multi-scene architecture (Bootstrap + AreaA + AreaB + AreaC) in place, with developer shortcuts and desktop input.
- Area A production art, Area B production art pass, and Area C production art pass are merged to `main`
  (latest merge: `feature/areac-production-art-pass`).
- Third-party art dependencies documented (`THIRD_PARTY_ASSET_DEPENDENCIES.md`); packs themselves are not in git.
- Recognition-protocol behaviour, CSV schema and EEG acquisition path are unchanged by the art work.
- EEG feature extraction, QC, Shadow Mode and offline analysis are implemented but need further validation;
  no neuroadaptation exists.
- Runtime validation status of the multi-scene build on Quest 2 hardware is not recorded here; check
  before claiming it.

### Known open issues
- NEW TRIAL and RESTART currently do not respond from the final Results screen.
- Treat this as a runtime regression. Do not claim the session restart/new-run flow works until it is fixed
  and retested.
- Near-identical EEG channels remain unresolved.
- Baseline aggregation, normalization and workload thresholds are not scientifically approved.

### Planned neuroadaptive direction (NOT implemented)
- Adaptation belongs to the Area B chair task.
- EEG from a completed trial/window may inform the NEXT trial.
- Never change difficulty mid-trial.
- No LOW/MODERATE/HIGH rule is approved yet.

## Where to look
- `README.md` — overview and status table.
- `CLAUDE_HANDOFF.md` — long continuity doc (950 lines, written 2026-08; dated, read by section only).
- `DECISION_RECORD_RECOGNITION_PROTOCOL.md` — why Recognition replaced FreeRecall.
- `EEG_PIPELINE_TECHNICAL_DOCUMENTATION.md`, `Assets/IKEA_EEG/Documentation/` — EEG, defaults, third-party packs.
- Offline analysis: Unity menu `IKEA_EEG/EEG/Offline Session Analysis…` (three rules are copied, not shared,
  with the live code).
