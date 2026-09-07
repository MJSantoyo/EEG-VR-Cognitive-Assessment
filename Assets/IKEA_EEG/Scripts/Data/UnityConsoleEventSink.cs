using System.Collections.Generic;
using UnityEngine;
using IkeaEeg.Core;

namespace IkeaEeg.Data
{
    /// <summary>
    /// Mirrors experimental events to the Unity Console for live debugging.
    ///
    /// The CSV is the record of truth; this exists so the researcher can watch the flow
    /// while the participant is in the headset. High-frequency/diagnostic events can be
    /// muted so the Console stays readable.
    /// </summary>
    [DisallowMultipleComponent]
    public class UnityConsoleEventSink : MonoBehaviour, IEventSink
    {
        [Tooltip("Log every event. When off, only the events in the important list are printed.")]
        [SerializeField] bool m_LogAllEvents = true;

        [Tooltip("Event types that are always printed, even when 'Log All Events' is off.")]
        [SerializeField] List<string> m_AlwaysLog = new List<string>
        {
            EventTypes.SessionStart,
            EventTypes.SessionEnd,
            EventTypes.TrialStart,
            EventTypes.TrialEnd,
            EventTypes.WordPresented,
            EventTypes.ImmediateRecallBeep,
            EventTypes.DelayedRecallBeep,
            EventTypes.ChairSelected,
            EventTypes.ChairCorrect,
            EventTypes.ChairIncorrect,
            EventTypes.RecordingSaved,
            EventTypes.Warning,
        };

        [Tooltip("Event types that are never printed (noise reduction).")]
        [SerializeField] List<string> m_Mute = new List<string>();

        public void Initialize(SessionContext context)
        {
            Debug.Log($"[IKEA_EEG] Console sink active for session {context.sessionId}.");
        }

        public void OnEvent(ExperimentEvent evt)
        {
            if (m_Mute.Contains(evt.eventType))
                return;

            if (!m_LogAllEvents && !m_AlwaysLog.Contains(evt.eventType))
                return;

            if (evt.eventType == EventTypes.Warning)
                Debug.LogWarning(evt.ToConsoleString());
            else
                Debug.Log(evt.ToConsoleString());
        }

        public void Shutdown()
        {
        }
    }
}
