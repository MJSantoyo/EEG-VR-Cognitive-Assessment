using System;
using System.Globalization;
using System.Text;

namespace IkeaEeg.Data
{
    /// <summary>
    /// One second-order section (biquad), Direct Form I, with its own state.
    ///
    /// Direct Form I rather than a high-order direct-form polynomial: an 8th-order filter
    /// expressed as a single difference equation is numerically fragile at these cutoff ratios
    /// (1 Hz at 250 Hz sampling is a pole very close to the unit circle), and a cascade of
    /// second-order sections keeps every coefficient well conditioned.
    /// </summary>
    public class Biquad
    {
        public double b0, b1, b2, a1, a2;

        // Per-section state. One instance per channel — never shared.
        double m_X1, m_X2, m_Y1, m_Y2;

        public double Process(double x)
        {
            var y = b0 * x + b1 * m_X1 + b2 * m_X2 - a1 * m_Y1 - a2 * m_Y2;

            m_X2 = m_X1;
            m_X1 = x;
            m_Y2 = m_Y1;
            m_Y1 = y;

            return y;
        }

        public void Reset()
        {
            m_X1 = m_X2 = m_Y1 = m_Y2 = 0d;
        }

        /// <summary>Magnitude response at one frequency, for validation and reporting.</summary>
        public double MagnitudeAt(double frequencyHz, double sampleRateHz)
        {
            var w = 2.0 * Math.PI * frequencyHz / sampleRateHz;

            // Evaluate H(z) on the unit circle, z = e^{jw}.
            var cos1 = Math.Cos(-w);
            var sin1 = Math.Sin(-w);
            var cos2 = Math.Cos(-2 * w);
            var sin2 = Math.Sin(-2 * w);

            var numRe = b0 + b1 * cos1 + b2 * cos2;
            var numIm = b1 * sin1 + b2 * sin2;

            var denRe = 1.0 + a1 * cos1 + a2 * cos2;
            var denIm = a1 * sin1 + a2 * sin2;

            var numMag = Math.Sqrt(numRe * numRe + numIm * numIm);
            var denMag = Math.Sqrt(denRe * denRe + denIm * denIm);

            return denMag > 0d ? numMag / denMag : 0d;
        }

        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "b0={0:E6} b1={1:E6} b2={2:E6} a1={3:E6} a2={4:E6}", b0, b1, b2, a1, a2);
        }
    }

    /// <summary>
    /// A causal Butterworth band-pass, as a cascade of second-order sections, with independent
    /// state per EEG channel.
    ///
    /// ============================== DESIGN AND ITS LIMITS ==============================
    /// TOPOLOGY: 4th-order Butterworth high-pass (2 biquads) cascaded with a 4th-order
    /// Butterworth low-pass (2 biquads) — 8th order overall, 4 sections. Coefficients come from
    /// the bilinear transform of the analogue Butterworth prototype, with the section Q values
    /// Q_k = 1 / (2 cos(pi(2k+1)/(2N))) that place the poles on the Butterworth circle.
    ///
    /// CAUSAL, AND THEREFORE NOT PHASE-LINEAR. This runs forward only, once, because it must be
    /// usable in real time. It imposes a frequency-dependent group delay — largest near the
    /// cutoffs, a few tens of milliseconds around 1 Hz. filtfilt would remove that but is
    /// non-causal and cannot be used online, so it is deliberately absent.
    ///
    /// WHAT THAT MEANS FOR THIS PROJECT: we measure spectral POWER over windows of seconds, and
    /// power is unaffected by phase. The delay would matter for ERP latency, which this pass does
    /// not compute. Anyone later measuring a latency from this filtered signal must account for
    /// the group delay or filter differently — hence this note rather than a silent assumption.
    ///
    /// CONTINUOUS BY DESIGN: state persists across calls, so the filter is never re-initialised
    /// per epoch. Re-zeroing state at each window boundary would inject a transient into the
    /// first ~1 s of every epoch — exactly where an event of interest usually sits.
    ///
    /// FREQUENCIES ARE DERIVED FROM THE ACTUAL SAMPLE RATE passed in. There is no 250 anywhere
    /// in this file.
    /// ==================================================================================
    /// </summary>
    public class EegBandpassFilter
    {
        /// <summary>Butterworth order of each half. 4 => two biquads per half.</summary>
        public const int HalfOrder = 4;

        /// <summary>Default notch centre. Mains frequency in the Americas; 50 Hz elsewhere.</summary>
        public const double DefaultNotchHz = 60.0;

        /// <summary>
        /// Default notch quality factor. Q = centre / bandwidth, so Q = 30 at 60 Hz is a
        /// −3 dB bandwidth of 2 Hz: narrow enough to leave neighbouring EEG untouched, wide
        /// enough to catch mains that drifts by a fraction of a hertz.
        /// </summary>
        public const double DefaultNotchQ = 30.0;

        readonly int m_ChannelCount;
        readonly double m_SampleRateHz;
        readonly double m_HighPassHz;
        readonly double m_LowPassHz;
        readonly bool m_NotchEnabled;
        readonly double m_NotchHz;
        readonly double m_NotchQ;

        /// <summary>[channel][section]. Each channel has its OWN state; nothing is shared.</summary>
        readonly Biquad[][] m_Sections;

        public double sampleRateHz => m_SampleRateHz;
        public double highPassHz => m_HighPassHz;
        public double lowPassHz => m_LowPassHz;
        public int channelCount => m_ChannelCount;
        public int sectionsPerChannel => m_Sections.Length > 0 ? m_Sections[0].Length : 0;

        /// <summary>Whether the optional mains notch is in the chain. OFF unless asked for.</summary>
        public bool notchEnabled => m_NotchEnabled;

        /// <summary>Notch centre in Hz. Meaningless when <see cref="notchEnabled"/> is false.</summary>
        public double notchHz => m_NotchHz;

        public double notchQ => m_NotchQ;

        /// <summary>−3 dB bandwidth of the notch, in Hz. Derived: centre / Q.</summary>
        public double notchBandwidthHz => m_NotchQ > 0d ? m_NotchHz / m_NotchQ : 0d;

        /// <summary>Overall filter order: high-pass + low-pass, plus 2 if the notch is on.</summary>
        public int overallOrder => HalfOrder * 2 + (m_NotchEnabled ? 2 : 0);

        /// <param name="notchEnabled">
        /// OPTIONAL mains notch. Defaults to OFF, and is scientifically REDUNDANT for the
        /// current 1–40 Hz analysis: the 4th-order 40 Hz low-pass already attenuates 60 Hz by
        /// about 18 dB, and nothing in theta (4–8 Hz) or alpha (8–12 Hz) is anywhere near it.
        /// It exists because it is part of the researcher's stated preprocessing protocol and
        /// they want it available and explicit — not because the passband needs it.
        /// </param>
        public EegBandpassFilter(int channelCount, double sampleRateHz,
            double highPassHz = 1.0, double lowPassHz = 40.0,
            bool notchEnabled = false, double notchHz = DefaultNotchHz,
            double notchQ = DefaultNotchQ)
        {
            if (channelCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(channelCount));

            if (sampleRateHz <= 0d)
                throw new ArgumentOutOfRangeException(nameof(sampleRateHz),
                    "the sample rate must come from the stream metadata");

            // Nyquist is a hard limit: a cutoff at or above it has no meaning after sampling.
            var nyquist = sampleRateHz * 0.5;

            if (lowPassHz >= nyquist)
            {
                throw new ArgumentOutOfRangeException(nameof(lowPassHz),
                    $"low-pass {lowPassHz} Hz is at or above Nyquist ({nyquist} Hz) for this " +
                    "stream; choose a lower cutoff or a faster amplifier");
            }

            if (highPassHz <= 0d || highPassHz >= lowPassHz)
                throw new ArgumentOutOfRangeException(nameof(highPassHz));

            // The notch is validated only when it is actually going to be built: an absurd
            // notch frequency on a filter that has no notch is not an error worth throwing.
            if (notchEnabled)
            {
                if (notchHz <= 0d || notchHz >= nyquist)
                {
                    throw new ArgumentOutOfRangeException(nameof(notchHz),
                        $"a {notchHz} Hz notch is not representable at {sampleRateHz} Hz " +
                        $"sampling (Nyquist {nyquist} Hz). The notch frequency and the sample " +
                        "rate must both come from the real configuration, never a constant.");
                }

                if (notchQ <= 0d)
                {
                    throw new ArgumentOutOfRangeException(nameof(notchQ),
                        "notch Q must be positive; Q = centre / bandwidth");
                }
            }

            m_ChannelCount = channelCount;
            m_SampleRateHz = sampleRateHz;
            m_HighPassHz = highPassHz;
            m_LowPassHz = lowPassHz;
            m_NotchEnabled = notchEnabled;
            m_NotchHz = notchHz;
            m_NotchQ = notchQ;

            m_Sections = new Biquad[channelCount][];

            for (var c = 0; c < channelCount; c++)
            {
                m_Sections[c] = BuildSections(sampleRateHz, highPassHz, lowPassHz,
                    notchEnabled, notchHz, notchQ);
            }
        }

        /// <summary>
        /// The section Q values that put an N-th order cascade's poles on the Butterworth
        /// circle: Q_k = 1 / (2 cos(pi (2k+1) / (2N))), k = 0 .. N/2-1.
        /// </summary>
        static double[] ButterworthQ(int order)
        {
            var sections = order / 2;
            var q = new double[sections];

            for (var k = 0; k < sections; k++)
                q[k] = 1.0 / (2.0 * Math.Cos(Math.PI * (2 * k + 1) / (2.0 * order)));

            return q;
        }

        /// <summary>
        /// Builds one channel's cascade.
        ///
        /// CHAIN ORDER: high-pass sections, then low-pass sections, then the optional notch
        /// LAST. The order is deliberate. The high-pass runs first so the enormous DC offsets
        /// this amplifier carries are removed before anything else has to represent them, which
        /// keeps every later section working on small numbers. The notch runs last because it is
        /// an optional, narrow correction: putting it at the end means enabling or disabling it
        /// cannot change the conditioning or the settling behaviour of the band-pass in front
        /// of it, so the two can be reasoned about independently.
        ///
        /// For a cascade of LTI sections the overall transfer function is the product and is
        /// order-independent in exact arithmetic; the ordering above is about numerical
        /// conditioning and about being able to reason separately, not about the response.
        /// </summary>
        static Biquad[] BuildSections(double fs, double hp, double lp,
            bool notchEnabled = false, double notchHz = DefaultNotchHz,
            double notchQ = DefaultNotchQ)
        {
            var qs = ButterworthQ(HalfOrder);
            var sections = new Biquad[qs.Length * 2 + (notchEnabled ? 1 : 0)];

            for (var k = 0; k < qs.Length; k++)
            {
                sections[k] = HighPass(fs, hp, qs[k]);
                sections[qs.Length + k] = LowPass(fs, lp, qs[k]);
            }

            if (notchEnabled)
                sections[qs.Length * 2] = Notch(fs, notchHz, notchQ);

            return sections;
        }

        /// <summary>
        /// Bilinear-transform band-stop (notch) biquad, RBJ form.
        ///
        /// H(z) has its zeros exactly ON the unit circle at the notch frequency, so the
        /// rejection at the centre is total rather than merely deep, and its poles just inside
        /// at a radius set by Q, so the notch is narrow. Expressed as a single second-order
        /// section and normalised by a0, which keeps it numerically stable at any Q we would
        /// realistically use — the same reasoning as the rest of this file.
        ///
        /// CAUSAL, like every other section here: forward-only, so it is NOT phase-linear and
        /// introduces a frequency-dependent group delay concentrated around the notch. That is
        /// acceptable for spectral POWER and remains wrong for ERP latency, exactly as
        /// documented for the band-pass.
        ///
        /// fs is a PARAMETER. Nothing about mains frequency or sample rate is assumed here.
        /// </summary>
        static Biquad Notch(double fs, double f0, double q)
        {
            var w0 = 2.0 * Math.PI * f0 / fs;
            var cosW = Math.Cos(w0);
            var alpha = Math.Sin(w0) / (2.0 * q);

            var a0 = 1.0 + alpha;

            return new Biquad
            {
                // Numerator 1, -2cos(w0), 1 => zeros on the unit circle at +/- w0.
                b0 = 1.0 / a0,
                b1 = -2.0 * cosW / a0,
                b2 = 1.0 / a0,
                a1 = -2.0 * cosW / a0,
                a2 = (1.0 - alpha) / a0,
            };
        }

        /// <summary>Bilinear-transform low-pass biquad (RBJ form) at cutoff f0 with quality q.</summary>
        static Biquad LowPass(double fs, double f0, double q)
        {
            var w0 = 2.0 * Math.PI * f0 / fs;
            var cosW = Math.Cos(w0);
            var alpha = Math.Sin(w0) / (2.0 * q);

            var a0 = 1.0 + alpha;

            return new Biquad
            {
                b0 = (1.0 - cosW) / 2.0 / a0,
                b1 = (1.0 - cosW) / a0,
                b2 = (1.0 - cosW) / 2.0 / a0,
                a1 = -2.0 * cosW / a0,
                a2 = (1.0 - alpha) / a0,
            };
        }

        /// <summary>Bilinear-transform high-pass biquad (RBJ form).</summary>
        static Biquad HighPass(double fs, double f0, double q)
        {
            var w0 = 2.0 * Math.PI * f0 / fs;
            var cosW = Math.Cos(w0);
            var alpha = Math.Sin(w0) / (2.0 * q);

            var a0 = 1.0 + alpha;

            return new Biquad
            {
                b0 = (1.0 + cosW) / 2.0 / a0,
                b1 = -(1.0 + cosW) / a0,
                b2 = (1.0 + cosW) / 2.0 / a0,
                a1 = -2.0 * cosW / a0,
                a2 = (1.0 - alpha) / a0,
            };
        }

        /// <summary>
        /// Filters one sample of one channel, advancing that channel's state.
        ///
        /// The RAW value is not modified — it is passed by value and the filtered result is
        /// returned separately, so the caller keeps both.
        /// </summary>
        public double Process(int channel, double sample)
        {
            if (channel < 0 || channel >= m_ChannelCount)
                return sample;

            var sections = m_Sections[channel];
            var y = sample;

            for (var s = 0; s < sections.Length; s++)
                y = sections[s].Process(y);

            return y;
        }

        /// <summary>
        /// Clears every channel's state.
        ///
        /// Called ONLY when the stream itself restarts — never per epoch. Resetting between
        /// windows is precisely the transient this design exists to avoid.
        /// </summary>
        public void Reset()
        {
            foreach (var channel in m_Sections)
            {
                foreach (var section in channel)
                    section.Reset();
            }
        }

        /// <summary>The cascade's magnitude response at a frequency, as a linear gain.</summary>
        public double MagnitudeAt(double frequencyHz)
        {
            var gain = 1.0;

            foreach (var section in m_Sections[0])
                gain *= section.MagnitudeAt(frequencyHz, m_SampleRateHz);

            return gain;
        }

        /// <summary>The cascade's magnitude response in dB.</summary>
        public double GainDbAt(double frequencyHz)
        {
            var magnitude = MagnitudeAt(frequencyHz);
            return magnitude > 0d ? 20.0 * Math.Log10(magnitude) : double.NegativeInfinity;
        }

        /// <summary>
        /// How long the filter needs before its output is trustworthy, in samples.
        ///
        /// Any IIR filter starts from zero state and takes time to settle. Data from before this
        /// point is a transient, not signal, and a window cut there would carry it into the
        /// spectrum. Derived from the lowest cutoff — the slowest thing the filter must resolve.
        /// </summary>
        public int SettlingSamples => (int)Math.Ceiling(m_SampleRateHz * 3.0 / m_HighPassHz);

        /// <summary>
        /// Magnitude response of the NOTCH SECTION ALONE at one frequency, as a linear ratio.
        ///
        /// Isolated deliberately: it answers "what does the notch itself do here?", which is the
        /// question that has to be answered to show the notch is correct without the band-pass
        /// response confounding it. Returns 1.0 (no effect) when the notch is disabled.
        /// </summary>
        public double NotchMagnitudeAt(double frequencyHz)
        {
            if (!m_NotchEnabled || m_Sections.Length == 0)
                return 1.0;

            var sections = m_Sections[0];
            return sections[sections.Length - 1].MagnitudeAt(frequencyHz, m_SampleRateHz);
        }

        public string Describe()
        {
            var sb = new StringBuilder();

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "Butterworth band-pass, causal, cascaded second-order sections (Direct Form I)"));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  high-pass {0:F3} Hz order {1}; low-pass {2:F3} Hz order {3}; overall order {4}",
                m_HighPassHz, HalfOrder, m_LowPassHz, HalfOrder, overallOrder));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  sections per channel: {0}; independent state per channel: {1} channels",
                sectionsPerChannel, m_ChannelCount));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  fs = {0:F4} Hz (from stream metadata); settling ≈ {1} samples ({2:F2} s)",
                m_SampleRateHz, SettlingSamples, SettlingSamples / m_SampleRateHz));
            if (m_NotchEnabled)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  notch ON: centre {0:F3} Hz, Q {1:F2}, -3 dB bandwidth {2:F3} Hz, " +
                    "placed LAST in the chain",
                    m_NotchHz, m_NotchQ, notchBandwidthHz));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  notch attenuation: {0:F2} dB at centre; {1:F2} dB at 6 Hz; {2:F2} dB at 10 Hz",
                    20.0 * Math.Log10(Math.Max(1e-12, NotchMagnitudeAt(m_NotchHz))),
                    20.0 * Math.Log10(Math.Max(1e-12, NotchMagnitudeAt(6.0))),
                    20.0 * Math.Log10(Math.Max(1e-12, NotchMagnitudeAt(10.0)))));
                sb.AppendLine("  OPTIONAL and redundant for 1-40 Hz work: the 40 Hz low-pass");
                sb.AppendLine("  already attenuates 60 Hz, and neither theta nor alpha is near it.");
            }
            else
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  notch OFF (available: {0:F0} Hz, Q {1:F0}); the {2:F0} Hz low-pass already",
                    DefaultNotchHz, DefaultNotchQ, m_LowPassHz));
                sb.AppendLine("  attenuates mains, so this is a protocol option, not a necessity.");
            }

            sb.AppendLine("  causal forward-only: NOT phase-linear; group delay is frequency-");
            sb.AppendLine("  dependent. Valid for spectral POWER; not for ERP latency.");
            sb.AppendLine("  coefficients (channel 0, normalised so a0 = 1):");

            var sections = m_Sections[0];
            var halfCount = HalfOrder / 2;

            for (var s = 0; s < sections.Length; s++)
            {
                var kind = s < halfCount ? "HP"
                    : s < halfCount * 2 ? "LP"
                    : "NOTCH";

                sb.AppendLine($"    [{s}] {kind} {sections[s].Describe()}");
            }

            return sb.ToString().TrimEnd();
        }
    }
}
