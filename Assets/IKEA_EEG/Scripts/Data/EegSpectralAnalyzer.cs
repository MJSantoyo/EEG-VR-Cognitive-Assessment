using System;
using System.Globalization;

namespace IkeaEeg.Data
{
    /// <summary>One frequency band, by name and edges.</summary>
    public readonly struct EegBand
    {
        public readonly string name;
        public readonly double lowHz;
        public readonly double highHz;

        public EegBand(string name, double lowHz, double highHz)
        {
            this.name = name;
            this.lowHz = lowHz;
            this.highHz = highHz;
        }

        public static readonly EegBand Theta = new EegBand("theta", 4.0, 8.0);
        public static readonly EegBand Alpha = new EegBand("alpha", 8.0, 12.0);
    }

    /// <summary>A one-sided power spectral density estimate.</summary>
    public class PsdResult
    {
        /// <summary>Bin centre frequencies, Hz. Length = fftLength/2 + 1.</summary>
        public double[] frequencies;

        /// <summary>One-sided PSD, in (input units)²/Hz.</summary>
        public double[] psd;

        /// <summary>Spacing between FFT bins, Hz — fs / fftLength.</summary>
        public double binSpacingHz;

        /// <summary>
        /// PHYSICAL frequency resolution, 1 / segmentDuration.
        ///
        /// Distinct from <see cref="binSpacingHz"/> and reported separately on purpose: if the
        /// FFT is zero-padded, bins get closer together but the observation does not get longer,
        /// so two sinusoids closer than this remain unresolvable no matter how fine the grid.
        /// Zero padding interpolates the spectrum; it does not add information.
        /// </summary>
        public double physicalResolutionHz;

        public int segmentCount;
        public int segmentSamples;
        public int fftLength;
        public double sampleRateHz;

        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "segments={0} x {1} samples; fft={2}; bin spacing={3:F4} Hz; " +
                "PHYSICAL resolution={4:F4} Hz; fs={5:F3} Hz",
                segmentCount, segmentSamples, fftLength, binSpacingHz,
                physicalResolutionHz, sampleRateHz);
        }
    }

    /// <summary>
    /// Welch power spectral density and band power.
    ///
    /// DELIBERATELY DEPENDENCY-FREE: it takes an array of samples and a sample rate and returns
    /// numbers. It knows nothing about LSL, about AuraLslReceiver, about Unity scene state or
    /// about the experiment, which is what makes it testable against synthetic signals with
    /// known answers — and that is the only way to be sure a spectrum is right, since a plausible
    /// but wrong PSD looks exactly like a correct one.
    ///
    /// UNITS ARE WHATEVER THE INPUT WAS, squared, per hertz. This class never claims µV²/Hz;
    /// the caller supplies the unit label from verified provenance.
    ///
    /// NO SIGNAL PROCESSING BEYOND THIS: no filtering (that happens upstream, continuously), no
    /// artefact rejection, no normalisation to a baseline, no interpretation.
    /// </summary>
    public static class EegSpectralAnalyzer
    {
        // ---------------------------------------------------------------------------------
        // Window
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Periodic Hann window: w[n] = 0.5 (1 - cos(2*pi*n / N)), n = 0..N-1.
        ///
        /// PERIODIC (divisor N), not symmetric (N-1). For spectral estimation with overlapping
        /// segments the periodic form is correct: it makes the window's implied period exactly N
        /// samples, which is what the DFT assumes, and 50%-overlapped periodic Hann sums to a
        /// constant. The symmetric form is for FIR filter design. The two differ by one sample
        /// and the choice is stated here because it changes the window energy — and therefore
        /// every absolute PSD value — by a small but real amount.
        /// </summary>
        public static double[] HannWindow(int length)
        {
            var w = new double[length];

            for (var n = 0; n < length; n++)
                w[n] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / length));

            return w;
        }

        // ---------------------------------------------------------------------------------
        // FFT
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// In-place iterative radix-2 Cooley-Tukey FFT. Length must be a power of two.
        ///
        /// Written locally rather than pulled in as a package: it is forty lines, it is
        /// deterministic, and adding a third-party dependency to a Quest build for this would be
        /// disproportionate. Verified against known signals in the self test.
        /// </summary>
        public static void Fft(double[] re, double[] im)
        {
            var n = re.Length;

            if (n <= 1)
                return;

            if ((n & (n - 1)) != 0)
                throw new ArgumentException("FFT length must be a power of two", nameof(re));

            // Bit-reversal permutation.
            for (int i = 1, j = 0; i < n; i++)
            {
                var bit = n >> 1;

                for (; (j & bit) != 0; bit >>= 1)
                    j ^= bit;

                j ^= bit;

                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            for (var len = 2; len <= n; len <<= 1)
            {
                var angle = -2.0 * Math.PI / len;
                var wRe = Math.Cos(angle);
                var wIm = Math.Sin(angle);

                for (var i = 0; i < n; i += len)
                {
                    double curRe = 1.0, curIm = 0.0;

                    for (var k = 0; k < len / 2; k++)
                    {
                        var uRe = re[i + k];
                        var uIm = im[i + k];

                        var vRe = re[i + k + len / 2] * curRe - im[i + k + len / 2] * curIm;
                        var vIm = re[i + k + len / 2] * curIm + im[i + k + len / 2] * curRe;

                        re[i + k] = uRe + vRe;
                        im[i + k] = uIm + vIm;
                        re[i + k + len / 2] = uRe - vRe;
                        im[i + k + len / 2] = uIm - vIm;

                        var nextRe = curRe * wRe - curIm * wIm;
                        curIm = curRe * wIm + curIm * wRe;
                        curRe = nextRe;
                    }
                }
            }
        }

        /// <summary>Smallest power of two greater than or equal to a value.</summary>
        public static int NextPowerOfTwo(int value)
        {
            var p = 1;
            while (p < value)
                p <<= 1;

            return p;
        }

        // ---------------------------------------------------------------------------------
        // Welch
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Welch PSD: overlapping Hann-windowed segments, averaged periodograms.
        ///
        /// NORMALISATION, stated explicitly because an unstated one is unreproducible:
        ///
        ///   P[k] = |X[k]|² / (fs · Σ w[n]²)
        ///
        /// where X is the FFT of the windowed, de-meaned segment. Dividing by Σw² (not by N)
        /// compensates the window's energy loss, and dividing by fs converts power per bin into
        /// power per hertz — a DENSITY, so its integral over a band is a power independent of
        /// the FFT length.
        ///
        /// ONE-SIDED: bins 1..N/2-1 are DOUBLED to fold in the negative frequencies. DC (bin 0)
        /// and Nyquist (bin N/2) are NOT doubled — they have no distinct negative-frequency
        /// partner, and doubling them is a common error that inflates both ends.
        ///
        /// Each segment is DE-MEANED before windowing. A residual offset would otherwise leak
        /// out of DC across the low bins and contaminate theta, which sits only 4 Hz away.
        /// </summary>
        public static PsdResult Welch(double[] samples, double sampleRateHz,
            double segmentSeconds = 2.0, double overlapFraction = 0.5, int fftLength = 0)
        {
            if (samples == null || samples.Length == 0)
                throw new ArgumentException("no samples", nameof(samples));

            if (sampleRateHz <= 0d)
                throw new ArgumentOutOfRangeException(nameof(sampleRateHz));

            // Segment length derived from the ACTUAL rate, never a constant.
            var segmentSamples = (int)Math.Round(segmentSeconds * sampleRateHz);

            if (segmentSamples > samples.Length)
                segmentSamples = samples.Length;

            if (segmentSamples < 8)
                throw new ArgumentException("window too short for a spectrum", nameof(samples));

            var step = Math.Max(1, (int)Math.Round(segmentSamples * (1.0 - overlapFraction)));

            var n = fftLength > 0 ? fftLength : NextPowerOfTwo(segmentSamples);

            if (n < segmentSamples)
                n = NextPowerOfTwo(segmentSamples);

            var window = HannWindow(segmentSamples);

            double windowEnergy = 0d;
            foreach (var w in window)
                windowEnergy += w * w;

            var bins = n / 2 + 1;
            var accumulated = new double[bins];
            var segments = 0;

            var re = new double[n];
            var im = new double[n];

            for (var start = 0; start + segmentSamples <= samples.Length; start += step)
            {
                // De-mean this segment.
                double mean = 0d;
                for (var i = 0; i < segmentSamples; i++)
                    mean += samples[start + i];

                mean /= segmentSamples;

                Array.Clear(re, 0, n);
                Array.Clear(im, 0, n);

                for (var i = 0; i < segmentSamples; i++)
                    re[i] = (samples[start + i] - mean) * window[i];

                // Anything past segmentSamples stays zero: that is the zero padding, and it
                // interpolates the spectrum without adding physical resolution.
                Fft(re, im);

                for (var k = 0; k < bins; k++)
                {
                    var power = re[k] * re[k] + im[k] * im[k];

                    // One-sided folding: interior bins carry their mirror's energy too.
                    if (k > 0 && k < n / 2)
                        power *= 2.0;

                    accumulated[k] += power / (sampleRateHz * windowEnergy);
                }

                segments++;
            }

            if (segments == 0)
                throw new ArgumentException("no complete segment fitted in the window");

            var psd = new double[bins];
            var frequencies = new double[bins];

            for (var k = 0; k < bins; k++)
            {
                psd[k] = accumulated[k] / segments;
                frequencies[k] = k * sampleRateHz / n;
            }

            return new PsdResult
            {
                frequencies = frequencies,
                psd = psd,
                binSpacingHz = sampleRateHz / n,

                // The observation length, not the grid spacing.
                physicalResolutionHz = sampleRateHz / segmentSamples,

                segmentCount = segments,
                segmentSamples = segmentSamples,
                fftLength = n,
                sampleRateHz = sampleRateHz,
            };
        }

        // ---------------------------------------------------------------------------------
        // Band power
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Integrates the PSD across a band by the trapezoidal rule.
        ///
        /// FREQUENCY-AWARE: it multiplies by the bin spacing, so the result is a power in
        /// (input units)² and does not change if the FFT length changes. Summing bare bin values
        /// would give a number that silently doubled whenever the FFT was zero-padded further.
        ///
        /// The band is inclusive of its edges. Trapezoidal rather than rectangular because the
        /// PSD is a sampled continuous function and the band edges rarely land on bin centres.
        /// </summary>
        public static double BandPower(PsdResult psd, EegBand band)
        {
            return BandPower(psd, band.lowHz, band.highHz);
        }

        public static double BandPower(PsdResult psd, double lowHz, double highHz)
        {
            if (psd?.psd == null || psd.frequencies == null)
                return double.NaN;

            double total = 0d;
            var previousIndex = -1;

            for (var k = 0; k < psd.frequencies.Length; k++)
            {
                var f = psd.frequencies[k];

                if (f < lowHz || f > highHz)
                    continue;

                if (previousIndex >= 0)
                {
                    var df = f - psd.frequencies[previousIndex];
                    total += 0.5 * (psd.psd[k] + psd.psd[previousIndex]) * df;
                }

                previousIndex = k;
            }

            // A band narrower than one bin contains no interval to integrate over. Reporting the
            // single bin's density times the bin width is the honest approximation.
            if (previousIndex >= 0 && total == 0d)
                total = psd.psd[previousIndex] * psd.binSpacingHz;

            return total;
        }

        /// <summary>The frequency of the largest PSD value within a range. For validation.</summary>
        public static double PeakFrequency(PsdResult psd, double lowHz, double highHz)
        {
            var best = double.NaN;
            var bestValue = double.NegativeInfinity;

            for (var k = 0; k < psd.frequencies.Length; k++)
            {
                var f = psd.frequencies[k];

                if (f < lowHz || f > highHz)
                    continue;

                if (psd.psd[k] > bestValue)
                {
                    bestValue = psd.psd[k];
                    best = f;
                }
            }

            return best;
        }
    }
}
