#if UNITY_EDITOR
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace IkeaEeg.XR
{
    /// <summary>
    /// READ-ONLY diagnosis of the desktop mouse path. Editor only, and it changes nothing.
    ///
    /// WHY THIS EXISTS. After the multi-scene split the mouse stopped reaching the UI and the
    /// 3D interactables on the desktop. Every STATIC difference between the old combined scene
    /// and the new bootstrap + room set was checked and they are identical: one EventSystem,
    /// one XRUIInputModule with enableMouseInput = 1, the rig camera tagged MainCamera, the
    /// same 19 world-space canvases with the same null worldCamera, the same GraphicRaycasters
    /// (3 bootstrap + 11 Area A + 4 Area B + 1 Area C), and the same DesktopMouseInteraction
    /// configuration. So the cause is something only visible while playing.
    ///
    /// This reports the facts that separate the remaining possibilities, on every left click:
    ///   * is there exactly ONE EventSystem, is it enabled, and which scene is it in;
    ///   * which input module is current, and is it actually active;
    ///   * does Camera.main resolve, to which camera, in which scene, and where is it;
    ///   * how many GraphicRaycasters are enabled, and in which scenes;
    ///   * what the EventSystem's own RaycastAll returns under the cursor -- THE decisive
    ///     datum: if this is empty while a button is plainly under the mouse, the failure is
    ///     in UI raycasting; if it is populated, the click is being delivered and the failure
    ///     is downstream;
    ///   * whether IsPointerOverGameObject() is true, which is the single gate that makes
    ///     DesktopMouseInteraction skip the 3D path entirely;
    ///   * what a plain Physics.Raycast from Camera.main hits, and whether that carries an
    ///     XRSimpleInteractable.
    ///
    /// IT ADDS NO INPUT PATH. It creates no EventSystem, no module, no interactor and no
    /// camera; it never invokes selectEntered and never consumes a click. It spawns itself the
    /// way DeveloperKeyboardShortcuts does, so no scene is modified and no validator count
    /// changes. The whole file is inside UNITY_EDITOR, so a Quest build contains no trace.
    /// Delete it once the regression is understood.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DesktopInputDiagnostics : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindAnyObjectByType<DesktopInputDiagnostics>() != null)
                return;

            var host = new GameObject("IKEA_EEG Desktop Input Diagnostics (editor only)");
            host.AddComponent<DesktopInputDiagnostics>();
            DontDestroyOnLoad(host);
        }

        void Update()
        {
            var mouse = Mouse.current;

            if (mouse == null || !mouse.leftButton.wasPressedThisFrame)
                return;

            // SHIFT-CLICK ONLY. Reporting on every click drowned the Console during ordinary
            // desktop testing, and this is worth keeping: it is what located the click-dispatch
            // fault in a single run, by showing a populated RaycastAll under a button that did
            // nothing. Holding Shift does not suppress the click itself -- the button still
            // behaves normally -- so this observes without ever changing what input does.
            var keyboard = Keyboard.current;

            if (keyboard == null || !keyboard.leftShiftKey.isPressed)
                return;

            Debug.Log(Report(mouse.position.ReadValue()));
        }

        static string Report(Vector2 screenPosition)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== DESKTOP INPUT DIAGNOSTIC (left click) =====");

            sb.AppendLine($"  loaded scenes: {LoadedScenes()}");
            sb.AppendLine($"  cursor: {screenPosition}  screen: {Screen.width}x{Screen.height}");

            // ---- EventSystem ------------------------------------------------------------
            var eventSystems = FindObjectsByType<EventSystem>(FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            sb.AppendLine($"  EventSystem count: {eventSystems.Length} " +
                          "(more than 1 is a fault; 0 means no UI input at all)");

            foreach (var es in eventSystems)
            {
                sb.AppendLine($"    - '{es.name}' scene='{es.gameObject.scene.name}' " +
                              $"activeInHierarchy={es.gameObject.activeInHierarchy} " +
                              $"enabled={es.enabled} isCurrent={(EventSystem.current == es)}");
            }

            var current = EventSystem.current;

            if (current == null)
            {
                sb.AppendLine("  FAIL  EventSystem.current is NULL — nothing can receive UI input.");
                return sb.ToString();
            }

            var module = current.currentInputModule;
            sb.AppendLine($"  currentInputModule: " +
                          $"{(module == null ? "NULL — no module is driving the EventSystem" : module.GetType().Name)}" +
                          $"{(module != null ? $" enabled={module.enabled} isActive={module.IsActive()}" : "")}");

            // ---- Camera -----------------------------------------------------------------
            var cam = Camera.main;

            if (cam == null)
            {
                sb.AppendLine("  FAIL  Camera.main is NULL — world-space canvases fall back to " +
                              "it for raycasting, and DesktopMouseInteraction casts from it.");
            }
            else
            {
                sb.AppendLine($"  Camera.main: '{cam.name}' scene='{cam.gameObject.scene.name}' " +
                              $"enabled={cam.enabled} activeInHierarchy={cam.gameObject.activeInHierarchy}");
                sb.AppendLine($"    position={cam.transform.position:F3} " +
                              $"forward={cam.transform.forward:F3} " +
                              $"targetDisplay={cam.targetDisplay} depth={cam.depth}");
            }

            var allCameras = FindObjectsByType<Camera>(FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            sb.AppendLine($"  enabled cameras in play: {allCameras.Length} " +
                          $"[{string.Join(", ", allCameras.Select(c => $"{c.name}({c.gameObject.scene.name})"))}]");

            // ---- Raycasters -------------------------------------------------------------
            var raycasters = FindObjectsByType<GraphicRaycaster>(FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            var byScene = raycasters
                .GroupBy(r => r.gameObject.scene.name)
                .Select(g => $"{g.Key}={g.Count()}");

            sb.AppendLine($"  enabled GraphicRaycasters: {raycasters.Length} " +
                          $"[{string.Join(", ", byScene)}]");

            // ---- What the EventSystem itself sees ---------------------------------------
            var pointer = new PointerEventData(current) { position = screenPosition };
            var hits = new System.Collections.Generic.List<RaycastResult>();
            current.RaycastAll(pointer, hits);

            sb.AppendLine($"  EventSystem.RaycastAll hits: {hits.Count}");

            foreach (var h in hits.Take(6))
            {
                sb.AppendLine($"    - '{h.gameObject.name}' " +
                              $"scene='{h.gameObject.scene.name}' " +
                              $"module={h.module.GetType().Name} distance={h.distance:F3}");
            }

            if (hits.Count == 0)
            {
                sb.AppendLine("    (empty: if a button is visibly under the cursor, UI " +
                              "raycasting is the fault — camera, raycaster or canvas plane)");
            }

            sb.AppendLine($"  IsPointerOverGameObject(): {current.IsPointerOverGameObject()} " +
                          "(true makes DesktopMouseInteraction skip the 3D path)");

            // ---- The 3D path -------------------------------------------------------------
            if (cam != null)
            {
                var ray = cam.ScreenPointToRay(screenPosition);

                if (Physics.Raycast(ray, out var hit, 10f))
                {
                    var interactable = hit.collider.GetComponentInParent<XRSimpleInteractable>();
                    sb.AppendLine($"  Physics.Raycast hit '{hit.collider.name}' at {hit.distance:F2} m " +
                                  $"scene='{hit.collider.gameObject.scene.name}' " +
                                  $"interactable={(interactable == null ? "NONE" : interactable.name)}" +
                                  $"{(interactable != null ? $" activeAndEnabled={interactable.isActiveAndEnabled}" : "")}");
                }
                else
                {
                    sb.AppendLine("  Physics.Raycast hit NOTHING within 10 m " +
                                  "(wrong camera position, or no colliders in the loaded room)");
                }
            }

            return sb.ToString();
        }

        static string LoadedScenes()
        {
            var names = new System.Collections.Generic.List<string>();

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                names.Add($"{s.name}{(s == SceneManager.GetActiveScene() ? "*" : "")}");
            }

            return string.Join(", ", names) + "   (* = active)";
        }
    }
}
#endif
