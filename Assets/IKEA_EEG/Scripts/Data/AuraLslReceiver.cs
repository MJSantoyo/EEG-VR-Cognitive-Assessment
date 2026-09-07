using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace IkeaEeg.Data
{
    /// <summary>One raw EEG sample: when it was taken, and what every channel read.</summary>
    public struct RawEegSample
    {
        /// <summary>
        /// THE AUTHORITATIVE TIMESTAMP for aligning this sample with experiment events:
        /// the sample's capture time expressed in THIS machine's LSL clock domain.
        ///
        /// Computed as remote + timeCorrection, which is what the installed liblsl documents:
        /// pull_sample returns the capture time on the REMOTE machine, and time_correction()
        /// returns the value to ADD to map it into the local domain.
        ///
        /// This is the value the ring buffer indexes on, that TryGetWindow compares against,
        /// and that shares a clock with the lsl_timestamp column of the event CSV. The two
        /// machines' raw clocks differed by roughly 300,000 seconds, so using the remote value
        /// here put every event outside every window.
        /// </summary>
        public double lslTimestamp;

        /// <summary>
        /// The UNCORRECTED capture time as the sender reported it, in AURA's own clock domain.
        ///
        /// Kept because it is the only record of what the amplifier actually said. The
        /// correction is an estimate that can be re-derived or refined offline, and discarding
        /// the raw value would make that impossible. It must NEVER be used for alignment.
        /// </summary>
        public double remoteLslTimestamp;

        /// <summary>The offset added to the remote timestamp to produce <see cref="lslTimestamp"/>.</summary>
        public double timeCorrection;

        /// <summary>
        /// THE ANALYSIS TIME BASE: the de-jittered timestamp used for event-aligned epochs.
        ///
        /// Derived from lslTimestamp by fitting a straight line to (sample index, timestamp),
        /// which recovers the uniform sample clock underneath AURA's jittered chunk anchors.
        /// It is what the ring buffer indexes on and what TryGetWindow compares against.
        ///
        /// The two raw values above are kept untouched beside it: this ADDS a time base, it
        /// does not replace or relabel anything. No EEG amplitude is affected.
        /// </summary>
        public double analysisTimestamp;

        /// <summary>
        /// One value per channel, in the stream's own channel order, length == channelCount.
        ///
        /// Deliberately unlabelled. Electrode names are NOT invented here: unless AURA publishes
        /// them in the stream description, "channel 3" is all that is honestly known, and
        /// guessing a 10-20 label would attach a location to a signal that may not have it.
        /// </summary>
        public double[] channels;

        public string Describe()
        {
            if (channels == null)
                return "no channels";

            var parts = new string[channels.Length];
            for (var i = 0; i < channels.Length; i++)
            {
                parts[i] = $"CH{i + 1}={channels[i].ToString("F3", CultureInfo.InvariantCulture)}";
            }

            return $"lsl_time={lslTimestamp.ToString("F6", CultureInfo.InvariantCulture)}  " +
                   string.Join("  ", parts);
        }
    }

    /// <summary>Where the receiver currently stands. Never optimistic.</summary>
    public enum AuraReceiverState
    {
        /// <summary>Nothing attempted yet.</summary>
        Idle,

        /// <summary>No stream named AURA was found on the network.</summary>
        NotFound,

        /// <summary>The stream was found but an inlet could not be opened on it.</summary>
        InletFailed,

        /// <summary>The stream's sample format cannot be received as numbers.</summary>
        UnsupportedFormat,

        /// <summary>An inlet is open. No sample has arrived yet.</summary>
        Connected,

        /// <summary>An inlet is open and real samples are arriving.</summary>
        Receiving,
    }

    /// <summary>
    /// The FIRST raw-EEG receiver: it finds the AURA stream, reads what that stream says about
    /// itself, opens an inlet and pulls real numeric samples.
    ///
    /// WHAT IT DELIBERATELY IS NOT — this is a diagnostic and a foundation, nothing more:
    ///   * no filtering, no re-referencing, no artefact rejection;
    ///   * no FFT, no PSD, no band power, no theta/alpha;
    ///   * no neuroadaptive logic and no connection to the experiment's state machine — it
    ///     reads nothing from the ExperimentManager and tells it nothing;
    ///   * no electrode labels, no montage, no units conversion.
    ///
    /// WHAT IT IS BUILT TO BECOME: every sample leaves here as a <see cref="RawEegSample"/> —
    /// an LSL timestamp plus one value per channel — which is exactly the shape a raw EEG
    /// buffer, and then preprocessing and feature extraction, will consume. Adding those later
    /// means subscribing to <see cref="sampleReceived"/> or draining
    /// <see cref="recentSamples"/>; it does not mean changing this class.
    ///
    /// NOTHING IS ASSUMED ABOUT THE AMPLIFIER. Channel count, sampling rate and sample format
    /// are read from the live stream. There is no default of 8 channels, no default of 250 Hz
    /// and no default of float32 anywhere in this file — a wrong guess would mis-shape every
    /// buffer downstream and the error would look like data.
    /// </summary>
    [DisallowMultipleComponent]
    public class AuraLslReceiver : MonoBehaviour
    {
        /// <summary>
        /// The stream to receive. EXACTLY "AURA" — the raw stream.
        ///
        /// AURA also publishes derived streams (filtered signals, power spectra). Those are
        /// somebody else's processing decisions applied to our data; this project must start
        /// from the raw signal so its own preprocessing is known and reportable.
        /// </summary>
        public const string StreamName = "AURA";

        [Header("Discovery")]
        [Tooltip("How long to search the network for the AURA stream. FINITE by design: an " +
                 "indefinite resolve would hang Unity whenever AURA is not running.")]
        [Range(0.5f, 15f)]
        [SerializeField] float m_ResolveTimeoutSeconds = 3f;

        [Header("Reception")]
        [Tooltip("Poll for samples every frame once connected. Non-blocking: each poll uses a " +
                 "zero timeout, so a quiet stream costs a frame nothing.")]
        [SerializeField] bool m_ReceiveContinuously;

        [Tooltip("Upper bound on samples drained per frame, so a backlog can never stall one. " +
                 "At 250 Hz and 90 fps roughly 3 samples arrive per frame.")]
        [Range(1, 512)]
        [SerializeField] int m_MaxSamplesPerFrame = 64;

        [Tooltip("How many of the most recent samples to keep for inspection. This is a " +
                 "DIAGNOSTIC ring, separate from the analysis buffer below.")]
        [Range(1, 1024)]
        [SerializeField] int m_RecentSampleCapacity = 64;

        [Header("Raw EEG buffer")]
        [Tooltip("Keep a continuous rolling history of raw EEG, so a window can be cut around " +
                 "an experiment event after that event has happened.")]
        [SerializeField] bool m_BufferSamples = true;

        [Tooltip("How much history to keep, in SECONDS. The sample capacity is derived from " +
                 "this and the rate the stream advertises — never from an assumed rate.")]
        [Range(1f, 600f)]
        [SerializeField] float m_BufferSeconds = 60f;

        [Header("Clock synchronization")]
        [Tooltip("How often to refresh liblsl's clock-offset estimate, in seconds. The first " +
                 "call establishes it; later calls are served from a background update and are " +
                 "documented as instantaneous, so this is cheap.")]
        [Range(1f, 120f)]
        [SerializeField] float m_TimeCorrectionRefreshSeconds = 5f;

        object m_StreamInfo;
        object m_Inlet;
        Type m_ElementType;
        double[] m_ScratchSample;

        double m_TimeCorrection;
        bool m_HasTimeCorrection;

        EegAnalysisTimebase m_Timebase;
        double m_InitialTimeCorrection;
        double m_LargestCorrectionChange;
        int m_CorrectionUpdates;
        float m_NextCorrectionRefresh;

        readonly Queue<RawEegSample> m_Recent = new Queue<RawEegSample>();

        /// <summary>Raised for every sample received. The hook a future EEG buffer subscribes to.</summary>
        public event Action<RawEegSample> sampleReceived;

        public AuraReceiverState state { get; private set; } = AuraReceiverState.Idle;

        /// <summary>What the stream advertised. Only meaningful once connected.</summary>
        public LslBinding.StreamMetadata metadata { get; private set; }

        /// <summary>Total samples actually received. Never incremented speculatively.</summary>
        public long samplesReceived { get; private set; }

        /// <summary>The most recent sample, or default before any arrives.</summary>
        public RawEegSample lastSample { get; private set; }

        /// <summary>Human-readable reason for the current state.</summary>
        public string detail { get; private set; } = "not started";

        /// <summary>
        /// The offset currently being added to every remote timestamp, in seconds.
        ///
        /// From liblsl's own time_correction(); never derived by comparing sample values to a
        /// local clock reading, which would fold transport latency into the estimate.
        /// </summary>
        public double timeCorrection => m_TimeCorrection;

        /// <summary>The analysis time base, or null for an irregular stream. Diagnostics only.</summary>
        public EegAnalysisTimebase analysisTimebase => m_Timebase;

        /// <summary>True once a clock-offset estimate has been obtained for this connection.</summary>
        public bool hasTimeCorrection => m_HasTimeCorrection;

        /// <summary>The first estimate obtained on this connection, for the report.</summary>
        public double initialTimeCorrection => m_InitialTimeCorrection;

        /// <summary>
        /// The largest change between consecutive estimates on this connection.
        ///
        /// Worth watching: the correction is added to every sample, so a jump in it would
        /// appear as a jump in the corrected timestamps and could itself create an apparent gap.
        /// </summary>
        public double largestCorrectionChange => m_LargestCorrectionChange;

        public int correctionUpdates => m_CorrectionUpdates;

        /// <summary>True only while an inlet is open on a real, resolved AURA stream.</summary>
        public bool isConnected =>
            m_Inlet != null &&
            (state == AuraReceiverState.Connected || state == AuraReceiverState.Receiving);

        /// <summary>The recent-sample ring, oldest first. Diagnostic only.</summary>
        public IReadOnlyCollection<RawEegSample> recentSamples => m_Recent;

        /// <summary>
        /// The continuous raw EEG history, or null until a connection has been made.
        ///
        /// Sized from the stream's OWN channel count and rate at connection time. This is what
        /// a window is cut from; it is the same buffer for the whole connection, so nothing
        /// downstream has to track reconnections.
        /// </summary>
        public RawEegRingBuffer buffer { get; private set; }

        /// <summary>
        /// The per-run raw EEG file writer, when one is attached.
        ///
        /// Set by whoever owns the run lifecycle; the receiver only feeds it. Keeping the
        /// attachment external means the receiver has no opinion about runs, sessions or
        /// folders — it receives samples and hands them on.
        /// </summary>
        public RawEegRecorder recorder { get; set; }

        // ---------------------------------------------------------------------------------
        // Connection
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Finds AURA, reads its metadata and opens an inlet.
        ///
        /// Every failure is a distinct, reportable state — the researcher needs to know whether
        /// AURA is off, whether the network dropped it, or whether it is publishing a format we
        /// cannot read as numbers. Returns false without ever leaving a half-open inlet behind.
        /// </summary>
        public bool Connect(out string problem)
        {
            Disconnect();

            if (!LslBinding.CanReceiveStreams)
            {
                state = AuraReceiverState.NotFound;
                detail = LslBinding.isAvailable
                    ? "the installed LSL binding exposes no inlet/resolver API"
                    : LslBinding.detail;

                problem = detail;
                return false;
            }

            // FINITE timeout. This call does block for up to that long, which is acceptable for
            // a deliberate connect; it is never FOREVER.
            m_StreamInfo = LslBinding.ResolveStreamInfo(StreamName, m_ResolveTimeoutSeconds,
                out var resolveDetail);

            if (m_StreamInfo == null)
            {
                state = AuraReceiverState.NotFound;
                detail = resolveDetail;
                problem = $"AURA_NOT_FOUND — {resolveDetail}";
                return false;
            }

            if (!LslBinding.TryReadStreamInfo(m_StreamInfo, out var meta, out var metaDetail))
            {
                state = AuraReceiverState.InletFailed;
                detail = metaDetail;
                problem = $"AURA_FOUND_BUT_INLET_FAILED — could not read its metadata: {metaDetail}";
                return false;
            }

            metadata = meta;

            // The element type comes from the format the STREAM declares.
            m_ElementType = LslBinding.ChannelElementType(meta.channelFormatValue);

            if (m_ElementType == null)
            {
                state = AuraReceiverState.UnsupportedFormat;
                detail = $"channel format {meta.channelFormat} cannot be received as numbers";
                problem = $"UNSUPPORTED_CHANNEL_FORMAT — {detail}";
                return false;
            }

            m_Inlet = LslBinding.CreateInlet(m_StreamInfo, out var inletDetail);

            if (m_Inlet == null)
            {
                state = AuraReceiverState.InletFailed;
                detail = inletDetail;
                problem = $"AURA_FOUND_BUT_INLET_FAILED — {inletDetail}";
                return false;
            }

            // Sized from what the stream reported, never from a constant.
            m_ScratchSample = new double[meta.channelCount];

            // The clock offset, BEFORE any sample is accepted. Without it every timestamp would
            // be in the sender's clock domain and no event could ever fall inside a window.
            RefreshTimeCorrection(initial: true);

            // The rolling history, likewise sized from the stream: capacity in SECONDS becomes
            // a sample count using the rate AURA advertises. An irregular-rate stream falls back
            // to a fixed sample count inside the buffer, because there is no rate to multiply.
            // The analysis time base, built from the rate the STREAM advertises. Only
            // meaningful for a regularly-sampled stream; an irregular one keeps its own
            // timestamps rather than being forced onto a uniform grid it does not have.
            m_Timebase = meta.hasRegularRate
                ? new EegAnalysisTimebase(meta.nominalSrate)
                : null;

            if (m_Timebase == null)
            {
                Debug.LogWarning("[IKEA_EEG] The stream advertises an irregular rate, so no "
                                 + "uniform analysis time base is built. Windows will be cut on "
                                 + "the raw corrected timestamps.");
            }

            if (m_BufferSamples)
            {
                buffer = new RawEegRingBuffer(meta.channelCount, meta.nominalSrate,
                    m_BufferSeconds);

                Debug.Log($"[IKEA_EEG] Raw EEG buffer: {buffer.capacitySamples} samples " +
                          $"({m_BufferSeconds:F0} s x {meta.nominalSrate:F0} Hz) x " +
                          $"{meta.channelCount} channels, all derived from the stream's own " +
                          $"metadata. Gap tolerance {buffer.gapToleranceSeconds * 1000d:F1} ms.");
            }

            m_Recent.Clear();
            samplesReceived = 0;
            state = AuraReceiverState.Connected;
            detail = $"inlet open on {meta}";

            problem = string.Empty;
            return true;
        }

        public void Disconnect()
        {
            if (m_Inlet != null)
            {
                LslBinding.Dispose(m_Inlet);
                m_Inlet = null;
            }

            if (m_StreamInfo != null)
            {
                LslBinding.Dispose(m_StreamInfo);
                m_StreamInfo = null;
            }

            m_ElementType = null;
            m_ScratchSample = null;
        }

        void OnDisable()
        {
            Disconnect();
            state = AuraReceiverState.Idle;
            detail = "receiver disabled";
        }

        // ---------------------------------------------------------------------------------
        // Reception
        // ---------------------------------------------------------------------------------

        void Update()
        {
            if (m_ReceiveContinuously && isConnected)
                Drain(m_MaxSamplesPerFrame);
        }

        /// <summary>
        /// Takes whatever samples are already waiting, up to a limit, and returns how many.
        ///
        /// FRAME-SAFE BY CONSTRUCTION: every pull uses a ZERO timeout, so it returns immediately
        /// whether or not data is there. Nothing in this class can block the main thread waiting
        /// for EEG — an amplifier that stops mid-session slows nothing down, it simply stops
        /// producing samples, and the state says so.
        /// </summary>
        public int Drain(int maxSamples)
        {
            if (!isConnected || m_ScratchSample == null)
                return 0;

            // Keep the clock offset current. Rate-limited inside, so this is a comparison
            // against a deadline on all but one call in several thousand.
            RefreshTimeCorrectionIfDue();

            var received = 0;

            for (var i = 0; i < maxSamples; i++)
            {
                if (!TryPullOne(0d, out var sample, out var error))
                {
                    if (!string.IsNullOrEmpty(error))
                    {
                        detail = error;
                        Debug.LogWarning($"[IKEA_EEG] AURA sample pull failed: {error}");
                    }

                    break;
                }

                Accept(sample);
                received++;
            }

            return received;
        }

        /// <summary>
        /// Waits up to <paramref name="timeoutSeconds"/> for ONE sample.
        ///
        /// For the researcher diagnostic only, and only with a small timeout: this blocks the
        /// calling thread for that long. The per-frame path uses <see cref="Drain"/>, which
        /// never waits at all.
        /// </summary>
        public bool TryReceiveOne(double timeoutSeconds, out RawEegSample sample, out string error)
        {
            sample = default;

            if (!isConnected || m_ScratchSample == null)
            {
                error = "not connected";
                return false;
            }

            RefreshTimeCorrectionIfDue();

            if (!TryPullOne(Math.Max(0d, timeoutSeconds), out sample, out error))
                return false;

            Accept(sample);
            return true;
        }

        /// <summary>
        /// Re-reads liblsl's clock-offset estimate.
        ///
        /// LIFECYCLE: once when the inlet connects (with a real timeout, because the first
        /// estimate takes a few milliseconds to establish), then at most once every
        /// <see cref="m_TimeCorrectionRefreshSeconds"/> with a short timeout. It is NEVER called
        /// per sample: at 250 Hz that would put a round-trip estimate on every sample for a
        /// value that changes by microseconds.
        ///
        /// A failed refresh keeps the previous estimate rather than reverting to zero — a stale
        /// offset that is a few hundred microseconds out is vastly better than none at all,
        /// which would throw every timestamp back into the sender's clock domain.
        /// </summary>
        void RefreshTimeCorrection(bool initial)
        {
            var timeout = initial ? 2.0 : 0.2;

            if (!LslBinding.TryGetTimeCorrection(m_Inlet, timeout, out var correction,
                    out var error))
            {
                if (initial)
                {
                    Debug.LogWarning($"[IKEA_EEG] No LSL clock correction yet ({error}). EEG " +
                                     "timestamps stay in the SENDER's clock domain until an " +
                                     "estimate arrives, and cannot be aligned with events.");
                }

                return;
            }

            // THE BUG THIS BLOCK EXISTS FOR (measured, session S_20260902_131219_r01_27eda8).
            //
            // Every sample's local timestamp is remote + m_TimeCorrection. When the FIRST
            // correction attempt fails — it has a 2 s timeout and the sender may not answer that
            // fast — m_TimeCorrection stays 0, so localRaw == remoteTimestamp and every sample
            // pulled in that period is stamped in the SENDER's clock domain. Here that was
            // 1.24e6 s away from this machine's clock.
            //
            // Those samples were fed to the analysis time base, which anchors its grid on the
            // first value it sees and can only steer by a fraction of a sample interval. When
            // the correction finally arrived the raw local timestamps snapped into the right
            // domain, the grid did not, and it could not: it saturated its own steering clamp
            // and free-ran at 416.7 Hz for the whole recording while looking flawless.
            //
            // THE FIX IS TO TELL THE TIME BASE THE DOMAIN MOVED. A correction that changes by
            // more than the grid can steer is not a refinement, it is a different clock — the
            // grid must be re-anchored rather than asked to walk there.
            var domainChanged = false;

            if (m_Timebase != null)
            {
                var jump = m_HasTimeCorrection
                    ? Math.Abs(correction - m_TimeCorrection)
                    : Math.Abs(correction);

                // Only meaningful once samples have actually been through the grid: before that
                // there is no anchor to invalidate.
                domainChanged = m_Timebase.samplesSeen > 0 &&
                                jump > m_Timebase.reanchorThresholdSeconds;
            }

            if (m_HasTimeCorrection)
            {
                var change = Math.Abs(correction - m_TimeCorrection);

                if (change > m_LargestCorrectionChange)
                    m_LargestCorrectionChange = change;
            }
            else
            {
                m_InitialTimeCorrection = correction;

                Debug.Log($"[IKEA_EEG] LSL clock correction: {correction:F6} s. Remote AURA " +
                          "timestamps are mapped into this machine's LSL clock as " +
                          "local = remote + correction, which is what the installed liblsl " +
                          "documents. Events and EEG now share one clock.");
            }

            m_TimeCorrection = correction;
            m_HasTimeCorrection = true;
            m_CorrectionUpdates++;
            m_NextCorrectionRefresh = Time.realtimeSinceStartup + m_TimeCorrectionRefreshSeconds;

            // Applied AFTER the new correction is stored, so the next sample re-seeds the grid
            // with a timestamp that is already in the corrected domain.
            if (domainChanged)
            {
                m_Timebase.InvalidateAnchor("time_correction moved the clock domain");
                m_TimebaseDomainResets++;

                Debug.LogWarning(
                    "[IKEA_EEG] LSL clock correction moved by more than the analysis grid can " +
                    $"steer (now {correction:F6} s). The analysis time base has been " +
                    "RE-ANCHORED so it stays in this machine's clock domain. Samples pulled " +
                    "before this point carry analysis timestamps from the previous domain and " +
                    "must not be epoched against event timestamps.");
            }
        }

        /// <summary>
        /// How many times a clock-domain change forced the analysis grid to re-anchor.
        ///
        /// Expected to be 0 on a healthy connection and 1 when the first correction arrived
        /// late. Anything more means the clock relationship is unstable and the recording's
        /// analysis timeline has that many discontinuities in it.
        /// </summary>
        public int timebaseDomainResets => m_TimebaseDomainResets;

        int m_TimebaseDomainResets;

        /// <summary>Refreshes the offset if it is due. Cheap after the first call.</summary>
        void RefreshTimeCorrectionIfDue()
        {
            if (m_Inlet == null)
                return;

            if (m_HasTimeCorrection && Time.realtimeSinceStartup < m_NextCorrectionRefresh)
                return;

            RefreshTimeCorrection(initial: !m_HasTimeCorrection);
        }

        bool TryPullOne(double timeoutSeconds, out RawEegSample sample, out string error)
        {
            sample = default;

            var ok = LslBinding.TryPullNumericSample(m_Inlet, m_ElementType,
                metadata.channelCount, timeoutSeconds, m_ScratchSample,
                out var remoteTimestamp, out error);

            if (!ok)
                return false;

            // Copied out of the scratch buffer: a subscriber that keeps the sample must not find
            // its values overwritten by the next pull.
            var channels = new double[metadata.channelCount];
            Array.Copy(m_ScratchSample, channels, metadata.channelCount);

            // THE CLOCK FIX. pull_sample reports the capture time on the REMOTE machine; adding
            // the correction maps it into this machine's LSL clock — the same domain the event
            // log's lsl_timestamp column is stamped from.
            var localRaw = remoteTimestamp + m_TimeCorrection;

            // THE ANALYSIS TIME BASE. Added alongside the raw values, never over them: the
            // fitted timestamp is what epochs are cut on, while the remote and local raw values
            // survive for provenance and diagnostics. The EEG amplitudes are passed through
            // completely untouched.
            var analysis = m_Timebase != null ? m_Timebase.Add(localRaw) : localRaw;

            sample = new RawEegSample
            {
                remoteLslTimestamp = remoteTimestamp,
                timeCorrection = m_TimeCorrection,
                lslTimestamp = localRaw,
                analysisTimestamp = analysis,
                channels = channels,
            };

            return true;
        }

        void Accept(RawEegSample sample)
        {
            samplesReceived++;
            lastSample = sample;
            state = AuraReceiverState.Receiving;

            // The continuous history and the on-disk record, in that order. Both take the
            // sample as received; neither transforms it.
            //
            // Deliberately NOT logged to the Console: at a few hundred hertz that alone would
            // cost more than the acquisition.
            // The buffer is indexed on the ANALYSIS timestamp, because that is the time base
            // epochs are cut on. The recorder receives all three so the file keeps everything.
            buffer?.Add(sample.analysisTimestamp, sample.channels);
            recorder?.Write(sample.analysisTimestamp, sample.lslTimestamp,
                sample.remoteLslTimestamp, sample.timeCorrection, sample.channels);

            m_Recent.Enqueue(sample);
            while (m_Recent.Count > Mathf.Max(1, m_RecentSampleCapacity))
                m_Recent.Dequeue();

            sampleReceived?.Invoke(sample);
        }

        /// <summary>Editor/builder wiring.</summary>
        public void Configure(float resolveTimeoutSeconds, bool receiveContinuously)
        {
            m_ResolveTimeoutSeconds = resolveTimeoutSeconds;
            m_ReceiveContinuously = receiveContinuously;
        }

        /// <summary>Editor/builder wiring for the rolling buffer.</summary>
        public void ConfigureBuffer(bool bufferSamples, float bufferSeconds)
        {
            m_BufferSamples = bufferSamples;
            m_BufferSeconds = bufferSeconds;
        }

        /// <summary>
        /// Cuts a window of EEG around an event's LSL timestamp.
        ///
        /// The event timestamp must come from the SAME clock the samples carry — the
        /// lsl_timestamp column of the event CSV, stamped from liblsl's local_clock(). Passing a
        /// session-clock or Time.time value here would ask for a range that never existed.
        /// </summary>
        public bool TryGetWindow(double eventLslTimestamp, double preSeconds, double postSeconds,
            out EegWindow window)
        {
            if (buffer == null)
            {
                window = default;
                return false;
            }

            return buffer.TryGetWindow(eventLslTimestamp, preSeconds, postSeconds, out window);
        }
    }
}
