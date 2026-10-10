using HeuristicLab.Core;
using HeuristicLab.Data;
using HeuristicLab.Encodings.SymbolicExpressionTreeEncoding;
using HeuristicLab.Problems.DataAnalysis.Symbolic;
using Ant = HeuristicLab.Problems.GeneticProgramming.ArtificialAnt;
using LawnMower = HeuristicLab.Problems.GeneticProgramming.LawnMower;
using Robocode = HeuristicLab.Problems.GeneticProgramming.Robocode;

namespace HeuristicLab.Next.Runtime.Visuals;

/// <summary>Genetic programming: expression trees, and what the evolved programs do (ant trail, mowed lawn, robot code).</summary>
internal static class Programs {
  public static IReadOnlyList<Visual>? For(IItem item) => item switch {
    Ant.Solution s => [Trail(s.World, s.SymbolicExpressionTree, s.MaxTimeSteps), Tree(s.SymbolicExpressionTree)],
    LawnMower.Solution s => [Lawn(s), Tree(s.Tree)],
    Robocode.Solution s => [new TextVisual("Robot code", Robocode.Interpreter.InterpretProgramTree(s.Tree.Root, "BestSolution")), Tree(s.Tree)],
    ISymbolicDataAnalysisModel model => Model(model),
    ISymbolicExpressionTree tree => [Tree(tree)],
    _ => null
  };

  /// <summary>Artificial ant problems (GP and grammatical evolution): the world with its food.</summary>
  public static IReadOnlyList<Visual>? ForProblem(IParameterizedItem problem) =>
    problem.Parameters.TryGetValue("World", out var parameter) && parameter is IValueParameter { Value: BoolMatrix world } ? [World(world)] : null;

  public static IReadOnlyList<Visual> Model(ISymbolicDataAnalysisModel model) =>
    [Tree(model.SymbolicExpressionTree), new TextVisual("Formula", new InfixExpressionFormatter().Format(model.SymbolicExpressionTree))];

  private const double LevelHeight = 56, NodeHeight = 26, Gap = 10;

  /// <summary>
  /// The tree top-down: leaves side by side in order, each parent centered over its children
  /// (the layout of HeuristicLab's tree chart). Node boxes are sized to their labels.
  /// </summary>
  public static SceneVisual Tree(ISymbolicExpressionTree tree, string title = "Tree") {
    var shapes = new List<Shape>();
    var edges = new List<Shape>();
    double nextLeft = 0;
    int nodes = 0, depth = 0;

    (double Center, double Top) Place(ISymbolicExpressionTreeNode node, int level) {
      nodes++;
      depth = Math.Max(depth, level + 1);
      string label = node.ToString() ?? "";
      double width = Math.Max(34, label.Length * 7 + 12);
      double top = level * LevelHeight;
      double center;
      var children = node.Subtrees.Select(child => Place(child, level + 1)).ToList();
      if (children.Count == 0) {
        center = nextLeft + width / 2;
        nextLeft += width + Gap;
      } else {
        center = (children[0].Center + children[^1].Center) / 2;
        // a wide parent over narrow children must not overlap its neighbors
        nextLeft = Math.Max(nextLeft, center + width / 2 + Gap);
        foreach (var child in children)
          edges.Add(new LineShape(new Point2(center, top + NodeHeight), new Point2(child.Center, child.Top), Rgb.Gray, 1.2));
      }
      var fill = node.Subtrees.Any() ? new Rgb(0xE3, 0xEC, 0xFA) : new Rgb(0xE6, 0xF4, 0xE7);
      shapes.Add(new RectShape(center - width / 2, top, width, NodeHeight, fill, new Rgb(0x5A, 0x6B, 0x86), label));
      return (center, top);
    }

    Place(tree.Root, 0);
    return new SceneVisual(title, [.. edges, .. shapes], YUp: false) {
      Layered = false, Notes = [$"Length {nodes}, depth {depth}", "Scroll to zoom, drag to move"] };
  }

  private static readonly Rgb Food = new(0x9E, 0xC9, 0xF0), Grid = new(0xD0, 0xD0, 0xD0);

  /// <summary>The ant's world: food as light blue dots.</summary>
  private static SceneVisual World(BoolMatrix world) {
    var shapes = GridShapes(world.Rows, world.Columns);
    int food = 0;
    for (int r = 0; r < world.Rows; r++)
      for (int c = 0; c < world.Columns; c++)
        if (world[r, c]) { food++; shapes.Add(new EllipseShape(c + 0.1, r + 0.1, 0.8, 0.8, Food)); }
    return new SceneVisual("World", shapes, YUp: false) { Notes = [$"{food} food items on a {world.Rows} × {world.Columns} grid"] };
  }

  /// <summary>
  /// The trail of the evolved ant, as HeuristicLab's ant view draws it: the program is run step by
  /// step on the world; every position the ant visits gets a brown square with a line showing
  /// its heading.
  /// </summary>
  private static SceneVisual Trail(BoolMatrix world, ISymbolicExpressionTree program, int maxTimeSteps) {
    var shapes = GridShapes(world.Rows, world.Columns);
    int food = 0;
    for (int r = 0; r < world.Rows; r++)
      for (int c = 0; c < world.Columns; c++)
        if (world[r, c]) { food++; shapes.Add(new EllipseShape(c + 0.1, r + 0.1, 0.8, 0.8, Food)); }
    var interpreter = new Ant.Interpreter(program, world, maxTimeSteps);
    var path = new List<Point2>();
    void Mark() {
      interpreter.AntLocation(out int row, out int column);
      shapes.Add(new RectShape(column + 0.25, row + 0.25, 0.5, 0.5, Rgb.Brown) { IsSolution = true });
      var center = new Point2(column + 0.5, row + 0.5);
      var heading = interpreter.AntDirection switch {
        0 => new Point2(center.X + 0.5, center.Y), 1 => new Point2(center.X, center.Y + 0.5),
        2 => new Point2(center.X - 0.5, center.Y), _ => new Point2(center.X, center.Y - 0.5)
      };
      shapes.Add(new LineShape(center, heading, Rgb.Brown, 1.5) { IsSolution = true });
      path.Add(center);
    }
    Mark();
    // every step uses time (moves and turns); the guard only protects against a stuck program
    for (int guard = 0; interpreter.ElapsedTime < interpreter.MaxTimeSteps && guard < 100_000; guard++) {
      interpreter.Step();
      Mark();
    }
    return new SceneVisual("Ant trail", shapes, YUp: false) {
      Notes = [$"Food eaten: {interpreter.FoodEaten} of {food}", $"{interpreter.ElapsedTime} of {maxTimeSteps} time steps",
               "Light blue: food; brown: the ant's positions and headings"]
    };
  }

  /// <summary>The lawn after running the mower program: mowed tiles light green, others dark green.</summary>
  private static SceneVisual Lawn(LawnMower.Solution solution) {
    var mowed = LawnMower.Interpreter.EvaluateLawnMowerProgram(solution.Length, solution.Width, solution.Tree);
    int rows = mowed.GetLength(0), columns = mowed.GetLength(1), count = 0;
    var pixels = new Rgb[rows * columns];
    for (int r = 0; r < rows; r++)
      for (int c = 0; c < columns; c++) {
        if (mowed[r, c]) count++;
        pixels[r * columns + c] = mowed[r, c] ? new Rgb(0x7F, 0xFF, 0x00) : new Rgb(0x00, 0x64, 0x00);
      }
    return new SceneVisual("Lawn", [new RasterShape(0, 0, columns, rows, rows, columns, pixels) { IsSolution = true }, .. GridLines(rows, columns)], YUp: false) {
      Layered = false,
      Notes = [$"Mowed {count} of {rows * columns} tiles"]
    };
  }

  private static List<Shape> GridShapes(int rows, int columns) => [new RectShape(0, 0, columns, rows, new Rgb(255, 255, 255)), .. GridLines(rows, columns)];

  private static IEnumerable<Shape> GridLines(int rows, int columns) =>
    Enumerable.Range(0, rows + 1).Select(r => (Shape)new LineShape(new Point2(0, r), new Point2(columns, r), Grid, 0.6))
      .Concat(Enumerable.Range(0, columns + 1).Select(c => new LineShape(new Point2(c, 0), new Point2(c, rows), Grid, 0.6)));
}
