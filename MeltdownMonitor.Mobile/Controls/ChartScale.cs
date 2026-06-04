namespace MeltdownMonitor.Mobile.Controls;

/// <summary>
/// Pure data→pixel mapping for the hand-rolled metric charts. Kept free of any
/// Avalonia type so it is unit-testable. Mirrors the desktop ImPlot behaviour:
/// padded auto-fit on Y, newest sample at the right edge on a time X axis.
/// Public (rather than internal) to match the existing <c>Sparkline</c> control and
/// keep the test assembly's access trivial without an InternalsVisibleTo.
/// </summary>
public static class ChartScale
{
	/// <summary>Padded [min, max] across one or more series (nulls/empties skipped).
	/// A flat or empty overall range expands to a small visible band so a line still renders.</summary>
	public static (double Min, double Max) FitRange(IReadOnlyList<IReadOnlyList<double>?> series, double padFraction)
	{
		double min = double.PositiveInfinity;
		double max = double.NegativeInfinity;
		foreach (var s in series)
		{
			if (s is null)
			{
				continue;
			}

			foreach (double v in s)
			{
				if (double.IsNaN(v) || double.IsInfinity(v))
				{
					continue;
				}

				if (v < min)
				{
					min = v;
				}

				if (v > max)
				{
					max = v;
				}
			}
		}

		if (double.IsInfinity(min) || double.IsInfinity(max))
		{
			return (0.0, 1.0);
		}

		double span = max - min;
		if (span <= 0)
		{
			double band = Math.Abs(max) > 1e-9 ? Math.Abs(max) * 0.1 : 1.0;
			return (min - band, max + band);
		}

		double pad = span * padFraction;
		return (min - pad, max + pad);
	}

	/// <summary>Pixel Y for a value: max maps to 0 (top), min maps to <paramref name="height"/> (bottom).</summary>
	public static double Y(double value, double min, double max, double height)
	{
		double range = max - min;
		if (range <= 0)
		{
			return height * 0.5;
		}

		double frac = (value - min) / range;
		return height - (Math.Clamp(frac, 0.0, 1.0) * height);
	}

	/// <summary>Pixel X for a timestamp (epoch seconds): now at the right edge,
	/// (now - windowSec) at the left edge. Clamped to the control width.</summary>
	public static double TimeX(double timestampSec, double now, double windowSec, double width)
	{
		double w = Math.Max(1.0, windowSec);
		double age = now - timestampSec;
		double frac = 1.0 - Math.Clamp(age / w, 0.0, 1.0);
		return frac * width;
	}
}
