#region License Information
/* HeuristicLab
 * Copyright (C) Heuristic and Evolutionary Algorithms Laboratory (HEAL)
 *
 * This file is part of HeuristicLab.
 *
 * HeuristicLab is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * HeuristicLab is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with HeuristicLab. If not, see <http://www.gnu.org/licenses/>.
 */
#endregion


using System;
using System.Diagnostics;

namespace HeuristicLab.PluginInfrastructure {
  /// <summary>
  /// Headless replacement for the legacy ErrorHandling class. ShowErrorDialog keeps the
  /// non-WinForms overloads that library code calls, but hands the error to ErrorDisplay,
  /// which a front end sets; without one the error is written to Trace.
  /// </summary>
  public static class ErrorHandling {
    public static Action<string, Exception> ErrorDisplay { get; set; }

    public static void ShowErrorDialog(Exception exception) {
      ShowErrorDialog(string.Empty, exception);
    }

    public static void ShowErrorDialog(string message, Exception exception) {
      var display = ErrorDisplay;
      if (display != null) display(message, exception);
      else Trace.TraceError("{0}{1}{2}", message, string.IsNullOrEmpty(message) ? "" : Environment.NewLine, BuildErrorMessage(exception));
    }

    public static string BuildErrorMessage(Exception exception) {
      if (exception == null) {
        return string.Empty;
      } else {
        string message =
          "HeuristicLab version: " + AssemblyHelpers.GetFileVersion(typeof(ErrorHandling).Assembly) + Environment.NewLine +
          exception.GetType().Name + ": " + exception.Message + Environment.NewLine +
                         exception.StackTrace;
        while (exception.InnerException != null) {
          exception = exception.InnerException;
          message += Environment.NewLine +
                     "-----" + Environment.NewLine +
                     exception.GetType().Name + ": " + exception.Message + Environment.NewLine +
                     exception.StackTrace;
        }
        return message;
      }
    }
  }
}
