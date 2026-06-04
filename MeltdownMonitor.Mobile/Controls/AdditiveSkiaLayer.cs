using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace MeltdownMonitor.Mobile.Controls;

/// <summary>
/// An <see cref="ICustomDrawOperation"/> that leases the Skia canvas and runs a caller
/// delegate with additive (<see cref="SKBlendMode.Plus"/>) compositing — the only way to
/// reproduce the desktop Regulation Field's glow bloom, which Avalonia's DrawingContext
/// cannot express. The delegate receives the canvas and an <see cref="SKPaint"/> pre-set to
/// additive + anti-aliased; the caller sets colour/stroke/style per primitive. On a non-Skia
/// backend (e.g. design-time), the lease is unavailable and the layer simply draws nothing.
/// </summary>
internal sealed class AdditiveSkiaLayer : ICustomDrawOperation
{
	private readonly Action<SKCanvas, SKPaint> _draw;

	public AdditiveSkiaLayer(Rect bounds, Action<SKCanvas, SKPaint> draw)
	{
		Bounds = bounds;
		_draw = draw;
	}

	public Rect Bounds { get; }

	public bool HitTest(Point p) => false;

	public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);

	public void Dispose()
	{
		// Nothing to release; the delegate owns no unmanaged state beyond the paint it is handed.
	}

	public void Render(ImmediateDrawingContext context)
	{
		var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
		if (leaseFeature is null)
		{
			return;
		}

		using var lease = leaseFeature.Lease();
		using var paint = new SKPaint
		{
			IsAntialias = true,
			BlendMode = SKBlendMode.Plus,
		};
		_draw(lease.SkCanvas, paint);
	}
}
