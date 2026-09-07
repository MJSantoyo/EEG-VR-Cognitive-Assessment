using UnityEngine;

namespace IkeaEeg.XR
{
    /// <summary>
    /// The areas of the session.
    ///
    /// Familiarization is APPENDED, not inserted before AreaA, even though it comes first in
    /// the flow: Unity serialises enums by integer, and inserting a value would silently
    /// renumber every SpawnPoint already saved in the scene. Presentation order lives in the
    /// state machine, not in this enum's numbering.
    /// </summary>
    public enum ExperimentArea
    {
        AreaA,
        AreaB,
        AreaC,

        /// <summary>Area 0 — VR familiarization. Runs BEFORE Area A. Not part of scoring.</summary>
        Familiarization,
    }

    /// <summary>
    /// Marks a predefined standing position. Spawn_A / Spawn_B / Spawn_C.
    ///
    /// The transform's position is where the participant's HEAD (camera) is placed on the
    /// floor plane, and its forward is the direction they are made to face.
    /// </summary>
    [DisallowMultipleComponent]
    public class SpawnPoint : MonoBehaviour
    {
        [SerializeField] ExperimentArea m_Area = ExperimentArea.AreaA;

        public ExperimentArea area => m_Area;

        public void SetArea(ExperimentArea area) => m_Area = area;

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.1f, 0.9f, 0.4f, 0.9f);
            Gizmos.DrawWireSphere(transform.position + Vector3.up * 0.05f, 0.35f);
            Gizmos.DrawRay(transform.position + Vector3.up * 0.05f, transform.forward * 1.0f);
        }
    }
}
