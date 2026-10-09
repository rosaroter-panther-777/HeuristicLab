using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeuristicLab.Next.Runtime;

/// <summary>JSON form of reports and values (NaN/Infinity allowed, enums as names).</summary>
public static class ReportJson {
  public static readonly JsonSerializerOptions Options = new() {
    WriteIndented = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() },
  };

  public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
  public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
