namespace MeltdownMonitor.Core.Beats;

/// <summary>
/// Static identity read from the sensor's GATT Device Information Service
/// (<c>0x180A</c>): manufacturer (<c>0x2A29</c>), model (<c>0x2A24</c>), serial
/// (<c>0x2A25</c>), and firmware / hardware / software revisions
/// (<c>0x2A26</c> / <c>0x2A27</c> / <c>0x2A28</c>). Every field is optional — a
/// device need not expose them all.
/// </summary>
public record DeviceInformation(
	string? ManufacturerName = null,
	string? ModelNumber = null,
	string? SerialNumber = null,
	string? FirmwareRevision = null,
	string? HardwareRevision = null,
	string? SoftwareRevision = null)
{
	/// <summary>A short one-line summary for display, e.g. <c>"Polar H10 · fw 3.1.1"</c>.</summary>
	public string Summary
	{
		get
		{
			string? name = !string.IsNullOrWhiteSpace(ModelNumber) ? ModelNumber : ManufacturerName;
			var parts = new List<string>();
			if (!string.IsNullOrWhiteSpace(name))
			{
				parts.Add(name!.Trim());
			}

			if (!string.IsNullOrWhiteSpace(FirmwareRevision))
			{
				parts.Add($"fw {FirmwareRevision!.Trim()}");
			}

			return parts.Count > 0 ? string.Join(" · ", parts) : "Unknown device";
		}
	}
}
