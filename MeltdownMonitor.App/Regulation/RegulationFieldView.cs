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
	private const int RrBufferLength = 160;      // recent RR intervals for the live trace texture
	private const int MinRrForJitter = 8;        // below this, draw a smooth (flat) trace
	private const int LobeSegments = 96;
	private const float LobeSwellFactor = 1.4f;  // how much a lobe thickens at full index
	private const float MaxJitterPx = 18f;       // peak trace deflection from the real RR signal at 1× exaggeration
	private const float RrDevScaleMs = 30f;      // beat-to-beat difference that maps to full deflection
	private const float LobeHeightMin = 0.7f;    // live lobe height factor at lowest Poincaré roundness
	private const float LobeHeightMax = 1.18f;   // ...and at highest
	private const float MarkerYSpan = 0.8f;      // vagal-tone vertical travel, as a fraction of lobe height

	private readonly Pipeline _pipeline;
	private readonly object _lock = new();
	private readonly List<RegulationTrailPoint> _trail = [];
	private readonly double[] _rr = new double[RrBufferLength];
	private int _rrCount;
	private long _beatsAppended;   // total non-artifact beats ever appended; the absolute timeline the texture scrolls along

	// Interpolation state: render one sample behind, holding from→to and lerping the whole
	// reading across the measured inter-sample interval so reading-derived visuals move
	// smoothly, arriving as the next sample lands. Guarded by _lock (_display is written on the
	// UI thread and read on the sample thread to seed the next segment).
	private RegulationReading _from = new(0, 1, 0, 0.5, 0);
	private RegulationReading _to = new(0, 1, 0, 0.5, 0);
	private RegulationReading _display = new(0, 1, 0, 0.5, 0);
	private DateTimeOffset _segStart;
	private DateTimeOffset _lastArrival;
	private double _segDuration = 5.0;
	private double _intervalEwma = 5.0;   // running-average inter-sample interval

	// Animation state (UI thread only).
	private float _breathPhase;
	private float _animTime;
	private float _hrDisplay = 60f;          // eased HR for a smooth breathing cadence
	private RrTexturePlayhead _playhead;     // free-running smooth scroll for the live RR texture
	private float _arrowSpeed;               // eased displayed normalized speed for the velocity arrow
	private float _drawScale = 1f;           // indicator size multiplier; grows with the field, never below 1

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
			var now = DateTimeOffset.UtcNow;

			// Start a new interpolation segment from the currently displayed state to this
			// reading. Span slightly longer than the running-average interval so the motion is
			// still in flight (not finished and waiting) when the next sample lands; the
			// from=_display handoff keeps it seamless if the previous segment hadn't completed.
			if (_lastArrival != default)
			{
				double interval = Math.Clamp((now - _lastArrival).TotalSeconds, 0.5, 30.0);
				_intervalEwma = (_intervalEwma * 0.7) + (interval * 0.3);
			}
			_segDuration = _intervalEwma * 1.15;
			_from = _display;
			_to = reading;
			_segStart = now;
			_lastArrival = now;

			// Capture the detector state with the point so each segment keeps the colour it
			// was drawn in, rather than the whole trail recolouring as the state advances.
			_trail.Add(new RegulationTrailPoint(reading, sample.State));
			int cap = _pipeline.RegulationTrailLength;
			while (_trail.Count > cap)
			{
				_trail.RemoveAt(0);
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

			// Advance the absolute beat timeline. The texture playhead scrolls along this; it
			// advances smoothly by frame time (not by beat arrival), so the texture stays fluid
			// even though beats land in irregular BLE batches.
			_beatsAppended++;
		}
	}

	private (RegulationReading from, RegulationReading to, DateTimeOffset segStart, double segDuration, RegulationTrailPoint[] trail, double[] rr, long beatsAppended) Snapshot()
	{
		lock (_lock)
		{
			var trail = _trail.ToArray();

			var rr = new double[_rrCount];
			Array.Copy(_rr, rr, _rrCount);

			// Sample the beat count under the same lock as rr so the two stay consistent — the
			// texture maps the lobe using both, and an off-by-one between them would jolt it.
			return (_from, _to, _segStart, _segDuration, trail, rr, _beatsAppended);
		}
	}

	public void Draw()
	{
		var (from, to, segStart, segDuration, trail, rr, beatsAppended) = Snapshot();
		float dt = ImGui.GetIO().DeltaTime;
		_animTime += dt;

		// Render one sample behind: lerp the whole reading from the previous sample to the
		// latest across the measured interval (smoothstep), so every reading-derived visual
		// moves continuously and arrives just as the next sample lands.
		double prog = segDuration > 0 ? Math.Clamp((DateTimeOffset.UtcNow - segStart).TotalSeconds / segDuration, 0.0, 1.0) : 1.0;
		float k = (float)(prog * prog * (3.0 - (2.0 * prog)));
		RegulationReading disp = LerpReading(from, to, k);
		lock (_lock)
		{
			_display = disp; // seeds the next segment's "from"
		}

		// Fill the available window space.
		Vector2 avail = ImGui.GetContentRegionAvail();
		float height = MathF.Max(120f, avail.Y - 8f);
		float width = avail.X;
		Vector2 origin = ImGui.GetCursorScreenPos();
		ImGui.Dummy(new Vector2(width, height)); // reserve layout space

		var draw = ImGui.GetWindowDrawList();
		Vector2 centre = origin + new Vector2(width * 0.5f, height * 0.46f);
		float halfWidth = width * 0.34f;
		// Indicators (marker, crossover, arrow, labels, strokes) are tuned in pixels for a ~240px
		// half-width field; scale them up so they stay legible as the widget grows, never below 1x.
		_drawScale = MathF.Max(1f, halfWidth / 240f);
		float baseLobeHeight = MathF.Min(height * 0.28f, halfWidth * 0.62f);

		// Poincaré SD1/SD2 ratio shapes the live lobe height; the ghost stays at base height.
		float roundness = (float)disp.LobeRoundness;
		float liveLobeHeight = baseLobeHeight * (LobeHeightMin + ((LobeHeightMax - LobeHeightMin) * roundness));
		float labelClearHeight = baseLobeHeight * LobeHeightMax; // keep labels clear of the tallest lobe
		float markerYClamp = liveLobeHeight * MarkerYSpan;

		float confidence = (float)disp.Confidence;

		// Ease displayed HR so the breathing pulse rate doesn't step every sample.
		float hrTarget = (float)(_pipeline.LatestSample?.MeanHr ?? 60.0);
		_hrDisplay += (hrTarget - _hrDisplay) * (1f - MathF.Exp(-dt * 1.5f));
		_breathPhase += dt * (MathF.Max(40f, _hrDisplay) / 60f) * MathF.Tau;

		// RR-texture scroll: a free-running playhead over the absolute beat timeline. It dead-reckons
		// forward at the real beat rate (constant velocity → flows like the breathing pulse) and is
		// gently corrected toward the newest sample, decoupling the visual from the irregular, batched
		// arrival of beats over BLE. Driven purely by frame time — never reset or clamped per beat.
		_playhead.Advance(dt, MathF.Max(40f, _hrDisplay) / 60f, beatsAppended - 1);

		// Ease the arrow magnitude toward the latest dynamics so it grows/shrinks smoothly.
		var dynamics = _pipeline.LatestDynamics;
		_arrowSpeed += ((float)dynamics.NormalizedSpeed - _arrowSpeed) * (1f - MathF.Exp(-dt * 6f));

		DrawLfHfHalo(draw, centre, halfWidth, disp, confidence);
		DrawWindowOfTolerance(draw, centre, halfWidth, baseLobeHeight, confidence);
		DrawVagalAxis(draw, centre, markerYClamp, confidence);
		DrawAxisHistograms(draw, origin, centre, halfWidth, labelClearHeight, markerYClamp, height, trail, confidence);
		DrawLemniscate(draw, centre, halfWidth, baseLobeHeight, liveLobeHeight, disp, rr, _playhead.Position, beatsAppended, confidence);
		DrawTrail(draw, centre, halfWidth, liveLobeHeight, trail, disp, confidence);
		DrawRecoveryTarget(draw, centre, halfWidth, liveLobeHeight, confidence);
		DrawMarker(draw, centre, halfWidth, liveLobeHeight, disp, confidence);
		DrawVelocityArrow(draw, centre, halfWidth, liveLobeHeight, disp, dynamics, confidence);
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

	private void DrawLemniscate(ImDrawListPtr draw, Vector2 centre, float halfWidth, float baseLobeHeight, float liveLobeHeight, RegulationReading r, double[] rr, double playhead, long beatsAppended, float confidence)
	{
		// Ghost baseline (symmetric resting frame) at the base height.
		var ghost = LemniscateGeometry.Polyline(centre, halfWidth, baseLobeHeight, LobeSegments);
		uint ghostCol = Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Overlay1, 0.28f * confidence));
		for (int i = 0; i < ghost.Count; i++)
		{
			draw.AddLine(ghost[i], ghost[(i + 1) % ghost.Count], ghostCol, 2f * _drawScale);
		}

		// Live two-tone trace at the Poincaré-shaped height, textured with the real signal.
		var live = LemniscateGeometry.Polyline(centre, halfWidth, liveLobeHeight, LobeSegments);
		float[] dev = BuildRrDeviations(rr);
		float warmSwell = 1f + (MathF.Max(0f, (float)r.Index) * LobeSwellFactor);
		float coolSwell = 1f + (MathF.Max(0f, -(float)r.Index) * LobeSwellFactor);
		float baseThick = (4f + (6f * (float)r.VariabilityQuality)) * (float)_pipeline.LobeThickness * _drawScale;
		int n = live.Count;

		// Jitter each VERTEX once (along the smoothed vertex normal) so adjacent segments
		// share an endpoint and the trace stays continuous. The trace IS the live beat-to-beat
		// signal: jagged when HRV is healthy, flat when it collapses; tapers to nothing at the crossover.
		// Map the lobe ONCE onto a window of the RR signal (no tiling → no spatial repeat),
		// rotating so the oldest↔newest seam lands on a crossover (depth≈0, hidden). The window's
		// leading edge is the smooth `playhead` (in absolute beat-index units); converting it to a
		// buffer index lets the texture flow continuously without snapping to beat arrivals.
		int devLen = dev.Length;
		int quarter = n / 4;
		double span = Math.Min(devLen - 1, n - 1);
		// Absolute beat index → buffer index: the newest buffer sample (devLen-1) is absolute (beatsAppended-1).
		double bufOffset = devLen - beatsAppended;
		var pts = new Vector2[n];
		for (int i = 0; i < n; i++)
		{
			Vector2 v = live[i];
			float depth = MathF.Min(1f, MathF.Abs(v.X - centre.X) / halfWidth);
			float jitter = 0f;
			if (devLen > 1)
			{
				int seg = (((i - quarter) % n) + n) % n;
				double posAbs = (playhead - span) + ((seg / (double)(n - 1)) * span);
				double posBuf = Math.Clamp(posAbs + bufOffset, 0.0, devLen - 1);
				int i0 = (int)Math.Floor(posBuf);
				int i1 = Math.Min(i0 + 1, devLen - 1);
				float d = dev[i0] + ((float)(dev[i1] - dev[i0]) * (float)(posBuf - i0));
				jitter = d * MaxJitterPx * (float)_pipeline.JitterExaggeration * _drawScale * depth;
			}
			Vector2 normal = Normal(live[(i - 1 + n) % n], live[(i + 1) % n]);
			pts[i] = v + (normal * jitter);
		}

		// Draw a closed Catmull-Rom spline through the jittered vertices: smooth flowing
		// undulations rather than faceted spikes. The continuous chain leaves no segment
		// gaps. Colour/thickness come from each sub-point's side of the crossover.
		const int sub = 4;
		for (int i = 0; i < n; i++)
		{
			Vector2 p0 = pts[(i - 1 + n) % n];
			Vector2 p1 = pts[i];
			Vector2 p2 = pts[(i + 1) % n];
			Vector2 p3 = pts[(i + 2) % n];

			Vector2 prev = p1;
			for (int s = 1; s <= sub; s++)
			{
				Vector2 cur = CatmullRom(p0, p1, p2, p3, s / (float)sub);
				float midX = (prev.X + cur.X) * 0.5f;
				bool warm = midX >= centre.X;
				float depth = MathF.Min(1f, MathF.Abs(midX - centre.X) / halfWidth);

				Vector4 c = warm
					? MacchiatoPalette.Lerp(MacchiatoPalette.Peach, MacchiatoPalette.Maroon, depth)
					: MacchiatoPalette.Lerp(MacchiatoPalette.Sky, MacchiatoPalette.Sapphire, depth);
				c = MacchiatoPalette.WithAlpha(c, confidence);

				float thick = baseThick * (warm ? warmSwell : coolSwell);
				uint col = Col(c);
				draw.AddLine(prev, cur, col, thick);
				draw.AddCircleFilled(cur, thick * 0.5f, col); // round join — fills butt-cap notches at bends
				prev = cur;
			}
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

	private void DrawTrail(ImDrawListPtr draw, Vector2 centre, float halfWidth, float liveLobeHeight, RegulationTrailPoint[] trail, RegulationReading disp, float confidence)
	{
		if (trail.Length < 2)
		{
			return;
		}

		// Map every trail reading to its 2D field position, using the same X = arousal index,
		// Y = vagal tone mapping as the live marker.
		float clamp = liveLobeHeight * MarkerYSpan;
		var pts = new Vector2[trail.Length];
		for (int i = 0; i < trail.Length; i++)
		{
			Vector2 p = LemniscateGeometry.MarkerPoint((float)trail[i].Reading.Index, centre, halfWidth);
			float yOff = ((float)trail[i].Reading.VariabilityQuality - 0.5f) * liveLobeHeight * MarkerYSpan;
			p.Y += Math.Clamp(yOff, -clamp, clamp);
			pts[i] = p;
		}

		// The head of the tail is the interpolated marker position, not the latest raw sample,
		// so the last segment lands exactly on the marker.
		Vector2 head = LemniscateGeometry.MarkerPoint((float)disp.Index, centre, halfWidth);
		float headY = ((float)disp.VariabilityQuality - 0.5f) * liveLobeHeight * MarkerYSpan;
		head.Y += Math.Clamp(headY, -clamp, clamp);
		pts[^1] = head;

		// Join the points into one smooth comet tail with a Catmull-Rom spline through them:
		// oldest faint → newest bright, thickening toward the head.
		const int sub = 8;
		int count = pts.Length;
		for (int i = 0; i < count - 1; i++)
		{
			Vector2 p0 = pts[Math.Max(0, i - 1)];
			Vector2 p1 = pts[i];
			Vector2 p2 = pts[i + 1];
			Vector2 p3 = pts[Math.Min(count - 1, i + 2)];

			// Each segment keeps the colour of the state it was captured under, so the trail
			// records the journey through states rather than recolouring to the current one.
			Vector4 segBase = MacchiatoPalette.State(trail[i].State);

			Vector2 prev = p1;
			for (int s = 1; s <= sub; s++)
			{
				float t = s / (float)sub;
				Vector2 cur = CatmullRom(p0, p1, p2, p3, t);
				float frac = (i + t) / (count - 1);
				float width = (1f + (2.5f * frac)) * _drawScale;
				// Leading edge (newest, frac->1) brightens with speed and tints by trend so the
				// comet visibly "leans" the way arousal is heading; older segments keep their own colour.
				Vector4 segCol = _pipeline.LatestDynamics.Trend switch
				{
					RegulationTrend.Escalating => MacchiatoPalette.Lerp(segBase, MacchiatoPalette.Peach, frac * _arrowSpeed),
					RegulationTrend.DeEscalating => MacchiatoPalette.Lerp(segBase, MacchiatoPalette.Sky, frac * _arrowSpeed),
					_ => segBase,
				};
				float segAlpha = (0.55f + (0.3f * _arrowSpeed)) * frac * confidence;
				draw.AddLine(prev, cur, Col(MacchiatoPalette.WithAlpha(segCol, segAlpha)), width);
				prev = cur;
			}
		}
	}

	// Uniform Catmull-Rom interpolation between p1 and p2 (p0/p3 are the neighbouring points).
	private static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
	{
		float t2 = t * t;
		float t3 = t2 * t;
		return 0.5f * ((2f * p1)
			+ ((-p0 + p2) * t)
			+ (((2f * p0) - (5f * p1) + (4f * p2) - p3) * t2)
			+ ((-p0 + (3f * p1) - (3f * p2) + p3) * t3));
	}

	// Field-wise linear interpolation between two readings.
	private static RegulationReading LerpReading(RegulationReading a, RegulationReading b, float t) => new(
		a.Index + ((b.Index - a.Index) * t),
		a.VariabilityQuality + ((b.VariabilityQuality - a.VariabilityQuality) * t),
		a.Confidence + ((b.Confidence - a.Confidence) * t),
		a.LobeRoundness + ((b.LobeRoundness - a.LobeRoundness) * t),
		a.LfHfBalance + ((b.LfHfBalance - a.LfHfBalance) * t));

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

		float ring = (11f + (2f * MathF.Sin(_animTime * 3f))) * _drawScale;
		draw.AddCircle(gate, ring, goal, 24, 2f * _drawScale);
		draw.AddCircleFilled(gate, 2.5f * _drawScale, goal);

		float ay = gate.Y - (lobeHeight * 0.5f) - (6f * _drawScale);
		draw.AddTriangleFilled(
			new Vector2(gate.X - (11f * _drawScale), ay),
			new Vector2(gate.X - (3f * _drawScale), ay - (5f * _drawScale)),
			new Vector2(gate.X - (3f * _drawScale), ay + (5f * _drawScale)), goal);

		Vector2 lbl = ImGui.CalcTextSize("RECOVER");
		draw.AddText(new Vector2(gate.X - (lbl.X * 0.5f), ay - ImGui.GetTextLineHeight() - 3f), goalDim, "RECOVER");
	}

	private static Vector2 MarkerScreenPos(Vector2 centre, float halfWidth, float liveLobeHeight, RegulationReading disp)
	{
		Vector2 p = LemniscateGeometry.MarkerPoint((float)disp.Index, centre, halfWidth);
		float clamp = liveLobeHeight * MarkerYSpan;
		float yOff = ((float)disp.VariabilityQuality - 0.5f) * liveLobeHeight * MarkerYSpan;
		p.Y += Math.Clamp(yOff, -clamp, clamp);
		return p;
	}

	private void DrawVelocityArrow(ImDrawListPtr draw, Vector2 centre, float halfWidth, float liveLobeHeight, RegulationReading disp, RegulationDynamics dyn, float confidence)
	{
		if (confidence < 0.999f || dyn.Trend == RegulationTrend.Steady || _arrowSpeed < 0.02f)
		{
			return;
		}

		Vector2 p = MarkerScreenPos(centre, halfWidth, liveLobeHeight, disp);
		float dir = dyn.Trend == RegulationTrend.Escalating ? 1f : -1f;
		Vector4 hue = dyn.Trend == RegulationTrend.Escalating ? MacchiatoPalette.Peach : MacchiatoPalette.Sky;
		float alpha = confidence * (0.35f + (0.65f * _arrowSpeed));
		uint col = Col(MacchiatoPalette.WithAlpha(hue, alpha));

		float gap = 12f * _drawScale;
		float len = (10f + (_arrowSpeed * 46f)) * _drawScale;
		Vector2 start = p + new Vector2(dir * gap, 0f);
		Vector2 tip = start + new Vector2(dir * len, 0f);
		draw.AddLine(start, tip, col, 3f * _drawScale);

		float head = 7f * _drawScale;
		draw.AddTriangleFilled(
			tip,
			tip + new Vector2(-dir * head, -head * 0.7f),
			tip + new Vector2(-dir * head, head * 0.7f),
			col);
	}

	// Marker: X = arousal index (eased), Y = vagal tone (eased) — grounded/low when HRV is
	// healthy, lifted when it collapses. Breathing halo pulses at the current heart rate.
	private void DrawMarker(ImDrawListPtr draw, Vector2 centre, float halfWidth, float liveLobeHeight, RegulationReading disp, float confidence)
	{
		Vector2 p = MarkerScreenPos(centre, halfWidth, liveLobeHeight, disp);

		Vector4 stateCol = MacchiatoPalette.State(_pipeline.CurrentState);
		float pulse = 1f + (0.18f * MathF.Sin(_breathPhase));
		draw.AddCircleFilled(p, 16f * _drawScale * pulse, Col(MacchiatoPalette.WithAlpha(stateCol, 0.18f * confidence)));
		draw.AddCircleFilled(p, 6.5f * _drawScale, Col(MacchiatoPalette.WithAlpha(stateCol, confidence)));
		draw.AddCircleFilled(p, 2.6f * _drawScale, Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Base, confidence)));
	}

	private void DrawCrossover(ImDrawListPtr draw, Vector2 centre, float confidence)
	{
		draw.AddCircleFilled(centre, 7f * _drawScale, Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Lavender, confidence)));
		draw.AddCircleFilled(centre, 3f * _drawScale, Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Text, confidence)));
	}

	// Vertical-axis legend for the marker's Y dimension (vagal tone / HRV amount): the marker
	// rides low when HRV is healthy (steady) and lifts as it collapses (fragile).
	private void DrawVagalAxis(ImDrawListPtr draw, Vector2 centre, float yClamp, float confidence)
	{
		uint axis = Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Overlay1, 0.22f * confidence));
		uint label = Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Subtext0, 0.8f * confidence));
		float lineH = ImGui.GetTextLineHeight();

		draw.AddLine(new Vector2(centre.X, centre.Y - yClamp), new Vector2(centre.X, centre.Y + yClamp), axis, 1f * _drawScale);

		Vector2 fr = ImGui.CalcTextSize("FRAGILE");
		Vector2 st = ImGui.CalcTextSize("STEADY");
		draw.AddText(new Vector2(centre.X - (fr.X * 0.5f), centre.Y - yClamp - lineH - 2f), label, "FRAGILE");
		draw.AddText(new Vector2(centre.X - (st.X * 0.5f), centre.Y + yClamp + 2f), label, "STEADY");
	}

	// Axis density histograms: how the samples currently in the trail window are distributed.
	// X (arousal index) is a row of vertical bars below the field, each column aligned with the
	// index it counts — left=cool/REST, right=warm/MELTDOWN, echoing the lobe colours. Y
	// (variability quality) is a column of horizontal bars on the left margin, each row aligned
	// with the marker's vagal-tone height — top=FRAGILE (low quality), bottom=STEADY (high).
	private void DrawAxisHistograms(ImDrawListPtr draw, Vector2 origin, Vector2 centre, float halfWidth, float lobeClearHeight, float markerYClamp, float height, RegulationTrailPoint[] trail, float confidence)
	{
		if (trail.Length < 2)
		{
			return;
		}

		var xHist = RegulationFieldHistogram.IndexAxis(trail);
		var yHist = RegulationFieldHistogram.QualityAxis(trail, 16);
		uint axisCol = Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Overlay1, 0.22f * confidence));

		// X axis (arousal index), below the field, bars growing downward from a baseline that
		// clears the lowest lobe tip. Clamp the strip so it never collides with the readout.
		if (xHist.PeakCount > 0)
		{
			float baseY = centre.Y + lobeClearHeight + (16f * _drawScale);
			float maxH = MathF.Max(0f, MathF.Min(22f * _drawScale, (origin.Y + height - 26f) - baseY));
			if (maxH > 1f)
			{
				int n = xHist.BucketCount;
				float slot = (halfWidth * 2f) / n;
				float barW = MathF.Max(1f, slot - (1.5f * _drawScale));
				draw.AddLine(new Vector2(centre.X - halfWidth, baseY), new Vector2(centre.X + halfWidth, baseY), axisCol, 1f * _drawScale);
				for (int b = 0; b < n; b++)
				{
					int c = xHist.Counts[b];
					if (c == 0)
					{
						continue;
					}

					float bx = centre.X - halfWidth + ((b + 0.5f) * slot);
					Vector4 hue = bx >= centre.X ? MacchiatoPalette.Peach : MacchiatoPalette.Sky;
					uint col = Col(MacchiatoPalette.WithAlpha(hue, 0.55f * confidence));
					float bh = maxH * (c / (float)xHist.PeakCount);
					draw.AddRectFilled(new Vector2(bx - (barW * 0.5f), baseY), new Vector2(bx + (barW * 0.5f), baseY + bh), col);
				}
			}
		}

		// Y axis (variability quality), on the left margin, bars growing rightward. The vertical
		// position matches the marker's vagal-tone mapping: quality 0 (FRAGILE) at top, 1 (STEADY)
		// at bottom — i.e. the same span the vagal axis labels bracket.
		if (yHist.PeakCount > 0)
		{
			float axisX = origin.X + (4f * _drawScale);
			float maxW = MathF.Max(0f, MathF.Min(22f * _drawScale, (centre.X - halfWidth - 40f) - axisX));
			if (maxW > 1f)
			{
				int n = yHist.BucketCount;
				float topY = centre.Y - markerYClamp;
				float botY = centre.Y + markerYClamp;
				float slot = (botY - topY) / n;
				float barH = MathF.Max(1f, slot - (1.5f * _drawScale));
				draw.AddLine(new Vector2(axisX, topY), new Vector2(axisX, botY), axisCol, 1f * _drawScale);
				for (int b = 0; b < n; b++)
				{
					int c = yHist.Counts[b];
					if (c == 0)
					{
						continue;
					}

					float by = topY + ((b + 0.5f) * slot);
					uint col = Col(MacchiatoPalette.WithAlpha(MacchiatoPalette.Lavender, 0.55f * confidence));
					float bw = maxW * (c / (float)yHist.PeakCount);
					draw.AddRectFilled(new Vector2(axisX, by - (barH * 0.5f)), new Vector2(axisX + bw, by + (barH * 0.5f)), col);
				}
			}
		}
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
		var dyn = _pipeline.LatestDynamics;
		string trend = dyn.Trend switch
		{
			RegulationTrend.Escalating => $"^ escalating {dyn.Velocity:+0.00;-0.00}/s",
			RegulationTrend.DeEscalating => $"v easing {dyn.Velocity:+0.00;-0.00}/s",
			_ => "- steady",
		};
		ImGui.Text($"HR {s.MeanHr:F0} bpm    RMSSD {s.Rmssd:F0} ms ({rel})    {_pipeline.CurrentState}    {trend}");
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
