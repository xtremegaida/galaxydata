using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GalaxyData.Connectors;
using GalaxyData.Connectors.BuiltIn;
using GalaxyData.Query.DuckDb;
using GalaxyData.Query.Execution;
using GalaxyData.Query.Types;
using GalaxyData.Web.Auth;
using GalaxyData.Web.Browse;
using GalaxyData.Web.Catalog;
using GalaxyData.Web.Changes;
using GalaxyData.Web.Connections;
using GalaxyData.Web.Dashboards;
using GalaxyData.Web.Metadata;
using GalaxyData.Web.Features.Audit;
using GalaxyData.Web.Features.Auth;
using GalaxyData.Web.Features.Browse;
using GalaxyData.Web.Features.Changes;
using GalaxyData.Web.Features.Catalog;
using GalaxyData.Web.Features.Connections;
using GalaxyData.Web.Features.Dashboards;
using GalaxyData.Web.Features.Health;
using GalaxyData.Web.Features.Overlay;
using GalaxyData.Web.Features.Query;
using GalaxyData.Web.Features.Users;
using GalaxyData.Web.Overlay;
using GalaxyData.Web.Problems;
using GalaxyData.Web.Queries;
using GalaxyData.Web.Schemas;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace GalaxyData.Web.Hosting;

/// <summary>
/// How the application is put together: <see cref="AddGalaxyData"/> registers its services,
/// <see cref="UseGalaxyData"/> sets up how requests are handled, and <see cref="MapGalaxyData"/> maps the API under
/// <c>/api</c> (each feature's endpoints), its OpenAPI document, and the client for every other path.
/// </summary>
public static partial class WebApp
{
   /// <summary>The OpenAPI document's path; <c>{documentName}</c> is <c>v1</c>.</summary>
   public const string OpenApiPattern = "/api/openapi/{documentName}.json";

   private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(5);

   public static WebApplicationBuilder AddGalaxyData(this WebApplicationBuilder builder)
   {
      ArgumentNullException.ThrowIfNull(builder);
      IServiceCollection services = builder.Services;
      builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

      // Every logger's entries are masked of secrets, whichever providers write them.
      services.AddSingleton<SecretRedactor>();
      services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(sp => new RedactingLoggerFactory(
         new LoggerFactory(sp.GetServices<ILoggerProvider>(), sp.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>(), sp.GetService<IOptions<LoggerFactoryOptions>>(),
            sp.GetService<IExternalScopeProvider>()),
         sp.GetRequiredService<SecretRedactor>())));

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
      services.AddGalaxyDataRateLimits();
      services.AddOptions<HttpsRedirectionOptions>().Configure<IOptions<GalaxyDataOptions>>((o, settings) => o.HttpsPort ??= settings.Value.Security.HttpsPort);
      services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<GalaxyDataOptions>>((o, settings) => Proxies(o, settings.Value.Proxy));
      // The connectors: each kind of source, its connection and its provider. Made after the merge engine, which some
      // keep their sources' tables in, so they are disposed before it.
      services.AddSingleton(sp => BuiltInConnectors.Create(new ConnectorContext(sp.GetRequiredService<IMergeEngine>())));
      services.AddSingleton(sp => sp.GetRequiredService<ConnectorSet>().Kinds);
      services.AddSingleton<FileRoots>();
      services.AddSingleton<ConnectionSecrets>();
      services.AddSingleton<ConnectionTester>();
      services.AddSingleton<SourceProviders>();
      services.AddSingleton<SourceConnections>();
      services.AddSingleton<CatalogService>();
      services.AddSingleton<QueryEngines>();
      services.AddSingleton<BrowseService>();
      services.AddScoped<OverlayEditor>();
      services.AddSingleton<QueryService>();
      services.AddSingleton(_ => new WidgetKinds(WidgetKinds.BuiltIn()));
      services.AddSingleton<DashboardViews>();
      services.AddSingleton<WidgetResults>();
      services.AddSingleton<WidgetRunner>();
      services.AddSingleton<PublicDashboards>();
      services.AddSingleton(sp => new DefinitionRules(sp.GetRequiredService<WidgetKinds>(), sp.GetRequiredService<IOptions<GalaxyDataOptions>>().Value.Dashboards));
      services.AddSingleton<ChangePlans>();
      services.AddScoped<ChangeService>();
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
         // A widget's kind is read wherever it is in its config, as clients may write it last.
         o.SerializerOptions.AllowOutOfOrderMetadataProperties = true;
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
            // An enum used only where it may be null (a required op, checked as given) is described with null among its
            // values; the properties say they may be null.
            if (Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) is { IsEnum: true } && schema.Enum is { } values)
            {
               for (int i = values.Count - 1; i >= 0; i--)
               {
                  if (values[i] is null || values[i]!.GetValueKind() == JsonValueKind.Null) { values.RemoveAt(i); }
               }
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

   /// <summary>The proxies believed when they say who the client is: those configured, or one on the same machine.</summary>
   private static void Proxies(ForwardedHeadersOptions options, ProxySettings proxy)
   {
      options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
      options.ForwardLimit = proxy.ForwardLimit;
      if (proxy.KnownProxies is not { Count: > 0 } && proxy.KnownNetworks is not { Count: > 0 }) { return; }
      options.KnownProxies.Clear();
      options.KnownIPNetworks.Clear();
      foreach (string address in proxy.KnownProxies ?? []) { options.KnownProxies.Add(System.Net.IPAddress.Parse(address)); }
      foreach (string network in proxy.KnownNetworks ?? []) { options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network)); }
   }

   public static WebApplication UseGalaxyData(this WebApplication app)
   {
      ArgumentNullException.ThrowIfNull(app);
      // ASP.NET Core's own switch believes every address of what it says of the client. (Before the data directory
      // is held, which an application that doesn't start wouldn't let go.)
      if (string.Equals(app.Configuration["FORWARDEDHEADERS_ENABLED"], "true", StringComparison.OrdinalIgnoreCase))
      {
         throw new InvalidOperationException(
            "ASPNETCORE_FORWARDEDHEADERS_ENABLED believes any client of whom it is and how it connected: unset it, and set GalaxyData:Proxy (Enabled, and the proxies to trust) instead.");
      }
      app.Services.GetRequiredService<DataDirectory>().Open();
      app.Services.GetRequiredService<FileRoots>().Prepare();
      GalaxyDataOptions settings = app.Services.GetRequiredService<IOptions<GalaxyDataOptions>>().Value;
      app.Services.GetRequiredService<SecretRedactor>().Add(settings.Bootstrap.AdminPassword);
      // Who the client is, before anything goes by it (limits, HTTPS, cookies).
      if (settings.Proxy.Enabled) { app.UseForwardedHeaders(); }
      app.UseSecurityHeaders(settings.Security, hsts: settings.Security.Hsts && !app.Environment.IsDevelopment());
      if (settings.Security.RequireHttps) { app.UseHttpsRedirection(); }
      app.UseExceptionHandler();
      app.UseStatusCodePages();
      app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = CacheControl(context.File.Name) });
      app.UseRouting();
      app.UseAuthentication();
      // Before authorization, so requests it refuses are counted too.
      app.UseRateLimiter();
      app.UseAuthorization();
      return app;
   }

   public static WebApplication MapGalaxyData(this WebApplication app)
   {
      ArgumentNullException.ThrowIfNull(app);
      RouteGroupBuilder api = app.MapGroup("/api")
         .ProducesProblem(StatusCodes.Status429TooManyRequests)
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
      api.MapQuery();
      api.MapSavedQueries();
      api.MapChanges();
      api.MapDashboards();

      // Anyone's: outside the API's group, so without its anti-forgery check and the catalog's version.
      app.MapPublicDashboards();

      app.MapOpenApi(OpenApiPattern).RequireAuthorization(Policies.CanRead);

      // The client routes in the browser: every path but the API's and its files' is its page.
      app.MapFallbackToFile($"{{*path:{ClientPathConstraint.Name}}}", "index.html", new StaticFileOptions
      {
         OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
      }).AllowAnonymous();
      return app;
   }

   /// <summary>
   /// Files whose names have their content's hash are kept by browsers for a year; others are checked each time. As
   /// the client is built, a hash is eight capitals and digits (<c>main-LKPGWKWT.js</c>: its own files, media and
   /// workers) or, for its lazy chunks, eight of base64url's letters, digits, <c>_</c> and <c>-</c>
   /// (<c>chunk-BhQOlwLr.js</c>). It has a capital, and a capital or a digit after its first character: words and
   /// dates of eight (<c>settings-overview.txt</c>, <c>settings-Overview.txt</c>, <c>notes-20250101.txt</c>) aren't
   /// hashes.
   /// </summary>
   private static string CacheControl(string fileName) =>
      HashedFile().IsMatch(fileName) && !WordOrDate().IsMatch(fileName) ? "public, max-age=31536000, immutable" : "no-cache";

   /// <summary>Eight of base64url's characters before the extension. (Linear in the name, as is the next.)</summary>
   [GeneratedRegex("-[A-Za-z0-9_-]{8}\\.[A-Za-z0-9]+$", RegexOptions.NonBacktracking)]
   private static partial Regex HashedFile();

   /// <summary>Eight without a capital, or with one only first and no digit: a word or a date.</summary>
   [GeneratedRegex("-(?:[a-z0-9_-]{8}|[A-Z][a-z_-]{7})\\.[A-Za-z0-9]+$", RegexOptions.NonBacktracking)]
   private static partial Regex WordOrDate();

   private static DuckDbMergeOptions MergeOptions(MergeSettings settings, DataDirectory data) => new()
   {
      MemoryLimit = settings.MemoryLimit,
      Threads = settings.Threads,
      TempDirectory = settings.TempDirectory is { } temp ? data.Resolve(temp) : null,
      ExtensionDirectory = settings.ExtensionDirectory is { } extensions ? data.Resolve(extensions) : null,
      DownloadExtensions = settings.DownloadExtensions,
   };
}
