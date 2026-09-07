using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using IkeaEeg.Data;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Cuts ONE event-centred epoch out of an ALREADY RECORDED raw_eeg CSV and reports what it
    /// actually contains.
    ///
    /// WHY THIS EXISTS SEPARATELY FROM <see cref="EegDiagnostics"/>. The existing
    /// IKEA_EEG > EEG > Test Event Window is a LIVE test: it connects a temporary
    /// AuraLslReceiver, collects a few seconds from a running amplifier, and cuts a window
    /// around a SYNTHETIC event stamped LslClock.Now(). That answers "can this machine align an
    /// event to a live stream". It cannot answer "did THAT recorded session, at THAT real
    /// event, produce a usable epoch" — it has no way to open a file and no way to be given a
    /// timestamp.
    ///
    /// WHAT IS DELIBERATELY NOT DUPLICATED HERE. The epoch extraction itself. This class parses
    /// a CSV into the EXISTING <see cref="RawEegRingBuffer"/> and then calls the EXISTING
    /// <see cref="RawEegRingBuffer.TryGetWindow"/> — the same method the live path and the
    /// runtime feature pipeline use. If the extractor were wrong, this tool would be wrong in
    /// exactly the same way, which is the point: it validates the real path rather than a
    /// second implementation that could agree with nothing.
    ///
    /// Nothing here filters, scales, re-references or analyses. No filter, no Welch, no band
    /// definition and no marker/CSV schema is touched. It reads a file and counts.
    /// </summary>
    public static class EegRecordedWindowValidator
    {
        // ---- Defaults: the HARBOR validation case ------------------------------------------
        // Prefilled so the menu item runs the requested check with no configuration. They are
        // starting values for the window, not constants of the system — the window dialog can
        // be pointed at any recorded session and any event timestamp.

        const string k_DefaultSessionId = "S_20260901_184525_r01_88cb0d";
        const double k_DefaultEventTimestamp = 31591.099233d;
        const string k_DefaultLabel = "HARBOR / IMMEDIATE";
        const double k_DefaultPreSeconds = 1.0d;
        const double k_DefaultPostSeconds = 2.0d;

        /// <summary>
        /// The de-jittered analysis time base. The recorded file's own header names this as the
        /// column to cut epochs with.
        /// </summary>
        public const string AnalysisColumn = "lsl_timestamp_analysis";

        /// <summary>
        /// This machine's unsmoothed LSL clock — the SAME clock the event CSV's lsl_timestamp
        /// column carries. Offered so the two can be compared rather than assumed equivalent.
        /// </summary>
        public const string LocalRawColumn = "lsl_timestamp_local_raw";

        [MenuItem("IKEA_EEG/EEG/Validate Recognition Event Window", false, 145)]
        public static void ValidateMenu()
        {
            EegRecordedWindowValidatorWindow.Open();
        }

        /// <summary>Batch-mode entry point. Runs the prefilled HARBOR case.</summary>
        public static void ValidateFromCommandLine()
        {
            var request = DefaultRequest();
            Debug.Log(BuildReport(request, out _));
        }

        /// <summary>
        /// Batch-mode regression run across both recorded sessions: the one that worked and the
        /// one whose analysis column was written in the wrong clock domain.
        ///
        /// Exists so the fix can be re-proved against real data on demand rather than only in
        /// the synthetic self-test.
        /// </summary>
        public static void ValidateBothSessionsFromCommandLine()
        {
            var cases = new[]
            {
                (session: "S_20260901_184525_r01_88cb0d", ts: 31591.099233d,
                 label: "HARBOR / IMMEDIATE (previous session)", reconstruct: false),
                (session: "S_20260902_131219_r01_27eda8", ts: 97963.935270d,
                 label: "PICTURE / IMMEDIATE (new session, AS RECORDED)", reconstruct: false),
                (session: "S_20260902_131219_r01_27eda8", ts: 97963.935270d,
                 label: "PICTURE / IMMEDIATE (new session, RECONSTRUCTED)", reconstruct: true),
            };

            foreach (var c in cases)
            {
                var request = DefaultRequest();
                request.sessionId = c.session;
                request.eventTimestamp = c.ts;
                request.label = c.label;
                request.reconstructAnalysis = c.reconstruct;

                Debug.Log(BuildReport(request, out _));
            }
        }

        public static Request DefaultRequest()
        {
            return new Request
            {
                sessionId = k_DefaultSessionId,
                eventTimestamp = k_DefaultEventTimestamp,
                preSeconds = k_DefaultPreSeconds,
                postSeconds = k_DefaultPostSeconds,
                label = k_DefaultLabel,
                timestampColumn = AnalysisColumn,
                compareWithLocalRaw = true,
            };
        }

        /// <summary>What to validate. Every field is editable in the window.</summary>
        public struct Request
        {
            public string sessionId;
            public double eventTimestamp;
            public double preSeconds;
            public double postSeconds;
            public string label;

            /// <summary>Which recorded time base to cut on.</summary>
            public string timestampColumn;

            /// <summary>Also cut on the raw local clock, to show how far the two disagree.</summary>
            public bool compareWithLocalRaw;

            /// <summary>
            /// Rebuild the analysis timeline from the recorded local_raw column using the
            /// current <see cref="EegAnalysisTimebase"/>, instead of trusting the column the
            /// recorder wrote.
            ///
            /// This is what makes a session recorded by a broken build usable: local_raw is
            /// correct in the file, so the de-jittered timeline can be re-derived offline. It is
            /// also the regression harness for the timebase itself — replaying a real recording
            /// through the real class.
            /// </summary>
            public bool reconstructAnalysis;
        }

        /// <summary>Whether a recorded analysis column can be trusted, and why not.</summary>
        public struct DomainCheck
        {
            public double medianOffsetSeconds;
            public double effectiveRateHz;
            public double nominalRateHz;
            public bool domainMatches;
            public bool ratePlausible;
            public string verdict;

            public bool ok => domainMatches && ratePlausible;
        }

        /// <summary>
        /// THE GUARD THAT WOULD HAVE CAUGHT THE BUG. Asks whether the recorded analysis column
        /// is in the same clock domain as local_raw, and whether it runs at the advertised rate.
        ///
        /// Both thresholds are derived from the stream's OWN nominal rate, never from any
        /// particular session's numbers: the analysis timeline must sit within one second of the
        /// raw local clock (transport jitter on this hardware reaches ~0.35 s, a clock-domain
        /// error is 10^5 s or more, so one second separates them by orders of magnitude at both
        /// ends), and its effective rate must be within 20% of nominal.
        /// </summary>
        public static DomainCheck CheckAnalysisDomain(Recording recording)
        {
            var n = recording.sampleCount;

            var check = new DomainCheck
            {
                nominalRateHz = recording.nominalRateHz,
            };

            var offsets = new double[n];

            for (var i = 0; i < n; i++)
                offsets[i] = recording.analysisTimestamps[i] - recording.localRawTimestamps[i];

            Array.Sort(offsets);
            check.medianOffsetSeconds = offsets[n / 2];

            var span = recording.analysisTimestamps[n - 1] - recording.analysisTimestamps[0];
            check.effectiveRateHz = n > 1 && span > 0d ? (n - 1) / span : 0d;

            check.domainMatches = Math.Abs(check.medianOffsetSeconds) <= 1.0d;

            check.ratePlausible = recording.nominalRateHz <= 0d ||
                                  (check.effectiveRateHz >= recording.nominalRateHz * 0.8 &&
                                   check.effectiveRateHz <= recording.nominalRateHz * 1.2);

            if (check.ok)
            {
                check.verdict = "analysis column is in the local clock domain and runs at the " +
                                "advertised rate";
            }
            else if (!check.domainMatches)
            {
                check.verdict =
                    $"CLOCK-DOMAIN FAILURE — the analysis column sits " +
                    $"{check.medianOffsetSeconds:F3} s from the raw local clock. Event " +
                    "timestamps cannot be cut against it. Enable 'Reconstruct analysis from " +
                    "local_raw'.";
            }
            else
            {
                check.verdict =
                    $"RATE FAILURE — the analysis column runs at {check.effectiveRateHz:F2} Hz " +
                    $"against an advertised {recording.nominalRateHz:F2} Hz. The de-jitter grid " +
                    "was free-running. Enable 'Reconstruct analysis from local_raw'.";
            }

            return check;
        }

        /// <summary>
        /// Re-derives the analysis timeline from the recorded local_raw column, through the REAL
        /// <see cref="EegAnalysisTimebase"/>.
        ///
        /// Deliberately the production class and not a copy: a reconstruction that agreed with a
        /// private reimplementation would prove nothing about what the recorder will do next time.
        /// </summary>
        public static int ReconstructAnalysis(Recording recording, out EegAnalysisTimebase timebase)
        {
            timebase = null;

            if (recording.nominalRateHz <= 0d)
                return 0;

            timebase = new EegAnalysisTimebase(recording.nominalRateHz);

            for (var i = 0; i < recording.sampleCount; i++)
            {
                recording.analysisTimestamps[i] =
                    timebase.Add(recording.localRawTimestamps[i]);
            }

            return timebase.reanchorCount;
        }

        /// <summary>The parsed contents of one recorded raw_eeg CSV.</summary>
        public class Recording
        {
            public string path = string.Empty;
            public int channelCount;
            public double nominalRateHz;
            public string streamName = string.Empty;
            public string sourceId = string.Empty;

            /// <summary>True when the file's own header disclaims a verified electrode map.</summary>
            public bool montageUnverified;

            public readonly List<double> analysisTimestamps = new List<double>();
            public readonly List<double> localRawTimestamps = new List<double>();
            public readonly List<double[]> samples = new List<double[]>();

            public int sampleCount => samples.Count;
        }

        // ---------------------------------------------------------------------------------
        // Locating the file
        // ---------------------------------------------------------------------------------

        public static string DataRoot()
        {
            return Path.Combine(Application.persistentDataPath, "IKEA_EEG_Data");
        }

        /// <summary>
        /// The raw EEG file inside a session folder, whatever it is called.
        ///
        /// The name is NOT assumed: older runs wrote raw_eeg.csv and this session wrote
        /// raw_eeg_88cb0d.csv. Matching on the prefix means the tool keeps working across both
        /// rather than reporting "no EEG" for a file that is sitting right there.
        /// </summary>
        public static string FindRawEegFile(string sessionId, out string problem)
        {
            problem = string.Empty;

            var folder = Path.Combine(DataRoot(), sessionId);

            if (!Directory.Exists(folder))
            {
                problem = $"session folder not found: {folder}";
                return string.Empty;
            }

            var matches = Directory.GetFiles(folder, "raw_eeg*.csv");

            if (matches.Length == 0)
            {
                problem = $"no raw_eeg*.csv in {folder} — this run recorded no EEG";
                return string.Empty;
            }

            Array.Sort(matches);
            return matches[0];
        }

        // ---------------------------------------------------------------------------------
        // Parsing
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Reads the recorded CSV, taking channel count and nominal rate from the file's OWN
        /// header comments rather than from anything this tool assumes.
        /// </summary>
        public static Recording Load(string path, out string problem)
        {
            problem = string.Empty;

            var recording = new Recording { path = path };
            var columns = new Dictionary<string, int>(StringComparer.Ordinal);
            var analysisIndex = -1;
            var localRawIndex = -1;
            var firstChannelIndex = -1;

            try
            {
                using (var reader = new StreamReader(path))
                {
                    string line;

                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length == 0)
                            continue;

                        if (line[0] == '#')
                        {
                            ReadHeaderComment(line, recording);
                            continue;
                        }

                        // The first non-comment line is the column header.
                        if (columns.Count == 0)
                        {
                            var names = line.Split(',');

                            for (var i = 0; i < names.Length; i++)
                            {
                                var name = names[i].Trim();
                                columns[name] = i;

                                if (name == AnalysisColumn)
                                    analysisIndex = i;
                                else if (name == LocalRawColumn)
                                    localRawIndex = i;
                                else if (name.StartsWith("ch", StringComparison.Ordinal) &&
                                         firstChannelIndex < 0)
                                {
                                    firstChannelIndex = i;
                                }
                            }

                            if (analysisIndex < 0)
                            {
                                problem = $"the file has no {AnalysisColumn} column";
                                return null;
                            }

                            if (firstChannelIndex < 0)
                            {
                                problem = "the file has no ch1..chN columns";
                                return null;
                            }

                            continue;
                        }

                        var parts = line.Split(',');

                        if (parts.Length < firstChannelIndex + recording.channelCount)
                            continue;

                        if (!TryParse(parts[analysisIndex], out var analysis))
                            continue;

                        var localRaw = localRawIndex >= 0 &&
                                       TryParse(parts[localRawIndex], out var lr)
                            ? lr
                            : analysis;

                        var channels = new double[recording.channelCount];
                        var ok = true;

                        for (var c = 0; c < recording.channelCount; c++)
                        {
                            if (!TryParse(parts[firstChannelIndex + c], out channels[c]))
                            {
                                ok = false;
                                break;
                            }
                        }

                        if (!ok)
                            continue;

                        recording.analysisTimestamps.Add(analysis);
                        recording.localRawTimestamps.Add(localRaw);
                        recording.samples.Add(channels);
                    }
                }
            }
            catch (Exception e)
            {
                problem = $"could not read '{path}': {e.Message}";
                return null;
            }

            if (recording.sampleCount == 0)
            {
                problem = "the file contains no parseable sample rows";
                return null;
            }

            return recording;
        }

        static void ReadHeaderComment(string line, Recording recording)
        {
            var body = line.TrimStart('#', ' ');

            if (body.StartsWith("channel_count=", StringComparison.Ordinal) &&
                int.TryParse(body.Substring("channel_count=".Length), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var channels))
            {
                recording.channelCount = channels;
            }
            else if (body.StartsWith("nominal_srate_hz=", StringComparison.Ordinal) &&
                     TryParse(body.Substring("nominal_srate_hz=".Length), out var rate))
            {
                recording.nominalRateHz = rate;
            }
            else if (body.StartsWith("stream_name=", StringComparison.Ordinal))
            {
                recording.streamName = body.Substring("stream_name=".Length);
            }
            else if (body.StartsWith("stream_source_id=", StringComparison.Ordinal))
            {
                recording.sourceId = body.Substring("stream_source_id=".Length);
            }
            else if (body.IndexOf("No electrode mapping is claimed",
                         StringComparison.OrdinalIgnoreCase) >= 0)
            {
                recording.montageUnverified = true;
            }
        }

        static bool TryParse(string value, out double result)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                out result);
        }

        // ---------------------------------------------------------------------------------
        // Cutting the window — through the EXISTING extractor
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Fills a real <see cref="RawEegRingBuffer"/> from the recording and asks it for the
        /// window. The buffer is sized to hold the WHOLE file so nothing is overwritten.
        /// </summary>
        public static bool CutWindow(Recording recording, bool useLocalRaw, Request request,
            out EegWindow window, out RawEegRingBuffer buffer)
        {
            var timestamps = useLocalRaw
                ? recording.localRawTimestamps
                : recording.analysisTimestamps;

            var span = timestamps[timestamps.Count - 1] - timestamps[0];

            buffer = new RawEegRingBuffer(
                recording.channelCount,
                recording.nominalRateHz,
                capacitySeconds: Math.Max(1d, span + 10d));

            for (var i = 0; i < recording.sampleCount; i++)
                buffer.Add(timestamps[i], recording.samples[i]);

            return buffer.TryGetWindow(request.eventTimestamp, request.preSeconds,
                request.postSeconds, out window);
        }

        // ---------------------------------------------------------------------------------
        // The report
        // ---------------------------------------------------------------------------------

        public static string BuildReport(Request request, out EegWindow window)
        {
            window = default;

            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== RECORDED EVENT WINDOW VALIDATION =====");
            sb.AppendLine($"  label:   {request.label}");
            sb.AppendLine($"  session: {request.sessionId}");
            sb.AppendLine();

            var path = FindRawEegFile(request.sessionId, out var findProblem);

            if (string.IsNullOrEmpty(path))
            {
                sb.AppendLine($"  FAIL — {findProblem}");
                return sb.ToString();
            }

            sb.AppendLine($"  file:    {path}");

            var recording = Load(path, out var loadProblem);

            if (recording == null)
            {
                sb.AppendLine($"  FAIL — {loadProblem}");
                return sb.ToString();
            }

            sb.AppendLine($"  stream:  {recording.streamName} ({recording.sourceId})");
            sb.AppendLine($"  header:  channel_count={recording.channelCount}, " +
                          $"nominal_srate_hz={recording.nominalRateHz:F3}");
            sb.AppendLine($"  file samples: {recording.sampleCount} spanning " +
                          $"{recording.analysisTimestamps[recording.sampleCount - 1] - recording.analysisTimestamps[0]:F3} s");

            if (recording.montageUnverified)
            {
                sb.AppendLine("  montage: UNVERIFIED — the file states channels are in STREAM " +
                              "ORDER with no electrode map. Channels are reported as ch1..chN " +
                              "and are NOT labelled Fp1/F3/etc.");
            }

            sb.AppendLine();

            // ---- CLOCK-DOMAIN GUARD, before anything is cut ---------------------------------
            // A window cut against a timeline in the wrong clock domain either returns nothing
            // or, worse, returns something that looks entirely healthy. This is checked first
            // and reported whether it passes or fails.
            var domain = CheckAnalysisDomain(recording);

            sb.AppendLine("  ---- ANALYSIS TIME-BASE INTEGRITY ----");
            sb.AppendLine($"  median(analysis - local_raw): {domain.medianOffsetSeconds:F6} s");
            sb.AppendLine($"  effective rate of analysis column: {domain.effectiveRateHz:F4} Hz " +
                          $"(advertised {recording.nominalRateHz:F2} Hz)");
            sb.AppendLine($"  verdict: {(domain.ok ? "OK — " : "")}{domain.verdict}");
            sb.AppendLine();

            if (request.reconstructAnalysis)
            {
                var reanchors = ReconstructAnalysis(recording, out var timebase);

                sb.AppendLine("  ---- RECONSTRUCTED from " + LocalRawColumn + " ----");
                sb.AppendLine("  The recorded analysis column has been REPLACED in memory by a " +
                              "timeline re-derived");
                sb.AppendLine("  through the project's own EegAnalysisTimebase. The file on " +
                              "disk is not modified.");

                if (timebase != null)
                {
                    sb.AppendLine($"  re-anchors: {reanchors}   fitted rate: " +
                                  $"{timebase.fittedRateHz:F4} Hz   " +
                                  $"largest drift from raw: {timebase.largestDriftSeconds:F4} s");
                }

                var after = CheckAnalysisDomain(recording);
                sb.AppendLine($"  after reconstruction: median offset " +
                              $"{after.medianOffsetSeconds:F6} s, rate " +
                              $"{after.effectiveRateHz:F4} Hz — " +
                              $"{(after.ok ? "OK" : "STILL FAILING")}");
                sb.AppendLine();
            }
            else if (!domain.ok)
            {
                sb.AppendLine("  The epoch below is cut against a timeline that FAILED the " +
                              "integrity check.");
                sb.AppendLine("  Treat every number in it as unusable until reconstruction is " +
                              "enabled.");
                sb.AppendLine();
            }

            var useLocalRaw = string.Equals(request.timestampColumn, LocalRawColumn,
                StringComparison.Ordinal);

            sb.AppendLine($"  cutting on: {(useLocalRaw ? LocalRawColumn : AnalysisColumn)}");
            sb.AppendLine();

            if (!CutWindow(recording, useLocalRaw, request, out window, out var buffer))
            {
                sb.AppendLine("  FAIL — no sample fell inside the requested window. The event " +
                              "timestamp is outside this recording.");
                sb.AppendLine($"  recording covers " +
                              $"[{recording.analysisTimestamps[0]:F6} .. " +
                              $"{recording.analysisTimestamps[recording.sampleCount - 1]:F6}]");
                return sb.ToString();
            }

            AppendWindowReport(sb, window, buffer, recording, request,
                domain.ok || request.reconstructAnalysis);

            if (request.compareWithLocalRaw && !useLocalRaw)
            {
                sb.AppendLine();
                sb.AppendLine("  ---- CROSS-CHECK: the same cut on " + LocalRawColumn + " ----");
                sb.AppendLine("  The event CSV's lsl_timestamp is stamped on the LOCAL RAW clock,");
                sb.AppendLine("  while epochs are cut on the de-jittered ANALYSIS base. The two");
                sb.AppendLine("  are only interchangeable if they agree to well within a sample.");

                var offsets = 0d;
                for (var i = 0; i < recording.sampleCount; i++)
                    offsets += recording.analysisTimestamps[i] - recording.localRawTimestamps[i];

                var meanOffsetMs = offsets / recording.sampleCount * 1000d;
                var sampleIntervalMs = recording.nominalRateHz > 0d
                    ? 1000d / recording.nominalRateHz
                    : double.NaN;

                sb.AppendLine($"  mean (analysis - local_raw): {meanOffsetMs:F3} ms " +
                              $"against a {sampleIntervalMs:F3} ms sample interval");

                if (CutWindow(recording, true, request, out var rawWindow, out var rawBuffer))
                {
                    sb.AppendLine($"  local_raw cut: {rawWindow.sampleCount} samples, " +
                                  $"status {rawWindow.status}, " +
                                  $"monotonic {IsMonotonic(rawWindow)}");

                    var rawStats = Continuity(rawWindow, recording.nominalRateHz);
                    sb.AppendLine($"  local_raw gaps > 2x nominal inside the epoch: " +
                                  $"{rawStats.largeGaps}; smallest interval " +
                                  $"{rawStats.minIntervalMs:F3} ms");
                }
            }

            return sb.ToString();
        }

        static void AppendWindowReport(StringBuilder sb, EegWindow window,
            RawEegRingBuffer buffer, Recording recording, Request request, bool domainOk)
        {
            var evt = request.eventTimestamp;
            var stats = Continuity(window, recording.nominalRateHz);
            var expected = buffer.ExpectedSampleCount(request.preSeconds, request.postSeconds);

            sb.AppendLine("  ---- WINDOW ----");
            sb.AppendLine($"   1. event timestamp     : {evt.ToString("F6", CultureInfo.InvariantCulture)}");
            sb.AppendLine($"   2. requested window    : " +
                          $"{window.requestedStart.ToString("F6", CultureInfo.InvariantCulture)} .. " +
                          $"{window.requestedEnd.ToString("F6", CultureInfo.InvariantCulture)}   " +
                          $"(-{request.preSeconds:F1} s / +{request.postSeconds:F1} s)");
            sb.AppendLine($"   3. actual first sample : {window.firstTimestamp.ToString("F6", CultureInfo.InvariantCulture)}" +
                          $"   (rel {window.firstTimestamp - evt:+0.000000;-0.000000} s)");
            sb.AppendLine($"   4. actual last sample  : {window.lastTimestamp.ToString("F6", CultureInfo.InvariantCulture)}" +
                          $"   (rel {window.lastTimestamp - evt:+0.000000;-0.000000} s)");
            sb.AppendLine($"   5. samples             : {window.sampleCount}" +
                          $"   (expected ~{expected} at {recording.nominalRateHz:F0} Hz)");
            sb.AppendLine($"   6. channels            : {window.channelCount}");
            sb.AppendLine($"   7. effective rate      : {stats.effectiveRateHz:F4} Hz over " +
                          $"{stats.spanSeconds:F6} s");

            var brackets = window.firstTimestamp <= evt && window.lastTimestamp >= evt;
            sb.AppendLine($"   8. brackets the event  : {brackets}");

            sb.AppendLine($"   9. nearest before/at   : " +
                          $"{(double.IsNaN(stats.nearestBefore) ? "none" : stats.nearestBefore.ToString("F6", CultureInfo.InvariantCulture))}" +
                          $"   (rel {stats.nearestBeforeMs:+0.000;-0.000} ms)");
            sb.AppendLine($"      nearest after       : " +
                          $"{(double.IsNaN(stats.nearestAfter) ? "none" : stats.nearestAfter.ToString("F6", CultureInfo.InvariantCulture))}" +
                          $"   (rel {stats.nearestAfterMs:+0.000;-0.000} ms)");
            sb.AppendLine($"      exact sample at t=0 : {stats.exactHit}");
            sb.AppendLine($"  10. distance to nearest : {stats.nearestDistanceMs:F3} ms " +
                          $"(one sample interval = {1000d / recording.nominalRateHz:F3} ms)");
            sb.AppendLine($"  11. status              : {window.status}" +
                          $"   (COMPLETE means the recording covers both requested edges)");
            sb.AppendLine($"  12. monotonic           : {stats.monotonic}" +
                          $"   (negative intervals: {stats.negativeIntervals})");
            sb.AppendLine($"  13. intervals           : min {stats.minIntervalMs:F3} / " +
                          $"mean {stats.meanIntervalMs:F3} / max {stats.maxIntervalMs:F3} ms");
            sb.AppendLine($"      gaps > 2x nominal   : {stats.largeGaps}");

            sb.AppendLine();

            var countPlausible = expected <= 0 ||
                                 (window.sampleCount >= expected * 0.8 &&
                                  window.sampleCount <= expected * 1.2);

            var withinOneSample = stats.nearestDistanceMs <= 1000d / recording.nominalRateHz;

            var pass = window.sampleCount > 0 &&
                       brackets &&
                       countPlausible &&
                       stats.monotonic &&
                       stats.largeGaps == 0 &&
                       withinOneSample &&
                       domainOk &&
                       window.status == EegWindowStatus.Complete;

            sb.AppendLine(pass
                ? "  PASS — an event-centred epoch was extracted from the recording: complete, " +
                  "monotonic, gap-free, bracketing the event within one sample interval."
                : "  REVIEW — see the flags above.");

            if (!countPlausible)
            {
                sb.AppendLine($"    sample count {window.sampleCount} is outside +/-20% of the " +
                              $"~{expected} the advertised rate implies.");
            }

            if (!withinOneSample)
            {
                sb.AppendLine("    the nearest sample is further than one sample interval from " +
                              "the event.");
            }

            if (!domainOk)
            {
                sb.AppendLine("    the analysis timeline FAILED the clock-domain / rate check " +
                              "above, so this epoch is not usable however healthy its own " +
                              "counts look.");
            }
        }

        /// <summary>Everything measured about the epoch's own timeline.</summary>
        public struct EpochStats
        {
            public double spanSeconds;
            public double effectiveRateHz;
            public double minIntervalMs;
            public double meanIntervalMs;
            public double maxIntervalMs;
            public int largeGaps;
            public int negativeIntervals;
            public bool monotonic;
            public double nearestBefore;
            public double nearestAfter;
            public double nearestBeforeMs;
            public double nearestAfterMs;
            public double nearestDistanceMs;
            public bool exactHit;
        }

        public static EpochStats Continuity(EegWindow window, double nominalRateHz)
        {
            var stats = new EpochStats
            {
                monotonic = true,
                nearestBefore = double.NaN,
                nearestAfter = double.NaN,
                nearestBeforeMs = double.NaN,
                nearestAfterMs = double.NaN,
                nearestDistanceMs = double.NaN,
                minIntervalMs = double.NaN,
                maxIntervalMs = double.NaN,
                meanIntervalMs = double.NaN,
            };

            if (window.timestamps == null || window.sampleCount == 0)
                return stats;

            var t = window.timestamps;
            var evt = window.eventTimestamp;

            stats.spanSeconds = t[window.sampleCount - 1] - t[0];
            stats.effectiveRateHz = window.sampleCount > 1 && stats.spanSeconds > 0d
                ? (window.sampleCount - 1) / stats.spanSeconds
                : 0d;

            var nominalInterval = nominalRateHz > 0d ? 1d / nominalRateHz : double.NaN;
            var min = double.MaxValue;
            var max = double.MinValue;
            var sum = 0d;

            for (var i = 1; i < window.sampleCount; i++)
            {
                var step = t[i] - t[i - 1];

                if (step <= 0d)
                {
                    stats.monotonic = false;
                    stats.negativeIntervals++;
                }

                if (!double.IsNaN(nominalInterval) && step > 2d * nominalInterval)
                    stats.largeGaps++;

                if (step < min) min = step;
                if (step > max) max = step;
                sum += step;
            }

            if (window.sampleCount > 1)
            {
                stats.minIntervalMs = min * 1000d;
                stats.maxIntervalMs = max * 1000d;
                stats.meanIntervalMs = sum / (window.sampleCount - 1) * 1000d;
            }

            for (var i = 0; i < window.sampleCount; i++)
            {
                if (t[i] <= evt)
                {
                    stats.nearestBefore = t[i];
                }
                else if (double.IsNaN(stats.nearestAfter))
                {
                    stats.nearestAfter = t[i];
                    break;
                }
            }

            if (!double.IsNaN(stats.nearestBefore))
                stats.nearestBeforeMs = (stats.nearestBefore - evt) * 1000d;

            if (!double.IsNaN(stats.nearestAfter))
                stats.nearestAfterMs = (stats.nearestAfter - evt) * 1000d;

            var dBefore = double.IsNaN(stats.nearestBefore)
                ? double.MaxValue
                : Math.Abs(stats.nearestBeforeMs);

            var dAfter = double.IsNaN(stats.nearestAfter)
                ? double.MaxValue
                : Math.Abs(stats.nearestAfterMs);

            stats.nearestDistanceMs = Math.Min(dBefore, dAfter);
            stats.exactHit = stats.nearestDistanceMs < 1e-6;

            return stats;
        }

        public static bool IsMonotonic(EegWindow window)
        {
            if (window.timestamps == null)
                return true;

            for (var i = 1; i < window.sampleCount; i++)
            {
                if (window.timestamps[i] <= window.timestamps[i - 1])
                    return false;
            }

            return true;
        }
    }
}
