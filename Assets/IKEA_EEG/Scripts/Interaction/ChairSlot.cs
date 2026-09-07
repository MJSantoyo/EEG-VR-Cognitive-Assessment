using UnityEngine;

namespace IkeaEeg.Interaction
{
    /// <summary>
    /// One of the six FIXED, pre-validated positions a chair can stand in during Area B.
    ///
    /// WHY SLOTS INSTEAD OF RANDOM POSITIONS: randomising world positions freely would let a
    /// trial place a chair behind another, out of ray range, or at a different viewing distance
    /// from the rest — and response time would then partly measure how awkward the chair was to
    /// point at rather than how hard it was to find. So the geometry is authored once, checked
    /// once (non-overlapping, all visible from Spawn_B, all within the interactor's reach, all
    /// at a comparable distance), and only the ASSIGNMENT of chairs to slots is randomised.
    ///
    /// The slot's forward direction is the direction its occupant faces, so every chair
    /// presents the same aspect to the participant wherever it stands.
    /// </summary>
    [DisallowMultipleComponent]
    public class ChairSlot : MonoBehaviour
    {
        [Tooltip("Position of this slot in the layout, 0-based. Must be unique within Area B.")]
        [SerializeField] int m_SlotIndex;

        public int slotIndex => m_SlotIndex;

        public void SetSlotIndex(int index) => m_SlotIndex = index;

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.8f);
            Gizmos.DrawWireCube(transform.position + Vector3.up * 0.45f,
                new Vector3(0.75f, 0.9f, 0.75f));
            Gizmos.DrawRay(transform.position + Vector3.up * 0.45f, transform.forward * 0.6f);
        }
    }
}
