using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using JsonPit;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

/// <summary>
/// Remote sync integration tests: Nkosikazi (master) and Mzansi (client) share a pit via OneDrive.
///
/// Prerequisites:
///   - Run on Nkosikazi (the macOS machine)
///   - OneDrive configured in Os.Config.Cloud["OneDrive"] on both machines
///   - SSH access: <c>ssh Mzansi</c> works without password prompt
///   - pits CLI installed on Mzansi: <c>/usr/local/bin/pits</c>
///
/// The "better idea" for testing master transfer without waiting 60 s for ticket expiration:
/// we SSH into Mzansi and overwrite the flag files with expired timestamps, then run pits.
/// This deterministically simulates an expired ticket on Mzansi's local copy.
/// </summary>
public sealed class RemoteSyncTests : IDisposable
{
	private readonly ITestOutputHelper output;
	private readonly string testId;
	private readonly RaiPath localRoot;     // OneDrive on Nkosikazi
	private readonly string mzansiRoot;     // OneDrive on Mzansi
	private const string PitName = "SyncTest";
	private const string MzansiHost = "Mzansi";
	private const int SyncPollMs = 5_000;
	private const int SyncTimeoutMs = 600_000;  // 10 minutes

	public RemoteSyncTests(ITestOutputHelper output)
	{
		this.output = output;
		testId = $"sync-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Environment.ProcessId}";

		var localOneDrive = (string)Os.Config?.Cloud?["OneDrive"];
		if (!string.IsNullOrWhiteSpace(localOneDrive))
		{
			localRoot = new RaiPath(localOneDrive) / "RAIkeep" / "jsonpit-remote-sync-tests" / testId;
			// Mzansi's OneDrive root comes from its Os.Config — hardcoded here for SSH commands
			mzansiRoot = $"/srv/ServerData/OneDriveData/RAIkeep/jsonpit-remote-sync-tests/{testId}";
		}
	}

	public void Dispose()
	{
		try
		{
			if (localRoot?.Exists() == true)
				new RaiFile(localRoot.Path).rmdir(depth: 10, deleteFiles: true);
		}
		catch { }
		try { SshExec($"rm -rf \"{mzansiRoot}\""); }
		catch { }
	}

	/// <summary>
	/// Full remote sync scenario exercising all five requirements:
	///
	///   1. Nkosikazi creates the pit → becomes master (Master.flag, ProcessFlag).
	///   2. Mzansi seeds an entry via pits CLI → client path → change files only.
	///   3. Nkosikazi Load() merges change files → sees both entries.
	///   4. Master transfers to Mzansi after ticket expiration (simulated via flag overwrite).
	///   5. Load() always returns the most current in-memory state; only Save() persists.
	///
	/// Change file cleanup: the master deletes change files only when they are older than
	/// 600 s (10 min), so within this test window they will remain on disk.  This is by design —
	/// cloud replication needs time to propagate before files can safely be removed.
	/// </summary>
	[Fact]
	public void RemoteSync_MasterClient_FullScenario()
	{
		SkipIfPrerequisitesNotMet();
		localRoot.mkdir();
		output.WriteLine($"Test id : {testId}");
		output.WriteLine($"Local   : {localRoot.FullPath}");
		output.WriteLine($"Mzansi  : {mzansiRoot}");

		// ──────────────────────────────────────────────────────────────
		// PHASE 1 — Nkosikazi creates the pit and becomes master
		// ──────────────────────────────────────────────────────────────
		var pitFile = new PitFile(localRoot, PitName);
		output.WriteLine($"PitFile : {pitFile.FullName}");

		var pit = new Pit(pitFile, readOnly: false);
		var nkosikaziItem = new PitItem("NkosikaziEntry");
		nkosikaziItem.SetProperty(new { Source = "Nkosikazi", Note = "Created by master" });
		pit.Add(nkosikaziItem);

		// Before Save(), nothing is on disk yet
		Assert.False(pitFile.Exists(), "Pit file must not exist before Save()");

		pit.Save(force: true);

		Assert.True(pitFile.Exists(), "Pit file must exist after Save()");
		Assert.Equal("Nkosikazi", pit.MasterFlag().Originator);
		Assert.False(pit.MasterFlag().IsExpired);
		Assert.NotNull(pit["NkosikaziEntry"]);
		output.WriteLine("Phase 1  PASS — pit created, Nkosikazi is master");

		// ──────────────────────────────────────────────────────────────
		// PHASE 2 — Wait for sync, then seed an entry from Mzansi
		// ──────────────────────────────────────────────────────────────
		var mzansiPitDir = $"{mzansiRoot}/{PitName}";
		var mzansiPitFilePath = $"{mzansiPitDir}/{PitName}.pit";

		WaitForFileOnMzansi(mzansiPitFilePath, "pit file to sync to Mzansi");

		// Also wait for Master.flag — Mzansi needs it to know it is not master
		WaitForFileOnMzansi($"{mzansiPitDir}/Master.flag", "Master.flag to sync to Mzansi");

		// Create a small JSON seed file on Mzansi and run pits
		var seedJson = "[{\\\"Id\\\":\\\"MzansiEntry\\\",\\\"Source\\\":\\\"Mzansi\\\",\\\"Note\\\":\\\"Added by client\\\"}]";
		SshExec($"echo '{seedJson}' > /tmp/{PitName}.json5");
		var seedOutput = SshExec($"pits -n -s /tmp/{PitName}.json5 -r \"{mzansiRoot}/\"");
		output.WriteLine($"pits on Mzansi:\n{seedOutput}");

		// Mzansi should have created change files (it cannot write the canonical pit)
		var mzansiChangeFiles = SshExec($"ls \"{mzansiPitDir}/\" 2>/dev/null | grep '_Mzansi.json'");
		Assert.False(string.IsNullOrWhiteSpace(mzansiChangeFiles),
			"Mzansi must create change files as a client (not overwrite the pit)");
		output.WriteLine($"Mzansi change files:\n{mzansiChangeFiles}");

		// Verify Master.flag still shows Nkosikazi
		var masterAfterMzansi = SshExec($"cat \"{mzansiPitDir}/Master.flag\"");
		Assert.Contains("Nkosikazi", masterAfterMzansi);
		output.WriteLine("Phase 2  PASS — Mzansi created change files, master unchanged");

		// ──────────────────────────────────────────────────────────────
		// PHASE 3 — Wait for change files to sync back, then Load on Nkosikazi
		// ──────────────────────────────────────────────────────────────
		var pitDir = pit.PitDir;
		WaitForLocalChangeFiles(pitDir, "_Mzansi.json");

		// Re-open the pit (autoload triggers Load + MergeChanges)
		var reloaded = new Pit(pitFile, readOnly: false);

		Assert.NotNull(reloaded["NkosikaziEntry"]);
		Assert.NotNull(reloaded["MzansiEntry"]);
		Assert.Equal("Nkosikazi", reloaded["NkosikaziEntry"]["Source"]?.ToString());
		Assert.Equal("Mzansi", reloaded["MzansiEntry"]["Source"]?.ToString());
		output.WriteLine("Phase 3  PASS — master Load() sees both entries after merge");

		// Change files younger than 600 s remain — master does NOT delete them yet
		var remainingChangeFiles = pitDir.EnumerateFiles("*.json")
			.Where(f => f.Name != reloaded.JsonFile.Name)
			.ToList();
		output.WriteLine($"Change files still on disk: {remainingChangeFiles.Count} (expected: kept until 600 s old)");

		// ──────────────────────────────────────────────────────────────
		// PHASE 4 — Master transfer: expire ticket, let Mzansi claim
		// ──────────────────────────────────────────────────────────────
		// Phase 3's master merge wrote an updated pit file.  OneDrive is now
		// syncing it to Mzansi.  We must wait for that sync to complete before
		// running pits on Mzansi — otherwise it reads a half-written file.
		// In a real scenario the TicketDuration (60 s) provides this buffer
		// naturally; here we simulate expiry, so we wait explicitly.
		WaitForContentOnMzansi(mzansiPitFilePath, "MzansiEntry",
			"merged pit file to finish syncing to Mzansi");

		// "Better idea": instead of waiting 60 s for TicketDuration to elapse,
		// we overwrite the flag files on Mzansi's local copy with expired timestamps.
		// This deterministically simulates the passage of time.
		var expiredTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("o");
		SshExec($"echo 'Nkosikazi|{expiredTimestamp}' > \"{mzansiPitDir}/Master.flag\"");
		// Also expire Nkosikazi's process flag so AnyForeignProcessActive() returns false
		var nkosikaziFlagName = ProcessFlagFile.FlagName("pits");  // "Nkosikazi-pits"
		SshExec($"echo 'Nkosikazi:dotnet:1|{expiredTimestamp}' > \"{mzansiPitDir}/{nkosikaziFlagName}.flag\"");
		output.WriteLine("Expired flag files on Mzansi's local copy");

		// Seed a second entry from Mzansi — this time it should become master
		var seed2Json = "[{\\\"Id\\\":\\\"MzansiMasterEntry\\\",\\\"Source\\\":\\\"Mzansi\\\",\\\"Note\\\":\\\"Now I am master\\\"}]";
		SshExec($"echo '{seed2Json}' > /tmp/{PitName}.json5");
		var seed2Output = SshExec($"pits -n -s /tmp/{PitName}.json5 -r \"{mzansiRoot}/\"");
		output.WriteLine($"pits on Mzansi (after ticket expiry):\n{seed2Output}");

		// Master.flag should now show Mzansi
		var newMaster = SshExec($"cat \"{mzansiPitDir}/Master.flag\"");
		Assert.Contains("Mzansi", newMaster);
		output.WriteLine("Phase 4  PASS — master transferred to Mzansi after ticket expiry");

		// ──────────────────────────────────────────────────────────────
		// PHASE 5 — Nkosikazi Load() picks up all changes
		// ──────────────────────────────────────────────────────────────
		// Wait for the updated pit file (written by new master Mzansi) to sync back
		WaitForMasterChange(pitDir, "Mzansi");

		var finalPit = new Pit(pitFile, readOnly: true);

		Assert.NotNull(finalPit["NkosikaziEntry"]);
		Assert.NotNull(finalPit["MzansiEntry"]);
		Assert.NotNull(finalPit["MzansiMasterEntry"]);
		Assert.Equal("Now I am master", finalPit["MzansiMasterEntry"]["Note"]?.ToString());
		output.WriteLine("Phase 5  PASS — Nkosikazi sees all 3 entries after Mzansi became master");

		// ──────────────────────────────────────────────────────────────
		// PHASE 6 — Change file cleanup: master deletes files older than 600 s
		// ──────────────────────────────────────────────────────────────
		// Reclaim master for Nkosikazi so we can test the cleanup path locally.
		// Expire Mzansi's master flag on the local copy and also expire any
		// Mzansi process flag so AnyForeignProcessActive() returns false.
		var expiredTs = DateTimeOffset.UtcNow.AddMinutes(-5);
		var localMasterFlag = new MasterFlagFile(pitDir, "Master");
		localMasterFlag.Update(time: expiredTs, originator: "Mzansi");
		// Expire any Mzansi process flag files locally
		foreach (var flagFile in pitDir.EnumerateFiles("*.flag"))
		{
			if (flagFile.Name.StartsWith("Mzansi", StringComparison.OrdinalIgnoreCase) &&
				!flagFile.Name.Equals("Master", StringComparison.OrdinalIgnoreCase))
			{
				var tv = new TimestampedValue("Mzansi:dotnet:1", expiredTs);
				var tf = new TextFile(flagFile.FullName) { Lines = [tv.ToString()], Changed = true };
				tf.Save();
			}
		}

		// Re-open as writable so TryAcquireMaster() can claim
		var masterPit = new Pit(pitFile, readOnly: false);
		Assert.True(masterPit.TryAcquireMaster(), "Nkosikazi should reclaim master after expiring Mzansi's ticket");
		output.WriteLine("Phase 6  Nkosikazi reclaimed master");

		// Count change files before backdating
		var changeFilesBefore = pitDir.EnumerateFiles("*.json")
			.Where(f => f.Name != masterPit.JsonFile.Name)
			.ToList();
		Assert.NotEmpty(changeFilesBefore);
		output.WriteLine($"Phase 6  Change files before cleanup: {changeFilesBefore.Count}");

		// Backdate all change files to 11 minutes ago so they exceed the 600 s threshold.
		// RaiFile.FileAge uses CreationTimeUtc, so BackdateCreationTime sets that.
		// Propagation delay is skipped here (local-only test, no cloud peers need the backdate).
		var backdate = DateTime.UtcNow.AddMinutes(-11);
		foreach (var cf in changeFilesBefore)
			cf.BackdateCreationTime(backdate, propagationDelayMs: 0);

		// MergeChanges() on a master pit should now delete the aged-out change files
		masterPit.MergeChanges();

		var changeFilesAfter = pitDir.EnumerateFiles("*.json")
			.Where(f => f.Name != masterPit.JsonFile.Name)
			.ToList();
		Assert.Empty(changeFilesAfter);
		output.WriteLine("Phase 6  PASS — master cleaned up change files older than 600 s");

		// Data integrity: all 3 entries still present after cleanup
		Assert.NotNull(masterPit["NkosikaziEntry"]);
		Assert.NotNull(masterPit["MzansiEntry"]);
		Assert.NotNull(masterPit["MzansiMasterEntry"]);
		output.WriteLine("Phase 6  Data integrity verified — all entries preserved");

		output.WriteLine($"\nAll phases passed for test {testId}");
	}

	#region Prerequisite checks

	private void SkipIfPrerequisitesNotMet()
	{
		if (Environment.MachineName != "Nkosikazi")
			Assert.Skip("This test must run on Nkosikazi.");

		var localOneDrive = (string)Os.Config?.Cloud?["OneDrive"];
		if (string.IsNullOrWhiteSpace(localOneDrive))
			Assert.Skip("OneDrive not configured in Os.Config.Cloud.");

		if (localRoot == null)
			Assert.Skip("Could not resolve local OneDrive test root.");

		if (!CanSshTo(MzansiHost))
			Assert.Skip($"Cannot reach {MzansiHost} via SSH.");

		var pitsCheck = SshExec("which pits");
		if (string.IsNullOrWhiteSpace(pitsCheck) || !pitsCheck.Contains("pits"))
			Assert.Skip($"pits CLI not installed on {MzansiHost}.");
	}

	private static bool CanSshTo(string host)
	{
		try
		{
			var result = RunProcess("ssh", $"-o ConnectTimeout=5 {host} echo ok");
			return result.Trim() == "ok";
		}
		catch { return false; }
	}

	#endregion

	#region Sync polling helpers

	private void WaitForFileOnMzansi(string remotePath, string description)
	{
		output.WriteLine($"Waiting for {description} ...");
		var sw = Stopwatch.StartNew();
		while (sw.ElapsedMilliseconds < SyncTimeoutMs)
		{
			var check = SshExec($"test -f \"{remotePath}\" && echo EXISTS || echo MISSING");
			if (check.Trim() == "EXISTS")
			{
				output.WriteLine($"  synced in {sw.Elapsed.TotalSeconds:F1} s");
				return;
			}
			Thread.Sleep(SyncPollMs);
		}
		Assert.Fail($"Timed out ({SyncTimeoutMs / 1000} s) waiting for {description} on {MzansiHost}");
	}

	private void WaitForLocalChangeFiles(RaiPath pitDir, string suffix)
	{
		output.WriteLine($"Waiting for change files matching *{suffix} in {pitDir.FullPath} ...");
		var sw = Stopwatch.StartNew();
		var suffixWithoutExt = suffix.Replace(".json", "");
		while (sw.ElapsedMilliseconds < SyncTimeoutMs)
		{
			var found = pitDir.EnumerateFiles("*.json")
				.Any(f => f.Name.Contains(suffixWithoutExt, StringComparison.OrdinalIgnoreCase));
			if (found)
			{
				output.WriteLine($"  found change file(s) in {sw.Elapsed.TotalSeconds:F1} s");
				return;
			}
			Thread.Sleep(SyncPollMs);
		}
		Assert.Fail($"Timed out ({SyncTimeoutMs / 1000} s) waiting for change files matching {suffix}");
	}

	private void WaitForContentOnMzansi(string remotePath, string expectedContent, string description)
	{
		output.WriteLine($"Waiting for {description} ...");
		var sw = Stopwatch.StartNew();
		while (sw.ElapsedMilliseconds < SyncTimeoutMs)
		{
			var content = SshExec($"cat \"{remotePath}\" 2>/dev/null");
			if (content.Contains(expectedContent, StringComparison.Ordinal))
			{
				output.WriteLine($"  content arrived in {sw.Elapsed.TotalSeconds:F1} s");
				return;
			}
			Thread.Sleep(SyncPollMs);
		}
		Assert.Fail($"Timed out ({SyncTimeoutMs / 1000} s) waiting for {description} on {MzansiHost}");
	}

	private void WaitForMasterChange(RaiPath pitDir, string expectedMaster)
	{
		output.WriteLine($"Waiting for Master.flag to show '{expectedMaster}' locally ...");
		var sw = Stopwatch.StartNew();
		while (sw.ElapsedMilliseconds < SyncTimeoutMs)
		{
			try
			{
				var flag = new MasterFlagFile(pitDir, "Master");
				if (flag.Originator == expectedMaster)
				{
					output.WriteLine($"  master changed in {sw.Elapsed.TotalSeconds:F1} s");
					return;
				}
			}
			catch { }
			Thread.Sleep(SyncPollMs);
		}
		Assert.Fail($"Timed out ({SyncTimeoutMs / 1000} s) waiting for master to become {expectedMaster}");
	}

	#endregion

	#region SSH helper

	private static string SshExec(string command)
	{
		return RunProcess("ssh", $"{MzansiHost} \"{command}\"");
	}

	private static string RunProcess(string fileName, string arguments)
	{
		using var proc = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = fileName,
				Arguments = arguments,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			}
		};
		proc.Start();
		var stdout = proc.StandardOutput.ReadToEnd();
		var stderr = proc.StandardError.ReadToEnd();
		proc.WaitForExit(30_000);
		return stdout + stderr;
	}

	#endregion
}
