using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using IkeaEeg.XR;

namespace IkeaEeg.SceneFlow
{
    /// <summary>
    /// The Area C room scene: delayed recognition, POST_TASK_REST and the results screen.
    ///
    /// Carries Anchor_Recognition_AreaC, the second parking place for the PERSISTENT
    /// recognition response pair. Area C is the last room of a session, so nothing unloads it
    /// during a normal run: SESSION_END happens with this room still open.
    /// </summary>
    public sealed class AreaCSceneContext : AreaSceneContext
    {
        [Header("Spawn")]
        [SerializeField] SpawnPoint m_SpawnAreaC;

        [Header("Recognition")]
        [Tooltip("Where the PERSISTENT recognition response pair is parked while in Area C.")]
        [SerializeField] Transform m_RecognitionAnchor;

        [Header("Area C - exit")]
        [SerializeField] GameObject m_Panel;
        [SerializeField] TextMeshProUGUI m_Instruction;
        [SerializeField] TextMeshProUGUI m_Status;
        [SerializeField] TextMeshProUGUI m_Word;
        [SerializeField] TextMeshProUGUI m_RecognitionCounter;
        [SerializeField] TextMeshProUGUI m_DeveloperCheatsheet;
        [SerializeField] TextMeshProUGUI m_Fixation;
        [SerializeField] TextMeshProUGUI m_Results;
        [SerializeField] TextMeshProUGUI m_Warning;
        [SerializeField] Button m_RestReadyButton;
        [SerializeField] Button m_RestartButton;
        [SerializeField] Button m_NewTrialButton;
        [SerializeField] Button m_EndButton;
        [SerializeField] Button m_RecenterButton;

        public override ExperimentArea area => ExperimentArea.AreaC;
        public override SpawnPoint primarySpawn => m_SpawnAreaC;

        public Transform recognitionAnchor => m_RecognitionAnchor;

        public override void BindInto(PersistentSystems systems)
        {
            var ui = systems.ui;

            ui.BindAreaC(m_Panel, m_Instruction, m_Status, m_Results,
                m_RestartButton, m_EndButton, m_NewTrialButton, m_Word,
                m_RecognitionCounter, m_DeveloperCheatsheet, m_Fixation, m_RestReadyButton);
            ui.BindSharedAreaC(m_RecenterButton, m_Warning);

            systems.teleporter.SetSpawnPoint(ExperimentArea.AreaC, m_SpawnAreaC);

            if (systems.recognitionPanel != null)
                systems.recognitionPanel.SetAnchor(
                    IkeaEeg.Experiment.RecognitionPhase.Delayed, m_RecognitionAnchor);
        }


        // ---- Author-time wiring ---------------------------------------------------------
        // Called ONLY by the scene builder, in the Editor, while this room's scene is open.
        // Every reference it sets lives in the SAME scene as this component, which is what
        // makes them serializable at all -- a cross-scene reference would be dropped on load.

        public void AuthorSpawn(SpawnPoint spawn, Transform recognitionAnchor)
        {
            m_SpawnAreaC = spawn;
            m_RecognitionAnchor = recognitionAnchor;
        }

        public void AuthorUi(GameObject panel, TextMeshProUGUI instruction,
            TextMeshProUGUI status, TextMeshProUGUI word, TextMeshProUGUI recognitionCounter,
            TextMeshProUGUI developerCheatsheet, TextMeshProUGUI fixation,
            TextMeshProUGUI results, TextMeshProUGUI warning, Button restReady,
            Button restart, Button newTrial, Button end, Button recenter)
        {
            m_Panel = panel;
            m_Instruction = instruction;
            m_Status = status;
            m_Word = word;
            m_RecognitionCounter = recognitionCounter;
            m_DeveloperCheatsheet = developerCheatsheet;
            m_Fixation = fixation;
            m_Results = results;
            m_Warning = warning;
            m_RestReadyButton = restReady;
            m_RestartButton = restart;
            m_NewTrialButton = newTrial;
            m_EndButton = end;
            m_RecenterButton = recenter;
        }

        public override IEnumerable<string> MissingReferences()
        {
            if (m_SpawnAreaC == null) yield return nameof(m_SpawnAreaC);
            if (m_RecognitionAnchor == null) yield return nameof(m_RecognitionAnchor);
            if (m_Panel == null) yield return nameof(m_Panel);
            if (m_Word == null) yield return nameof(m_Word);
            if (m_Fixation == null) yield return nameof(m_Fixation);
            if (m_RestReadyButton == null) yield return nameof(m_RestReadyButton);
            if (m_Results == null) yield return nameof(m_Results);
            if (m_EndButton == null) yield return nameof(m_EndButton);
        }
    }
}
