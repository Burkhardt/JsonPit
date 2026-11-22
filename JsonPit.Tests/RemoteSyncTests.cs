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
		Assert.StartsWith($"{Environment.MachineName}-", pit.MasterFlag().Originator);
		Assert.Equal("Returned", pit.MasterFlag().ReadTicket().State);
		Assert.NotNull(pit["NkosikaziEntry"]);
		output.WriteLine("Phase 1  PASS — pit created, Nkosikazi is master");

		// ──────────────────────────────────────────────────────────────
		// PHASE 2 — Wait for sync, then seed an entry from Mzansi
		// ──────────────────────────────────────────────────────────────
		var mzansiPitDir = $"{mzansiRoot}/{PitName}";
		var mzansiPitFilePath = $"{mzansiPitDir}/{PitName}.pit";

		WaitForFileOnMzansi(mzansiPitFilePath, "pit file to sync to Mzansi");

		// Wait for the authority record's contents, not merely its pathname. OneDrive may
		// materialize a placeholder before the Nkosikazi ownership bytes have arrived;
		// launching pits in that window would let Mzansi claim an apparently empty ticket.
		WaitForContentOnMzansi(
			$"{mzansiPitDir}/Master.flag",
			"Nkosikazi",
			"Nkosikazi Master.flag ownership to finish syncing to Mzansi");

		var timing = PitTiming.Load(pit.PitDir);
		var returned = pit.MasterFlag().ReadTicket();
		var banRemaining = returned.Written.Value + timing.PitSync + timing.Buffer - DateTimeOffset.UtcNow;
		if (banRemaining > TimeSpan.Zero) Thread.Sleep(banRemaining + TimeSpan.FromMilliseconds(10));
		Assert.True(TicketFixture.AttemptClaim(pit).Acquired);
		var held = pit.MasterFlag().ReadTicket();
		WaitForContentOnMzansi($"{mzansiPitDir}/Master.flag", held.ToString(), "single-use Held ticket");
		Assert.True(DateTimeOffset.UtcNow < held.Claimed + timing.Ticket);

		// Create a small JSON seed file on Mzansi and run pits
		var seedJson = "[{\"Id\":\"MzansiEntry\",\"Source\":\"Mzansi\",\"Note\":\"Added by client\"}]";
		SshExec($"echo '{seedJson}' > /tmp/{PitName}.json5");
		var seedOutput = SeedOnMzansi($"/tmp/{PitName}.json5");
		output.WriteLine($"pits on Mzansi:\n{seedOutput}");

		// Mzansi should have created change files (it cannot write the canonical pit).
		// Match the machine in the identity segment; current change-file names
		// also contain the application, PID, and a short payload checksum.
		var mzansiChangeFiles = SshExec($"ls \"{mzansiPitDir}/\" 2>/dev/null | grep '_Mzansi'");
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
		WaitForLocalChangeFiles(pitDir, MzansiHost);
		// One live public Pit per canonical path per process (CR003 §4): dispose before reopening.
		pit.Dispose();
		// The provider may have dehydrated the local canonical while syncing; wait until
		// its bytes are locally readable so the bounded in-library retry window is not
		// spent on File Provider hydration timeouts.
		WaitForLocalReadable(pitFile);
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
		// Reopening merged the remote delta in memory. Mzansi's next winning
		// save must reconcile it into the canonical file as part of that turn.
		reloaded.Dispose();
		var expiredTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("o");
		SshExec($"echo 'Nkosikazi|{expiredTimestamp}' > '{mzansiPitDir}/Master.flag'");
		SshExec($"for f in '{mzansiPitDir}'/Nkosikazi-*.flag; do [ -f \"$f\" ] && echo 'Nkosikazi:dotnet:1|{expiredTimestamp}' > \"$f\"; done; true");
		output.WriteLine("Expired flag files on Mzansi's local copy");

		// Seed a second entry from Mzansi — this time it should become master
		var seed2Json = "[{\"Id\":\"MzansiMasterEntry\",\"Source\":\"Mzansi\",\"Note\":\"Now I am master\"}]";
		SshExec($"echo '{seed2Json}' > /tmp/{PitName}.json5");
		var seed2Output = SeedOnMzansi($"/tmp/{PitName}.json5");
		output.WriteLine($"pits on Mzansi (after ticket expiry):\n{seed2Output}");

		// Master.flag should now show Mzansi
		WaitForContentOnMzansi($"{mzansiPitDir}/Master.flag", "Mzansi",
			"Mzansi master ownership after successful seed");
		var newMaster = SshExec($"cat \"{mzansiPitDir}/Master.flag\"");
		Assert.Contains("Mzansi", newMaster);
		output.WriteLine("Phase 4  PASS — master transferred to Mzansi after ticket expiry");

		// ──────────────────────────────────────────────────────────────
		// PHASE 5 — Nkosikazi Load() picks up all changes
		// ──────────────────────────────────────────────────────────────
		// Wait for the updated pit file (written by new master Mzansi) to sync back
		WaitForMasterChange(pitDir, "Mzansi");
		WaitForLocalReadable(pitFile);

		var finalPit = new Pit(pitFile, readOnly: true);

		Assert.NotNull(finalPit["NkosikaziEntry"]);
		Assert.NotNull(finalPit["MzansiEntry"]);
		Assert.NotNull(finalPit["MzansiMasterEntry"]);
		Assert.Equal("Now I am master", finalPit["MzansiMasterEntry"]["Note"]?.ToString());
		finalPit.Dispose(); // release canonical-path ownership before phase 6 reopens the pit
		output.WriteLine("Phase 5  PASS — Nkosikazi sees all 3 entries after Mzansi became master");

		// ──────────────────────────────────────────────────────────────
		// PHASE 6 — Change file cleanup: two-stage current-master-only protocol (CR003).
		// A file becomes deletable only after (1) merge, (2) a successful canonical save
		// that accounts for it, and (3) a propagation grace measured from that save —
		// original file age does not count. The 10-minute production grace is shortened
		// through the settable property for this local phase.
		// ──────────────────────────────────────────────────────────────
		// Reclaim master for Nkosikazi so we can test the cleanup path locally.
		var expiredTs = DateTimeOffset.UtcNow.AddMinutes(-5);
		var localMasterFlag = new MasterFlagFile(pitDir, "Master");
		TicketFixture.WriteMaster(localMasterFlag, time: expiredTs, originator: "Mzansi");
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
		WaitForLocalReadable(pitFile);
		var masterPit = new Pit(pitFile, readOnly: false);
		var originalGrace = Pit.ChangeFileCleanupGrace;
		try
		{
			var changeFilesBefore = pitDir.EnumerateFiles("*.json")
				.Where(f => f.Name != masterPit.JsonFile.Name)
				.ToList();
			Assert.NotEmpty(changeFilesBefore);
			output.WriteLine($"Phase 6  Change files before cleanup: {changeFilesBefore.Count}");

			TicketFixture.SetCleanupGrace(TimeSpan.FromSeconds(2));

			// First pass: merge + canonical save. CR021 cleanup eligibility is measured
			// from each immutable receipt's first canonicalization timestamp. Receipts
			// created by earlier merges may therefore already be eligible after the
			// shortened grace is applied here.
			masterPit.MergeChanges();
			var stillPresent = pitDir.EnumerateFiles("*.json")
				.Where(f => f.Name != masterPit.JsonFile.Name)
				.ToList();
			output.WriteLine($"Phase 6  Change files after first receipt-aware cleanup: {stillPresent.Count}");

			Thread.Sleep(timing.PitSync + timing.Buffer + TimeSpan.FromSeconds(3));

			// Second pass: revalidates exact master authority and canonical health, then deletes.
			masterPit.MergeChanges();

			var changeFilesAfter = pitDir.EnumerateFiles("*.json")
				.Where(f => f.Name != masterPit.JsonFile.Name)
				.ToList();
			Assert.Empty(changeFilesAfter);
			output.WriteLine("Phase 6  PASS — master cleaned up change files after the post-save grace");

			// Data integrity: all 3 entries still present after cleanup
			Assert.NotNull(masterPit["NkosikaziEntry"]);
			Assert.NotNull(masterPit["MzansiEntry"]);
			Assert.NotNull(masterPit["MzansiMasterEntry"]);
			output.WriteLine("Phase 6  Data integrity verified — all entries preserved");
		}
		finally
		{
			TicketFixture.SetCleanupGrace(originalGrace);
			masterPit.Dispose();
		}

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
			var result = SshSystem.ExecuteRemoteCommand(
				host,
				"echo ok",
				timeoutMilliseconds: 5_000,
				options: new[] { "-o", "ConnectTimeout=5" });
			return result.Output.Trim() == "ok";
		}
		catch { return false; }
	}

	#endregion

	#region Sync polling helpers

	/// <summary>
	/// Waits until the provider has (re)hydrated the local canonical so its bytes are
	/// actually readable. macOS OneDrive can dehydrate a file during sync; reading a
	/// placeholder then blocks until the File Provider times out, which would otherwise
	/// burn JsonPit's bounded in-library retry window on environmental hydration delays.
	/// </summary>
	private void WaitForLocalReadable(PitFile pitFile)
	{
		var sw = Stopwatch.StartNew();
		while (sw.ElapsedMilliseconds < SyncTimeoutMs)
		{
			try
			{
				var bytes = pitFile.ReadAllBytes();
				if (bytes.Length > 1)
				{
					output.WriteLine($"  canonical locally readable ({bytes.Length} bytes) after {sw.Elapsed.TotalSeconds:F1} s");
					return;
				}
			}
			catch (System.IO.IOException) { }
			Thread.Sleep(SyncPollMs);
		}
		Assert.Fail($"Timed out ({SyncTimeoutMs / 1000} s) waiting for the provider to hydrate {pitFile.FullName}");
	}

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

	private void WaitForLocalChangeFiles(RaiPath pitDir, string machine)
	{
		output.WriteLine($"Waiting for change files from {machine} in {pitDir.FullPath} ...");
		var sw = Stopwatch.StartNew();
		while (sw.ElapsedMilliseconds < SyncTimeoutMs)
		{
			var found = pitDir.EnumerateFiles("*.json")
				.Any(f => ChangeFile.IdentityOf(f.Name)?.StartsWith(machine + "-", StringComparison.OrdinalIgnoreCase) == true);
			if (found)
			{
				output.WriteLine($"  found change file(s) in {sw.Elapsed.TotalSeconds:F1} s");
				return;
			}
			Thread.Sleep(SyncPollMs);
		}
		Assert.Fail($"Timed out ({SyncTimeoutMs / 1000} s) waiting for change files from {machine}");
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
				// v3.13.2 owners are exact process identities ({Machine}-{Subscriber}-{PID});
				// match the machine prefix so the check works for legacy values too.
				if (flag.Originator?.StartsWith(expectedMaster, StringComparison.OrdinalIgnoreCase) == true)
				{
					output.WriteLine($"  master changed in {sw.Elapsed.TotalSeconds:F1} s → {flag.Originator}");
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

	private string SeedOnMzansi(string sourceFile)
	{
		var result = SshSystem.ExecuteRemoteCommand(MzansiHost, BuildRemotePitsSeedCommand(sourceFile), timeoutMilliseconds: 120_000);
		Assert.True(result.Succeeded,
			$"Remote seed failed: exit={result.ExitCode}, timedOut={result.TimedOut}, stdout={result.StandardOutput}, stderr={result.StandardError}");
		return result.Output;
	}

	private string BuildRemotePitsSeedCommand(string sourceFile)
	{
		var command = new PitsCommand();
		var request = PitsSeedRequest.ForPit(PitName, new RaiFile(sourceFile)) with
		{
			Options = new PitsCommandOptions
			{
				PitRoot = new RaiPath(mzansiRoot),
				NoLogo = true
			}
		};
		return command.BuildPosixShellCommand(command.BuildSeedArguments(request));
	}

	private static string SshExec(string command)
	{
		return SshSystem.ExecuteRemoteCommand(MzansiHost, command, timeoutMilliseconds: 30_000).Output;
	}

	#endregion
}
