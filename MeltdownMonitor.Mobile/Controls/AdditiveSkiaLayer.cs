using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace MeltdownMonitor.Mobile.Controls;

/// <summary>
/// An <see cref="ICustomDrawOperation"/> that leases the Skia canvas and runs a caller
/// delegate under a chosen <see cref="SKBlendMode"/>. With the default
/// <see cref="SKBlendMode.Plus"/> it reproduces the desktop Regulation Field's additive glow
/// bloom (which Avalonia's DrawingContext cannot express); pass <see cref="SKBlendMode.SrcOver"/>
/// for plain alpha compositing, letting each glow layer be toggled between glow and flat alpha.
/// The delegate receives the canvas and an <see cref="SKPaint"/> pre-set to the requested blend
/// mode + anti-aliased; the caller sets colour/stroke/style per primitive. On a non-Skia backend
/// (e.g. design-time), the lease is unavailable and the layer simply draws nothing.
/// </summary>
internal sealed class AdditiveSkiaLayer : ICustomDrawOperation
{
	private readonly Action<SKCanvas, SKPaint> _draw;
	private readonly SKBlendMode _blendMode;

	public AdditiveSkiaLayer(Rect bounds, Action<SKCanvas, SKPaint> draw, SKBlendMode blendMode = SKBlendMode.Plus)
	{
		Bounds = bounds;
		_draw = draw;
		_blendMode = blendMode;
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
			BlendMode = _blendMode,
		};
		_draw(lease.SkCanvas, paint);
	}
}
