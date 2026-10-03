using System;
using System.Globalization;
using System.Threading;

namespace GalaxyData.Query.Execution;

/// <summary>A query that ran longer than it may: it was stopped, as a cancelled one is.</summary>
public sealed class QueryTimeoutException(TimeSpan timeout)
   : QueryExecutionException($"The query ran longer than {Deadline.Describe(timeout)}, the most it may, and was stopped")
{
   public TimeSpan Timeout { get; } = timeout;
}

/// <summary>
/// The time a query (or the writing of changes) may take: a token cancelled when the time is up or when the caller
/// cancels, which stops what is running as cancelling does. A query stopped by the time, not by the caller, has
/// <see cref="Expired"/>, and fails with <see cref="QueryTimeoutException"/>.
/// </summary>
internal sealed class Deadline : IDisposable
{
   private readonly CancellationTokenSource timer;
   private readonly CancellationTokenSource linked;
   private readonly CancellationToken caller;

   private Deadline(TimeSpan timeout, TimeProvider clock, CancellationToken caller)
   {
      Timeout = timeout;
      this.caller = caller;
      timer = new CancellationTokenSource(timeout, clock);
      linked = CancellationTokenSource.CreateLinkedTokenSource(caller, timer.Token);
   }

   /// <summary>A deadline when there is a timeout; null otherwise, and the caller's token serves alone.</summary>
   public static Deadline? Start(TimeSpan? timeout, TimeProvider clock, CancellationToken caller) =>
      timeout is { } time ? new Deadline(time, clock, caller) : null;

   public TimeSpan Timeout { get; }

   /// <summary>Cancelled when the time is up or the caller cancels.</summary>
   public CancellationToken Token => linked.Token;

   /// <summary>The time is up, and the caller didn't cancel first.</summary>
   public bool Expired => timer.IsCancellationRequested && !caller.IsCancellationRequested;

   public QueryTimeoutException Exception() => new(Timeout);

   /// <summary><c>250 ms</c>, <c>30 s</c>, <c>1.5 s</c>.</summary>
   internal static string Describe(TimeSpan time) => time < TimeSpan.FromSeconds(1)
      ? time.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture) + " ms"
      : time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s";

   public void Dispose()
   {
      linked.Dispose();
      timer.Dispose();
   }
}
