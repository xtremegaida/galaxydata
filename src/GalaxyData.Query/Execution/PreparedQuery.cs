using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Binding;
using GalaxyData.Query.Diagnostics;
using GalaxyData.Query.Planning;
using GalaxyData.Query.Results;
using GalaxyData.Query.Sql;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Execution;

/// <summary>A bound and planned query with its SQL written; nothing has run yet.</summary>
public sealed class PreparedQuery
{
   private readonly QueryEngine engine;
   private readonly QueryParameters parameters;

   internal PreparedQuery(QueryEngine engine, string text, QueryParameters parameters, IReadOnlyList<QueryDiagnostic> diagnostics,
                          LogicalPlan? plan, IReadOnlyList<QueryFragment> fragments)
   {
      this.engine = engine;
      this.parameters = parameters;
      Text = text;
      Diagnostics = diagnostics;
      Plan = plan;
      Fragments = fragments;
   }

   public string Text { get; }

   public IReadOnlyList<QueryDiagnostic> Diagnostics { get; }

   public bool Success => Plan != null && Fragments.Count > 0 && !Diagnostics.Any(d => d.IsError);

   public LogicalPlan? Plan { get; }

   public ResultSchema? Schema => Plan?.Schema;

   /// <summary>The SQL each source runs.</summary>
   public IReadOnlyList<QueryFragment> Fragments { get; }

   public async Task<QueryResult> ExecuteAsync(CancellationToken cancellationToken = default)
   {
      if (!Success) { throw new QueryException(Diagnostics); }
      QueryFragment fragment = Fragments[0];
      SourceProvider provider = engine.Provider(fragment.Source);
      QueryEngineOptions options = engine.Options;
      DateTimeOffset started = options.Clock.GetUtcNow();

      DbConnection connection = await engine.Connections.OpenAsync(fragment.Source, cancellationToken).ConfigureAwait(false);
      DbCommand? command = null;
      try
      {
         await provider.PrepareConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
         command = connection.CreateCommand();
         command.CommandText = fragment.Sql;
         if (options.CommandTimeout is { } timeout) { command.CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds); }
         foreach (SqlParameterSlot slot in fragment.Statement.Parameters)
         {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = fragment.Dialect.ParameterName(slot.Name);
            provider.BindParameter(parameter, Resolve(slot, started), slot.Type);
            command.Parameters.Add(parameter);
         }
         DbDataReader reader;
         try
         {
            reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
         }
         catch (DbException e)
         {
            throw new QueryExecutionException($"{fragment.Source.Alias} ({fragment.Dialect.Name}) failed to run the query: {e.Message}", e);
         }
         return new QueryResult(Schema!, fragment, provider, connection, command, reader, options.LenientConversion, started, options.Clock);
      }
      catch
      {
         if (command != null) { await command.DisposeAsync().ConfigureAwait(false); }
         await connection.DisposeAsync().ConfigureAwait(false);
         throw;
      }
   }

   /// <summary>The value of a parameter when the query runs, converted to the type the SQL expects.</summary>
   private object? Resolve(SqlParameterSlot slot, DateTimeOffset started)
   {
      object? value = slot.Source switch
      {
         null => slot.Constant,
         ParameterSource.User => User(slot),
         ParameterSource.Now => started.UtcDateTime,
         ParameterSource.Today => DateOnly.FromDateTime(started.UtcDateTime),
         _ => throw new InvalidOperationException($"Unexpected parameter source {slot.Source}"),
      };
      return slot.Pattern != null && value is string text ? slot.Pattern.Apply(text) : value;
   }

   private object? User(SqlParameterSlot slot)
   {
      if (!parameters.TryGet(slot.ParameterName!, out QueryParameter? parameter))
      {
         throw new QueryExecutionException($"No value was given for ${slot.ParameterName}");
      }
      if (parameter.Value == null || parameter.Type.Kind == slot.Type.Kind) { return parameter.Value; }
      return TypeRules.TryConvertConstant(parameter.Value, parameter.Type, slot.Type, out object? converted, out _, out string? error)
         ? converted
         : throw new QueryExecutionException($"${slot.ParameterName} can't be used as {TypeRules.Describe(slot.Type)}: {error}");
   }
}
