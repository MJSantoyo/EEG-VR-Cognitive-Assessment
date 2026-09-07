using TMPro;
using UnityEngine;

namespace IkeaEeg.Localization
{
    /// <summary>
    /// Binds one TMP label to one localization key.
    ///
    /// Static labels — button captions, headings, the controller callouts — carry this and need
    /// no code at all: they set themselves on enable and re-read whenever the language changes.
    /// Dynamic text (a target specification, a status line) is still written by the
    /// ExperimentManager, which asks the same service for the same keys.
    ///
    /// This is what makes the language switch total: the participant cannot end up looking at a
    /// screen where the body text changed and the buttons did not.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(TMP_Text))]
    public class LocalizedText : MonoBehaviour
    {
        [Tooltip("Key into the localization table, e.g. START_EXPERIMENT.")]
        [SerializeField] string m_Key = string.Empty;

        [Tooltip("Optional wrapper applied around the localized string, where {0} is the text. " +
                 "Used for button captions that carry a smaller explanatory second line.")]
        [SerializeField] string m_Format = string.Empty;

        [Tooltip("Second key appended on its own line at a smaller size. For the results " +
                 "buttons, whose captions are an action plus an explanation.")]
        [SerializeField] string m_SubtitleKey = string.Empty;

        TMP_Text m_Label;

        public string key => m_Key;

        void Awake()
        {
            m_Label = GetComponent<TMP_Text>();
        }

        void OnEnable()
        {
            ExperimentLocalization.languageChanged += Refresh;
            Refresh();
        }

        void OnDisable()
        {
            ExperimentLocalization.languageChanged -= Refresh;
        }

        public void SetKey(string key, string subtitleKey = "", string format = "")
        {
            m_Key = key;
            m_SubtitleKey = subtitleKey;
            m_Format = format;
            Refresh();
        }

        /// <summary>Re-reads the string for the language now in force.</summary>
        public void Refresh()
        {
            if (m_Label == null)
                m_Label = GetComponent<TMP_Text>();

            if (m_Label == null || string.IsNullOrEmpty(m_Key))
                return;

            var text = ExperimentLocalization.Get(m_Key);

            if (!string.IsNullOrEmpty(m_SubtitleKey))
            {
                text += "\n<size=55%>" + ExperimentLocalization.Get(m_SubtitleKey) + "</size>";
            }

            if (!string.IsNullOrEmpty(m_Format))
                text = string.Format(m_Format, text);

            m_Label.text = text;
        }
    }
}
