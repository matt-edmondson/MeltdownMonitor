using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MeltdownMonitor.Mobile.Converters;

/// <summary>
/// Maps a boolean to an opacity (true → 1, false → 0). Unlike <c>IsVisible</c>,
/// which collapses an element out of the layout, an element hidden via opacity
/// keeps its measured size — so toggling it does not reflow its neighbours.
/// Used by the Now screen's trend/recovery readouts to reserve their space.
/// </summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
	public static readonly BoolToOpacityConverter Instance = new();

	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> value is true ? 1.0 : 0.0;

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotSupportedException();
}
