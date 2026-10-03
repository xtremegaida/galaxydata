using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Sql;

namespace GalaxyData.Query.Dml;

/// <summary>
/// Runs scripts, one for each connection, as one change: every connection is opened and its transaction begun
/// (after the provider readies it: SQLite's foreign keys), then each script's statements run in order. A statement
/// that fails, or changes another number of rows than it must, rolls every connection back. When all have run,
/// each connection checks what its commit would (PostgreSQL's deferred constraints), and then they commit in order.
/// There is no two-phase commit: a commit that fails after another succeeded leaves the changes partly written, and
/// the rest are rolled back. Cancelling before the commits roll everything back; the commits aren't cancelled.
/// </summary>
internal sealed class DmlExecutor(QueryEngine engine)
{
   private sealed class Connection(DmlScript script, SourceProvider provider) : IAsyncDisposable
   {
      public DmlScript Script { get; } = script;

      public SourceProvider Provider { get; } = provider;

      public DbConnection? Database { get; set; }

      public DbTransaction? Transaction { get; set; }

      public List<DmlStatementResult> Results { get; } = [];

      public DmlScriptStatus Status { get; set; } = DmlScriptStatus.RolledBack;

      public string? Error { get; set; }

      public string Name => $"{Script.Source.Alias} ({Script.Dialect.Name})";

      public async Task RollBackAsync()
      {
         if (Transaction == null || Status == DmlScriptStatus.Committed) { return; }
         try
         {
            await Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
         }
         catch (Exception e) when (e is DbException or InvalidOperationException)
         {
            // Already rolled back (a failed commit), or the connection is gone, which rolls it back too.
         }
      }

      public async ValueTask DisposeAsync()
      {
         if (Transaction != null) { await Transaction.DisposeAsync().ConfigureAwait(false); }
         if (Database != null) { await Database.DisposeAsync().ConfigureAwait(false); }
      }
   }

   public async Task<DmlResult> RunAsync(IReadOnlyList<DmlScript> scripts, CancellationToken cancellationToken)
   {
      if (scripts.GroupBy(s => s.Source.Alias, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
      {
         throw new ArgumentException($"There are {twice.Count()} scripts for {twice.Key}: a connection runs one", nameof(scripts));
      }
      List<Connection> connections = scripts.Select(s => new Connection(s, engine.Provider(s.Source))).ToList();
      try
      {
         DmlFailure? failure = await WriteAsync(connections, cancellationToken).ConfigureAwait(false);
         if (failure != null)
         {
            foreach (Connection connection in connections) { await connection.RollBackAsync().ConfigureAwait(false); }
         }
         else
         {
            failure = await CommitAsync(connections).ConfigureAwait(false);
         }
         return new DmlResult(connections.Select(c => new DmlScriptResult(c.Script, c.Status, c.Results, c.Error)).ToList(), failure);
      }
      catch
      {
         foreach (Connection connection in connections) { await connection.RollBackAsync().ConfigureAwait(false); }
         throw;
      }
      finally
      {
         foreach (Connection connection in connections) { await connection.DisposeAsync().ConfigureAwait(false); }
      }
   }

   /// <summary>Opens the connections, runs the statements and checks what the commits will; the failure that stops the changes, if any.</summary>
   private async Task<DmlFailure?> WriteAsync(List<Connection> connections, CancellationToken cancellationToken)
   {
      foreach (Connection connection in connections)
      {
         SourceInfo source = connection.Script.Source;
         try
         {
            connection.Database = await engine.OpenAsync(source, cancellationToken).ConfigureAwait(false);
            await connection.Provider.PrepareWriteAsync(connection.Database, source, cancellationToken).ConfigureAwait(false);
            connection.Transaction = await connection.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
         }
         catch (Exception e) when (e is not OperationCanceledException)
         {
            cancellationToken.ThrowIfCancellationRequested();
            return new DmlFailure(DmlFailureKind.Connection, source, $"{connection.Name} couldn't be opened to write the changes: {e.Message}", null, e);
         }
      }
      foreach (Connection connection in connections)
      {
         foreach (DmlStatement statement in connection.Script.Statements)
         {
            DmlStatementResult result;
            try
            {
               result = await RunAsync(connection, statement, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
               cancellationToken.ThrowIfCancellationRequested();
               connection.Results.Add(new DmlStatementResult(statement, -1, null));
               return new DmlFailure(DmlFailureKind.Statement, connection.Script.Source, $"{connection.Name} failed to run {statement.Description}: {e.Message}", statement, e);
            }
            connection.Results.Add(result);
            if (statement.ExpectedRows is { } expected && result.RowsChanged != expected)
            {
               return new DmlFailure(DmlFailureKind.Conflict, connection.Script.Source, Conflict(connection, statement, result.RowsChanged), statement, null);
            }
         }
      }
      foreach (Connection connection in connections)
      {
         try
         {
            await connection.Provider.PrepareCommitAsync(connection.Database!, connection.Transaction!, cancellationToken).ConfigureAwait(false);
         }
         catch (Exception e) when (e is not OperationCanceledException)
         {
            cancellationToken.ThrowIfCancellationRequested();
            return new DmlFailure(DmlFailureKind.Statement, connection.Script.Source, $"{connection.Name} can't commit the changes: {e.Message}", null, e);
         }
      }
      return null;
   }

   private static string Conflict(Connection connection, DmlStatement statement, long rows)
   {
      string changed = rows == 1 ? "1 row" : $"{rows.ToString(CultureInfo.InvariantCulture)} rows";
      if (statement.Kind == DmlStatementKind.Insert) { return $"{connection.Name}: {statement.Description} gave back {changed}, not the row it inserted"; }
      return rows == 0
         ? $"{connection.Name}: {statement.Description} found no row: it was changed or deleted since it was read"
         : $"{connection.Name}: {statement.Description} found {changed}: the key doesn't tell them apart";
   }

   private async Task<DmlStatementResult> RunAsync(Connection connection, DmlStatement statement, CancellationToken cancellationToken)
   {
      await using DbCommand command = connection.Database!.CreateCommand();
      command.Transaction = connection.Transaction;
      command.CommandText = statement.Sql;
      if (engine.Options.CommandTimeout is { } timeout) { command.CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds); }
      SqlDialect dialect = connection.Script.Dialect;
      foreach (SqlParameterSlot slot in statement.Parameters)
      {
         DbParameter parameter = command.CreateParameter();
         parameter.ParameterName = dialect.ParameterName(slot.Name);
         connection.Provider.BindParameter(parameter, slot.Constant, slot);
         command.Parameters.Add(parameter);
      }
      switch (statement.Counting)
      {
         case DmlRowCount.Selected:
            // The count is the last result: an edited statement may give rows before it (SQL Server's OUTPUT).
            object? count = null;
            await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
               do
               {
                  if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { count = reader.GetValue(0); }
               }
               while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
            }
            return new DmlStatementResult(statement, statement.Kind == DmlStatementKind.Other || count == null ? -1 : Convert.ToInt64(count, CultureInfo.InvariantCulture), null);
         case DmlRowCount.Rows:
            await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
               long rows = 0;
               object?[]? first = null;
               while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
               {
                  if (rows++ > 0) { continue; }
                  first = new object?[statement.ReturnedColumns.Count];
                  for (int i = 0; i < first.Length; i++)
                  {
                     // A value that doesn't read as its column's type is left out; the change was made all the same.
                     QueryResult.TryRead(connection.Provider, reader, i, statement.ReturnedColumns[i].Type, out first[i], out _);
                  }
               }
               return new DmlStatementResult(statement, rows, first);
            }
         default:
            int changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new DmlStatementResult(statement, statement.Kind == DmlStatementKind.Other ? -1 : changed, null);
      }
   }

   /// <summary>Commits each connection in turn; once one fails, the rest are rolled back. The failure, if any.</summary>
   private async Task<DmlFailure?> CommitAsync(List<Connection> connections)
   {
      DmlFailure? failure = null;
      List<string> committed = [];
      foreach (Connection connection in connections)
      {
         if (failure != null)
         {
            await connection.RollBackAsync().ConfigureAwait(false);
            continue;
         }
         try
         {
            if (engine.Options.BeforeCommit is { } before) { await before(connection.Script.Source).ConfigureAwait(false); }
            await connection.Transaction!.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            connection.Status = DmlScriptStatus.Committed;
            committed.Add(connection.Script.Source.Alias);
         }
         catch (Exception e)
         {
            connection.Status = DmlScriptStatus.CommitFailed;
            connection.Error = e.Message;
            await connection.RollBackAsync().ConfigureAwait(false);
            string message = committed.Count == 0
               ? $"{connection.Name} failed to commit, so nothing was: {e.Message}"
               : $"{connection.Name} failed to commit after {string.Join(", ", committed)} had: their changes are written, and the others' aren't: {e.Message}";
            failure = new DmlFailure(DmlFailureKind.Commit, connection.Script.Source, message, null, e);
         }
      }
      return failure;
   }
}
