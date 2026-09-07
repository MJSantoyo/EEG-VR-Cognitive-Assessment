using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using IkeaEeg.Data;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Proves — or disproves — that a channel keeps its identity all the way from the wire to
    /// its band power.
    ///
    /// WHY THIS EXISTS. A live capture showed one electrode reporting theta six orders of
    /// magnitude above its neighbours while the Researcher Monitor showed all eight traces
    /// looking comparable. Two incompatible pictures of the same recording. Either one channel's
    /// data is being mixed up somewhere between the inlet and the FFT, or both pictures are
    /// correct and one of them is being read wrongly. Nothing short of measuring every stage of
    /// the same window can tell those apart.
    ///
    /// WHAT IT MEASURES, per channel, on ONE captured window:
    ///   RAW      min / max / mean / RMS   — straight from the receiver's buffer
    ///   FILTERED min / max / mean / RMS   — from the pipeline's filtered buffer
    ///   THETA / ALPHA                     — from the same window the analyser is given
    /// and pairwise, for every channel pair: exact sample equality count, normalised
    /// correlation, and RMS ratio.
    ///
    /// It also answers the structural questions directly: how many receivers and pipelines are
    /// alive, and whether the monitor's buffer and the analyser's input are the same object.
    ///
    /// READ-ONLY with respect to the experiment. It creates its own temporary host exactly as
    /// the spectral diagnostic does, so it can run with no scene loaded; when a pipeline already
    /// exists in the scene it reports that instead of quietly measuring a different one.
    /// </summary>
    public static class EegChannelIntegrityDiagnostics
    {
        [MenuItem("IKEA_EEG/EEG/Diagnose Channel Integrity", false, 144)]
        public static void DiagnoseMenu()
        {
            Debug.Log(Report());
        }

        /// <summary>Batch-mode entry point.</summary>
        public static void DiagnoseFromCommandLine()
        {
            Debug.Log(Report());
        }

        /// <summary>Analysis window, seconds. Matches the spectral diagnostic.</summary>
        const double k_WindowSeconds = 4.0;

        /// <summary>
        /// How long to collect. Deliberately much longer than the spectral diagnostic's 8 s.
        ///
        /// The settling rule in the pipeline is three high-pass time constants, which leaves
        /// about 5% of any input step still decaying. When the amplifier carries DC offsets far
        /// larger than the EEG riding on them, 5% of the offset can still dwarf the signal. A
        /// long capture lets the same recording be examined EARLY and LATE, which turns that
        /// from an argument into a measurement.
        /// </summary>
        const double k_CollectSeconds = 30.0;

        /// <summary>Where the early comparison window is centred, seconds after first sample.</summary>
        const double k_EarlyWindowAt = 6.0;

        public static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== EEG CHANNEL INTEGRITY =====");

            if (!LslBinding.isAvailable || !LslBinding.CanReceiveStreams)
            {
                sb.AppendLine($"  FAIL — LSL unavailable: {LslBinding.detail}");
                return sb.ToString();
            }

            // ---- 1. Structural: how many of each thing is alive? ----------------------------
            ReportInstances(sb);

            var host = new GameObject("__IKEA_EEG_Integrity")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };

            try
            {
                var receiver = host.AddComponent<AuraLslReceiver>();
                receiver.Configure(resolveTimeoutSeconds: 4f, receiveContinuously: false);
                receiver.ConfigureBuffer(bufferSamples: true, bufferSeconds: 60f);

                var pipeline = host.AddComponent<EegFeaturePipeline>();
                pipeline.EnsureSubscribed();

                if (!receiver.Connect(out var problem))
                {
                    sb.AppendLine($"  FAIL — {problem}");
                    return sb.ToString();
                }

                var meta = receiver.metadata;

                sb.AppendLine();
                sb.AppendLine("INPUT:");
                sb.AppendLine($"  stream:   {meta.name} ({meta.type})");
                sb.AppendLine($"  channels: {meta.channelCount}");
                sb.AppendLine($"  rate:     {meta.nominalSrate.ToString("F3", CultureInfo.InvariantCulture)} Hz");
                sb.AppendLine($"  format:   {meta.channelFormat}");
                sb.AppendLine($"  collecting {k_CollectSeconds:F0} s …");

                var deadline = DateTime.UtcNow.AddSeconds(k_CollectSeconds);
                var received = 0;

                while (DateTime.UtcNow < deadline)
                {
                    received += receiver.Drain(4096);
                    Thread.Sleep(5);
                }

                received += receiver.Drain(4096);
                sb.AppendLine($"  samples received: {received}");

                if (received == 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("  FAIL — the inlet opened but no sample arrived. Nothing can " +
                                  "be concluded about channel integrity.");
                    return sb.ToString();
                }

                sb.AppendLine();
                sb.AppendLine("PREPROCESSING:");
                sb.AppendLine(pipeline.DescribePreprocessing());

                // ---- 2. Same data path? ------------------------------------------------------
                ReportPathIdentity(sb, receiver, pipeline);

                // ---- 3. The window everything below is measured on ---------------------------
                var filtered = pipeline.filteredBuffer;
                var raw = receiver.buffer;

                if (filtered == null || raw == null)
                {
                    sb.AppendLine("  FAIL — a buffer is missing; cannot compare stages.");
                    return sb.ToString();
                }

                var filteredStats = filtered.GetStats();
                var latest = filteredStats.latestTimestamp;

                if (!filtered.TryGetWindow(latest - k_WindowSeconds * 0.5,
                        k_WindowSeconds * 0.5, k_WindowSeconds * 0.5, out var filteredWindow))
                {
                    sb.AppendLine("  FAIL — no filtered window available.");
                    return sb.ToString();
                }

                // The RAW window over the SAME timestamps, so the two stages describe the same
                // instants and any difference between them is the filter and nothing else.
                if (!raw.TryGetWindow(latest - k_WindowSeconds * 0.5,
                        k_WindowSeconds * 0.5, k_WindowSeconds * 0.5, out var rawWindow))
                {
                    sb.AppendLine("  FAIL — no raw window available over the same interval.");
                    return sb.ToString();
                }

                var features = pipeline.AnalyzeLatestWindow(k_WindowSeconds);

                sb.AppendLine();
                sb.AppendLine("WINDOW:");
                sb.AppendLine($"  filtered: {filteredWindow.sampleCount} samples, " +
                              $"{filteredWindow.channelCount} ch, {filteredWindow.status}");
                sb.AppendLine($"  raw:      {rawWindow.sampleCount} samples, " +
                              $"{rawWindow.channelCount} ch, {rawWindow.status}");
                sb.AppendLine($"  span:     [{filteredWindow.firstTimestamp:F6} .. " +
                              $"{filteredWindow.lastTimestamp:F6}]");

                // ---- 4. Per channel, every stage --------------------------------------------
                ReportPerChannel(sb, rawWindow, filteredWindow, features, pipeline);

                // ---- 5. Pairwise ------------------------------------------------------------
                ReportPairwise(sb, filteredWindow, features);

                // ---- 6. Quality --------------------------------------------------------------
                sb.AppendLine();
                sb.AppendLine("QUALITY:");
                sb.AppendLine($"  {features.QualityText()}");
                sb.AppendLine($"  feature validity: {features.featureValidity}");

                var explanation = features.Explain();

                if (!string.IsNullOrEmpty(explanation))
                {
                    foreach (var line in explanation.Split('\n'))
                        sb.AppendLine($"    {line}");
                }

                // ---- 7. Early vs late: is the anomaly a decaying transient? ------------------
                ReportSettlingEvidence(sb, pipeline, filtered, features);

                return sb.ToString();
            }
            catch (Exception e)
            {
                sb.AppendLine($"  FAIL — {e.GetType().Name}: {e.Message}");
                sb.AppendLine(e.StackTrace);
                return sb.ToString();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        // -----------------------------------------------------------------------------------

        /// <summary>
        /// How many receivers and pipelines exist in the loaded scene.
        ///
        /// Reported, never "corrected". A duplicate is a fact about the scene that the person
        /// running this needs to know about; destroying it here would hide the very thing worth
        /// seeing, and would be a mutation from a tool that has no business mutating.
        /// </summary>
        static void ReportInstances(StringBuilder sb)
        {
            var receivers = UnityEngine.Object.FindObjectsByType<AuraLslReceiver>(
                FindObjectsSortMode.None);
            var pipelines = UnityEngine.Object.FindObjectsByType<EegFeaturePipeline>(
                FindObjectsSortMode.None);

            sb.AppendLine();
            sb.AppendLine("SCENE INSTANCES (before this diagnostic adds its own temporary host):");
            sb.AppendLine($"  AuraLslReceiver:    {receivers.Length}");
            sb.AppendLine($"  EegFeaturePipeline: {pipelines.Length}");

            foreach (var r in receivers)
                sb.AppendLine($"    receiver on '{PathOf(r.gameObject)}'");

            foreach (var p in pipelines)
                sb.AppendLine($"    pipeline on '{PathOf(p.gameObject)}'");

            if (receivers.Length > 1)
            {
                sb.AppendLine("  WARNING — more than one receiver. Two inlets on one stream is " +
                              "forbidden by design; they are NOT destroyed here, only reported.");
            }

            if (pipelines.Length > 1)
            {
                sb.AppendLine("  WARNING — more than one pipeline. The monitor uses " +
                              "FindAnyObjectByType and would pick an arbitrary one.");
            }

            if (receivers.Length == 0 && pipelines.Length == 0)
            {
                sb.AppendLine("  (no scene loaded, or EEG nodes absent — this diagnostic builds " +
                              "its own temporary host below and measures that)");
            }
        }

        static string PathOf(GameObject go)
        {
            var path = go.name;
            var t = go.transform.parent;

            while (t != null)
            {
                path = t.name + "/" + path;
                t = t.parent;
            }

            return path;
        }

        /// <summary>
        /// Whether the Researcher Monitor and the spectral analyser read the same bytes.
        ///
        /// The monitor reads <c>pipeline.filteredBuffer</c>; the analyser cuts its window from
        /// the same field. This checks the claim by REFERENCE first — same object or not — and
        /// then numerically, by computing RMS over a window taken through the monitor's own
        /// accessor and comparing it with RMS over the window handed to the analyser. Reference
        /// equality alone would not catch a stale copy, and numbers alone would not distinguish
        /// "same buffer" from "two buffers that happen to agree".
        /// </summary>
        static void ReportPathIdentity(StringBuilder sb, AuraLslReceiver receiver,
            EegFeaturePipeline pipeline)
        {
            sb.AppendLine();
            sb.AppendLine("DATA PATH IDENTITY (monitor vs spectral analyser):");

            var monitorBuffer = pipeline.filteredBuffer;   // exactly what the monitor reads
            var rawBuffer = receiver.buffer;

            sb.AppendLine($"  monitor filtered buffer is the pipeline's own: " +
                          $"{ReferenceEquals(monitorBuffer, pipeline.filteredBuffer)}");
            sb.AppendLine($"  monitor raw buffer is the receiver's own:      " +
                          $"{ReferenceEquals(rawBuffer, receiver.buffer)}");
            sb.AppendLine($"  filtered buffer is NOT the raw buffer:         " +
                          $"{!ReferenceEquals(monitorBuffer, rawBuffer)}");
        }

        /// <summary>Per-channel statistics at every stage, side by side.</summary>
        static void ReportPerChannel(StringBuilder sb, EegWindow rawWindow,
            EegWindow filteredWindow, LatestEegFeatures features, EegFeaturePipeline pipeline)
        {
            var montage = pipeline.montage;

            sb.AppendLine();
            sb.AppendLine("PER CHANNEL — RAW (amplifier native units):");
            sb.AppendLine("  ch    label      min            max            mean           RMS");

            for (var c = 0; c < rawWindow.channelCount; c++)
            {
                var s = Describe(rawWindow, c);
                sb.AppendLine($"  {c + 1,-5} {Label(montage, c),-10} {s.min,-14:E4} {s.max,-14:E4} " +
                              $"{s.mean,-14:E4} {s.rms,-14:E4}");
            }

            sb.AppendLine();
            sb.AppendLine("PER CHANNEL — FILTERED (same instants, same units):");
            sb.AppendLine("  ch    label      min            max            mean           RMS");

            for (var c = 0; c < filteredWindow.channelCount; c++)
            {
                var s = Describe(filteredWindow, c);
                sb.AppendLine($"  {c + 1,-5} {Label(montage, c),-10} {s.min,-14:E4} {s.max,-14:E4} " +
                              $"{s.mean,-14:E4} {s.rms,-14:E4}");
            }

            sb.AppendLine();
            sb.AppendLine("PER CHANNEL — SPECTRAL (from the window above):");
            sb.AppendLine("  ch    label      theta          alpha          filtered RMS   " +
                          "theta/RMS^2");

            for (var c = 0; c < filteredWindow.channelCount && c < features.thetaPerChannel.Length; c++)
            {
                var s = Describe(filteredWindow, c);

                // A consistency ratio: band power should be a fraction of total power. A value
                // far above 1 would mean the PSD is describing something the samples do not
                // contain, which is the signature of a de-interleave or scaling fault.
                var ratio = s.rms > 0d
                    ? features.thetaPerChannel[c] / (s.rms * s.rms)
                    : double.NaN;

                sb.AppendLine($"  {c + 1,-5} {Label(montage, c),-10} " +
                              $"{features.thetaPerChannel[c],-14:E4} " +
                              $"{features.alphaPerChannel[c],-14:E4} {s.rms,-14:E4} {ratio,-14:F4}");
            }

            sb.AppendLine();
            sb.AppendLine("  theta/RMS^2 must be <= ~1: band power is part of total power.");
            sb.AppendLine("  A value far above 1 would indicate the PSD does not describe these");
            sb.AppendLine("  samples — i.e. a genuine channel-association fault.");
        }

        struct Stats
        {
            public double min, max, mean, rms;
        }

        static Stats Describe(EegWindow window, int channel)
        {
            var stats = new Stats { min = double.MaxValue, max = double.MinValue };
            double sum = 0d, sumSq = 0d;
            var used = 0;

            for (var i = 0; i < window.sampleCount; i++)
            {
                var v = window.samples[i][channel];

                if (double.IsNaN(v) || double.IsInfinity(v))
                    continue;

                stats.min = Math.Min(stats.min, v);
                stats.max = Math.Max(stats.max, v);
                sum += v;
                sumSq += v * v;
                used++;
            }

            if (used == 0)
                return new Stats { min = double.NaN, max = double.NaN, mean = double.NaN, rms = double.NaN };

            stats.mean = sum / used;
            stats.rms = Math.Sqrt(sumSq / used);
            return stats;
        }

        static string Label(AuraMontageConfig montage, int channel)
        {
            var label = montage != null ? montage.LabelOfIndex(channel) : null;
            return string.IsNullOrEmpty(label) ? $"CH{channel + 1}" : label;
        }

        /// <summary>
        /// Every channel pair, three ways.
        ///
        /// Exact equality count catches a literal duplicate. Normalised correlation catches a
        /// pair that is the same signal scaled or offset — which exact equality cannot see, and
        /// which is what an overwhelming common-mode component looks like. RMS ratio says
        /// whether they are even the same size. A fault that survives all three is not a
        /// channel-association fault.
        /// </summary>
        static void ReportPairwise(StringBuilder sb, EegWindow window, LatestEegFeatures features)
        {
            var channels = window.channelCount;

            sb.AppendLine();
            sb.AppendLine("PAIRWISE (filtered window):");
            sb.AppendLine("  pair          equal samples    correlation   RMS ratio");

            var suspicious = new List<string>();
            var maxCorrelation = -2.0;
            var maxCorrelationPair = string.Empty;

            for (var a = 0; a < channels; a++)
            {
                for (var b = a + 1; b < channels; b++)
                {
                    var equal = 0;

                    for (var i = 0; i < window.sampleCount; i++)
                    {
                        if (window.samples[i][a] == window.samples[i][b])
                            equal++;
                    }

                    var correlation = Correlation(window, a, b);
                    var rmsA = Describe(window, a).rms;
                    var rmsB = Describe(window, b).rms;
                    var ratio = rmsB > 0d ? rmsA / rmsB : double.NaN;

                    var name = $"{LabelOfFeatures(features, a)}-{LabelOfFeatures(features, b)}";

                    if (correlation > maxCorrelation)
                    {
                        maxCorrelation = correlation;
                        maxCorrelationPair = name;
                    }

                    // Only the notable rows are printed: 28 pairs of unremarkable numbers
                    // would bury the one that matters.
                    if (equal == window.sampleCount || Math.Abs(correlation) > 0.99)
                    {
                        sb.AppendLine($"  {name,-13} {equal,-16} {correlation,-13:F6} {ratio,-10:F4}");
                        suspicious.Add($"{name} (r={correlation:F6}, {equal} equal samples)");
                    }
                }
            }

            if (suspicious.Count == 0)
            {
                sb.AppendLine($"  (no pair was identical or correlated above |r| = 0.99)");
                sb.AppendLine($"  highest correlation: {maxCorrelationPair} r = {maxCorrelation:F6}");
                sb.AppendLine("  INTER_CHANNEL_IDENTICAL: not detected");
            }
            else
            {
                sb.AppendLine($"  INTER_CHANNEL_IDENTICAL: {suspicious.Count} suspicious pair(s)");

                foreach (var s in suspicious)
                    sb.AppendLine($"    {s}");
            }
        }

        static string LabelOfFeatures(LatestEegFeatures features, int channel)
        {
            if (features.channelLabels != null && channel < features.channelLabels.Length &&
                !string.IsNullOrEmpty(features.channelLabels[channel]))
            {
                return features.channelLabels[channel];
            }

            return $"CH{channel + 1}";
        }

        /// <summary>Pearson correlation between two channels of the same window.</summary>
        static double Correlation(EegWindow window, int a, int b)
        {
            double sumA = 0d, sumB = 0d;
            var n = 0;

            for (var i = 0; i < window.sampleCount; i++)
            {
                var va = window.samples[i][a];
                var vb = window.samples[i][b];

                if (double.IsNaN(va) || double.IsNaN(vb) ||
                    double.IsInfinity(va) || double.IsInfinity(vb))
                {
                    continue;
                }

                sumA += va;
                sumB += vb;
                n++;
            }

            if (n < 2)
                return double.NaN;

            var meanA = sumA / n;
            var meanB = sumB / n;

            double cov = 0d, varA = 0d, varB = 0d;

            for (var i = 0; i < window.sampleCount; i++)
            {
                var va = window.samples[i][a];
                var vb = window.samples[i][b];

                if (double.IsNaN(va) || double.IsNaN(vb) ||
                    double.IsInfinity(va) || double.IsInfinity(vb))
                {
                    continue;
                }

                var da = va - meanA;
                var db = vb - meanB;

                cov += da * db;
                varA += da * da;
                varB += db * db;
            }

            var denominator = Math.Sqrt(varA * varB);
            return denominator > 0d ? cov / denominator : double.NaN;
        }

        /// <summary>
        /// The decisive test for a settling transient: the SAME recording, measured early and
        /// measured late.
        ///
        /// A high-pass filter starting from zero state against a large DC offset emits a
        /// decaying transient. That transient is low-frequency, so it lands in theta, and its
        /// size is proportional to the offset — which differs per channel. If that is what is
        /// happening, a window taken later in the same capture must show dramatically lower
        /// theta on exactly the affected channels, with nothing else changed: same filter, same
        /// buffer, same code path, same electrodes.
        ///
        /// If instead the anomaly is identical early and late, it is not a transient and this
        /// hypothesis is wrong. The test can refute itself, which is the point of running it.
        /// </summary>
        static void ReportSettlingEvidence(StringBuilder sb, EegFeaturePipeline pipeline,
            RawEegRingBuffer filtered, LatestEegFeatures lateFeatures)
        {
            sb.AppendLine();
            sb.AppendLine("SETTLING EVIDENCE (same capture, early window vs late window):");

            var stats = filtered.GetStats();
            var earlyCentre = stats.firstTimestamp + k_EarlyWindowAt;

            if (earlyCentre + k_WindowSeconds * 0.5 >= stats.latestTimestamp)
            {
                sb.AppendLine("  capture too short to compare an early and a late window.");
                return;
            }

            if (!filtered.TryGetWindow(earlyCentre, k_WindowSeconds * 0.5, k_WindowSeconds * 0.5,
                    out var earlyWindow) || earlyWindow.sampleCount < 2)
            {
                sb.AppendLine("  the early window is no longer in the buffer.");
                return;
            }

            var early = pipeline.AnalyzeWindowForDiagnostics(earlyWindow);

            sb.AppendLine($"  early window centred {k_EarlyWindowAt:F1} s after the first sample; " +
                          "late window is the most recent.");
            sb.AppendLine();
            sb.AppendLine("  ch    label      theta EARLY    theta LATE     ratio early/late");

            for (var c = 0; c < early.thetaPerChannel.Length &&
                            c < lateFeatures.thetaPerChannel.Length; c++)
            {
                var e = early.thetaPerChannel[c];
                var l = lateFeatures.thetaPerChannel[c];
                var ratio = l > 0d ? e / l : double.NaN;

                sb.AppendLine($"  {c + 1,-5} {LabelOfFeatures(lateFeatures, c),-10} " +
                              $"{e,-14:E4} {l,-14:E4} {ratio,-14:F1}");
            }

            sb.AppendLine();
            sb.AppendLine("  A large early/late ratio on the anomalous channels — and only on");
            sb.AppendLine("  those — is the signature of a decaying high-pass transient rather");
            sb.AppendLine("  than of a channel-association fault. A ratio near 1 everywhere");
            sb.AppendLine("  refutes that explanation.");
        }
    }
}
