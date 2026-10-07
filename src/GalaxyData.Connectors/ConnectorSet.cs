using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Query.Execution;

namespace GalaxyData.Connectors;

/// <summary>
/// The connectors a host has, in the order its kinds are listed: their kinds of connection and the engine's
/// providers for them. Disposing the set disposes the connectors (and what they keep in the merge engine).
/// </summary>
public sealed class ConnectorSet : IDisposable
{
   public ConnectorSet(IEnumerable<Connector> connectors)
   {
      ArgumentNullException.ThrowIfNull(connectors);
      All = connectors.ToList();
      foreach (Connector connector in All)
      {
         if (!string.Equals(connector.Kind.Id, connector.Provider.ProviderKind, StringComparison.Ordinal))
         {
            throw new ArgumentException($"The connector '{connector.Id}' has a provider of '{connector.Provider.ProviderKind}' sources: a kind's id is its provider's kind", nameof(connectors));
         }
      }
      if (All.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
      {
         throw new ArgumentException($"There are two connectors of '{twice.Key}' sources", nameof(connectors));
      }
      Kinds = new ConnectionKinds(All.Select(c => c.Kind));
      Providers = All.Select(c => c.Provider).ToList();
   }

   public IReadOnlyList<Connector> All { get; }

   public ConnectionKinds Kinds { get; }

   /// <summary>The engine's providers, one for each kind.</summary>
   public IReadOnlyList<SourceProvider> Providers { get; }

   /// <summary>The connector of a kind, by its id.</summary>
   public Connector? Find(string? id) => All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

   public void Dispose()
   {
      foreach (Connector connector in All) { connector.Dispose(); }
   }
}
