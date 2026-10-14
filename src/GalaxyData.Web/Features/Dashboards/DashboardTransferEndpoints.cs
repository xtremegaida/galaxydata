using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Query.Catalog;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Dashboards;
using GalaxyData.Web.Hosting;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Palettes;
using GalaxyData.Web.Problems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace GalaxyData.Web.Features.Dashboards;

/// <summary>
/// A dashboard's file brought in as a new dashboard of the user's: under <see cref="Name"/> (none: the file's), its
/// sources' connections' aliases given others by <see cref="Aliases"/> (the file's alias, case ignored, to this
/// instance's).
/// </summary>
public sealed record ImportDashboardRequest([Required] DashboardExport Dashboard, string? Name = null, Dictionary<string, string>? Aliases = null);

/// <summary>
/// Dashboards as files (<see cref="DashboardExport"/>), to take to another instance or keep: one of the copies the user
/// sees, with the palettes it names; and a file brought in as a new dashboard of the user's, unpublished and private,
/// with the palettes it brings found or made.
/// </summary>
internal static class DashboardTransferEndpoints
{
   /// <summary>A definition may be as long as one saved, with its palettes besides.</summary>
   private const long ImportBodyLimit = 2 * 1024 * 1024;

   public static void MapDashboardTransfer(RouteGroupBuilder dashboards)
   {
      dashboards.MapGet("/{id:int}/export", ExportAsync).WithName("ExportDashboard")
         .WithSummary("A copy the user sees (the working one if they may edit it, unless copy says), with the palettes it names, as a file")
         .Produces(StatusCodes.Status200OK, typeof(DashboardExport), "application/json")
         .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);
      dashboards.MapPost("/import", ImportAsync).WithName("ImportDashboard")
         .WithSummary("Makes a dashboard of the user's from a dashboard's file, finding or making the palettes it brings")
         .WithMetadata(new RequestSizeLimitAttribute(ImportBodyLimit))
         .ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
   }

   private static async Task<Results<ContentHttpResult, ValidationProblem, ProblemHttpResult>> ExportAsync(int id, string? copy, ClaimsPrincipal me, MetadataDb db,
      TimeProvider clock, HttpResponse response, CancellationToken cancellationToken)
   {
      if (copy is not (null or "working" or "published")) { return ApiProblems.Invalid(new Dictionary<string, string[]> { ["copy"] = ["Say working or published"] }); }
      if (await DashboardEndpoints.FindAsync(db, me, id, cancellationToken) is not { } row) { return DashboardEndpoints.NoSuchDashboard(id); }
      (DashboardDefinition? working, DashboardDefinition? published) = DashboardViews.Copies(row, me);
      DashboardDefinition? chosen = copy switch
      {
         "working" => working,
         "published" => published,
         _ => working ?? published,
      };
      if (chosen == null)
      {
         return ApiProblems.Result(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "There is no such copy",
            copy == "working" ? $"{row.Name}'s working copy is {row.OwnerName}'s" : $"{row.Name} isn't published");
      }
      SortedSet<int> named = PaletteRefs.Of(chosen);
      List<Palette> palettes = named.Count == 0 ? [] : await db.Palettes.AsNoTracking().Where(p => named.Contains(p.Id)).OrderBy(p => p.Id).ToListAsync(cancellationToken);
      DashboardExport file = new(DashboardExport.FileFormat, DashboardExport.CurrentVersion, row.Name, row.Description, clock.GetUtcNow().UtcDateTime, chosen,
         [.. palettes.Select(p => new ExportedPalette(p.Id, p.Name, p.Description, PaletteJson.Read(p.DefinitionJson)))]);
      response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = DashboardExport.FileName(row.Name) }.ToString();
      return TypedResults.Text(DashboardExport.Write(file), "application/json");
   }

   private static async Task<Results<Created<DashboardDto>, ValidationProblem, ProblemHttpResult>> ImportAsync(ImportDashboardRequest request, ClaimsPrincipal me,
      MetadataDb db, DefinitionRules rules, PaletteRules paletteRules, IOptions<GalaxyDataOptions> options, TimeProvider clock, DashboardViews views,
      CancellationToken cancellationToken)
   {
      Dictionary<string, string[]> errors = [];
      DashboardExport? file = request.Dashboard;
      if (file == null) { errors["dashboard"] = ["Give the dashboard's file"]; }
      else if (file.Format != DashboardExport.FileFormat) { errors["dashboard.format"] = [$"A dashboard's file says it is {DashboardExport.FileFormat}"]; }
      else if (file.Version != DashboardExport.CurrentVersion) { errors["dashboard.version"] = [$"Dashboards' files here are of version {DashboardExport.CurrentVersion}"]; }
      if (file == null || errors.Count > 0) { return ApiProblems.Invalid(errors); }

      Dictionary<int, ExportedPalette> brought = Palettes(file.Palettes ?? [], paletteRules, options.Value.Dashboards.MaxWidgets + 1, errors);
      DashboardDefinition? definition = rules.Check(Aliased(file.Definition, request.Aliases, errors), "dashboard.definition.", errors);
      if (file.Description?.Length > MetadataDb.DescriptionLength) { errors["dashboard.description"] = [$"A description has {MetadataDb.DescriptionLength} characters at most"]; }
      if (errors.Count > 0) { return ApiProblems.Invalid(errors); }
      // A palette the file doesn't bring was gone when it was exported: here its id may be another's.
      definition = PaletteRefs.Remap(definition!, p => brought.ContainsKey(p) ? p : null);
      Dashboard row = new() { OwnerId = me.RequiredUserId(), OwnerName = me.Identity?.Name ?? string.Empty };
      string name = request.Name ?? file.Name;
      DashboardEndpoints.Fill(row, name, file.Description, definition, rules, errors);
      if (errors.Count > 0) { return ApiProblems.Invalid(errors); }
      if (await DashboardEndpoints.NameTakenAsync(db, row, cancellationToken)) { return DashboardEndpoints.NameTaken(row.Name); }

      await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
      DateTime now = clock.GetUtcNow().UtcDateTime;
      Dictionary<int, int> ids = [];
      foreach (int named in PaletteRefs.Of(definition))
      {
         ids[named] = await PaletteAsync(db, brought[named], me, now, cancellationToken);
      }
      DashboardEndpoints.Fill(row, name, file.Description, PaletteRefs.Remap(definition, p => ids[p]), rules, errors);
      row.CreatedAt = now;
      row.UpdatedAt = now;
      db.Dashboards.Add(row);
      if (!await DashboardEndpoints.SavedAsync(db, cancellationToken)) { return DashboardEndpoints.NameTaken(row.Name); }
      await transaction.CommitAsync(cancellationToken);
      return TypedResults.Created($"/api/dashboards/{row.Id}", await views.DtoAsync(db, row, me, cancellationToken));
   }

   /// <summary>The palettes a file brings, checked as they would be made, by the ids its definition names them by.</summary>
   private static Dictionary<int, ExportedPalette> Palettes(List<ExportedPalette> palettes, PaletteRules rules, int most, Dictionary<string, string[]> errors)
   {
      Dictionary<int, ExportedPalette> brought = [];
      if (palettes.Count > most)
      {
         errors["dashboard.palettes"] = [$"A dashboard names {most} palettes at most"];
         return brought;
      }
      for (int i = 0; i < palettes.Count; i++)
      {
         string path = $"dashboard.palettes[{i}]";
         if (palettes[i] is not { } palette)
         {
            errors[path] = ["A palette can't be null"];
            continue;
         }
         string name = (palette.Name ?? string.Empty).Trim();
         if (name.Length == 0) { errors[path + ".name"] = ["Name the palette"]; }
         else if (name.Length > MetadataDb.NameLength) { errors[path + ".name"] = [$"A name has {MetadataDb.NameLength} characters at most"]; }
         if (palette.Description?.Length > MetadataDb.DescriptionLength) { errors[path + ".description"] = [$"A description has {MetadataDb.DescriptionLength} characters at most"]; }
         PaletteDefinition? definition = rules.Check(palette.Definition, path + ".definition.", errors);
         if (!brought.TryAdd(palette.Id, palette with { Name = name, Definition = definition! })) { errors[path + ".id"] = ["Another palette of the file has the id"]; }
      }
      return brought;
   }

   /// <summary>The definition with its sources' entities under this instance's aliases, where <paramref name="aliases"/> gives them others.</summary>
   private static DashboardDefinition? Aliased(DashboardDefinition? definition, Dictionary<string, string>? aliases, Dictionary<string, string[]> errors)
   {
      if (definition?.Sources == null || aliases is not { Count: > 0 }) { return definition; }
      Dictionary<string, string> to = new(StringComparer.OrdinalIgnoreCase);
      foreach ((string from, string alias) in aliases)
      {
         if (EntityName.TryParse(alias, out EntityName? name) && name.Count == 1) { to[from] = name.Parts[0]; }
         else { errors[$"aliases.{from}"] = [$"'{alias}' isn't a connection's alias"]; }
      }
      return definition with
      {
         // A null source is left for the rules to refuse.
         Sources = [.. definition.Sources.Select(s => s?.Entity is { } entity && EntityName.TryParse(entity, out EntityName? name) && to.TryGetValue(name.Parts[0], out string? alias)
            ? s with { Entity = new EntityName(name.Parts.Skip(1).Prepend(alias)).ToString() }
            : s!)],
      };
   }

   /// <summary>
   /// The id of a palette like one a file brings: one of the same definition and name (anyone's: everyone uses every
   /// palette), or of the user's of the same definition (one an import made under another name); otherwise a new one of
   /// the user's, under its name or the first of "name (2)", "name (3)", … they haven't got.
   /// </summary>
   private static async Task<int> PaletteAsync(MetadataDb db, ExportedPalette palette, ClaimsPrincipal me, DateTime now, CancellationToken cancellationToken)
   {
      string json = PaletteJson.Write(palette.Definition);
      string hash = DefinitionJson.Hash(json);
      int owner = me.RequiredUserId();
      var same = await db.Palettes.Where(p => p.Hash == hash && (p.Name == palette.Name || p.OwnerId == owner)).Select(p => new { p.Id, p.Name }).ToListAsync(cancellationToken);
      if (same.OrderBy(p => string.Equals(p.Name, palette.Name, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(p => p.Id).FirstOrDefault() is { } found) { return found.Id; }
      HashSet<string> taken = new(await db.Palettes.Where(p => p.OwnerId == owner).Select(p => p.Name).ToListAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);
      string name = palette.Name;
      for (int n = 2; taken.Contains(name); n++)
      {
         string suffix = string.Create(CultureInfo.InvariantCulture, $" ({n})");
         name = palette.Name[..Math.Min(palette.Name.Length, MetadataDb.NameLength - suffix.Length)] + suffix;
      }
      Palette row = new()
      {
         OwnerId = owner,
         OwnerName = me.Identity?.Name ?? string.Empty,
         Name = name,
         Description = string.IsNullOrWhiteSpace(palette.Description) ? null : palette.Description.Trim(),
         DefinitionJson = json,
         Hash = hash,
         CreatedAt = now,
         UpdatedAt = now,
      };
      db.Palettes.Add(row);
      await db.SaveChangesAsync(cancellationToken);
      return row.Id;
   }
}
