// .NET 5+ added System.Collections.Generic.ReferenceEqualityComparer, which makes the name
// ambiguous in legacy tests importing both namespaces; aliases win over namespace imports.
global using ReferenceEqualityComparer = HeuristicLab.Common.ReferenceEqualityComparer;
