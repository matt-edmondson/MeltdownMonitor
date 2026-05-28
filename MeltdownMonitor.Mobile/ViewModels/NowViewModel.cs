using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Mobile.ViewModels;

/// <summary>
/// Surface for the Now screen. Phase 1 scaffold — the observable streams
/// will be wired to the BLE pipeline in Phase 2.
/// </summary>
public sealed class NowViewModel
{
	public IObservable<DetectorState>? StateStream { get; init; }
	public IObservable<HrvSample>? SampleStream { get; init; }
}
