using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JsonPit;

/// <summary>JSON strings remain strings; lifecycle dates are interpreted by PitItem.</summary>
internal static class PitJson
{
    public static JArray ParseArray(string json) => Parse(json) as JArray ?? throw new JsonReaderException("Expected a JSON array.");
    public static JObject ParseObject(string json) => Parse(json) as JObject ?? throw new JsonReaderException("Expected a JSON object.");

    public static JToken Parse(string json)
    {
        using var text = new StringReader(json);
        using var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None };
        var result = JToken.Load(reader);
        while (reader.Read())
            if (reader.TokenType != JsonToken.Comment) throw new JsonReaderException("Additional content after the JSON value.");
        return result;
    }
}
