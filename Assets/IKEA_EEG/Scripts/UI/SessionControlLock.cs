using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace IkeaEeg.UI
{
    /// <summary>
    /// Blocks a session-control button for a short period after it appears, so a trigger already
    /// being held when a panel opens cannot fire it.
    ///
    /// THE PROBLEM THIS SOLVES. NEW TRIAL, RESTART and END sit on the results screen, which
    /// appears at the end of a run — immediately after the participant has been pressing the
    /// trigger to answer recognition items. A ray already pointing at the panel plus a trigger
    /// still held or pressed again out of habit discards or ends a session that took minutes to
    /// record. The three actions are not equally destructive, but all three are irreversible
    /// enough to be worth a guard, and RESTART discards the run outright.
    ///
    /// WHY A TIMED LOCK RATHER THAN A CONFIRMATION DIALOG. A confirmation step adds a second
    /// screen to a flow the researcher runs many times a day, and the accidental press is a
    /// timing accident, not a decision error — the person did not mean to press anything yet. A
    /// few seconds of unresponsiveness removes the accident without adding a step.
    ///
    /// WHAT IT DOES NOT TOUCH. This is a UI guard and nothing else. It does not gate participant
    /// stimuli, does not delay any experimental event, does not participate in Recognition
    /// scoring, EEG recording, LSL markers or session logic, and adds no timer that anything else
    /// waits on. Everything it does is confined to one Button's <c>interactable</c> flag.
    ///
    /// WHY <c>interactable</c>. The XR ray drives these buttons through XRUIInputModule and
    /// TrackedDeviceGraphicRaycaster, which both route through Selectable — so an uninteractable
    /// Button rejects an XR selection by the same path as a mouse click, and gets the disabled
    /// tint at the same time. Intercepting the click instead would leave the button looking live
    /// while silently swallowing presses, which teaches the user it is broken.
    ///
    /// THE LOCK RESETS ON EVERY APPEARANCE, because it keys off OnEnable — whether the button
    /// itself is toggled or its whole panel is. Re-entering the results screen re-arms it.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Button))]
    public class SessionControlLock : MonoBehaviour
    {
        /// <summary>
        /// The project default, in seconds.
        ///
        /// An interaction-safety value, not an experimental parameter: it is long enough to
        /// outlast a reflexive second press and short enough not to feel broken. Nothing
        /// measured by the experiment depends on it.
        /// </summary>
        public const float DefaultLockSeconds = 3.0f;

        [Tooltip("How long this button ignores input after it appears, in seconds. A UI safety " +
                 "guard against an accidental press carried over from the previous screen — it " +
                 "is not an experimental parameter and nothing measured depends on it.")]
        [SerializeField] float m_LockSeconds = DefaultLockSeconds;

        [Tooltip("Tint applied while locked. Multiplied over the button's own colour, so the " +
                 "control reads as present but not yet available.")]
        [SerializeField] Color m_LockedTint = new Color(0.45f, 0.45f, 0.45f, 0.6f);

        Button m_Button;
        float m_UnlockAt;
        bool m_Locked;

        /// <summary>How long the guard lasts. Clamped at zero — a negative lock is no lock.</summary>
        public float lockSeconds
        {
            get => Mathf.Max(0f, m_LockSeconds);
            set => m_LockSeconds = Mathf.Max(0f, value);
        }

        /// <summary>True while the button is refusing input.</summary>
        public bool isLocked => m_Locked;

        /// <summary>Seconds still to wait, 0 once unlocked.</summary>
        public float remainingSeconds =>
            m_Locked ? Mathf.Max(0f, m_UnlockAt - Time.unscaledTime) : 0f;

        void Awake()
        {
            m_Button = GetComponent<Button>();

            if (m_Button == null)
                return;

            // A visible disabled state, set here rather than in the scene builder so a scene
            // that was built before this component existed still shows the guard.
            var colors = m_Button.colors;
            colors.disabledColor = m_LockedTint;
            m_Button.colors = colors;
        }

        void OnEnable()
        {
            Engage();
        }

        void OnDisable()
        {
            // Left interactable so that a button re-enabled by some other path is never stuck
            // off. OnEnable re-engages the lock immediately, so this cannot leak an unlocked
            // frame on the way back in.
            if (m_Button != null)
                m_Button.interactable = true;

            m_Locked = false;
        }

        /// <summary>
        /// Starts the lock. Called on every enable, and safe to call again at any time — a
        /// researcher-facing panel that re-shows itself re-arms rather than accumulating state.
        /// </summary>
        public void Engage()
        {
            if (m_Button == null)
                m_Button = GetComponent<Button>();

            if (m_Button == null)
                return;

            if (lockSeconds <= 0f)
            {
                m_Locked = false;
                m_Button.interactable = true;
                return;
            }

            // UNSCALED time: this is a wall-clock guard for a human hand. Scaled time would let
            // a timeScale change shorten or lengthen it, and the experiment must never be able
            // to influence a safety interlock.
            m_UnlockAt = Time.unscaledTime + lockSeconds;
            m_Locked = true;
            m_Button.interactable = false;
        }

        /// <summary>Ends the lock early. For tests and diagnostics; nothing in the run calls it.</summary>
        public void Release()
        {
            m_Locked = false;

            if (m_Button != null)
                m_Button.interactable = true;
        }

        void Update()
        {
            if (!m_Locked)
                return;

            if (Time.unscaledTime < m_UnlockAt)
                return;

            m_Locked = false;

            if (m_Button != null)
                m_Button.interactable = true;
        }

        /// <summary>
        /// Adds the guard to a button if it does not already have one, and returns it.
        ///
        /// Idempotent, so the UI controller can call it on every start-up without stacking
        /// components, and so a scene built with the guard already serialized is left alone
        /// rather than gaining a second copy.
        /// </summary>
        public static SessionControlLock Attach(Button button,
            float seconds = DefaultLockSeconds)
        {
            if (button == null)
                return null;

            var existing = button.GetComponent<SessionControlLock>();

            if (existing != null)
                return existing;

            var lockComponent = button.gameObject.AddComponent<SessionControlLock>();
            lockComponent.m_LockSeconds = Mathf.Max(0f, seconds);
            return lockComponent;
        }

        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0}: {1} ({2:F1} s guard, {3:F1} s remaining)",
                name, m_Locked ? "LOCKED" : "interactable", lockSeconds, remainingSeconds);
        }
    }
}
