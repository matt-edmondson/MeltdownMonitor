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
	private string? _databasePath;
	private bool _isLoading;
	private string? _error;

	public HistoryViewModel(string? databasePath = null)
	{
		_databasePath = databasePath;
		LoadCommand = new RelayCommand(() => _ = LoadAsync());
	}

	/// <summary>
	/// Point the history list at the live database. The iOS head calls this
	/// once <c>BuildAndStartPipelineAsync</c> has resolved the sandbox path
	/// (design doc §6.1) — the VM is constructed before that path is known.
	/// </summary>
	public void UseDatabase(string databasePath)
	{
		_databasePath = databasePath;
		_ = LoadAsync();
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

			var path = _databasePath;
			var samples = await Task.Run(
				() => MeltdownRepository.ReadHistory(path, from, to))
				.ConfigureAwait(true);

			// Annotations are additive context, not the backbone of the list — a
			// read failure (e.g. the table doesn't exist on a brand-new DB) must
			// not blank out the state timeline, so it degrades to "no check-ins".
			var annotations = await Task.Run(
				() => ReadAnnotationsSafe(path, from, to))
				.ConfigureAwait(true);

			var merged = BuildTransitions(samples)
				.Concat(annotations.Select(a => HistoryEvent.ForAnnotation(a.Timestamp, a.Label, a.Notes)))
				.OrderBy(e => e.Timestamp)
				.ToList();

			Events.Clear();
			foreach (var ev in merged)
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
				yield return HistoryEvent.ForStateChange(
					sample.Timestamp,
					sample.State,
					sample.Rmssd,
					sample.BaselineRmssd,
					sample.MeanHr);
				last = sample.State;
			}
		}
	}

	private static IReadOnlyList<AnnotationRecord> ReadAnnotationsSafe(
		string databasePath, DateTimeOffset from, DateTimeOffset to)
	{
		try
		{
			return MeltdownRepository.ReadAnnotations(databasePath, from, to);
		}
		catch
		{
			return [];
		}
	}
}

public enum HistoryEventKind
{
	StateChange,
	Annotation,
}

/// <summary>
/// One row in the History timeline — either a detector state transition or a
/// user self check-in. A single record (rather than a class hierarchy) keeps
/// the Avalonia <c>DataTemplate</c> to one binding surface; the display
/// properties switch on <see cref="Kind"/>.
/// </summary>
public sealed record HistoryEvent
{
	private static readonly IBrush AnnotationBrush =
		new SolidColorBrush(Color.FromRgb(0x3A, 0xA0, 0x8A));

	public required DateTimeOffset Timestamp { get; init; }
	public required HistoryEventKind Kind { get; init; }

	public DetectorState State { get; init; }
	public double Rmssd { get; init; }
	public double BaselineRmssd { get; init; }
	public double MeanHr { get; init; }

	public AnnotationLabel AnnotationLabel { get; init; }
	public string? Notes { get; init; }

	public static HistoryEvent ForStateChange(
		DateTimeOffset timestamp, DetectorState state, double rmssd, double baselineRmssd, double meanHr) =>
		new()
		{
			Timestamp = timestamp,
			Kind = HistoryEventKind.StateChange,
			State = state,
			Rmssd = rmssd,
			BaselineRmssd = baselineRmssd,
			MeanHr = meanHr,
		};

	public static HistoryEvent ForAnnotation(DateTimeOffset timestamp, AnnotationLabel label, string? notes) =>
		new()
		{
			Timestamp = timestamp,
			Kind = HistoryEventKind.Annotation,
			AnnotationLabel = label,
			Notes = notes,
		};

	public string TimeLabel => Timestamp.ToLocalTime().ToString("HH:mm");
	public string DateLabel => Timestamp.ToLocalTime().ToString("ddd MMM d");

	public string Title => Kind == HistoryEventKind.Annotation
		? $"You felt {AnnotationLabel}"
		: State.ToString();

	public IBrush StateBrush => Kind == HistoryEventKind.Annotation
		? AnnotationBrush
		: StateColors.BrushFor(State);

	public string Detail => Kind == HistoryEventKind.Annotation
		? (string.IsNullOrWhiteSpace(Notes) ? "Self check-in" : Notes!)
		: $"RMSSD {Rmssd:F1} / baseline {BaselineRmssd:F1} · HR {MeanHr:F0}";
}
