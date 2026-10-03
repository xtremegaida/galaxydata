using System;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Metadata;

namespace GalaxyData.Web.Features.Audit;

/// <summary>Records what administrators do, in the same save as what they did.</summary>
public static class AdminAudit
{
   /// <summary>The name of the application itself as an actor (the bootstrap).</summary>
   public const string System = "(system)";

   private static readonly JsonSerializerOptions DetailsJson = new(JsonSerializerDefaults.Web)
   {
      Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
   };

   public static string User(string userName) => "user:" + userName;

   /// <summary>Adds an event for <paramref name="actor"/> (null for the application) doing <paramref name="action"/>; <paramref name="details"/> is written as JSON, and must hold no secret.</summary>
   public static void Add(MetadataDb db, ClaimsPrincipal? actor, string action, string target, object? details, DateTime at)
   {
      ArgumentNullException.ThrowIfNull(db);
      db.AdminAuditEvents.Add(new AdminAuditEvent
      {
         At = at,
         ActorId = actor?.UserId(),
         ActorName = actor?.Identity?.Name ?? System,
         Action = action,
         Target = target,
         Details = details == null ? null : JsonSerializer.Serialize(details, DetailsJson),
      });
   }
}
