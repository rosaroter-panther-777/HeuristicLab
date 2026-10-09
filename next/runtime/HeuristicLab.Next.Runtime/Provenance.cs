using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HeuristicLab.Next.Runtime;

/// <summary>
/// Where a result came from. Seeded HeuristicLab runs are reproducible per runtime and platform,
/// not across them (see docs/windows-baseline.md), so the runtime is part of every result.
/// </summary>
public sealed record Provenance(
  string Tool, string ToolVersion, string Runtime, string OperatingSystem, string Architecture,
  string? InputFile, string? InputSha256, DateTimeOffset StartedAt) {

  public static Provenance Capture(string? inputFile) {
    var assembly = Assembly.GetEntryAssembly() ?? typeof(Provenance).Assembly;
    var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                  ?? assembly.GetName().Version?.ToString() ?? "unknown";
    return new Provenance(
      assembly.GetName().Name ?? "unknown", version,
      RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
      RuntimeInformation.ProcessArchitecture.ToString(),
      inputFile == null ? null : Path.GetFullPath(inputFile),
      inputFile == null ? null : Sha256(inputFile),
      DateTimeOffset.UtcNow);
  }

  private static string Sha256(string path) {
    using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
  }
}
