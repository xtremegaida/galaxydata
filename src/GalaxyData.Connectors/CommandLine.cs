using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Introspection;

namespace GalaxyData.Connectors;

/// <summary>What the command line's target of a kind is; the command line lists kinds in this order.</summary>
public enum CommandLineTargets : byte
{
   /// <summary>A database file, a <c>.sql</c> script run into an in-memory database, <c>:memory:</c>, or a connection string.</summary>
   Files,

   /// <summary>A connection string.</summary>
   ConnectionString,

   /// <summary>A folder.</summary>
   Folder,
}

/// <summary>
/// How the command line takes a source of a kind: its target, what that is for people (<c>a connection string</c>;
/// null for files), and an example of one, for a target that must be a connection string.
/// </summary>
public sealed record CommandLineHelp(CommandLineTargets Targets, string? Target = null, string? Example = null);

/// <summary>A source given on the command line as <c>alias=kind:target</c>, and whether it may be written.</summary>
public sealed record CommandLineSource(string Alias, string Kind, string Target, bool Writable)
{
   /// <summary>A <c>.sql</c> script, run into a fresh in-memory database.</summary>
   public bool IsScript => Target.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) && !IsConnectionString;

   public bool IsMemory => Target == ":memory:";

   /// <summary>Anything with an <c>=</c> in it.</summary>
   public bool IsConnectionString => Target.Contains('=', StringComparison.Ordinal);
}

/// <summary>
/// A source the command line opened: the engine's source and its schema, how its connections are opened (null when
/// the provider opens them itself), and what its schema left out. Disposing it lets its connections go.
/// </summary>
public sealed class OpenedSource : IAsyncDisposable
{
   private readonly DbConnection? keeper;
   private readonly IAsyncDisposable? owner;

   public OpenedSource(SourceInfo info, SourceSchema schema, Func<DbConnection>? open = null, DbConnection? keeper = null, IAsyncDisposable? owner = null)
   {
      ArgumentNullException.ThrowIfNull(info);
      ArgumentNullException.ThrowIfNull(schema);
      Info = info;
      Schema = schema;
      Open = open;
      this.keeper = keeper;
      this.owner = owner;
   }

   public SourceInfo Info { get; }

   public SourceSchema Schema { get; }

   /// <summary>A new connection to the source, which the caller opens and disposes; null for a source its provider opens.</summary>
   public Func<DbConnection>? Open { get; }

   /// <summary>What the schema left out (workbooks that can't be read), to tell.</summary>
   public IReadOnlyList<string> Warnings { get; init; } = [];

   public async ValueTask DisposeAsync()
   {
      if (keeper != null) { await keeper.DisposeAsync().ConfigureAwait(false); }
      if (owner != null) { await owner.DisposeAsync().ConfigureAwait(false); }
   }
}
