using System;
using Newtonsoft.Json.Linq;
using OsLib;

namespace JsonPit;

public sealed record PitTiming(TimeSpan Ticket, TimeSpan PitSync, TimeSpan FlagSync, TimeSpan Buffer)
{
	public static PitTiming Load(RaiPath pitDirectory)
	{
		var root = pitDirectory.Parent.Parent;
		if (pitDirectory.Cloud)
		{
			root = pitDirectory;
			while (root.Parent.Cloud && root.Parent.FullPath != root.FullPath) root = root.Parent;
		}
		var file = new TextFile(root / "JsonPit", "timing", "json5");
		var config = file.Exists() ? PitJson.ParseObject(file.ReadAllText()) : new JObject();
		TimeSpan Seconds(string key, int fallback)
		{
			if (config[key] is { } token && token.Type is not (JTokenType.Integer or JTokenType.Float))
				throw new FormatException($"Expected a number for {key}.");
			var value = config.Value<double?>(key) ?? fallback;
			if (!double.IsFinite(value) || value < 0 || value > int.MaxValue / 1000)
				throw new FormatException($"Invalid timing value for {key}.");
			return TimeSpan.FromSeconds(value);
		}
		var timing = new PitTiming(Seconds("TicketDurationSeconds", 120), Seconds("SyncPitDurationSeconds", 60),
			Seconds("SyncFlagDurationSeconds", 15), Seconds("BufferSeconds", 5));
		if (timing.Ticket <= timing.PitSync + timing.FlagSync + timing.Buffer)
			throw new FormatException("TicketDurationSeconds must exceed the combined propagation windows and buffer.");
		return timing;
	}
}

internal sealed record MasterTicket(string State, string Owner, DateTimeOffset Claimed, DateTimeOffset? Written, string Machine)
{
	public static MasterTicket Parse(string line)
	{
		var fields = line.Split('|');
		if (fields.Length == 2 && fields[0].Length > 0)
			return new("Held", fields[0], DateTimeOffset.Parse(fields[1]), null, "");
		if (fields.Length != 5 || fields[0] is not ("Held" or "Returned") || fields[1].Length == 0 ||
			(fields[0] == "Returned" && (fields[3].Length == 0 || fields[4].Length == 0)))
			throw new FormatException("Invalid Master.flag record.");
		return new(fields[0], fields[1], DateTimeOffset.Parse(fields[2]),
			fields[3].Length == 0 ? null : DateTimeOffset.Parse(fields[3]), fields[4]);
	}

	public override string ToString() => $"{State}|{Owner}|{Claimed:O}|{Written:O}|{Machine}";
}
