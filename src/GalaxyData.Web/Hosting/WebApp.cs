using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using GalaxyData.Query.DuckDb;
using GalaxyData.Query.Excel;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Types;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Browse;
using GalaxyData.Web.Catalog;
using GalaxyData.Web.Connections;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Features.Audit;
using GalaxyData.Web.Features.Auth;
using GalaxyData.Web.Features.Browse;
using GalaxyData.Web.Features.Catalog;
using GalaxyData.Web.Features.Connections;
using GalaxyData.Web.Features.Health;
using GalaxyData.Web.Features.Overlay;
using GalaxyData.Web.Features.Users;
using GalaxyData.Web.Overlay;
using GalaxyData.Web.Problems;
using GalaxyData.Web.Schemas;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace GalaxyData.Web.Hosting;

/// <summary>
/// How the application is put together: <see cref="AddGalaxyData"/> registers its services,
/// <see cref="UseGalaxyData"/> sets up how requests are handled, and <see cref="MapGalaxyData"/> maps the API under
/// <c>/api</c> (each feature's endpoints), its OpenAPI document, and the client for every other path.
/// </summary>
public static class WebApp
{
   /// <summary>The OpenAPI document's path; <c>{documentName}</c> is <c>v1</c>.</summary>
   public const string OpenApiPattern = "/api/openapi/{documentName}.json";

   private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(5);

   public static WebApplicationBuilder AddGalaxyData(this WebApplicationBuilder builder)
   {
      ArgumentNullException.ThrowIfNull(builder);
      IServiceCollection services = builder.Services;

      services.AddOptions<GalaxyDataOptions>().BindConfiguration(GalaxyDataOptions.Section).ValidateOnStart();
      services.AddSingleton<IValidateOptions<GalaxyDataOptions>, GalaxyDataOptionsValidator>();
      services.TryAddSingleton(TimeProvider.System);
      services.AddSingleton(sp => new DataDirectory(sp.GetRequiredService<IOptions<GalaxyDataOptions>>().Value, sp.GetRequiredService<IHostEnvironment>()));
      services.AddSingleton(sp => new DuckDbMergeEngine(MergeOptions(sp.GetRequiredService<IOptions<GalaxyDataOptions>>().Value.Merge,
         sp.GetRequiredService<DataDirectory>())));
      services.AddSingleton<IMergeEngine>(sp => sp.GetRequiredService<DuckDbMergeEngine>());

      services.AddDbContext<MetadataDb>((sp, o) => o.UseSqlite(MetadataDb.ConnectionString(MetadataDb.PathIn(sp.GetRequiredService<DataDirectory>()))));
      services.AddHostedService<MetadataInitializer>();
      services.AddGalaxyDataAuth();
      services.AddSingleton<ConnectionKind, PostgreSqlKind>();
      services.AddSingleton<ConnectionKind, SqlServerKind>();
      services.AddSingleton<ConnectionKind, SqliteKind>();
      services.AddSingleton<ConnectionKind, DuckDbKind>();
      services.AddSingleton<ConnectionKind, ExcelKind>();
      services.AddSingleton<ConnectionKinds>();
      services.AddSingleton<FileRoots>();
      services.AddSingleton<ConnectionSecrets>();
      services.AddSingleton<ConnectionTester>();
      services.AddSingleton(sp => new ExcelSourceProvider(sp.GetRequiredService<DuckDbMergeEngine>()));
      services.AddSingleton<SourceProviders>();
      services.AddSingleton<SourceConnections>();
      services.AddSingleton<CatalogService>();
      services.AddSingleton<QueryEngines>();
      services.AddSingleton<BrowseService>();
      services.AddScoped<OverlayEditor>();
      services.AddSingleton<SchemaReader>();
      services.AddSingleton<SchemaRefreshQueue>();
      services.AddSingleton<SchemaRefresher>();
      // After the metadata database is migrated (hosted services start in turn).
      services.AddHostedService<SchemaRefreshWorker>();
      services.AddValidation();

      services.ConfigureHttpJsonOptions(o =>
      {
         // Enums by name only: a number would be read as a value the enum hasn't (a role 7).
         o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
         // Numbers are numbers (the web defaults read them from strings too, and describe them as either).
         o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
      });
      services.AddProblemDetails(o => o.CustomizeProblemDetails = context => ApiProblems.Complete(context.ProblemDetails));
      services.AddExceptionHandler<ApiExceptionHandler>();
      services.Configure<RouteOptions>(o => o.SetParameterPolicy<ClientPathConstraint>(ClientPathConstraint.Name));

      services.AddHealthChecks()
         .AddCheck<DataDirectoryHealthCheck>(DataDirectoryHealthCheck.Name, timeout: HealthCheckTimeout)
         .AddCheck<MergeEngineHealthCheck>(MergeEngineHealthCheck.Name, timeout: HealthCheckTimeout);

      services.AddOpenApi("v1", o =>
      {
         o.AddDocumentTransformer((document, _, _) =>
         {
            document.Info = new OpenApiInfo
            {
               Title = "GalaxyData",
               Version = "v1",
               Description = "Browse, query and change data across databases and folders of workbooks. Errors are problem details (RFC 9457) with a code.",
            };
            // Clients call the API where they found it.
            document.Servers?.Clear();
            return Task.CompletedTask;
         });
         o.AddSchemaTransformer((schema, context, _) =>
         {
            if (context.JsonTypeInfo.Type == typeof(ProblemDetails)) { DescribeProblem(schema); }
            if (context.JsonTypeInfo.Type == typeof(ScalarType))
            {
               // Written as the language writes types, by its own converter.
               schema.Type = JsonSchemaType.String;
               schema.Description = "A logical type as the query language writes it: int64, decimal(10,2)?, string(50,ansi); ? when it may be null";
            }
            return Task.CompletedTask;
         });
      });
      return builder;
   }

   /// <summary>Every problem has a code (<see cref="ProblemCodes"/>) and the request's trace id; the schema says so.</summary>
   private static void DescribeProblem(OpenApiSchema schema)
   {
      schema.Properties ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
      schema.Properties["code"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "What the problem is, for clients to tell problems apart by" };
      schema.Properties["traceId"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "The request's trace, to find it in the logs" };
      schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
      schema.Required.Add("code");
   }

   public static WebApplication UseGalaxyData(this WebApplication app)
   {
      ArgumentNullException.ThrowIfNull(app);
      app.Services.GetRequiredService<DataDirectory>().Open();
      app.Services.GetRequiredService<FileRoots>().Prepare();
      app.UseExceptionHandler();
      app.UseStatusCodePages();
      app.UseStaticFiles();
      app.UseRouting();
      app.UseAuthentication();
      app.UseAuthorization();
      app.UseRateLimiter();
      return app;
   }

   public static WebApplication MapGalaxyData(this WebApplication app)
   {
      ArgumentNullException.ThrowIfNull(app);
      RouteGroupBuilder api = app.MapGroup("/api")
         .ProducesProblem(StatusCodes.Status500InternalServerError)
         .AddEndpointFilter<AntiforgeryFilter>()
         .AddEndpointFilter<CatalogVersionFilter>();
      api.MapHealth();
      api.MapAuth();
      api.MapUsers();
      api.MapAudit();
      api.MapConnections();
      api.MapCatalog();
      api.MapBrowse();
      api.MapOverlay();

      app.MapOpenApi(OpenApiPattern).RequireAuthorization(Policies.CanRead);

      // The client routes in the browser: every path but the API's and its files' is its page.
      app.MapFallbackToFile($"{{*path:{ClientPathConstraint.Name}}}", "index.html", new StaticFileOptions
      {
         OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
      }).AllowAnonymous();
      return app;
   }

   private static DuckDbMergeOptions MergeOptions(MergeSettings settings, DataDirectory data) => new()
   {
      MemoryLimit = settings.MemoryLimit,
      Threads = settings.Threads,
      TempDirectory = settings.TempDirectory is { } temp ? data.Resolve(temp) : null,
      ExtensionDirectory = settings.ExtensionDirectory is { } extensions ? data.Resolve(extensions) : null,
      DownloadExtensions = settings.DownloadExtensions,
   };
}
