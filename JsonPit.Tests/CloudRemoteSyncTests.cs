using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using JsonPit;
using OsLib;
using Xunit;
using System.Linq;

namespace JsonPit.Tests
{
	public class CloudRemoteSyncTests
	{
		[Theory]
		[InlineData(Cloud.GoogleDrive)]
		[InlineData(Cloud.Dropbox)]
		[InlineData(Cloud.OneDrive)]
		public void Pit_SyncsWithMzansi(Cloud provider)
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
			var remoteChangeDir = probe.GetRemoteDirectory(remoteRelativeDir + "Changes/");

			var itemId = "CloudItem_" + Guid.NewGuid().ToString("N");
			var peerItemId = "PeerItem_" + Guid.NewGuid().ToString("N");
			var marker = "marker-" + Guid.NewGuid().ToString("N");
			var pit = new Pit(pitRoot.Path, readOnly: false, autoload: true, backup: false);
			var item = new PitItem(itemId);
			item.SetProperty(new { Value = 42, Provider = provider.ToString(), CreatedBy = "RAIkeep", Marker = marker });
			pit.Add(item);

			var remoteRelativeFile = probe.GetRelativePathForLocalFile(pit.JsonFile.FullName);
			var remoteJsonFile = new RaiFile(probe.GetRemoteFullName(remoteRelativeFile));
			var remoteMasterFlag = new RaiFile(probe.GetRemoteFullName(remoteRelativeDir + "Changes/Master.flag"));
			var remoteProcessFlag = new RaiFile(probe.GetRemoteFullName(remoteRelativeDir + $"Changes/{Environment.MachineName}.flag"));
			var remoteMachineName = GetRemoteMachineName(probe);
			var remoteSeedSource = probe.GetRemoteFullName(remoteRelativeDir + $"seed-{peerItemId}.json5");
			var keepArtifactsForInspection = ShouldPreserveArtifacts(provider);

			try
			{
				pitRoot.mkdir();
				var initialLocalSize = GetLocalFileSize(pit.JsonFile.FullName);

				var saveTimer = Stopwatch.StartNew();
				pit.Save(force: true);
				saveTimer.Stop();
				var localSizeAfterSave = GetLocalFileSize(pit.JsonFile.FullName);

				Assert.True(pit.JsonFile.Cloud);
				Assert.True(pit.JsonFile.Exists());
				Assert.True(localSizeAfterSave > initialLocalSize, $"Expected local pit file size to increase. before={initialLocalSize}; after={localSizeAfterSave}; file={pit.JsonFile.FullName}");
				Assert.True(WaitForRemoteFileExists(probe, remoteJsonFile.FullName, TimeSpan.FromMinutes(2), out var createSeen),
					$"Remote pit file did not appear. {probe.LastFailure}\n{probe.Observer.DescribePathState(remoteDir.Path)}");

				var localMasterFlag = new RaiFile(pit.ChangesDir, "Master", "flag");
				var localProcessFlag = new RaiFile(pit.ChangesDir, Environment.MachineName, "flag");
				Assert.True(localMasterFlag.Exists(), $"Expected local Master.flag at {localMasterFlag.FullName}");
				Assert.True(localProcessFlag.Exists(), $"Expected local process flag at {localProcessFlag.FullName}");
				Assert.True(WaitForRemoteFileExists(probe, remoteMasterFlag.FullName, TimeSpan.FromMinutes(2), out var masterSeen),
					$"Remote Master.flag did not appear. {probe.LastFailure}\n{probe.Observer.ListDirectory(remoteChangeDir.Path)}");
				Assert.True(WaitForRemoteFileExists(probe, remoteProcessFlag.FullName, TimeSpan.FromMinutes(2), out var processSeen),
					$"Remote process flag did not appear. {probe.LastFailure}\n{probe.Observer.ListDirectory(remoteChangeDir.Path)}");

				var updateTimer = Stopwatch.StartNew();
				item.SetProperty(new { Value = 84, UpdatedBy = "Nkosikasi" });
				pit.Save(force: true);
				updateTimer.Stop();

				var remoteSeedTimer = Stopwatch.StartNew();
				RunRemotePitSeeder(probe, remoteSeedSource, remoteJsonFile.FullName, peerItemId, provider, marker);
				remoteSeedTimer.Stop();

				Assert.True(probe.Observer.WaitForFileContainingAll(remoteSeedSource, TimeSpan.FromMinutes(2), out var remoteSeedSeen, peerItemId, provider.ToString(), "Mzansi", "peer-" + marker),
					$"Remote seed file did not contain expected fresh peer item payload. {probe.LastFailure}\n{probe.Observer.DescribePathState(remoteSeedSource)}");
				Assert.True(WaitForRemoteChangePitContainingAll(probe, remoteChangeDir.Path, TimeSpan.FromMinutes(2), out var remoteWriterSeen, peerItemId, provider.ToString(), "Mzansi", "peer-" + marker),
					$"Remote change pit did not appear for '{remoteMachineName}'. {probe.LastFailure}\n{probe.Observer.ListDirectory(remoteChangeDir.Path)}");
				Assert.True(WaitForLocallySyncedPitItem(pitRoot.Path, peerItemId, TimeSpan.FromMinutes(2), out var localSyncSeen, out var diskLoadedPit),
					$"Fresh Load() from local disk did not observe remotely written item '{peerItemId}' under '{pitRoot.Path}'.");
				Assert.Equal(126, diskLoadedPit.Get(peerItemId)?["Value"]?.ToObject<int>());
				Assert.Equal("Mzansi", diskLoadedPit.Get(peerItemId)?["CreatedBy"]?.ToObject<string>());
				Assert.False(string.IsNullOrWhiteSpace(diskLoadedPit.Get(peerItemId)?["SeededAtUtc"]?.ToObject<string>()));
				Assert.False(string.IsNullOrWhiteSpace(diskLoadedPit.Get(peerItemId)?["SeedNonce"]?.ToObject<string>()));

				var mergeTimer = Stopwatch.StartNew();
				pit.Reload();
				pit.Save(force: true);
				mergeTimer.Stop();

				var mergeSeen = localSyncSeen;
				var merged = new Pit(pitRoot.Path, readOnly: false, autoload: false, backup: false);
				merged.Load(undercover: true);
				Assert.NotNull(merged.Get(itemId));
				Assert.NotNull(merged.Get(peerItemId));
				Assert.Equal(126, merged.Get(peerItemId)?["Value"]?.ToObject<int>());
				Assert.Equal("Mzansi", merged.Get(peerItemId)?["CreatedBy"]?.ToObject<string>());
				Assert.False(string.IsNullOrWhiteSpace(merged.Get(peerItemId)?["SeededAtUtc"]?.ToObject<string>()));
				Assert.False(string.IsNullOrWhiteSpace(merged.Get(peerItemId)?["SeedNonce"]?.ToObject<string>()));

				Console.WriteLine(
					$"JsonPit {provider} remote sync via Mzansi: save-local={saveTimer.ElapsedMilliseconds}ms save-remote={createSeen.TotalMilliseconds:F0}ms " +
					$"flags-master={masterSeen.TotalMilliseconds:F0}ms flags-process={processSeen.TotalMilliseconds:F0}ms update-local={updateTimer.ElapsedMilliseconds}ms " +
					$"remote-seed={remoteSeedTimer.ElapsedMilliseconds}ms seed-file={remoteSeedSeen.TotalMilliseconds:F0}ms remote-change-pit={remoteWriterSeen.TotalMilliseconds:F0}ms local-load={localSyncSeen.TotalMilliseconds:F0}ms " +
					$"merge-local={mergeTimer.ElapsedMilliseconds}ms merge-remote={mergeSeen.TotalMilliseconds:F0}ms remote={probe.SshTarget} file={remoteJsonFile.FullName}");
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

		private static bool ShouldPreserveArtifacts(Cloud provider)
		{
			if (provider == Cloud.GoogleDrive)
				return true;

			var keep = Environment.GetEnvironmentVariable("RAIKEEP_KEEP_CLOUD_TEST_ARTIFACTS");
			if (string.IsNullOrWhiteSpace(keep))
				return false;

			return !string.Equals(keep, "0", StringComparison.OrdinalIgnoreCase) &&
				!string.Equals(keep, "false", StringComparison.OrdinalIgnoreCase);
		}

		private static long GetLocalFileSize(string filePath)
		{
			return File.Exists(filePath) ? new FileInfo(filePath).Length : 0L;
		}

		private static long GetRemoteFileSize(RemoteCloudSyncProbe probe, string remoteFile)
		{
			var result = probe.Observer.ExecuteScript($"if [ -f '{remoteFile.Replace("'", "'\"'\"'")}' ]; then wc -c < '{remoteFile.Replace("'", "'\"'\"'")}' | tr -d ' '; else printf 0; fi");
			if (result.ExitCode != 0)
				return 0L;

			return long.TryParse(result.StandardOutput.Trim(), out var size) ? size : 0L;
		}

		private static bool WaitForRemoteFileExists(RemoteCloudSyncProbe probe, string remoteFile, TimeSpan timeout, out TimeSpan elapsed)
		{
			var watch = Stopwatch.StartNew();
			while (watch.Elapsed <= timeout)
			{
				if (probe.Observer.FileExists(remoteFile))
				{
					elapsed = watch.Elapsed;
					return true;
				}

				System.Threading.Thread.Sleep(1000);
			}

			elapsed = watch.Elapsed;
			return false;
		}

		private static bool WaitForLocallySyncedPitItem(string pitRoot, string itemId, TimeSpan timeout, out TimeSpan elapsed, out Pit loadedPit)
		{
			var watch = Stopwatch.StartNew();
			while (watch.Elapsed <= timeout)
			{
				var candidate = new Pit(pitRoot, readOnly: false, autoload: false, backup: false);
				candidate.Load(undercover: true);
				candidate.Reload();
				if (candidate.Get(itemId) != null)
				{
					elapsed = watch.Elapsed;
					loadedPit = candidate;
					return true;
				}

				System.Threading.Thread.Sleep(1000);
			}

			elapsed = watch.Elapsed;
			loadedPit = null;
			return false;
		}

		private static bool WaitForRemoteFileContentChange(RemoteCloudSyncProbe probe, string remoteFile, string previousContent, TimeSpan timeout, out TimeSpan elapsed)
		{
			var watch = Stopwatch.StartNew();
			while (watch.Elapsed <= timeout)
			{
				if (probe.Observer.FileExists(remoteFile))
				{
					var currentContent = probe.Observer.ReadFile(remoteFile) ?? string.Empty;
					if (!string.IsNullOrWhiteSpace(currentContent) && !string.Equals(currentContent, previousContent ?? string.Empty, StringComparison.Ordinal))
					{
						elapsed = watch.Elapsed;
						return true;
					}
				}

				System.Threading.Thread.Sleep(1000);
			}

			elapsed = watch.Elapsed;
			return false;
		}

		private static bool WaitForRemoteChangePitContainingAll(RemoteCloudSyncProbe probe, string remoteChangeDir, TimeSpan timeout, out TimeSpan elapsed, params string[] expectedFragments)
		{
			var watch = Stopwatch.StartNew();
			while (watch.Elapsed <= timeout)
			{
				var command =
					$"if [ -d {QuoteForBash(remoteChangeDir)} ]; then " +
					$"find {QuoteForBash(remoteChangeDir)} -type f -name '*.pit' -print -exec cat {{}} \\; ; " +
					$"fi";

				var result = probe.Observer.ExecuteScript(command, timeoutMilliseconds: 120000);
				if (result.ExitCode == 0 && expectedFragments.All(fragment => result.StandardOutput.Contains(fragment, StringComparison.Ordinal)))
				{
					elapsed = watch.Elapsed;
					return true;
				}

				System.Threading.Thread.Sleep(1000);
			}

			elapsed = watch.Elapsed;
			return false;
		}

		private static string GetRemoteMachineName(RemoteCloudSyncProbe probe)
		{
			var result = probe.Observer.ExecuteScript("hostname");
			Assert.True(result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput), $"Could not resolve remote machine name. {probe.LastFailure}");
			return result.StandardOutput.Trim();
		}

		private static void RunRemotePitSeeder(RemoteCloudSyncProbe probe, string remoteSourceFile, string remotePitFile, string peerItemId, Cloud provider, string marker)
		{
			var payload = BuildRemoteSeedPayload(peerItemId, provider, marker);
			var encodedPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
			var sourceDir = new RaiFile(remoteSourceFile).Path;
			var pitDir = new RaiFile(remotePitFile).Path;
			var command =
				$"mkdir -p {QuoteForBash(sourceDir)} {QuoteForBash(pitDir)} && " +
				$"printf '%s' {QuoteForBash(encodedPayload)} | base64 --decode > {QuoteForBash(remoteSourceFile)} && " +
				$"pits -n -s {QuoteForBash(remoteSourceFile)} -d {QuoteForBash(remotePitFile)}";

			var result = probe.Observer.ExecuteScript(command, timeoutMilliseconds: 180000);
			Assert.True(result.ExitCode == 0, $"Remote PitSeeder invocation failed. {probe.LastFailure}");
		}

		private static string BuildRemoteSeedPayload(string peerItemId, Cloud provider, string marker)
		{
			var seededAtUtc = DateTimeOffset.UtcNow.ToString("o");
			var seedNonce = Guid.NewGuid().ToString("N");
			return "[\n" +
				$"  {{ Id: \"{peerItemId}\", Name: \"{peerItemId}\", Value: 126, Provider: \"{provider}\", CreatedBy: \"Mzansi\", Marker: \"peer-{marker}\", SeededAtUtc: \"{seededAtUtc}\", SeedNonce: \"{seedNonce}\" }}\n" +
				"]\n";
		}

		private static string QuoteForBash(string value)
		{
			return $"'{(value ?? string.Empty).Replace("'", "'\"'\"'")}'";
		}
	}
}