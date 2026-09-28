using System.Collections.Generic;
using UnityEngine;
using IkeaEeg.Experiment;
using IkeaEeg.Interaction;
using IkeaEeg.UI;
using IkeaEeg.XR;

namespace IkeaEeg.SceneFlow
{
    /// <summary>
    /// The persistent systems a room scene is allowed to bind itself into.
    ///
    /// Passed to <see cref="AreaSceneContext.BindInto"/> when a room scene finishes loading.
    /// It is a plain carrier, not a service locator: it holds the ALREADY EXISTING singletons
    /// and creates nothing. A room scene can therefore hand its own objects to the experiment
    /// without being able to construct a second manager, a second logger or a second EEG
    /// pipeline -- there is nowhere here to put one.
    /// </summary>
    public sealed class PersistentSystems
    {
        public ExperimentManager manager;
        public ExperimentUIController ui;
        public ChairSelectionTask chairTask;
        public XRRigTeleporter teleporter;

        /// <summary>
        /// The ONE shared recognition panel. Rooms supply their anchor, never their own panel.
        /// </summary>
        public RecognitionResponsePanel recognitionPanel;

        public bool isComplete =>
            manager != null && ui != null && chairTask != null && teleporter != null;
    }

    /// <summary>
    /// ONE per room scene. The single object that knows what that room contains.
    ///
    /// WHY THIS EXISTS. Unity cannot serialize a reference that crosses a scene boundary. The
    /// persistent systems (ExperimentUIController and its 66 references, ChairSelectionTask's
    /// chairs, XRRigTeleporter's spawn points) currently point straight at objects in Area 0,
    /// A, B and C because all four live in one scene. Split the rooms apart and every one of
    /// those references is dropped on load.
    ///
    /// The fix is to invert the direction. The persistent side stops holding room references;
    /// instead each room scene carries ONE component that holds its OWN objects -- a reference
    /// wholly inside its own scene, which serializes perfectly normally -- and hands them over
    /// when it loads.
    ///
    /// WHAT IT DELIBERATELY DOES NOT DO:
    ///   * it does not create, own or configure any persistent system;
    ///   * it does not drive the experiment, the state machine or any task;
    ///   * it does not move the participant -- <see cref="AreaSceneLoader"/> does that, through
    ///     the existing XRRigTeleporter, so the teleport is logged exactly as it always was;
    ///   * it holds no state that outlives its scene. Unload the room and it goes with it.
    ///
    /// It rebinds through the SAME public Bind/Set methods the scene builder already calls, so
    /// a runtime bind and an authored bind produce byte-identical wiring. Nothing about the
    /// protocol, the logging or the science can change by going through this path.
    /// </summary>
    [DisallowMultipleComponent]
    public abstract class AreaSceneContext : MonoBehaviour
    {
        static readonly List<AreaSceneContext> s_Loaded = new List<AreaSceneContext>();

        /// <summary>Every room context currently loaded. Normally one; two only mid-transition.</summary>
        public static IReadOnlyList<AreaSceneContext> loaded => s_Loaded;

        /// <summary>Which room this is. Fixed by the concrete type, never authored.</summary>
        public abstract ExperimentArea area { get; }

        /// <summary>Where the participant stands in this room. Used by the loader to recenter.</summary>
        public abstract SpawnPoint primarySpawn { get; }

        /// <summary>
        /// Hand this room's objects to the persistent systems.
        ///
        /// Called by <see cref="AreaSceneLoader"/> AFTER the scene is fully loaded and BEFORE
        /// the participant is placed, so nothing is ever shown unbound.
        /// </summary>
        public abstract void BindInto(PersistentSystems systems);

        /// <summary>The room context for an area, or null when that room is not loaded.</summary>
        public static AreaSceneContext ForArea(ExperimentArea area)
        {
            foreach (var c in s_Loaded)
            {
                if (c != null && c.area == area)
                    return c;
            }

            return null;
        }

        protected virtual void OnEnable()
        {
            if (!s_Loaded.Contains(this))
                s_Loaded.Add(this);
        }

        protected virtual void OnDisable()
        {
            s_Loaded.Remove(this);
        }

        /// <summary>
        /// Reports any reference this room scene failed to author, by field name.
        ///
        /// A room that loads with a missing reference would fail silently and late -- a dead
        /// button, a word label that never updates -- so the loader asks for this up front and
        /// logs an error naming the field rather than letting the protocol run half-wired.
        /// </summary>
        public abstract IEnumerable<string> MissingReferences();
    }
}
