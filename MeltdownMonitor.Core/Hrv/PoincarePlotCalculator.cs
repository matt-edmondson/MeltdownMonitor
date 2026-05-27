namespace MeltdownMonitor.Core.Hrv;

public static class PoincarePlotCalculator
{
	/// <summary>
	/// Computes SD1, SD2, and their ratio from an NN interval series.
	/// Returns null if fewer than 3 intervals are provided.
	/// </summary>
	public static (double SD1, double SD2, double Ratio, double Sdnn)? Compute(double[] rrsMs)
	{
		if (rrsMs.Length < 3)
		{
			return null;
		}

		// SD1 = std of (RR[i+1] - RR[i]) / sqrt(2) = RMSSD / sqrt(2)
		double sumSqDiff = 0;
		for (int i = 1; i < rrsMs.Length; i++)
		{
			double d = rrsMs[i] - rrsMs[i - 1];
			sumSqDiff += d * d;
		}

		double rmssd = Math.Sqrt(sumSqDiff / (rrsMs.Length - 1));
		double sd1 = rmssd / Math.Sqrt(2.0);

		// SDNN = std(RR)
		double mean = rrsMs.Average();
		double variance = rrsMs.Average(r => (r - mean) * (r - mean));
		double sdnn = Math.Sqrt(variance);

		// SD2 = sqrt(2 * SDNN^2 - SD1^2)   (may be 0 if data is pathological)
		double sd2Sq = 2.0 * sdnn * sdnn - sd1 * sd1;
		double sd2 = Math.Sqrt(Math.Max(0, sd2Sq));

		double ratio = sd2 > 0 ? sd1 / sd2 : 0;

		return (sd1, sd2, ratio, sdnn);
	}
}
