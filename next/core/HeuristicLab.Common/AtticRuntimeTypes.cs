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
using HEAL.Attic;

namespace HeuristicLab.Common {
  /// <summary>
  /// Registers runtime types that HEAL.Attic 1.8.0 does not know with fixed GUIDs, so that
  /// HeuristicLab objects can be persisted on modern .NET. Runs as a module initializer, i.e.
  /// before any type of this assembly (and thus any HeuristicLab item) is used.
  /// </summary>
  internal static class AtticRuntimeTypes {
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register() {
      // .NET 5+ default comparers of Dictionary<string, T> / HashSet<string> and of nested sets;
      // field-less and default-constructible, which is what Attic's collection transformers need.
      Register(new Guid("93DE6707-4FCE-4CA7-95DA-FE9DD891BB7B"), "System.Collections.Generic.StringEqualityComparer");
      Register(new Guid("C777590F-AA61-4296-A669-9EBC9A7C4A54"), "System.Collections.Generic.HashSetEqualityComparer`1");
    }

    private static void Register(Guid guid, string typeName) {
      var type = typeof(object).Assembly.GetType(typeName);  // absent on .NET Framework
      if (type == null || IsRegistered(type)) return;
      Mapper.StaticCache.RegisterType(guid, type);
    }

    // newer Attic versions may know the type already
    private static bool IsRegistered(Type type) {
      try {
        Mapper.StaticCache.GetGuid(type);
        return true;
      } catch (Exception) {
        return false;
      }
    }
  }
}

namespace System.Runtime.CompilerServices {
  // netstandard2.0 lacks the attribute; the C# compiler only needs a type with this name
  [AttributeUsage(AttributeTargets.Method, Inherited = false)]
  internal sealed class ModuleInitializerAttribute : Attribute { }
}
