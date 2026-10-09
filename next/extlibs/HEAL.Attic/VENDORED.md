# HEAL.Attic (vendored)

Source of HEAL.Attic 1.8.0, MIT license (see LICENSE.txt), vendored so that defects that
surface on modern .NET can be fixed here.

- Upstream: https://github.com/heal-research/HEAL.Attic, tag v1.8,
  commit fde85ed2deb0907cfc4418e71d9d1e1de69246aa (archive sha256
  86e0737a72404c0375f884ee995308658f0f7f4229230cadb6b3d891fc1c2ad5)
- src/ contains upstream src/HEAL.Attic unchanged except for the patches listed below
  (build scripts and the upstream csproj are not included).
- Assembly identity matches the NuGet package: HEAL.Attic, version 1.8.0.0, signed with
  the HEAL key (public key token ba48961d6f65dcec). The file format is unchanged.

## Patches
1. Thread-safe attribute caches (src/Core/StorableTypeAttribute.cs, StorableAttribute.cs,
   StorableHookAttribute.cs): the static caches were plain Dictionary/HashSet instances
   shared by all threads. Concurrent (de)serialization could corrupt them; .NET Framework
   failed silently, .NET Core throws "Operations that change non-concurrent collections
   must have exclusive access" and the corrupted cache then breaks every later
   serialization in the process. They are now ConcurrentDictionary instances.
2. Thread-safe type registry (src/Core/StaticCache.cs): UpdateRegisteredTypes(), called on
   every serialization and deserialization, mutated the shared type/GUID dictionaries without
   the lock that RegisterType uses, and the lookups read them unlocked. Both now hold the
   existing lock. Same symptom as patch 1.
