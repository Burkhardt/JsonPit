using System;
using OsLib;

namespace JsonPit.Tests;

/// <summary>Exercise claim primitives and reproduce peer states without starting a second host.</summary>
internal static class TicketFixture
{
#pragma warning disable RAI018 // Direct calls let protocol tests observe rejection and the earliest write instant.
	internal static (bool Acquired, DateTimeOffset EarliestWriteUtc) AttemptClaim(Pit pit) => pit.TryAcquireMaster();

	internal static void WriteProcess(ProcessFlagFile flag, string process = null) => flag.Update(process: process);
#pragma warning restore RAI018

#pragma warning disable RAI019 // A simulated peer must be able to install an expired or foreign ticket.
	internal static MasterFlagFile PeerFlag(RaiPath directory, string server) => new MasterFlagFile(directory, "Master", server);
	internal static void WritePeerTicket(MasterFlagFile flag, MasterTicket ticket) => flag.WriteTicket(ticket);
	internal static void WriteMaster(MasterFlagFile flag, DateTimeOffset? time = null, string originator = null) =>
		flag.Update(time, originator);
#pragma warning restore RAI019

#pragma warning disable RAI020 // Boundary tests set and restore the cleanup window without waiting ten minutes.
	internal static void SetCleanupGrace(TimeSpan value) => Pit.ChangeFileCleanupGrace = value;
#pragma warning restore RAI020
}
