# IKEA_EEG — Researcher Dashboard (Investigator Console) v0

A local, **read-only** monitor for a running or recorded IKEA_EEG session.

It observes. It controls nothing. There is no control in this interface for a threshold, a
filter, an ROI, a baseline, a normalization formula, a workload level or task difficulty, and
the server that backs it has no write path — every handler is a GET.

**It is not an EEG viewer.** Raw live EEG is watched in AURA, which is the instrument's own
display and the authoritative one. Duplicating a waveform here would add a second, lower-rate,
unvalidated picture of the same signal and invite people to read it as the analysed one.

---

## Launch

**1. Start the dashboard server** (Python 3.8+, standard library only — nothing to install):

```bash
python "C:\Users\Mariana\Unity Projects\IKEA_EEG\Tools\ResearcherDashboard\serve.py"
```

**2. Open** <http://127.0.0.1:8760/> in any browser.

**3. Press Play in the Unity Editor.** The live panel fills in within a second. Nothing needs
to be wired in the scene — the status writer creates itself at runtime.

### Mock mode — no AURA, no Unity

```bash
python "C:\Users\Mariana\Unity Projects\IKEA_EEG\Tools\ResearcherDashboard\serve.py" --mock
```

Simulated values for UI work. A permanent amber banner and an amber source chip make it
impossible to mistake for real data. The **Session review** tab still reads your real recorded
sessions in this mode.

### Options

| Flag | Effect |
|---|---|
| `--mock` | Simulated live data. Offline review stays real. |
| `--port N` | Default 8760. |

Bound to `127.0.0.1` only — nothing off this machine can reach it.

---

## How the data gets there

```
Unity (Editor only)                        this process                browser
──────────────────                         ────────────                ───────
ResearcherStatusWriter  ──writes──>  status.json  ──reads──>  serve.py  ──GET──>  index.html
   (reads public getters)            4x / second               (read-only)         (polls 1 Hz)

IKEA_EEG_Data/<session>/  ─────────────────────────reads──────>  serve.py
   events, shadow_decisions, shadow_diagnostics
```

Unity opens no socket and answers no request. A dashboard that is closed, crashed or never
started cannot affect a participant session, because nothing in the session waits on it.

`ResearcherStatusWriter` is wrapped in `#if UNITY_EDITOR`, so a Quest player build never
creates it, never opens the file and never spends a millisecond in it.

### Endpoints

`/api/status` · `/api/events` · `/api/files` · `/api/sessions` · `/api/session/<id>` ·
`/api/meta`

All GET, all read-only. There is no `/api/eeg` — see *Removed* below.

---

## What the dashboard shows

**Live tab**

- A primary status strip: current phase, EEG stream state, valid channels, channel quality,
  feature validity, QC warning, Shadow Mode state — the seven things readable at arm's length
- Session and run metadata, elapsed time, session directory
- EEG **acquisition status**: connected or not, stream name, nominal rate, samples received,
  feature windows published, latest LSL timestamp, time correction
- Channel quality per electrode (Fp1, F3, Fz, F4, Cz, P3, Pz, P4) with ROI membership
- Latest feature window: `theta_fc`, `alpha_post`, the seven validity conditions, QC flags
- Shadow Mode: level, gates, counters, montage and controller versions
- QC diagnostics: grouped rejection breakdown and the dominant cause
- **Session EEG summary**: descriptive counts only — windows observed, how many passed
  `featureValidity`, how many were rejected, rows written, distinct causes, dominant cause
- Generated data files for the current session
- Recent events (`Phase at event` is the phase recorded at the moment each event was logged,
  not the current phase)
- **Participant view** — a reserved placeholder, see below

**Session review tab** — recorded sessions: metadata, file inventory, event table, shadow
decision summary, and the persisted `shadow_diagnostics.txt`.

---

## What is real and what is not

| Panel | Live (Unity running) | Mock | Offline review |
|---|---|---|---|
| Session / phase / elapsed | ✅ real | simulated | ✅ real (from CSV) |
| EEG acquisition status, samples, windows | ✅ real | simulated | — |
| Channel quality (8 electrodes) | ✅ real | simulated | — |
| Feature window, theta_fc, alpha_post | ✅ real | simulated | — |
| Shadow mode, level, gates | ✅ real | simulated | ✅ real (from CSV) |
| Rejection breakdown | ✅ real | simulated | ✅ real (from diagnostics file) |
| Session EEG summary (counts) | ✅ real | simulated | — |
| Recent events | ✅ real (tailed CSV) | simulated | ✅ real |
| Data files | ✅ real | simulated | ✅ real |
| Participant view | **not implemented** — placeholder | placeholder | — |
| Raw EEG waveform display | **not implemented, and not planned here** — use AURA | — | — |
| Raw EEG waveforms from file | not implemented — later phase | — | size only, never loaded |

"Real" above means the value is read from a Unity-written snapshot or a recorded file. The
live path has been exercised with a real Unity Play Mode session; **it has not been validated
with AURA hardware attached**, and no panel has yet displayed real EEG features.

`theta_fc` is `mean(F3, Fz, F4, Cz)` as of 2026-09-16. Sessions recorded before that carry a
purely frontal value under the same name and are not comparable on that feature.

Sessions recorded before `shadow_diagnostics.txt` was introduced will not have one; the
offline panel says so rather than showing an empty box.

---

## The session EEG summary

Counts, not signal. Windows observed, how many passed `featureValidity`, how many were
rejected and at what percentage, rows written, the number of distinct rejection causes, and
the dominant one.

Every figure is arithmetic over the tally the Shadow controller already keeps. There is no
interpretation, no threshold, and no statement about whether the data is usable — that
judgement stays with the researcher.

## The participant view

A **placeholder**. Real VR spectator streaming is **not implemented**: no capture, no image
path, no polling. The panel exists so the space is reserved and labelled rather than empty.

`ARCHITECTURE.md` compares four approaches and recommends a 1 Hz low-resolution spectator
camera, conditional on someone first measuring its frame-time cost during a real VR session.
That remains **future work**.

---

## Removed

The **Live EEG Monitor — Display Only** panel was removed on 2026-09-25, together with:

- the `/api/eeg` endpoint (now returns 404)
- the incremental `?since=N` sample protocol
- the synthetic waveform generator that backed it in mock mode
- the canvas renderer and its client-side ring buffer

**Reason:** AURA already displays raw live EEG, and it is the instrument's own validated
display. A second waveform in the browser duplicated it at a lower rate, occupied the largest
panel on the page, and risked being read as the analysed signal when it was neither filtered
nor part of the analysis path.

The space is now the participant-view placeholder. The waveform architecture assessment is
kept in `ARCHITECTURE.md` as a record of the decision, not as pending work.
