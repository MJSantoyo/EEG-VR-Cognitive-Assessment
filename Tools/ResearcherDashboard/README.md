# IKEA_EEG — Researcher Dashboard (Investigator Console) v0

A local, **read-only** monitor for a running or recorded IKEA_EEG session.

It observes. It controls nothing. There is no control in this interface for a threshold, a
filter, an ROI, a baseline, a normalization formula, a workload level or task difficulty, and
the server that backs it has no write path — every handler is a GET.

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

Simulated values for UI work. A permanent amber banner, an amber outline around the whole
page and an amber accent colour make it impossible to mistake for real data. The **Session
review** tab still reads your real recorded sessions in this mode.

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

---

## What is real and what is not

| Panel | Live (Unity running) | Mock | Offline review |
|---|---|---|---|
| Session / phase / elapsed | ✅ real | simulated | ✅ real (from CSV) |
| EEG acquisition, samples, windows | ✅ real | simulated | — |
| Channel quality (8 electrodes) | ✅ real | simulated | — |
| Feature window, theta_fc, alpha_post | ✅ real | simulated | — |
| Shadow mode, level, gates | ✅ real | simulated | ✅ real (from CSV) |
| Rejection breakdown | ✅ real | simulated | ✅ real (from diagnostics file) |
| Recent events | ✅ real (tailed CSV) | simulated | ✅ real |
| Data files | ✅ real | simulated | ✅ real |
| Live EEG monitor (waveforms) | **not connected** — see `ARCHITECTURE.md` | ✅ synthetic trace, labelled SIMULATED | not implemented |
| VR participant view | **not implemented** — placeholder only | placeholder only | — |
| Raw EEG waveforms from file | not implemented — later phase | — | size only, never loaded |

`theta_fc` is `mean(F3, Fz, F4, Cz)` as of 2026-09-16. Sessions recorded before that carry a
purely frontal value under the same name and are not comparable on that feature.

Sessions recorded before `shadow_diagnostics.txt` was introduced will not have one; the
offline panel says so rather than showing an empty box.

---

## The EEG monitor panel

Mock data only. `/api/eeg` returns `available: false` in live mode rather than inventing a
trace — wiring it to real acquisition is an architecture decision that has not been taken.
See **`ARCHITECTURE.md`** for the assessment and the recommendation (option A: the status
writer publishes a decimated snapshot; a second LSL consumer is rejected outright).

The wire protocol is incremental: the browser polls `/api/eeg?since=N` and receives only new
samples, so a poll costs ~4 KB rather than re-sending the whole window.

Whatever it displays is **unfiltered and decimated for viewing**. It is a picture of the
stream, not the analysis path, and it is labelled as such on the panel itself.

## The VR participant view

A placeholder card only. Real spectator streaming is **not implemented**; `ARCHITECTURE.md`
compares four approaches and recommends a 1 Hz low-resolution spectator camera, conditional on
someone first measuring its frame-time cost during a real VR session.
