namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// An inclusive bucket-coordinate bounding box over a <see cref="RegulationFieldDensity"/> grid —
/// the block of cells where dwell concentrates. Coordinates are bucket indices (not pixels):
/// <see cref="MinX"/>/<see cref="MaxX"/> are columns on the arousal-index axis,
/// <see cref="MinY"/>/<see cref="MaxY"/> rows on the vagal-tone axis. Both ends are inclusive, so a
/// single-cell region has <see cref="MinX"/> == <see cref="MaxX"/>.
/// </summary>
public readonly record struct RegulationDensityBounds(int MinX, int MinY, int MaxX, int MaxY)
{
	/// <summary>Width of the box in cells (inclusive), always &gt;= 1.</summary>
	public int Width => MaxX - MinX + 1;

	/// <summary>Height of the box in cells (inclusive), always &gt;= 1.</summary>
	public int Height => MaxY - MinY + 1;
}
