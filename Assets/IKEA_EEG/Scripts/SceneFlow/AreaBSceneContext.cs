using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using IkeaEeg.Interaction;
using IkeaEeg.XR;

namespace IkeaEeg.SceneFlow
{
    /// <summary>
    /// The Area B room scene: the chair-selection showroom.
    ///
    /// Besides its UI this room owns the chairs and the chair slots, which the PERSISTENT
    /// ChairSelectionTask needs. That is the one place a room hands over task objects rather
    /// than interface objects, and it goes through the existing SetChairs/SetSlots methods the
    /// scene builder already calls -- the chair logic itself is untouched.
    ///
    /// The target chair and the colour materials are deliberately NOT set here. They are
    /// configuration, they come from ExperimentConfig, and they are bound once on the
    /// persistent side: a room scene must never be able to change what counts as a correct
    /// answer.
    /// </summary>
    public sealed class AreaBSceneContext : AreaSceneContext
    {
        [Header("Spawn")]
        [SerializeField] SpawnPoint m_SpawnAreaB;

        [Header("Chair task objects")]
        [SerializeField] List<ChairTarget> m_Chairs = new List<ChairTarget>();
        [SerializeField] List<ChairSlot> m_ChairSlots = new List<ChairSlot>();

        [Header("Area B - showroom UI")]
        [SerializeField] GameObject m_InstructionPanel;
        [SerializeField] TextMeshProUGUI m_Instruction;
        [SerializeField] GameObject m_StatusPanel;
        [SerializeField] TextMeshProUGUI m_Status;
        [SerializeField] TextMeshProUGUI m_Feedback;
        [SerializeField] TextMeshProUGUI m_Warning;
        [SerializeField] Button m_ExitButton;
        [SerializeField] Button m_ReadyButton;
        [SerializeField] Button m_RecenterButton;
        [SerializeField] GameObject m_ShapeLegend;
        [SerializeField] GameObject m_InstructionOverlay;
        [SerializeField] TextMeshProUGUI m_OverlayText;

        public override ExperimentArea area => ExperimentArea.AreaB;
        public override SpawnPoint primarySpawn => m_SpawnAreaB;

        public IReadOnlyList<ChairTarget> chairs => m_Chairs;
        public IReadOnlyList<ChairSlot> chairSlots => m_ChairSlots;

        public override void BindInto(PersistentSystems systems)
        {
            var ui = systems.ui;

            ui.BindShapeLegend(m_ShapeLegend);
            ui.BindAreaB(m_InstructionPanel, m_Instruction, m_StatusPanel, m_Status,
                m_Feedback, m_ExitButton, m_ReadyButton, m_InstructionOverlay, m_OverlayText);
            ui.BindSharedAreaB(m_RecenterButton, m_Warning);

            // The chairs exist only while this room is loaded, so the task is re-pointed at
            // them here rather than holding a reference across the unload.
            systems.chairTask.SetChairs(m_Chairs);
            systems.chairTask.SetSlots(m_ChairSlots);

            systems.teleporter.SetSpawnPoint(ExperimentArea.AreaB, m_SpawnAreaB);
        }


        // ---- Author-time wiring ---------------------------------------------------------
        // Called ONLY by the scene builder, in the Editor, while this room's scene is open.
        // Every reference it sets lives in the SAME scene as this component, which is what
        // makes them serializable at all -- a cross-scene reference would be dropped on load.

        public void AuthorSpawn(SpawnPoint spawn) => m_SpawnAreaB = spawn;

        public void AuthorChairs(IEnumerable<ChairTarget> chairs, IEnumerable<ChairSlot> slots)
        {
            m_Chairs.Clear();
            m_ChairSlots.Clear();

            if (chairs != null) m_Chairs.AddRange(chairs);
            if (slots != null) m_ChairSlots.AddRange(slots);
        }

        public void AuthorUi(GameObject instructionPanel, TextMeshProUGUI instruction,
            GameObject statusPanel, TextMeshProUGUI status, TextMeshProUGUI feedback,
            TextMeshProUGUI warning, Button exit, Button ready, Button recenter,
            GameObject shapeLegend, GameObject instructionOverlay, TextMeshProUGUI overlayText)
        {
            m_InstructionPanel = instructionPanel;
            m_Instruction = instruction;
            m_StatusPanel = statusPanel;
            m_Status = status;
            m_Feedback = feedback;
            m_Warning = warning;
            m_ExitButton = exit;
            m_ReadyButton = ready;
            m_RecenterButton = recenter;
            m_ShapeLegend = shapeLegend;
            m_InstructionOverlay = instructionOverlay;
            m_OverlayText = overlayText;
        }

        public override IEnumerable<string> MissingReferences()
        {
            if (m_SpawnAreaB == null) yield return nameof(m_SpawnAreaB);
            if (m_Chairs == null || m_Chairs.Count == 0) yield return nameof(m_Chairs);
            if (m_ChairSlots == null || m_ChairSlots.Count == 0) yield return nameof(m_ChairSlots);
            if (m_InstructionPanel == null) yield return nameof(m_InstructionPanel);
            if (m_ShapeLegend == null) yield return nameof(m_ShapeLegend);
            if (m_InstructionOverlay == null) yield return nameof(m_InstructionOverlay);
            if (m_ReadyButton == null) yield return nameof(m_ReadyButton);
            if (m_ExitButton == null) yield return nameof(m_ExitButton);
        }
    }
}
