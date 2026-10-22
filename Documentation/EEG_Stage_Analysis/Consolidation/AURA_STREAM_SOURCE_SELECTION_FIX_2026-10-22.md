# AURA stream source selection fix — 2026-10-22

Branch `feature/protocol-ux-localization`, uncommitted. Explicitly authorised change to the
acquisition path (stream selection only).

## Symptom
Two LSL streams are named exactly `AURA` on the lab network:

| host | source_id (observed) | uid (observed) | result |
|---|---|---|---|
| laptop-san (computer A) | `AuraLSL-20261022-16;32;55` | `f44ee346-25af-4b5d-8c85-7bb1191d67d4` | LabRecorder: 7506 samples on A, 7533 on B, ~30 s each; 8 ch, 250 Hz, float32 |
| MSI (unrelated machine) | `AuraLSL-20261022-13;24;28` | — | Unity selected it and received 0 samples |

## Cause
`LslBinding.ResolveStreamInfo` called `resolve_stream("name", "AURA", minimum: 1, timeout)`.
In the installed binding (`Assets/Plugins/LSL.cs`, liblsl v1.17.7), `lsl_resolve_byprop` returns as
soon as `minimum` matches have answered. With `minimum=1` it returned the first responder,
and the code used `results[0]`. Which machine answers first is not controlled, so MSI could be picked.
Extra results were also never disposed.

## Change
- `LslBinding.ResolveStreamCandidates`: same call with `minimum = 1024` (the wrapper's buffer
  size), so it collects every `AURA` that answers within the finite timeout (3 s in Bootstrap).
  `ResolveStreamInfo` (marker loopback test) keeps `minimum=1` and now disposes extra results.
- `StreamMetadata` now carries `hostname` and `uid`, read from the live StreamInfo.
- `AuraLslReceiver`: new serialized `m_PreferredSourceHostname`. Bootstrap value: **`laptop-san`**
  (the legacy `IKEA_EEG_Experiment.unity` was not edited and has no value). The scene builder sets the same value.
- `AuraLslReceiver.SelectCandidate` is the one rule, used by `Connect` (Play Mode and every Editor
  diagnostic that calls `Connect`):
  - host configured: exactly one candidate from that host (case-insensitive) → selected; none →
    `AURA_HOST_NOT_FOUND` (no fallback); several → `AURA_AMBIGUOUS`, with identities listed.
  - no host configured: one candidate → selected; several → `AURA_AMBIGUOUS`; none → `AURA_NOT_FOUND`.
  - Candidates that are not selected are disposed on every path.
- `IKEA_EEG/LSL/Check AURA Stream` reads the preference from the open scene's receiver (Play Mode,
  or with Bootstrap open), or else from `IKEA_EEG_Bootstrap.unity` on disk. It prints the requested host,
  all candidates, the selected name/host/source_id/uid/channels/rate/format, and the samples received.
  When nothing arrives it reports "no samples were received" and does not guess why.
- source_id and uid are not fixed in code. They change when AURA restarts its outlet.

Unchanged: filters, notch, units, QC, clocks, windows, baselines, protocol, content, adaptation
(still off), CSV schema.

## Behaviour change to know
`Connect` now always blocks for the full resolve timeout (3 s) instead of returning on the first
answer. In a fresh process, the first liblsl call also had a one-time ~5 s start-up cost (measured 8.05 s
for a 3 s timeout, then 3.00–3.06 s).

## Validation
- Compile/static: `dotnet build Assembly-CSharp-Editor.csproj` (output outside the project), 0 errors.
  Unity was open on the project, so Unity's own compile and batch self-test were **not** run.
- Selection tests: new self-test section `AURA STREAM SELECTION (SOURCE HOST)` (also
  `ExperimentSelfTest.RunAuraSelectionFromCommandLine`). The same 6 cases were run against the
  compiled `Assembly-CSharp.dll` from a console harness: all passed.
- Real network, from a console harness on computer B (ACER), using the shipped
  `ResolveStreamCandidates` + `SelectCandidate` and a 4 s read-only inlet (not Check AURA Stream, not Unity):
  ```
  resolve: resolved 1 stream(s) named 'AURA'
    candidate 0: name=AURA; type=EEG; channels=8; rate=250.000 Hz; format=cf_float32;
      source_id=AuraLSL-20261022-16;32;55; host=laptop-san; uid=f44ee346-25af-4b5d-8c85-7bb1191d67d4
  requested host: laptop-san -> selected index 0
  samples received in 4.0 s: 982  (remote ts span 3.927 s, ~249.8 Hz)
  ```
  MSI was **not** visible at the time, so the live two-publisher case was not reproduced. It is
  covered only by the metadata-level tests.
- Pending: `Check AURA Stream` and Play Mode in Unity, ideally while MSI is also publishing.
- Self-test adjustment: the existing receiver check gives its synthetic in-process `AURA` outlet the
  local hostname, and accepts `AURA_AMBIGUOUS` on its negative path. Without this, a real AURA on
  the LAN would make the check ambiguous.

## Evidence added later on 2026-10-22 (commit of this fix)

Three kinds of evidence, kept apart:

**1. Reported by the user (run inside Unity by them; not observed by Claude).**
`IKEA_EEG/LSL/Check AURA Stream` in the Editor printed:
- requested host `laptop-san`; selected hostname `laptop-san`
- source_id `AuraLSL-20261022-16;32;55`; uid `f44ee346-25af-4b5d-8c85-7bb1191d67d4`
- 8 channels, 250 Hz, float32
- 5/5 samples, timestamps increasing, 5-sample span 0.016 s → PASS

Only one candidate was visible. This does **not** test selection while MSI is also publishing.

**2. Observed directly by Claude on disk** (session folders under
`%USERPROFILE%\AppData\LocalLow\DefaultCompany\IKEA_EEG\IKEA_EEG_Data\`; not copied into Git):

| run folder | `stream_source_id` in `eeg_session_metadata.txt` and `raw_eeg.csv` header | data rows in `raw_eeg.csv` |
|---|---|---|
| `S_20261022_173034_r01_72217e` | `AuraLSL-20261022-16;32;55` | 34659 |
| `S_20261022_174233_r01_6b5a31` | `AuraLSL-20261022-16;32;55` | 74736 |

Both metadata files record SHA-256 hashes for `AuraLslReceiver.cs` (`48c9132e…36a5`) and
`IKEA_EEG_Bootstrap.unity` (`a2e4d6ac…acbb`). These match the files in this commit. Both runs therefore
used this selection code with `laptop-san` configured, and received from laptop-san's outlet. MSI's
presence during those runs was not recorded.

**3. Tests.** Unchanged from the Validation section above: the dotnet build and the console harness
(6 selection cases). The Unity self-test was not re-run for this commit because Unity was open on the project.

## Still open (not resolved by this change)
- A real test with MSI and laptop-san publishing `AURA` at the same time.
- Other Editor diagnostics (`EegDiagnostics`, `EegSpectralDiagnostics`, `EegChannelIntegrityDiagnostics`)
  create receivers without a hostname. With two `AURA` publishers they fail with `AURA_AMBIGUOUS`.
- The legacy `IKEA_EEG_Experiment.unity` has no hostname configured.
- 60 Hz interference and acquisition quality.
- Units. The user states AURA units **are µV**. Recording metadata and CLAUDE.md still say the
  scaling is unconfirmed. This discrepancy is flagged here and not changed.
- Time synchronisation and sample-loss detection have not been validated.
