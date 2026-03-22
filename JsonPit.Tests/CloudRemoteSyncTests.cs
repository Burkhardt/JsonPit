using System;
using System.Diagnostics;
using System.IO;
using JsonPit;
using OsLib;
using Xunit;

namespace JsonPit.Tests
{
	public class CloudRemoteSyncTests
	{
		[Theory]
		[InlineData(CloudStorageType.GoogleDrive)]
		[InlineData(CloudStorageType.Dropbox)]
		[InlineData(CloudStorageType.OneDrive)]
		public void Pit_SyncsWithMzansi(CloudStorageType provider)
		{
			Console.WriteLine(Os.GetCloudConfigurationDiagnosticReport(refresh: true));
			Console.WriteLine(Os.GetRemoteTestConfigurationDiagnosticReport(refresh: true));

			if (!RemoteCloudSyncProbe.TryCreate(provider, "mzansi", out var probe, out var reason))
				Assert.Skip(reason + Environment.NewLine + Os.GetCloudConfigurationDiagnosticReport() + Environment.NewLine + Os.GetRemoteTestConfigurationDiagnosticReport());

			var providerKey = provider.ToString().ToLowerInvariant();
			var pitRoot = probe.LocalCloudRoot / "RAIkeep" / "jsonpit-remote-sync-tests" / providerKey / "pit-store";
			pitRoot.mkdir();
			var remoteRelativeDir = $"RAIkeep/jsonpit-remote-sync-tests/{providerKey}/pit-store/";
			var remoteDir = probe.GetRemoteDirectory(remoteRelativeDir);

			var pit = new Pit(pitRoot.Path, readOnly: false, autoload: false, backup: false);
			var item = new PitItem("CloudItem");
			item.SetProperty(new { Value = 42, Provider = provider.ToString(), CreatedBy = "RAIkeep" });
			pit.Add(item);

			var remoteRelativeFile = probe.GetRelativePathForLocalFile(pit.JsonFile.FullName);
			var remoteJsonFile = new RaiFile(probe.GetRemoteFullName(remoteRelativeFile));
			var keepArtifactsForInspection = false;

			try
			{
				if (pitRoot.Exists())
					new RaiFile(pitRoot.Path).rmdir(depth: 10, deleteFiles: true);

				Assert.True(
					probe.Observer.WaitForMissing(remoteDir.Path, TimeSpan.FromMinutes(2), out _),
					$"Remote baseline directory did not vanish after local cleanup. {probe.LastFailure}");

				pitRoot.mkdir();

				var saveTimer = Stopwatch.StartNew();
				pit.Save(force: true);
				saveTimer.Stop();

				Assert.True(pit.JsonFile.Cloud);
				Assert.True(pit.JsonFile.Exists());
				Assert.True(probe.Observer.WaitForFileContainingAll(remoteJsonFile.FullName, TimeSpan.FromMinutes(2), out var createSeen, "CloudItem", provider.ToString(), "RAIkeep"), probe.LastFailure);

				var updateTimer = Stopwatch.StartNew();
				item.SetProperty(new { Value = 84, UpdatedBy = "Nkosikasi" });
				pit.Save(force: true);
				updateTimer.Stop();

				Assert.True(probe.Observer.WaitForFileContainingAll(remoteJsonFile.FullName, TimeSpan.FromMinutes(2), out var updateSeen, "84", "UpdatedBy", "Nkosikasi"), probe.LastFailure);

				var deleteTimer = Stopwatch.StartNew();
				new RaiFile(pitRoot.Path).rmdir(depth: 10, deleteFiles: true);
				deleteTimer.Stop();
				var localPitRootExistsAfterDelete = pitRoot.Exists();
				var localJsonFileExistsAfterDelete = pit.JsonFile.Exists();

				var deletePropagated = probe.Observer.WaitForMissing(remoteDir.Path, TimeSpan.FromMinutes(2), out var deleteSeen);
				if (!deletePropagated)
				{
					keepArtifactsForInspection = true;
					var remoteFileExists = probe.Observer.FileExists(remoteJsonFile.FullName);
					var remoteFileState = probe.Observer.DescribePathState(remoteJsonFile.FullName);
					var remoteDirListing = probe.Observer.ListDirectory(remoteDir.Path);
					throw new Xunit.Sdk.XunitException(
						$"Delete did not propagate to Mzansi within timeout. localPitRootExistsAfterDelete={localPitRootExistsAfterDelete}; localJsonFileExistsAfterDelete={localJsonFileExistsAfterDelete}; remoteFileExists={remoteFileExists}; remoteFile='{remoteJsonFile.FullName}'; remoteDir='{remoteDir.Path}'.\n" +
						$"Remote file state:\n{remoteFileState}\n\nRemote directory listing:\n{remoteDirListing}\n\nLast failure: {probe.LastFailure}");
				}

				Console.WriteLine($"JsonPit {provider} remote sync via Mzansi: save-local={saveTimer.ElapsedMilliseconds}ms save-remote={createSeen.TotalMilliseconds:F0}ms update-local={updateTimer.ElapsedMilliseconds}ms update-remote={updateSeen.TotalMilliseconds:F0}ms delete-local={deleteTimer.ElapsedMilliseconds}ms delete-remote={deleteSeen.TotalMilliseconds:F0}ms remote={probe.SshTarget}");
			}
			finally
			{
				if (!keepArtifactsForInspection)
				{
					try
					{
						if (pitRoot.Exists())
							new RaiFile(pitRoot.Path).rmdir(depth: 10, deleteFiles: true);
					}
					catch
					{
					}
				}
				else Console.WriteLine($"Preserving local and remote run directory for inspection: local='{pitRoot.Path}' remote='{remoteDir.Path}'");
			}
		}
	}
}