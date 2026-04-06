using System;
using System.Globalization;

namespace JsonPit;

/// <summary>
/// Value with an attached timestamp and round-trip string format.
/// Persisted as "value|timestamp" where timestamp uses ISO 8601 round-trip format.
/// </summary>
public class TimestampedValue
{
	public DateTimeOffset Time { get; set; }
	public string Value { get; set; }

	public override string ToString() => $"{Value}|{Time.UtcDateTime:o}";

	public TimestampedValue(object value, DateTimeOffset? time = null)
	{
		Value = value?.ToString() ?? string.Empty;
		Time = time ?? DateTimeOffset.UtcNow;
	}

	public TimestampedValue(string valueAndTime)
	{
		if (string.IsNullOrEmpty(valueAndTime))
		{
			Value = "";
			Time = DateTimeOffset.UtcNow;
			return;
		}

		var parts = valueAndTime.Split('|');
		Value = parts[0];
		Time = parts.Length == 2 && parts[1].Length > 0
			? DateTimeOffset.ParseExact(parts[1], "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
			: DateTimeOffset.UtcNow;
	}

	public TimestampedValue() { }
}
