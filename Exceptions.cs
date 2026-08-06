using System;

namespace JsonPit;

/// <summary>
/// Thrown when JsonPit's bounded persistence/durability contract cannot be fulfilled
/// (CR003, coordinated v3.13.2): a canonical read during a known rewrite exhausted its
/// bounded retry policy, or an explicit <c>Save</c>/<c>Reload</c>/graceful <c>Dispose</c>
/// could not make accepted fragments durable. It is never thrown to report a genuinely
/// absent pit on initial creation — that remains a valid no-data case.
/// </summary>
public class JsonPitPersistenceException : System.IO.IOException
{
	public JsonPitPersistenceException(string message) : base(message) { }
	public JsonPitPersistenceException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when a second live public <see cref="Pit"/> instance is constructed for a
/// canonical pit path already owned by another live public instance in this process
/// (CR003, coordinated v3.13.2). This applies to writable and read-only public
/// instances alike. Reuse one in-memory <see cref="Pit"/> per canonical path through
/// your application's singleton, keyed container, or equivalent process-level store,
/// and <c>Dispose</c> it before legitimately reopening the pit.
/// </summary>
public class PitInstanceConflictException : InvalidOperationException
{
	/// <summary>Canonical pit path whose ownership was contested.</summary>
	public string CanonicalPath { get; }

	public PitInstanceConflictException(string canonicalPath)
		: base($"A live public Pit instance already owns the canonical pit path '{canonicalPath}' in this process. " +
			"JsonPit supports no more than one live public in-memory Pit instance per distinct canonical pit path " +
			"(writable or read-only). Share that instance through your application's singleton or keyed container, " +
			"or Dispose() the existing instance before reopening the pit.")
	{
		CanonicalPath = canonicalPath;
	}
}
