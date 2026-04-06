using System;
using System.Collections.Generic;
namespace JsonPit;
/// <summary>
/// Timestamp comparison extensions for PitItem.
/// </summary>
public static class PitItemExtensions
{
	/// <summary>Tolerance in ticks for timestamp comparison. Zero means exact match.</summary>
	public static long dtSharp = 0;
	public static bool isLike(this DateTimeOffset dto1, DateTimeOffset dto2) =>
		Math.Abs(dto1.UtcTicks - dto2.UtcTicks) <= dtSharp;
	public static DateTimeOffset aligned(this DateTimeOffset dto1, DateTimeOffset dto2) =>
		dto1.isLike(dto2) ? dto2 : dto1;
}
/// <summary>
/// Equality comparer for PitItem using IEquatable implementation.
/// </summary>
class PitItemEqualityComparer : IEqualityComparer<PitItem>
{
	public bool Equals(PitItem d1, PitItem d2) =>
		(d1, d2) switch
		{
			(null, null) => true,
			(null, _) or (_, null) => false,
			_ => d1.Equals(d2)
		};
	public int GetHashCode(PitItem x) => x?.GetHashCode() ?? 0;
}
