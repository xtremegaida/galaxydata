using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Introspection;
using GalaxyData.Web.Connections;
using GalaxyData.Web.Metadata;
using Microsoft.EntityFrameworkCore;

namespace GalaxyData.Web.Schemas;

/// <summary>A snapshot as listed: when its structure was first and last found, and how much changed since the one before.</summary>
public sealed record SchemaSnapshotDto(long Id, string Hash, int TableCount, DateTime TakenAt, DateTime CheckedAt, ChangeCountsDto? Changes);

public sealed record ChangeCountsDto(int Added, int Removed, int Changed);

/// <summary>A snapshot with its schema, and what changed since the one before it (null for a connection's first).</summary>
public sealed record SchemaSnapshotDetailDto(SchemaSnapshotDto Snapshot, SourceSchema Schema, IReadOnlyList<SchemaChange>? Changes);

/// <summary>The settings and options (never secrets) a schema was read with.</summary>
public sealed record SnapshotSource(Dictionary<string, string> Settings, Dictionary<string, string> Options);

/// <summary>
/// Keeps connections' schemas: each structure (row counts aside, which change all the time) once, with what changed
/// since the one before; the newest <see cref="Kept"/> for each connection.
/// </summary>
public static class SchemaSnapshots
{
   public const int Kept = 5;

   private static readonly JsonSerializerOptions ChangesJson = new(JsonSerializerDefaults.Web)
   {
      Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
   };

   /// <summary>The schema's structure hashed: its serialized form without row counts.</summary>
   public static string Hash(SourceSchema schema)
   {
      ArgumentNullException.ThrowIfNull(schema);
      return IntrospectionJson.Hash(schema with { Tables = [.. schema.Tables.Select(t => t with { RowCountEstimate = null })] });
   }

   public static byte[] Compress(SourceSchema schema)
   {
      using MemoryStream compressed = new();
      using (GZipStream zip = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
      {
         zip.Write(Encoding.UTF8.GetBytes(IntrospectionJson.Serialize(schema)));
      }
      return compressed.ToArray();
   }

   /// <summary>A snapshot's schema; <see cref="JsonException"/> or <see cref="InvalidDataException"/> when its data is damaged.</summary>
   public static SourceSchema Read(byte[] data)
   {
      using GZipStream zip = new(new MemoryStream(data), CompressionMode.Decompress);
      return IntrospectionJson.Deserialize(zip);
   }

   public static List<SchemaChange>? Changes(string? json) => json == null ? null : JsonSerializer.Deserialize<List<SchemaChange>>(json, ChangesJson);

   /// <summary>What a snapshot was read with; null when it doesn't say.</summary>
   public static SnapshotSource? ReadWith(string? json)
   {
      if (json == null) { return null; }
      SnapshotSource? read = JsonSerializer.Deserialize<SnapshotSource>(json, ChangesJson);
      return read == null
         ? null
         : new SnapshotSource(new(read.Settings ?? [], StringComparer.OrdinalIgnoreCase), new(read.Options ?? [], StringComparer.OrdinalIgnoreCase));
   }

   public static SchemaSnapshotDto Dto(long id, string hash, int tableCount, DateTime takenAt, DateTime checkedAt, string? changes) =>
      new(id, hash, tableCount, takenAt, checkedAt, Changes(changes) is { } list
         ? new ChangeCountsDto(list.Count(c => c.Change == SchemaChangeKind.Added), list.Count(c => c.Change == SchemaChangeKind.Removed),
                               list.Count(c => c.Change == SchemaChangeKind.Changed))
         : null);

   /// <summary>
   /// Keeps a schema just read from <paramref name="connection"/> as it was then: a new snapshot when its structure
   /// changed, with what changed; otherwise the newest is brought up to date (its row counts, and what it was read
   /// with). True when a snapshot was added.
   /// </summary>
   public static async Task<bool> SaveAsync(MetadataDb db, SourceConnection connection, SourceSchema schema, DateTime now, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(db);
      ArgumentNullException.ThrowIfNull(connection);
      ArgumentNullException.ThrowIfNull(schema);
      int connectionId = connection.Id;
      string hash = Hash(schema);
      byte[] data = Compress(schema);
      string readWith = JsonSerializer.Serialize(new SnapshotSource(connection.Settings(), connection.Options()), ChangesJson);
      SchemaSnapshot? latest = await db.SchemaSnapshots.Where(s => s.ConnectionId == connectionId).OrderByDescending(s => s.Id).FirstOrDefaultAsync(cancellationToken);
      if (latest != null && latest.Hash == hash)
      {
         latest.Data = data;
         latest.TableCount = schema.Tables.Count;
         latest.CheckedAt = now;
         latest.ReadWith = readWith;
         await db.SaveChangesAsync(cancellationToken);
         return false;
      }
      List<SchemaChange>? changes = null;
      if (latest != null)
      {
         try
         {
            changes = SchemaDiff.Compare(Read(latest.Data), schema);
         }
         catch (Exception e) when (e is JsonException or InvalidDataException)
         {
            // What changed can't be told from a snapshot that can't be read.
         }
      }
      db.SchemaSnapshots.Add(new SchemaSnapshot
      {
         ConnectionId = connectionId,
         Hash = hash,
         Data = data,
         TableCount = schema.Tables.Count,
         TakenAt = now,
         CheckedAt = now,
         Changes = changes == null ? null : JsonSerializer.Serialize(changes, ChangesJson),
         ReadWith = readWith,
      });
      await db.SaveChangesAsync(cancellationToken);
      List<long> old = await db.SchemaSnapshots.Where(s => s.ConnectionId == connectionId).OrderByDescending(s => s.Id).Skip(Kept).Select(s => s.Id)
         .ToListAsync(cancellationToken);
      if (old.Count > 0) { await db.SchemaSnapshots.Where(s => old.Contains(s.Id)).ExecuteDeleteAsync(cancellationToken); }
      return true;
   }
}
