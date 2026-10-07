using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Introspection;
using GalaxyData.Query.Providers;

namespace GalaxyData.Connectors;

/// <summary>What a host gives the connectors it makes: the merge engine, in whose database some keep their sources' tables.</summary>
public sealed class ConnectorContext(IMergeEngine merge)
{
   public IMergeEngine Merge { get; } = merge ?? throw new ArgumentNullException(nameof(merge));
}

/// <summary>
/// One kind of source, plugged into the hosts: the engine's provider for it (its SQL dialect, schema, and values),
/// the kind of connection the application makes to it (its form, connection string and trying), and how the command
/// line opens one. Each connector's project holds all that is particular to its database; the hosts take the
/// connectors from one list and name no database. Not to be confused with a <see cref="SourceConnector"/>, which
/// opens connections with one connection string.
/// </summary>
public abstract class Connector : IDisposable
{
   private static readonly IReadOnlyDictionary<string, string> NoOptions = new Dictionary<string, string>();

   /// <summary>The kind's id, which is also the provider's kind: <c>sqlite</c>.</summary>
   public string Id => Kind.Id;

   public abstract SourceProvider Provider { get; }

   public abstract ConnectionKind Kind { get; }

   /// <summary>The sources the provider opens itself, under their aliases, if this connector's are such (a folder of workbooks).</summary>
   public virtual IAttachedSources? Attached => null;

   /// <summary>How the command line takes a source of this kind; null when it doesn't.</summary>
   public virtual CommandLineHelp? CommandLine => null;

   /// <summary>Opens a source given on the command line: connects (or attaches), and reads its schema.</summary>
   public virtual ValueTask<OpenedSource> OpenAsync(CommandLineSource source, CancellationToken cancellationToken) =>
      throw new NotSupportedException($"{Kind.DisplayName} sources aren't opened on the command line");

   /// <summary>
   /// Opens a database the command line names, with <paramref name="keeper"/>, a connection kept open while the
   /// source is (in-memory databases need one): runs a <c>.sql</c> target's script into it, reads its schema, and
   /// opens the source's other connections with <paramref name="open"/>. <paramref name="owner"/> is what the
   /// connections come from, disposed with the source (a data source).
   /// </summary>
   protected async ValueTask<OpenedSource> OpenDatabaseAsync(CommandLineSource source, DbConnection keeper, Func<DbConnection> open, IAsyncDisposable? owner,
                                                            CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(source);
      ArgumentNullException.ThrowIfNull(keeper);
      ArgumentNullException.ThrowIfNull(open);
      try
      {
         if (keeper.State != ConnectionState.Open) { await keeper.OpenAsync(cancellationToken).ConfigureAwait(false); }
         if (source.IsScript) { await keeper.ExecuteAsync(await File.ReadAllTextAsync(source.Target, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false); }
         SourceSchema schema = await Provider.Introspector.IntrospectAsync(keeper, IntrospectionOptions.Default, cancellationToken).ConfigureAwait(false);
         SourceInfo info = Kind.Configure(new SourceInfo(source.Alias, Provider.ProviderKind, schema.DefaultSchema) { IsReadOnly = !source.Writable }, NoOptions);
         return new OpenedSource(info, schema, open, keeper, owner);
      }
      catch
      {
         await keeper.DisposeAsync().ConfigureAwait(false);
         if (owner != null) { await owner.DisposeAsync().ConfigureAwait(false); }
         throw;
      }
   }

   /// <summary>
   /// Makes what a source's connections need with <paramref name="create"/>: a connection string the provider
   /// doesn't take (a keyword it doesn't know, a value that doesn't parse) is a <see cref="FormatException"/>.
   /// </summary>
   protected static T Create<T>(CommandLineSource source, Func<T> create)
   {
      ArgumentNullException.ThrowIfNull(source);
      ArgumentNullException.ThrowIfNull(create);
      try
      {
         return create();
      }
      catch (ArgumentException e)
      {
         // The providers' connection string builders reject keywords they don't know, and values that don't parse.
         throw new FormatException($"The connection string of '{source.Alias}' isn't one {source.Kind} takes: {e.Message}", e);
      }
   }

   /// <summary>The full path of the database file a source names; <see cref="FileNotFoundException"/> when there is none.</summary>
   protected static string ExistingFile(CommandLineSource source)
   {
      ArgumentNullException.ThrowIfNull(source);
      return File.Exists(source.Target)
         ? Path.GetFullPath(source.Target)
         : throw new FileNotFoundException($"The {source.Kind} database for '{source.Alias}' doesn't exist: {source.Target}");
   }

   public void Dispose()
   {
      Dispose(true);
      GC.SuppressFinalize(this);
   }

   protected virtual void Dispose(bool disposing)
   {
   }

   public override string ToString() => Id;
}

/// <summary>
/// Sources whose provider opens their connections itself, registered under their aliases with no connection string
/// (a folder of workbooks, whose sheets the merge engine's database keeps).
/// </summary>
public interface IAttachedSources
{
   /// <summary>
   /// Registers a source with the provider, unless it is registered so already (which would drop what is loaded for
   /// it); the source to add to a catalog.
   /// </summary>
   SourceInfo Attach(string alias, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> options);

   /// <summary>Forgets a source, and what was loaded for it; false when it wasn't registered.</summary>
   bool Detach(string alias);

   /// <summary>Reads the schema a source with these settings and options has, under a name of its own: registered sources stay as they are.</summary>
   Task<SourceSchema> ReadSchemaAsync(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken);
}
