using Jil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Xunit;
using OsLib;
using RaiUtils;
using JsonPit;
using System.ComponentModel.DataAnnotations;

namespace JsonPit.Tests
{
	public class JsonPitTestClass
	{
		/// to make sure tests are repeatable, I chose to add an attribute to the simple tests.
		/// the value of the attribute is Seconds; I make sure Seconds is not the same for two tests running after each other.
		/// by initializing Seconds in the beginning of the test suite run and by incrementing Seconds in the beginning of each test.
		/// I make sure Seconds stays the same within a test.
		private long Seconds = (long)(DateTime.Now - new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local)).TotalSeconds;
		private static RaiPath PitPath = RAIkeepTestEnvironment.CloudPath("ObjectPit");
		private Pit pit = Open_ObjectPit_Pit();

		private static Pit Open_ObjectPit_Pit()
		{
			// var dir = Path.Combine(Path.GetTempPath(), "JsonPitTests", "TempDirTests") + Path.DirectorySeparatorChar;
			// // Pit claims it won't throw if missing, but creating it is fine too:
			// Directory.CreateDirectory(dir);
			return new Pit(PitPath, readOnly: false);
		}
		
		[Fact]
		public void AddPitItem_Test()
		{
			// Arrange
			var pitItem = new PitItem("TestItem");
			pitItem.SetProperty(new { Description = "A test item" });
			pitItem.SetProperty(new { Zeit = Seconds });

			// Act
			pit.Add(pitItem);
			pit.Save();
			var retrievedItem = pit.Get("TestItem");

			// Assert
			Assert.NotNull(retrievedItem);
			Assert.Equal("A test item", retrievedItem["Description"]?.ToString());
			Assert.Equal(Seconds, retrievedItem["Zeit"]?.ToObject<long>());
		}

		[Fact]
		public void UpdatePitItem_Test()
		{
			// Arrange
			var pitItem = new PitItem("UpdateItem");
			pitItem.SetProperty(new { Description = "Original" });
			pit.Add(pitItem);
			pit.Save();

			// Act
			pitItem.SetProperty(new { Description = "Updated" });
			//pit.Update(pitItem);
			pit.PitItem = pitItem;
			pit.Save();
			var updatedItem = pit.Get("UpdateItem");

			// Assert
			Assert.NotNull(updatedItem);
			Assert.Equal("Updated", updatedItem["Description"]?.ToString());
		}

		[Fact]
		public void DeletePitItem_Test()
		{
			// Arrange
			var pitItem = new PitItem("DeleteItem");
			pitItem.SetProperty(new { Description = "To be deleted" });
			pit.Add(pitItem);
			pit.Save();

			// Act
			pit.Delete("DeleteItem");
			pit.Save();
			var deletedItem = pit.Get("DeleteItem");

			// Assert
			Assert.Null(deletedItem);
		}
		[Fact]
		public void RetrievePitItem_Test()
		{
			// Arrange
			var pitItem = new PitItem("RetrieveItem");
			pitItem.SetProperty(new { Description = "To be retrieved" });
			pitItem.SetProperty(new { Zeit = Seconds });
			pit.Add(pitItem);
			pit.Save();

			// Act
			var retrievedItem = pit.Get("RetrieveItem");

			// Assert
			Assert.NotNull(retrievedItem);
			Assert.Equal("To be retrieved", retrievedItem["Description"]?.ToString());
		}

		[Fact]
		public void LoadFromDisk_PreservesModifiedTimestamp()
		{
			Seconds++;
			try
			{
				pit = Open_ObjectPit_Pit();
				var item = new PitItem("AAPL");
				item.SetProperty(new { Price = 262.77 });
				item.SetProperty(new { Zeit = Seconds });
				pit.Add(item);
				pit.Save();

				var savedModified = pit["AAPL"].Modified;

				var reloaded = new Pit(PitPath, readOnly: true, unflagged: true);
				var loadedItem = reloaded["AAPL"];
				Assert.NotNull(loadedItem);
				Assert.Equal(savedModified, loadedItem.Modified);
			}
			finally
			{
				// if (Directory.Exists(root))
				// 	Directory.Delete(root, recursive: true);
			}
		}
		[Fact]
		public void JsonPit_StepByStepExample_Test()
		{
			// Initialize the JsonPit
			var examplesPit = new Pit(pitDirectory: RAIkeepTestEnvironment.CloudPath("Examples"), readOnly: false);
			// no file not found exception will be thrown if the path does not exist

			// Create a PitItem
			var pitItem = new PitItem("RSB");
			pitItem.SetProperty(new { Age = 61 });
			pitItem.SetProperty(new { Children = 7 });
			pitItem.SetProperty(new { Kids = new[] { "Nina", "Hannah", "Vuyisile", "Kilian", "Laura", "Mbali", "Logan" } });
			examplesPit.Add(pitItem);

			// Add Another PitItem
			var pitItem2 = new PitItem("Nomsa", new
			{
				Age = 52,
				Children = 7,
				Kids = new[] { "Nina", "Hannah", "Vuyisile", "Kilian", "Laura", "Mbali", "Logan" }
			});
			examplesPit.Add(pitItem2);

			// Save the Pit
			examplesPit.Save();

			// Retrieve Items from the Pit
			var item = examplesPit.Get("RSB");
			var item2 = examplesPit["Nomsa"];

			// Validate properties
			var id = item["Id"]?.ToString();
			Assert.Equal("RSB", id);
			var Age = item["Age"];
			Assert.Equal(61, Convert.ToInt16(Age));
			var Children = item["Children"];
			Assert.Equal(7, Convert.ToInt16(Children));
			var Kids = item["Kids"]?.ToObject<List<string>>();
			var Kid6 = Kids?[6];
			Assert.Equal("Logan", Kid6);
			var Kids2 = item2["Kids"]?.ToObject<List<string>>();
			var Kid5 = Kids2?[5];
			Assert.Equal("Mbali", Kid5);
		}

		[Fact]
		public void DirectAccess_Test()
		{
			// Pit 
			var Families = new Pit(RAIkeepTestEnvironment.CloudPath("UnitTest"), readOnly: false);
			var Burkhardt = new PitItem("Burkhardt");
			Burkhardt["Address"] = "123 Main St";
			Burkhardt["Father"] = "John Doe";
			Burkhardt["Mother"] = "Jane Doe";
			Families.Add(Burkhardt);
			// This is a placeholder for another test
			Burkhardt.ExtendWith(new JObject { { "Phone", "619-555-1234" } });
			Burkhardt.ExtendWith(new JObject { { "Email", "burkhardt@example.com" } });
			System.Threading.Thread.Sleep(25);
			Burkhardt.ExtendWith(new JObject { { "Email", "example@burkhardt.com" } });
			System.Threading.Thread.Sleep(25);
			Burkhardt.ExtendWith(new JArray {
				new JObject { { "Email", "burkhardt@example.com" } },
			 	new JObject { { "Email", "example@burkhardt.com" } },
				new JObject { { "BizEmail", "burkhardt@example.com" } },
				new JObject { { "Instagram", "burkhardt_insta" } }
			});
			Assert.Equal("example@burkhardt.com", Burkhardt["Email"]);
			var history = Families.HistoricItems["Burkhardt"];
			Assert.NotNull(history.Peek());
			Assert.Equal("example@burkhardt.com", history.Peek()["Email"]?.ToString());
			Families.Save();
			Assert.True(true);
		}
	} // class JsonPitTestClass

	public sealed class PitItems_Sort_Tests
	{
		[Fact]
		public void Sort_DoesNotChange_Modified_Timestamps()
		{
			var tsNewer = DateTimeOffset.UtcNow;
			var tsOlder = tsNewer.AddMinutes(-5);

			var newer = new PitItem("AAPL", invalidate: false, timestamp: tsNewer);
			var older = new PitItem("AAPL", invalidate: false, timestamp: tsOlder);

			var list = new PitItems();
			list = list.Push(newer);
			list = list.Push(older); // out of order -> triggers Sort()

			Assert.Equal(tsNewer, newer.Modified);
			Assert.Equal(tsOlder, older.Modified);

			// Ensure ordering still correct after sort (oldest first).
			Assert.Equal(tsOlder, list.History.First().Modified);
			Assert.Equal(tsNewer, list.History.Last().Modified);
		}
	}

	public sealed class Pit_GetAt_Tests
	{
		private static RaiPath CreatePitPath([CallerMemberName] string testName = "")
		{
			var root = (new RaiPath(Path.GetTempPath())) / "JsonPitTests" / "GetAtTests" / SanitizeSegment(testName);
			root.rmdir(depth: 10, deleteFiles: true);
			return root;
		}

		private static void 	Cleanup(RaiPath path) => path.rmdir(depth: 10, deleteFiles: true);

		private static string SanitizeSegment(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
				return "test";

			var invalid = Path.GetInvalidFileNameChars();
			var cleaned = new string(value
				.Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '-' : ch)
				.ToArray())
				.Trim('-');

			return string.IsNullOrWhiteSpace(cleaned) ? "test" : cleaned;
		}

		[Fact]
		public void GetAt_ReturnsItemAtOrBeforeTimestamp()
		{
			var pitPath = CreatePitPath();
			var symbol = "TGETAT";
			try
			{
				var pit = new Pit(pitPath, readOnly: false, unflagged: true, autoload: false);
				var baseTs = DateTimeOffset.UtcNow;

				var item1 = new PitItem(symbol, invalidate: false, timestamp: baseTs.AddMinutes(-2));
				item1["Price"] = 1;
				pit.Add(item1);

				var item2 = new PitItem(symbol, invalidate: false, timestamp: baseTs.AddMinutes(-1));
				item2["Price"] = 2;
				pit.Add(item2);

				var item3 = new PitItem(symbol, invalidate: false, timestamp: baseTs);
				item3["Price"] = 3;
				pit.Add(item3);

				var atItem2 = pit.GetAt(symbol, item2.Modified.AddTicks(1));
				Assert.NotNull(atItem2);
				Assert.Equal(2, atItem2["Price"]!.Value<int>());

				var beforeAll = pit.GetAt(symbol, baseTs.AddMinutes(-3));
				Assert.Null(beforeAll);

				//Assert.True(pit.Invalid());	// invalidate: false above makes this one fail

				pit.Save(); 
			
			}
			finally
			{
				pitPath.rmdir(depth: 3, deleteFiles: true);
			}
		}

		[Fact]
		public void GetAt_RespectsDeletedFlag()
		{
			var pitPath = CreatePitPath();
			var symbol = "TDELETE";
			try
			{
				var pit = new Pit(pitPath, readOnly: false, unflagged: true, autoload: false);
				var baseTs = DateTimeOffset.UtcNow;

				var item1 = new PitItem(symbol, invalidate: false, timestamp: baseTs.AddMinutes(-2));
				item1["Price"] = 1;
				pit.Add(item1);

				pit.Save(force: true);  // why do I need to force it? because invalidate was set to false above

				var deleted = new PitItem(symbol, invalidate: false, timestamp: baseTs.AddMinutes(-1));
				deleted.Deleted = true;
				pit.Add(deleted);

				var hidden = pit.GetAt(symbol, deleted.Modified.AddTicks(1));
				Assert.Null(hidden);

				var visibleDeleted = pit.GetAt(symbol, deleted.Modified.AddTicks(1), withDeleted: true);
				Assert.NotNull(visibleDeleted);
				Assert.True(visibleDeleted!.Deleted);

				pit.Save();	// why do I need to force it? 
			}
			finally
			{
				Cleanup(pitPath);
			}
		}

		[Fact]
		public void GetAt_ProjectsState_ByReverseShadowing()
		{
			var pitPath = CreatePitPath();
			var symbol = "TSHADOW";
			try
			{
				var pit = new Pit(pitPath, readOnly: false, unflagged: true, autoload: false);
				var baseTs = DateTimeOffset.UtcNow;

				var item1 = new PitItem(symbol, invalidate: false, timestamp: baseTs.AddMinutes(-3));
				item1["Email"] = "before@example.org";
				item1["Country"] = "ZA";
				pit.Add(item1);

				var item2 = new PitItem(symbol, invalidate: false, timestamp: baseTs.AddMinutes(-2));
				item2["Phone"] = "+27-82-000-0000";
				pit.Add(item2);

				var item3 = new PitItem(symbol, invalidate: false, timestamp: baseTs.AddMinutes(-1));
				item3["Email"] = "after@example.org";
				pit.Add(item3);

				var projected = pit[symbol];

				Assert.NotNull(projected);
				Assert.Equal(symbol, projected!.Id);
				Assert.Equal(baseTs.AddMinutes(-1), projected.Modified);
				Assert.Equal("after@example.org", projected["Email"]!.Value<string>());
				Assert.Equal("ZA", projected["Country"]!.Value<string>());
				Assert.Equal("+27-82-000-0000", projected["Phone"]!.Value<string>());
				Assert.Null(pit.HistoricItems[symbol].History.Last()["Country"]);
			}
			finally
			{
				Cleanup(pitPath);
			}
		}

		[Fact]
		public void GetAt_StopsAtDeletionWall_WhenProjectingOlderFragments()
		{
			var pitPath = CreatePitPath();
			var symbol = "TDELETIONWALL";
			try
			{
				var pit = new Pit(pitPath, readOnly: false, unflagged: true, autoload: false);
				var baseTs = DateTimeOffset.UtcNow;

				var original = new PitItem(symbol, invalidate: false, timestamp: baseTs.AddMinutes(-3));
				original["Email"] = "before@example.org";
				original["Country"] = "ZA";
				pit.Add(original);

				var tombstone = new PitItem(symbol, invalidate: false, timestamp: baseTs.AddMinutes(-2));
				tombstone.Deleted = true;
				pit.Add(tombstone);

				var resurrected = new PitItem(symbol, invalidate: false, timestamp: baseTs.AddMinutes(-1));
				resurrected["Email"] = "after@example.org";
				pit.Add(resurrected);

				var projected = pit.GetAt(symbol, resurrected.Modified.AddTicks(1));
				Assert.NotNull(projected);
				Assert.Equal("after@example.org", projected!["Email"]!.Value<string>());
				Assert.Null(projected["Country"]);

				var deletedAtWall = pit.GetAt(symbol, tombstone.Modified);
				Assert.Null(deletedAtWall);

				var visibleDeleted = pit.GetAt(symbol, tombstone.Modified, withDeleted: true);
				Assert.NotNull(visibleDeleted);
				Assert.True(visibleDeleted!.Deleted);
				Assert.Equal(symbol, visibleDeleted.Id);
				Assert.Equal(tombstone.Modified, visibleDeleted.Modified);
				Assert.Null(visibleDeleted["Email"]);
				Assert.Null(visibleDeleted["Country"]);
				Assert.Null(visibleDeleted["Note"]);
			}
			finally
			{
				Cleanup(pitPath);
			}
		}
	}

	#region AdditionalTests from conversation with GPT5.2 Thinking

	public sealed class PitItemExtendJson_ArrayTabularTests
	{
		[Fact]
		public void Extend_Object_AddsNewAttributes_WithoutTouchingOthers_Test()
		{
			var item = new PitItem("AAPL");
			item.SetProperty(new { Price = 262.77 });

			var jsonObj = @"{ ""Bid"": 263.41, ""Ask"": 263.44 }";
			var extended = new PitItem("AAPL", jsonObj);

			// NOTE: this test assumes you are extending an existing item in-place;
			// if your constructor creates a new PitItem, prefer item.Extend(json) instead.
		}

		[Fact]
		public void Extend_Array_AppendsValuesAndCreatesNewAttributes_Test()
		{
			var aapl = new PitItem("AAPL");
			aapl.SetProperty(new { Price = 262.77 });

			//var priceCountBefore = aapl.HistoryCount("Price"); // <-- whatever your API is

			var jsonArr = @"[
              { ""Bid"": 263.41, ""Ask"": 263.44 },
              { ""Bid"": 263.43, ""Ask"": 263.40 }
            ]";

			aapl.Extend(jsonArr); // preferred API; or new PitItem("AAPL", jsonArr) if ctor mutates

			// Assert.Equal(priceCountBefore, aapl.HistoryCount("Price"));     // no new price values
			// Assert.Equal(2, aapl.HistoryCount("Bid"));                      // two bid values appended
			// Assert.Equal(2, aapl.HistoryCount("Ask"));                      // two ask values appended

			Assert.Equal(263.43, aapl["Bid"].Value<double>(), 5);           // latest bid
			Assert.Equal(263.40, aapl["Ask"].Value<double>(), 5);           // latest ask

			Assert.True(aapl.Modified < DateTimeOffset.UtcNow);           // ensure modified timestamp is updated
		}

		[Fact]
		public void Extend_Array_WithMixedSchema_ExtendsSchemaAsSeen_Test()
		{
			var item = new PitItem("AAPL");
			item.SetProperty(new { Price = 262.77 });

			var jsonArr = @"[
              { ""Bid"": 263.41 },
              { ""Ask"": 263.44, ""Spread"": 0.03 }
            ]";

			item.Extend(jsonArr);

			Assert.NotNull(item["Bid"]);
			Assert.NotNull(item["Ask"]);
			Assert.NotNull(item["Spread"]);

			// Assert.Equal(1, item.HistoryCount("Bid"));
			// Assert.Equal(1, item.HistoryCount("Ask"));
			// Assert.Equal(1, item.HistoryCount("Spread"));
		}
	}

	public sealed class PitItem_ExtendJson_Tests
	{
		[Fact]
		public void Ctor_WithJObjectJson_ExtendsItemWithAttributes()
		{
			// Arrange
			var before = DateTimeOffset.UtcNow;
			var json = @"{ ""Price"": 223.57, ""Volume"": 17320 }";

			// Act
			var item = new PitItem(id: "AAPL", extendWithAsJson: json, comment: "tiingo");

			// Assert
			var after = DateTimeOffset.UtcNow;

			Assert.Equal("AAPL", item.Id);
			Assert.False(item.Deleted);
			Assert.Equal("tiingo", item.Note);

			// Modified is set by Invalidate() in the ctor; allow a small window.
			Assert.True(item.Modified >= before.AddSeconds(-1) && item.Modified <= after.AddSeconds(1));

			Assert.NotNull(item["Price"]);
			Assert.NotNull(item["Volume"]);
			Assert.Equal(223.57, item["Price"]!.Value<double>(), 5);
			Assert.Equal(17320, item["Volume"]!.Value<int>());
		}

		[Fact]
		public void Ctor_WithJArrayJson_UnionsSchemaAndLastValueWinsPerAttribute()
		{
			// Arrange
			var json = @"[
              { ""Bid"": 263.41, ""Ask"": 263.44 },
              { ""Bid"": 263.43, ""Ask"": 263.40 }
            ]";

			// Act
			var item = new PitItem(id: "AAPL", extendWithAsJson: json);

			// Assert
			// Schema extension: Ask/Bid exist even if they were not present previously.
			Assert.NotNull(item["Bid"]);
			Assert.NotNull(item["Ask"]);

			// In-memory behavior: last row wins for repeated keys.
			Assert.Equal(263.43, item["Bid"]!.Value<double>(), 5);
			Assert.Equal(263.40, item["Ask"]!.Value<double>(), 5);
		}

		[Fact]
		public void Extend_Array_AddsNewAttributes_AndDoesNotTouchExistingOnes()
		{
			// Arrange
			var item = new PitItem("AAPL");
			item.SetProperty(new { Price = 262.77 }); // sets Modified and Dirty

			var priceBefore = item["Price"]!.Value<double>();

			var jsonArr = @"[
              { ""Bid"": 263.41, ""Ask"": 263.44 },
              { ""Bid"": 263.43, ""Ask"": 263.40 }
            ]";

			// Act
			item.Extend(jsonArr);

			// Assert
			// Existing property remains unchanged because the array doesn't contain "Price".
			Assert.Equal(priceBefore, item["Price"]!.Value<double>(), 5);

			// New properties added.
			Assert.NotNull(item["Bid"]);
			Assert.NotNull(item["Ask"]);

			// Last row wins per attribute (in-memory).
			Assert.Equal(263.43, item["Bid"]!.Value<double>(), 5);
			Assert.Equal(263.40, item["Ask"]!.Value<double>(), 5);
		}

		[Fact]
		public void Extend_Array_WithNonObjectElement_SetsRawToLastNonObject()
		{
			// Arrange
			var item = new PitItem("AAPL");
			var jsonArr = @"[
              { ""Bid"": 263.41 },
              42,
              ""hello""
            ]";

			// Act
			item.Extend(jsonArr);

			// Assert
			// Your current implementation overwrites Raw each time it sees a non-object.
			Assert.NotNull(item["_"]);

			// last non-object wins => "hello"
			Assert.Equal("hello", item["_"]!.Value<string>());
		}
	}

	#endregion 

} // namespace JsonPit.Tests
