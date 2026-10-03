using System;
using System.Threading;
using GalaxyData.Query.DuckDb;
using GalaxyData.Query.Execution;
using GalaxyData.Web.Hosting;
using Microsoft.Extensions.Options;

namespace GalaxyData.Web.Catalog;

/// <summary>
/// The engine queries run on: one for each catalog built, connecting through its sources, with the application's
/// merge engine and its query settings (<see cref="QuerySettings"/>).
/// </summary>
public sealed class QueryEngines(SourceConnections connections, SourceProviders providers, DuckDbMergeEngine merge, IOptions<GalaxyDataOptions> options,
                                 TimeProvider clock)
{
   private readonly Lock gate = new();
   private (CatalogState State, QueryEngine Engine)? last;

   public QueryEngine For(CatalogState state)
   {
      ArgumentNullException.ThrowIfNull(state);
      lock (gate)
      {
         if (last is { } cached && ReferenceEquals(cached.State, state)) { return cached.Engine; }
         QuerySettings settings = options.Value.Query;
         QueryEngine engine = new(state.Catalog, connections, providers.All, merge, new QueryEngineOptions
         {
            Clock = clock,
            Timeout = settings.Timeout,
            MaxFetchedRows = settings.MaxFetchedRows,
         });
         last = (state, engine);
         return engine;
      }
   }
}
