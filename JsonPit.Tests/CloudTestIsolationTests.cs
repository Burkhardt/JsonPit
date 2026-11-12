using System;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

public class CloudTestIsolationTests
{
	[Fact]
	public void LocalFixtureRoots_AreIsolatedUnderDropbox()
	{
		var root = ConfiguredCloudPits.RequirePitRoot("isolation-check");
		var dropbox = new RaiPath((string)Os.Config.Cloud.Dropbox);
		Assert.Equal("Dropbox", ConfiguredCloudPits.ProviderNameOrNull());
		Assert.StartsWith(dropbox.FullPath, root.FullPath);
		Assert.StartsWith("RAIkeep.Test.", ConfiguredCloudPits.RunDirectory);
		Assert.True(Guid.TryParseExact(ConfiguredCloudPits.RunDirectory["RAIkeep.Test.".Length..], "N", out _));
		Assert.Equal((dropbox / ConfiguredCloudPits.RunDirectory / "isolation-check").FullPath, root.FullPath);
		Assert.NotEqual(dropbox.FullPath, root.FullPath);
	}
}
