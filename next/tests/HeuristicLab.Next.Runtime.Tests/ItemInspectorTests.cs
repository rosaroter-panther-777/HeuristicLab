using HeuristicLab.Core;
using HeuristicLab.Data;
using HeuristicLab.Optimization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Next.Runtime.Tests;

[TestClass]
public class ItemInspectorTests {
  private static IAlgorithm TabuSearchCh130() =>
    Setups.Create(new SetupRequest("TabuSearch") { Problem = "TravelingSalesmanProblem", Instance = "ch130" }).Algorithm;

  private static IParameter P(IParameterizedItem item, string name) => item.Parameters[name];

  [TestMethod]
  public void TabuSearchParametersHaveTheirKinds() {
    var ts = TabuSearchCh130();
    Assert.AreEqual(ValueKind.Text, ItemInspector.KindOf(ItemInspector.Value(P(ts, "MaximumIterations"))));
    Assert.AreEqual(ValueKind.Bool, ItemInspector.KindOf(ItemInspector.Value(P(ts, "SetSeedRandomly"))));
    Assert.AreEqual(ValueKind.CheckedList, ItemInspector.KindOf(ItemInspector.Value(P(ts, "Analyzer"))));
    var generators = ItemInspector.Choices(P(ts, "MoveGenerator"))!;
    Assert.IsTrue(generators.Count > 1, "move generators that fit the TSP");
    Assert.AreEqual("IMoveGenerator", ItemInspector.TypeName(P(ts, "MoveGenerator").DataType));

    var problem = (IParameterizedItem)ts.Problem!;
    var best = ItemInspector.Value(P(problem, "BestKnownSolution"))!;
    Assert.AreEqual(ValueKind.Array, ItemInspector.KindOf(best));
    Assert.AreEqual("Permutation [130]", ItemInspector.Summary(best));
    Assert.AreEqual(ValueKind.Matrix, ItemInspector.KindOf(ItemInspector.Value(P(problem, "Coordinates"))));
    Assert.AreEqual("6110", ItemInspector.Summary(ItemInspector.Value(P(problem, "BestKnownQuality"))));
  }

  [TestMethod]
  public void EditingCellsAndScalarsValidates() {
    var ts = TabuSearchCh130();
    var iterations = ItemInspector.Value(P(ts, "MaximumIterations"))!;
    Assert.IsNull(ItemInspector.SetText(iterations, "50"));
    Assert.AreEqual(50, ((IntValue)iterations).Value);
    Assert.IsNotNull(ItemInspector.SetText(iterations, "lots"));
    var coordinates = (DoubleMatrix)ItemInspector.Value(P((IParameterizedItem)ts.Problem!, "Coordinates"))!;
    Assert.IsNull(ItemInspector.SetCell(coordinates, 0, 1, "12.5"));
    Assert.AreEqual(12.5, coordinates[0, 1]);
  }

  [TestMethod]
  public void AnalyzersCanBeCheckedAndReordered() {
    var ts = TabuSearchCh130();
    var analyzers = CheckedList.TryCreate(ItemInspector.Value(P(ts, "Analyzer")))!;
    Assert.IsTrue(analyzers.Count >= 3);
    var first = analyzers[0];
    bool firstChecked = analyzers.IsChecked(0);
    analyzers.SetChecked(1, false);
    var second = analyzers[1];
    analyzers.Move(0, 1);
    Assert.AreSame(first, analyzers[1]);
    Assert.AreSame(second, analyzers[0]);
    Assert.IsFalse(analyzers.IsChecked(0), "checked state moves with its item");
    Assert.AreEqual(firstChecked, analyzers.IsChecked(1));
    Assert.IsTrue(analyzers.AddableTypes().Count > 0);
  }

  [TestMethod]
  public void OperatorGraphStartsAtTheInitialOperator() {
    var ts = TabuSearchCh130();
    var layout = ItemInspector.Layout(ItemInspector.GraphOf(ts)!);
    Assert.AreEqual(layout.Initial, layout.Nodes.Single(n => n.Layer == 0).Operator);
    var names = layout.Nodes.OrderBy(n => n.Layer).Select(n => n.Operator.Name).ToList();
    CollectionAssert.AreEqual(new[] { "RandomCreator", "SolutionsCreator" }, names.Take(2).ToArray(), string.Join(", ", names));
    Assert.IsTrue(names.Contains("TabuSearchMainLoop"));
    var mainLoop = layout.Nodes.Single(n => n.Operator.Name == "TabuSearchMainLoop").Operator;
    Assert.IsNotNull(ItemInspector.GraphOf(mainLoop), "the main loop has its own graph");
    Assert.IsTrue(layout.Edges.Count >= layout.Nodes.Count - 1);
  }

  [TestMethod]
  public async Task BestSolutionResultIsATourWithQuality() {
    var ts = TabuSearchCh130();
    ParameterEditor.Apply(ts, ["MaximumIterations=3"]);
    await OptimizerRunner.RunAsync(ts);
    var best = ts.Results["Best TSP Solution"].Value;
    var members = ItemInspector.Members(best).Select(m => m.Name).ToList();
    CollectionAssert.AreEqual(new[] { "Coordinates", "Permutation", "Quality" }, members);
    Assert.AreEqual(((DoubleValue)ts.Results["BestQuality"].Value).Value, ItemInspector.QualityOf(best));
    var tour = ItemInspector.TourOf(best)!;
    Assert.AreEqual(131, tour.Count, "130 cities, closed");
    Assert.AreEqual(tour[0], tour[^1]);
    Assert.IsNull(ItemInspector.TourOf(ts.Results["BestQuality"].Value));
  }

  [TestMethod]
  public void EnginesAndProblemOrigin() {
    var engines = ItemInspector.Engines().Select(e => e.Name).ToList();
    CollectionAssert.Contains(engines, "Sequential Engine", string.Join(", ", engines));
    CollectionAssert.Contains(engines, "Debug Engine", "the only engine that honors breakpoints");
    var origin = ProblemInstances.Origin(TabuSearchCh130().Problem!);
    Assert.AreEqual("ch130", origin!.Name);
    StringAssert.Contains(origin.Provider, "TSPLIB");
  }
}
