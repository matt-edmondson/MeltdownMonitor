using System.Numerics;
using Hexa.NET.ImGui;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Regulation;

namespace MeltdownMonitor.App.Regulation;

/// <summary>
/// Signature visualization: the Regulation Field. Draws the lemniscate instrument with a
/// live marker (arousal-vs-baseline on X, vagal tone on Y), a comet trail, a ghost baseline,
/// a Poincaré-shaped live trace textured with the real beat-to-beat signal, an optional
/// LF/HF balance halo, a recovery target during alerts, and a live readout. Custom ImGui
/// draw-list rendering — not ImPlot. Consumes the reading the <see cref="Pipeline"/> computes
/// centrally (single source shared with the tray/overlay).
/// </summary>
public sealed class RegulationFieldView : IDisposable
{
	private const int TrailLength = 48;          // ~last few minutes at the emit cadence
	private const int RrBufferLength = 160;      // recent RR intervals for the live trace texture
	private const int MinRrForJitter = 8;        // below this, draw a smooth (flat) trace
	private const int LobeSegments = 96;
	private const float MarkerEaseRate = 6f;     // exponential-smoothing rate for the marker glide
	private const float LobeSwellFactor = 1.4f;  // how much a lobe thickens at full index
	private const float MaxJitterPx = 6f;        // peak trace deflection from the real RR signal
	private const float RrDevScaleMs = 50f;      // beat-to-beat difference that maps to full deflection
	private const float LobeHeightMin = 0.7f;    // live lobe height factor at lowest Poincaré roundness
	private const float LobeHeightMax = 1.18f;   // ...and at highest
	private const float MarkerYSpan = 0.8f;      // vagal-tone vertical travel, as a fraction of lobe height

	private readonly Pipeline _pipeline;
	private readonly object _lock = new();
	private readonly TrailPoint[] _trail = new TrailPoint[TrailLength];
	private int _trailCount;
	private readonly double[] _rr = new double[RrBufferLength];
	private int _rrCount;

	// Animation state (UI thread only).
	private float _markerPos;       // eased toward the latest index (X)
	private float _markerY;         // eased toward the vagal-tone offset (Y)
	private float _breathPhase;
	private float _animTime;

	private readonly record struct TrailPoint(DateTimeOffset Time, RegulationReading Reading);

	public RegulationFieldView(Pipeline pipeline)
	{
		_pipeline = pipeline;
		_pipeline.SampleUpdated += OnSampleUpdated;
		_pipeline.BeatReceived += OnBeatReceived;
	}

	private void OnSampleUpdated(HrvSample sample)
	{
		// Compute the reading here (self-contained) rather than depending on a pipeline
		// reading event — keeps the view robust to pipeline API changes.
		var reading = RegulationFieldCalculator.Compute(
			sample,
			_pipeline.LatestThresholds,
			_pipeline.Baseline.WarmUpProgress,
			_pipeline.Baseline.IsWarm);

		lock (_lock)
		{
			var point = new TrailPoint(DateTimeOffset.UtcNow, reading);
			if (_trailCount < TrailLength)
			{
				_trail[_trailCount++] = point;
			}
			else
			{
				Array.Copy(_trail, 1, _trail, 0, TrailLength - 1);
				_trail[^1] = point;
			}
		}
	}

	private void OnBeatReceived(Beat beat)
	{
		if (beat.IsArtifact)
		{
			return;
		}

		lock (_lock)
		{
			if (_rrCount < RrBufferLength)
			{
				_rr[_rrCount++] = beat.RrMs;
			}
			else
			{
				Array.Copy(_rr, 1, _rr, 0, RrBufferLength - 1);
				_rr[^1] = beat.RrMs;
			}
		}
	}

	private (RegulationReading latest, RegulationReading[] trail, double[] rr) Snapshot()
	{
		lock (_lock)
		{
			var trail = new RegulationReading[_trailCount];
			for (int i = 0; i < _trailCount; i++)
			{
				trail[i] = _trail[i].Reading;
			}

			var rr = new double[_rrCount];
			Array.Copy(_rr, rr, _rrCount);

			RegulationReading latest = _trailCount > 0 ? _trail[_trailCount - 1].Reading : new RegulationReading(0, 1, 0, 0.5, 0);
			return (latest, trail, rr);
		}
	}

	public void Draw()
	{
		var (latest, trail, rr) = Snapshot();
		float dt = ImGui.GetIO().DeltaTime;
		_animTime += dt;

		// Reserve a wide panel and centre the instrument in it.
		Vector2 avail = ImGui.GetContentRegionAvail();
		float height = MathF.Min(avail.Y - 8f, 360f);
		float width = avail.X;
		Vector2 origin = ImGui.GetCursorScreenPos();
		ImGui.Dummy(new Vector2(width, height)); // reserve layout space

		var draw = ImGui.GetWindowDrawList();
		Vector2 centre = origin + new Vector2(width * 0.5f, height * 0.46f);
		float halfWidth = MathF.Min(width * 0.34f, 260f);
		float baseLobeHeight = MathF.Min(height * 0.28f, halfWidth * 0.62f);

		// Poincaré SD1/SD2 ratio shapes the live lobe height; the ghost stays at base height.
		float roundness = (float)latest.LobeRoundness;
		float liveLobeHeight = baseLobeHeight * (LobeHeightMin + ((LobeHeightMax - LobeHeightMin) * roundness));
		float labelClearHeight = baseLobeHeight * LobeHeightMax; // keep labels clear of the tallest lobe

		float confidence = (float)latest.Confidence;

		// Ease the marker toward its target each frame so it glides between 5 s readings.
		_markerPos += ((float)latest.Index - _markerPos) * (1f - MathF.Exp(-dt * MarkerEaseRate));
		float yTarget = ((float)latest.VariabilityQuality - 0.5f) * liveLobeHeight * MarkerYSpan;
		_markerY += (yTarget - _markerY) * (1f - MathF.Exp(-dt * MarkerEaseRate));

		double hr = _pipeline.LatestSample?.MeanHr ?? 60.0;
		_breathPhase += dt * (float)(Math.Max(40.0, hr) / 60.0) * MathF.Tau;

		DrawLfHfHalo(draw, centre, halfWidth, latest, confidence);
		DrawWindowOfTolerance(draw, centre, halfWidth, baseLobeHeight, confidence);
		DrawLemniscate(draw, centre, halfWidth, baseLobeHeight, liveLobeHeight, latest, rr, confidence);
		DrawTrail(draw, centre, halfWidth, trail, confidence);
		DrawRecoveryTarget(draw, centre, halfWidth, liveLobeHeight, confidence);
		DrawMarker(draw, centre, halfWidth, liveLobeHeight, confidence);
		DrawCrossover(draw, centre, confidence);
		DrawLabelsAndLock(draw, origin, centre, halfWidth, labelClearHeight);
		DrawReadout(origin, height);

		if (confidence < 0.999f)
		{
			DrawCalibratingOverlay(draw, centre, confidence);
		}
	}

	private static uint Col(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

	// Soft, asymmetric glow biased toward the dominant autonomic pole. Gated on the LF/HF
	// corroboration setting — LF/HF is noisy and off by default, so it only surfaces here
	// for users who have opted into trusting it.
	private void DrawLfHfHalo(ImDrawListPtr draw, Vector2 centre, float halfWidth, RegulationReading r, float confidence)
	{
		if (!_pipeline.LatestThresholds.UseLfHfCorroboration)
		{
			return;
		}

		float bal = (float)r.LfHfBalance;
		if (MathF.Abs(bal) < 0.02f)
		{
			return;
		}

		Vector4 hue = bal >= 0 ? MacchiatoPalette.Peach : MacchiatoPalette.Sky;
		Vector2 c = centre + new Vector2(bal * halfWidth * 0.6f, 0f);
		float baseAlpha = MathF.Min(1f, MathF.Abs(bal)) * 0.10f * confidence;

		// Three concentric discs fake a soft radial falloff (ImGui has no radial gradient).
		for (int i = 3; i >= 1; i--)
		{
			float radius = halfWidth * 0.30f * i;
			draw.AddCircleFilled(c, radius, Col(MacchiatoPalette.WithAlpha(hue, baseAlpha / i)), 32);
		}
	}

	private static void DrawWindowOfTolerance(ImDrawListPtr draw, Vector2 centre, float halfWidth, float lobeHeight, float confidence)
	{
		var zone = MacchiatoPalette.WithAlpha(MacchiatoPalette.Lavender, 0.08f * confidence);
		draw.AddEllipseFilled(centre, new Vector2(halfWidth * 0.32f, lobeHeight * 0.7f), Col(zone));
	}

	private void DrawLemniscate(ImDrawListPtr draw, Vector2 centre, float halfWidth, float baseLobeHeight, float liveLobeHeight, RegulationReading r, double[] rr, float confidence)
	{
		// Ghost baseline (symmetric resting frame) at the base height.
		var ghost = LemniscateGeometry.Polyline(centre, halfWidth, baseLobeHeight, LobeSegments);
		uint ghostCol = Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Overlay1, 0.28f * confidence));
		for (int i = 0; i < ghost.Count; i++)
		{
			draw.AddLine(ghost[i], ghost[(i + 1) % ghost.Count], ghostCol, 2f);
		}

		// Live two-tone trace at the Poincaré-shaped height, textured with the real signal.
		var live = LemniscateGeometry.Polyline(centre, halfWidth, liveLobeHeight, LobeSegments);
		float[] dev = BuildRrDeviations(rr);
		float warmSwell = 1f + (MathF.Max(0f, (float)r.Index) * LobeSwellFactor);
		float coolSwell = 1f + (MathF.Max(0f, -(float)r.Index) * LobeSwellFactor);
		float baseThick = 4f + (6f * (float)r.VariabilityQuality);

		for (int i = 0; i < live.Count; i++)
		{
			Vector2 a = live[i];
			Vector2 b = live[(i + 1) % live.Count];
			float midX = (a.X + b.X) * 0.5f;
			bool warm = midX >= centre.X;
			float depth = MathF.Min(1f, MathF.Abs(midX - centre.X) / halfWidth);

			Vector4 c = warm
				? MacchiatoPalette.Lerp(MacchiatoPalette.Peach, MacchiatoPalette.Maroon, depth)
				: MacchiatoPalette.Lerp(MacchiatoPalette.Sky, MacchiatoPalette.Sapphire, depth);
			c = MacchiatoPalette.WithAlpha(c, confidence);

			// The trace IS the live beat-to-beat signal: jagged when HRV is healthy, flat when
			// it collapses. Tapers to nothing at the crossover.
			float jitter = dev.Length > 0 ? dev[i % dev.Length] * MaxJitterPx * depth : 0f;
			Vector2 n = Normal(a, b) * jitter;

			float thick = baseThick * (warm ? warmSwell : coolSwell);
			draw.AddLine(a + n, b + n, Col(c), thick);
		}
	}

	// Normalised beat-to-beat differences in [-1, 1]. An (almost) flat result when variability
	// has collapsed; jagged when it is healthy. Empty when too few clean beats have arrived.
	private static float[] BuildRrDeviations(double[] rr)
	{
		if (rr.Length < MinRrForJitter)
		{
			return [];
		}

		var dev = new float[rr.Length];
		for (int i = 1; i < rr.Length; i++)
		{
			dev[i] = Math.Clamp((float)(rr[i] - rr[i - 1]) / RrDevScaleMs, -1f, 1f);
		}

		return dev;
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

	// During an active alert, mark where the arousal marker must fall back below to clear the
	// Warning condition (the warm-side warning boundary). Recovery also needs the cooldown
	// timer, so this is the metric target — "which way, and how far", not the whole story.
	private void DrawRecoveryTarget(ImDrawListPtr draw, Vector2 centre, float halfWidth, float lobeHeight, float confidence)
	{
		if (_pipeline.CurrentState is not (DetectorState.Warning or DetectorState.Alerting))
		{
			return;
		}

		Vector2 gate = LemniscateGeometry.MarkerPoint((float)RegulationFieldCalculator.WarningBoundaryIndex, centre, halfWidth);
		uint goal = Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Green, confidence));
		uint goalDim = Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Green, 0.45f * confidence));

		float ring = 11f + (2f * MathF.Sin(_animTime * 3f));
		draw.AddCircle(gate, ring, goal, 24, 2f);
		draw.AddCircleFilled(gate, 2.5f, goal);

		float ay = gate.Y - (lobeHeight * 0.5f) - 6f;
		draw.AddTriangleFilled(
			new Vector2(gate.X - 11f, ay),
			new Vector2(gate.X - 3f, ay - 5f),
			new Vector2(gate.X - 3f, ay + 5f), goal);

		Vector2 lbl = ImGui.CalcTextSize("RECOVER");
		draw.AddText(new Vector2(gate.X - (lbl.X * 0.5f), ay - ImGui.GetTextLineHeight() - 3f), goalDim, "RECOVER");
	}

	// Marker: X = arousal index (eased), Y = vagal tone (eased) — grounded/low when HRV is
	// healthy, lifted when it collapses. Breathing halo pulses at the current heart rate.
	private void DrawMarker(ImDrawListPtr draw, Vector2 centre, float halfWidth, float liveLobeHeight, float confidence)
	{
		Vector2 p = LemniscateGeometry.MarkerPoint(_markerPos, centre, halfWidth);
		float clamp = liveLobeHeight * MarkerYSpan;
		p.Y += Math.Clamp(_markerY, -clamp, clamp);

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

	private void DrawLabelsAndLock(ImDrawListPtr draw, Vector2 origin, Vector2 centre, float halfWidth, float lobeClearHeight)
	{
		uint rest = Col(MacchiatoPalette.Sapphire);
		uint melt = Col(MacchiatoPalette.Peach);
		uint dim = Col(MacchiatoPalette.Subtext0);

		// Pole labels sit clear of the lobe tips: REST right-aligned just left of the cool
		// tip, MELTDOWN just right of the warm tip, both vertically centred on the tip line.
		// WINDOW OF TOLERANCE is horizontally centred above the tallest possible lobe.
		float lineH = ImGui.GetTextLineHeight();
		float midY = centre.Y - (lineH * 0.5f);
		const float poleGap = 16f;

		Vector2 restSize = ImGui.CalcTextSize("REST");
		draw.AddText(new Vector2(centre.X - halfWidth - poleGap - restSize.X, midY), rest, "REST");
		draw.AddText(new Vector2(centre.X + halfWidth + poleGap, midY), melt, "MELTDOWN");

		Vector2 wotSize = ImGui.CalcTextSize("WINDOW OF TOLERANCE");
		draw.AddText(new Vector2(centre.X - (wotSize.X * 0.5f), centre.Y - lobeClearHeight - lineH - 10f), dim, "WINDOW OF TOLERANCE");

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

	public void Dispose()
	{
		_pipeline.SampleUpdated -= OnSampleUpdated;
		_pipeline.BeatReceived -= OnBeatReceived;
	}
}
