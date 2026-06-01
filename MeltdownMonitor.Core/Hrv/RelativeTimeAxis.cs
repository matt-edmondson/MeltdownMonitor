using System.Globalization;

namespace MeltdownMonitor.Core.Hrv;

/// <summary>
/// Generates relative tick marks ("now", "-1 min", "-30s") for a time-relative plot
/// axis that spans <c>windowSeconds</c> back from the present. Positions are &lt;= 0
/// (seconds before now); the step coarsens as the window widens so labels never crowd.
/// Pure and platform-neutral so both heads share one definition.
/// </summary>
public static class RelativeTimeAxis
{
	/// <summary>
	/// Returns evenly spaced tick positions (seconds, all &lt;= 0) and their labels:
	/// 0 = now, then stepping back in multiples of an auto-chosen step for as long as the
	/// tick stays within <paramref name="windowSeconds"/>. The grid favours clean step
	/// multiples, so the last tick may fall short of the exact window edge. Labels under a
	/// minute read in seconds ("-30s"); the rest read in minutes.
	/// </summary>
	public static (double[] Positions, string[] Labels) Ticks(double windowSeconds)
	{
		double window = Math.Max(1.0, windowSeconds);
		double step = ChooseStep(window);

		var positions = new List<double>();
		var labels = new List<string>();
		for (double t = 0.0; t >= -window - 1e-6; t -= step)
		{
			positions.Add(t);
			labels.Add(Label(t));
		}

		return ([.. positions], [.. labels]);
	}

	// All step values are whole seconds whose /60 is exactly representable, which is what
	// Label's "minutes == Math.Floor(minutes)" test relies on — preserve that if adding a band.
	// Tick spacing bands — coarse enough that even a 6-hour window doesn't crowd.
	private static double ChooseStep(double windowSeconds) => windowSeconds switch
	{
		<= 120.0 => 30.0,    // <= 2 min  -> every 30 s
		<= 600.0 => 120.0,   // <= 10 min -> every 2 min
		<= 3600.0 => 600.0,  // <= 60 min -> every 10 min
		_ => 1800.0,         // larger    -> every 30 min
	};

	private static string Label(double t)
	{
		if (t >= -1e-6)
		{
			return "now";
		}

		double seconds = -t;
		if (seconds < 60.0)
		{
			return string.Create(CultureInfo.InvariantCulture, $"-{seconds:0}s");
		}

		double minutes = seconds / 60.0;
		return minutes == Math.Floor(minutes)
			? string.Create(CultureInfo.InvariantCulture, $"-{minutes:0} min")
			: string.Create(CultureInfo.InvariantCulture, $"-{minutes:0.#} min");
	}
}
