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
		public void Merge_NestedNull_RemovesOnlyNestedProperty()
		{
			var pit = NewPit();
			var id = NewId("DP_nested_merge_");
			var item = new PitItem(id);
			item.SetProperty(JObject.Parse(@"{
				'What': {
					'Instrument': 'Guitar',
					'Chat': 'LegacyChatId'
				}
			}"));
			pit.Add(item);
			pit.Save(force: true);

			var live = pit[id];
			Assert.True(live.Merge(JObject.Parse(@"{ 'What': { 'Chat': null } }")));
			Assert.Equal(JTokenType.Null, live["What"]!["Chat"]!.Type);
			pit.Add(live);
			pit.Save(force: true);

			var projected = pit.Get(id);
			var what = Assert.IsType<JObject>(projected!["What"]);
			Assert.Equal("Guitar", what["Instrument"]?.Value<string>());
			Assert.False(what.ContainsKey("Chat"));
			Assert.Null(projected["What"]?["Chat"]);
			Assert.Equal(
				JTokenType.Null,
				pit.HistoricItems[id].History[0]["What"]!["Chat"]!.Type);
		}

		[Fact]
		public void PartialNestedNullFragment_PreservesOlderSibling()
		{
			var pit = NewPit();
			var id = NewId("DP_nested_fragment_");
			var original = new PitItem(id);
			original["What"] = new JObject
			{
				["Instrument"] = "Guitar",
				["Chat"] = "LegacyChatId"
			};
			pit.Add(original);
			pit.Save(force: true);

			var fragment = new PitItem(id);
			fragment["What"] = new JObject { ["Chat"] = JValue.CreateNull() };
			pit.Add(fragment);
			pit.Save(force: true);

			var what = Assert.IsType<JObject>(pit.Get(id)!["What"]);
			Assert.Equal("Guitar", what["Instrument"]?.Value<string>());
			Assert.False(what.ContainsKey("Chat"));
		}

		[Fact]
		public void DeletePropertyPath_ArbitraryDepth_PrunesEmptyParents()
		{
			var pit = NewPit();
			var id = NewId("DP_nested_empty_");
			var item = new PitItem(id);
			item.SetProperty(JObject.Parse(@"{ 'Action': { 'Conversation': { 'Chat': 'Legacy' } } }"));
			pit.Add(item);
			pit.Save(force: true);

			var live = pit[id];
			live.DeletePropertyPath("Action.Conversation.Chat");
			pit.Add(live);
			pit.Save(force: true);

			var projected = pit.Get(id);
			Assert.NotNull(projected);
			Assert.False(projected.ContainsKey("Action"));
		}

		[Fact]
		public void Projection_PreservesExplicitEmptyObject_WhenNoTombstoneCreatedIt()
		{
			var pit = NewPit();
			var id = NewId("DP_empty_object_");
			var item = new PitItem(id);
			item["Presentation"] = new JObject();
			pit.Add(item);
			pit.Save(force: true);

			var projected = pit.Get(id);
			var presentation = Assert.IsType<JObject>(projected!["Presentation"]);
			Assert.False(presentation.HasValues);
		}

		[Fact]
		public void DeletePropertyPath_SurvivesReload_AndPreservesSibling()
		{
			var root = TestRoot();
			var pit = NewPit(root);
			var id = NewId("DP_nested_reload_");
			var item = new PitItem(id);
			item.SetProperty(JObject.Parse(@"{ 'What': { 'Instrument': 'Guitar', 'Chat': 'Legacy' } }"));
			pit.Add(item);
			pit.Save(force: true);

			var live = pit[id];
			live.DeletePropertyPath("What.Chat");
			pit.Add(live);
			pit.Save(force: true);
			pit.Dispose();

			using var reloaded = new Pit(root, readOnly: true, autoload: true, unflagged: true);
			var projected = reloaded.Get(id);
			var what = Assert.IsType<JObject>(projected!["What"]);
			Assert.Equal("Guitar", what["Instrument"]?.Value<string>());
			Assert.False(what.ContainsKey("Chat"));
		}

		[Fact]
		public void DeleteProperty_RemainsLiteral_WhenNameContainsDot()
		{
			var pit = NewPit();
			var id = NewId("DP_literal_dot_");
			var item = new PitItem(id);
			item["What.Chat"] = "literal";
			item["What"] = new JObject { ["Chat"] = "nested" };
			pit.Add(item);
			pit.Save(force: true);

			var live = pit[id];
			live.DeleteProperty("What.Chat");
			pit.Add(live);
			pit.Save(force: true);

			var projected = pit.Get(id);
			Assert.False(projected!.ContainsKey("What.Chat"));
			Assert.Equal("nested", projected["What"]?["Chat"]?.Value<string>());
		}

		[Theory]
		[InlineData("")]
		[InlineData("What..Chat")]
		[InlineData(".What")]
		[InlineData("What.")]
		public void DeletePropertyPath_RejectsMalformedPaths(string path)
		{
			var item = new PitItem("MalformedPath");
			Assert.Throws<ArgumentException>(() => item.DeletePropertyPath(path));
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
