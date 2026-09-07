using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using IkeaEeg.Data;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// The operator front end for <see cref="EegRecordedWindowValidator"/>: five fields, one
    /// button, a text report and a plot of the epoch with t=0 marked.
    ///
    /// Deliberately small. This is a validation aid for one epoch, not an analysis dashboard —
    /// it draws what was extracted so the numbers in the report can be seen to be true, and
    /// stops there.
    /// </summary>
    public class EegRecordedWindowValidatorWindow : EditorWindow
    {
        EegRecordedWindowValidator.Request m_Request;
        string m_Report = string.Empty;
        EegWindow m_Window;
        bool m_HasWindow;
        Vector2 m_Scroll;
        int m_Channel = -1;          // -1 = all channels
        bool m_UseLocalRaw;

        public static void Open()
        {
            var window = GetWindow<EegRecordedWindowValidatorWindow>(
                utility: false, title: "EEG Event Window", focus: true);

            window.minSize = new Vector2(720f, 560f);
            window.Show();
        }

        void OnEnable()
        {
            if (string.IsNullOrEmpty(m_Request.sessionId))
                m_Request = EegRecordedWindowValidator.DefaultRequest();
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Validate one event-centred EEG epoch from a RECORDED " +
                                       "session", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "Cuts the window with the project's existing extractor " +
                "(RawEegRingBuffer.TryGetWindow) — the same code the live path uses. " +
                "Reads only; nothing is written and no recording is modified.",
                MessageType.None);

            EditorGUILayout.Space();

            m_Request.sessionId = EditorGUILayout.TextField(
                new GUIContent("Session id", "Folder name under IKEA_EEG_Data."),
                m_Request.sessionId);

            m_Request.label = EditorGUILayout.TextField(
                new GUIContent("Label", "Free text shown on the report and the plot."),
                m_Request.label);

            m_Request.eventTimestamp = EditorGUILayout.DoubleField(
                new GUIContent("Event lsl_timestamp",
                    "The lsl_timestamp column of the event row in events_*.csv."),
                m_Request.eventTimestamp);

            m_Request.preSeconds = EditorGUILayout.DoubleField(
                new GUIContent("Pre (s)", "Seconds before the event."), m_Request.preSeconds);

            m_Request.postSeconds = EditorGUILayout.DoubleField(
                new GUIContent("Post (s)", "Seconds after the event."), m_Request.postSeconds);

            m_UseLocalRaw = EditorGUILayout.Toggle(
                new GUIContent("Cut on local_raw instead",
                    "Off = lsl_timestamp_analysis, the de-jittered base the file's own header " +
                    "names for cutting epochs. On = the unsmoothed local clock."),
                m_UseLocalRaw);

            m_Request.timestampColumn = m_UseLocalRaw
                ? EegRecordedWindowValidator.LocalRawColumn
                : EegRecordedWindowValidator.AnalysisColumn;

            m_Request.compareWithLocalRaw = EditorGUILayout.Toggle(
                new GUIContent("Cross-check both time bases",
                    "Reports how far the analysis base and the raw local clock disagree."),
                m_Request.compareWithLocalRaw);

            m_Request.reconstructAnalysis = EditorGUILayout.Toggle(
                new GUIContent("Reconstruct analysis from local_raw",
                    "Re-derives the de-jittered timeline from the recorded local_raw column " +
                    "using the project's own EegAnalysisTimebase. Turn this ON for sessions " +
                    "recorded before the clock-domain fix. The file on disk is never modified."),
                m_Request.reconstructAnalysis);

            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Validate window", GUILayout.Height(28f)))
                    Run();

                if (GUILayout.Button("Reset to HARBOR case", GUILayout.Height(28f),
                        GUILayout.Width(180f)))
                {
                    m_Request = EegRecordedWindowValidator.DefaultRequest();
                    m_UseLocalRaw = false;
                    m_Report = string.Empty;
                    m_HasWindow = false;
                }
            }

            EditorGUILayout.Space();

            if (m_HasWindow && m_Window.sampleCount > 0)
                DrawPlot();

            if (string.IsNullOrEmpty(m_Report))
                return;

            EditorGUILayout.Space();

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll, GUILayout.MinHeight(180f));
            EditorGUILayout.TextArea(m_Report, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            if (GUILayout.Button("Copy report to clipboard"))
                EditorGUIUtility.systemCopyBuffer = m_Report;
        }

        void Run()
        {
            m_Report = EegRecordedWindowValidator.BuildReport(m_Request, out m_Window);
            m_HasWindow = m_Window.sampleCount > 0;
            m_Channel = Mathf.Clamp(m_Channel, -1, m_Window.channelCount - 1);

            Debug.Log(m_Report);
            Repaint();
        }

        // ---------------------------------------------------------------------------------
        // Plot
        // ---------------------------------------------------------------------------------

        void DrawPlot()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Channel", GUILayout.Width(60f));

                var names = new string[m_Window.channelCount + 1];
                names[0] = "All (stacked)";

                for (var c = 0; c < m_Window.channelCount; c++)
                    names[c + 1] = $"ch{c + 1}";

                m_Channel = EditorGUILayout.Popup(m_Channel + 1, names) - 1;

                GUILayout.FlexibleSpace();

                EditorGUILayout.LabelField(
                    "channels are STREAM ORDER — no electrode map is claimed",
                    EditorStyles.miniLabel, GUILayout.Width(340f));
            }

            var rect = GUILayoutUtility.GetRect(10f, 100000f, 220f, 220f);
            DrawEpoch(rect, m_Window, m_Channel, m_Request);
        }

        static void DrawEpoch(Rect rect, EegWindow window,
            int channel, EegRecordedWindowValidator.Request request)
        {
            EditorGUI.DrawRect(rect, new Color(0.11f, 0.11f, 0.13f));

            if (window.timestamps == null || window.sampleCount < 2)
                return;

            var evt = window.eventTimestamp;
            var t0 = window.requestedStart - evt;
            var t1 = window.requestedEnd - evt;
            var span = t1 - t0;

            if (span <= 0d)
                return;

            Handles.BeginGUI();

            // ---- grid: one line per 0.5 s ---------------------------------------------------
            Handles.color = new Color(1f, 1f, 1f, 0.07f);

            for (var s = Mathf.Ceil((float)t0 * 2f) / 2f; s <= (float)t1; s += 0.5f)
            {
                var x = rect.x + (float)((s - t0) / span) * rect.width;
                Handles.DrawLine(new Vector3(x, rect.y), new Vector3(x, rect.yMax));
            }

            // ---- the channels ---------------------------------------------------------------
            var first = channel < 0 ? 0 : channel;
            var last = channel < 0 ? window.channelCount - 1 : channel;
            var lanes = last - first + 1;
            var laneHeight = rect.height / lanes;

            for (var c = first; c <= last; c++)
            {
                // Each lane is auto-scaled to its OWN range: the recorded values are raw ADC
                // units with large per-channel DC offsets, so a shared scale would flatten
                // every channel but one. This is a shape check, not an amplitude comparison.
                var min = double.MaxValue;
                var max = double.MinValue;

                for (var i = 0; i < window.sampleCount; i++)
                {
                    var v = window.samples[i][c];
                    if (v < min) min = v;
                    if (v > max) max = v;
                }

                var range = max - min;
                if (range <= 0d)
                    range = 1d;

                var laneY = rect.y + (c - first) * laneHeight;
                var pad = laneHeight * 0.12f;

                Handles.color = new Color(0.45f, 0.75f, 1f, 0.95f);

                var previous = Vector3.zero;

                for (var i = 0; i < window.sampleCount; i++)
                {
                    var rel = window.timestamps[i] - evt;
                    var x = rect.x + (float)((rel - t0) / span) * rect.width;
                    var norm = (float)((window.samples[i][c] - min) / range);
                    var y = laneY + laneHeight - pad - norm * (laneHeight - 2f * pad);

                    var point = new Vector3(x, y);

                    if (i > 0)
                        Handles.DrawLine(previous, point);

                    previous = point;
                }

                if (lanes > 1)
                {
                    Handles.color = new Color(1f, 1f, 1f, 0.10f);
                    Handles.DrawLine(new Vector3(rect.x, laneY), new Vector3(rect.xMax, laneY));
                }

                GUI.Label(new Rect(rect.x + 4f, laneY + 2f, 60f, 14f), $"ch{c + 1}",
                    EditorStyles.miniLabel);
            }

            // ---- t = 0 ----------------------------------------------------------------------
            var eventX = rect.x + (float)((0d - t0) / span) * rect.width;

            Handles.color = new Color(1f, 0.35f, 0.35f, 0.95f);
            Handles.DrawLine(new Vector3(eventX, rect.y), new Vector3(eventX, rect.yMax));

            Handles.EndGUI();

            GUI.Label(new Rect(eventX + 4f, rect.y + 2f, 260f, 16f),
                $"t = 0   {request.label}", EditorStyles.whiteMiniLabel);

            GUI.Label(new Rect(rect.x + 4f, rect.yMax - 16f, 120f, 14f),
                $"{t0.ToString("0.0", CultureInfo.InvariantCulture)} s",
                EditorStyles.whiteMiniLabel);

            GUI.Label(new Rect(rect.xMax - 60f, rect.yMax - 16f, 60f, 14f),
                $"+{t1.ToString("0.0", CultureInfo.InvariantCulture)} s",
                EditorStyles.whiteMiniLabel);

            GUI.Label(new Rect(rect.x + 4f, rect.y + 2f, 400f, 14f),
                $"{window.sampleCount} samples x {window.channelCount} ch   " +
                $"status {window.status}", EditorStyles.whiteMiniLabel);
        }
    }
}
