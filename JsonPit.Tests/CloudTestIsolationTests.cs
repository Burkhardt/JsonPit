using System;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

[Trait("Category", "CloudEffect")]
public class CloudTestIsolationTests
{
	[Fact]
	public void CloudFixtureRoots_AreIsolatedUnderTestCloud()
	{
		var root = ConfiguredCloudPits.RequirePitRoot("isolation-check");
		var providerRoot = Os.CloudRoot(Os.TestCloud);
		Assert.Equal(Os.TestCloud, ConfiguredCloudPits.ProviderNameOrNull());
		Assert.StartsWith(providerRoot.FullPath, root.FullPath);
		Assert.StartsWith("RAIkeep.Test.", ConfiguredCloudPits.RunDirectory);
		Assert.True(Guid.TryParseExact(ConfiguredCloudPits.RunDirectory["RAIkeep.Test.".Length..], "N", out _));
		Assert.Equal((providerRoot / ConfiguredCloudPits.RunDirectory / "isolation-check").FullPath, root.FullPath);
		Assert.NotEqual(providerRoot.FullPath, root.FullPath);
	}
}
