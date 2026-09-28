using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GalaxyData.Query.Introspection;

/// <summary>Serializes <see cref="SourceSchema"/> to compact, stable JSON; defaults are omitted.</summary>
public static class IntrospectionJson
{
   public static string Serialize(SourceSchema schema, bool indented = false)
   {
      ArgumentNullException.ThrowIfNull(schema);
      return JsonSerializer.Serialize(schema, indented ? IntrospectionJsonContext.Indented.SourceSchema : IntrospectionJsonContext.Compact.SourceSchema);
   }

   public static SourceSchema Deserialize(string json)
   {
      ArgumentNullException.ThrowIfNull(json);
      return JsonSerializer.Deserialize(json, IntrospectionJsonContext.Default.SourceSchema)
         ?? throw new JsonException("The schema document is empty");
   }

   public static SourceSchema Deserialize(Stream json)
   {
      ArgumentNullException.ThrowIfNull(json);
      return JsonSerializer.Deserialize(json, IntrospectionJsonContext.Default.SourceSchema)
         ?? throw new JsonException("The schema document is empty");
   }

   /// <summary>A SHA-256 over the compact serialization, as lower-case hex; equal schemas hash equal.</summary>
   public static string Hash(SourceSchema schema)
   {
      byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(schema, IntrospectionJsonContext.Compact.SourceSchema);
      return Convert.ToHexStringLower(SHA256.HashData(bytes));
   }
}

[JsonSourceGenerationOptions(
   PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
   DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
   UseStringEnumConverter = true)]
[JsonSerializable(typeof(SourceSchema))]
internal sealed partial class IntrospectionJsonContext : JsonSerializerContext
{
   private static IntrospectionJsonContext? compact;
   private static IntrospectionJsonContext? indented;

   // The documents are stored and sent as JSON, never embedded in HTML, so quotes and the like stay readable.
   public static IntrospectionJsonContext Compact =>
      compact ??= new(new JsonSerializerOptions(Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

   public static IntrospectionJsonContext Indented =>
      indented ??= new(new JsonSerializerOptions(Compact.Options) { WriteIndented = true });
}
