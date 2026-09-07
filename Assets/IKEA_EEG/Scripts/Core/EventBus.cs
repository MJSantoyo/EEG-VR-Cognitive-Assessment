using System;
using System.Collections.Generic;
using UnityEngine;

namespace IkeaEeg.Core
{
    /// <summary>
    /// Fan-out dispatcher: one event in, N sinks out.
    ///
    /// Deliberately dumb and synchronous — an event must reach every sink in the same frame
    /// it was produced, otherwise the timestamps we later align to EEG become meaningless.
    /// A sink that throws is logged and skipped so one broken sink can never abort a trial.
    /// </summary>
    public class EventBus
    {
        readonly List<IEventSink> m_Sinks = new List<IEventSink>();
        SessionContext m_Context;
        bool m_Initialized;

        public IReadOnlyList<IEventSink> sinks => m_Sinks;

        /// <summary>Raised after the event reached all sinks. For in-scene UI/debug listeners.</summary>
        public event Action<ExperimentEvent> eventPublished;

        public void Register(IEventSink sink)
        {
            if (sink == null || m_Sinks.Contains(sink))
                return;

            m_Sinks.Add(sink);

            // Late registration is allowed; catch the sink up on the session context.
            if (m_Initialized)
                SafeInitialize(sink);
        }

        public void Unregister(IEventSink sink)
        {
            if (sink != null)
                m_Sinks.Remove(sink);
        }

        public void InitializeSinks(SessionContext context)
        {
            m_Context = context;
            m_Initialized = true;
            for (var i = 0; i < m_Sinks.Count; i++)
                SafeInitialize(m_Sinks[i]);
        }

        public void Publish(ExperimentEvent evt)
        {
            for (var i = 0; i < m_Sinks.Count; i++)
            {
                try
                {
                    m_Sinks[i].OnEvent(evt);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[IKEA_EEG] Sink {m_Sinks[i].GetType().Name} threw on " +
                                   $"'{evt.eventType}': {e}");
                }
            }

            eventPublished?.Invoke(evt);
        }

        public void ShutdownSinks()
        {
            if (!m_Initialized)
                return;

            for (var i = 0; i < m_Sinks.Count; i++)
            {
                try
                {
                    m_Sinks[i].Shutdown();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[IKEA_EEG] Sink {m_Sinks[i].GetType().Name} threw on Shutdown: {e}");
                }
            }

            m_Initialized = false;
        }

        void SafeInitialize(IEventSink sink)
        {
            try
            {
                sink.Initialize(m_Context);
            }
            catch (Exception e)
            {
                Debug.LogError($"[IKEA_EEG] Sink {sink.GetType().Name} threw on Initialize: {e}");
            }
        }
    }
}
