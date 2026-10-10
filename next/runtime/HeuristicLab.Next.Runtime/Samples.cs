using HEAL.Attic;
using HeuristicLab.Common;
using HeuristicLab.Core;

namespace HeuristicLab.Next.Runtime;

/// <param name="Id">File name without extension, e.g. "GA_TSP".</param>
/// <param name="Limitation">Why the sample cannot run here, if it cannot.</param>
public sealed record SampleInfo(string Id, string Group, string Name, string Description, string ItemName, string? Limitation);

/// <summary>
/// The samples of HeuristicLab's start page: the .hl files of HeuristicLab.Optimizer/Documents,
/// embedded in this assembly, in the start page's groups. Every Load returns a fresh copy.
/// </summary>
public static class Samples {
  public const string StandardProblems = "Standard Problems", DataAnalysis = "Data Analysis", Scripts = "Scripts";

  // the start page's group lookup (HeuristicLab.Optimizer StartPage.FillGroupLookup)
  private static readonly (string Group, string[] Ids)[] Groups = [
    (StandardProblems, ["ALPSGA_TSP", "ES_Griewank", "OSES_Griewank", "GA_Grouping", "GA_TSP", "GA_VRP", "GE_ArtificialAnt",
                        "IslandGA_TSP", "LS_Knapsack", "PSO_Rastrigin", "RAPGA_JSSP", "SA_Rastrigin", "SGP_SantaFe", "GP_Multiplexer",
                        "SGP_Robocode", "SS_VRP", "TS_TSP", "TS_VRP", "VNS_OP", "VNS_TSP", "GA_BPP"]),
    (DataAnalysis, ["ALPSGP_SymReg", "SGP_SymbClass", "SGP_SymbReg", "OSGP_SymReg", "OSGP_TimeSeries", "GE_SymbReg", "GPR",
                    "GP_Shape_Constrained_Regression", "GP_Structure_Template_Regression"]),
    (Scripts, ["GA_QAP_Script", "GUI_Automation_Script", "OSGA_Rastrigin_Script", "GridSearch_RF_Classification_Script",
               "GridSearch_RF_Regression_Script", "GridSearch_SVM_Classification_Script", "GridSearch_SVM_Regression_Script"])
  ];

  private static readonly Dictionary<string, string> Limitations = new() {
    ["SGP_Robocode"] = "Evaluates robots by running battles in Robocode, which must be installed (robocode.sourceforge.io) and configured in the problem.",
    ["GUI_Automation_Script"] = "Automates HeuristicLab's Windows user interface (MainForm), which does not exist here; it does not compile.",
  };

  private const string ResourcePrefix = "Samples.";

  public static IReadOnlyList<string> Ids => Groups.SelectMany(g => g.Ids).ToList();

  public static string GroupOf(string id) => Groups.First(g => g.Ids.Contains(id)).Group;

  public static IStorableContent Load(string id) {
    HlRuntime.Initialize();
    using var stream = typeof(Samples).Assembly.GetManifestResourceStream(ResourcePrefix + id + ".hl")
      ?? throw new ArgumentException($"No sample '{id}'.");
    return (IStorableContent)new ProtoBufSerializer().Deserialize(stream);
  }

  private static IReadOnlyList<SampleInfo>? infos;
  private static readonly SemaphoreSlim InfoLock = new(1, 1);

  /// <summary>Names and descriptions of all samples (loads each once, in parallel), sorted by name within each group as the start page shows them.</summary>
  public static async Task<IReadOnlyList<SampleInfo>> ListAsync() {
    await InfoLock.WaitAsync().ConfigureAwait(false);
    try {
      return infos ??= await Task.Run(() => Ids.AsParallel().AsOrdered().Select(Info).ToList()
        .OrderBy(i => Array.FindIndex(Groups, g => g.Group == i.Group)).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList()).ConfigureAwait(false);
    } finally {
      InfoLock.Release();
    }
  }

  private static SampleInfo Info(string id) {
    var content = Load(id);
    var named = content as INamedItem;
    var item = content as IItem;
    return new SampleInfo(id, GroupOf(id), named?.Name ?? id, named?.Description ?? "", item?.ItemName ?? content.GetType().Name,
      Limitations.GetValueOrDefault(id));
  }
}
