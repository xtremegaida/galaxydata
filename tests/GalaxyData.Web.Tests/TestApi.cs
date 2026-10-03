using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Auth;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Shouldly;
using Xunit;

namespace GalaxyData.Web.Tests;

/// <summary>
/// A client of the API as the browser's is: it keeps the cookies it is given, and sends the anti-forgery token from
/// the <c>XSRF-TOKEN</c> cookie in the <c>X-XSRF-TOKEN</c> header of requests that change anything (asking for the
/// session first when it has none). Every response it gets is kept, headers and body, to look for secrets in.
/// </summary>
internal sealed class TestApi
{
   public const string AdminName = "admin";

   public const string AdminPassword = "bootstrap-pass-4-tests";

   private static readonly Uri Base = new("http://localhost/");

   private readonly CookieContainer cookies = new();

   public TestApi(WebAppFactory factory)
   {
      ArgumentNullException.ThrowIfNull(factory);
      Client = factory.CreateDefaultClient(Base, new Recorder(Responses), new CookieContainerHandler(cookies));
   }

   public HttpClient Client { get; }

   /// <summary>Every response's headers and body, as text.</summary>
   public ConcurrentQueue<string> Responses { get; } = new();

   private static CancellationToken Token => TestContext.Current.CancellationToken;

   public Cookie? Cookie(string name) => cookies.GetCookies(Base)[name];

   /// <summary>A client signed in as <paramref name="userName"/>, which has changed the password to <paramref name="changeTo"/> when it had to.</summary>
   public static async Task<TestApi> SignedInAsync(WebAppFactory factory, string userName = AdminName, string password = AdminPassword, string? changeTo = null)
   {
      TestApi api = new(factory);
      JsonElement session = await (await api.SignInAsync(userName, password)).JsonAsync(HttpStatusCode.OK);
      if (session.GetProperty("user").GetProperty("mustChangePassword").GetBoolean())
      {
         changeTo.ShouldNotBeNull($"{userName} must change their password");
         await (await api.PostAsync("/api/auth/change-password", new { currentPassword = password, newPassword = changeTo })).JsonAsync(HttpStatusCode.OK);
      }
      return api;
   }

   public async Task<JsonElement> SessionAsync() => await (await Client.GetAsync("/api/auth/session", Token)).JsonAsync(HttpStatusCode.OK);

   public Task<HttpResponseMessage> SignInAsync(string userName, string password) => PostAsync("/api/auth/sign-in", new { userName, password });

   public Task<HttpResponseMessage> GetAsync(string path) => Client.GetAsync(path, Token);

   public Task<HttpResponseMessage> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body);

   public Task<HttpResponseMessage> PutAsync(string path, object? body) => SendAsync(HttpMethod.Put, path, body);

   public Task<HttpResponseMessage> DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path, null);

   /// <summary>A request that changes something, with the anti-forgery token (unless <paramref name="withToken"/> is false).</summary>
   public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, bool withToken = true)
   {
      if (withToken && Cookie(Xsrf.CookieName) == null) { await SessionAsync(); }
      using HttpRequestMessage request = new(method, path);
      if (body != null) { request.Content = JsonContent.Create(body); }
      if (withToken) { request.Headers.Add(Xsrf.HeaderName, Cookie(Xsrf.CookieName)!.Value); }
      return await Client.SendAsync(request, Token);
   }

   /// <summary>Makes a user (who must change the password at the first sign-in); their id.</summary>
   public async Task<int> CreateUserAsync(string userName, string role, string password, string? displayName = null)
   {
      JsonElement user = await (await PostAsync("/api/users", new { userName, displayName, role, password })).JsonAsync(HttpStatusCode.Created);
      return user.GetProperty("id").GetInt32();
   }

   private sealed class Recorder(ConcurrentQueue<string> responses) : DelegatingHandler
   {
      protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
         await response.Content.LoadIntoBufferAsync(cancellationToken);
         string headers = string.Join("\n", response.Headers.Concat(response.Content.Headers).Select(h => $"{h.Key}: {string.Join(", ", h.Value)}"));
         responses.Enqueue($"{(int)response.StatusCode} {request.Method} {request.RequestUri}\n{headers}\n\n{await response.Content.ReadAsStringAsync(cancellationToken)}");
         return response;
      }
   }
}

internal static class JsonResponses
{
   /// <summary>The JSON body of a response, checked to have <paramref name="status"/> (the body says why it hasn't).</summary>
   public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response, HttpStatusCode status)
   {
      string text = await response.Content.ReadAsStringAsync();
      response.StatusCode.ShouldBe(status, text);
      return JsonDocument.Parse(text).RootElement;
   }
}

/// <summary>A clock that moves when told to.</summary>
internal sealed class ManualClock : TimeProvider
{
   private DateTimeOffset now = DateTimeOffset.UtcNow;

   public override DateTimeOffset GetUtcNow() => now;

   public void Advance(TimeSpan by) => now += by;
}

/// <summary>A data directory that outlives the applications that use it in turn; removed when disposed, once none holds it.</summary>
internal sealed class SharedData : IAsyncDisposable
{
   private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gd-web-tests", Guid.NewGuid().ToString("N")[..12]);

   public string Path => System.IO.Path.Combine(root, "data");

   public async ValueTask DisposeAsync()
   {
      await WebAppFactory.ReleasedAsync(Path);
      if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
   }
}
