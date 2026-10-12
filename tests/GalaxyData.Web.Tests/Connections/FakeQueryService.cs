using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using ClickHouse.Driver.ADO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace GalaxyData.Web.Tests.Connections;

/// <summary>
/// A stand-in for a HyperWhirlpool warehouse's query service: it takes ClickHouse's HTTP protocol with a query key as
/// the password (any user name), and passes each request on to a ClickHouse server as that server's login, as the
/// service passes them to ClickHouse as its reader. A wrong key is refused as ClickHouse refuses a wrong password.
/// </summary>
internal sealed class FakeQueryService : IAsyncDisposable
{
   public const string Key = "rk_test_5f2c9a1e7b3d4086";

   private readonly WebApplication app;
   private readonly HttpClient client;

   private FakeQueryService(WebApplication app, HttpClient client, Uri address)
   {
      this.app = app;
      this.client = client;
      Address = address;
   }

   /// <summary>Where it listens: <c>http://127.0.0.1:port</c>.</summary>
   public Uri Address { get; }

   /// <summary>Starts it in front of the ClickHouse server <paramref name="clickHouse"/> (a connection string).</summary>
   public static async Task<FakeQueryService> StartAsync(string clickHouse)
   {
      ClickHouseConnectionStringBuilder server = new(clickHouse);
      Uri upstream = new($"{server.Protocol ?? "http"}://{server.Host}:{server.Port}");
      string login = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{server.Username}:{server.Password}"));
      HttpClient client = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None }) { BaseAddress = upstream };

      WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
      builder.WebHost.UseUrls("http://127.0.0.1:0");
      builder.Logging.ClearProviders();
      WebApplication app = builder.Build();
      app.Run(async context =>
      {
         if (!Authorized(context.Request))
         {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers["X-ClickHouse-Exception-Code"] = "516";
            await context.Response.WriteAsync("Code: 516. DB::Exception: Authentication failed. (AUTHENTICATION_FAILED)");
            return;
         }
         using HttpRequestMessage forward = new(new HttpMethod(context.Request.Method), context.Request.Path + context.Request.QueryString);
         if (context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0)
         {
            forward.Content = new StreamContent(context.Request.Body);
            foreach (string name in (string[])["Content-Type", "Content-Encoding"])
            {
               if (context.Request.Headers.TryGetValue(name, out var value)) { forward.Content.Headers.TryAddWithoutValidation(name, value.ToArray()); }
            }
         }
         forward.Headers.Authorization = new AuthenticationHeaderValue("Basic", login);
         using HttpResponseMessage response = await client.SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
         context.Response.StatusCode = (int)response.StatusCode;
         foreach ((string name, var values) in response.Headers.Concat(response.Content.Headers))
         {
            if (name.StartsWith("X-ClickHouse-", StringComparison.OrdinalIgnoreCase) || name is "Content-Type" or "Content-Encoding")
            {
               context.Response.Headers[name] = values.ToArray();
            }
         }
         await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
      });
      await app.StartAsync();
      return new FakeQueryService(app, client, new Uri(app.Urls.Single()));
   }

   /// <summary>Whether the request's password (Basic) or bearer token is the key.</summary>
   private static bool Authorized(HttpRequest request)
   {
      if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out AuthenticationHeaderValue? header) || header.Parameter == null) { return false; }
      if (header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)) { return header.Parameter == Key; }
      if (!header.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)) { return false; }
      string credentials = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
      return credentials[(credentials.IndexOf(':', StringComparison.Ordinal) + 1)..] == Key;
   }

   public async ValueTask DisposeAsync()
   {
      await app.StopAsync();
      await app.DisposeAsync();
      client.Dispose();
   }
}
