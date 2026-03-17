using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using JsonPit;
using Newtonsoft.Json.Linq;
using OsLib;
using Xunit;

namespace JsonPit.Tests
{
	public class JsonPitRealWorldIntegrationTests
	{
		[Theory]
		[InlineData(CloudStorageType.Dropbox)]
		[InlineData(CloudStorageType.OneDrive)]
		[InlineData(CloudStorageType.GoogleDrive)]
		[InlineData(CloudStorageType.ICloud)]
		public void Pit_SaveAndReload_WorksAgainstRealWritableCloudProvider(CloudStorageType provider)
		{
			if (!TryPrepareWritableIntegrationRoot(provider, out var root, out var providerRoot, out var reason))
				Assert.Skip($"Provider {provider}: {reason}. {Os.GetCloudStorageSetupGuidance()}");

			try
			{
				var pitRoot = root / "pit-store";
				pitRoot.mkdir();

				var pit = new Pit(pitRoot.Path, readOnly: false, autoload: false, backup: false);
				var item = new PitItem("CloudItem");
				item.SetProperty(new { Value = 42, Provider = provider.ToString(), CreatedBy = "RAIkeep" });
				pit.Add(item);

				var saveTimer = Stopwatch.StartNew();
				pit.Save(force: true);
				saveTimer.Stop();

				Assert.True(pit.JsonFile.Cloud);
				Assert.True(pit.JsonFile.Exists());
				pit.JsonFile.AwaitMaterializing();

				var loadTimer = Stopwatch.StartNew();
				var reloaded = new Pit(pitRoot.Path, readOnly: false, autoload: true, backup: false);
				loadTimer.Stop();

				var loaded = reloaded.Get("CloudItem");
				Assert.NotNull(loaded);
				Assert.Equal(42, loaded["Value"]!.ToObject<int>());
				Assert.Equal(provider.ToString(), loaded["Provider"]!.ToObject<string>());
				Assert.Equal("RAIkeep", loaded["CreatedBy"]!.ToObject<string>());

				Console.WriteLine($"Provider {provider}: pit-save={saveTimer.ElapsedMilliseconds}ms pit-load={loadTimer.ElapsedMilliseconds}ms file={pit.JsonFile.FullName} root={providerRoot}");
			}
			finally
			{
				Cleanup(root);
			}
		}

		private static bool TryPrepareWritableIntegrationRoot(CloudStorageType provider, out RaiPath root, out string providerRoot, out string reason)
		{
			Os.ResetCloudStorageCache();
			providerRoot = Os.GetCloudStorageRoot(provider, refresh: true);
			if (string.IsNullOrWhiteSpace(providerRoot))
			{
				root = new RaiPath(Os.TempDir) / "RAIkeep" / "missing-cloud-root";
				reason = "provider root is not configured or not discoverable on this machine";
				return false;
			}

			providerRoot = new RaiPath(providerRoot).Path;
			if (!Directory.Exists(providerRoot))
			{
				root = new RaiPath(providerRoot) / "RAIkeep" / "jsonpit-cloud-integration-tests" / provider.ToString();
				reason = $"provider root does not exist: {providerRoot}";
				return false;
			}

			root = new RaiPath(providerRoot) / "RAIkeep" / "jsonpit-cloud-integration-tests" / provider.ToString();
			reason = string.Empty;

			try
			{
				Cleanup(root);
				root.mkdir();
				return true;
			}
			catch (UnauthorizedAccessException ex)
			{
				reason = $"root is not writable: {ex.Message}";
			}
			catch (IOException ex)
			{
				reason = $"root is not writable: {ex.Message}";
			}

			return false;
		}

		private static void Cleanup(RaiPath root)
		{
			try
			{
				if (Directory.Exists(root.Path))
					new RaiFile(root.Path).rmdir(depth: 10, deleteFiles: true);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Cleanup warning for {root.Path}: {ex.Message}");
			}
		}
	}
}