using System.Numerics;

namespace MeltdownMonitor.Core.Hrv;

/// <summary>
/// Computes LF and HF power spectral density from an NN interval series.
///
/// Method: linear interpolation to a 4 Hz grid → Hanning window → radix-2 FFT
/// → one-sided PSD integrated over the standard HRV frequency bands.
///
/// Minimum signal length: <see cref="MinWindowSeconds"/> (default 120 s).
/// Clinical standard is 5 minutes; shorter windows underestimate LF.
/// </summary>
public static class FrequencyDomainHrvCalculator
{
	public const double SampleRateHz = 4.0;
	public const double MinWindowSeconds = 120.0;

	public const double LfLow = 0.04;
	public const double LfHigh = 0.15;
	public const double HfLow = 0.15;
	public const double HfHigh = 0.40;

	/// <summary>
	/// Returns (LF ms², HF ms², LF/HF) or null if the window is too short.
	/// </summary>
	/// <param name="rrsMs">NN intervals in milliseconds, oldest first.</param>
	public static (double LfMs2, double HfMs2, double LfHfRatio)? Compute(double[] rrsMs)
	{
		if (rrsMs.Length < 3)
		{
			return null;
		}

		// Build cumulative time axis in seconds
		double[] t = BuildTimeAxis(rrsMs);
		double totalDuration = t[^1] + rrsMs[^1] / 1000.0;

		if (totalDuration < MinWindowSeconds)
		{
			return null;
		}

		// Interpolate to regular 4 Hz grid
		double[] signal = Interpolate(rrsMs, t, totalDuration);
		int n = signal.Length;

		if (n < 2)
		{
			return null;
		}

		// Subtract mean
		double mean = signal.Average();
		for (int i = 0; i < n; i++)
		{
			signal[i] -= mean;
		}

		// Apply Hanning window and accumulate window power
		double windowSumSq = 0;
		for (int i = 0; i < n; i++)
		{
			double w = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (n - 1)));
			signal[i] *= w;
			windowSumSq += w * w;
		}

		// Zero-pad to next power of two
		int fftSize = NextPow2(n);
		var buf = new Complex[fftSize];
		for (int i = 0; i < n; i++)
		{
			buf[i] = new Complex(signal[i], 0);
		}

		Fft(buf);

		// One-sided PSD (ms²/Hz): PSD[k] = 2 * |X[k]|² / (fs * Σw²)
		double df = SampleRateHz / fftSize;
		double lfPower = 0, hfPower = 0;

		for (int k = 1; k < fftSize / 2; k++)
		{
			double freq = k * df;
			double psd = 2.0 * buf[k].MagnitudeSquared() / (SampleRateHz * windowSumSq);

			if (freq >= LfLow && freq < LfHigh)
			{
				lfPower += psd * df;
			}
			else if (freq >= HfLow && freq < HfHigh)
			{
				hfPower += psd * df;
			}
		}

		double ratio = hfPower > 0 ? lfPower / hfPower : 0;
		return (lfPower, hfPower, ratio);
	}

	private static double[] BuildTimeAxis(double[] rrsMs)
	{
		double[] t = new double[rrsMs.Length];
		for (int i = 1; i < rrsMs.Length; i++)
		{
			t[i] = t[i - 1] + rrsMs[i - 1] / 1000.0;
		}

		return t;
	}

	private static double[] Interpolate(double[] rrsMs, double[] t, double totalDuration)
	{
		int nSamples = (int)(totalDuration * SampleRateHz);
		var result = new double[nSamples];
		int seg = 0;

		for (int j = 0; j < nSamples; j++)
		{
			double tx = j / SampleRateHz;

			// Advance segment pointer
			while (seg < t.Length - 2 && t[seg + 1] <= tx)
			{
				seg++;
			}

			if (seg >= t.Length - 1)
			{
				result[j] = rrsMs[^1];
				continue;
			}

			double span = t[seg + 1] - t[seg];
			double alpha = span > 0 ? (tx - t[seg]) / span : 0;
			result[j] = rrsMs[seg] + alpha * (rrsMs[seg + 1] - rrsMs[seg]);
		}

		return result;
	}

	private static int NextPow2(int n)
	{
		int p = 1;
		while (p < n)
		{
			p <<= 1;
		}

		return p;
	}

	/// <summary>In-place radix-2 Cooley-Tukey FFT. Length must be a power of two.</summary>
	public static void Fft(Complex[] data)
	{
		int n = data.Length;

		// Bit-reversal permutation
		for (int i = 1, j = 0; i < n; i++)
		{
			int bit = n >> 1;
			for (; (j & bit) != 0; bit >>= 1)
			{
				j ^= bit;
			}

			j ^= bit;
			if (i < j)
			{
				(data[i], data[j]) = (data[j], data[i]);
			}
		}

		// Butterfly passes
		for (int len = 2; len <= n; len <<= 1)
		{
			double angle = -2.0 * Math.PI / len;
			var wlen = new Complex(Math.Cos(angle), Math.Sin(angle));

			for (int i = 0; i < n; i += len)
			{
				Complex w = Complex.One;
				int half = len / 2;

				for (int j = 0; j < half; j++)
				{
					Complex u = data[i + j];
					Complex v = data[i + j + half] * w;
					data[i + j] = u + v;
					data[i + j + half] = u - v;
					w *= wlen;
				}
			}
		}
	}
}

file static class ComplexExtensions
{
	internal static double MagnitudeSquared(this Complex c) => c.Real * c.Real + c.Imaginary * c.Imaginary;
}
