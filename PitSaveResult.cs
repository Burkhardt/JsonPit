namespace JsonPit;

public enum PitSaveOutcome { NothingToWrite, Wrote, PublishedAsChanges, PitNotLoaded }

/// <summary>Local persistence outcome. FragmentCount counts fragments durably published as change files.</summary>
public readonly record struct PitSaveResult(PitSaveOutcome Outcome, int FragmentCount = 0)
{
	internal static PitSaveResult FromChanges(int count) =>
		new(count == 0 ? PitSaveOutcome.NothingToWrite : PitSaveOutcome.PublishedAsChanges, count);
}
