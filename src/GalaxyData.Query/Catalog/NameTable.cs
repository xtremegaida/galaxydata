using System;
using System.Collections.Generic;

namespace GalaxyData.Query.Catalog;

public enum MatchStatus : byte
{
   NotFound,
   Found,
   Ambiguous,
}

/// <summary>
/// The outcome of a case-insensitive name lookup. When several names differ only by case, an exact-case match
/// wins; without one the match is ambiguous and <see cref="Candidates"/> lists them.
/// </summary>
public readonly struct NameMatch<T> where T : class
{
   private NameMatch(MatchStatus status, T? item, IReadOnlyList<T> candidates)
   {
      Status = status;
      Item = item;
      Candidates = candidates;
   }

   public static NameMatch<T> NotFound => new(MatchStatus.NotFound, null, []);

   public static NameMatch<T> Found(T item) => new(MatchStatus.Found, item, [item]);

   public static NameMatch<T> Ambiguous(IReadOnlyList<T> candidates) => new(MatchStatus.Ambiguous, null, candidates);

   public MatchStatus Status { get; }

   public T? Item { get; }

   public IReadOnlyList<T> Candidates { get; }

   public bool IsFound => Status == MatchStatus.Found;
}

/// <summary>Names mapped to items, matched case-insensitively with an exact-case tie-break.</summary>
internal sealed class NameTable<T> where T : class
{
   private readonly Dictionary<string, List<(string Name, T Item)>> byName = new(StringComparer.OrdinalIgnoreCase);

   public void Add(string name, T item)
   {
      if (!byName.TryGetValue(name, out List<(string Name, T Item)>? entries))
      {
         entries = new List<(string Name, T Item)>(1);
         byName.Add(name, entries);
      }
      entries.Add((name, item));
   }

   public bool Contains(string name) => byName.ContainsKey(name);

   public NameMatch<T> Find(string name)
   {
      if (!byName.TryGetValue(name, out List<(string Name, T Item)>? entries)) { return NameMatch<T>.NotFound; }
      if (entries.Count == 1) { return NameMatch<T>.Found(entries[0].Item); }
      T? exact = null;
      foreach ((string candidate, T item) in entries)
      {
         if (!string.Equals(candidate, name, StringComparison.Ordinal)) { continue; }
         if (exact != null && !ReferenceEquals(exact, item)) { exact = null; break; }
         exact = item;
      }
      if (exact != null) { return NameMatch<T>.Found(exact); }
      List<T> candidates = new(entries.Count);
      foreach ((_, T item) in entries)
      {
         if (!candidates.Contains(item)) { candidates.Add(item); }
      }
      return candidates.Count == 1 ? NameMatch<T>.Found(candidates[0]) : NameMatch<T>.Ambiguous(candidates);
   }
}
