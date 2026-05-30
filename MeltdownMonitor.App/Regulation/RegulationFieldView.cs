using System.Numerics;
using Hexa.NET.ImGui;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.App.Regulation;

/// <summary>
/// Signature visualization: the Regulation Field. Draws the lemniscate instrument
/// with a live marker (arousal-vs-baseline), comet trail, ghost baseline, and a
/// live readout. Custom ImGui draw-list rendering — not ImPlot.
/// </summary>
public sealed class RegulationFieldView : IDisposable
{
	private const int TrailLength = 48;     // ~last few minutes at the emit cadence
	private const int LobeSegments = 96;
	private const float MarkerEaseRate = 6f;     // exponential-smoothing rate for the marker glide
	private const float LobeSwellFactor = 1.4f;  // how much a lobe thickens at full index

	private readonly Pipeline _pipeline;
	private readonly object _lock = new();
	private readonly RegulationReading[] _trail = new RegulationReading[TrailLength];
	private int _trailCount;

	// Animation state (UI thread only).
	private float _markerPos;       // eased toward the latest index
	private float _breathPhase;
	private float _animTime;

	public RegulationFieldView(Pipeline pipeline)
	{
		_pipeline = pipeline;
		_pipeline.SampleUpdated += OnSampleUpdated;
	}

	private void OnSampleUpdated(HrvSample sample)
	{
		var reading = RegulationFieldCalculator.Compute(
			sample,
			_pipeline.LatestThresholds,
			_pipeline.Baseline.WarmUpProgress,
			_pipeline.Baseline.IsWarm);

		lock (_lock)
		{
			if (_trailCount < TrailLength)
			{
				_trail[_trailCount++] = reading;
			}
			else
			{
				Array.Copy(_trail, 1, _trail, 0, TrailLength - 1);
				_trail[^1] = reading;
			}
		}
	}

	private (RegulationReading latest, RegulationReading[] trail) Snapshot()
	{
		lock (_lock)
		{
			if (_trailCount == 0)
			{
				return (new RegulationReading(0, 1, 0, 0.5, 0), []);
			}

			var copy = new RegulationReading[_trailCount];
			Array.Copy(_trail, copy, _trailCount);
			return (copy[^1], copy);
		}
	}

	public void Draw()
	{
		var (latest, trail) = Snapshot();
		float dt = ImGui.GetIO().DeltaTime;
		_animTime += dt;

		// Ease the marker toward the latest index so it glides between 5 s samples.
		float target = (float)latest.Index;
		_markerPos += (target - _markerPos) * (1f - MathF.Exp(-dt * MarkerEaseRate));

		// Breathing cadence from current HR (fallback 60 bpm).
		double hr = _pipeline.LatestSample?.MeanHr ?? 60.0;
		_breathPhase += dt * (float)(Math.Max(40.0, hr) / 60.0) * MathF.Tau;

		// Reserve a wide panel and centre the instrument in it.
		Vector2 avail = ImGui.GetContentRegionAvail();
		float height = MathF.Min(avail.Y - 8f, 360f);
		float width = avail.X;
		Vector2 origin = ImGui.GetCursorScreenPos();
		ImGui.Dummy(new Vector2(width, height)); // reserve layout space

		var draw = ImGui.GetWindowDrawList();
		Vector2 centre = origin + new Vector2(width * 0.5f, height * 0.46f);
		float halfWidth = MathF.Min(width * 0.34f, 260f);
		float lobeHeight = MathF.Min(height * 0.28f, halfWidth * 0.62f);

		float confidence = (float)latest.Confidence;

		DrawWindowOfTolerance(draw, centre, halfWidth, lobeHeight, confidence);
		DrawLemniscate(draw, centre, halfWidth, lobeHeight, latest, confidence);
		DrawTrail(draw, centre, halfWidth, trail, confidence);
		DrawRecoveryTarget(draw, centre, halfWidth, lobeHeight, confidence);
		DrawMarker(draw, centre, halfWidth, confidence);
		DrawCrossover(draw, centre, confidence);
		DrawLabelsAndLock(draw, origin, centre, halfWidth, lobeHeight);
		DrawReadout(origin, height);

		if (confidence < 0.999f)
		{
			DrawCalibratingOverlay(draw, centre, confidence);
		}
	}

	private static uint Col(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

	private static void DrawWindowOfTolerance(ImDrawListPtr draw, Vector2 centre, float halfWidth, float lobeHeight, float confidence)
	{
		var zone = MacchiatoPalette.WithAlpha(MacchiatoPalette.Lavender, 0.08f * confidence);
		draw.AddEllipseFilled(centre, new Vector2(halfWidth * 0.32f, lobeHeight * 0.7f), Col(zone));
	}

	private void DrawLemniscate(ImDrawListPtr draw, Vector2 centre, float halfWidth, float lobeHeight, RegulationReading r, float confidence)
	{
		var ghost = LemniscateGeometry.Polyline(centre, halfWidth, lobeHeight, LobeSegments);

		uint ghostCol = Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Overlay1, 0.28f * confidence));
		for (int i = 0; i < ghost.Count; i++)
		{
			draw.AddLine(ghost[i], ghost[(i + 1) % ghost.Count], ghostCol, 2f);
		}

		float warmSwell = 1f + (MathF.Max(0f, (float)r.Index) * LobeSwellFactor);
		float coolSwell = 1f + (MathF.Max(0f, -(float)r.Index) * LobeSwellFactor);
		float quality = (float)r.VariabilityQuality;
		float baseThick = 4f + (6f * quality);

		for (int i = 0; i < ghost.Count; i++)
		{
			Vector2 a = ghost[i];
			Vector2 b = ghost[(i + 1) % ghost.Count];
			float midX = (a.X + b.X) * 0.5f;
			bool warm = midX >= centre.X;
			float depth = MathF.Min(1f, MathF.Abs(midX - centre.X) / halfWidth);

			Vector4 c = warm
				? MacchiatoPalette.Lerp(MacchiatoPalette.Peach, MacchiatoPalette.Maroon, depth)
				: MacchiatoPalette.Lerp(MacchiatoPalette.Sky, MacchiatoPalette.Sapphire, depth);
			c = MacchiatoPalette.WithAlpha(c, confidence);

			float jitter = quality * 1.5f * MathF.Sin((_animTime * 6f) + (i * 0.7f)) * depth;
			Vector2 n = Normal(a, b) * jitter;

			float thick = baseThick * (warm ? warmSwell : coolSwell);
			draw.AddLine(a + n, b + n, Col(c), thick);
		}
	}

	private void DrawTrail(ImDrawListPtr draw, Vector2 centre, float halfWidth, RegulationReading[] trail, float confidence)
	{
		if (trail.Length < 2)
		{
			return;
		}

		Vector4 stateCol = MacchiatoPalette.State(_pipeline.CurrentState);
		for (int i = 0; i < trail.Length - 1; i++)
		{
			float frac = i / (float)(trail.Length - 1);
			Vector2 p = LemniscateGeometry.MarkerPoint((float)trail[i].Index, centre, halfWidth);
			float radius = 1.5f + (3f * frac);
			draw.AddCircleFilled(p, radius, Col(MacchiatoPalette.WithAlpha(stateCol, 0.5f * frac * confidence)));
		}
	}

	// During an active alert, mark where the arousal marker must fall back below to clear
	// the Warning condition (the warm-side warning boundary). A calm green target the marker
	// should return toward. Recovery also requires the cooldown timer, so this is the metric
	// target, not the whole story — but it answers "which way, and how far, to get out".
	private void DrawRecoveryTarget(ImDrawListPtr draw, Vector2 centre, float halfWidth, float lobeHeight, float confidence)
	{
		if (_pipeline.CurrentState is not (DetectorState.Warning or DetectorState.Alerting))
		{
			return;
		}

		Vector2 gate = LemniscateGeometry.MarkerPoint((float)RegulationFieldCalculator.WarningBoundaryIndex, centre, halfWidth);
		uint goal = Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Green, confidence));
		uint goalDim = Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Green, 0.45f * confidence));

		// Pulsing target ring on the track at the recovery boundary.
		float ring = 11f + (2f * MathF.Sin(_animTime * 3f));
		draw.AddCircle(gate, ring, goal, 24, 2f);
		draw.AddCircleFilled(gate, 2.5f, goal);

		// Chevron pointing inward (toward the centre / regulated zone) — the way back.
		float ay = gate.Y - (lobeHeight * 0.5f) - 6f;
		draw.AddTriangleFilled(
			new Vector2(gate.X - 11f, ay),
			new Vector2(gate.X - 3f, ay - 5f),
			new Vector2(gate.X - 3f, ay + 5f), goal);

		Vector2 lbl = ImGui.CalcTextSize("RECOVER");
		draw.AddText(new Vector2(gate.X - (lbl.X * 0.5f), ay - ImGui.GetTextLineHeight() - 3f), goalDim, "RECOVER");
	}

	private void DrawMarker(ImDrawListPtr draw, Vector2 centre, float halfWidth, float confidence)
	{
		Vector2 p = LemniscateGeometry.MarkerPoint(_markerPos, centre, halfWidth);
		Vector4 stateCol = MacchiatoPalette.State(_pipeline.CurrentState);

		float pulse = 1f + (0.18f * MathF.Sin(_breathPhase));
		draw.AddCircleFilled(p, 16f * pulse, Col(MacchiatoPalette.WithAlpha(stateCol, 0.18f * confidence)));
		draw.AddCircleFilled(p, 6.5f, Col(MacchiatoPalette.WithAlpha(stateCol, confidence)));
		draw.AddCircleFilled(p, 2.6f, Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Base, confidence)));
	}

	private static void DrawCrossover(ImDrawListPtr draw, Vector2 centre, float confidence)
	{
		draw.AddCircleFilled(centre, 7f, Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Lavender, confidence)));
		draw.AddCircleFilled(centre, 3f, Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Text, confidence)));
	}

	private void DrawLabelsAndLock(ImDrawListPtr draw, Vector2 origin, Vector2 centre, float halfWidth, float lobeHeight)
	{
		uint rest = Col(MacchiatoPalette.Sapphire);
		uint melt = Col(MacchiatoPalette.Peach);
		uint dim = Col(MacchiatoPalette.Subtext0);

		// Pole labels sit clear of the lobe tips: REST right-aligned just left of the cool
		// tip, MELTDOWN just right of the warm tip, both vertically centred on the tip line.
		// WINDOW OF TOLERANCE is horizontally centred above the lobes.
		float lineH = ImGui.GetTextLineHeight();
		float midY = centre.Y - (lineH * 0.5f);
		const float poleGap = 16f;

		Vector2 restSize = ImGui.CalcTextSize("REST");
		draw.AddText(new Vector2(centre.X - halfWidth - poleGap - restSize.X, midY), rest, "REST");
		draw.AddText(new Vector2(centre.X + halfWidth + poleGap, midY), melt, "MELTDOWN");

		Vector2 wotSize = ImGui.CalcTextSize("WINDOW OF TOLERANCE");
		draw.AddText(new Vector2(centre.X - (wotSize.X * 0.5f), centre.Y - lobeHeight - lineH - 10f), dim, "WINDOW OF TOLERANCE");

		var state = _pipeline.CurrentState;
		if (state is DetectorState.Warning or DetectorState.Alerting)
		{
			draw.AddText(new Vector2(origin.X + 8f, origin.Y + 8f), Col(MacchiatoPalette.Overlay1), "BASELINE LOCKED");
		}
	}

	private void DrawReadout(Vector2 origin, float height)
	{
		ImGui.SetCursorScreenPos(new Vector2(origin.X + 8f, origin.Y + height - 22f));
		var s = _pipeline.LatestSample;
		if (s is null)
		{
			ImGui.TextDisabled("Waiting for beats…");
			return;
		}

		double drop = s.BaselineRmssd > 0 ? (s.BaselineRmssd - s.Rmssd) / s.BaselineRmssd : 0;
		string rel = drop >= 0
			? $"{drop * 100:F0}% below base"
			: $"{-drop * 100:F0}% above base";
		ImGui.Text($"HR {s.MeanHr:F0} bpm    RMSSD {s.Rmssd:F0} ms ({rel})    {_pipeline.CurrentState}");
	}

	private static void DrawCalibratingOverlay(ImDrawListPtr draw, Vector2 centre, float confidence)
	{
		string msg = $"Calibrating baseline… {confidence * 100:F0}%";
		draw.AddText(new Vector2(centre.X - 70f, centre.Y + 30f), Col(MacchiatoPalette.Subtext0), msg);
	}

	private static Vector2 Normal(Vector2 a, Vector2 b)
	{
		Vector2 d = b - a;
		float len = d.Length();
		return len < 1e-4f ? Vector2.Zero : new Vector2(-d.Y / len, d.X / len);
	}

	public void Dispose() => _pipeline.SampleUpdated -= OnSampleUpdated;
}
