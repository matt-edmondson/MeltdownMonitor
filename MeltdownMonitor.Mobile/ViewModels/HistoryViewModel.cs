using System.Collections.ObjectModel;
using Avalonia.Media;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;
using MeltdownMonitor.Core.Persistence;

namespace MeltdownMonitor.Mobile.ViewModels;

/// <summary>
/// History tab — reads the recent HRV samples from the repository and
/// surfaces them as a chronological list of state transitions. The desktop
/// has no equivalent screen; design doc §6 calls this out as the biggest
/// UX win of going mobile.
/// </summary>
public sealed class HistoryViewModel : ViewModelBase
{
	private readonly string? _databasePath;
	private bool _isLoading;
	private string? _error;

	public HistoryViewModel(string? databasePath = null)
	{
		_databasePath = databasePath;
		LoadCommand = new RelayCommand(() => _ = LoadAsync());
	}

	public ObservableCollection<HistoryEvent> Events { get; } = [];

	public bool IsLoading
	{
		get => _isLoading;
		private set => SetField(ref _isLoading, value);
	}

	public bool IsEmpty => !_isLoading && Events.Count == 0 && _error is null;

	public string? Error
	{
		get => _error;
		private set
		{
			if (SetField(ref _error, value))
			{
				Raise(nameof(IsEmpty));
			}
		}
	}

	public RelayCommand LoadCommand { get; }

	public async Task LoadAsync(TimeSpan? window = null)
	{
		if (_databasePath is null)
		{
			Events.Clear();
			Raise(nameof(IsEmpty));
			return;
		}

		IsLoading = true;
		Error = null;
		try
		{
			var to = DateTimeOffset.UtcNow;
			var from = to - (window ?? TimeSpan.FromHours(24));

			var samples = await Task.Run(
				() => MeltdownRepository.ReadHistory(_databasePath, from, to))
				.ConfigureAwait(true);

			Events.Clear();
			foreach (var ev in BuildTransitions(samples))
			{
				Events.Add(ev);
			}

			Raise(nameof(IsEmpty));
		}
		catch (Exception ex)
		{
			Error = ex.Message;
		}
		finally
		{
			IsLoading = false;
		}
	}

	private static IEnumerable<HistoryEvent> BuildTransitions(IReadOnlyList<HrvSample> samples)
	{
		DetectorState? last = null;
		foreach (var sample in samples)
		{
			if (last != sample.State)
			{
				yield return new HistoryEvent(
					sample.Timestamp,
					sample.State,
					sample.Rmssd,
					sample.BaselineRmssd,
					sample.MeanHr);
				last = sample.State;
			}
		}
	}
}

public sealed record HistoryEvent(
	DateTimeOffset Timestamp,
	DetectorState State,
	double Rmssd,
	double BaselineRmssd,
	double MeanHr)
{
	public string TimeLabel => Timestamp.ToLocalTime().ToString("HH:mm");
	public string DateLabel => Timestamp.ToLocalTime().ToString("ddd MMM d");
	public string StateLabel => State.ToString();
	public IBrush StateBrush => StateColors.BrushFor(State);
	public string Detail => $"RMSSD {Rmssd:F1} / baseline {BaselineRmssd:F1} · HR {MeanHr:F0}";
}
