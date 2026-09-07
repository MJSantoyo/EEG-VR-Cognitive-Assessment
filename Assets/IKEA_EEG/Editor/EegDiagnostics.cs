using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using IkeaEeg.Data;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Researcher checks for the raw EEG path: is signal arriving, is it continuous, and can a
    /// window be cut around a known event.
    ///
    /// TRANSPORT AND ALIGNMENT ONLY. Nothing here filters, transforms or interprets the signal;
    /// it reports what arrived and when. No PASS is ever printed for data that was not actually
    /// received.
    /// </summary>
    public static class EegDiagnostics
    {
        /// <summary>Long enough to fill a window either side of the test event.</summary>
        const double k_CollectSeconds = 4.0;

        const double k_PreSeconds = 1.0;
        const double k_PostSeconds = 2.0;

        [MenuItem("IKEA_EEG/EEG/Check Raw EEG Buffer", false, 140)]
        public static void CheckBufferMenu()
        {
            Debug.Log(CheckBufferReport());
        }

        /// <summary>Batch-mode entry point.</summary>
        public static void CheckBufferFromCommandLine()
        {
            Debug.Log(CheckBufferReport());
        }

        [MenuItem("IKEA_EEG/EEG/Test Event Window", false, 141)]
        public static void TestEventWindowMenu()
        {
            Debug.Log(TestEventWindowReport());
        }

        /// <summary>Batch-mode entry point.</summary>
        public static void TestEventWindowFromCommandLine()
        {
            Debug.Log(TestEventWindowReport());
        }

        // ---------------------------------------------------------------------------------
        // Shared setup
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Connects a temporary receiver and collects for a fixed interval.
        ///
        /// A throwaway GameObject with HideAndDontSave: the experiment scene is not modified and
        /// nothing is left behind. Update() does not run in the Editor outside Play Mode, so the
        /// samples are drained explicitly here rather than by the receiver's own per-frame path.
        /// </summary>
        static AuraLslReceiver Connect(GameObject host, StringBuilder sb, out bool ok)
        {
            ok = false;

            if (!LslBinding.isAvailable)
            {
                sb.AppendLine($"  FAIL — no LSL library: {LslBinding.detail}");
                return null;
            }

            if (!LslBinding.CanReceiveStreams)
            {
                sb.AppendLine("  FAIL — the installed binding exposes no inlet/resolver API.");
                return null;
            }

            var receiver = host.AddComponent<AuraLslReceiver>();
            receiver.Configure(resolveTimeoutSeconds: 4f, receiveContinuously: false);
            receiver.ConfigureBuffer(bufferSamples: true, bufferSeconds: 60f);

            if (!receiver.Connect(out var problem))
            {
                sb.AppendLine($"  FAIL — {problem}");
                sb.AppendLine();
                sb.AppendLine("  Run IKEA_EEG > LSL > Diagnose Network if this is unexpected.");
                return receiver;
            }

            ok = true;
            return receiver;
        }

        /// <summary>
        /// Pulls for a wall-clock interval, draining whatever has arrived.
        ///
        /// Uses the receiver's non-blocking drain in a loop with a short sleep, so this never
        /// waits indefinitely on a stream that has gone quiet.
        /// </summary>
        static int Collect(AuraLslReceiver receiver, double seconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            var total = 0;

            while (DateTime.UtcNow < deadline)
            {
                total += receiver.Drain(4096);
                Thread.Sleep(10);
            }

            total += receiver.Drain(4096);
            return total;
        }

        static void AppendStreamMetadata(StringBuilder sb, LslBinding.StreamMetadata meta)
        {
            sb.AppendLine($"  stream name:   {meta.name}");
            sb.AppendLine($"  stream type:   {meta.type}");
            sb.AppendLine($"  channels:      {meta.channelCount}");
            sb.AppendLine("  nominal rate:  " + (meta.hasRegularRate
                ? $"{meta.nominalSrate.ToString("F3", CultureInfo.InvariantCulture)} Hz"
                : "irregular"));
            sb.AppendLine($"  format:        {meta.channelFormat}");
            sb.AppendLine($"  source id:     " +
                          (string.IsNullOrEmpty(meta.sourceId) ? "(none)" : meta.sourceId));
        }

        /// <summary>
        /// The clock relationship between the two machines.
        ///
        /// Reported prominently because it is the difference between an EEG file that can be
        /// epoched against the event log and one that cannot: the two machines' raw LSL clocks
        /// are unrelated, and only this offset ties them together.
        /// </summary>
        static void AppendClockCorrection(StringBuilder sb, AuraLslReceiver receiver)
        {
            sb.AppendLine();
            sb.AppendLine("  ---- LSL CLOCK SYNCHRONIZATION ----");
            sb.AppendLine($"  bound API:        {LslBinding.boundTimeCorrection}");
            sb.AppendLine($"  correction known: {receiver.hasTimeCorrection}");

            if (!receiver.hasTimeCorrection)
            {
                sb.AppendLine("  WARNING — with no correction, EEG timestamps stay in the SENDER's");
                sb.AppendLine("  clock domain and no event can fall inside a window.");
                return;
            }

            sb.AppendLine($"  correction:       " +
                          $"{receiver.timeCorrection.ToString("F6", CultureInfo.InvariantCulture)} s");
            sb.AppendLine($"  initial estimate: " +
                          $"{receiver.initialTimeCorrection.ToString("F6", CultureInfo.InvariantCulture)} s");
            sb.AppendLine($"  updates so far:   {receiver.correctionUpdates}");
            sb.AppendLine($"  largest change:   " +
                          $"{(receiver.largestCorrectionChange * 1000d).ToString("F3", CultureInfo.InvariantCulture)} ms");
            sb.AppendLine("  formula:          local = remote + correction  (liblsl's own");
            sb.AppendLine("                    documented mapping; no offset is invented here)");

            sb.AppendLine($"  local_clock now:  " +
                          LslClock.Now().ToString("F6", CultureInfo.InvariantCulture));
        }

        // ---------------------------------------------------------------------------------
        // Timestamp continuity
        // ---------------------------------------------------------------------------------

        /// <summary>How long to watch the stream. Long enough for several refresh intervals.</summary>
        const double k_ContinuitySeconds = 8.0;

        /// <summary>Anomalies printed in full. The rest are counted, never dumped.</summary>
        const int k_MaxExamples = 10;

        /// <summary>One inter-sample step, with everything needed to explain it.</summary>
        struct Step
        {
            public int index;
            public double prevRemote;
            public double remote;
            public double remoteDelta;
            public double prevLocal;
            public double local;
            public double localDelta;
            public double correction;
            public double prevAnalysis;
            public double analysis;
            public double analysisDelta;
        }

        [MenuItem("IKEA_EEG/EEG/Diagnose Timestamp Continuity", false, 142)]
        public static void DiagnoseContinuityMenu()
        {
            Debug.Log(ContinuityReport());
        }

        /// <summary>Batch-mode entry point.</summary>
        public static void DiagnoseContinuityFromCommandLine()
        {
            Debug.Log(ContinuityReport());
        }

        /// <summary>
        /// Separates the REMOTE timestamp series from the CORRECTED one, so an irregularity can
        /// be attributed to the sender rather than to our clock handling — or the other way
        /// round.
        ///
        /// The two series differ by exactly one added offset. Adding a CONSTANT cannot change
        /// ordering or intervals, so if both series show the same anomalies the anomalies were
        /// already in what AURA sent. If only the corrected series is disturbed, the correction
        /// is moving between samples and that is our bug to fix.
        ///
        /// DIAGNOSIS ONLY. Nothing here sorts, smooths, interpolates, clamps or drops a sample,
        /// and no post-processing flag is enabled on the inlet.
        /// </summary>
        public static string ContinuityReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== TIMESTAMP CONTINUITY DIAGNOSTIC =====");
            sb.AppendLine("  Diagnosis only. No sample is reordered, smoothed, interpolated,");
            sb.AppendLine("  clamped or discarded, and no liblsl post-processing is enabled.");
            sb.AppendLine();

            var host = new GameObject("__IKEA_EEG_Continuity")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };

            try
            {
                var receiver = Connect(host, sb, out var connected);

                if (!connected)
                    return sb.ToString();

                AppendStreamMetadata(sb, receiver.metadata);
                AppendClockCorrection(sb, receiver);

                // Captured through the receiver's own event, in the exact order Accept() saw
                // them — the same order pull_sample returned them in.
                var samples = new List<RawEegSample>(4096);
                void OnSample(RawEegSample s) => samples.Add(s);

                receiver.sampleReceived += OnSample;

                sb.AppendLine();
                sb.AppendLine($"  collecting {k_ContinuitySeconds:F0} s …");

                var wallStart = DateTime.UtcNow;
                Collect(receiver, k_ContinuitySeconds);
                var wallSeconds = (DateTime.UtcNow - wallStart).TotalSeconds;

                receiver.sampleReceived -= OnSample;

                if (samples.Count < 2)
                {
                    sb.AppendLine();
                    sb.AppendLine($"  FAIL — only {samples.Count} sample(s) arrived; there is " +
                                  "nothing to analyse. AURA is advertising but not sending.");
                    return sb.ToString();
                }

                // ---- Build the step series ------------------------------------------------
                var steps = new List<Step>(samples.Count);

                for (var i = 1; i < samples.Count; i++)
                {
                    steps.Add(new Step
                    {
                        index = i,
                        prevRemote = samples[i - 1].remoteLslTimestamp,
                        remote = samples[i].remoteLslTimestamp,
                        remoteDelta = samples[i].remoteLslTimestamp -
                                      samples[i - 1].remoteLslTimestamp,
                        prevLocal = samples[i - 1].lslTimestamp,
                        local = samples[i].lslTimestamp,
                        localDelta = samples[i].lslTimestamp - samples[i - 1].lslTimestamp,
                        correction = samples[i].timeCorrection,
                        prevAnalysis = samples[i - 1].analysisTimestamp,
                        analysis = samples[i].analysisTimestamp,
                        analysisDelta = samples[i].analysisTimestamp -
                                        samples[i - 1].analysisTimestamp,
                    });
                }

                var expectedInterval = receiver.metadata.hasRegularRate
                    ? 1.0 / receiver.metadata.nominalSrate
                    : 0d;

                var tolerance = receiver.buffer != null
                    ? receiver.buffer.gapToleranceSeconds
                    : expectedInterval * 4d;

                sb.AppendLine();
                sb.AppendLine($"  samples analysed:   {samples.Count}");
                sb.AppendLine($"  wall-clock elapsed: {wallSeconds.ToString("F3", CultureInfo.InvariantCulture)} s");
                sb.AppendLine($"  expected interval:  " +
                              $"{(expectedInterval * 1000d).ToString("F3", CultureInfo.InvariantCulture)} ms " +
                              $"(from the advertised {receiver.metadata.nominalSrate:F0} Hz)");
                sb.AppendLine($"  gap tolerance:      " +
                              $"{(tolerance * 1000d).ToString("F3", CultureInfo.InvariantCulture)} ms");

                // ---- The correction itself ------------------------------------------------
                var corrections = samples.Select(s => s.timeCorrection).Distinct().ToList();

                sb.AppendLine();
                sb.AppendLine($"  distinct correction values during collection: {corrections.Count}");

                if (corrections.Count > 1)
                {
                    var spread = corrections.Max() - corrections.Min();

                    sb.AppendLine($"  correction spread: " +
                                  $"{(spread * 1000d).ToString("F3", CultureInfo.InvariantCulture)} ms " +
                                  "— a MOVING correction can itself create apparent jumps");
                }
                else
                {
                    sb.AppendLine("  the correction was CONSTANT — it cannot have created any");
                    sb.AppendLine("  ordering anomaly, because adding a constant preserves order.");
                }

                AppendSeries(sb, "REMOTE (as AURA sent it)", steps, s => s.remoteDelta,
                    expectedInterval, tolerance);

                AppendSeries(sb, "LOCAL RAW (after clock correction)", steps, s => s.localDelta,
                    expectedInterval, tolerance);

                AppendSeries(sb, "ANALYSIS (de-jittered time base)", steps, s => s.analysisDelta,
                    expectedInterval, tolerance);

                if (receiver.analysisTimebase != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("    fitted rate:      " +
                                  receiver.analysisTimebase.fittedRateHz.ToString(
                                      "F4", CultureInfo.InvariantCulture) + " Hz");
                    sb.AppendLine("    largest fit residual: " +
                                  (receiver.analysisTimebase.largestResidualSeconds * 1000d)
                                  .ToString("F3", CultureInfo.InvariantCulture) + " ms");
                    sb.AppendLine("    largest grid drift:   " +
                                  (receiver.analysisTimebase.largestDriftSeconds * 1000d)
                                  .ToString("F3", CultureInfo.InvariantCulture) + " ms");
                    sb.AppendLine("    (the residual IS the jitter the fit removes, measured)");
                }

                // ---- Anomaly detail --------------------------------------------------------
                var backward = steps.Where(s => s.remoteDelta <= 0d).ToList();
                var gaps = steps.Where(s => s.remoteDelta > tolerance).ToList();

                sb.AppendLine();
                sb.AppendLine("  ---- NON-MONOTONIC EXAMPLES (first " + k_MaxExamples + ") ----");

                if (backward.Count == 0)
                {
                    sb.AppendLine("    (none)");
                }
                else
                {
                    foreach (var s in backward.Take(k_MaxExamples))
                    {
                        sb.AppendLine($"    #{s.index}  remote {s.prevRemote:F6} -> {s.remote:F6} " +
                                      $"(delta {(s.remoteDelta * 1000d).ToString("F3", CultureInfo.InvariantCulture)} ms)");
                        sb.AppendLine($"          local  {s.prevLocal:F6} -> {s.local:F6} " +
                                      $"(delta {(s.localDelta * 1000d).ToString("F3", CultureInfo.InvariantCulture)} ms) " +
                                      $"correction {s.correction:F6}");
                    }

                    sb.AppendLine($"    total non-monotonic: {backward.Count}");
                    sb.AppendLine($"    worst backward jump: " +
                                  $"{(backward.Min(s => s.remoteDelta) * 1000d).ToString("F3", CultureInfo.InvariantCulture)} ms");
                }

                sb.AppendLine();
                sb.AppendLine("  ---- GAP EXAMPLES (first " + k_MaxExamples + ") ----");

                if (gaps.Count == 0)
                {
                    sb.AppendLine("    (none)");
                }
                else
                {
                    foreach (var s in gaps.Take(k_MaxExamples))
                    {
                        var ratio = expectedInterval > 0d ? s.remoteDelta / expectedInterval : 0d;

                        sb.AppendLine($"    #{s.index}  remote {s.prevRemote:F6} -> {s.remote:F6}  " +
                                      $"delta {(s.remoteDelta * 1000d).ToString("F3", CultureInfo.InvariantCulture)} ms  " +
                                      $"= {ratio.ToString("F2", CultureInfo.InvariantCulture)}x expected");
                    }

                    sb.AppendLine($"    total gaps: {gaps.Count}");
                    sb.AppendLine($"    largest gap: " +
                                  $"{(gaps.Max(s => s.remoteDelta) * 1000d).ToString("F3", CultureInfo.InvariantCulture)} ms");
                }

                // ---- Verdict ----------------------------------------------------------------
                var remoteBad = steps.Count(s => s.remoteDelta <= 0d);
                var localBad = steps.Count(s => s.localDelta <= 0d);

                sb.AppendLine();
                sb.AppendLine("  ---- VERDICT ----");
                sb.AppendLine($"  non-monotonic in REMOTE series: {remoteBad}");
                sb.AppendLine($"  non-monotonic in LOCAL  series: {localBad}");

                if (remoteBad > 0 && remoteBad == localBad)
                {
                    sb.AppendLine();
                    sb.AppendLine("  The irregularity ORIGINATES BEFORE clock correction — it is");
                    sb.AppendLine("  present in the timestamps AURA sent. Our ingestion adds a");
                    sb.AppendLine("  constant and preserves order, so it cannot have caused this.");
                    sb.AppendLine("  NOTHING has been altered to hide it.");
                }
                else if (remoteBad == 0 && localBad > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("  The remote series is clean but the corrected one is not —");
                    sb.AppendLine("  the correction is MOVING between samples. That is a bug in");
                    sb.AppendLine("  our correction lifecycle, not in AURA.");
                }
                else if (remoteBad == 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("  Both series are monotonic. No ordering anomaly in this sample.");
                }

                return sb.ToString();
            }
            catch (Exception e)
            {
                sb.AppendLine($"  FAIL — {e.GetType().Name}: {e.Message}");
                return sb.ToString();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// Interval statistics for one timestamp series.
        ///
        /// The effective rate is reported two ways because they answer different questions: the
        /// span-derived rate uses the timestamps themselves, while the monotonic-only rate
        /// excludes the disturbed steps so a handful of anomalies cannot distort the estimate.
        /// Neither is presented as a packet-loss figure — LSL exposes no such counter.
        /// </summary>
        static void AppendSeries(StringBuilder sb, string title, List<Step> steps,
            Func<Step, double> delta, double expectedInterval, double tolerance)
        {
            sb.AppendLine();
            sb.AppendLine($"  ---- {title} ----");

            var deltas = steps.Select(delta).ToList();
            var forward = deltas.Where(d => d > 0d).OrderBy(d => d).ToList();

            if (forward.Count == 0)
            {
                sb.AppendLine("    no forward steps");
                return;
            }

            var median = forward[forward.Count / 2];
            var mean = forward.Average();

            sb.AppendLine($"    min interval:    {(deltas.Min() * 1000d).ToString("F3", CultureInfo.InvariantCulture)} ms");
            sb.AppendLine($"    max interval:    {(deltas.Max() * 1000d).ToString("F3", CultureInfo.InvariantCulture)} ms");
            sb.AppendLine($"    median interval: {(median * 1000d).ToString("F3", CultureInfo.InvariantCulture)} ms");
            sb.AppendLine($"    mean interval:   {(mean * 1000d).ToString("F3", CultureInfo.InvariantCulture)} ms");

            sb.AppendLine($"    non-monotonic:   {deltas.Count(d => d <= 0d)}");
            sb.AppendLine($"    gaps > tolerance:{deltas.Count(d => d > tolerance)}");

            // Rate from the median interval: robust to the anomalies, unlike a span/count ratio.
            sb.AppendLine($"    rate from median interval: " +
                          $"{(1.0 / median).ToString("F2", CultureInfo.InvariantCulture)} Hz");

            var monotonicTotal = forward.Sum();

            sb.AppendLine($"    rate over monotonic spans: " +
                          $"{(forward.Count / monotonicTotal).ToString("F2", CultureInfo.InvariantCulture)} Hz");

            if (expectedInterval > 0d)
            {
                sb.AppendLine($"    median vs expected: " +
                              $"{(median / expectedInterval).ToString("F3", CultureInfo.InvariantCulture)}x");
            }
        }

        // ---------------------------------------------------------------------------------
        // Buffer check
        // ---------------------------------------------------------------------------------

        public static string CheckBufferReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== RAW EEG BUFFER CHECK =====");
            sb.AppendLine("  Transport and continuity only. No filtering or analysis is performed.");
            sb.AppendLine();

            var host = new GameObject("__IKEA_EEG_EegCheck")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };

            try
            {
                var receiver = Connect(host, sb, out var connected);

                sb.AppendLine($"  AURA connected: {connected}");

                if (!connected)
                    return sb.ToString();

                sb.AppendLine();
                AppendStreamMetadata(sb, receiver.metadata);
                AppendClockCorrection(sb, receiver);

                var buffer = receiver.buffer;

                if (buffer == null)
                {
                    sb.AppendLine();
                    sb.AppendLine("  FAIL — no ring buffer was created.");
                    return sb.ToString();
                }

                sb.AppendLine();
                sb.AppendLine($"  buffer capacity: {buffer.capacitySamples} samples " +
                              $"(derived from the advertised rate, not assumed)");
                sb.AppendLine($"  gap tolerance:   " +
                              $"{(buffer.gapToleranceSeconds * 1000d).ToString("F2", CultureInfo.InvariantCulture)} ms");
                sb.AppendLine();

                // A single WAITING pull first, before the non-blocking drain.
                //
                // These two exercise different liblsl paths, and separating them turns "no
                // samples" from one symptom into two different diagnoses: if the waiting pull
                // succeeds and the poll never does, the data connection is fine and the
                // non-blocking path is at fault; if neither returns anything, the inlet opened
                // but no data is flowing over it.
                var primed = receiver.TryReceiveOne(2.0, out var firstSample, out var firstError);

                sb.AppendLine($"  waiting pull (2.0 s): {(primed ? "SAMPLE RECEIVED" : "nothing")}" +
                              (string.IsNullOrEmpty(firstError) ? "" : $" — {firstError}"));

                if (primed)
                {
                    sb.AppendLine($"    lsl_time = " +
                                  firstSample.lslTimestamp.ToString("F6", CultureInfo.InvariantCulture));
                }

                sb.AppendLine($"  collecting for {k_CollectSeconds:F0} s …");

                var received = Collect(receiver, k_CollectSeconds);
                var stats = buffer.GetStats();

                sb.AppendLine();
                sb.AppendLine($"  samples received:   {received}");
                sb.AppendLine($"  total samples:      {stats.totalSamples}");
                sb.AppendLine($"  buffered samples:   {stats.bufferedSamples} / {stats.capacitySamples}");
                sb.AppendLine($"  buffered seconds:   {stats.bufferedSeconds.ToString("F3", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"  first LSL time:     {stats.firstTimestamp.ToString("F6", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"  latest LSL time:    {stats.latestTimestamp.ToString("F6", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"  observed duration:  {stats.observedDurationSeconds.ToString("F3", CultureInfo.InvariantCulture)} s");
                sb.AppendLine($"  effective rate:     {stats.effectiveRateHz.ToString("F2", CultureInfo.InvariantCulture)} Hz");
                sb.AppendLine($"  timestamps monotonic: {stats.timestampsMonotonic}");
                sb.AppendLine($"  non-monotonic samples: {stats.nonMonotonicCount}");
                sb.AppendLine($"  gaps over tolerance:   {stats.gapCount}");
                sb.AppendLine($"  largest gap:        " +
                              $"{(stats.largestGapSeconds * 1000d).ToString("F2", CultureInfo.InvariantCulture)} ms");

                if (receiver.metadata.hasRegularRate && stats.effectiveRateHz > 0d)
                {
                    var drift = stats.effectiveRateHz - receiver.metadata.nominalSrate;

                    sb.AppendLine($"  rate vs advertised: {drift.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)} Hz");
                    sb.AppendLine("  NOTE: a persistent shortfall here is the honest indication of");
                    sb.AppendLine("        dropped data. LSL does not expose a packet-loss counter,");
                    sb.AppendLine("        so no loss figure is invented.");
                }

                // ONE sample. Printing the stream would flood the Console and cost more than
                // the acquisition itself.
                if (buffer.TryGetLatest(out var latestTime, out var latestChannels))
                {
                    sb.AppendLine();
                    sb.AppendLine("  latest sample (one only):");
                    sb.AppendLine($"    lsl_time = {latestTime.ToString("F6", CultureInfo.InvariantCulture)}");

                    for (var c = 0; c < latestChannels.Length; c++)
                    {
                        sb.AppendLine($"    ch{c + 1} = " +
                                      latestChannels[c].ToString("F6", CultureInfo.InvariantCulture));
                    }
                }

                sb.AppendLine();

                sb.AppendLine(stats.totalSamples > 0
                    ? "  PASS — real raw EEG samples are buffered and time-stamped."
                    : "  FAIL — the inlet opened but no sample arrived.");

                return sb.ToString();
            }
            catch (Exception e)
            {
                sb.AppendLine($"  FAIL — {e.GetType().Name}: {e.Message}");
                return sb.ToString();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        // ---------------------------------------------------------------------------------
        // Event window
        // ---------------------------------------------------------------------------------

        public static string TestEventWindowReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== EVENT WINDOW TEST =====");
            sb.AppendLine($"  Cuts {k_PreSeconds:F0} s before / {k_PostSeconds:F0} s after a " +
                          "researcher marker, on the LSL clock.");
            sb.AppendLine();

            // The clock itself first: without it there is no alignment to test.
            sb.AppendLine($"  LSL clock available: {LslClock.isAvailable}");

            if (!LslClock.isAvailable)
            {
                sb.AppendLine("  FAIL — liblsl's local_clock() is not available, so no event can");
                sb.AppendLine("  carry an EEG-alignment timestamp.");
                return sb.ToString();
            }

            var host = new GameObject("__IKEA_EEG_EegWindowCheck")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };

            try
            {
                var receiver = Connect(host, sb, out var connected);

                if (!connected)
                    return sb.ToString();

                AppendStreamMetadata(sb, receiver.metadata);
                AppendClockCorrection(sb, receiver);
                sb.AppendLine();

                // Fill the PRE side first: a window needs signal that predates the event.
                sb.AppendLine($"  collecting {k_PreSeconds + 0.5:F1} s of pre-event signal …");
                Collect(receiver, k_PreSeconds + 0.5);

                // THE EVENT. A synthetic researcher marker stamped exactly as a real
                // experiment event is — from liblsl's own clock, the same one the samples
                // carry. Nothing else would be alignable.
                var eventTimestamp = LslClock.Now();

                sb.AppendLine($"  synthetic event at lsl_time = " +
                              $"{eventTimestamp.ToString("F6", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"  collecting {k_PostSeconds + 0.5:F1} s of post-event signal …");

                Collect(receiver, k_PostSeconds + 0.5);

                if (!receiver.TryGetWindow(eventTimestamp, k_PreSeconds, k_PostSeconds,
                        out var window))
                {
                    sb.AppendLine();
                    sb.AppendLine("  FAIL — no sample fell inside the requested window.");
                    return sb.ToString();
                }

                var expected = receiver.buffer.ExpectedSampleCount(k_PreSeconds, k_PostSeconds);

                sb.AppendLine();
                sb.AppendLine("  ---- WINDOW ----");
                sb.AppendLine($"  requested range: [" +
                              $"{window.requestedStart.ToString("F6", CultureInfo.InvariantCulture)} .. " +
                              $"{window.requestedEnd.ToString("F6", CultureInfo.InvariantCulture)}]");
                sb.AppendLine($"  actual first:    {window.firstTimestamp.ToString("F6", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"  actual last:     {window.lastTimestamp.ToString("F6", CultureInfo.InvariantCulture)}");
                sb.AppendLine($"  samples:         {window.sampleCount}");
                sb.AppendLine($"  channels:        {window.channelCount}");
                sb.AppendLine($"  status:          {window.status}");

                // The yardstick comes from the ADVERTISED rate, computed here — never a
                // hard-coded 750.
                sb.AppendLine($"  expected at {receiver.metadata.nominalSrate:F0} Hz: ~{expected} samples");

                var brackets = window.firstTimestamp <= eventTimestamp &&
                               window.lastTimestamp >= eventTimestamp;

                sb.AppendLine($"  brackets the event: {brackets}");

                var plausible = expected <= 0 ||
                                (window.sampleCount >= expected * 0.8 &&
                                 window.sampleCount <= expected * 1.2);

                sb.AppendLine($"  count plausible (±20% of advertised): {plausible}");

                sb.AppendLine();

                if (window.sampleCount > 0 && brackets && plausible &&
                    window.status == EegWindowStatus.Complete)
                {
                    sb.AppendLine("  PASS — a real EEG window was extracted around an event whose");
                    sb.AppendLine("  timestamp came from the same LSL clock as the samples.");
                }
                else if (window.sampleCount > 0 && brackets)
                {
                    sb.AppendLine("  PARTIAL — the window brackets the event but is incomplete " +
                                  $"({window.status}).");
                    sb.AppendLine("  Collect for longer, or check the gap diagnostics.");
                }
                else
                {
                    sb.AppendLine("  FAIL — the extracted window does not bracket the event.");
                }

                return sb.ToString();
            }
            catch (Exception e)
            {
                sb.AppendLine($"  FAIL — {e.GetType().Name}: {e.Message}");
                return sb.ToString();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }
    }
}
