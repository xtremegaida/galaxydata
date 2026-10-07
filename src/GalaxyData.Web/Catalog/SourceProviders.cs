using System;
using System.Collections.Generic;
using System.Linq;
using GalaxyData.Connectors;
using GalaxyData.Query.Execution;

namespace GalaxyData.Web.Catalog;

/// <summary>The engine's providers, one for each kind of connection: the connectors'.</summary>
public sealed class SourceProviders(ConnectorSet connectors)
{
   public IReadOnlyList<SourceProvider> All { get; } = connectors.Providers;

   /// <summary>The provider of a kind of connection (its id is the provider's kind).</summary>
   public SourceProvider For(string kind) =>
      All.FirstOrDefault(p => string.Equals(p.ProviderKind, kind, StringComparison.Ordinal))
         ?? throw new InvalidOperationException($"No provider serves {kind} sources");
}
