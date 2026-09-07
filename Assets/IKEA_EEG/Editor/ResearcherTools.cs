using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using IkeaEeg.Data;
using IkeaEeg.Experiment;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Researcher-facing controls that must NOT appear on the participant's panel in the
    /// headset: replaying a seed, previewing the generated stimulus sequence, and checking the
    /// LSL marker transport.
    ///
    /// Keeping them on the Editor menu is the separation the protocol needs — a participant
    /// cannot reach them, and using one is a deliberate act by the person running the session.
    /// </summary>
    public static class ResearcherTools
    {
        // =================================================================================
        // Seed replay
        // =================================================================================

        [MenuItem("IKEA_EEG/Researcher/Replay Same Seed (Play Mode)", false, 100)]
        public static void ReplaySameSeed()
        {
            if (!Application.isPlaying)
            {
                EditorUtility.DisplayDialog("IKEA_EEG",
                    "Replay Same Seed restarts the RUNNING experiment with the seed it is " +
                    "currently using.\n\nEnter Play Mode first.", "OK");
                return;
            }

            var manager = Object.FindAnyObjectByType<ExperimentManager>();
            if (manager == null)
            {
                Debug.LogError("[IKEA_EEG] No ExperimentManager in the scene.");
                return;
            }

            var seed = manager.sessionSeed;
            manager.ReplaySameSeed();

            Debug.Log($"[IKEA_EEG] Replaying seed {seed} in a NEW session. The chair trials, " +
                      "targets, attributes and slot arrangement will be identical to the " +
                      "previous run; the session id, CSV and audio folder are new, so nothing " +
                      "from the previous run is overwritten.");
        }

        [MenuItem("IKEA_EEG/Researcher/Replay Same Seed (Play Mode)", true)]
        public static bool ReplaySameSeedValidate() => Application.isPlaying;

        // =================================================================================
        // Stimulus preview
        // =================================================================================

        /// <summary>
        /// Prints the chair block a given seed produces, WITHOUT running the experiment.
        ///
        /// This is how a protocol is inspected before a participant sees it: the same generator
        /// the runtime uses, so what is printed is exactly what would be presented.
        /// </summary>
        [MenuItem("IKEA_EEG/Researcher/Preview Chair Trials For Seed…", false, 101)]
        public static void PreviewChairTrials()
        {
            var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(
                ExperimentAssetBuilder.ConfigPath);

            if (config == null)
            {
                Debug.LogError($"[IKEA_EEG] No ExperimentConfig at {ExperimentAssetBuilder.ConfigPath}.");
                return;
            }

            var chairIds = new[]
            {
                "Chair_01", "Chair_02", "Chair_03", "Chair_04", "Chair_05", "Chair_06",
            };

            var task = Object.FindAnyObjectByType<IkeaEeg.Interaction.ChairSelectionTask>();
            var ids = task != null && task.chairs.Count > 0 ? task.GetChairIds() : new System.Collections.Generic.List<string>(chairIds);
            var slotCount = task != null && task.slots.Count > 0 ? task.slots.Count : ids.Count;

            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== CHAIR TRIAL PREVIEW =====");
            sb.AppendLine($"protocol: {config.DescribeProtocol()}");

            // A handful of seeds, including the configured fixed one, so both reproducibility
            // (same seed twice) and variability (different seeds) are visible at a glance.
            var seeds = new[] { config.fixedSeed, config.fixedSeed, 1L, 2L, 424242L };

            foreach (var seed in seeds)
            {
                sb.AppendLine();
                sb.AppendLine($"--- seed {seed} ---");

                if (!ChairTrialGenerator.TryGenerateBlock(seed, config.BuildDifficultySequence(),
                        config.difficultyProfiles, ids, slotCount, out var plans, out var problem))
                {
                    sb.AppendLine($"  GENERATION FAILED: {problem}");
                    continue;
                }

                foreach (var plan in plans)
                    sb.AppendLine("  " + plan.Describe());
            }

            sb.AppendLine();
            sb.AppendLine("The first two blocks use the SAME seed and must be identical.");
            Debug.Log(sb.ToString());
        }

        // =================================================================================
        // LSL
        // =================================================================================

        [MenuItem("IKEA_EEG/LSL/Check LSL Availability", false, 120)]
        public static void CheckLslAvailability()
        {
            LslBinding.ResetProbe();

            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== LSL AVAILABILITY =====");
            sb.AppendLine($"  available: {LslBinding.isAvailable}");
            sb.AppendLine($"  detail:    {LslBinding.detail}");
            sb.AppendLine($"  assembly:  {(string.IsNullOrEmpty(LslBinding.boundAssembly) ? "none" : LslBinding.boundAssembly)}");
            sb.AppendLine($"  loopback test possible: {LslBinding.CanRunLoopbackTest}");

            if (LslBinding.isAvailable)
            {
                // The NATIVE library is a separate fact from the managed API. Reported on its
                // own line so "available: True" can never be read as "markers will transmit".
                var nativeOk = LslBinding.TryNativeCheck(out var nativeDetail);
                sb.AppendLine($"  native lsl.dll: {(nativeOk ? "OK" : "NOT USABLE")} — {nativeDetail}");

                sb.AppendLine();
                sb.AppendLine("  Bound signatures:");
                sb.AppendLine($"    push_sample:    {LslBinding.boundPushSample}");
                sb.AppendLine($"    pull_sample:    {LslBinding.boundPullSample}");
                sb.AppendLine($"    resolve_stream: {LslBinding.boundResolveStream}");

                sb.AppendLine();
                sb.AppendLine("  Overloads the installed binding offers:");

                foreach (var overload in LslBinding.DescribeOverloads("StreamOutlet", "push_sample"))
                    sb.AppendLine($"    StreamOutlet.{overload}");

                foreach (var overload in LslBinding.DescribeOverloads("StreamInlet", "pull_sample"))
                    sb.AppendLine($"    StreamInlet.{overload}");
            }

            if (!LslBinding.isAvailable)
            {
                sb.AppendLine();
                sb.AppendLine("  No LSL library is installed, so NO markers can be transmitted.");
                sb.AppendLine("  The experiment still runs and still writes CSV and WAV data.");
                sb.AppendLine("  Installation instructions:");
                sb.AppendLine("      Assets/IKEA_EEG/Documentation/EEG_LSL_PIPELINE.md");
            }

            Debug.Log(sb.ToString());
        }

        /// <summary>
        /// Proves marker transmission end to end WITHOUT any EEG hardware: opens a real outlet,
        /// resolves it from a real inlet in the same process, pushes a marker and reads it back.
        ///
        /// If liblsl is not installed this reports that honestly and does nothing else — it
        /// never simulates a successful transmission.
        /// </summary>
        [MenuItem("IKEA_EEG/LSL/Run Marker Loopback Test", false, 121)]
        public static void RunLoopbackTest()
        {
            Debug.Log(LoopbackTestReport());
        }

        /// <summary>
        /// Finds the raw AURA EEG stream, reports what it advertises, and pulls a few real
        /// samples off it.
        ///
        /// A DIAGNOSTIC, not a pipeline: no filtering, no spectra, no band power, nothing
        /// connected to the experiment. It answers one question — is real EEG reaching Unity —
        /// and it answers PASS only when actual samples arrived.
        /// </summary>
        [MenuItem("IKEA_EEG/LSL/Check AURA Stream", false, 122)]
        public static void CheckAuraStream()
        {
            Debug.Log(AuraStreamReport());
        }

        /// <summary>How many samples the diagnostic prints. Never the whole stream.</summary>
        const int k_AuraDiagnosticSamples = 5;

        public static string AuraStreamReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== AURA RAW EEG CHECK =====");

            if (!LslBinding.isAvailable)
            {
                sb.AppendLine("  FAIL — AURA_NOT_FOUND: no LSL library is installed.");
                sb.AppendLine($"  detail: {LslBinding.detail}");
                return sb.ToString();
            }

            if (!LslBinding.CanReceiveStreams)
            {
                sb.AppendLine("  FAIL — AURA_FOUND_BUT_INLET_FAILED: the installed binding " +
                              "exposes no inlet/resolver API.");
                return sb.ToString();
            }

            // A plain scene object, created and destroyed here. Nothing is added to the
            // experiment scene and nothing is saved.
            var host = new GameObject("__IKEA_EEG_AuraCheck") { hideFlags = HideFlags.HideAndDontSave };

            try
            {
                var receiver = host.AddComponent<AuraLslReceiver>();
                receiver.Configure(resolveTimeoutSeconds: 3f, receiveContinuously: false);

                if (!receiver.Connect(out var problem))
                {
                    sb.AppendLine($"  FAIL — {problem}");
                    sb.AppendLine();
                    sb.AppendLine($"  Looked for a stream named exactly '{AuraLslReceiver.StreamName}'.");
                    sb.AppendLine();

                    // What IS visible. "AURA is off", "AURA publishes under another name" and
                    // "this machine sees no LSL traffic at all" need completely different
                    // fixes, and only this distinguishes them.
                    sb.AppendLine("  Streams visible from this machine right now:");

                    var visible = LslBinding.ResolveAllStreams(5.0, out var listDetail);

                    if (visible.Length == 0)
                    {
                        sb.AppendLine($"    (none — {listDetail})");
                        sb.AppendLine();
                        sb.AppendLine("    No LSL traffic of ANY kind is reaching this machine.");
                        sb.AppendLine("    Check: AURA running and LSL Stream Out enabled; both");
                        sb.AppendLine("    machines on the SAME subnet; Windows Firewall allowing");
                        sb.AppendLine("    Unity and liblsl on the private network (LSL discovery");
                        sb.AppendLine("    is UDP multicast and is blocked by default on public");
                        sb.AppendLine("    networks).");
                    }
                    else
                    {
                        foreach (var stream in visible)
                            sb.AppendLine($"    {stream}");

                        sb.AppendLine();
                        sb.AppendLine("    LSL discovery IS working — the network and firewall are");
                        sb.AppendLine($"    fine. No stream is named exactly '{AuraLslReceiver.StreamName}'.");
                        sb.AppendLine("    If one of the streams above is the EEG, AURA is publishing");
                        sb.AppendLine("    it under a different name than expected.");
                    }

                    return sb.ToString();
                }

                var meta = receiver.metadata;

                sb.AppendLine();
                sb.AppendLine("  ===== AURA LSL STREAM FOUND =====");
                sb.AppendLine($"  name:          {meta.name}");
                sb.AppendLine($"  type:          {meta.type}");
                sb.AppendLine($"  channels:      {meta.channelCount}");
                sb.AppendLine($"  sampling rate: " + (meta.hasRegularRate
                    ? $"{meta.nominalSrate.ToString("F3", CultureInfo.InvariantCulture)} Hz"
                    : "irregular (0 = no nominal rate advertised)"));
                sb.AppendLine($"  format:        {meta.channelFormat}");
                sb.AppendLine($"  source id:     " +
                              (string.IsNullOrEmpty(meta.sourceId) ? "(none published)" : meta.sourceId));
                sb.AppendLine("  =================================");
                sb.AppendLine();
                sb.AppendLine($"  Reading as {LslBinding.ChannelElementType(meta.channelFormatValue)?.Name}" +
                              $"[{meta.channelCount}] — element type and buffer size both come " +
                              "from the stream, not from an assumption.");
                sb.AppendLine();

                // A SMALL batch, printed in full. Continuously logging EEG would flood the
                // Console and cost more than the reception itself.
                var samples = new List<RawEegSample>();

                for (var i = 0; i < k_AuraDiagnosticSamples; i++)
                {
                    // Small finite wait per sample — never indefinite.
                    if (!receiver.TryReceiveOne(1.0, out var sample, out var error))
                    {
                        if (!string.IsNullOrEmpty(error))
                            sb.AppendLine($"  pull error: {error}");

                        break;
                    }

                    samples.Add(sample);
                }

                if (samples.Count == 0)
                {
                    sb.AppendLine("  FAIL — AURA_FOUND_BUT_NO_SAMPLES: the stream was resolved and " +
                                  "an inlet opened, but no sample arrived.");
                    sb.AppendLine("  The outlet exists but is not currently sending — check that " +
                                  "the headset is connected and streaming in AURA.");
                    return sb.ToString();
                }

                sb.AppendLine($"  Received {samples.Count} real sample(s):");

                for (var i = 0; i < samples.Count; i++)
                {
                    sb.AppendLine();
                    sb.AppendLine($"  Sample {i + 1}");
                    sb.AppendLine($"    lsl_time = " +
                                  samples[i].lslTimestamp.ToString("F6", CultureInfo.InvariantCulture));

                    for (var c = 0; c < samples[i].channels.Length; c++)
                    {
                        sb.AppendLine($"    CH{c + 1} = " +
                                      samples[i].channels[c].ToString("F6", CultureInfo.InvariantCulture));
                    }
                }

                // Timestamps are the thing that will later align EEG with markers, so their
                // sanity is worth reporting now rather than discovering later.
                var increasing = true;
                for (var i = 1; i < samples.Count; i++)
                {
                    if (samples[i].lslTimestamp <= samples[i - 1].lslTimestamp)
                        increasing = false;
                }

                var span = samples[samples.Count - 1].lslTimestamp - samples[0].lslTimestamp;

                sb.AppendLine();
                sb.AppendLine($"  timestamps non-zero:   {samples.TrueForAll(s => s.lslTimestamp != 0d)}");
                sb.AppendLine($"  timestamps increasing: {increasing}");
                sb.AppendLine($"  span over {samples.Count} samples: " +
                              $"{span.ToString("F6", CultureInfo.InvariantCulture)} s");

                if (meta.hasRegularRate && samples.Count > 1)
                {
                    var expected = (samples.Count - 1) / meta.nominalSrate;
                    sb.AppendLine($"  expected at {meta.nominalSrate:F0} Hz: " +
                                  $"{expected.ToString("F6", CultureInfo.InvariantCulture)} s");
                }

                sb.AppendLine();
                sb.AppendLine("  PASS — real raw EEG samples received from AURA.");
                sb.AppendLine("  NOTE: this is reception only. No filtering, no spectra, no band " +
                              "power and no experiment coupling exist yet.");

                return sb.ToString();
            }
            catch (System.Exception e)
            {
                sb.AppendLine($"  FAIL — {e.GetType().Name}: {e.Message}");
                return sb.ToString();
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        public static string LoopbackTestReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== LSL MARKER LOOPBACK TEST =====");

            if (!LslBinding.isAvailable)
            {
                sb.AppendLine("  SKIPPED — no LSL library is installed.");
                sb.AppendLine($"  detail: {LslBinding.detail}");
                sb.AppendLine("  This is NOT a failure of the experiment: markers are simply not " +
                              "available yet.");
                return sb.ToString();
            }

            if (!LslBinding.CanRunLoopbackTest)
            {
                sb.AppendLine("  SKIPPED — the installed LSL binding exposes no inlet/resolve API, " +
                              "so a receive side cannot be built in-process.");
                sb.AppendLine("  Verify instead with LabRecorder or an external LSL viewer.");
                return sb.ToString();
            }

            const string streamName = "IKEA_EEG_Markers_LoopbackTest";
            const string marker = "CHAIR_SELECTED|trial=2|chair=Chair_04|correct=1";

            var outlet = LslBinding.CreateOutlet(streamName, "Markers",
                "IKEA_EEG_Unity_Markers_LoopbackTest", out var outletDetail);

            if (outlet == null)
            {
                sb.AppendLine($"  FAIL — could not create an outlet: {outletDetail}");
                return sb.ToString();
            }

            sb.AppendLine($"  ok    outlet created ({outletDetail})");

            object inlet = null;

            try
            {
                var info = LslBinding.ResolveStreamInfo(streamName, 5.0, out var resolveDetail);
                if (info == null)
                {
                    sb.AppendLine($"  FAIL — the outlet was not resolvable: {resolveDetail}");
                    return sb.ToString();
                }

                sb.AppendLine($"  ok    stream resolved ({resolveDetail})");

                inlet = LslBinding.CreateInlet(info, out var inletDetail);
                if (inlet == null)
                {
                    sb.AppendLine($"  FAIL — could not open an inlet: {inletDetail}");
                    return sb.ToString();
                }

                sb.AppendLine($"  ok    inlet opened ({inletDetail})");

                // WAIT for the consumer to actually attach before pushing.
                //
                // Opening an inlet returns as soon as the object exists; the subscription to the
                // outlet completes asynchronously a moment later. LSL does not buffer a sample
                // for a consumer that was not yet attached when it was pushed, so pushing
                // immediately is a race — and it is a race this test lost intermittently,
                // reporting "nothing received" for a transport that was working perfectly.
                //
                // have_consumers() is the outlet's own answer to "is anyone listening", so this
                // waits for the real condition rather than sleeping a hopeful fixed interval.
                var attachDeadline = System.DateTime.UtcNow.AddSeconds(3);
                var consumerAttached = false;

                while (System.DateTime.UtcNow < attachDeadline)
                {
                    if (LslBinding.HaveConsumers(outlet))
                    {
                        consumerAttached = true;
                        break;
                    }

                    System.Threading.Thread.Sleep(20);
                }

                sb.AppendLine(consumerAttached
                    ? "  ok    inlet subscribed to the outlet (have_consumers)"
                    : "  info  the outlet still reports no consumer; pushing anyway");

                if (!LslBinding.PushSample(outlet, new[] { marker }, out var pushError))
                {
                    sb.AppendLine($"  FAIL — push_sample failed: {pushError}");
                    return sb.ToString();
                }

                sb.AppendLine($"  ok    marker pushed: {marker}");

                var received = LslBinding.PullSample(inlet, 5.0, out var pullDetail);

                if (received == marker)
                {
                    sb.AppendLine($"  ok    marker received unchanged ({pullDetail})");
                    sb.AppendLine("  PASS — real LSL transmission verified in-process.");
                }
                else if (received == null)
                {
                    sb.AppendLine($"  FAIL — nothing received: {pullDetail}");
                }
                else
                {
                    sb.AppendLine($"  FAIL — received '{received}', expected '{marker}'");
                }
            }
            finally
            {
                LslBinding.Dispose(inlet);
                LslBinding.Dispose(outlet);
            }

            return sb.ToString();
        }
    }
}
