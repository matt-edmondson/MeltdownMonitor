using MeltdownMonitor.Core.Persistence;

namespace MeltdownMonitor.Tests;

[TestClass]
public class MeltdownRepositoryAnnotationTests
{
	[TestMethod]
	public void WriteThenRead_RoundTripsLabelAndNotes()
	{
		var path = NewTempDbPath();
		try
		{
			var ts = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
			MeltdownRepository.WriteAnnotation(path, ts, AnnotationLabel.Edged, "tight chest");

			var read = MeltdownRepository.ReadAnnotations(path, ts.AddMinutes(-1), ts.AddMinutes(1));

			Assert.AreEqual(1, read.Count);
			Assert.AreEqual(ts, read[0].Timestamp);
			Assert.AreEqual(AnnotationLabel.Edged, read[0].Label);
			Assert.AreEqual("tight chest", read[0].Notes);
		}
		finally
		{
			TryDelete(path);
		}
	}

	[TestMethod]
	public void WriteWithoutNotes_ReadsBackNull()
	{
		var path = NewTempDbPath();
		try
		{
			var ts = DateTimeOffset.UtcNow;
			MeltdownRepository.WriteAnnotation(path, ts, AnnotationLabel.Fine);

			var read = MeltdownRepository.ReadAnnotations(path, ts.AddMinutes(-1), ts.AddMinutes(1));

			Assert.AreEqual(1, read.Count);
			Assert.IsNull(read[0].Notes, "An omitted note must round-trip as null, not empty string.");
		}
		finally
		{
			TryDelete(path);
		}
	}

	[TestMethod]
	public void ReadAnnotations_FiltersByWindowAndOrdersAscending()
	{
		var path = NewTempDbPath();
		try
		{
			var t0 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
			MeltdownRepository.WriteAnnotation(path, t0, AnnotationLabel.Fine);
			MeltdownRepository.WriteAnnotation(path, t0.AddMinutes(10), AnnotationLabel.Blown, "spiral");
			MeltdownRepository.WriteAnnotation(path, t0.AddHours(48), AnnotationLabel.Escalating);

			var read = MeltdownRepository.ReadAnnotations(path, t0.AddMinutes(-1), t0.AddHours(1));

			Assert.AreEqual(2, read.Count, "The third annotation is outside the window.");
			Assert.AreEqual(AnnotationLabel.Fine, read[0].Label);
			Assert.AreEqual(AnnotationLabel.Blown, read[1].Label);
			Assert.IsTrue(read[0].Timestamp < read[1].Timestamp, "Results must be chronological.");
		}
		finally
		{
			TryDelete(path);
		}
	}

	[TestMethod]
	public void Shutdown_RoundTripsThroughStringPersistence()
	{
		// Shutdown is the low-arousal/collapse self-report (audit A(c)). Labels persist as
		// case-insensitive lower-cased strings, so the freshly-appended enum member must survive
		// the write→read boundary like the original four.
		var path = NewTempDbPath();
		try
		{
			var ts = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_500_000);
			MeltdownRepository.WriteAnnotation(path, ts, AnnotationLabel.Shutdown, "went numb");

			var read = MeltdownRepository.ReadAnnotations(path, ts.AddMinutes(-1), ts.AddMinutes(1));

			Assert.AreEqual(1, read.Count);
			Assert.AreEqual(AnnotationLabel.Shutdown, read[0].Label);
			Assert.AreEqual("went numb", read[0].Notes);
		}
		finally
		{
			TryDelete(path);
		}
	}

	[TestMethod]
	public void InstanceInsert_IsReadableByStaticReader()
	{
		// The pipeline writes annotations via the instance method on its live
		// connection; the History tab reads via the static method on its own.
		// Confirm the lower-cased label storage round-trips across that boundary.
		var path = NewTempDbPath();
		try
		{
			var ts = DateTimeOffset.UtcNow;
			using (var repo = new MeltdownRepository(path))
			{
				repo.InsertAnnotation(ts, AnnotationLabel.Escalating, "note");
			}

			var read = MeltdownRepository.ReadAnnotations(path, ts.AddMinutes(-1), ts.AddMinutes(1));

			Assert.AreEqual(1, read.Count);
			Assert.AreEqual(AnnotationLabel.Escalating, read[0].Label);
		}
		finally
		{
			TryDelete(path);
		}
	}

	private static string NewTempDbPath() =>
		Path.Combine(Path.GetTempPath(), $"mm-{Guid.NewGuid():N}.db");

	private static void TryDelete(string path)
	{
		foreach (var f in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
		{
			try
			{
				File.Delete(f);
			}
			catch
			{
				// best-effort temp cleanup
			}
		}
	}
}
