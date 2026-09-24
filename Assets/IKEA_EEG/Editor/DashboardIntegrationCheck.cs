using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using IkeaEeg.Dashboard;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Drives REAL Play Mode and checks that the researcher dashboard's status file is
    /// produced, refreshed and honest about what it does not know.
    ///
    /// WHY THIS EXISTS. Everything about the dashboard had been exercised against mock data
    /// or against recorded files. The one link never tested was the first one: does Unity,
    /// actually running, actually write the file the dashboard reads? A self test that runs
    /// in edit mode cannot answer that, because the writer is spawned by
    /// RuntimeInitializeOnLoadMethod and driven by the player loop -- neither of which exists
    /// outside Play Mode.
    ///
    /// WHY AN EMPTY SCENE. Deliberate, and the most important safety property here. In Play
    /// Mode Application.isPlaying is true, so EventLogger writes to the REAL participant data
    /// root. Loading the experiment scene would therefore create a real session folder among
    /// the participant runs every time this check ran. An empty scene has no ExperimentManager
    /// and no EventLogger, so no session is ever opened -- and it also produces exactly the
    /// state worth testing hardest: no session, no receiver, no pipeline. A dashboard that
    /// reports that honestly is a dashboard that will not lie when the EEG is missing.
    ///
    /// WHAT THIS DOES NOT PROVE. It does not exercise the experiment, the protocol, AURA, or
    /// any scientific path, and it says nothing about what the status file contains during a
    /// real participant run with hardware attached. It proves the TRANSPORT.
    ///
    /// Nothing here is part of a build, and nothing it does can influence a recorded session.
    /// </summary>
    [InitializeOnLoad]
    public static class DashboardIntegrationCheck
    {
        const string k_Active    = "IkeaEeg.DashCheck.Active";
        const string k_Stage     = "IkeaEeg.DashCheck.Stage";
        const string k_Deadline  = "IkeaEeg.DashCheck.Deadline";
        const string k_NextPoll  = "IkeaEeg.DashCheck.NextPoll";
        const string k_Polls     = "IkeaEeg.DashCheck.Polls";
        const string k_Changes   = "IkeaEeg.DashCheck.Changes";
        const string k_LastLen   = "IkeaEeg.DashCheck.LastLen";
        const string k_LastStamp = "IkeaEeg.DashCheck.LastStamp";
        const string k_Failures  = "IkeaEeg.DashCheck.Failures";
        const string k_ExistedBefore = "IkeaEeg.DashCheck.Existed";

        const string Tag = "[DASHCHECK] ";

        const int    PollsWanted     = 10;     // ~5 s of Play Mode at 0.5 s spacing
        const double PollSpacing     = 0.5;
        const double OverallTimeout  = 180.0;  // hard ceiling so batch mode can never hang

        /// <summary>Re-attaches the tick after the domain reload that entering Play Mode causes.</summary>
        static DashboardIntegrationCheck()
        {
            if (SessionState.GetBool(k_Active, false))
                EditorApplication.update += Tick;
        }

        static string StatusPath => Path.Combine(
            Application.persistentDataPath, ResearcherStatusWriter.FolderName,
            ResearcherStatusWriter.FileName);

        static string TempPath => StatusPath + ".tmp";

        [MenuItem("IKEA_EEG/Validate Dashboard Status Path", false, 41)]
        public static void RunMenu() => Begin(false);

        /// <summary>Batch entry. Run WITHOUT -quit: this quits itself when it is done.</summary>
        public static void RunFromCommandLine() => Begin(true);

        static void Begin(bool quitWhenDone)
        {
            Log("=== dashboard status-path integration check ===");
            Log("status file: " + StatusPath);

            SessionState.SetBool(k_ExistedBefore, File.Exists(StatusPath));
            Log("status file existed before this run: " +
                SessionState.GetBool(k_ExistedBefore, false));

            var dataRoot = Path.Combine(Application.persistentDataPath, "IKEA_EEG_Data");
            Log("participant folders before: " + CountFolders(dataRoot));

            // An EMPTY scene: no ExperimentManager, so Play Mode opens no session and the
            // participant data root is never touched. See the class comment.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Log("opened an empty scene (no ExperimentManager, no EventLogger)");

            SessionState.SetBool(k_Active, true);
            SessionState.SetString(k_Stage, "entering");
            SessionState.SetFloat(k_Deadline, (float)(EditorApplication.timeSinceStartup + OverallTimeout));
            SessionState.SetFloat(k_NextPoll, 0f);
            SessionState.SetInt(k_Polls, 0);
            SessionState.SetInt(k_Changes, 0);
            SessionState.SetInt(k_LastLen, -1);
            SessionState.SetString(k_LastStamp, string.Empty);
            SessionState.SetInt(k_Failures, 0);
            SessionState.SetBool("IkeaEeg.DashCheck.Quit", quitWhenDone);

            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;

            Log("entering Play Mode...");
            EditorApplication.EnterPlaymode();
        }

        static void Tick()
        {
            if (!SessionState.GetBool(k_Active, false))
            {
                EditorApplication.update -= Tick;
                return;
            }

            if (EditorApplication.timeSinceStartup > SessionState.GetFloat(k_Deadline, 0f))
            {
                Fail("TIMEOUT: Play Mode did not complete within " +
                     OverallTimeout.ToString(CultureInfo.InvariantCulture) + " s");
                Finish();
                return;
            }

            var stage = SessionState.GetString(k_Stage, "entering");

            if (stage == "entering")
            {
                if (!EditorApplication.isPlaying)
                    return;

                Log("Play Mode ACTIVE");
                SessionState.SetString(k_Stage, "sampling");
                return;
            }

            if (stage == "sampling")
            {
                if (!EditorApplication.isPlaying)
                {
                    Fail("Play Mode ended unexpectedly during sampling");
                    Finish();
                    return;
                }

                if (EditorApplication.timeSinceStartup < SessionState.GetFloat(k_NextPoll, 0f))
                    return;

                SessionState.SetFloat(k_NextPoll,
                    (float)(EditorApplication.timeSinceStartup + PollSpacing));

                Poll();

                if (SessionState.GetInt(k_Polls, 0) >= PollsWanted)
                {
                    SessionState.SetString(k_Stage, "leaving");
                    Log("leaving Play Mode...");
                    EditorApplication.ExitPlaymode();
                }

                return;
            }

            if (stage == "leaving")
            {
                if (EditorApplication.isPlaying)
                    return;

                Log("Play Mode STOPPED");
                Summarise();
                Finish();
            }
        }

        static void Poll()
        {
            var polls = SessionState.GetInt(k_Polls, 0) + 1;
            SessionState.SetInt(k_Polls, polls);

            if (!File.Exists(StatusPath))
            {
                Log($"poll {polls}: status.json ABSENT");
                return;
            }

            string payload;

            try
            {
                payload = File.ReadAllText(StatusPath);
            }
            catch (IOException e)
            {
                // Would mean the reader can catch a half-written file: the exact failure the
                // temp-and-move write exists to prevent.
                Fail("poll " + polls + ": status.json could not be read: " + e.Message);
                return;
            }

            var len = payload.Length;
            var stamp = Extract(payload, "\"written_utc\":\"");

            if (SessionState.GetInt(k_LastLen, -1) >= 0 &&
                stamp != SessionState.GetString(k_LastStamp, string.Empty))
            {
                SessionState.SetInt(k_Changes, SessionState.GetInt(k_Changes, 0) + 1);
            }

            SessionState.SetInt(k_LastLen, len);
            SessionState.SetString(k_LastStamp, stamp);

            // A stray .tmp visible to a reader would mean the move is not atomic.
            if (File.Exists(TempPath))
                Fail("poll " + polls + ": a .tmp file was visible beside status.json");

            Log($"poll {polls}: {len} bytes, written_utc={stamp}, " +
                $"source={Extract(payload, "\"data_source\":\"")}, " +
                $"phase={Extract(payload, "\"phase\":\"")}, " +
                $"connected={ExtractRaw(payload, "\"connected\":")}, " +
                $"present={ExtractRaw(payload, "\"present\":")}");
        }

        static void Summarise()
        {
            var polls = SessionState.GetInt(k_Polls, 0);
            var changes = SessionState.GetInt(k_Changes, 0);

            Check(File.Exists(StatusPath), "status.json exists after Play Mode");

            if (!SessionState.GetBool(k_ExistedBefore, false) && File.Exists(StatusPath))
                Log("PASS: status.json was CREATED by this Play Mode run (absent beforehand)");

            Check(polls >= PollsWanted, $"sampled {polls} times during Play Mode");
            Check(changes >= 3,
                $"the snapshot was refreshed {changes} time(s) while playing " +
                "(the writer is running on the player loop, not written once)");

            Check(!File.Exists(TempPath), "no .tmp file is left behind (atomic move completed)");

            var payload = File.Exists(StatusPath) ? File.ReadAllText(StatusPath) : string.Empty;

            Check(payload.Contains("\"data_source\":\"" + ResearcherStatusWriter.DataSourceLive + "\""),
                "the snapshot is stamped " + ResearcherStatusWriter.DataSourceLive +
                " -- written by Unity, not by the mock generator");

            foreach (var section in new[] { "\"session\":", "\"acquisition\":", "\"channels\":",
                                            "\"window\":", "\"shadow\":" })
            {
                Check(payload.Contains(section), "the snapshot carries " + section.Trim(':', '"'));
            }

            // The honesty checks. With no receiver and no pipeline in an empty scene, the
            // dashboard must say so -- not report healthy electrodes, and not fall back to
            // anything synthetic.
            Check(payload.Contains("\"connected\":false"),
                "missing EEG is reported as connected=false, NOT as healthy");
            Check(payload.Contains("\"present\":false"),
                "with no feature window, window.present=false rather than invented values");
            Check(!payload.Contains("\"state\":\"healthy\""),
                "no electrode is claimed healthy when no window has ever been analysed");
            Check(payload.Contains("MOCK") == false,
                "the word MOCK appears nowhere in a Unity-written snapshot");

            // Play Mode ended. The file must survive so the dashboard can show it going stale,
            // rather than vanishing and looking like a dashboard fault.
            Check(File.Exists(StatusPath),
                "the snapshot SURVIVES Unity shutdown, so the dashboard can age it rather " +
                "than blanking");

            var dataRoot = Path.Combine(Application.persistentDataPath, "IKEA_EEG_Data");
            Log("participant folders after: " + CountFolders(dataRoot));

            var failures = SessionState.GetInt(k_Failures, 0);
            Log(failures == 0
                ? "=== RESULT: PASS (0 failures) ==="
                : $"=== RESULT: FAIL ({failures} failure(s)) ===");
        }

        static void Finish()
        {
            var quit = SessionState.GetBool("IkeaEeg.DashCheck.Quit", false);
            var failures = SessionState.GetInt(k_Failures, 0);

            SessionState.SetBool(k_Active, false);
            EditorApplication.update -= Tick;

            if (quit)
                EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        // ---- helpers --------------------------------------------------------------------

        static int CountFolders(string root)
        {
            try { return Directory.Exists(root) ? Directory.GetDirectories(root).Length : -1; }
            catch (IOException) { return -1; }
        }

        static string Extract(string payload, string key)
        {
            var i = payload.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return "<absent>";
            i += key.Length;
            var j = payload.IndexOf('"', i);
            return j < 0 ? "<unterminated>" : payload.Substring(i, j - i);
        }

        static string ExtractRaw(string payload, string key)
        {
            var i = payload.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return "<absent>";
            i += key.Length;
            var j = i;
            while (j < payload.Length && payload[j] != ',' && payload[j] != '}') j++;
            return payload.Substring(i, j - i);
        }

        static void Check(bool condition, string what)
        {
            if (condition) Log("PASS: " + what);
            else Fail(what);
        }

        static void Fail(string what)
        {
            SessionState.SetInt(k_Failures, SessionState.GetInt(k_Failures, 0) + 1);
            Debug.LogError(Tag + "FAIL: " + what);
        }

        static void Log(string message) => Debug.Log(Tag + message);
    }
}
