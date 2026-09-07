using UnityEngine;
using Unity.XR.CoreUtils;
using IkeaEeg.Core;

namespace IkeaEeg.XR
{
    /// <summary>
    /// Moves the XR Origin between Spawn_A / Spawn_B / Spawn_C.
    ///
    /// WHY THIS INSTEAD OF SCENE LOADING: the whole experiment lives in one scene and the rig
    /// is simply repositioned. No scene load means no asset-loading hitch, no lost references
    /// and — critically for the future EEG sync — a continuous, unbroken session clock.
    ///
    /// WHY XROrigin.MoveCameraToWorldLocation INSTEAD OF SETTING transform.position: with
    /// room-scale tracking the camera sits at an arbitrary offset inside the tracking volume.
    /// Moving the rig transform directly would land the participant that offset away from the
    /// intended mark. MoveCameraToWorldLocation compensates for the current head offset, so
    /// the participant's HEAD ends up on the spawn point wherever they happen to be standing
    /// in their play space.
    /// </summary>
    [DisallowMultipleComponent]
    public class XRRigTeleporter : MonoBehaviour
    {
        [Header("Rig")]
        [SerializeField] XROrigin m_XROrigin;

        [Header("Spawn points")]
        [Tooltip("Area 0 — VR familiarization. The session starts here unless it is skipped.")]
        [SerializeField] SpawnPoint m_SpawnFamiliarization;

        [SerializeField] SpawnPoint m_SpawnA;
        [SerializeField] SpawnPoint m_SpawnB;
        [SerializeField] SpawnPoint m_SpawnC;

        [Header("Options")]
        [Tooltip("Also rotate the rig so the participant faces the spawn point's forward direction.")]
        [SerializeField] bool m_MatchSpawnRotation = true;

        public ExperimentArea currentArea { get; private set; } = ExperimentArea.AreaA;

        void Awake()
        {
            if (m_XROrigin == null)
                m_XROrigin = FindAnyObjectByType<XROrigin>();

            if (m_XROrigin == null)
                Debug.LogError("[IKEA_EEG] XRRigTeleporter could not find an XROrigin. " +
                               "Area transitions will not work.");
        }

        public void SetXROrigin(XROrigin origin) => m_XROrigin = origin;

        public void SetSpawnPoints(SpawnPoint a, SpawnPoint b, SpawnPoint c,
            SpawnPoint familiarization = null)
        {
            m_SpawnA = a;
            m_SpawnB = b;
            m_SpawnC = c;
            m_SpawnFamiliarization = familiarization;
        }

        public SpawnPoint GetSpawn(ExperimentArea area)
        {
            switch (area)
            {
                case ExperimentArea.Familiarization: return m_SpawnFamiliarization;
                case ExperimentArea.AreaA: return m_SpawnA;
                case ExperimentArea.AreaB: return m_SpawnB;
                case ExperimentArea.AreaC: return m_SpawnC;
                default: return null;
            }
        }

        /// <summary>
        /// Re-aligns the participant with the CURRENT area's intended forward direction,
        /// exactly as if they had just entered it.
        ///
        /// This is the experiment-level equivalent of the headset's own recenter: the rig is
        /// yawed so that wherever the participant is physically facing becomes "forward toward
        /// the room and its UI", and they are placed back on the spawn mark. Their real
        /// standing height and room-scale tracking are untouched — nothing here changes the
        /// tracking origin mode or disables room-scale movement, it only moves the rig
        /// transform inside the world.
        /// </summary>
        public void Recenter()
        {
            TeleportTo(currentArea, EventTypes.Recenter);
        }

        /// <summary>Repositions the rig so the participant stands at the given area's spawn.</summary>
        public void TeleportTo(ExperimentArea area)
        {
            TeleportTo(area, EventTypes.TeleportToArea);
        }

        void TeleportTo(ExperimentArea area, string eventType)
        {
            var spawn = GetSpawn(area);

            if (m_XROrigin == null || spawn == null)
            {
                Debug.LogError($"[IKEA_EEG] Cannot teleport to {area}: " +
                               $"{(m_XROrigin == null ? "XROrigin missing" : "spawn point missing")}.");
                return;
            }

            if (m_MatchSpawnRotation)
            {
                // Yaw the rig around the camera so the participant faces the spawn forward
                // without the world appearing to swing around their feet.
                var currentForward = m_XROrigin.Camera != null
                    ? Vector3.ProjectOnPlane(m_XROrigin.Camera.transform.forward, Vector3.up)
                    : m_XROrigin.transform.forward;

                var desiredForward = Vector3.ProjectOnPlane(spawn.transform.forward, Vector3.up);

                if (currentForward.sqrMagnitude > 0.0001f && desiredForward.sqrMagnitude > 0.0001f)
                {
                    var angle = Vector3.SignedAngle(currentForward.normalized,
                        desiredForward.normalized, Vector3.up);
                    m_XROrigin.RotateAroundCameraUsingOriginUp(angle);
                }
            }

            // ---------------------------------------------------------------------------
            // DO NOT use XROrigin.MoveCameraToWorldLocation(spawn.position) here.
            //
            // That call puts the CAMERA exactly on the given point. The spawn points sit on
            // the floor (y = 0), so it placed the participant's HEAD at floor level and left
            // the rig origin ~1.6 m BELOW the floor — which is what produced both the
            // "everything is at floor height" view and the falling afterwards, since the
            // CharacterController and GravityProvider then had to resolve a rig buried under
            // the ground.
            //
            // What we actually want is: rig FLOOR to the spawn's floor, head horizontally
            // over the spawn, and the participant's real tracked head height preserved.
            // So the vertical component comes from the ORIGIN and the horizontal components
            // come from the CAMERA.
            // ---------------------------------------------------------------------------
            var originTransform = m_XROrigin.transform;
            var cameraTransform = m_XROrigin.Camera != null
                ? m_XROrigin.Camera.transform
                : originTransform;

            var cameraPosition = cameraTransform.position;
            var target = spawn.transform.position;

            var delta = new Vector3(
                target.x - cameraPosition.x,
                target.y - originTransform.position.y,
                target.z - cameraPosition.z);

            // A CharacterController caches its own position and fights direct transform
            // writes, which can snap the rig back or wedge it in the floor. Disabling it for
            // the assignment is the supported way to teleport a controller-driven rig.
            var characterController = m_XROrigin.GetComponentInChildren<CharacterController>();
            var hadController = characterController != null && characterController.enabled;

            if (hadController)
                characterController.enabled = false;

            originTransform.position += delta;

            if (hadController)
                characterController.enabled = true;

            currentArea = area;

            var logger = EventLogger.Instance;
            if (logger != null)
            {
                logger.Log(eventType, e =>
                {
                    e.objectId = spawn.name;
                    e.notes = $"area={area}; spawn={spawn.transform.position.ToString("F3")}; " +
                              $"origin={originTransform.position.ToString("F3")}; " +
                              $"camera={cameraTransform.position.ToString("F3")}; " +
                              $"head_height={(cameraTransform.position.y - originTransform.position.y):F2}m";
                });
            }
        }

        /// <summary>Maps an area to the CSV <c>room</c> label.</summary>
        public static string ToRoomName(ExperimentArea area)
        {
            switch (area)
            {
                case ExperimentArea.Familiarization: return RoomNames.Familiarization;
                case ExperimentArea.AreaA: return RoomNames.AreaA;
                case ExperimentArea.AreaB: return RoomNames.AreaB;
                case ExperimentArea.AreaC: return RoomNames.AreaC;
                default: return RoomNames.None;
            }
        }
    }
}
