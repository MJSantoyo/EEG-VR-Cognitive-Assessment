using System;
using System.Globalization;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using IkeaEeg.Data;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// The live spectral diagnostic: one concise report of what the EEG currently looks like in
    /// the theta and alpha bands.
    ///
    /// It exercises the REAL pipeline — the same filter, the same Welch configuration and the
    /// same montage the runtime uses — so a number seen here is the number the runtime would
    /// produce. No sample is printed; the report is a summary, not a dump.
    /// </summary>
    public static class EegSpectralDiagnostics
    {
        [MenuItem("IKEA_EEG/EEG/Analyze Spectral Window", false, 143)]
        public static void AnalyzeMenu()
        {
            Debug.Log(Report());
        }

        /// <summary>Batch-mode entry point.</summary>
        public static void AnalyzeFromCommandLine()
        {
            Debug.Log(Report());
        }

        /// <summary>Outer analysis window. 4 s gives roughly 16 cycles at 4 Hz.</summary>
        const double k_WindowSeconds = 4.0;

        public static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== EEG SPECTRAL WINDOW =====");

            if (!LslBinding.isAvailable || !LslBinding.CanReceiveStreams)
            {
                sb.AppendLine($"  FAIL — LSL unavailable: {LslBinding.detail}");
                return sb.ToString();
            }

            var host = new GameObject("__IKEA_EEG_Spectral")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };

            try
            {
                var receiver = host.AddComponent<AuraLslReceiver>();
                receiver.Configure(resolveTimeoutSeconds: 4f, receiveContinuously: false);
                receiver.ConfigureBuffer(bufferSamples: true, bufferSeconds: 60f);

                // The pipeline attaches to THIS receiver and subscribes to its sample event.
                // Only one inlet exists for this diagnostic.
                var pipeline = host.AddComponent<EegFeaturePipeline>();

                // OnEnable does not run for a component added in edit mode, so the
                // subscription is made explicitly. Still ONE receiver and ONE inlet.
                pipeline.EnsureSubscribed();

                if (!receiver.Connect(out var problem))
                {
                    sb.AppendLine($"  FAIL — {problem}");
                    sb.AppendLine("  No live EEG. Run IKEA_EEG > LSL > Diagnose Network if " +
                                  "this is unexpected.");
                    return sb.ToString();
                }

                var meta = receiver.metadata;

                sb.AppendLine();
                sb.AppendLine("INPUT:");
                sb.AppendLine($"  stream:      {meta.name} ({meta.type})");
                sb.AppendLine($"  channels:    {meta.channelCount}");
                sb.AppendLine($"  rate:        {meta.nominalSrate.ToString("F3", CultureInfo.InvariantCulture)} Hz");
                sb.AppendLine($"  format:      {meta.channelFormat}");

                // Collect long enough for the filter to settle AND fill the window.
                var settleSeconds = 3.0 / 1.0;   // 3 time constants at the 1 Hz high-pass
                var collectSeconds = settleSeconds + k_WindowSeconds + 1.0;

                sb.AppendLine($"  collecting {collectSeconds:F1} s (filter settling + window) …");

                var deadline = DateTime.UtcNow.AddSeconds(collectSeconds);
                var received = 0;

                while (DateTime.UtcNow < deadline)
                {
                    received += receiver.Drain(4096);
                    Thread.Sleep(10);
                }

                received += receiver.Drain(4096);

                sb.AppendLine($"  samples received: {received}");

                if (received == 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("  FAIL — the inlet opened but no sample arrived. AURA is " +
                                  "advertising without transmitting; nothing can be analysed.");
                    return sb.ToString();
                }

                var features = pipeline.AnalyzeLatestWindow(k_WindowSeconds);

                sb.AppendLine();
                sb.AppendLine("PREPROCESSING:");
                sb.AppendLine(pipeline.DescribePreprocessing());

                sb.AppendLine();
                sb.AppendLine("QUALITY:");
                sb.AppendLine($"  {features.QualityText()}");
                sb.AppendLine($"  feature validity: {features.featureValidity}");

                // Spelled out rather than left as a flag name: "IdenticalChannels" does not tell
                // a researcher WHICH electrodes matched, and that is the whole diagnostic value.
                if (!string.IsNullOrEmpty(features.identicalChannelDetail))
                {
                    sb.AppendLine($"  inter-channel identity: {features.identicalChannelDetail}");
                    sb.AppendLine("  Real electrodes cannot produce bit-identical signals. Check " +
                                  "electrode connection, impedance, reference/ground and any " +
                                  "amplifier test-signal mode before trusting anything below.");
                }

                sb.AppendLine();
                sb.AppendLine("WINDOW:");
                sb.AppendLine($"  requested:   {k_WindowSeconds:F1} s");
                sb.AppendLine($"  samples:     {features.sampleCount}");
                sb.AppendLine($"  channels:    {features.channelCount}");
                sb.AppendLine($"  span:        [{features.windowStart.ToString("F6", CultureInfo.InvariantCulture)} .. " +
                              $"{features.windowEnd.ToString("F6", CultureInfo.InvariantCulture)}]");

                sb.AppendLine();
                sb.AppendLine("WELCH:");
                sb.AppendLine("  segment 2.0 s, 50% overlap, periodic Hann");
                sb.AppendLine($"  physical resolution ≈ {(1.0 / 2.0):F2} Hz " +
                              "(from the 2 s observation, not the FFT length)");

                if (features.thetaPerChannel != null)
                {
                    sb.AppendLine();
                    sb.AppendLine($"PER CHANNEL  (power in {features.powerUnits}):");

                    for (var c = 0; c < features.thetaPerChannel.Length; c++)
                    {
                        var label = string.IsNullOrEmpty(features.channelLabels[c])
                            ? $"CH{c + 1}"
                            : features.channelLabels[c];

                        sb.AppendLine($"  {label,-5} theta = {features.thetaPerChannel[c],12:E4}   " +
                                      $"alpha = {features.alphaPerChannel[c],12:E4}");
                    }
                }

                sb.AppendLine();
                sb.AppendLine("ROI:");

                if (features.roiValid)
                {
                    sb.AppendLine($"  Frontal Theta   (F3,Fz,F4) = " +
                                  $"{features.frontalTheta.ToString("E4", CultureInfo.InvariantCulture)} " +
                                  features.powerUnits);
                    sb.AppendLine($"  Posterior Alpha (P3,Pz,P4) = " +
                                  $"{features.posteriorAlpha.ToString("E4", CultureInfo.InvariantCulture)} " +
                                  features.powerUnits);
                }
                else
                {
                    sb.AppendLine($"  UNAVAILABLE — {features.roiProblem}");
                }

                if (features.derivedValid)
                {
                    sb.AppendLine();
                    sb.AppendLine("DERIVED (EXPLORATORY — not a validated workload score):");
                    sb.AppendLine($"  theta/alpha ratio = {features.thetaAlphaRatio:F4}");
                    sb.AppendLine($"  log theta - log alpha = {features.logThetaAlpha:F4}");
                }

                sb.AppendLine();
                sb.AppendLine("BASELINE:");

                if (features.baselineAvailable)
                {
                    sb.AppendLine($"  available ({pipeline.baselineSeconds:F1} s)");
                    sb.AppendLine($"  delta theta = {features.deltaThetaDb:F3} dB");
                    sb.AppendLine($"  delta alpha = {features.deltaAlphaDb:F3} dB");
                }
                else
                {
                    sb.AppendLine($"  unavailable — {pipeline.baselineDetail}");
                    sb.AppendLine("  absolute values above remain valid.");
                }

                sb.AppendLine();
                sb.AppendLine("UNITS:");
                sb.AppendLine($"  amplitude {features.amplitudeUnits}; power {features.powerUnits}; " +
                              $"PSD {features.psdUnits}");
                sb.AppendLine("  The stream publishes no scaling, so these are NOT µV.");

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
