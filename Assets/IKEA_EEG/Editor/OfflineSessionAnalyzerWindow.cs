using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using IkeaEeg.Data;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Menu entry, session picker and batch entry point for <see cref="OfflineSessionAnalyzer"/>.
    ///
    /// The preprocessing values default to whatever the experiment scene's EegFeaturePipeline
    /// actually carries, read through SerializedObject. Whether that read succeeded is recorded in
    /// <see cref="OfflineSessionAnalyzer.Options.configSource"/> and printed in the report, so a
    /// report can never imply the live configuration was used when the scene could not be read and
    /// built-in defaults were substituted instead.
    /// </summary>
    public class OfflineSessionAnalyzerWindow : EditorWindow
    {
        const string k_ScenePath = "Assets/IKEA_EEG/Scenes/IKEA_EEG_Experiment.unity";

        [MenuItem("IKEA_EEG/EEG/Offline Session Analysis…", false, 146)]
        public static void Open()
        {
            var window = GetWindow<OfflineSessionAnalyzerWindow>(true, "Offline Session Analysis");
            window.minSize = new Vector2(720f, 560f);
            window.Refresh();
            window.Show();
        }

        // ---------------------------------------------------------------------------------
        // State
        // ---------------------------------------------------------------------------------

        class SessionEntry
        {
            internal string sessionId = string.Empty;
            internal bool hasRawEeg;
            internal long rawBytes;
            internal bool hasEvents;
            internal int recognitionMarkers;
        }

        readonly List<SessionEntry> m_Sessions = new List<SessionEntry>();
        readonly List<string> m_Log = new List<string>();

        int m_Selected = -1;
        Vector2 m_ListScroll;
        Vector2 m_LogScroll;
        bool m_OnlyWithEeg = true;
        OfflineSessionAnalyzer.Options m_Options = new OfflineSessionAnalyzer.Options();
        string m_ConfigSource = "not read yet";

        // ---------------------------------------------------------------------------------
        // Discovery
        // ---------------------------------------------------------------------------------

        void Refresh()
        {
            m_Sessions.Clear();
            m_Selected = -1;

            var root = EegRecordedWindowValidator.DataRoot();

            if (!Directory.Exists(root))
            {
                m_Log.Add("No session root at " + root);
                return;
            }

            var folders = Directory.GetDirectories(root);
            Array.Sort(folders, StringComparer.Ordinal);
            Array.Reverse(folders);

            foreach (var folder in folders)
            {
                var entry = new SessionEntry { sessionId = Path.GetFileName(folder) };

                var raw = Directory.GetFiles(folder, "raw_eeg*.csv");
                entry.hasRawEeg = raw.Length > 0;

                if (entry.hasRawEeg)
                    entry.rawBytes = new FileInfo(raw[0]).Length;

                var eventFiles = Directory.GetFiles(folder, "events_*.csv");
                entry.hasEvents = eventFiles.Length > 0;

                if (entry.hasEvents)
                {
                    try
                    {
                        // Counting rather than parsing: the picker only needs to say whether the
                        // run used the recognition protocol at all.
                        foreach (var line in File.ReadLines(eventFiles[0]))
                        {
                            if (line.IndexOf("RECOGNITION_ITEM_ONSET", StringComparison.Ordinal) >= 0)
                                entry.recognitionMarkers++;
                        }
                    }
                    catch (Exception)
                    {
                        // A file being written by a live session is not an error here.
                    }
                }

                m_Sessions.Add(entry);
            }

            m_ConfigSource = ReadLiveConfig(m_Options);
            m_Options.configSource = m_ConfigSource;
        }

        // ---------------------------------------------------------------------------------
        // Reading the live preprocessing configuration
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Fills <paramref name="options"/> from the experiment scene's EegFeaturePipeline and
        /// returns a description of where the values came from.
        ///
        /// Reads through SerializedObject rather than reflection because the serialized values are
        /// the ones the built player will use — a private field's compile-time initialiser can
        /// differ from what is stored on the scene, and it is the stored value that matters.
        ///
        /// The scene is opened ADDITIVELY and closed again only when it was not already loaded, so
        /// running this never disturbs whatever the user has open and never marks a scene dirty.
        /// </summary>
        public static string ReadLiveConfig(OfflineSessionAnalyzer.Options options)
        {
            var pipeline = FindPipelineInLoadedScenes();

            if (pipeline != null)
            {
                ApplyFrom(pipeline, options);
                return "EegFeaturePipeline on an already-open scene";
            }

            if (!File.Exists(k_ScenePath))
            {
                return "built-in defaults (" + k_ScenePath + " not found)";
            }

            Scene opened = default;
            var openedHere = false;

            try
            {
                opened = EditorSceneManager.OpenScene(k_ScenePath, OpenSceneMode.Additive);
                openedHere = true;

                pipeline = FindPipelineIn(opened);

                if (pipeline == null)
                    return "built-in defaults (no EegFeaturePipeline in " + k_ScenePath + ")";

                ApplyFrom(pipeline, options);
                return "EegFeaturePipeline serialized on " + k_ScenePath;
            }
            catch (Exception e)
            {
                return "built-in defaults (could not open " + k_ScenePath + ": " + e.Message + ")";
            }
            finally
            {
                if (openedHere && opened.IsValid() && opened.isLoaded)
                {
                    // removeScene: true — the additively loaded copy is discarded entirely.
                    EditorSceneManager.CloseScene(opened, true);
                }
            }
        }

        static EegFeaturePipeline FindPipelineInLoadedScenes()
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var pipeline = FindPipelineIn(SceneManager.GetSceneAt(i));

                if (pipeline != null)
                    return pipeline;
            }

            return null;
        }

        /// <summary>
        /// Walks one scene's roots for the component, INCLUDING inactive objects.
        ///
        /// FindObjectsByType and GameObject.Find both skip inactive objects, and the EEG rig is
        /// routinely left disabled in the editor — a search that used either would report "no
        /// pipeline" for a scene that has one.
        /// </summary>
        static EegFeaturePipeline FindPipelineIn(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                return null;

            foreach (var root in scene.GetRootGameObjects())
            {
                var found = root.GetComponentInChildren<EegFeaturePipeline>(true);

                if (found != null)
                    return found;
            }

            return null;
        }

        static void ApplyFrom(EegFeaturePipeline pipeline, OfflineSessionAnalyzer.Options options)
        {
            var serialized = new SerializedObject(pipeline);

            options.highPassHz = Read(serialized, "m_HighPassHz", options.highPassHz);
            options.lowPassHz = Read(serialized, "m_LowPassHz", options.lowPassHz);
            options.notchHz = Read(serialized, "m_NotchHz", options.notchHz);
            options.notchQ = Read(serialized, "m_NotchQ", options.notchQ);
            options.spectralWindowSeconds =
                Read(serialized, "m_WindowSeconds", options.spectralWindowSeconds);
            options.welchSegmentSeconds =
                Read(serialized, "m_WelchSegmentSeconds", options.welchSegmentSeconds);

            var notch = serialized.FindProperty("m_NotchEnabled");

            if (notch != null)
                options.notchEnabled = notch.boolValue;
        }

        static double Read(SerializedObject serialized, string name, double fallback)
        {
            var property = serialized.FindProperty(name);

            if (property == null)
                return fallback;

            switch (property.propertyType)
            {
                case SerializedPropertyType.Float:
                    return property.doubleValue;

                case SerializedPropertyType.Integer:
                    return property.intValue;

                default:
                    return fallback;
            }
        }

        // ---------------------------------------------------------------------------------
        // GUI
        // ---------------------------------------------------------------------------------

        void OnGUI()
        {
            if (m_Sessions.Count == 0 && Event.current.type == EventType.Layout)
                Refresh();

            EditorGUILayout.LabelField("Offline EEG replay / analysis", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "Reads an already-recorded session from disk and re-runs the project's own " +
                "filter and Welch code over it. Writes plots, CSVs and a Markdown report into " +
                "<session>/" + OfflineSessionAnalyzer.OutputFolderName + "/.\n\n" +
                "Engineering validation only. Nothing produced here is a cognitive or clinical " +
                "measure. Amplitudes stay in AURA native units.",
                MessageType.Info);

            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Preprocessing source", m_ConfigSource);

                if (GUILayout.Button("Re-read", GUILayout.Width(80f)))
                {
                    m_ConfigSource = ReadLiveConfig(m_Options);
                    m_Options.configSource = m_ConfigSource;
                }
            }

            EditorGUILayout.LabelField("Filter",
                m_Options.highPassHz.ToString("F2", CultureInfo.InvariantCulture) + " – " +
                m_Options.lowPassHz.ToString("F2", CultureInfo.InvariantCulture) + " Hz, notch " +
                (m_Options.notchEnabled
                    ? m_Options.notchHz.ToString("F1", CultureInfo.InvariantCulture) + " Hz Q " +
                      m_Options.notchQ.ToString("F1", CultureInfo.InvariantCulture)
                    : "disabled"));

            EditorGUILayout.LabelField("Spectral",
                m_Options.spectralWindowSeconds.ToString("F1", CultureInfo.InvariantCulture) +
                " s window, " +
                m_Options.welchSegmentSeconds.ToString("F1", CultureInfo.InvariantCulture) +
                " s Welch segments, " +
                (m_Options.welchOverlapFraction * 100d).ToString("F0", CultureInfo.InvariantCulture) +
                " % overlap, Hann, FFT " + m_Options.fftLength);

            EditorGUILayout.Space();

            m_OnlyWithEeg = EditorGUILayout.ToggleLeft(
                "Only show sessions that recorded raw EEG", m_OnlyWithEeg);

            EditorGUILayout.LabelField("Sessions", EditorStyles.boldLabel);

            m_ListScroll = EditorGUILayout.BeginScrollView(m_ListScroll, GUILayout.Height(220f));

            for (var i = 0; i < m_Sessions.Count; i++)
            {
                var entry = m_Sessions[i];

                if (m_OnlyWithEeg && !entry.hasRawEeg)
                    continue;

                using (new EditorGUILayout.HorizontalScope())
                {
                    var selected = EditorGUILayout.ToggleLeft(entry.sessionId, m_Selected == i,
                        GUILayout.Width(320f));

                    if (selected)
                        m_Selected = i;

                    EditorGUILayout.LabelField(
                        entry.hasRawEeg
                            ? "EEG " + (entry.rawBytes / 1024 / 1024) + " MB"
                            : "no EEG",
                        GUILayout.Width(90f));

                    EditorGUILayout.LabelField(
                        entry.recognitionMarkers > 0
                            ? entry.recognitionMarkers + " recognition items"
                            : entry.hasEvents ? "events, no recognition" : "no events");
                }
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(m_Selected < 0))
            {
                if (GUILayout.Button("Run offline analysis", GUILayout.Height(30f)))
                    RunSelected();
            }

            if (GUILayout.Button("Refresh session list"))
                Refresh();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);

            m_LogScroll = EditorGUILayout.BeginScrollView(m_LogScroll);

            foreach (var line in m_Log)
                EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.EndScrollView();
        }

        void RunSelected()
        {
            if (m_Selected < 0 || m_Selected >= m_Sessions.Count)
                return;

            var options = m_Options.Clone();
            options.sessionId = m_Sessions[m_Selected].sessionId;

            m_Log.Clear();
            m_Log.Add("Running " + options.sessionId + " …");

            var result = OfflineSessionAnalyzer.Run(options);

            m_Log.Add(result.OneLine());
            m_Log.Add("Output: " + result.outputFolder);

            foreach (var failure in result.failures)
                m_Log.Add("FAILURE: " + failure);

            foreach (var warning in result.warnings)
                m_Log.Add("warning: " + warning);

            foreach (var file in result.filesWritten)
                m_Log.Add("wrote " + Path.GetFileName(file));

            Debug.Log("[IKEA_EEG] Offline analysis: " + result.summary);

            if (!string.IsNullOrEmpty(result.outputFolder) &&
                Directory.Exists(result.outputFolder))
            {
                EditorUtility.RevealInFinder(Path.Combine(result.outputFolder, "report.md"));
            }
        }

        // ---------------------------------------------------------------------------------
        // Batch
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Headless entry point.
        ///
        /// Usage: -executeMethod IkeaEeg.EditorTools.OfflineSessionAnalyzerWindow.RunFromCommandLine
        ///        -offlineSession &lt;session id&gt;   (repeatable)
        ///
        /// With no -offlineSession argument it analyses nothing and says so, rather than picking a
        /// session on the user's behalf.
        /// </summary>
        public static void RunFromCommandLine()
        {
            var sessions = new List<string>();
            var args = Environment.GetCommandLineArgs();

            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-offlineSession", StringComparison.Ordinal))
                    sessions.Add(args[i + 1]);
            }

            if (sessions.Count == 0)
            {
                Debug.LogError("[IKEA_EEG] Offline analysis: no -offlineSession <id> argument was " +
                               "supplied. Nothing was analysed.");
                return;
            }

            var template = new OfflineSessionAnalyzer.Options();
            template.configSource = ReadLiveConfig(template);

            var failures = 0;

            foreach (var sessionId in sessions)
            {
                var options = template.Clone();
                options.sessionId = sessionId;

                var result = OfflineSessionAnalyzer.Run(options);

                Debug.Log("[IKEA_EEG] Offline analysis: " + result.OneLine() + "\n" +
                          "  output: " + result.outputFolder + "\n" +
                          (result.failures.Count > 0
                              ? "  failures: " + string.Join("; ", result.failures) + "\n"
                              : "") +
                          (result.warnings.Count > 0
                              ? "  warnings: " + string.Join("; ", result.warnings)
                              : ""));

                if (!result.ok)
                    failures++;
            }

            Debug.Log("[IKEA_EEG] Offline analysis finished: " + sessions.Count +
                      " session(s), " + failures + " did not complete.");
        }
    }
}
