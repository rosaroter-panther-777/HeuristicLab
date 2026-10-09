using Avalonia.Headless;
using HeuristicLab.Studio.Services;
using HeuristicLab.Studio.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Studio.Tests;

[TestClass]
public class SettingsTests {
  private static HeadlessUnitTestSession session = null!;
  private static string LegacyGaTsp => Path.Combine(AppContext.BaseDirectory, "TestData", "GA_TSP.hl");

  [ClassInitialize]
  public static void Start(TestContext _) => session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

  [TestMethod]
  public async Task RecentFilesAndRunSettingsSurviveARestart() {
    var path = Path.Combine(Path.GetTempPath(), $"studio-settings-{Guid.NewGuid():N}", "settings.json");
    try {
      var (recent, seed, runs, reopened) = await session.Dispatch(async () => {
        var first = new MainViewModel(null, null, new JsonSettingsStore(path));
        await first.LoadAsync(LegacyGaTsp);
        first.SeedText = "42";
        first.RepetitionsText = "5";
        first.SaveSettings();

        var second = new MainViewModel(null, null, new JsonSettingsStore(path));
        second.SelectedRecentFile = second.RecentFiles.First();
        for (int i = 0; i < 100 && !second.HasDocument; i++) await Task.Delay(20);
        return (second.RecentFiles.ToList(), second.SeedText, second.RepetitionsText, second.HasDocument);
      }, CancellationToken.None);
      CollectionAssert.AreEqual(new[] { Path.GetFullPath(LegacyGaTsp) }, recent);
      Assert.AreEqual("42", seed);
      Assert.AreEqual("5", runs);
      Assert.IsTrue(reopened, "selecting a recent file opens it");
    } finally {
      Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
  }

  [TestMethod]
  public void CorruptSettingsGiveDefaults() {
    var path = Path.Combine(Path.GetTempPath(), $"studio-settings-{Guid.NewGuid():N}.json");
    File.WriteAllText(path, "{ not json");
    try {
      var settings = new JsonSettingsStore(path).Load();
      Assert.AreEqual(0, settings.RecentFiles.Count);
      Assert.AreEqual("1", settings.Runs);
    } finally { File.Delete(path); }
  }
}
