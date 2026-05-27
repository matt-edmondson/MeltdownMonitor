using Microsoft.Data.Sqlite;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Core.Persistence;

public class MeltdownRepository : IDisposable
{
	private readonly SqliteConnection _connection;

	public MeltdownRepository(string databasePath)
	{
		_connection = new SqliteConnection($"Data Source={databasePath}");
		_connection.Open();
		EnsureSchema();
	}

	private void EnsureSchema()
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = """
			CREATE TABLE IF NOT EXISTS beats (
				ts       INTEGER PRIMARY KEY,
				rr_ms    REAL    NOT NULL,
				hr_bpm   INTEGER NOT NULL,
				artifact INTEGER NOT NULL
			);
			CREATE TABLE IF NOT EXISTS hrv_samples (
				ts             INTEGER PRIMARY KEY,
				rmssd          REAL,
				pnn50          REAL,
				mean_hr        REAL,
				baseline_rmssd REAL,
				baseline_hr    REAL,
				state          TEXT NOT NULL
			);
			CREATE TABLE IF NOT EXISTS alerts (
				ts                  INTEGER PRIMARY KEY,
				trigger_reason      TEXT    NOT NULL,
				rmssd_at_trigger    REAL,
				baseline_at_trigger REAL
			);
			CREATE TABLE IF NOT EXISTS annotations (
				ts    INTEGER PRIMARY KEY,
				label TEXT    NOT NULL,
				notes TEXT
			);
			""";
		cmd.ExecuteNonQuery();

		// Migrate: add extended HRV columns to hrv_samples if this is an older database
		MigrateHrvSamples();
	}

	private void MigrateHrvSamples()
	{
		var existing = GetColumnNames("hrv_samples");
		var toAdd = new (string col, string type)[]
		{
			("lf_power_ms2",   "REAL"),
			("hf_power_ms2",   "REAL"),
			("lf_hf_ratio",    "REAL"),
			("sd1",            "REAL"),
			("sd2",            "REAL"),
			("sd1_sd2_ratio",  "REAL"),
			("sdnn",           "REAL"),
		};

		foreach (var (col, type) in toAdd)
		{
			if (!existing.Contains(col))
			{
				using var cmd = _connection.CreateCommand();
				cmd.CommandText = $"ALTER TABLE hrv_samples ADD COLUMN {col} {type}";
				cmd.ExecuteNonQuery();
			}
		}
	}

	private HashSet<string> GetColumnNames(string tableName)
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = $"PRAGMA table_info({tableName})";
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		using var reader = cmd.ExecuteReader();
		while (reader.Read())
		{
			names.Add(reader.GetString(1));
		}

		return names;
	}

	public void InsertBeat(Beat beat)
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = """
			INSERT OR IGNORE INTO beats (ts, rr_ms, hr_bpm, artifact)
			VALUES ($ts, $rr, $hr, $artifact)
			""";
		cmd.Parameters.AddWithValue("$ts", beat.Timestamp.ToUnixTimeMilliseconds());
		cmd.Parameters.AddWithValue("$rr", beat.RrMs);
		cmd.Parameters.AddWithValue("$hr", beat.HeartRateBpm);
		cmd.Parameters.AddWithValue("$artifact", beat.IsArtifact ? 1 : 0);
		cmd.ExecuteNonQuery();
	}

	public void InsertHrvSample(HrvSample sample)
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = """
			INSERT OR IGNORE INTO hrv_samples (
				ts, rmssd, pnn50, mean_hr, baseline_rmssd, baseline_hr, state,
				lf_power_ms2, hf_power_ms2, lf_hf_ratio, sd1, sd2, sd1_sd2_ratio, sdnn)
			VALUES (
				$ts, $rmssd, $pnn50, $mean_hr, $baseline_rmssd, $baseline_hr, $state,
				$lf, $hf, $lf_hf, $sd1, $sd2, $sd1_sd2, $sdnn)
			""";
		cmd.Parameters.AddWithValue("$ts", sample.Timestamp.ToUnixTimeMilliseconds());
		cmd.Parameters.AddWithValue("$rmssd", sample.Rmssd);
		cmd.Parameters.AddWithValue("$pnn50", sample.Pnn50);
		cmd.Parameters.AddWithValue("$mean_hr", sample.MeanHr);
		cmd.Parameters.AddWithValue("$baseline_rmssd", sample.BaselineRmssd);
		cmd.Parameters.AddWithValue("$baseline_hr", sample.BaselineHr);
		cmd.Parameters.AddWithValue("$state", sample.State.ToString());

		ExtendedHrvMetrics? ext = sample.Extended;
		cmd.Parameters.AddWithValue("$lf",     ext is not null ? ext.LfPowerMs2  : DBNull.Value);
		cmd.Parameters.AddWithValue("$hf",     ext is not null ? ext.HfPowerMs2  : DBNull.Value);
		cmd.Parameters.AddWithValue("$lf_hf",  ext is not null ? ext.LfHfRatio   : DBNull.Value);
		cmd.Parameters.AddWithValue("$sd1",    ext is not null ? ext.SD1          : DBNull.Value);
		cmd.Parameters.AddWithValue("$sd2",    ext is not null ? ext.SD2          : DBNull.Value);
		cmd.Parameters.AddWithValue("$sd1_sd2",ext is not null ? ext.SD1SD2Ratio  : DBNull.Value);
		cmd.Parameters.AddWithValue("$sdnn",   ext is not null ? ext.Sdnn         : DBNull.Value);

		cmd.ExecuteNonQuery();
	}

	public void InsertAlert(AlertPayload alert)
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = """
			INSERT OR IGNORE INTO alerts (ts, trigger_reason, rmssd_at_trigger, baseline_at_trigger)
			VALUES ($ts, $reason, $rmssd, $baseline)
			""";
		cmd.Parameters.AddWithValue("$ts", alert.Timestamp.ToUnixTimeMilliseconds());
		cmd.Parameters.AddWithValue("$reason", alert.TriggerReason);
		cmd.Parameters.AddWithValue("$rmssd", alert.RmssdAtTrigger);
		cmd.Parameters.AddWithValue("$baseline", alert.BaselineAtTrigger);
		cmd.ExecuteNonQuery();
	}

	public void InsertAnnotation(DateTimeOffset timestamp, AnnotationLabel label, string? notes = null)
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = """
			INSERT OR REPLACE INTO annotations (ts, label, notes)
			VALUES ($ts, $label, $notes)
			""";
		cmd.Parameters.AddWithValue("$ts", timestamp.ToUnixTimeMilliseconds());
		cmd.Parameters.AddWithValue("$label", label.ToString().ToLowerInvariant());
		cmd.Parameters.AddWithValue("$notes", notes ?? (object)DBNull.Value);
		cmd.ExecuteNonQuery();
	}

	public IReadOnlyList<HrvSample> GetHrvSamples(DateTimeOffset from, DateTimeOffset to)
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = """
			SELECT ts, rmssd, pnn50, mean_hr, baseline_rmssd, baseline_hr, state,
			       lf_power_ms2, hf_power_ms2, lf_hf_ratio, sd1, sd2, sd1_sd2_ratio, sdnn
			FROM hrv_samples
			WHERE ts >= $from AND ts <= $to
			ORDER BY ts
			""";
		cmd.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
		cmd.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());

		var results = new List<HrvSample>();
		using var reader = cmd.ExecuteReader();
		while (reader.Read())
		{
			var ts = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0));
			var state = Enum.Parse<DetectorState>(reader.GetString(6));

			ExtendedHrvMetrics? ext = null;
			if (!reader.IsDBNull(7))
			{
				ext = new ExtendedHrvMetrics(
					reader.GetDouble(7),
					reader.GetDouble(8),
					reader.GetDouble(9),
					reader.GetDouble(10),
					reader.GetDouble(11),
					reader.GetDouble(12),
					reader.GetDouble(13));
			}

			results.Add(new HrvSample(ts,
				reader.GetDouble(1),
				reader.GetDouble(2),
				reader.GetDouble(3),
				reader.GetDouble(4),
				reader.GetDouble(5),
				state)
			{
				Extended = ext,
			});
		}

		return results;
	}

	public void Dispose()
	{
		_connection.Dispose();
		GC.SuppressFinalize(this);
	}
}
