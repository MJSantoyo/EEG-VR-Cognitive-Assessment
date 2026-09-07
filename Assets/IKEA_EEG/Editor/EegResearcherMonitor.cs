using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using IkeaEeg.Data;
using IkeaEeg.Experiment;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// The researcher's live EEG dashboard — an Editor window on the desktop, never in the
    /// headset.
    ///
    /// ============================== STRICTLY READ-ONLY ==============================
    /// It OBSERVES the scene's existing pipeline and changes nothing. It does not open an LSL
    /// inlet, construct a receiver, allocate a raw buffer, run a filter or compute a spectrum of
    /// its own — it finds the one EegFeaturePipeline already in the scene and reads what that
    /// pipeline has already produced. A second inlet would double the network load and give two
    /// disagreeing views of the same signal; a second filter would disagree in its state.
    ///
    /// It starts nothing, stops nothing, triggers no event, writes no data and cannot alter the
    /// experiment, the difficulty, the timestamps or a single sample.
    ///
    /// PERFORMANCE: repaints at ~15 Hz, not at the 250 Hz sample rate. Traces are DOWNSAMPLED
    /// COPIES made for drawing only — the analysis data is never touched. Buffers for the plot
    /// are allocated once and reused.
    /// ==============================================================================
    /// </summary>
    public class EegResearcherMonitor : EditorWindow
    {
        const double k_TraceSeconds = 8.0;
        const int k_MaxPointsPerTrace = 400;     // plenty for an 8 s overview at this width
        const double k_RepaintHz = 15.0;

        bool m_ShowFiltered = true;
        double m_LastRepaint;
        Vector2 m_Scroll;

        /// <summary>Reused across repaints so the window allocates nothing per frame.</summary>
        readonly List<Vector3> m_PlotPoints = new List<Vector3>(k_MaxPointsPerTrace);

        [MenuItem("IKEA_EEG/EEG/Researcher EEG Monitor", false, 144)]
        public static void Open()
        {
            var window = GetWindow<EegResearcherMonitor>("EEG Monitor");
            window.minSize = new Vector2(820f, 560f);
            window.Show();
        }

        void OnEnable()
        {
            EditorApplication.update += Tick;
        }

        void OnDisable()
        {
            EditorApplication.update -= Tick;
        }

        /// <summary>Repaint on a timer rather than every editor tick.</summary>
        void Tick()
        {
            var now = EditorApplication.timeSinceStartup;

            if (now - m_LastRepaint < 1.0 / k_RepaintHz)
                return;

            m_LastRepaint = now;
            Repaint();
        }

        /// <summary>
        /// The scene's pipeline, or null.
        ///
        /// FindAnyObjectByType, not construction: the window attaches to what exists and creates
        /// nothing. When the experiment is not running there is simply nothing to show.
        /// </summary>
        static EegFeaturePipeline FindPipeline()
        {
            return Object.FindAnyObjectByType<EegFeaturePipeline>();
        }

        static AuraLslReceiver FindReceiver()
        {
            return Object.FindAnyObjectByType<AuraLslReceiver>();
        }

        void OnGUI()
        {
            var pipeline = FindPipeline();
            var receiver = FindReceiver();

            DrawStatusBar(receiver, pipeline);

            if (receiver == null || pipeline == null)
            {
                EditorGUILayout.HelpBox(
                    "No EEG pipeline in the open scene.\n\n" +
                    "This window is a VIEW ONLY — it never creates a receiver, an inlet or a " +
                    "processing pipeline of its own. Open the experiment scene and enter Play " +
                    "Mode, or use IKEA_EEG > EEG > Analyze Spectral Window for a one-shot " +
                    "measurement.",
                    MessageType.Info);
                return;
            }

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);

            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical();
            DrawTraces(pipeline, receiver);
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(GUILayout.Width(260f));
            DrawFeaturePanel(pipeline);
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            DrawFooter(pipeline);

            EditorGUILayout.EndScrollView();
        }

        // ---------------------------------------------------------------------------------

        void DrawStatusBar(AuraLslReceiver receiver, EegFeaturePipeline pipeline)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var connected = receiver != null && receiver.isConnected;

            EditorGUILayout.BeginHorizontal();

            var previous = GUI.color;
            GUI.color = connected ? new Color(0.5f, 1f, 0.6f) : new Color(1f, 0.6f, 0.5f);
            EditorGUILayout.LabelField(connected ? "● AURA CONNECTED" : "● AURA NOT CONNECTED",
                EditorStyles.boldLabel, GUILayout.Width(190f));
            GUI.color = previous;

            if (receiver != null && connected)
            {
                var meta = receiver.metadata;

                EditorGUILayout.LabelField($"{meta.name}", GUILayout.Width(70f));
                EditorGUILayout.LabelField(
                    $"{meta.nominalSrate.ToString("F0", CultureInfo.InvariantCulture)} Hz",
                    GUILayout.Width(60f));
                EditorGUILayout.LabelField($"{meta.channelCount} ch", GUILayout.Width(50f));

                EditorGUILayout.LabelField(
                    receiver.hasTimeCorrection ? "LSL synced" : "LSL NOT synced",
                    GUILayout.Width(100f));

                var stats = receiver.buffer?.GetStats();

                if (stats.HasValue)
                {
                    EditorGUILayout.LabelField(
                        stats.Value.timestampsMonotonic ? "timebase OK" : "timebase NON-MONOTONIC",
                        GUILayout.Width(150f));
                }
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            var ready = pipeline != null && pipeline.filterReady;

            GUI.color = ready ? new Color(0.5f, 1f, 0.6f) : new Color(1f, 0.85f, 0.5f);
            EditorGUILayout.LabelField(
                ready ? "filter settled" : "FILTER SETTLING — features not yet valid",
                GUILayout.Width(280f));
            GUI.color = previous;

            if (pipeline != null)
            {
                EditorGUILayout.LabelField(
                    $"{pipeline.samplesFiltered}/{pipeline.settlingSamples} samples " +
                    $"({pipeline.settlingSeconds:F1} s required)");
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        // ---------------------------------------------------------------------------------

        void DrawTraces(EegFeaturePipeline pipeline, AuraLslReceiver receiver)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Last {k_TraceSeconds:F0} s", EditorStyles.boldLabel,
                GUILayout.Width(80f));

            // A VIEW toggle: it selects which existing buffer to read, and never re-filters.
            m_ShowFiltered = GUILayout.Toggle(m_ShowFiltered, "Filtered", EditorStyles.miniButtonLeft,
                GUILayout.Width(70f));
            m_ShowFiltered = !GUILayout.Toggle(!m_ShowFiltered, "Raw", EditorStyles.miniButtonRight,
                GUILayout.Width(70f));

            var montage = pipeline.montage;

            EditorGUILayout.LabelField(
                montage != null ? montage.amplitudeUnitLabel : "AURA native units");

            EditorGUILayout.EndHorizontal();

            // Read whichever buffer the researcher asked for. Both already exist.
            var buffer = m_ShowFiltered ? pipeline.filteredBuffer : receiver.buffer;

            if (buffer == null)
            {
                EditorGUILayout.HelpBox("That buffer is not available yet.", MessageType.None);
                return;
            }

            var stats = buffer.GetStats();

            if (stats.bufferedSamples < 2)
            {
                EditorGUILayout.HelpBox("Waiting for samples…", MessageType.None);
                return;
            }

            if (!buffer.TryGetWindow(stats.latestTimestamp - k_TraceSeconds * 0.5,
                    k_TraceSeconds * 0.5, k_TraceSeconds * 0.5, out var window) ||
                window.sampleCount < 2)
            {
                EditorGUILayout.HelpBox("Waiting for a full trace window…", MessageType.None);
                return;
            }

            for (var c = 0; c < window.channelCount; c++)
            {
                var label = montage != null ? montage.LabelOfIndex(c) : $"CH{c + 1}";
                DrawTrace(string.IsNullOrEmpty(label) ? $"CH{c + 1}" : label, window, c);
            }
        }

        void DrawTrace(string label, EegWindow window, int channel)
        {
            var rect = GUILayoutUtility.GetRect(400f, 52f, GUILayout.ExpandWidth(true));

            EditorGUI.DrawRect(rect, new Color(0.13f, 0.13f, 0.15f));

            var labelRect = new Rect(rect.x + 4f, rect.y + 2f, 46f, 16f);
            EditorGUI.LabelField(labelRect, label, EditorStyles.miniBoldLabel);

            // DOWNSAMPLE FOR DISPLAY ONLY. This copy is thrown away each repaint; the analysis
            // data is untouched and unaware of it.
            var stride = Mathf.Max(1, window.sampleCount / k_MaxPointsPerTrace);

            double min = double.MaxValue, max = double.MinValue;

            for (var i = 0; i < window.sampleCount; i += stride)
            {
                var v = window.samples[i][channel];

                if (double.IsNaN(v) || double.IsInfinity(v))
                    continue;

                min = System.Math.Min(min, v);
                max = System.Math.Max(max, v);
            }

            if (min >= max)
            {
                EditorGUI.LabelField(new Rect(rect.x + 56f, rect.y + 18f, 200f, 16f),
                    "flat / no data", EditorStyles.miniLabel);
                return;
            }

            var span = max - min;
            var plotX = rect.x + 54f;
            var plotW = rect.width - 60f;

            m_PlotPoints.Clear();

            var index = 0;
            for (var i = 0; i < window.sampleCount; i += stride, index++)
            {
                var v = window.samples[i][channel];

                if (double.IsNaN(v) || double.IsInfinity(v))
                    v = min;

                var t = (float)(index * stride) / Mathf.Max(1, window.sampleCount - 1);
                var norm = (float)((v - min) / span);

                m_PlotPoints.Add(new Vector3(
                    plotX + t * plotW,
                    rect.y + rect.height - 6f - norm * (rect.height - 14f),
                    0f));
            }

            Handles.BeginGUI();
            Handles.color = m_ShowFiltered
                ? new Color(0.55f, 0.85f, 1f)
                : new Color(0.85f, 0.85f, 0.55f);

            if (m_PlotPoints.Count > 1)
                Handles.DrawAAPolyLine(1.5f, m_PlotPoints.ToArray());

            Handles.EndGUI();

            // Range in native units — never labelled µV.
            EditorGUI.LabelField(
                new Rect(rect.xMax - 150f, rect.y + 2f, 146f, 16f),
                $"{min:E2} … {max:E2}", EditorStyles.miniLabel);
        }

        // ---------------------------------------------------------------------------------

        void DrawFeaturePanel(EegFeaturePipeline pipeline)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("SPECTRAL FEATURES", EditorStyles.boldLabel);

            var features = pipeline.latest;

            if (features == null)
            {
                EditorGUILayout.LabelField("no window analysed yet", EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Use Analyze Spectral Window,", EditorStyles.miniLabel);
                EditorGUILayout.LabelField("or wait for the runtime.", EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
                return;
            }

            var previous = GUI.color;
            GUI.color = features.featureValidity
                ? new Color(0.5f, 1f, 0.6f)
                : new Color(1f, 0.75f, 0.5f);

            EditorGUILayout.LabelField(features.featureValidity ? "VALID" : "NOT VALID",
                EditorStyles.boldLabel);
            GUI.color = previous;

            EditorGUILayout.LabelField(features.QualityText(), EditorStyles.wordWrappedMiniLabel);

            // Raised above the numbers rather than left as a flag name, because it invalidates
            // everything below it and the cause is at the electrodes, not in the analysis.
            if (!string.IsNullOrEmpty(features.identicalChannelDetail))
            {
                EditorGUILayout.HelpBox(
                    $"Identical channels:\n{features.identicalChannelDetail}\n\n" +
                    "Real electrodes cannot produce bit-identical signals. Check connection, " +
                    "impedance, reference/ground and any amplifier test-signal mode.",
                    MessageType.Error);
            }

            EditorGUILayout.Space(4f);

            if (features.roiValid)
            {
                EditorGUILayout.LabelField("Frontal Theta", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField($"  {features.frontalTheta:E4}");
                EditorGUILayout.LabelField($"  {features.powerUnits}", EditorStyles.miniLabel);

                EditorGUILayout.Space(2f);

                EditorGUILayout.LabelField("Posterior Alpha", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField($"  {features.posteriorAlpha:E4}");
                EditorGUILayout.LabelField($"  {features.powerUnits}", EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.HelpBox($"ROI unavailable:\n{features.roiProblem}",
                    MessageType.Warning);
            }

            if (features.derivedValid)
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("EXPLORATORY", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField($"  ratio {features.thetaAlphaRatio:F3}",
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"  log   {features.logThetaAlpha:F3}",
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField("  not a validated score", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Baseline", EditorStyles.miniBoldLabel);

            if (features.baselineAvailable)
            {
                EditorGUILayout.LabelField($"  Δθ {features.deltaThetaDb:F2} dB",
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"  Δα {features.deltaAlphaDb:F2} dB",
                    EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField("  unavailable", EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        // ---------------------------------------------------------------------------------

        void DrawFooter(EegFeaturePipeline pipeline)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Experiment state is READ from the existing manager. Nothing is duplicated and
            // nothing is written back.
            var manager = Object.FindAnyObjectByType<ExperimentManager>();

            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.LabelField("PHASE:", EditorStyles.miniBoldLabel, GUILayout.Width(50f));
            EditorGUILayout.LabelField(manager != null ? manager.state.ToString() : "no experiment",
                GUILayout.Width(180f));

            var features = pipeline.latest;

            if (features != null)
            {
                EditorGUILayout.LabelField(
                    $"window {features.windowSeconds:F1} s / {features.sampleCount} samples",
                    GUILayout.Width(220f));

                EditorGUILayout.LabelField($"units: {features.amplitudeUnits}");
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }
    }
}
