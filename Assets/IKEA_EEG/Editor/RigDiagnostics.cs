using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;
using UnityEngine.XR.Interaction.Toolkit.Feedback;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Dumps the actual serialized state of the scene's XR rig.
    ///
    /// Exists because the rig is a chain of nested prefab variants whose effective values are
    /// impractical to read from the YAML — this reports what the components really hold.
    /// </summary>
    public static class RigDiagnostics
    {
        [MenuItem("IKEA_EEG/Diagnose Rig Configuration", false, 60)]
        public static void DumpMenu()
        {
            Debug.Log(Dump());
        }

        public static void DumpFromCommandLine()
        {
            EditorSceneManager.OpenScene(ExperimentSceneBuilder.ScenePath, OpenSceneMode.Single);
            Debug.Log(Dump());
        }

        public static string Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== RIG DIAGNOSTICS =====");

            foreach (var interactor in Object.FindObjectsByType<NearFarInteractor>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                sb.AppendLine($"-- NearFarInteractor '{PathOf(interactor.transform)}'");
                sb.AppendLine($"     enabled={interactor.enabled} activeInHierarchy={interactor.gameObject.activeInHierarchy}");
                sb.AppendLine($"     enableNearCasting={interactor.enableNearCasting} " +
                              $"enableFarCasting={interactor.enableFarCasting} " +
                              $"farAttachMode={interactor.farAttachMode}");
                sb.AppendLine($"     selectInput.mode={interactor.selectInput.inputSourceMode} " +
                              $"performedRef={NameOf(interactor.selectInput.inputActionReferencePerformed)}");
                sb.AppendLine($"     uiPressInput.mode={interactor.uiPressInput.inputSourceMode} " +
                              $"performedRef={NameOf(interactor.uiPressInput.inputActionReferencePerformed)}");

                var curveCaster = interactor.farInteractionCaster as CurveInteractionCaster;
                if (curveCaster != null)
                    sb.AppendLine($"     farCaster.castDistance={curveCaster.castDistance}");
            }

            foreach (var visual in Object.FindObjectsByType<CurveVisualController>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                sb.AppendLine($"-- CurveVisualController '{PathOf(visual.transform)}'");
                sb.AppendLine($"     lineDynamicsMode={visual.lineDynamicsMode}   <== ray length behaviour");
                sb.AppendLine($"     restingVisualLineLength={visual.restingVisualLineLength} m");
                sb.AppendLine($"     retractDelay={visual.retractDelay} s");
            }

            foreach (var haptic in Object.FindObjectsByType<SimpleHapticFeedback>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                sb.AppendLine($"-- SimpleHapticFeedback '{PathOf(haptic.transform)}' enabled={haptic.enabled}");
            }

            foreach (var poke in Object.FindObjectsByType<XRPokeInteractor>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                sb.AppendLine($"-- XRPokeInteractor '{PathOf(poke.transform)}' " +
                              $"enabled={poke.enabled} active={poke.gameObject.activeInHierarchy}");
            }

            foreach (var group in Object.FindObjectsByType<XRInteractionGroup>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var members = group.startingGroupMembers == null
                    ? "none"
                    : string.Join(", ", group.startingGroupMembers.Select(m => m != null ? m.name : "null"));
                sb.AppendLine($"-- XRInteractionGroup '{PathOf(group.transform)}' members=[{members}]");
            }

            return sb.ToString();
        }

        static string NameOf(InputActionReference reference)
        {
            if (reference == null)
                return "NULL";

            var action = reference.action;
            if (action == null)
                return reference.name;

            var bindings = string.Join(" | ", action.bindings.Select(b => b.path));
            return $"{action.actionMap?.name}/{action.name}  bindings=[{bindings}]";
        }

        static string PathOf(Transform t)
        {
            var path = t.name;
            var current = t.parent;
            var depth = 0;
            while (current != null && depth < 3)
            {
                path = current.name + "/" + path;
                current = current.parent;
                depth++;
            }

            return path;
        }
    }
}
