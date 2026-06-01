using MeltdownMonitor.Core.Detection;

namespace MeltdownMonitor.Core.Regulation;

/// <summary>
/// A captured point on the Regulation Field comet trail: the <see cref="RegulationReading"/>
/// plus the <see cref="DetectorState"/> at the moment it was recorded. Carrying the state lets
/// each trail segment keep the colour it had when first drawn, instead of the whole trail
/// recolouring to the current state as the detector advances.
/// </summary>
public readonly record struct RegulationTrailPoint(RegulationReading Reading, DetectorState State);
