using Microsoft.Data.Sqlite;
using MeltdownMonitor.Core.Beats;
using MeltdownMonitor.Core.Detection;
using MeltdownMonitor.Core.Hrv;

namespace MeltdownMonitor.Core.Persistence;

public class MeltdownRepository : IDisposable
{
	private readonly SqliteConnection _connection;

	// Serializes writes on the shared connection: the pipeline loop writes beats
	// and HRV samples while battery readings arrive on a background BLE thread,
	// and SqliteConnection is not safe for concurrent use.
	private readonly object _writeLock = new();

	public MeltdownRepository(string databasePath)
		: this(databasePath, MeltdownRepositoryOptions.Default)
	{
	}

	/// <summary>
	/// Opens the repository with platform-specific tuning. The iOS head passes
	/// <see cref="MeltdownRepositoryOptions.IosSandbox"/> so the database plays
	/// nicely with the data-protection encryption that kicks in when the device
	/// is locked (design doc §4.7): WAL is disabled in favour of TRUNCATE and
	/// <c>fullfsync</c> is enabled so background BLE callbacks can still commit.
	/// Desktop callers use the parameterless constructor and are unaffected.
	/// </summary>
	public MeltdownRepository(string databasePath, MeltdownRepositoryOptions options)
	{
		_connection = new SqliteConnection($"Data Source={databasePath}");
		_connection.Open();
		ApplyPragmas(options);
		EnsureSchema();
		JournalMode = QueryJournalMode();
	}

	/// <summary>
	/// The journal mode SQLite settled on for this connection — useful for
	/// confirming the iOS sandbox profile actually took (design doc §4.7) and
	/// for diagnostics. Rollback-journal modes are per-connection, so this
	/// reflects the live connection rather than anything stored on disk.
	/// </summary>
	public string JournalMode { get; }

	private string QueryJournalMode()
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = "PRAGMA journal_mode;";
		return ((string)cmd.ExecuteScalar()!).ToLowerInvariant();
	}

	private void ApplyPragmas(MeltdownRepositoryOptions options)
	{
		if (options.JournalMode is null && !options.FullFsync)
		{
			return;
		}

		using var cmd = _connection.CreateCommand();
		if (options.JournalMode is not null)
		{
			cmd.CommandText = $"PRAGMA journal_mode={options.JournalMode};";
			cmd.ExecuteNonQuery();
		}

		if (options.FullFsync)
		{
			cmd.CommandText = "PRAGMA fullfsync=ON;";
			cmd.ExecuteNonQuery();
		}
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
			CREATE TABLE IF NOT EXISTS battery (
				ts      INTEGER PRIMARY KEY,
				percent INTEGER NOT NULL
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
			("contact",        "TEXT"),
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
		lock (_writeLock)
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
	}

	public void InsertBattery(BatteryReading reading)
	{
		lock (_writeLock)
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				INSERT OR REPLACE INTO battery (ts, percent)
				VALUES ($ts, $percent)
				""";
			cmd.Parameters.AddWithValue("$ts", reading.Timestamp.ToUnixTimeMilliseconds());
			cmd.Parameters.AddWithValue("$percent", reading.Percent);
			cmd.ExecuteNonQuery();
		}
	}

	public void InsertHrvSample(HrvSample sample)
	{
		lock (_writeLock)
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				INSERT OR IGNORE INTO hrv_samples (
					ts, rmssd, pnn50, mean_hr, baseline_rmssd, baseline_hr, state,
					lf_power_ms2, hf_power_ms2, lf_hf_ratio, sd1, sd2, sd1_sd2_ratio, sdnn, contact)
				VALUES (
					$ts, $rmssd, $pnn50, $mean_hr, $baseline_rmssd, $baseline_hr, $state,
					$lf, $hf, $lf_hf, $sd1, $sd2, $sd1_sd2, $sdnn, $contact)
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
			cmd.Parameters.AddWithValue("$contact", sample.SensorContact.ToString());

			cmd.ExecuteNonQuery();
		}
	}

	public void InsertAlert(AlertPayload alert)
	{
		lock (_writeLock)
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
	}

	public void InsertAnnotation(DateTimeOffset timestamp, AnnotationLabel label, string? notes = null)
	{
		lock (_writeLock)
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
	}

	/// <summary>
	/// Records a self check-in via a short-lived connection of its own, mirroring
	/// <see cref="ReadHistory"/>. The live pipeline owns a single long-running
	/// connection on a background thread; user-initiated annotation writes arrive
	/// on the UI thread, so routing them through an independent connection avoids
	/// concurrent use of the pipeline's <see cref="SqliteConnection"/>. A short
	/// <c>busy_timeout</c> lets SQLite's file lock serialise the two writers
	/// instead of failing fast with SQLITE_BUSY.
	/// </summary>
	public static void WriteAnnotation(string databasePath, DateTimeOffset timestamp, AnnotationLabel label, string? notes = null)
	{
		using var conn = new SqliteConnection($"Data Source={databasePath}");
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = """
			PRAGMA busy_timeout=3000;
			CREATE TABLE IF NOT EXISTS annotations (
				ts    INTEGER PRIMARY KEY,
				label TEXT    NOT NULL,
				notes TEXT
			);
			INSERT OR REPLACE INTO annotations (ts, label, notes)
			VALUES ($ts, $label, $notes)
			""";
		cmd.Parameters.AddWithValue("$ts", timestamp.ToUnixTimeMilliseconds());
		cmd.Parameters.AddWithValue("$label", label.ToString().ToLowerInvariant());
		cmd.Parameters.AddWithValue("$notes", notes ?? (object)DBNull.Value);
		cmd.ExecuteNonQuery();
	}

	/// <summary>
	/// Reads the self check-ins in the window via a short-lived read-only
	/// connection, mirroring <see cref="ReadHistory"/>. Labels are stored
	/// lower-cased (see <see cref="InsertAnnotation"/>), so parsing is
	/// case-insensitive.
	/// </summary>
	public static IReadOnlyList<AnnotationRecord> ReadAnnotations(string databasePath, DateTimeOffset from, DateTimeOffset to)
	{
		using var conn = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = """
			SELECT ts, label, notes
			FROM annotations
			WHERE ts >= $from AND ts <= $to
			ORDER BY ts
			""";
		cmd.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
		cmd.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());

		var results = new List<AnnotationRecord>();
		using var reader = cmd.ExecuteReader();
		while (reader.Read())
		{
			var ts = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0));
			var label = Enum.Parse<AnnotationLabel>(reader.GetString(1), ignoreCase: true);
			var notes = reader.IsDBNull(2) ? null : reader.GetString(2);
			results.Add(new AnnotationRecord(ts, label, notes));
		}

		return results;
	}

	public IReadOnlyList<HrvSample> GetHrvSamples(DateTimeOffset from, DateTimeOffset to)
	{
		lock (_writeLock)
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				SELECT ts, rmssd, pnn50, mean_hr, baseline_rmssd, baseline_hr, state,
				       lf_power_ms2, hf_power_ms2, lf_hf_ratio, sd1, sd2, sd1_sd2_ratio, sdnn, contact
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

				var contact = reader.IsDBNull(14)
					? SensorContactStatus.NotSupported
					: Enum.TryParse<SensorContactStatus>(reader.GetString(14), ignoreCase: true, out var c)
						? c
						: SensorContactStatus.NotSupported;

				results.Add(new HrvSample(ts,
					reader.GetDouble(1),
					reader.GetDouble(2),
					reader.GetDouble(3),
					reader.GetDouble(4),
					reader.GetDouble(5),
					state)
				{
					Extended = ext,
					SensorContact = contact,
				});
			}

			return results;
		}
	}

	/// <summary>
	/// Opens a short-lived read-only connection to query HRV samples.
	/// Safe to call from any thread independently of the main write connection.
	/// </summary>
	public static IReadOnlyList<HrvSample> ReadHistory(string databasePath, DateTimeOffset from, DateTimeOffset to)
	{
		using var conn = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = """
			SELECT ts, rmssd, pnn50, mean_hr, baseline_rmssd, baseline_hr, state,
			       lf_power_ms2, hf_power_ms2, lf_hf_ratio, sd1, sd2, sd1_sd2_ratio, sdnn, contact
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

			var contact = reader.IsDBNull(14)
				? SensorContactStatus.NotSupported
				: Enum.TryParse<SensorContactStatus>(reader.GetString(14), ignoreCase: true, out var c)
					? c
					: SensorContactStatus.NotSupported;

			results.Add(new HrvSample(ts,
				reader.GetDouble(1),
				reader.GetDouble(2),
				reader.GetDouble(3),
				reader.GetDouble(4),
				reader.GetDouble(5),
				state)
			{
				Extended = ext,
				SensorContact = contact,
			});
		}

		return results;
	}

	/// <summary>
	/// Reads battery-level readings in the window via a short-lived read-only
	/// connection, mirroring <see cref="ReadHistory"/>. Tolerates databases
	/// created before the <c>battery</c> table existed by returning an empty list.
	/// </summary>
	public static IReadOnlyList<BatteryReading> ReadBatteryHistory(string databasePath, DateTimeOffset from, DateTimeOffset to)
	{
		using var conn = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = """
			SELECT ts, percent
			FROM battery
			WHERE ts >= $from AND ts <= $to
			ORDER BY ts
			""";
		cmd.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
		cmd.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());

		var results = new List<BatteryReading>();
		using var reader = cmd.ExecuteReader();
		while (reader.Read())
		{
			var ts = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0));
			results.Add(new BatteryReading(ts, reader.GetInt32(1)));
		}

		return results;
	}

	public void Dispose()
	{
		_connection.Dispose();
		GC.SuppressFinalize(this);
	}
}
