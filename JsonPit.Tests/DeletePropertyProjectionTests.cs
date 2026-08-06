using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JsonPit.Tests
{
	public sealed class DeletePropertyProjectionTests
	{
		[Fact]
		public void DeleteProperty_RemovesAttribute_NoNullShadow()
		{
			var pit = NewPit();
			var id = NewId("DP_basic_");
			var item = new PitItem(id);
			item.SetProperty(new { Keep = "here", Doomed = "bye" });
			pit.Add(item);
			pit.Save(force: true);

			var live = pit[id];
			live.DeleteProperty("Doomed");
			pit.Add(live);
			pit.Save(force: true);

			var projected = pit.Get(id);

			Assert.NotNull(projected);
			Assert.Equal("here", projected["Keep"]?.Value<string>());
			Assert.False(((JObject)projected).ContainsKey("Doomed"));
			Assert.Null(projected["Doomed"]);
		}

		[Fact]
		public void DeleteProperty_SurvivesReloadFromDisk()
		{
			var root = TestRoot();
			var pit = NewPit(root);
			var id = NewId("DP_reload_");
			var item = new PitItem(id);
			item.SetProperty(new { A = 1, B = 2 });
			pit.Add(item);
			pit.Save(force: true);

			var live = pit[id];
			live.DeleteProperty("A");
			pit.Add(live);
			pit.Save(force: true);
			pit.Dispose(); // release canonical-path ownership before reopening (CR003 §4)

			using var reloaded = new Pit(root, readOnly: true, autoload: true, unflagged: true);
			var projected = reloaded.Get(id);

			Assert.NotNull(projected);
			Assert.False(((JObject)projected).ContainsKey("A"));
			Assert.Equal(2, projected["B"]?.Value<int>());
		}

		[Fact]
		public void DeleteProperty_ItemRemainsLive_OthersIntact()
		{
			var pit = NewPit();
			var id = NewId("DP_live_");
			var item = new PitItem(id);
			item.SetProperty(new { A = "x", B = "y", C = "z" });
			pit.Add(item);
			pit.Save(force: true);

			var live = pit[id];
			live.DeleteProperty("B");
			pit.Add(live);
			pit.Save(force: true);

			var projected = pit.Get(id);

			Assert.NotNull(projected);
			Assert.False(projected[nameof(PitItem.Deleted)]?.Value<bool>() ?? true);
			Assert.Equal("x", projected["A"]?.Value<string>());
			Assert.Equal("z", projected["C"]?.Value<string>());
			Assert.False(((JObject)projected).ContainsKey("B"));
		}

		[Fact]
		public void PartialNullFragment_DeletesOnlyThatAttribute()
		{
			var pit = NewPit();
			var id = NewId("DP_partial_");
			var item = new PitItem(id);
			item.SetProperty(new { A = "x", B = "y" });
			pit.Add(item);
			pit.Save(force: true);

			var deletion = new PitItem(id);
			deletion.DeleteProperty("A");
			pit.Add(deletion);
			pit.Save(force: true);

			var projected = pit.Get(id);

			Assert.NotNull(projected);
			Assert.False(((JObject)projected).ContainsKey("A"));
			Assert.Equal("y", projected["B"]?.Value<string>());
		}

		[Fact]
		public void DeleteProperty_ThenReintroduce_Works()
		{
			var pit = NewPit();
			var id = NewId("DP_readd_");
			var item = new PitItem(id);
			item.SetProperty(new { Status = "Draft" });
			pit.Add(item);
			pit.Save(force: true);

			var live = pit[id];
			live.DeleteProperty("Status");
			pit.Add(live);
			pit.Save(force: true);

			var again = new PitItem(id);
			again.SetProperty(new { Status = "Signed" });
			pit.Add(again);
			pit.Save(force: true);

			Assert.Equal("Signed", pit.Get(id)?["Status"]?.Value<string>());
		}

		[Fact]
		public void DeleteProperty_PreservesHistory_TimeTravel()
		{
			var pit = NewPit();
			var id = NewId("DP_history_");
			var item = new PitItem(id);
			item.SetProperty(new { A = "before" });
			pit.Add(item);
			pit.Save(force: true);
			var beforeDelete = pit[id].Modified;

			System.Threading.Thread.Sleep(20);
			var live = pit[id];
			live.DeleteProperty("A");
			pit.Add(live);
			pit.Save(force: true);

			var past = pit.GetAt(id, beforeDelete);

			Assert.NotNull(past);
			Assert.Equal("before", past["A"]?.Value<string>());
		}

		[Fact]
		public void ProjectState_ReintroducedPropertyAboveTombstone_WinsAndOlderAttributesSurvive()
		{
			var id = "4711";
			var oldest = Fragment(id, DateTimeOffset.UtcNow.AddMinutes(-3), new JObject
			{
				["Name"] = "Franz",
				["Gender"] = "male"
			});
			var olderAge = Fragment(id, DateTimeOffset.UtcNow.AddMinutes(-2), new JObject
			{
				["Name"] = "Friedel",
				["Age"] = 77
			});
			var ageDeleted = Fragment(id, DateTimeOffset.UtcNow.AddMinutes(-1), new JObject
			{
				["Name"] = "Friedel",
				["Age"] = JValue.CreateNull()
			});
			var reintroducedAge = Fragment(id, DateTimeOffset.UtcNow, new JObject
			{
				["Name"] = "Friedel",
				["Age"] = 62,
				["Birthdate"] = 19631015
			});

			var history = PitItems.Create(id)
				.Push(oldest)
				.Push(olderAge)
				.Push(ageDeleted)
				.Push(reintroducedAge);

			var projected = history.ProjectState();

			Assert.NotNull(projected);
			Assert.Equal("4711", projected.Id);
			Assert.Equal("Friedel", projected["Name"]?.Value<string>());
			Assert.Equal(62, projected["Age"]?.Value<int>());
			Assert.Equal(19631015, projected["Birthdate"]?.Value<int>());
			Assert.Equal("male", projected["Gender"]?.Value<string>());
		}

		private static PitItem Fragment(string id, DateTimeOffset modified, JObject properties)
		{
			properties[nameof(PitItem.Id)] = id;
			properties[nameof(PitItem.Modified)] = modified;
			properties[nameof(PitItem.Deleted)] = false;
			return new PitItem(properties);
		}

		private static Pit NewPit() => NewPit(TestRoot());

		private static Pit NewPit(OsLib.RaiPath root) =>
			new(root, readOnly: false, autoload: false, backup: false, unflagged: true);

		private static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N");

		private static OsLib.RaiPath TestRoot() =>
			RAIkeepTestEnvironment.RootPath / "DeletePropertyProjectionTests" / Guid.NewGuid().ToString("N");
	}
}