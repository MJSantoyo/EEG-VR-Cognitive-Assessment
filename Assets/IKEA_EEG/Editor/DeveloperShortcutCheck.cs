#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using IkeaEeg.XR;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Drives REAL Play Mode and checks that the M developer shortcut reaches Area B.
    ///
    /// WHY PLAY MODE. The button's onClick listener is attached by ExperimentUIController in
    /// OnEnable, so outside Play Mode the button exists with an empty event and pressing it
    /// proves nothing. Only a running session can show the jump actually landing.
    ///
    /// WHAT IT COSTS. Unlike the dashboard check, this needs the REAL experiment scene, so
    /// Play Mode opens a real session and a real session folder appears in the participant data
    /// root. That folder is reported by name at the end so it can be reviewed and removed by
    /// hand; nothing here deletes recorded data.
    ///
    /// Editor-only, and it drives the same public entry point the key does.
    /// </summary>
    [InitializeOnLoad]
    public static class DeveloperShortcutCheck
    {
        const string k_Active = "IkeaEeg.MCheck.Active";
        const string k_Stage = "IkeaEeg.MCheck.Stage";
        const string k_Deadline = "IkeaEeg.MCheck.Deadline";
        const string k_Wait = "IkeaEeg.MCheck.Wait";
        const string k_Fail = "IkeaEeg.MCheck.Fail";
        const string k_Room = "IkeaEeg.MCheck.Room";

        const string Tag = "[MCHECK] ";
        const double Timeout = 240.0;

        static DeveloperShortcutCheck()
        {
            if (SessionState.GetBool(k_Active, false))
                EditorApplication.update += Tick;
        }

        /// <summary>Batch entry. Run WITHOUT -quit: it quits itself.</summary>
        public static void RunFromCommandLine()
        {
            Log("=== M developer shortcut check ===");

            EditorSceneManager.OpenScene(ExperimentSceneBuilder.ScenePath, OpenSceneMode.Single);

            SessionState.SetBool(k_Active, true);
            SessionState.SetString(k_Stage, "entering");
            SessionState.SetFloat(k_Deadline, (float)(EditorApplication.timeSinceStartup + Timeout));
            SessionState.SetFloat(k_Wait, 0f);
            SessionState.SetInt(k_Fail, 0);
            SessionState.SetString(k_Room, string.Empty);

            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;

            Log("entering Play Mode with the real experiment scene...");
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
                Fail("TIMEOUT");
                Finish();
                return;
            }

            var stage = SessionState.GetString(k_Stage, "entering");

            if (stage == "entering")
            {
                if (!EditorApplication.isPlaying)
                    return;

                Log("Play Mode ACTIVE — letting the session open");
                SessionState.SetFloat(k_Wait, (float)(EditorApplication.timeSinceStartup + 6.0));
                SessionState.SetString(k_Stage, "settle");
                return;
            }

            if (stage == "settle")
            {
                if (EditorApplication.timeSinceStartup < SessionState.GetFloat(k_Wait, 0f))
                    return;

                var logger = UnityEngine.Object.FindAnyObjectByType<IkeaEeg.Core.EventLogger>();
                var before = logger == null ? "?" : logger.currentRoom;
                SessionState.SetString(k_Room, before);

                Log($"room before the jump: {before}");

                Check(DeveloperKeyboardShortcuts.FindAreaBButton() != null,
                    $"the developer button '{DeveloperKeyboardShortcuts.AreaBButtonName}' is " +
                    "present in the running scene");

                // The same call the M key makes.
                Check(DeveloperKeyboardShortcuts.JumpToAreaB(),
                    "JumpToAreaB() raised the existing developer action");

                SessionState.SetFloat(k_Wait, (float)(EditorApplication.timeSinceStartup + 5.0));
                SessionState.SetString(k_Stage, "verify");
                return;
            }

            if (stage == "verify")
            {
                if (EditorApplication.timeSinceStartup < SessionState.GetFloat(k_Wait, 0f))
                    return;

                var logger = UnityEngine.Object.FindAnyObjectByType<IkeaEeg.Core.EventLogger>();
                var after = logger == null ? "?" : logger.currentRoom;
                var before = SessionState.GetString(k_Room, string.Empty);

                Log($"room after the jump : {after}");

                Check(after != before, $"the room changed ({before} -> {after})");
                Check(after != null && after.IndexOf("B", StringComparison.OrdinalIgnoreCase) >= 0,
                    $"and the participant is now in Area B ({after})");

                if (logger != null)
                {
                    Log("session folder created by this check: " + logger.sessionDirectory);
                }

                SessionState.SetString(k_Stage, "leaving");
                EditorApplication.ExitPlaymode();
                return;
            }

            if (stage == "leaving")
            {
                if (EditorApplication.isPlaying)
                    return;

                var fails = SessionState.GetInt(k_Fail, 0);
                Log(fails == 0 ? "=== RESULT: PASS ===" : $"=== RESULT: FAIL ({fails}) ===");
                Finish();
            }
        }

        static void Finish()
        {
            var fails = SessionState.GetInt(k_Fail, 0);
            SessionState.SetBool(k_Active, false);
            EditorApplication.update -= Tick;
            EditorApplication.Exit(fails == 0 ? 0 : 1);
        }

        static void Check(bool ok, string what)
        {
            if (ok) Log("PASS: " + what);
            else Fail(what);
        }

        static void Fail(string what)
        {
            SessionState.SetInt(k_Fail, SessionState.GetInt(k_Fail, 0) + 1);
            Debug.LogError(Tag + "FAIL: " + what);
        }

        static void Log(string m) => Debug.Log(Tag + m);
    }
}
#endif
