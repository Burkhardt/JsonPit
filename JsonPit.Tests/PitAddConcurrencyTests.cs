using System;
using System.IO;
using OsLib;
using Xunit;
using System.Threading;
using System.Threading.Tasks;

namespace JsonPit.Tests
{
	public sealed class Pit_Add_Concurrency_Tests : IDisposable
	{
		#region unique id per test per threat
		private static long RefreshedNonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		private long RefreshedValue
		{
			get
			{
				return (uint)Environment.CurrentManagedThreadId ^ Interlocked.Increment(ref RefreshedNonce);
			}
		}
		#endregion
		private static string CreateUniquePitFileName() => RAIkeepTestEnvironment.CloudFile("PitFiles", "Test", "JsonPitTests");
		private string jsonPitTestPitFileName = CreateUniquePitFileName();
		private Pit jsonPitTestsPit;
		
		private void CleanupPit()
		{
			if (jsonPitTestsPit != null)
			{
				jsonPitTestsPit.ReadOnly = true;
				jsonPitTestsPit = null;
			}

			var pitRoot = new RaiFile(jsonPitTestPitFileName);
			pitRoot.Name = pitRoot.Name;
			var canonicalDirectory = pitRoot.Path + pitRoot.Name + Os.DIRSEPERATOR;
			if (Directory.Exists(canonicalDirectory))
				Directory.Delete(canonicalDirectory, recursive: true);
		}

		private void Create_JsonPitTests_Pit()
		{
			CleanupPit();
			jsonPitTestPitFileName = CreateUniquePitFileName();
			jsonPitTestsPit = new Pit(jsonPitTestPitFileName, readOnly: false);
		}
		/// open or save and reopen the pit
		private void Open_JsonPitTests_Pit()
		{
			if (jsonPitTestsPit != null)
				jsonPitTestsPit.Save();
			else jsonPitTestsPit = new Pit(jsonPitTestPitFileName, readOnly: false);
		}

		public void Dispose()
		{
			CleanupPit();
		}

		[Fact]
		public void Add_NewItem_ReturnsTrue_AndIsRetrievableAsTop()
		{
			Create_JsonPitTests_Pit();

			// Arrange
			//Open_JsonPitTests_Pit();
			var refreshed = RefreshedValue;	// different per threat and per test
			var item = new PitItem("AAPL");
			item.SetProperty(new { Price = 262.77 });
			item.SetProperty(new { Refreshed = refreshed });

			// Act
			var added = jsonPitTestsPit.Add(item);

			// Assert
			Assert.True(added);

			var top = jsonPitTestsPit["AAPL"];
			Assert.NotNull(top);
			Assert.Equal(262.77, top["Price"]!.ToObject<double>(), 5);

			jsonPitTestsPit.Save();
		}

		[Fact]
		public void Add_DuplicateContentIgnoringModified_ReturnsFalse_AndDoesNotIncreaseHistory()
		{
			Create_JsonPitTests_Pit();

			// Arrange
			//Open_JsonPitTests_Pit();
			var refreshed = RefreshedValue;

			var item1 = new PitItem("AAPL");
			item1.SetProperty(new { Price = 262.77 });
			item1.SetProperty(new { Refreshed = refreshed });

			// Add first time
			var added1 = jsonPitTestsPit.Add(item1);
			var countAfter1 = jsonPitTestsPit.HistoricItems["AAPL"].Count;

			// Same payload, different Modified
			Thread.Sleep(10);   // makes implicitely sure that item2.Modified is later than item1.Modified
			var item2 = new PitItem("AAPL");
			item2.SetProperty(new { Price = 262.77 });
			item2.SetProperty(new { Refreshed = refreshed });
			
			// I probably do not have to do that - manual adjustment; writing to Modified is allowed
			// item2.Modified = item1.Modified.AddMilliseconds(10);

			// Act
			var added2 = jsonPitTestsPit.Add(item2);
			var countAfter2 = jsonPitTestsPit.HistoricItems["AAPL"].Count;

			// Assert
			Assert.True(added1);
			Assert.False(added2);
			Assert.Equal(countAfter1, countAfter2);
		}

		[Fact]
		public void Add_DuplicateItem_ReturnsFalse_AndDoesNotIncreaseHistory()
		{
			// Arrange
			Create_JsonPitTests_Pit();
			var refreshed = RefreshedValue; // // make sure, item1 is new to the pit; item2 will be a duplicate

			var item1 = new PitItem("AAPL");
			item1.SetProperty(new { Price = 262.77 });
			item1.SetProperty(new { Refreshed = refreshed });

			// Copy constructor preserves Modified and contents (per your PitItem(PitItem other) implementation)
			var item2 = new PitItem(item1);
			item2.SetProperty(new { Price = 262.77 });
			item2.SetProperty(new { Refreshed = refreshed });

			// Act
			var countStart = jsonPitTestsPit.HistoricItems.TryGetValue("AAPL", out var startHistory) ? startHistory.Count : 0;

			var added1 = jsonPitTestsPit.Add(item1);
			var countAfter1 = jsonPitTestsPit.HistoricItems["AAPL"].Count; // assumes Count exists (it’s used in Peek)

			var added2 = jsonPitTestsPit.Add(item2);
			var countAfter2 = jsonPitTestsPit.HistoricItems["AAPL"].Count;

			// Assert
			Assert.True(added1);
			Assert.False(added2);

			Assert.NotEqual(countStart, countAfter1);
			Assert.Equal(countAfter1, countAfter2);

			jsonPitTestsPit.Save();
		}

		[Fact]
		public void Add_ManyItemsInParallel_DoesNotLoseUpdates()
		{
			Create_JsonPitTests_Pit();

			// Arrange
			const int n = 200;
			var baseTs = DateTimeOffset.UtcNow;

			// Ensure the list exists and won't trim to MaxCount (which is why you saw "5")
			var list = jsonPitTestsPit.HistoricItems.GetOrAdd("AAPL", _ => new PitItems());
			list.MaxCount = 0; // unlimited history for this test

			// Act
			Parallel.For(0, n, i =>
			{
				// Each item must differ in CONTENT, not just Modified, otherwise the new Add() dedupe will drop it.
				var item = new PitItem("AAPL", invalidate: false, timestamp: baseTs.AddMilliseconds(i));
				item["Bid"] = 1000 + i;
				item["Seq"] = i;

				jsonPitTestsPit.Add(item);
			});

			// Assert
			Assert.True(jsonPitTestsPit.HistoricItems.ContainsKey("AAPL"));

			var history = jsonPitTestsPit.HistoricItems["AAPL"];
			Assert.Equal(n, history.Count);

			var top = jsonPitTestsPit["AAPL"];
			Assert.NotNull(top);

			// The top item should be the one with the highest timestamp (i = n-1)
			Assert.Equal(n - 1, top.Value<int>("Seq"));
			Assert.Equal(1000 + (n - 1), top.Value<int>("Bid"));
		}
	} // class Pit_Add_Concurrency_Tests
} // namespace JsonPit.Tests
