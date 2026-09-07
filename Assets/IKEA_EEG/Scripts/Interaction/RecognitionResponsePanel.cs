using System;
using UnityEngine;
using IkeaEeg.Experiment;

namespace IkeaEeg.Interaction
{
    /// <summary>
    /// The pair of recognition response buttons, as one thing the experiment can arm, disarm
    /// and show.
    ///
    /// WHY A PANEL RATHER THAN TWO LOOSE BUTTONS. Arming is a property of the ANSWER, not of
    /// either button: after "seen before" is pressed, "not seen before" must also stop
    /// accepting input, or a fast second press would register a contradictory answer to the
    /// same word. Holding both here makes that atomic — one call arms or disarms the pair —
    /// instead of relying on every caller remembering to touch both.
    ///
    /// It owns no protocol logic and decides nothing about correctness. It forwards the chosen
    /// answer to <see cref="ExperimentManager"/> and does no more.
    /// </summary>
    [DisallowMultipleComponent]
    public class RecognitionResponsePanel : MonoBehaviour
    {
        [Tooltip("The SEEN BEFORE button.")]
        [SerializeField] RecognitionResponseButton m_SeenBefore;

        [Tooltip("The NOT SEEN BEFORE button.")]
        [SerializeField] RecognitionResponseButton m_NotSeenBefore;

        [Tooltip("Root shown/hidden with the panel. This object when left empty.")]
        [SerializeField] GameObject m_Root;

        [Tooltip("Where the panel sits during the IMMEDIATE phase (Area A).")]
        [SerializeField] Transform m_AreaAAnchor;

        [Tooltip("Where the panel sits during the DELAYED phase (Area C).")]
        [SerializeField] Transform m_AreaCAnchor;

        /// <summary>Raised once per item with the chosen answer.</summary>
        public event Action<RecognitionResponse> responseSelected;

        public RecognitionResponseButton seenBeforeButton => m_SeenBefore;
        public RecognitionResponseButton notSeenBeforeButton => m_NotSeenBefore;

        /// <summary>True while either button would accept a press.</summary>
        public bool isArmed =>
            (m_SeenBefore != null && m_SeenBefore.isArmed) ||
            (m_NotSeenBefore != null && m_NotSeenBefore.isArmed);

        void Awake()
        {
            if (m_Root == null)
                m_Root = gameObject;

            Disarm();
        }

        void OnEnable()
        {
            if (m_SeenBefore != null)
                m_SeenBefore.responseSelected += OnButton;

            if (m_NotSeenBefore != null)
                m_NotSeenBefore.responseSelected += OnButton;
        }

        void OnDisable()
        {
            if (m_SeenBefore != null)
                m_SeenBefore.responseSelected -= OnButton;

            if (m_NotSeenBefore != null)
                m_NotSeenBefore.responseSelected -= OnButton;
        }

        public void Show(bool visible)
        {
            if (m_Root != null)
                m_Root.SetActive(visible);

            if (!visible)
                Disarm();
        }

        /// <summary>
        /// Moves the panel to the anchor for a phase, then shows it.
        ///
        /// ONE panel that relocates, rather than one panel per area. Two panels would mean two
        /// sets of buttons alive at once, and <c>FindAnyObjectByType</c> would bind the manager
        /// to an arbitrary one of them — the participant could then answer on a panel nobody was
        /// listening to. A single instance makes that impossible.
        ///
        /// A missing anchor leaves the panel where it is and still shows it: being in the wrong
        /// place is recoverable, being invisible mid-protocol is not.
        /// </summary>
        public void ShowForPhase(RecognitionPhase phase)
        {
            var anchor = phase == RecognitionPhase.Immediate ? m_AreaAAnchor : m_AreaCAnchor;

            if (anchor != null)
            {
                transform.SetPositionAndRotation(anchor.position, anchor.rotation);
            }
            else
            {
                Debug.LogWarning($"[IKEA_EEG] No {phase} anchor on the recognition panel; " +
                                 "showing it at its current position.");
            }

            Show(true);
        }

        public void Arm()
        {
            m_SeenBefore?.Arm();
            m_NotSeenBefore?.Arm();
        }

        public void Disarm()
        {
            m_SeenBefore?.Disarm();
            m_NotSeenBefore?.Disarm();
        }

        void OnButton(RecognitionResponse response)
        {
            // Disarm the PAIR the instant either fires, so the other cannot also answer this
            // item. The pressed button has already latched itself; this closes the other one.
            Disarm();

            responseSelected?.Invoke(response);
        }

        /// <summary>Builder wiring.</summary>
        public void Configure(RecognitionResponseButton seenBefore,
            RecognitionResponseButton notSeenBefore, GameObject root,
            Transform areaAAnchor, Transform areaCAnchor)
        {
            m_SeenBefore = seenBefore;
            m_NotSeenBefore = notSeenBefore;
            m_Root = root;
            m_AreaAAnchor = areaAAnchor;
            m_AreaCAnchor = areaCAnchor;
        }
    }
}
