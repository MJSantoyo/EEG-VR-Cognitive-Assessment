#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace IkeaEeg.XR
{
    /// <summary>
    /// Desktop keyboard shortcuts for testing in the Editor without a headset.
    ///
    ///     M   jump to Area B, the chair-task showroom.
    ///     P   end the rest block that is currently running (DEMO ONLY).
    ///
    /// EDITOR ONLY, TWICE OVER. The whole file is inside #if UNITY_EDITOR so it is not compiled
    /// into a player build at all, and the object is spawned by a RuntimeInitializeOnLoadMethod
    /// that only exists inside that same guard. A Quest build contains no trace of this.
    ///
    /// NO SECOND STATE MACHINE. This does not move the rig, does not set a state and does not
    /// call the experiment. It invokes the onClick of the developer panel's existing
    /// "AREA B" button, which is the same UnityEvent a press on that button raises in VR:
    ///
    ///     Btn_Dev_AreaB.onClick
    ///       -> ExperimentUIController.RaiseDevGoAreaB()
    ///       -> ExperimentUIController.developerGoToArea(ExperimentArea.AreaB)
    ///       -> ExperimentManager.OnDeveloperAreaJump(AreaB)
    ///
    /// Everything that path already does still happens, unchanged and in order: the jump is
    /// refused on an aborted or ended run, the logger is marked developer-interrupted, a
    /// DEVELOPER_AREA_JUMP event is written carrying developer_interrupted=TRUE and
    /// run_is_not_a_valid_participant_protocol_run=TRUE, the developer panel closes, and the
    /// running flow and narration are stopped before the new area starts. This class adds no
    /// logging of its own to the event stream -- duplicating it would misrepresent one jump as
    /// two -- and writes only a Console line saying the keyboard raised it.
    ///
    /// It spawns itself rather than being added to the scene: the experiment scene is generated
    /// by a destructive builder, and a runtime-spawned object keeps the authored scene
    /// untouched.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DeveloperKeyboardShortcuts : MonoBehaviour
    {
        /// <summary>The developer panel button this shortcut presses. Authored by the scene builder.</summary>
        public const string AreaBButtonName = "Btn_Dev_AreaB";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindAnyObjectByType<DeveloperKeyboardShortcuts>() != null)
                return;

            var host = new GameObject("IKEA_EEG Developer Keyboard (editor only)");
            host.AddComponent<DeveloperKeyboardShortcuts>();
            DontDestroyOnLoad(host);
        }

        void Update()
        {
            // Input System only; this project has no legacy input backend enabled.
            var keyboard = Keyboard.current;

            if (keyboard == null)
                return;

            if (keyboard.mKey.wasPressedThisFrame)
                JumpToAreaB();

            if (keyboard.pKey.wasPressedThisFrame)
                SkipRestBlock();
        }

        /// <summary>
        /// DEMO ONLY. Ends the rest block currently running, through the experiment own
        /// completion path.
        ///
        /// Calls ExperimentManager.DeveloperSkipRestBlock, which raises a flag the waits
        /// inside RunRestBlock already poll. No state is assigned here, no coroutine is
        /// started, no teleport happens and the configured 180 s is not changed -- the
        /// block simply finishes early through its own end marker, so PRE_TASK_REST
        /// continues into word encoding and POST_TASK_REST into the results path.
        ///
        /// Outside a rest block the manager logs a warning and changes nothing.
        /// </summary>
        public static bool SkipRestBlock()
        {
            var manager = FindAnyObjectByType<IkeaEeg.Experiment.ExperimentManager>();

            if (manager == null)
            {
                Debug.LogWarning("[IKEA_EEG] Developer keyboard: no ExperimentManager in " +
                                 "the scene, so P did nothing.");
                return false;
            }

            Debug.Log("[IKEA_EEG] Developer keyboard: P pressed — requesting a DEMO skip " +
                      "of the current rest block.");

            return manager.DeveloperSkipRestBlock();
        }

        /// <summary>
        /// Raises the existing developer button. Public so the self test can drive the same
        /// entry point the key does.
        /// </summary>
        public static bool JumpToAreaB()
        {
            var button = FindAreaBButton();

            if (button == null)
            {
                Debug.LogWarning($"[IKEA_EEG] Developer keyboard: '{AreaBButtonName}' was not " +
                                 "found in the scene, so the Area B jump was not raised. Is the " +
                                 "experiment scene open?");
                return false;
            }

            Debug.Log("[IKEA_EEG] Developer keyboard: M pressed — raising the existing " +
                      $"'{AreaBButtonName}' action. This is a DEVELOPER jump; the run is not a " +
                      "valid participant protocol run.");

            // Invoking the UnityEvent, not simulating a click, so it works whether or not the
            // developer panel happens to be open.
            button.onClick.Invoke();
            return true;
        }

        /// <summary>
        /// Finds the button including inactive objects: the developer panel is normally closed,
        /// and GameObject.Find would miss it.
        /// </summary>
        public static Button FindAreaBButton()
        {
            var buttons = FindObjectsByType<Button>(FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            foreach (var button in buttons)
            {
                if (button.name == AreaBButtonName)
                    return button;
            }

            return null;
        }
    }
}
#endif
