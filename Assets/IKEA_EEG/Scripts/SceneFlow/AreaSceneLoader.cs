using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using IkeaEeg.Experiment;
using IkeaEeg.Interaction;
using IkeaEeg.UI;
using IkeaEeg.XR;

namespace IkeaEeg.SceneFlow
{
    /// <summary>
    /// Loads exactly ONE room scene at a time and rebinds it into the persistent systems.
    ///
    /// ORDER MATTERS, and it is: load the destination, bind it, place the participant, and only
    /// THEN unload the room being left. Unloading first would leave a frame with no room, no
    /// bound UI and a participant standing in empty space, and it would destroy the objects the
    /// persistent systems still point at before their replacements exist.
    ///
    /// DORMANT UNTIL THE ROOM SCENES EXIST. <see cref="multiSceneAvailable"/> is false while the
    /// project still ships the single combined scene, and every call then returns immediately
    /// having changed nothing. That is what lets this land alongside the existing architecture
    /// without altering a single frame of current behaviour.
    ///
    /// WHAT IT DELIBERATELY DOES NOT DO:
    ///   * it does not decide WHEN to change rooms -- ExperimentManager's state machine does,
    ///     and this is only the mechanism it calls;
    ///   * it does not teleport directly. It asks the existing XRRigTeleporter, so the move is
    ///     logged as the same event it has always been and no second placement path exists;
    ///   * it does not touch the logger, the session, the EEG pipeline or any CSV. Those are
    ///     persistent and never learn that a scene changed.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AreaSceneLoader : MonoBehaviour
    {
        public const string AreaASceneName = "IKEA_EEG_AreaA";
        public const string AreaBSceneName = "IKEA_EEG_AreaB";
        public const string AreaCSceneName = "IKEA_EEG_AreaC";

        [SerializeField] ExperimentManager m_Manager;
        [SerializeField] ExperimentUIController m_Ui;
        [SerializeField] ChairSelectionTask m_ChairTask;
        [SerializeField] XRRigTeleporter m_Teleporter;
        [SerializeField] RecognitionResponsePanel m_RecognitionPanel;

        /// <summary>The room currently open, or null before the first load.</summary>
        public AreaSceneContext current { get; private set; }

        /// <summary>True while a switch is in flight. Guards against a second, overlapping one.</summary>
        public bool isSwitching { get; private set; }

        public void Bind(ExperimentManager manager, ExperimentUIController ui,
            ChairSelectionTask chairTask, XRRigTeleporter teleporter,
            RecognitionResponsePanel recognitionPanel = null)
        {
            m_Manager = manager;
            m_Ui = ui;
            m_ChairTask = chairTask;
            m_Teleporter = teleporter;
            m_RecognitionPanel = recognitionPanel;
        }

        /// <summary>
        /// The scene file for an area. Familiarization shares Area A's scene: the practice room
        /// and the entrance are one stretch of the protocol and were kept together.
        /// </summary>
        public static string SceneNameFor(ExperimentArea area)
        {
            switch (area)
            {
                case ExperimentArea.Familiarization: return AreaASceneName;
                case ExperimentArea.AreaA: return AreaASceneName;
                case ExperimentArea.AreaB: return AreaBSceneName;
                case ExperimentArea.AreaC: return AreaCSceneName;
                default: return null;
            }
        }

        /// <summary>
        /// True only when all three room scenes are in the build settings.
        ///
        /// Checked rather than assumed, because a half-migrated project that loaded Area B but
        /// could not find Area C would strand a participant mid-session. All three or none.
        /// </summary>
        public static bool multiSceneAvailable
        {
            get
            {
                foreach (var name in new[] { AreaASceneName, AreaBSceneName, AreaCSceneName })
                {
                    if (!IsInBuildSettings(name))
                        return false;
                }

                return true;
            }
        }

        static bool IsInBuildSettings(string sceneName)
        {
            for (var i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
            {
                var path = SceneUtility.GetScenePathByBuildIndex(i);

                if (System.IO.Path.GetFileNameWithoutExtension(path) == sceneName)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Make <paramref name="area"/> the one loaded room, then place the participant in it.
        ///
        /// Yields until the destination is loaded, bound and occupied and the previous room is
        /// gone. Returns immediately, changing nothing, when multi-scene is not available or the
        /// requested room is already the open one.
        /// </summary>
        public IEnumerator SwitchTo(ExperimentArea area, string reason = null)
        {
            if (!multiSceneAvailable)
                yield break;

            if (isSwitching)
            {
                Debug.LogWarning($"[IKEA_EEG] A room switch is already running; the request for " +
                                 $"{area} was ignored. Nothing changed.");
                yield break;
            }

            var sceneName = SceneNameFor(area);

            if (string.IsNullOrEmpty(sceneName))
            {
                Debug.LogError($"[IKEA_EEG] No room scene is mapped to {area}.");
                yield break;
            }

            // Familiarization and Area A are the same scene: moving between them is a teleport
            // inside one room, never a load.
            var alreadyOpen = current != null && SceneNameFor(current.area) == sceneName;

            isSwitching = true;

            try
            {
                var previous = current;

                if (!alreadyOpen)
                {
                    var load = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);

                    while (load != null && !load.isDone)
                        yield return null;
                }

                var destination = AreaSceneContext.ForArea(area)
                                  ?? AreaSceneContext.loaded.FirstOrDefault(
                                      c => c != null && SceneNameFor(c.area) == sceneName);

                if (destination == null)
                {
                    Debug.LogError($"[IKEA_EEG] '{sceneName}' loaded but carries no " +
                                   $"AreaSceneContext for {area}, so nothing could be bound. " +
                                   "The room is left loaded for inspection.");
                    yield break;
                }

                ReportMissingReferences(destination);

                var systems = CollectSystems();

                if (!systems.isComplete)
                {
                    Debug.LogError("[IKEA_EEG] The persistent systems are incomplete, so the " +
                                   $"room {area} was not bound. This is a bootstrap problem, " +
                                   "not a room problem.");
                    yield break;
                }

                destination.BindInto(systems);

                // BINDING A BUTTON IS NOT SUBSCRIBING TO IT. The Bind* methods assign fields;
                // the onClick listeners are wired by the controller's OnEnable, which ran back
                // in the bootstrap scene while every one of these fields was still null. Without
                // this the room's buttons hit-test perfectly and do nothing at all.
                systems.ui.RewireBoundButtons();

                current = destination;

                // Through the EXISTING teleporter, so the placement is logged exactly as it has
                // always been and there is no second way to move the participant.
                if (systems.teleporter != null)
                    systems.teleporter.TeleportTo(area);

                // LAST: the room being left goes only once its replacement is bound and occupied.
                if (previous != null && previous != destination)
                {
                    var previousScene = previous.gameObject.scene;

                    if (previousScene.IsValid() && previousScene.isLoaded)
                    {
                        var unload = SceneManager.UnloadSceneAsync(previousScene);

                        while (unload != null && !unload.isDone)
                            yield return null;
                    }
                }

                Debug.Log($"[IKEA_EEG] Room is now {area} ('{sceneName}')" +
                          (string.IsNullOrEmpty(reason) ? "." : $" ({reason}).") +
                          $" Rooms loaded: {AreaSceneContext.loaded.Count}.");
            }
            finally
            {
                isSwitching = false;
            }
        }

        void ReportMissingReferences(AreaSceneContext context)
        {
            var missing = context.MissingReferences().ToList();

            if (missing.Count == 0)
                return;

            Debug.LogError($"[IKEA_EEG] {context.GetType().Name} is missing " +
                           $"{missing.Count} reference(s): {string.Join(", ", missing)}. " +
                           "The room was still bound, but these will be dead at run time.");
        }

        PersistentSystems CollectSystems()
        {
            return new PersistentSystems
            {
                manager = m_Manager != null
                    ? m_Manager : FindAnyObjectByType<ExperimentManager>(),
                ui = m_Ui != null
                    ? m_Ui : FindAnyObjectByType<ExperimentUIController>(),
                chairTask = m_ChairTask != null
                    ? m_ChairTask : FindAnyObjectByType<ChairSelectionTask>(),
                teleporter = m_Teleporter != null
                    ? m_Teleporter : FindAnyObjectByType<XRRigTeleporter>(),
                recognitionPanel = m_RecognitionPanel != null
                    ? m_RecognitionPanel : FindAnyObjectByType<RecognitionResponsePanel>(),
            };
        }
    }
}
