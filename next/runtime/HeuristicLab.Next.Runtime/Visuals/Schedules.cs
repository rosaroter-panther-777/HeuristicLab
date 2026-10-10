using System.Globalization;
using HeuristicLab.Core;
using HeuristicLab.Encodings.ScheduleEncoding;
using HeuristicLab.Problems.Scheduling;

namespace HeuristicLab.Next.Runtime.Visuals;

/// <summary>Schedules as Gantt charts: one row per resource (machine), one bar per task, colored by job.</summary>
internal static class Schedules {
  public static IReadOnlyList<Visual>? For(IItem item) => item is Schedule schedule ? [Gantt("Gantt chart", schedule)] : null;

  public static IReadOnlyList<Visual>? ForProblem(object problem) =>
    problem is JobShopSchedulingProblem { BestKnownSolution: { } best } p ? [Gantt(Routes.BestKnownTitle(p), best)] : null;

  private static SceneVisual Gantt(string title, Schedule schedule) {
    var resources = schedule.Resources.ToArray();
    var shapes = new List<Shape>();
    var jobs = new SortedSet<int>();
    double makespan = 0;
    for (int r = 0; r < resources.Length; r++) {
      shapes.Add(new TextShape(new Point2(0, r + 0.5), $"Machine {resources[r].Index}  ", Rgb.Black, TextAnchor.Right));
      foreach (var task in resources[r].Tasks.ToArray()) {
        jobs.Add(task.JobNr);
        makespan = Math.Max(makespan, task.EndTime);
        shapes.Add(new RectShape(task.StartTime, r + 0.15, task.Duration, 0.7, Rgb.Palette(task.JobNr), Rgb.Black,
          $"J{task.JobNr.ToString(CultureInfo.InvariantCulture)}") { IsSolution = true, Group = $"Job {task.JobNr}" });
      }
    }
    return new SceneVisual(title, shapes, YUp: false, Uniform: false) {
      Axes = true, XAxisTitle = "Time",
      Legend = jobs.Take(14).Select(j => new LegendEntry($"Job {j}", Rgb.Palette(j))).ToList(),
      Notes = [$"Makespan {makespan.ToString("G10", CultureInfo.InvariantCulture)}", $"{resources.Length} machines, {jobs.Count} jobs"]
    };
  }
}
