using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using IkeaEeg.Interaction;
using IkeaEeg.XR;

namespace IkeaEeg.SceneFlow
{
    /// <summary>
    /// The Area A room scene: VR familiarization (Area 0) AND the entrance (Area A).
    ///
    /// The two share one scene because they share one stretch of the protocol -- the
    /// participant practises, then walks into the entrance and stays there for PRE_TASK_REST,
    /// word encoding and immediate recognition. They keep their existing world coordinates
    /// (Area 0 at x = -100, Area A at x = 0); nothing is moved, so AreaA_Visuals and every
    /// authored offset stay exactly as validated.
    ///
    /// Also carries Anchor_Recognition_AreaA. The recognition response pair ITSELF is
    /// persistent -- one object relocated between the Area A and Area C anchors -- because it
    /// is genuinely shared by the two recognition blocks and belongs to neither room.
    /// </summary>
    public sealed class AreaASceneContext : AreaSceneContext
    {
        [Header("Spawns")]
        [SerializeField] SpawnPoint m_SpawnFamiliarization;
        [SerializeField] SpawnPoint m_SpawnAreaA;

        [Header("Recognition")]
        [Tooltip("Where the PERSISTENT recognition response pair is parked while in Area A.")]
        [SerializeField] Transform m_RecognitionAnchor;

        [Header("Area 0 - language")]
        [SerializeField] GameObject m_LanguagePanel;
        [SerializeField] TextMeshProUGUI m_LanguageTitle;
        [SerializeField] Button m_LanguageEnglishButton;
        [SerializeField] Button m_LanguageSpanishButton;
        [SerializeField] Button m_LanguageJapaneseButton;

        [Header("Area 0 - familiarization")]
        [SerializeField] GameObject m_Panel0;
        [SerializeField] TextMeshProUGUI m_Instruction0;
        [SerializeField] TextMeshProUGUI m_PracticePrompt;
        [SerializeField] TextMeshProUGUI m_Status0;
        [SerializeField] TextMeshProUGUI m_Warning0;
        [SerializeField] TextMeshProUGUI m_ReadyLabel;
        [SerializeField] GameObject m_StartHighlight;
        [SerializeField] Button m_StartExperimentButton;
        [SerializeField] Button m_SkipIntroButton;
        [SerializeField] Button m_ReplayInstructionsButton;
        [SerializeField] Button m_RecenterButton0;
        [SerializeField] GameObject m_ResearcherStatusPanel;
        [SerializeField] TextMeshProUGUI m_ResearcherStatusText;
        [SerializeField] List<PracticeObject> m_PracticeObjects = new List<PracticeObject>();

        [Header("Area A - entrance")]
        [SerializeField] GameObject m_PanelA;
        [SerializeField] TextMeshProUGUI m_TitleA;
        [SerializeField] TextMeshProUGUI m_InstructionA;
        [SerializeField] TextMeshProUGUI m_WordA;
        [SerializeField] TextMeshProUGUI m_RecognitionCounterA;
        [SerializeField] TextMeshProUGUI m_DeveloperCheatsheetA;
        [SerializeField] TextMeshProUGUI m_FixationA;
        [SerializeField] TextMeshProUGUI m_StatusA;
        [SerializeField] TextMeshProUGUI m_WarningA;
        [SerializeField] Button m_StartButton;
        [SerializeField] Button m_EnterButton;
        [SerializeField] Button m_RecheckAudioButton;
        [SerializeField] Button m_RecenterButtonA;

        public override ExperimentArea area => ExperimentArea.AreaA;

        /// <summary>
        /// Area A's own standing position, NOT the practice room's.
        ///
        /// The loader recenters here when the room is entered. Familiarization begins the
        /// session at <see cref="spawnFamiliarization"/>, which the manager requests
        /// explicitly through the existing teleporter.
        /// </summary>
        public override SpawnPoint primarySpawn => m_SpawnAreaA;

        public SpawnPoint spawnFamiliarization => m_SpawnFamiliarization;
        public Transform recognitionAnchor => m_RecognitionAnchor;
        public IReadOnlyList<PracticeObject> practiceObjects => m_PracticeObjects;

        public override void BindInto(PersistentSystems systems)
        {
            var ui = systems.ui;

            ui.BindLanguagePanel(m_LanguagePanel, m_LanguageTitle, m_LanguageEnglishButton,
                m_LanguageSpanishButton, m_LanguageJapaneseButton);

            ui.BindFamiliarization(m_Panel0, m_Instruction0, m_Status0,
                m_StartExperimentButton, m_SkipIntroButton,
                m_ResearcherStatusPanel, m_ResearcherStatusText,
                m_PracticePrompt, m_ReadyLabel, m_StartHighlight,
                m_ReplayInstructionsButton);

            ui.BindAreaA(m_PanelA, m_TitleA, m_InstructionA, m_WordA, m_StatusA,
                m_StartButton, m_EnterButton, m_RecognitionCounterA,
                m_DeveloperCheatsheetA, m_FixationA);

            ui.BindSharedArea0(m_RecenterButton0, m_Warning0);
            ui.BindSharedAreaA(m_RecenterButtonA, m_WarningA, m_RecheckAudioButton);

            // Both spawns this scene owns. Area 0 and Area A share the room, so entering it
            // makes both reachable and the manager can move between them without a load.
            systems.teleporter.SetSpawnPoint(ExperimentArea.Familiarization, m_SpawnFamiliarization);
            systems.teleporter.SetSpawnPoint(ExperimentArea.AreaA, m_SpawnAreaA);

            // The practice objects exist only while this room is loaded.
            systems.manager.SetPracticeObjects(m_PracticeObjects);

            // The panel itself is persistent and shared with Area C; this room owns only the
            // place it stands while immediate recognition runs.
            if (systems.recognitionPanel != null)
                systems.recognitionPanel.SetAnchor(
                    IkeaEeg.Experiment.RecognitionPhase.Immediate, m_RecognitionAnchor);
        }


        // ---- Author-time wiring ---------------------------------------------------------
        // Called ONLY by the scene builder, in the Editor, while this room's scene is open.
        // Every reference it sets lives in the SAME scene as this component, which is what
        // makes them serializable at all -- a cross-scene reference would be dropped on load.

        public void AuthorSpawns(SpawnPoint familiarization, SpawnPoint areaA,
            Transform recognitionAnchor)
        {
            m_SpawnFamiliarization = familiarization;
            m_SpawnAreaA = areaA;
            m_RecognitionAnchor = recognitionAnchor;
        }

        public void AuthorArea0Ui(GameObject languagePanel, TextMeshProUGUI languageTitle,
            Button languageEnglish, Button languageSpanish, Button languageJapanese,
            GameObject panel, TextMeshProUGUI instruction, TextMeshProUGUI practicePrompt,
            TextMeshProUGUI status, TextMeshProUGUI warning, TextMeshProUGUI readyLabel,
            GameObject startHighlight, Button startExperiment, Button skipIntro,
            Button replayInstructions, Button recenter, GameObject researcherStatusPanel,
            TextMeshProUGUI researcherStatusText, IEnumerable<PracticeObject> practiceObjects)
        {
            m_LanguagePanel = languagePanel;
            m_LanguageTitle = languageTitle;
            m_LanguageEnglishButton = languageEnglish;
            m_LanguageSpanishButton = languageSpanish;
            m_LanguageJapaneseButton = languageJapanese;
            m_Panel0 = panel;
            m_Instruction0 = instruction;
            m_PracticePrompt = practicePrompt;
            m_Status0 = status;
            m_Warning0 = warning;
            m_ReadyLabel = readyLabel;
            m_StartHighlight = startHighlight;
            m_StartExperimentButton = startExperiment;
            m_SkipIntroButton = skipIntro;
            m_ReplayInstructionsButton = replayInstructions;
            m_RecenterButton0 = recenter;
            m_ResearcherStatusPanel = researcherStatusPanel;
            m_ResearcherStatusText = researcherStatusText;

            m_PracticeObjects.Clear();

            if (practiceObjects != null)
                m_PracticeObjects.AddRange(practiceObjects);
        }

        public void AuthorAreaAUi(GameObject panel, TextMeshProUGUI title,
            TextMeshProUGUI instruction, TextMeshProUGUI word,
            TextMeshProUGUI recognitionCounter, TextMeshProUGUI developerCheatsheet,
            TextMeshProUGUI fixation, TextMeshProUGUI status, TextMeshProUGUI warning,
            Button start, Button enter, Button recheckAudio, Button recenter)
        {
            m_PanelA = panel;
            m_TitleA = title;
            m_InstructionA = instruction;
            m_WordA = word;
            m_RecognitionCounterA = recognitionCounter;
            m_DeveloperCheatsheetA = developerCheatsheet;
            m_FixationA = fixation;
            m_StatusA = status;
            m_WarningA = warning;
            m_StartButton = start;
            m_EnterButton = enter;
            m_RecheckAudioButton = recheckAudio;
            m_RecenterButtonA = recenter;
        }

        public override IEnumerable<string> MissingReferences()
        {
            if (m_SpawnFamiliarization == null) yield return nameof(m_SpawnFamiliarization);
            if (m_SpawnAreaA == null) yield return nameof(m_SpawnAreaA);
            if (m_RecognitionAnchor == null) yield return nameof(m_RecognitionAnchor);
            if (m_LanguagePanel == null) yield return nameof(m_LanguagePanel);
            if (m_Panel0 == null) yield return nameof(m_Panel0);
            if (m_StartExperimentButton == null) yield return nameof(m_StartExperimentButton);
            if (m_PanelA == null) yield return nameof(m_PanelA);
            if (m_WordA == null) yield return nameof(m_WordA);
            if (m_FixationA == null) yield return nameof(m_FixationA);
            if (m_EnterButton == null) yield return nameof(m_EnterButton);
        }
    }
}
