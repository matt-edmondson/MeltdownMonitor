namespace MeltdownMonitor.Core.Beats;

public enum PolarDeviceType
{
	/// <summary>Connect to the first device found advertising the Heart Rate Service.</summary>
	Auto,

	/// <summary>Polar H10 chest strap — "Polar H10 xxxxxxxx"</summary>
	H10,

	/// <summary>Polar Verity Sense optical armband — "Polar Sense xxxxxxxx"</summary>
	VeritySense,
}
