#!/usr/bin/env python3
"""
Local, read-only server for the IKEA_EEG Researcher Dashboard.

WHAT THIS IS
    A ~300-line static file server with four read-only JSON endpoints. Python standard
    library only: no pip install, no virtualenv, no build step, no framework.

WHY A SEPARATE PROCESS
    Unity publishes a status snapshot to a JSON file and nothing more. It opens no socket,
    accepts no connection and answers no request, so a dashboard that is closed, crashed or
    never started cannot affect a participant session in any way. This process reads that
    file and the recorded session folders. It is a READER. It has no write path at all --
    every handler below is a GET, and nothing in this file opens a file for writing.

SAFETY
    Binds 127.0.0.1 only, so nothing outside this machine can reach it.
    Serves only: the dashboard's own directory, and files inside the IKEA_EEG data root.
    Refuses raw_eeg.csv bodies -- they run to hundreds of MB and belong in a later phase.
    Reports file sizes instead.

USAGE
    python serve.py                 live status + offline session review
    python serve.py --mock          synthetic data for UI work with no hardware
    python serve.py --port 8770     a different port
"""

import argparse
import json
import math
import os
import random
import re
import sys
import time
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse, unquote

HERE = Path(__file__).resolve().parent

# Unity's Application.persistentDataPath on Windows.
PERSISTENT = Path(os.environ.get("LOCALAPPDATA", "")).parent / "LocalLow" / "DefaultCompany" / "IKEA_EEG"
DATA_ROOT = PERSISTENT / "IKEA_EEG_Data"
STATUS_FILE = PERSISTENT / "IKEA_EEG_Dashboard" / "status.json"

SESSION_RE = re.compile(r"^S_\d{8}_\d{6}_r\d{2}_[0-9a-f]{6}$")

# Files the dashboard reports on per session. raw_eeg.csv is listed but never served.
KNOWN_FILES = [
    ("events", "events_*.csv", "Behavioural event log (45 columns)"),
    ("raw_eeg", "raw_eeg*.csv", "Raw EEG samples + provenance header"),
    ("shadow_decisions", "shadow_decisions.csv", "Shadow decisions (frozen 15 columns)"),
    ("shadow_diagnostics", "shadow_diagnostics.txt", "Session-end rejection breakdown"),
    ("session_summary", "session_summary*.csv", "Session summary"),
    ("researcher_summary", "researcher_summary*.txt", "Researcher summary"),
]

MOCK = False

# ---- Display-only EEG monitor -------------------------------------------------------------
# A VIEWING rate, not an analysis rate. The scientific path reads the ring buffer at the
# stream's own nominal rate and is untouched by anything here; this exists so a researcher can
# see that the electrodes are producing a trace. Nothing computed from this is ever a feature.
EEG_DISPLAY_RATE = 50.0          # Hz, decimated for the browser
EEG_WINDOW_SECONDS = 10.0        # visible span
EEG_LABELS = ["Fp1", "F3", "Fz", "F4", "Cz", "P3", "Pz", "P4"]



# ----------------------------------------------------------------------------- mock data

class MockClock:
    """
    Generates a plausible-looking live session.

    Every payload it produces carries data_source = "MOCK". The dashboard keys its banner and
    its accent colour off that single field, so a mock payload cannot be rendered as a live
    one even by accident -- there is no code path in the page that treats an unrecognised
    data_source as live.
    """

    PHASES = [
        ("Familiarization", 40), ("PreTaskRest", 180), ("WordEncoding", 44),
        ("ImmediateRecognition", 120), ("ChairTask", 90), ("DelayedRecognition", 110),
        ("PostTaskRest", 180), ("Results", 30),
    ]

    def __init__(self):
        self.t0 = time.time()
        self.session_id = "S_20260916_MOCKED_r01_dem0da"
        self.rng = random.Random(20260916)

    def phase(self, elapsed):
        acc = 0
        for name, length in self.PHASES:
            if elapsed < acc + length:
                return name
            acc += length
        return "Complete"

    def payload(self):
        elapsed = (time.time() - self.t0) % 794
        srate = 250.0
        samples = int(elapsed * srate)
        windows = max(0, int(elapsed / 4.0))
        phase = self.phase(elapsed)

        # After the first few minutes one posterior electrode drifts, so the channel panel and
        # the ROI counters have something to show. Deterministic, not random per request.
        degraded = []
        if elapsed > 300:
            degraded = ["P3"]
        if elapsed > 480:
            degraded = ["P3", "P4"]

        labels = ["Fp1", "F3", "Fz", "F4", "Cz", "P3", "Pz", "P4"]
        rois = {
            "Fp1": "", "F3": "FRONTAL_THETA", "Fz": "FRONTAL_THETA",
            "F4": "FRONTAL_THETA", "Cz": "FRONTAL_THETA",
            "P3": "POSTERIOR_ALPHA", "Pz": "POSTERIOR_ALPHA", "P4": "POSTERIOR_ALPHA",
        }
        channels = [
            {
                "index": i + 1,
                "label": lab,
                "state": "unknown" if windows == 0 else ("degraded" if lab in degraded else "healthy"),
                "rois": rois[lab],
            }
            for i, lab in enumerate(labels)
        ]

        in_roi = sum(1 for d in degraded if rois[d])
        theta = 240 + 120 * math.sin(elapsed / 37.0) + self.rng.uniform(-8, 8)
        alpha = 42 + 26 * math.sin(elapsed / 23.0) + self.rng.uniform(-3, 3)

        near_identical = elapsed > 120
        flags = "FLAGGED: NearIdenticalChannels" if near_identical else "PASS"

        # featureValidity is DERIVED from the simulated conditions, never asserted
        # independently. A demo that shows "all checks passed" beside "INVALID" teaches
        # the reader to distrust the panel, which is the opposite of the point.
        feature_valid = (not near_identical) and (not degraded)

        return {
            "data_source": "MOCK",
            "written_utc": datetime.now(timezone.utc).isoformat(),
            "session": {
                "session_id": self.session_id,
                "experiment_session_id": "E_20260916_MOCKED_dem0da",
                "run_index": 1,
                "active": True,
                "phase": phase,
                "room": "AreaA",
                "directory": str(DATA_ROOT / self.session_id) + "   [SIMULATED PATH]",
                "root_folder": "IKEA_EEG_Data",
                "elapsed_seconds": elapsed,
            },
            "acquisition": {
                "stream_name": "AURA",
                "connected": True,
                "detail": "SIMULATED STREAM - no hardware attached",
                "samples_received": samples,
                "nominal_srate": srate,
                "stream_channels": 8,
                "time_correction": -0.00042,
                "windows_published": windows,
            },
            "channels": channels,
            "window": {
                "present": windows > 0,
                "sample_count": 1000,
                "channel_count": 8,
                "lsl_start": 10655.43 + windows * 3.98,
                "lsl_end": 10659.43 + windows * 3.98,
                "theta_fc": theta,
                "alpha_post": alpha,
                "feature_validity": feature_valid,
                "roi_valid": in_roi == 0,
                "fc_roi_valid": True,
                "post_roi_valid": in_roi == 0,
                "filter_settled": True,
                "window_complete": True,
                "spectral_valid": True,
                "channel_checks": True,
                "cross_channel": not near_identical,
                "channel_health": not degraded,
                "degraded_in_roi": in_roi,
                "degraded_outside_roi": len(degraded) - in_roi,
                "valid_channels": 8 - len(degraded),
                "near_identical_pairs": 15 if near_identical else 0,
                "near_identical_detail": (
                    "near-identical channels: 15 channel pairs correlate at or above 0.9900 "
                    "on filtered signal (Cz~Pz r=0.99981, P3~Pz r=0.99959, +13 more) "
                    "[SIMULATED]" if near_identical else ""),
                "degraded_detail": "; ".join(f"{d} since 10700.000 (simulated)" for d in degraded),
                "flags": flags,
                "breakdown": (
                    f"filter_settled=True; window_complete=True; spectral_valid=True; "
                    f"channel_checks=True; cross_channel={not near_identical}; "
                    f"channel_health={not degraded}; valid_channels={8 - len(degraded)}; "
                    f"degraded_in_roi={in_roi}; degraded_outside_roi={len(degraded) - in_roi}; "
                    f"feature_valid={feature_valid}; flags={flags}"),
                "power_units": "AURA-native-units²",
            },
            "shadow": {
                "present": True,
                "subscribed": True,
                "windows_observed": windows,
                "decisions_generated": windows,
                "rows_written": windows,
                "controller_version": "shadow-1.1.0-phase1",
                "montage_status": "PHYSICALLY_VERIFIED_2026-09-15",
                "level": "INDETERMINATE",
                "rejected_reason": "FeatureInvalid",
                "baseline_valid": False,
                # Kept internally consistent with the window above: before the simulated
                # bridging starts, windows fail on channel health instead. A demo that
                # contradicts itself teaches the reader to distrust the panel.
                "rejections": [
                    {"cause": f"FeatureInvalid :: cross_channel={not near_identical}; "
                              f"channel_health={not degraded}; degraded_in_roi={in_roi}; "
                              f"flags={flags}",
                     "count": max(0, windows - 1)},
                    {"cause": "FeatureInvalid :: filter_settled=False; window_complete=False; "
                              "flags=FLAGGED: FilterNotSettled, InsufficientSamples",
                     "count": 1 if windows else 0},
                ],
            },
        }


class MockEeg:
    """
    Deterministic synthetic EEG for UI development.

    Every sample is a pure function of its absolute index, so a client can poll with
    ?since=N and append only what is new without the trace ever jumping or duplicating --
    the same incremental contract a real implementation would use.

    This is NOT filtered, NOT analysed and NOT the scientific path. It is a picture.
    """

    def __init__(self):
        self.t0 = time.time()

    def current_index(self):
        return int((time.time() - self.t0) * EEG_DISPLAY_RATE)

    @staticmethod
    def _noise(n, ch):
        # Cheap deterministic hash -> [-0.5, 0.5). Reproducible across requests.
        h = (n * 2654435761 + ch * 40503 + 12345) & 0xFFFFFFFF
        h ^= (h >> 13)
        h = (h * 1274126177) & 0xFFFFFFFF
        return ((h >> 8) & 0xFFFF) / 65536.0 - 0.5

    def sample(self, n, ch):
        t = n / EEG_DISPLAY_RATE
        posterior = ch >= 5          # P3, Pz, P4
        frontocentral = 1 <= ch <= 4  # F3, Fz, F4, Cz

        v = 3.0 * math.sin(2 * math.pi * 0.23 * t + ch)             # slow common drift
        v += (11.0 if posterior else 3.0) * math.sin(
            2 * math.pi * 10.1 * t + ch * 0.7)                       # alpha, posterior-weighted
        v += (9.0 if frontocentral else 3.5) * math.sin(
            2 * math.pi * 6.2 * t + ch * 1.3)                        # theta, frontocentral-weighted
        v += 2.5 * math.sin(2 * math.pi * 21.0 * t + ch * 2.1)       # low beta
        v += 6.0 * self._noise(n, ch)

        # Fp1 carries blink-like transients, as the frontmost electrode does in practice.
        if ch == 0:
            blink = t % 7.0
            if blink < 0.32:
                v += 55.0 * math.sin(math.pi * blink / 0.32)

        return round(v, 3)

    def window(self, since):
        latest = self.current_index()
        first_possible = max(0, latest - int(EEG_WINDOW_SECONDS * EEG_DISPLAY_RATE))

        if since is None or since < first_possible or since > latest:
            start = first_possible
        else:
            start = since

        rows = [[self.sample(n, ch) for ch in range(8)] for n in range(start, latest)]

        return {
            "simulated": True,
            "available": True,
            "rate": EEG_DISPLAY_RATE,
            "window_seconds": EEG_WINDOW_SECONDS,
            "channels": EEG_LABELS,
            "start_index": start,
            "next_index": latest,
            "units": "arbitrary display units (SIMULATED)",
            "note": "Synthetic trace for UI development. Not filtered, not analysed, "
                    "not the scientific path.",
            "samples": rows,
        }


MOCK_EEG = MockEeg()
MOCK_CLOCK = MockClock()


def mock_events(limit):
    base = time.time() - 300
    kinds = [
        ("SESSION_START", "Familiarization"), ("PRE_TASK_REST_START", "PreTaskRest"),
        ("PRE_TASK_REST_END", "PreTaskRest"), ("WORD_ENCODING_START", "WordEncoding"),
        ("WORD_PRESENTED", "WordEncoding"), ("WORD_OFFSET", "WordEncoding"),
        ("RECOGNITION_ITEM_ONSET", "ImmediateRecognition"),
        ("RECOGNITION_ITEM_TIMEOUT", "ImmediateRecognition"),
        ("NARRATION_STOPPED", "WordEncoding"),
    ]
    rng = random.Random(7)
    rows = []
    for i in range(limit):
        kind, phase = kinds[i % len(kinds)]
        rows.append({
            "timestamp": datetime.fromtimestamp(base + i * 2.3).strftime("%H:%M:%S"),
            "event": kind,
            "phase": phase,
            "lsl": round(10600 + i * 2.3 + rng.uniform(0, 0.4), 3),
        })
    return list(reversed(rows))


# ------------------------------------------------------------------------- session review

def is_session_name(name):
    return bool(SESSION_RE.match(name))


def session_path(session_id):
    """Resolve a session id to a folder, refusing anything that escapes the data root."""
    if not is_session_name(session_id):
        return None
    candidate = (DATA_ROOT / session_id).resolve()
    try:
        candidate.relative_to(DATA_ROOT.resolve())
    except ValueError:
        return None
    return candidate if candidate.is_dir() else None


def human_size(n):
    for unit in ("B", "KB", "MB", "GB"):
        if n < 1024 or unit == "GB":
            return f"{n:.0f} {unit}" if unit == "B" else f"{n / 1:.1f} {unit}".replace(".0 ", " ")
        n /= 1024.0
    return f"{n:.1f} GB"


def list_sessions(limit=400):
    if not DATA_ROOT.is_dir():
        return []
    out = []
    for entry in DATA_ROOT.iterdir():
        if not entry.is_dir() or not is_session_name(entry.name):
            continue
        try:
            files = list(entry.iterdir())
        except OSError:
            continue
        total = sum(f.stat().st_size for f in files if f.is_file())
        has_eeg = any(f.name.startswith("raw_eeg") for f in files)
        has_shadow = any(f.name == "shadow_decisions.csv" for f in files)
        out.append({
            "session_id": entry.name,
            "files": len(files),
            "bytes": total,
            "size": human_size(total),
            "has_eeg": has_eeg,
            "has_shadow": has_shadow,
            "modified": entry.stat().st_mtime,
        })
    out.sort(key=lambda r: r["session_id"], reverse=True)
    return out[:limit]


def read_csv_tail(path, limit):
    """Last `limit` data rows of a CSV, plus its header. Streams -- never loads the file."""
    try:
        with path.open("r", encoding="utf-8", errors="replace") as fh:
            header = fh.readline().rstrip("\n").split(",")
            tail, count = [], 0
            for line in fh:
                if line.startswith("#") or not line.strip():
                    continue
                count += 1
                tail.append(line.rstrip("\n"))
                if len(tail) > limit:
                    tail.pop(0)
    except OSError as exc:
        return {"error": str(exc)}
    return {"header": header, "rows": [r.split(",") for r in tail], "total_rows": count}


def count_lines(path, cap=2_000_000):
    """Line count without holding the file in memory. Capped so a huge raw file can't stall."""
    n = 0
    try:
        with path.open("rb") as fh:
            for _ in fh:
                n += 1
                if n >= cap:
                    break
    except OSError:
        return 0
    return n


def session_detail(session_id):
    folder = session_path(session_id)
    if folder is None:
        return {"error": f"unknown session: {session_id}"}

    present = []
    for key, pattern, description in KNOWN_FILES:
        matches = sorted(folder.glob(pattern))
        if matches:
            f = matches[0]
            stat = f.stat()
            present.append({
                "key": key, "name": f.name, "description": description,
                "bytes": stat.st_size, "size": human_size(stat.st_size), "present": True,
            })
        else:
            present.append({
                "key": key, "name": pattern, "description": description,
                "bytes": 0, "size": "-", "present": False,
            })

    detail = {"session_id": session_id, "directory": str(folder), "files": present}

    events = sorted(folder.glob("events_*.csv"))
    if events:
        parsed = read_csv_tail(events[0], 120)
        header = parsed.get("header", [])
        try:
            i_time = header.index("timestamp_absolute")
            i_event = header.index("event_type")
            i_state = header.index("experiment_state")
            i_lsl = header.index("lsl_timestamp")
        except ValueError:
            i_time = i_event = i_state = i_lsl = -1
        rows = []
        if i_event >= 0:
            for r in reversed(parsed.get("rows", [])):
                if len(r) <= max(i_time, i_event, i_state, i_lsl):
                    continue
                stamp = r[i_time]
                rows.append({
                    "timestamp": stamp[-12:] if len(stamp) > 12 else stamp,
                    "event": r[i_event], "phase": r[i_state], "lsl": r[i_lsl],
                })
        detail["events"] = {"recent": rows, "total": parsed.get("total_rows", 0)}

    shadow = folder / "shadow_decisions.csv"
    if shadow.is_file():
        parsed = read_csv_tail(shadow, 1)
        header = parsed.get("header", [])
        summary = {"total_rows": parsed.get("total_rows", 0), "reasons": {}, "valid_channels": {}}
        try:
            i_reason = header.index("rejected_reason")
            i_valid = header.index("n_valid_channels")
        except ValueError:
            i_reason = i_valid = -1
        if i_reason >= 0:
            try:
                with shadow.open("r", encoding="utf-8", errors="replace") as fh:
                    next(fh, None)
                    for line in fh:
                        parts = line.rstrip("\n").split(",")
                        if len(parts) <= max(i_reason, i_valid):
                            continue
                        summary["reasons"][parts[i_reason]] = \
                            summary["reasons"].get(parts[i_reason], 0) + 1
                        summary["valid_channels"][parts[i_valid]] = \
                            summary["valid_channels"].get(parts[i_valid], 0) + 1
            except OSError:
                pass
        detail["shadow"] = summary

    diag = folder / "shadow_diagnostics.txt"
    if diag.is_file():
        try:
            detail["diagnostics"] = diag.read_text(encoding="utf-8", errors="replace")[:20000]
        except OSError:
            pass

    raw = sorted(folder.glob("raw_eeg*.csv"))
    if raw:
        stat = raw[0].stat()
        detail["raw_eeg"] = {
            "name": raw[0].name, "bytes": stat.st_size, "size": human_size(stat.st_size),
            "note": "Not loaded. Raw sample visualisation is a later phase.",
        }

    return detail


def live_status():
    if MOCK:
        return MOCK_CLOCK.payload()
    if not STATUS_FILE.is_file():
        return {
            "data_source": "NONE",
            "reason": "No status.json yet. Enter Play Mode in the Unity Editor -- the writer "
                      "creates it automatically and updates it four times a second.",
            "expected_path": str(STATUS_FILE),
        }
    try:
        payload = json.loads(STATUS_FILE.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        return {"data_source": "NONE", "reason": f"status.json unreadable: {exc}"}
    payload["age_seconds"] = max(0.0, time.time() - STATUS_FILE.stat().st_mtime)
    return payload


def live_events(limit=40):
    """Recent events for the live panel, tailed from the active session's own CSV."""
    if MOCK:
        return {"recent": mock_events(limit), "total": 1462}
    status = live_status()
    directory = (status.get("session") or {}).get("directory", "")
    if not directory:
        return {"recent": [], "total": 0}
    folder = Path(directory)
    if not folder.is_dir():
        return {"recent": [], "total": 0}
    events = sorted(folder.glob("events_*.csv"))
    if not events:
        return {"recent": [], "total": 0}
    parsed = read_csv_tail(events[0], limit)
    header = parsed.get("header", [])
    try:
        i_time = header.index("timestamp_absolute")
        i_event = header.index("event_type")
        i_state = header.index("experiment_state")
        i_lsl = header.index("lsl_timestamp")
    except ValueError:
        return {"recent": [], "total": parsed.get("total_rows", 0)}
    rows = []
    for r in reversed(parsed.get("rows", [])):
        if len(r) <= max(i_time, i_event, i_state, i_lsl):
            continue
        stamp = r[i_time]
        rows.append({
            "timestamp": stamp[-12:] if len(stamp) > 12 else stamp,
            "event": r[i_event], "phase": r[i_state], "lsl": r[i_lsl],
        })
    return {"recent": rows, "total": parsed.get("total_rows", 0)}


def live_files():
    """Which files the ACTIVE session has produced so far."""
    if MOCK:
        return {"session_id": MOCK_CLOCK.session_id, "files": [
            {"key": k, "name": p, "description": d, "present": k != "shadow_diagnostics",
             "size": "simulated", "bytes": 0}
            for k, p, d in KNOWN_FILES]}
    status = live_status()
    session = (status.get("session") or {})
    sid = session.get("session_id", "")
    if sid and is_session_name(sid):
        return session_detail(sid)
    return {"session_id": sid, "files": []}


def eeg_window(query):
    """
    Recent samples for the display-only EEG monitor.

    MOCK ONLY. In live mode this reports that no display feed exists rather than inventing
    one: wiring it to real acquisition is an architecture decision that has not been taken,
    and a panel that silently fell back to synthetic data would be the worst possible
    outcome in a scientific tool.
    """
    if not MOCK:
        return {
            "simulated": False,
            "available": False,
            "reason": "No live EEG display feed. The status writer does not yet publish "
                      "waveform samples; see the architecture assessment before enabling one.",
            "channels": EEG_LABELS,
        }

    since = None
    for part in (query or "").split("&"):
        if part.startswith("since="):
            try:
                since = int(part[6:])
            except ValueError:
                since = None
    return MOCK_EEG.window(since)


# -------------------------------------------------------------------------------- server

class Handler(BaseHTTPRequestHandler):
    server_version = "IkeaEegDashboard/0.1"

    def log_message(self, fmt, *args):
        pass  # the console belongs to the researcher, not to request noise

    def _send(self, code, body, content_type):
        payload = body.encode("utf-8") if isinstance(body, str) else body
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(payload)

    def _json(self, obj, code=200):
        self._send(code, json.dumps(obj, default=str), "application/json; charset=utf-8")

    def do_GET(self):
        route = unquote(urlparse(self.path).path)

        if route in ("/", "/index.html"):
            page = HERE / "index.html"
            if not page.is_file():
                return self._send(500, "index.html is missing next to serve.py", "text/plain")
            return self._send(200, page.read_bytes(), "text/html; charset=utf-8")

        if route == "/api/status":
            return self._json(live_status())
        if route == "/api/events":
            return self._json(live_events())
        if route == "/api/files":
            return self._json(live_files())
        if route == "/api/eeg":
            return self._json(eeg_window(urlparse(self.path).query))
        if route == "/api/sessions":
            # Real folders even in mock mode. Mock replaces the LIVE panel, which has no
            # hardware behind it; recorded sessions on disk are real either way, and hiding
            # them would make offline review untestable without AURA.
            return self._json({"data_root": str(DATA_ROOT),
                               "mock": MOCK,
                               "sessions": list_sessions()})
        if route.startswith("/api/session/"):
            return self._json(session_detail(route[len("/api/session/"):]))
        if route == "/api/meta":
            return self._json({
                "mock": MOCK,
                "data_root": str(DATA_ROOT),
                "status_file": str(STATUS_FILE),
                "status_exists": STATUS_FILE.is_file(),
                "data_root_exists": DATA_ROOT.is_dir(),
            })

        self._send(404, "not found", "text/plain")

    # No do_POST, do_PUT or do_DELETE. This server cannot be asked to change anything.


def main():
    global MOCK
    ap = argparse.ArgumentParser(description="IKEA_EEG Researcher Dashboard (read-only)")
    ap.add_argument("--port", type=int, default=8760)
    ap.add_argument("--mock", action="store_true",
                    help="serve simulated data for UI development without AURA")
    args = ap.parse_args()
    MOCK = args.mock

    banner = "  *** MOCK / DEMO DATA - NOT REAL EEG ***" if MOCK else "  live mode"
    print("IKEA_EEG Researcher Dashboard" + banner)
    print(f"  data root   : {DATA_ROOT}  {'(found)' if DATA_ROOT.is_dir() else '(MISSING)'}")
    print(f"  status file : {STATUS_FILE}  {'(found)' if STATUS_FILE.is_file() else '(not yet)'}")
    print(f"  open        : http://127.0.0.1:{args.port}/")
    print("  read-only. Ctrl+C to stop.")

    try:
        ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()
    except KeyboardInterrupt:
        print("\nstopped")
        return 0
    except OSError as exc:
        print(f"could not start: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
