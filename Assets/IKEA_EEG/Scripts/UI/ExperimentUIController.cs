using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using IkeaEeg.Localization;
using IkeaEeg.XR;

namespace IkeaEeg.UI
{
    /// <summary>
    /// The world-space UI of all three areas.
    ///
    /// This is a passive view: it exposes button events and setter methods and contains no
    /// experimental logic, no timers and no knowledge of the protocol. The ExperimentManager
    /// drives it. That separation is what allows the state machine to remain the single
    /// authority on what happens when.
    ///
    /// Every reference here is wired automatically by the scene builder.
    /// </summary>
    [DisallowMultipleComponent]
    public class ExperimentUIController : MonoBehaviour
    {
        [Header("Language selection — shown before everything")]
        [SerializeField] GameObject m_LanguagePanel;
        [SerializeField] TMP_Text m_LanguageTitle;
        [SerializeField] Button m_LanguageEnglishButton;
        [SerializeField] Button m_LanguageSpanishButton;
        [SerializeField] Button m_LanguageJapaneseButton;

        [Header("Area 0 — VR familiarization")]
        [SerializeField] GameObject m_FamiliarizationPanel;
        [SerializeField] TMP_Text m_FamiliarizationInstruction;
        [SerializeField] TMP_Text m_FamiliarizationStatus;

        [Tooltip("The practice-task prompt, e.g. 'Select the BLUE object.'")]
        [SerializeField] TMP_Text m_PracticePrompt;

        [SerializeField] Button m_StartExperimentButton;

        [Tooltip("Label under START EXPERIMENT that appears once practice succeeds.")]
        [SerializeField] TMP_Text m_StartExperimentReadyLabel;

        [Tooltip("Highlight frame behind START EXPERIMENT. Enabled once practice succeeds, so " +
                 "prominence is not carried by colour alone.")]
        [SerializeField] GameObject m_StartExperimentHighlight;

        [SerializeField] Button m_SkipIntroButton;

        [Tooltip("Replays the spoken familiarization instructions once. Never loops.")]
        [SerializeField] Button m_ReplayInstructionsButton;

        [Tooltip("Researcher-only placeholder. Hidden and empty in this pass — reserved for a " +
                 "future EEG/marker status readout. It never shows anything to the participant.")]
        [SerializeField] GameObject m_ResearcherStatusPanel;

        [SerializeField] TMP_Text m_ResearcherStatusText;

        [Header("Area A — entrance")]
        [SerializeField] GameObject m_AreaAPanel;
        [SerializeField] TMP_Text m_AreaATitle;
        [SerializeField] TMP_Text m_AreaAInstruction;
        [SerializeField] TMP_Text m_AreaAWord;

        [Tooltip("Participant-facing 'Item X / N' progress line for the IMMEDIATE recognition " +
                 "phase. Visible only while a recognition item is on screen.")]
        [SerializeField] TMP_Text m_AreaARecognitionCounter;

        [Tooltip("DEVELOPER QA ONLY. Names the current item TARGET/LURE and the correct answer. " +
                 "Gated by ExperimentConfig.enableRecognitionDeveloperCheatsheet and hidden by " +
                 "default; never part of participant-facing behaviour.")]
        [SerializeField] TMP_Text m_AreaADeveloperCheatsheet;

        [SerializeField] TMP_Text m_AreaAStatus;
        [SerializeField] Button m_StartButton;
        [SerializeField] Button m_EnterAreaBButton;

        [Header("Area B — showroom")]
        [Tooltip("Persistent panel carrying the chair instruction. Stays visible during selection.")]
        [SerializeField] GameObject m_AreaBInstructionPanel;
        [SerializeField] TMP_Text m_AreaBInstruction;

        [SerializeField] GameObject m_AreaBStatusPanel;
        [SerializeField] TMP_Text m_AreaBStatus;
        [SerializeField] TMP_Text m_AreaBFeedback;
        [SerializeField] Button m_ExitToAreaCButton;

        [Tooltip("Gates the start of the chair block: shown with the general task instructions " +
                 "and hidden for the rest of the block.")]
        [SerializeField] Button m_ReadyButton;

        [Tooltip("Non-interactive legend of the three shape categories. Shown ONLY on the " +
                 "general instruction screen, never during a trial.")]
        [SerializeField] GameObject m_ShapeLegend;

        [Tooltip("The large instruction surface shown on entering Area B. It stands between " +
                 "the participant and the chairs and deliberately blocks them until READY.")]
        [SerializeField] GameObject m_AreaBInstructionOverlay;

        [SerializeField] TMP_Text m_AreaBOverlayText;

        [Header("Area C — exit")]
        [SerializeField] GameObject m_AreaCPanel;
        [SerializeField] TMP_Text m_AreaCInstruction;

        [Tooltip("The large centre label that carries the DELAYED recognition stimulus word. " +
                 "The Area A label cannot serve here: it lives on the Area A canvas, in a " +
                 "different room, and that canvas is inactive while Area C is open.")]
        [SerializeField] TMP_Text m_AreaCWord;

        [Tooltip("The Area C twin of the recognition progress line. Separate object for the same " +
                 "reason the stimulus label is: the Area A canvas is inactive during Area C.")]
        [SerializeField] TMP_Text m_AreaCRecognitionCounter;

        [Tooltip("The Area C twin of the developer QA overlay. Same gate, same rules.")]
        [SerializeField] TMP_Text m_AreaCDeveloperCheatsheet;

        [SerializeField] TMP_Text m_AreaCStatus;
        [SerializeField] TMP_Text m_AreaCResults;
        [SerializeField] Button m_RestartButton;
        [SerializeField] Button m_EndButton;
        [SerializeField] Button m_NewTrialButton;

        [Header("Recenter (one per area) + blocking warning")]
        [SerializeField] Button m_RecenterButton0;
        [SerializeField] TMP_Text m_Warning0;


        [SerializeField] Button m_RecenterButtonA;
        [SerializeField] Button m_RecenterButtonB;
        [SerializeField] Button m_RecenterButtonC;

        [Header("Recenter placement (Areas A and C)")]
        [Tooltip("Where Recenter sits while the START-state UI is on screen. Displaced to the " +
                 "left so it cannot collide with the start/enter row.")]
        [SerializeField] Vector2 m_RecenterDisplacedPosition = new Vector2(-580f, -430f);

        [Tooltip("Where Recenter sits in Area A during the normal task state: horizontally " +
                 "centred, and high enough to clear the recognition response buttons in front " +
                 "of the panel. Area A only — see SetRecenterDisplaced.")]
        [SerializeField] Vector2 m_RecenterCenteredPosition = new Vector2(0f, -474f);

        /// <summary>Tracks the current placement so a repeat call does no layout work.</summary>
        bool m_RecenterDisplaced = true;

        // Which of the lower-centre buttons are currently on screen. Recenter is displaced while
        // ANY of them is up, because they all occupy the same anchored row (0, -430) and the
        // centred Recenter at (0, -474) overlaps that row directly.
        bool m_StartButtonVisible;
        bool m_EnterAreaBButtonVisible;
        bool m_RecheckAudioButtonVisible;

        [Tooltip("High-visibility warning shown when stimuli cannot be delivered.")]
        [SerializeField] TMP_Text m_WarningA;
        [SerializeField] TMP_Text m_WarningB;
        [SerializeField] TMP_Text m_WarningC;

        [Tooltip("Re-runs the audio check so a routing fix can be applied without restarting.")]
        [SerializeField] Button m_RecheckAudioButton;

        [Header("Developer navigation (NOT participant functionality)")]
        [SerializeField] Button m_DevGoArea0Button;
        [SerializeField] Button m_DevGoAreaAButton;
        [SerializeField] Button m_DevGoAreaBButton;
        [SerializeField] Button m_DevGoAreaCButton;
        [SerializeField] Button m_DevCloseButton;
        [SerializeField] Button m_DevAbortButton;
        [SerializeField] Button m_DevLanguageButton;

        /// <summary>A platform language was chosen on the language screen.</summary>
        public event Action<ExperimentLanguage> languageSelected;

        /// <summary>Area 0: the participant is ready to begin the experiment.</summary>
        public event Action startExperimentPressed;

        /// <summary>Area 0: skip familiarization (researcher / returning participant).</summary>
        public event Action skipIntroPressed;

        /// <summary>Area 0: replay the spoken instructions once.</summary>
        public event Action replayInstructionsPressed;

        public event Action startPressed;
        public event Action enterAreaBPressed;

        /// <summary>The participant confirmed they understood the Area B task instructions.</summary>
        public event Action readyPressed;

        public event Action exitToAreaCPressed;
        public event Action restartPressed;
        /// <summary>NEW TRIAL: keep this run and start another one at Area A.</summary>
        public event Action newTrialPressed;

        public event Action endPressed;
        public event Action recenterPressed;
        public event Action recheckAudioPressed;

        /// <summary>Developer menu: jump to an area. Marks the run DEVELOPER_INTERRUPTED.</summary>
        public event Action<ExperimentArea> developerGoToArea;

        public event Action developerCloseMenu;
        public event Action developerAbortSession;

        /// <summary>Developer menu: go back to the language screen. Researcher-only.</summary>
        public event Action developerReturnToLanguage;

        void Awake()
        {
            // Covers a scene built before the guard existed. Idempotent, so a scene that already
            // carries the component is untouched.
            EnsureSessionControlLocks();
        }

        void OnEnable()
        {
            AddListener(m_LanguageEnglishButton, RaiseLanguageEnglish);
            AddListener(m_LanguageSpanishButton, RaiseLanguageSpanish);
            AddListener(m_LanguageJapaneseButton, RaiseLanguageJapanese);
            AddListener(m_StartExperimentButton, RaiseStartExperiment);
            AddListener(m_SkipIntroButton, RaiseSkipIntro);
            AddListener(m_ReplayInstructionsButton, RaiseReplayInstructions);
            AddListener(m_RecenterButton0, RaiseRecenter);
            AddListener(m_StartButton, RaiseStart);
            AddListener(m_EnterAreaBButton, RaiseEnterAreaB);
            AddListener(m_ReadyButton, RaiseReady);
            AddListener(m_ExitToAreaCButton, RaiseExitToAreaC);
            AddListener(m_RestartButton, RaiseRestart);
            AddListener(m_NewTrialButton, RaiseNewTrial);
            AddListener(m_EndButton, RaiseEnd);
            AddListener(m_RecenterButtonA, RaiseRecenter);
            AddListener(m_RecenterButtonB, RaiseRecenter);
            AddListener(m_RecenterButtonC, RaiseRecenter);
            AddListener(m_RecheckAudioButton, RaiseRecheckAudio);
            AddListener(m_DevGoArea0Button, RaiseDevGoArea0);
            AddListener(m_DevGoAreaAButton, RaiseDevGoAreaA);
            AddListener(m_DevGoAreaBButton, RaiseDevGoAreaB);
            AddListener(m_DevGoAreaCButton, RaiseDevGoAreaC);
            AddListener(m_DevCloseButton, RaiseDevClose);
            AddListener(m_DevAbortButton, RaiseDevAbort);
            AddListener(m_DevLanguageButton, RaiseDevLanguage);
        }

        void OnDisable()
        {
            RemoveListener(m_LanguageEnglishButton, RaiseLanguageEnglish);
            RemoveListener(m_LanguageSpanishButton, RaiseLanguageSpanish);
            RemoveListener(m_LanguageJapaneseButton, RaiseLanguageJapanese);
            RemoveListener(m_StartExperimentButton, RaiseStartExperiment);
            RemoveListener(m_SkipIntroButton, RaiseSkipIntro);
            RemoveListener(m_ReplayInstructionsButton, RaiseReplayInstructions);
            RemoveListener(m_RecenterButton0, RaiseRecenter);
            RemoveListener(m_StartButton, RaiseStart);
            RemoveListener(m_EnterAreaBButton, RaiseEnterAreaB);
            RemoveListener(m_ReadyButton, RaiseReady);
            RemoveListener(m_ExitToAreaCButton, RaiseExitToAreaC);
            RemoveListener(m_RestartButton, RaiseRestart);
            RemoveListener(m_NewTrialButton, RaiseNewTrial);
            RemoveListener(m_EndButton, RaiseEnd);
            RemoveListener(m_RecenterButtonA, RaiseRecenter);
            RemoveListener(m_RecenterButtonB, RaiseRecenter);
            RemoveListener(m_RecenterButtonC, RaiseRecenter);
            RemoveListener(m_RecheckAudioButton, RaiseRecheckAudio);
            RemoveListener(m_DevGoArea0Button, RaiseDevGoArea0);
            RemoveListener(m_DevGoAreaAButton, RaiseDevGoAreaA);
            RemoveListener(m_DevGoAreaBButton, RaiseDevGoAreaB);
            RemoveListener(m_DevGoAreaCButton, RaiseDevGoAreaC);
            RemoveListener(m_DevCloseButton, RaiseDevClose);
            RemoveListener(m_DevAbortButton, RaiseDevAbort);
            RemoveListener(m_DevLanguageButton, RaiseDevLanguage);
        }

        void RaiseLanguageEnglish() => languageSelected?.Invoke(ExperimentLanguage.English);
        void RaiseLanguageSpanish() => languageSelected?.Invoke(ExperimentLanguage.Spanish);
        void RaiseLanguageJapanese() => languageSelected?.Invoke(ExperimentLanguage.Japanese);
        void RaiseStartExperiment() => startExperimentPressed?.Invoke();
        void RaiseSkipIntro() => skipIntroPressed?.Invoke();
        void RaiseReplayInstructions() => replayInstructionsPressed?.Invoke();
        void RaiseStart() => startPressed?.Invoke();
        void RaiseEnterAreaB() => enterAreaBPressed?.Invoke();
        void RaiseReady() => readyPressed?.Invoke();
        void RaiseExitToAreaC() => exitToAreaCPressed?.Invoke();
        void RaiseRestart() => restartPressed?.Invoke();
        void RaiseNewTrial() => newTrialPressed?.Invoke();
        void RaiseEnd() => endPressed?.Invoke();
        void RaiseRecenter() => recenterPressed?.Invoke();

        /// <summary>
        /// Moves Recenter between its START-state and normal-task positions.
        ///
        /// WHY IT MOVES AT ALL. Recenter and the START/ENTER row both want the lower-centre of
        /// the Area A panel, and the recognition response buttons occupy the lower-centre of the
        /// world in FRONT of that panel. No single fixed position clears both, so the control
        /// that is not the task moves out of the way while the start row is up, and comes back
        /// to a comfortable centred spot once it is gone.
        ///
        /// Driven by <see cref="ShowStartButton"/> rather than polled: the start button's
        /// visibility IS the state being tracked, so there is nothing to poll and nothing that
        /// can drift out of sync with it.
        ///
        /// AREA A ONLY. Area C was originally included and that was wrong: its Recenter sits
        /// at the bottom of a much taller results canvas, where it already clears the delayed
        /// recognition buttons, and moving it up to the shared centred position put it on top of
        /// the RESTART / NEW TRIAL / END row. The existing final-screen overlap test caught it.
        /// Areas 0, B and C keep their own fixed placement.
        /// </summary>
        public void SetRecenterDisplaced(bool displaced)
        {
            if (m_RecenterDisplaced == displaced)
                return;

            m_RecenterDisplaced = displaced;

            var position = displaced ? m_RecenterDisplacedPosition : m_RecenterCenteredPosition;

            MoveRecenter(m_RecenterButtonA, position);
        }

        /// <summary>
        /// Re-evaluates where Recenter belongs from what is actually on screen.
        ///
        /// THE BUG THIS FIXES: placement used to be driven by the START button alone. START,
        /// ENTER SHOWROOM and RE-CHECK AUDIO all share the anchored row at (0, -430), so when
        /// recognition ended and ENTER SHOWROOM appeared, Recenter was still centred at
        /// (0, -474) and the two overlapped — Recenter spans y -524..-424 and the row spans
        /// -495..-365.
        ///
        /// Driven by the setters that show those buttons, so it is event-based and cannot drift
        /// out of sync with what the participant can see. Nothing polls.
        /// </summary>
        void UpdateRecenterPlacement()
        {
            SetRecenterDisplaced(m_StartButtonVisible ||
                                 m_EnterAreaBButtonVisible ||
                                 m_RecheckAudioButtonVisible);
        }

        /// <summary>True while Recenter is in its displaced (START-state) position.</summary>
        public bool recenterDisplaced => m_RecenterDisplaced;

        public Vector2 recenterDisplacedPosition => m_RecenterDisplacedPosition;
        public Vector2 recenterCenteredPosition => m_RecenterCenteredPosition;

        static void MoveRecenter(Button button, Vector2 anchoredPosition)
        {
            if (button == null)
                return;

            var rect = button.transform as RectTransform;

            if (rect != null)
                rect.anchoredPosition = anchoredPosition;
        }
        void RaiseRecheckAudio() => recheckAudioPressed?.Invoke();

        void RaiseDevGoArea0() => developerGoToArea?.Invoke(ExperimentArea.Familiarization);
        void RaiseDevGoAreaA() => developerGoToArea?.Invoke(ExperimentArea.AreaA);
        void RaiseDevGoAreaB() => developerGoToArea?.Invoke(ExperimentArea.AreaB);
        void RaiseDevGoAreaC() => developerGoToArea?.Invoke(ExperimentArea.AreaC);
        void RaiseDevClose() => developerCloseMenu?.Invoke();
        void RaiseDevAbort() => developerAbortSession?.Invoke();
        void RaiseDevLanguage() => developerReturnToLanguage?.Invoke();

        /// <summary>Wires the developer panel. Called by the scene builder only.</summary>
        public void BindDeveloperPanel(Button area0, Button areaA, Button areaB, Button areaC,
            Button close, Button abort, Button language = null)
        {
            m_DevGoArea0Button = area0;
            m_DevGoAreaAButton = areaA;
            m_DevGoAreaBButton = areaB;
            m_DevGoAreaCButton = areaC;
            m_DevCloseButton = close;
            m_DevAbortButton = abort;
            m_DevLanguageButton = language;
        }

        static void AddListener(Button button, UnityEngine.Events.UnityAction action)
        {
            if (button != null)
                button.onClick.AddListener(action);
        }

        static void RemoveListener(Button button, UnityEngine.Events.UnityAction action)
        {
            if (button != null)
                button.onClick.RemoveListener(action);
        }

        // ---------------------------------------------------------------------------------
        // Panel visibility
        // ---------------------------------------------------------------------------------

        /// <summary>Shows only the panels belonging to the given area.</summary>
        public void ShowArea(ExperimentArea area)
        {
            SetActive(m_FamiliarizationPanel, area == ExperimentArea.Familiarization);
            SetActive(m_AreaAPanel, area == ExperimentArea.AreaA);
            SetActive(m_AreaBInstructionPanel, area == ExperimentArea.AreaB);
            SetActive(m_AreaBStatusPanel, area == ExperimentArea.AreaB);
            SetActive(m_AreaCPanel, area == ExperimentArea.AreaC);

            // The instruction overlay and its legend belong to the Area B instruction screen
            // only; leaving an area always takes them down, so neither can survive into a trial.
            if (area != ExperimentArea.AreaB)
            {
                ShowShapeLegend(false);
                SetActive(m_AreaBInstructionOverlay, false);
            }
        }

        // ---------------------------------------------------------------------------------
        // Area 0 — familiarization
        // ---------------------------------------------------------------------------------

        public void SetFamiliarizationInstruction(string text) =>
            SetText(m_FamiliarizationInstruction, text);

        public void SetFamiliarizationStatus(string text) => SetText(m_FamiliarizationStatus, text);

        // ---------------------------------------------------------------------------------
        // Language selection
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Shows or hides the language screen.
        ///
        /// While it is up the familiarization panel is down, so the participant sees exactly
        /// one thing: three languages. Nothing else on screen would be readable to them yet.
        /// </summary>
        public void ShowLanguagePanel(bool visible)
        {
            SetActive(m_LanguagePanel, visible);

            if (visible)
                SetActive(m_FamiliarizationPanel, false);
        }

        public void ShowFamiliarizationPanel(bool visible) =>
            SetActive(m_FamiliarizationPanel, visible);

        /// <summary>True while the language screen is up.</summary>
        public bool languagePanelVisible =>
            m_LanguagePanel != null && m_LanguagePanel.activeSelf;

        public void SetPracticePrompt(string text) => SetText(m_PracticePrompt, text);

        public void ShowStartExperimentButton(bool visible) =>
            SetActive(m_StartExperimentButton, visible);

        public void ShowSkipIntroButton(bool visible) => SetActive(m_SkipIntroButton, visible);

        public void ShowReplayInstructionsButton(bool visible) =>
            SetActive(m_ReplayInstructionsButton, visible);

        /// <summary>
        /// Raises START EXPERIMENT to its "you are ready" state after successful practice.
        ///
        /// It does NOT gate the button — START EXPERIMENT is usable from the moment Area 0 is
        /// entered, practice or no practice. This only makes the next step obvious once the
        /// participant has shown they can select, and it does so with a highlight frame AND a
        /// text label, never with colour alone.
        /// </summary>
        public void SetStartExperimentReady(bool ready)
        {
            SetActive(m_StartExperimentHighlight, ready);

            if (m_StartExperimentReadyLabel != null)
                m_StartExperimentReadyLabel.gameObject.SetActive(ready);
        }

        public void SetStartExperimentReadyLabel(string text) =>
            SetText(m_StartExperimentReadyLabel, text);

        /// <summary>
        /// Researcher-only status surface, reserved for a future EEG/marker/signal-quality
        /// readout. It is inactive and empty in this pass and shows the participant nothing.
        /// NOTHING may write a fabricated value here — see the EEG pipeline document.
        /// </summary>
        public void ShowResearcherStatusPanel(bool visible) =>
            SetActive(m_ResearcherStatusPanel, visible);

        public void SetResearcherStatusText(string text) => SetText(m_ResearcherStatusText, text);

        // ---------------------------------------------------------------------------------
        // Area A
        // ---------------------------------------------------------------------------------

        public void SetAreaATitle(string text) => SetText(m_AreaATitle, text);
        public void SetAreaAInstruction(string text) => SetText(m_AreaAInstruction, text);
        public void SetAreaAStatus(string text) => SetText(m_AreaAStatus, text);

        /// <summary>
        /// Clears the large centre label in BOTH recognition areas.
        ///
        /// Both, not "the one for the current area": this method is also the reset path, and a
        /// word left behind on the area the participant is about to walk into would be a
        /// stimulus nobody presented.
        /// </summary>
        public void ClearWordDisplay()
        {
            SetText(m_AreaAWord, string.Empty);
            SetText(m_AreaCWord, string.Empty);
        }

        /// <summary>
        /// Puts one memory word on the large centre label.
        ///
        /// WHY THIS NOW EXISTS. This method was previously and deliberately absent: the
        /// FreeRecall protocol presents its words as an AUDITORY-ONLY stimulus, and displaying
        /// one would have broken that paradigm, so the UI offered no way to do it.
        ///
        /// The Recognition protocol is the opposite by design — its encoding stimuli are
        /// VISUAL-ONLY and are never spoken. Both paradigms now exist side by side, so the
        /// constraint moves from "this method does not exist" to "only the Recognition path
        /// calls it". <see cref="ExperimentManager"/> calls this exclusively from the visual
        /// encoding and recognition coroutines; the FreeRecall path still only ever calls
        /// <see cref="ClearWordDisplay"/>, so its auditory-only guarantee is unchanged.
        ///
        /// The self-test asserts that separation, because a comment cannot enforce it.
        ///
        /// WHY IT WRITES TWO LABELS. Each area owns its own world-space canvas, standing in its
        /// own room, and <see cref="ShowArea"/> deactivates every canvas but the current one.
        /// The Area A label therefore cannot carry the DELAYED stimulus: during Area C it is a
        /// disabled object 25 m away. That is the bug this pair fixes — the delayed word was
        /// being written, correctly and to the right string, onto a label nobody could see.
        ///
        /// Writing both is the same idiom the project already uses for the warning banner
        /// (<see cref="SetWarning"/>, four labels) and Recenter (four buttons): one call, one
        /// visible result, because at most one of the canvases is ever active. It is NOT two
        /// stimuli — the inactive one renders nothing.
        /// </summary>
        public void SetWordDisplay(string word)
        {
            SetText(m_AreaAWord, word);
            SetText(m_AreaCWord, word);
        }

        /// <summary>The Area C stimulus label, for the self test. Null before the scene build.</summary>
        public TMP_Text areaCWordLabel => m_AreaCWord;

        /// <summary>The Area A stimulus label, for the self test.</summary>
        public TMP_Text areaAWordLabel => m_AreaAWord;

        // ---------------------------------------------------------------------------------
        // Recognition progress counter (participant-facing)
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Writes the "Item X / N" progress line in BOTH recognition areas.
        ///
        /// Two labels for exactly the reason the stimulus word needs two: each area owns its own
        /// world-space canvas standing in its own room, and only one is ever active. Writing to a
        /// single label would reproduce the delayed-word bug — a correct string on a disabled
        /// object 200 m away. The inactive one renders nothing, so this is one visible line.
        /// </summary>
        public void SetRecognitionCounter(string text)
        {
            SetText(m_AreaARecognitionCounter, text);
            SetText(m_AreaCRecognitionCounter, text);
        }

        /// <summary>
        /// Shows or hides the progress line in both areas.
        ///
        /// Hiding also CLEARS it, so a stale "Item 30 / 30" cannot be left behind on a panel the
        /// participant walks back into.
        /// </summary>
        public void ShowRecognitionCounter(bool visible)
        {
            if (!visible)
                SetRecognitionCounter(string.Empty);

            SetActive(m_AreaARecognitionCounter, visible);
            SetActive(m_AreaCRecognitionCounter, visible);
        }

        public TMP_Text areaARecognitionCounter => m_AreaARecognitionCounter;
        public TMP_Text areaCRecognitionCounter => m_AreaCRecognitionCounter;

        // ---------------------------------------------------------------------------------
        // Developer QA cheatsheet (NOT participant functionality)
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Writes the developer QA overlay in both recognition areas.
        ///
        /// THIS IS A VIEW SETTER AND NOTHING ELSE. It has no knowledge of the item, performs no
        /// classification, raises no event and cannot reach the response path. The manager
        /// decides whether the gate is open, what the text says and when it is shown; this only
        /// puts a string on a label.
        /// </summary>
        public void SetDeveloperCheatsheet(string text)
        {
            SetText(m_AreaADeveloperCheatsheet, text);
            SetText(m_AreaCDeveloperCheatsheet, text);
        }

        /// <summary>Shows or hides the developer overlay in both areas, clearing it when hidden.</summary>
        public void ShowDeveloperCheatsheet(bool visible)
        {
            if (!visible)
                SetDeveloperCheatsheet(string.Empty);

            SetActive(m_AreaADeveloperCheatsheet, visible);
            SetActive(m_AreaCDeveloperCheatsheet, visible);
        }

        public TMP_Text areaADeveloperCheatsheet => m_AreaADeveloperCheatsheet;
        public TMP_Text areaCDeveloperCheatsheet => m_AreaCDeveloperCheatsheet;

        /// <summary>True while either developer overlay label is on screen. Used by the self test.</summary>
        public bool developerCheatsheetVisible =>
            (m_AreaADeveloperCheatsheet != null &&
             m_AreaADeveloperCheatsheet.gameObject.activeSelf) ||
            (m_AreaCDeveloperCheatsheet != null &&
             m_AreaCDeveloperCheatsheet.gameObject.activeSelf);

        /// <summary>True while either progress line is on screen.</summary>
        public bool recognitionCounterVisible =>
            (m_AreaARecognitionCounter != null &&
             m_AreaARecognitionCounter.gameObject.activeSelf) ||
            (m_AreaCRecognitionCounter != null &&
             m_AreaCRecognitionCounter.gameObject.activeSelf);

        public void ShowStartButton(bool visible)
        {
            SetActive(m_StartButton, visible);
            m_StartButtonVisible = visible;
            UpdateRecenterPlacement();
        }
        public void ShowEnterAreaBButton(bool visible)
        {
            SetActive(m_EnterAreaBButton, visible);
            m_EnterAreaBButtonVisible = visible;
            UpdateRecenterPlacement();
        }

        // ---------------------------------------------------------------------------------
        // Area B
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The main Area B panel. It carries the general task instructions before READY and the
        /// trial's target specification afterwards — one panel, one thing on it at a time, so
        /// the participant is never reading the task description and a target at once.
        /// </summary>
        public void SetChairInstruction(string text) => SetText(m_AreaBInstruction, text);

        public void SetAreaBStatus(string text) => SetText(m_AreaBStatus, text);
        public void SetAreaBFeedback(string text) => SetText(m_AreaBFeedback, text);
        public void ShowExitToAreaCButton(bool visible) => SetActive(m_ExitToAreaCButton, visible);
        public void ShowReadyButton(bool visible) => SetActive(m_ReadyButton, visible);

        /// <summary>
        /// The shape-category legend. Shown ONLY while the general Area B instructions are up.
        ///
        /// It is switched off the moment the block starts and cannot be switched on again for
        /// the rest of the block: during a trial the participant must work from the three
        /// target attributes alone, not from a permanent visual key.
        /// </summary>
        public void ShowShapeLegend(bool visible) => SetActive(m_ShapeLegend, visible);

        /// <summary>
        /// The Area B instruction overlay: a large surface directly in front of the participant
        /// that carries the task description and the shape definitions.
        ///
        /// It intentionally OCCLUDES the six chairs. The participant should understand what
        /// they are being asked to do before they start looking at the stimuli — and a chair
        /// inspected during the instructions is a chair already searched before the trial
        /// begins. Taken down on READY, and never shown again for the rest of the block.
        /// </summary>
        public void ShowAreaBInstructionOverlay(bool visible)
        {
            SetActive(m_AreaBInstructionOverlay, visible);
            ShowShapeLegend(visible);
        }

        public void SetAreaBOverlayText(string text) => SetText(m_AreaBOverlayText, text);

        /// <summary>True while the instruction overlay is occluding the chairs.</summary>
        public bool areaBOverlayVisible =>
            m_AreaBInstructionOverlay != null && m_AreaBInstructionOverlay.activeSelf;

        /// <summary>True when the legend is currently on screen. Used by the self test.</summary>
        public bool shapeLegendVisible =>
            m_ShapeLegend != null && m_ShapeLegend.activeSelf;

        // ---------------------------------------------------------------------------------
        // Area C
        // ---------------------------------------------------------------------------------

        public void SetAreaCInstruction(string text) => SetText(m_AreaCInstruction, text);
        public void SetAreaCStatus(string text) => SetText(m_AreaCStatus, text);
        public void SetResults(string text) => SetText(m_AreaCResults, text);
        public void ShowRestartButton(bool visible) => SetActive(m_RestartButton, visible);
        public void ShowNewTrialButton(bool visible) => SetActive(m_NewTrialButton, visible);
        public void ShowEndButton(bool visible) => SetActive(m_EndButton, visible);

        // ---------------------------------------------------------------------------------
        // Reset
        // ---------------------------------------------------------------------------------

        // ---------------------------------------------------------------------------------
        // Warnings and recenter (available in every area)
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Shows the same high-visibility warning on all three areas' panels. Empty clears it.
        /// Used when auditory stimuli cannot be delivered and the trial must not proceed.
        /// </summary>
        public void SetWarning(string text)
        {
            SetText(m_Warning0, text);
            SetText(m_WarningA, text);
            SetText(m_WarningB, text);
            SetText(m_WarningC, text);
        }

        public void ShowRecheckAudioButton(bool visible)
        {
            SetActive(m_RecheckAudioButton, visible);
            m_RecheckAudioButtonVisible = visible;
            UpdateRecenterPlacement();
        }

        public void ShowRecenterButtons(bool visible)
        {
            SetActive(m_RecenterButton0, visible);
            SetActive(m_RecenterButtonA, visible);
            SetActive(m_RecenterButtonB, visible);
            SetActive(m_RecenterButtonC, visible);
        }

        /// <summary>Returns every panel, label and button to its pre-trial state.</summary>
        public void ResetUI()
        {
            ClearWordDisplay();

            // Both recognition overlays go down on any reset. The counter belongs to an item
            // that is no longer on screen, and the developer overlay must never survive into a
            // state nobody armed it for.
            ShowRecognitionCounter(false);
            ShowDeveloperCheatsheet(false);

            SetWarning(string.Empty);
            ShowRecenterButtons(true);
            ShowRecheckAudioButton(false);
            SetAreaAStatus(string.Empty);
            SetAreaBStatus(string.Empty);
            SetAreaBFeedback(string.Empty);
            SetChairInstruction(string.Empty);
            SetAreaCStatus(string.Empty);
            SetResults(string.Empty);

            ShowStartButton(true);
            ShowEnterAreaBButton(false);
            ShowReadyButton(false);
            ShowShapeLegend(false);
            ShowExitToAreaCButton(false);
            ShowRestartButton(false);
            ShowNewTrialButton(false);
            ShowEndButton(false);

            // Area 0 controls are driven explicitly by the manager, never left over from a
            // previous run.
            ShowStartExperimentButton(false);
            ShowSkipIntroButton(false);
            SetFamiliarizationStatus(string.Empty);
            ShowResearcherStatusPanel(false);

            ShowArea(ExperimentArea.AreaA);
        }

        static void SetText(TMP_Text label, string value)
        {
            if (label != null)
                label.text = value ?? string.Empty;
        }

        static void SetActive(GameObject go, bool active)
        {
            if (go != null && go.activeSelf != active)
                go.SetActive(active);
        }

        static void SetActive(Button button, bool active)
        {
            if (button != null && button.gameObject.activeSelf != active)
                button.gameObject.SetActive(active);
        }

        static void SetActive(TMP_Text label, bool active)
        {
            if (label != null && label.gameObject.activeSelf != active)
                label.gameObject.SetActive(active);
        }

        // ---------------------------------------------------------------------------------
        // Builder wiring
        // ---------------------------------------------------------------------------------

        public void BindAreaA(GameObject panel, TMP_Text title, TMP_Text instruction,
            TMP_Text word, TMP_Text status, Button startButton, Button enterButton,
            TMP_Text recognitionCounter = null, TMP_Text developerCheatsheet = null)
        {
            m_AreaAPanel = panel;
            m_AreaATitle = title;
            m_AreaAInstruction = instruction;
            m_AreaAWord = word;
            m_AreaAStatus = status;
            m_StartButton = startButton;
            m_EnterAreaBButton = enterButton;
            m_AreaARecognitionCounter = recognitionCounter;
            m_AreaADeveloperCheatsheet = developerCheatsheet;
        }

        public void BindShared(Button recenterA, Button recenterB, Button recenterC,
            TMP_Text warningA, TMP_Text warningB, TMP_Text warningC, Button recheckAudio,
            Button recenter0 = null, TMP_Text warning0 = null)
        {
            m_RecenterButton0 = recenter0;
            m_Warning0 = warning0;
            m_RecenterButtonA = recenterA;
            m_RecenterButtonB = recenterB;
            m_RecenterButtonC = recenterC;
            m_WarningA = warningA;
            m_WarningB = warningB;
            m_WarningC = warningC;
            m_RecheckAudioButton = recheckAudio;
        }

        public void BindLanguagePanel(GameObject panel, TMP_Text title, Button english,
            Button spanish, Button japanese)
        {
            m_LanguagePanel = panel;
            m_LanguageTitle = title;
            m_LanguageEnglishButton = english;
            m_LanguageSpanishButton = spanish;
            m_LanguageJapaneseButton = japanese;
        }

        public void BindFamiliarization(GameObject panel, TMP_Text instruction, TMP_Text status,
            Button startExperimentButton, Button skipIntroButton,
            GameObject researcherStatusPanel, TMP_Text researcherStatusText,
            TMP_Text practicePrompt = null, TMP_Text readyLabel = null,
            GameObject startHighlight = null, Button replayButton = null)
        {
            m_FamiliarizationPanel = panel;
            m_FamiliarizationInstruction = instruction;
            m_FamiliarizationStatus = status;
            m_StartExperimentButton = startExperimentButton;
            m_SkipIntroButton = skipIntroButton;
            m_PracticePrompt = practicePrompt;
            m_StartExperimentReadyLabel = readyLabel;
            m_StartExperimentHighlight = startHighlight;
            m_ReplayInstructionsButton = replayButton;
            m_ResearcherStatusPanel = researcherStatusPanel;
            m_ResearcherStatusText = researcherStatusText;
        }

        public void BindShapeLegend(GameObject legend)
        {
            m_ShapeLegend = legend;
        }

        public void BindAreaB(GameObject instructionPanel, TMP_Text instruction,
            GameObject statusPanel, TMP_Text status, TMP_Text feedback, Button exitButton,
            Button readyButton, GameObject instructionOverlay = null,
            TMP_Text overlayText = null)
        {
            m_AreaBInstructionPanel = instructionPanel;
            m_AreaBInstruction = instruction;
            m_AreaBStatusPanel = statusPanel;
            m_AreaBStatus = status;
            m_AreaBFeedback = feedback;
            m_ExitToAreaCButton = exitButton;
            m_ReadyButton = readyButton;
            m_AreaBInstructionOverlay = instructionOverlay;
            m_AreaBOverlayText = overlayText;
        }

        public void BindAreaC(GameObject panel, TMP_Text instruction, TMP_Text status,
            TMP_Text results, Button restartButton, Button endButton, Button newTrialButton = null,
            TMP_Text word = null, TMP_Text recognitionCounter = null,
            TMP_Text developerCheatsheet = null)
        {
            m_AreaCPanel = panel;
            m_AreaCInstruction = instruction;
            m_AreaCStatus = status;
            m_AreaCResults = results;
            m_AreaCWord = word;
            m_AreaCRecognitionCounter = recognitionCounter;
            m_AreaCDeveloperCheatsheet = developerCheatsheet;
            m_RestartButton = restartButton;
            m_EndButton = endButton;
            m_NewTrialButton = newTrialButton;

            // Serialized into the scene at build time, so a freshly built scene carries the
            // guard without needing anything at run time.
            EnsureSessionControlLocks();
        }

        // ---------------------------------------------------------------------------------
        // Accidental-action guard
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The three run-management buttons, which are the only controls guarded.
        ///
        /// Deliberately not the Recenter button beside them, and not any participant control:
        /// this guard exists for actions that end or discard a recorded run, and adding it to
        /// ordinary controls would make the interface feel unresponsive for no safety gain.
        /// </summary>
        public Button[] SessionControlButtons()
        {
            return new[] { m_NewTrialButton, m_RestartButton, m_EndButton };
        }

        /// <summary>
        /// Attaches the appearance guard to each session-control button, idempotently.
        ///
        /// Called from Awake as well as from the builder because the two cover different cases:
        /// the builder path serializes the component into a newly built scene, while the Awake
        /// path fits a scene that was built before this guard existed. Attach() returns any
        /// existing component rather than adding a second, so running both is harmless.
        /// </summary>
        public void EnsureSessionControlLocks()
        {
            foreach (var button in SessionControlButtons())
            {
                if (button != null)
                    SessionControlLock.Attach(button, SessionControlLock.DefaultLockSeconds);
            }
        }
    }
}
