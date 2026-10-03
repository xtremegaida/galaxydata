using System;
using System.Threading;
using GalaxyData.Query.Catalog;
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
         QueryEngine engine = Trial(state.Catalog);
         last = (state, engine);
         return engine;
      }
   }

   /// <summary>An engine for another catalog (an overlay item tried before it is saved), not kept.</summary>
   public QueryEngine Trial(QueryCatalog catalog)
   {
      QuerySettings settings = options.Value.Query;
      return new QueryEngine(catalog, connections, providers.All, merge, new QueryEngineOptions
      {
         Clock = clock,
         Timeout = settings.Timeout,
         MaxFetchedRows = settings.MaxFetchedRows,
      });
   }
}
