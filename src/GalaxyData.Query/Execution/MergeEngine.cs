using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Types;

namespace GalaxyData.Query.Execution;

/// <summary>
/// Where queries that combine sources are finished. Each source's part of a query (a fragment) is loaded into a
/// table of a session, and the merge SQL, written in the dialect of <see cref="Provider"/>, combines them.
/// </summary>
public interface IMergeEngine
{
   /// <summary>The merge SQL's dialect, and how values are bound to it and read from its results.</summary>
   SourceProvider Provider { get; }

   /// <summary>A session for one query; its tables go when it is disposed.</summary>
   ValueTask<IMergeSession> OpenSessionAsync(CancellationToken cancellationToken);
}

/// <summary>One query's tables in the merge engine. Tables may be created and loaded concurrently.</summary>
public interface IMergeSession : IAsyncDisposable
{
   /// <summary>A new table, named as the merge SQL refers to it (<c>f1</c>), to load a fragment's rows into.</summary>
   ValueTask<IMergeTableWriter> CreateTableAsync(string name, IReadOnlyList<MergeColumn> columns, CancellationToken cancellationToken);

   /// <summary>A command for the merge SQL, on a connection where the session's tables are found by their names.</summary>
   DbCommand CreateCommand();

   /// <summary>
   /// Another connection where the session's tables are found by their names, for queries while tables load (the
   /// keys of one to fetch another by); the caller disposes it.
   /// </summary>
   ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken);
}

/// <summary>A column of a merge table: its name and logical type.</summary>
public sealed record MergeColumn(string Name, ScalarType Type);

/// <summary>Loads rows into a merge table; they are there once <see cref="CompleteAsync"/> returns.</summary>
public interface IMergeTableWriter : IAsyncDisposable
{
   /// <summary>Adds a row: a value for each column, in order, of the CLR type <see cref="ValueConverter"/> gives its logical type, or null.</summary>
   void Append(object?[] row);

   /// <summary>
   /// Adds the rows of a query the merge engine runs over tables of its own database (a source kept in it, such as an
   /// Excel folder), selecting a value for each column in order; <paramref name="prepare"/> sets the command's text
   /// and parameters. How many rows it added.
   /// </summary>
   ValueTask<long> AppendQueryAsync(Action<DbCommand> prepare, CancellationToken cancellationToken);

   ValueTask CompleteAsync(CancellationToken cancellationToken);
}
