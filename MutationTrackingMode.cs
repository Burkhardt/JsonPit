namespace JsonPit;

/// <summary>In-memory mutation policy. Never persisted in a pit or change file.</summary>
public enum MutationTrackingMode
{
	/// <summary>
	/// Use tracked indexers and mutation methods. No fallback comparisons at persistence
	/// boundaries; unreported changes such as direct JValue.Value edits are unsupported.
	/// </summary>
	TrackedChangesOnly,
	/// <summary>
	/// Track normal operations immediately and reconcile unreported changes before
	/// persistence or state replacement. Unreported edits will receive their history
	/// timestamp when detected. Intermediate unreported values may be coalesced.
	/// </summary>
	TrackedChangesWithFallback
}
