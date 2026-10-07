using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using GalaxyData.Web.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace GalaxyData.Web.Dashboards;

/// <summary>
/// Widgets' answers, kept a while for the next to ask the same (by the queries that make them, so widgets alike
/// share one), and worked out once for everyone asking at the same time: the work isn't stopped when one of them goes
/// away. A failure is kept a little while too, so a source that is down isn't asked again by every viewer at once.
/// A memory cache of its own, with a size limit: the application's holds entries without sizes.
/// </summary>
public sealed class WidgetResults : IDisposable
{
   public static readonly TimeSpan FailureDuration = TimeSpan.FromSeconds(10);

   private readonly MemoryCache cache;
   private readonly ConcurrentDictionary<string, Lazy<Task<Entry>>> running = new(StringComparer.Ordinal);
   private readonly TimeProvider clock;

   public WidgetResults(IOptions<GalaxyDataOptions> options, TimeProvider clock)
   {
      ArgumentNullException.ThrowIfNull(options);
      cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = options.Value.Dashboards.CacheSize });
      this.clock = clock;
   }

   /// <summary>
   /// The answer kept under <paramref name="key"/> (unless <paramref name="refresh"/>), or one being worked out, or one
   /// worked out now by <paramref name="compute"/> and kept for <paramref name="duration"/>; whether it was kept, and when it was worked out.
   /// </summary>
   public async Task<(T Value, bool Cached, DateTime At)> GetAsync<T>(string key, TimeSpan duration, bool refresh, Func<CancellationToken, Task<T>> compute,
                                                                     CancellationToken cancellationToken) where T : class
   {
      ArgumentNullException.ThrowIfNull(key);
      ArgumentNullException.ThrowIfNull(compute);
      if (!refresh && cache.TryGetValue(key, out Entry? kept) && kept != null) { return (Open<T>(kept), true, kept.At); }
      Lazy<Task<Entry>> work = running.GetOrAdd(key, k => new Lazy<Task<Entry>>(() => RunAsync(k, duration, async ct => await compute(ct))));
      Entry entry = await work.Value.WaitAsync(cancellationToken);
      return (Open<T>(entry), false, entry.At);
   }

   public void Dispose() => cache.Dispose();

   private static T Open<T>(Entry entry) where T : class
   {
      entry.Failure?.Throw();
      return (T)entry.Value!;
   }

   private async Task<Entry> RunAsync(string key, TimeSpan duration, Func<CancellationToken, Task<object>> compute)
   {
      Entry entry;
      try
      {
         // Its own token: whoever asked first may go, the others still want it. Queries have their own timeouts.
         object value = await Task.Run(() => compute(CancellationToken.None));
         entry = new Entry(value, null, clock.GetUtcNow().UtcDateTime);
         if (duration > TimeSpan.Zero) { cache.Set(key, entry, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = duration }); }
      }
      catch (Exception e) when (e is not OutOfMemoryException)
      {
         entry = new Entry(null, ExceptionDispatchInfo.Capture(e is TaskCanceledException { InnerException: { } inner } ? inner : e), clock.GetUtcNow().UtcDateTime);
         cache.Set(key, entry, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = FailureDuration });
      }
      finally
      {
         running.TryRemove(key, out _);
      }
      return entry;
   }

   private sealed record Entry(object? Value, ExceptionDispatchInfo? Failure, DateTime At);
}
