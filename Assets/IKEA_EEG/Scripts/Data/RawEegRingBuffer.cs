using System;
using System.Globalization;

namespace IkeaEeg.Data
{
    /// <summary>What a window request could actually be satisfied with.</summary>
    public enum EegWindowStatus
    {
        /// <summary>The whole requested range was in the buffer.</summary>
        Complete,

        /// <summary>The start of the range had already been overwritten or never arrived.</summary>
        MissingStart,

        /// <summary>The end of the range has not been received yet.</summary>
        MissingEnd,

        /// <summary>Both edges are missing.</summary>
        MissingBothEdges,

        /// <summary>No sample at all fell inside the range.</summary>
        Empty,
    }

    /// <summary>
    /// One extracted window of raw EEG, and an honest account of what it contains.
    ///
    /// The status and the actual timestamps travel WITH the data because "750 samples" and
    /// "750 samples covering the whole requested interval" are different claims, and an
    /// analysis that cannot tell them apart will silently average over a gap.
    /// </summary>
    public struct EegWindow
    {
        public double requestedStart;
        public double requestedEnd;
        public double eventTimestamp;

        /// <summary>Timestamp of the first and last sample actually returned.</summary>
        public double firstTimestamp;
        public double lastTimestamp;

        public int sampleCount;
        public int channelCount;
        public EegWindowStatus status;

        /// <summary>[sample][channel], chronological. Null when nothing matched.</summary>
        public double[][] samples;

        /// <summary>Timestamp of each returned sample, same order as <see cref="samples"/>.</summary>
        public double[] timestamps;

        public bool isComplete => status == EegWindowStatus.Complete;

        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "requested=[{0:F6}..{1:F6}] around event {2:F6}; " +
                "actual=[{3:F6}..{4:F6}]; samples={5}; channels={6}; status={7}",
                requestedStart, requestedEnd, eventTimestamp,
                firstTimestamp, lastTimestamp, sampleCount, channelCount, status);
        }
    }

    /// <summary>How the incoming signal has behaved. Measured, never estimated.</summary>
    public struct EegContinuityStats
    {
        public long totalSamples;
        public double firstTimestamp;
        public double latestTimestamp;

        /// <summary>Latest minus first, in seconds. 0 before two samples have arrived.</summary>
        public double observedDurationSeconds;

        /// <summary>
        /// Samples actually received divided by the time they spanned.
        ///
        /// This is what the stream DELIVERED, which is not necessarily what it advertises. A
        /// meaningful gap between the two is the first sign of dropped data.
        /// </summary>
        public double effectiveRateHz;

        /// <summary>False once any sample arrived with a timestamp at or before its predecessor.</summary>
        public bool timestampsMonotonic;

        /// <summary>Inter-sample intervals longer than the tolerance derived from the nominal rate.</summary>
        public int gapCount;

        /// <summary>The longest inter-sample interval seen, in seconds.</summary>
        public double largestGapSeconds;

        /// <summary>Timestamp at which the largest gap ended.</summary>
        public double largestGapAtTimestamp;

        /// <summary>Samples whose timestamp did not advance. Counted, never inferred.</summary>
        public int nonMonotonicCount;

        public int bufferedSamples;
        public double bufferedSeconds;
        public int capacitySamples;

        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "total={0}; buffered={1}/{2} ({3:F2} s); first={4:F6}; latest={5:F6}; " +
                "observed_duration_s={6:F3}; effective_rate_hz={7:F2}; monotonic={8}; " +
                "non_monotonic={9}; gaps={10}; largest_gap_s={11:F4}",
                totalSamples, bufferedSamples, capacitySamples, bufferedSeconds,
                firstTimestamp, latestTimestamp, observedDurationSeconds, effectiveRateHz,
                timestampsMonotonic ? "TRUE" : "FALSE", nonMonotonicCount,
                gapCount, largestGapSeconds);
        }
    }

    /// <summary>
    /// A bounded, chronological store of the most recent raw EEG.
    ///
    /// WHAT IT IS FOR: keeping enough signal around that a window can be cut around an
    /// experiment event AFTER that event has happened. Nothing can be extracted around a
    /// stimulus that was not already being recorded when it occurred, so the buffer runs
    /// continuously and the analysis asks it questions later.
    ///
    /// WHAT IT DELIBERATELY IS NOT: it does not filter, re-reference, rescale, interpolate or
    /// resample anything. Samples come out exactly as they came in. Any processing belongs
    /// downstream of here, where it can be described in a methods section.
    ///
    /// SIZING IS DERIVED, NEVER ASSUMED. Channel count and nominal rate come from the stream's
    /// own metadata; the capacity is expressed in SECONDS and converted using that rate. There
    /// is no 8 and no 250 anywhere in this file.
    ///
    /// MEMORY: one flat double[] of capacity x channels, allocated once. Writing a sample
    /// copies into it; nothing is allocated per sample, which matters at a few hundred hertz
    /// for a session lasting many minutes.
    ///
    /// THREADING: writes come from whichever thread drains the LSL inlet and reads come from
    /// the main thread or the Editor, so every public member takes the same lock. The critical
    /// sections are array copies of a few dozen doubles — short enough that a 250 Hz writer
    /// never meaningfully contends with an occasional reader.
    /// </summary>
    public class RawEegRingBuffer
    {
        readonly object m_Lock = new object();

        readonly int m_ChannelCount;
        readonly double m_NominalRateHz;
        readonly int m_Capacity;

        /// <summary>Flat [slot * channels + channel]. Allocated once, never grown.</summary>
        readonly double[] m_Samples;
        readonly double[] m_Timestamps;

        int m_Count;
        int m_Head;      // next slot to write

        long m_TotalSamples;
        double m_FirstTimestamp;
        double m_LatestTimestamp;
        bool m_Monotonic = true;
        int m_NonMonotonic;
        int m_GapCount;
        double m_LargestGap;
        double m_LargestGapAt;
        double m_GapToleranceSeconds;

        public int channelCount => m_ChannelCount;
        public double nominalRateHz => m_NominalRateHz;
        public int capacitySamples => m_Capacity;

        /// <summary>
        /// The interval above which a step between samples is called a gap.
        ///
        /// Derived from the nominal rate rather than fixed: at 250 Hz samples are 4 ms apart and
        /// at 128 Hz nearly 8, so any constant threshold would be wrong for one of them. The
        /// multiplier allows for ordinary transport jitter — LSL delivers in chunks — while
        /// still catching a genuine dropout.
        /// </summary>
        public double gapToleranceSeconds => m_GapToleranceSeconds;

        /// <param name="channelCount">From the stream metadata.</param>
        /// <param name="nominalRateHz">
        /// From the stream metadata. Zero or negative means the stream declares an irregular
        /// rate; capacity then falls back to the supplied sample count and gap detection is
        /// disabled, because there is no expected interval to compare against.
        /// </param>
        /// <param name="capacitySeconds">How much history to keep.</param>
        /// <param name="gapToleranceMultiplier">
        /// How many nominal sample intervals may elapse before a step counts as a gap.
        /// </param>
        /// <param name="fallbackCapacitySamples">Used only for an irregular-rate stream.</param>
        public RawEegRingBuffer(int channelCount, double nominalRateHz, double capacitySeconds,
            double gapToleranceMultiplier = 4.0, int fallbackCapacitySamples = 8192)
        {
            if (channelCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(channelCount),
                    "channel count must come from the stream metadata and be positive");

            m_ChannelCount = channelCount;
            m_NominalRateHz = nominalRateHz;

            m_Capacity = nominalRateHz > 0d
                ? Math.Max(1, (int)Math.Ceiling(nominalRateHz * Math.Max(0.001, capacitySeconds)))
                : Math.Max(1, fallbackCapacitySamples);

            m_Samples = new double[(long)m_Capacity * m_ChannelCount <= int.MaxValue
                ? m_Capacity * m_ChannelCount
                : throw new ArgumentOutOfRangeException(nameof(capacitySeconds),
                    "requested capacity does not fit in a single array")];

            m_Timestamps = new double[m_Capacity];

            m_GapToleranceSeconds = nominalRateHz > 0d
                ? gapToleranceMultiplier / nominalRateHz
                : double.PositiveInfinity;   // irregular rate: no expected interval, no gaps
        }

        /// <summary>
        /// Stores one sample. Overwrites the oldest when full — that is the point of a ring:
        /// memory stays flat however long the session runs.
        /// </summary>
        public void Add(double lslTimestamp, double[] channels)
        {
            if (channels == null || channels.Length < m_ChannelCount)
                return;

            lock (m_Lock)
            {
                if (m_TotalSamples == 0)
                {
                    m_FirstTimestamp = lslTimestamp;
                }
                else
                {
                    var step = lslTimestamp - m_LatestTimestamp;

                    if (step <= 0d)
                    {
                        // Out of order or duplicated. Recorded rather than dropped: the fact
                        // that it happened is itself data about the transport.
                        m_Monotonic = false;
                        m_NonMonotonic++;
                    }
                    else if (step > m_GapToleranceSeconds)
                    {
                        m_GapCount++;

                        if (step > m_LargestGap)
                        {
                            m_LargestGap = step;
                            m_LargestGapAt = lslTimestamp;
                        }
                    }
                }

                m_LatestTimestamp = lslTimestamp;
                m_TotalSamples++;

                var offset = m_Head * m_ChannelCount;
                for (var c = 0; c < m_ChannelCount; c++)
                    m_Samples[offset + c] = channels[c];

                m_Timestamps[m_Head] = lslTimestamp;

                m_Head = (m_Head + 1) % m_Capacity;

                if (m_Count < m_Capacity)
                    m_Count++;
            }
        }

        /// <summary>Index in the flat array of the i-th oldest buffered sample.</summary>
        int SlotOf(int chronologicalIndex)
        {
            // When full, the oldest sample sits at the head; before that, at slot 0.
            var start = m_Count == m_Capacity ? m_Head : 0;
            return (start + chronologicalIndex) % m_Capacity;
        }

        public EegContinuityStats GetStats()
        {
            lock (m_Lock)
            {
                var duration = m_TotalSamples > 1 ? m_LatestTimestamp - m_FirstTimestamp : 0d;

                var buffered = 0d;
                if (m_Count > 1)
                    buffered = m_Timestamps[SlotOf(m_Count - 1)] - m_Timestamps[SlotOf(0)];

                return new EegContinuityStats
                {
                    totalSamples = m_TotalSamples,
                    firstTimestamp = m_TotalSamples > 0 ? m_FirstTimestamp : 0d,
                    latestTimestamp = m_TotalSamples > 0 ? m_LatestTimestamp : 0d,
                    observedDurationSeconds = duration,

                    // Intervals, not samples: N samples span N-1 intervals.
                    effectiveRateHz = duration > 0d ? (m_TotalSamples - 1) / duration : 0d,

                    timestampsMonotonic = m_Monotonic,
                    nonMonotonicCount = m_NonMonotonic,
                    gapCount = m_GapCount,
                    largestGapSeconds = m_LargestGap,
                    largestGapAtTimestamp = m_LargestGapAt,

                    bufferedSamples = m_Count,
                    bufferedSeconds = buffered,
                    capacitySamples = m_Capacity,
                };
            }
        }

        /// <summary>The most recent sample, or false when none has arrived.</summary>
        public bool TryGetLatest(out double timestamp, out double[] channels)
        {
            lock (m_Lock)
            {
                if (m_Count == 0)
                {
                    timestamp = 0d;
                    channels = null;
                    return false;
                }

                var slot = SlotOf(m_Count - 1);
                timestamp = m_Timestamps[slot];

                channels = new double[m_ChannelCount];
                Array.Copy(m_Samples, slot * m_ChannelCount, channels, 0, m_ChannelCount);
                return true;
            }
        }

        /// <summary>
        /// Every sample in [eventTimestamp - preSeconds, eventTimestamp + postSeconds].
        ///
        /// The window is defined on the LSL CLOCK, which is the whole reason the event's LSL
        /// timestamp is recorded alongside its behavioural one: both the EEG and the event then
        /// live in one time base and no conversion is needed or possible to get wrong.
        ///
        /// Returns false only when nothing at all fell in range. A PARTIAL window still returns
        /// true, with a status saying which edge is missing — a window cut 200 ms after the
        /// event exists, it just does not yet extend as far forward as was asked for, and
        /// silently returning nothing would hide that.
        /// </summary>
        public bool TryGetWindow(double eventTimestamp, double preSeconds, double postSeconds,
            out EegWindow window)
        {
            var start = eventTimestamp - Math.Max(0d, preSeconds);
            var end = eventTimestamp + Math.Max(0d, postSeconds);

            window = new EegWindow
            {
                requestedStart = start,
                requestedEnd = end,
                eventTimestamp = eventTimestamp,
                channelCount = m_ChannelCount,
                status = EegWindowStatus.Empty,
            };

            lock (m_Lock)
            {
                if (m_Count == 0)
                    return false;

                var oldest = m_Timestamps[SlotOf(0)];
                var newest = m_Timestamps[SlotOf(m_Count - 1)];

                // Count first so the result arrays are allocated exactly once at the right size.
                var first = -1;
                var last = -1;

                for (var i = 0; i < m_Count; i++)
                {
                    var t = m_Timestamps[SlotOf(i)];

                    if (t < start)
                        continue;

                    if (t > end)
                        break;

                    if (first < 0)
                        first = i;

                    last = i;
                }

                if (first < 0)
                    return false;

                var count = last - first + 1;

                window.sampleCount = count;
                window.samples = new double[count][];
                window.timestamps = new double[count];

                for (var i = 0; i < count; i++)
                {
                    var slot = SlotOf(first + i);
                    var channels = new double[m_ChannelCount];

                    Array.Copy(m_Samples, slot * m_ChannelCount, channels, 0, m_ChannelCount);

                    window.samples[i] = channels;
                    window.timestamps[i] = m_Timestamps[slot];
                }

                window.firstTimestamp = window.timestamps[0];
                window.lastTimestamp = window.timestamps[count - 1];

                // "Missing" means the buffer cannot cover the edge, not that the returned
                // samples happen not to land exactly on it.
                var missingStart = oldest > start;
                var missingEnd = newest < end;

                window.status =
                    missingStart && missingEnd ? EegWindowStatus.MissingBothEdges
                    : missingStart ? EegWindowStatus.MissingStart
                    : missingEnd ? EegWindowStatus.MissingEnd
                    : EegWindowStatus.Complete;

                return true;
            }
        }

        /// <summary>
        /// How many samples a window of this length should contain at the ADVERTISED rate.
        ///
        /// A yardstick for diagnostics, not a promise: the real count depends on what actually
        /// arrived, which is exactly what the diagnostic is checking.
        /// </summary>
        public int ExpectedSampleCount(double preSeconds, double postSeconds)
        {
            if (m_NominalRateHz <= 0d)
                return 0;

            return (int)Math.Round((Math.Max(0d, preSeconds) + Math.Max(0d, postSeconds)) *
                                   m_NominalRateHz);
        }

        /// <summary>Empties the buffer and its statistics. Used when a new run starts.</summary>
        public void Clear()
        {
            lock (m_Lock)
            {
                m_Count = 0;
                m_Head = 0;
                m_TotalSamples = 0;
                m_FirstTimestamp = 0d;
                m_LatestTimestamp = 0d;
                m_Monotonic = true;
                m_NonMonotonic = 0;
                m_GapCount = 0;
                m_LargestGap = 0d;
                m_LargestGapAt = 0d;
            }
        }
    }
}
