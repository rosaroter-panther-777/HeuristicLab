using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace HeuristicLab.Studio.Services;

/// <summary>What Studio remembers between sessions.</summary>
public sealed class StudioSettings {
  public List<string> RecentFiles { get; set; } = [];
  public string Seed { get; set; } = "";
  public string Runs { get; set; } = "1";
  public string Parallel { get; set; } = "1";
  public string? ResultsFolder { get; set; }
  /// <summary>Resources tab; 0 cores means all.</summary>
  public int ResourceCores { get; set; }
  public int ResourceThreads { get; set; } = 1;
  public int ResourceConcurrentExperiments { get; set; }
  public double ResourceMemoryGB { get; set; }
  public string ResourcePriority { get; set; } = "Normal";
}

public interface ISettingsStore {
  StudioSettings Load();
  void Save(StudioSettings settings);
}

/// <summary>JSON file, by default ~/.config/HeuristicLab.Studio/settings.json. Unreadable files give defaults.</summary>
public sealed class JsonSettingsStore(string? path = null) : ISettingsStore {
  public string Path { get; } = path ?? System.IO.Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HeuristicLab.Studio", "settings.json");

  public StudioSettings Load() {
    try {
      return File.Exists(Path) ? JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(Path)) ?? new() : new();
    } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
      return new();
    }
  }

  public void Save(StudioSettings settings) {
    try {
      Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
      File.WriteAllText(Path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
      // settings are a convenience; never fail the application over them
    }
  }
}
