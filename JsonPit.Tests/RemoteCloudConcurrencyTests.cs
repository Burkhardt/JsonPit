using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using OsLib;
using Xunit;

namespace JsonPit.Tests;

/// <summary>
/// CR003 §5/§6/§7 — real configured-cloud, SSH-driven split-master scenario between two
/// machines. The conflicting master claim of the remote server and the provider's
/// conflict-copy artifact are produced on the remote node and travel to this machine
/// through the real configured provider, so detection, role determination, and recovery
/// are exercised against genuine cloud propagation.
///
/// Prerequisites (each missing one produces an explicit skip; a skip is never
/// release-acceptance evidence):
///   - run on Nkosikazi, OneDrive configured in Os.Config.Cloud
///   - SSH access to Mzansi without password prompt
/// </summary>
public sealed class RemoteCloudConcurrencyTests : IDisposable
{
	private readonly ITestOutputHelper output;
	private readonly string testId;
	private readonly RaiPath localRoot;
	private readonly string mzansiRoot;
	private const string PitName = "SplitMaster";
	private const string MzansiHost = "Mzansi";
	private const int SyncPollMs = 5_000;
	private const int SyncTimeoutMs = 600_000;
	private readonly TimeSpan originalDebounce = Pit.RecoveryDebounce;

	public RemoteCloudConcurrencyTests(ITestOutputHelper output)
	{
		this.output = output;
		testId = $"split-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Environment.ProcessId}";
		Pit.RecoveryDebounce = TimeSpan.FromMilliseconds(500);
		var localOneDrive = (string)Os.Config?.Cloud?["OneDrive"];
		if (!string.IsNullOrWhiteSpace(localOneDrive))
		{
			localRoot = new RaiPath(localOneDrive) / "RAIkeep" / "jsonpit-cr003-remote" / testId;
			mzansiRoot = $"/srv/ServerData/OneDriveData/RAIkeep/jsonpit-cr003-remote/{testId}";
		}
	}

	public void Dispose()
	{
		Pit.RecoveryDebounce = originalDebounce;
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
	/// Two servers claim master before synchronization reveals the conflict; the provider
	/// preserves the canonical name for the winner and a longer conflict copy carrying the
	/// losing claim. Both artifacts reach this machine through real OneDrive propagation;
	/// the live local claimant detects the signal, publishes its tenure write set plus
	/// dirty fragments as ordinary small change files, retires only its own longer
	/// conflict flag, and never touches exact canonical Master.flag.
	/// </summary>
	[Fact]
	public void TwoServerSplitMaster_LiveLocalLoser_RecoversThroughProviderSyncedConflictSignal()
	{
		SkipIfPrerequisitesNotMet();
		localRoot.mkdir();
		var stopwatch = Stopwatch.StartNew();
		output.WriteLine($"Test id  : {testId}");
		output.WriteLine($"Provider : OneDrive");
		output.WriteLine($"Machines : {Environment.MachineName} (local claimant) / {MzansiHost} (remote claimant)");

		// ── Phase 1: the local process claims master and canonically persists ──────
		var pitDir = localRoot / PitName;
		using var pit = new Pit(pitDir, readOnly: false, autoload: false, subscriber: "cr003");
		var itemA = new PitItem("LocalTenureA"); itemA.SetProperty(new { Origin = Environment.MachineName });
		pit.Add(itemA);
		pit.Save(force: true);
		var dirtyB = new PitItem("LocalDirtyB"); dirtyB.SetProperty(new { Origin = Environment.MachineName });
		pit.Add(dirtyB); // accepted after the snapshot — stays dirty
		var localIdentity = pit.ExactProcessIdentity;
		output.WriteLine($"Local claimant identity: {localIdentity}");
		Assert.Equal(localIdentity, pit.MasterFlag().Originator);

		var remotePitDir = $"{mzansiRoot}/{PitName}";
		WaitForRemoteFile($"{remotePitDir}/Master.flag", "local master claim to reach Mzansi");

		// ── Phase 2: the remote server writes as the winning claimant, and the provider
		// preserves the losing claim as a longer conflict copy. Both artifacts are
		// produced on the remote node and travel to this machine through OneDrive. ──
		var remoteWinner = $"{MzansiHost}-cr003-777777";
		var now = DateTimeOffset.UtcNow.ToString("o");
		// Single-quote remote paths: 'Master (1).flag' contains a space and parentheses.
		SshExec($"echo '{remoteWinner}|{now}' > '{remotePitDir}/Master.flag'");
		SshExec($"echo '{localIdentity}|{now}' > '{remotePitDir}/Master (1).flag'");
		SshExec($"echo '{MzansiHost}:cr003:777777|{now}' > '{remotePitDir}/{remoteWinner}.flag'");
		var remoteListing = SshExec($"ls -la '{remotePitDir}/'");
		Assert.Contains("Master (1).flag", remoteListing); // fail fast if the conflict copy was not created
		output.WriteLine($"Remote claim written on {MzansiHost} at +{stopwatch.Elapsed.TotalSeconds:F1}s: Master.flag → {remoteWinner}, conflict copy 'Master (1).flag' → {localIdentity}");

		// ── Phase 3: real provider propagation carries the conflict signal here ────
		var conflictFlag = new RaiFile(pitDir, "Master (1)", "flag");
		WaitForLocal(() => conflictFlag.Exists() || pit.LastRecoveryStatus?.Stage == RecoveryStage.Completed,
			"provider to materialize the longer Master*.flag conflict copy locally");
		output.WriteLine($"Conflict signal visible locally at +{stopwatch.Elapsed.TotalSeconds:F1}s");

		// ── Phase 4: the live loser detects (watcher or boundary), publishes, retires ──
		WaitForLocal(() =>
		{
			if (pit.LastRecoveryStatus?.Stage == RecoveryStage.Completed && !conflictFlag.Exists())
				return true;
			pit.Save(); // operation boundary — notifications are signals, not guaranteed delivery
			return pit.LastRecoveryStatus?.Stage == RecoveryStage.Completed && !conflictFlag.Exists();
		}, "live loser recovery to publish its write set and retire its longer conflict flag");
		output.WriteLine($"Loser recovery completed at +{stopwatch.Elapsed.TotalSeconds:F1}s");

		// The union of tenure write set and dirty fragments became small ordinary files.
		var changeFiles = pitDir.EnumerateFiles("*.json")
			.Where(f => f.Name != pit.JsonFile.Name)
			.Where(f => ChangeFile.IdentityOf(f.Name) == localIdentity)
			.ToList();
		Assert.True(changeFiles.Count >= 2,
			$"Expected LocalTenureA and LocalDirtyB as ordinary change files; found {changeFiles.Count}.");
		foreach (var file in changeFiles)
		{
			Assert.NotNull(ChangeFile.ReadValidated(new RaiFile(file.FullName)));
			output.WriteLine($"Recovered fragment file: {file.NameWithExtension}");
		}

		// Exact canonical Master.flag still names the remote winner — never altered here.
		Assert.Equal(remoteWinner, new MasterFlagFile(pitDir, "Master").Originator);
		Assert.False(conflictFlag.Exists());

		// Durable audit stages exist for the recorded release evidence.
		var events = EventDirectory.Events(pitDir);
		Assert.Contains(events.Keys, k => k.Contains("_ConflictDetected_"));
		Assert.Contains(events.Keys, k => k.Contains("_Completed_"));
		output.WriteLine($"Durable audit events: {events.Count} under {pitDir.FullPath}Events");
		output.WriteLine($"Total scenario time: {stopwatch.Elapsed.TotalSeconds:F1}s");
	}

	#region Prerequisites and helpers

	private void SkipIfPrerequisitesNotMet()
	{
		if (Environment.MachineName != "Nkosikazi")
			Assert.Skip("This CR003 remote scenario must run on Nkosikazi (the configured local claimant).");
		if (string.IsNullOrWhiteSpace((string)Os.Config?.Cloud?["OneDrive"]))
			Assert.Skip("OneDrive is not configured in Os.Config.Cloud on this machine.");
		if (localRoot is null)
			Assert.Skip("Could not resolve the local OneDrive test root.");
		if (!CanSshTo(MzansiHost))
			Assert.Skip($"Cannot reach {MzansiHost} via SSH without a password prompt.");
	}

	private static bool CanSshTo(string host)
	{
		try { return RunProcess("ssh", $"-o ConnectTimeout=5 {host} echo ok").Trim() == "ok"; }
		catch { return false; }
	}

	private void WaitForRemoteFile(string remotePath, string description)
	{
		output.WriteLine($"Waiting for {description} ...");
		var sw = Stopwatch.StartNew();
		while (sw.ElapsedMilliseconds < SyncTimeoutMs)
		{
			if (SshExec($"test -f \"{remotePath}\" && echo EXISTS || echo MISSING").Trim() == "EXISTS")
			{
				output.WriteLine($"  synced in {sw.Elapsed.TotalSeconds:F1} s");
				return;
			}
			Thread.Sleep(SyncPollMs);
		}
		Assert.Fail($"Timed out ({SyncTimeoutMs / 1000} s) waiting for {description} on {MzansiHost}");
	}

	private void WaitForLocal(Func<bool> condition, string description)
	{
		output.WriteLine($"Waiting for {description} ...");
		var sw = Stopwatch.StartNew();
		while (sw.ElapsedMilliseconds < SyncTimeoutMs)
		{
			if (condition())
			{
				output.WriteLine($"  observed in {sw.Elapsed.TotalSeconds:F1} s");
				return;
			}
			Thread.Sleep(SyncPollMs);
		}
		Assert.Fail($"Timed out ({SyncTimeoutMs / 1000} s) waiting for {description}");
	}

	private static string SshExec(string command) => RunProcess("ssh", $"{MzansiHost} \"{command}\"");

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
