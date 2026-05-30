using Hexa.NET.ImGui;
using System.Numerics;

namespace MeltdownMonitor.App;

/// <summary>
/// A transparent heads-up overlay: a small, borderless, corner-pinned panel drawn on
/// top of the status window showing a user-selectable set of live metrics. Right-click
/// the panel to choose which metrics appear, the corner, and the opacity.
/// </summary>
public sealed class MetricsOverlay
{
	private const float Margin = 12f;

	/// <summary>
	/// Draws the overlay for the current frame. Returns true when the user changed a
	/// setting via the context menu, so the caller can persist it.
	/// </summary>
	public bool Draw(in OverlaySample sample, OverlaySettings settings)
	{
		if (!settings.Enabled)
		{
			return false;
		}

		var viewport = ImGui.GetMainViewport();
		bool right = settings.Corner is OverlayCorner.TopRight or OverlayCorner.BottomRight;
		bool bottom = settings.Corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight;

		// Pin to the chosen corner: the pivot is the matching corner of the window, and
		// the position is the corner of the work area inset by a small margin.
		Vector2 pivot = new(right ? 1f : 0f, bottom ? 1f : 0f);
		Vector2 pos = new(
			viewport.WorkPos.X + (right ? viewport.WorkSize.X - Margin : Margin),
			viewport.WorkPos.Y + (bottom ? viewport.WorkSize.Y - Margin : Margin));

		ImGui.SetNextWindowPos(pos, ImGuiCond.Always, pivot);
		ImGui.SetNextWindowBgAlpha(settings.BackgroundAlpha);

		ImGuiWindowFlags flags =
			ImGuiWindowFlags.NoDecoration |
			ImGuiWindowFlags.AlwaysAutoResize |
			ImGuiWindowFlags.NoSavedSettings |
			ImGuiWindowFlags.NoFocusOnAppearing |
			ImGuiWindowFlags.NoNav |
			ImGuiWindowFlags.NoDocking;

		// Click-through makes the panel purely cosmetic so the charts under it stay usable.
		if (settings.ClickThrough)
		{
			flags |= ImGuiWindowFlags.NoInputs;
		}

		bool changed = false;
		if (ImGui.Begin("##metrics-overlay", flags))
		{
			DrawMetrics(sample, settings);
			changed = DrawContextMenu(settings);
		}

		ImGui.End();
		return changed;
	}

	private static void DrawMetrics(in OverlaySample sample, OverlaySettings settings)
	{
		if (settings.Metrics.Count == 0)
		{
			ImGui.TextDisabled("No metrics — right-click");
			return;
		}

		foreach (var metric in settings.Metrics)
		{
			ImGui.TextDisabled($"{OverlayMetrics.Label(metric)}:");
			ImGui.SameLine();

			string value = OverlayMetrics.Format(metric, sample);
			if (metric == OverlayMetric.State)
			{
				ImGui.TextColored(StateColors.For(sample.State), value);
			}
			else
			{
				ImGui.Text(value);
			}
		}
	}

	private static bool DrawContextMenu(OverlaySettings settings)
	{
		// A NoInputs window can't host a popup, so the menu is only reachable when the
		// overlay is interactive (click-through off).
		if (settings.ClickThrough)
		{
			return false;
		}

		bool changed = false;
		if (ImGui.BeginPopupContextWindow("overlay-menu"))
		{
			if (ImGui.BeginMenu("Metrics"))
			{
				foreach (var metric in OverlayMetrics.All)
				{
					bool shown = settings.Metrics.Contains(metric);
					if (ImGui.MenuItem(OverlayMetrics.Label(metric), shown))
					{
						if (shown)
						{
							settings.Metrics.Remove(metric);
						}
						else
						{
							settings.Metrics.Add(metric);
						}

						changed = true;
					}
				}

				ImGui.EndMenu();
			}

			if (ImGui.BeginMenu("Corner"))
			{
				foreach (var corner in Enum.GetValues<OverlayCorner>())
				{
					if (ImGui.MenuItem(corner.ToString(), settings.Corner == corner))
					{
						settings.Corner = corner;
						changed = true;
					}
				}

				ImGui.EndMenu();
			}

			if (ImGui.MenuItem("Click-through", settings.ClickThrough))
			{
				settings.ClickThrough = !settings.ClickThrough;
				changed = true;
			}

			ImGui.Separator();
			if (ImGui.MenuItem("Hide overlay"))
			{
				settings.Enabled = false;
				changed = true;
			}

			ImGui.EndPopup();
		}

		return changed;
	}
}
