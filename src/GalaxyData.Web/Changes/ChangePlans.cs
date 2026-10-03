using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using GalaxyData.Query.Dml;

namespace GalaxyData.Web.Changes;

/// <summary>A pending change a plan carries out (by the engine's index of it): its id, its version when previewed, and a new row's temporary id.</summary>
public sealed record PlannedChange(long Id, int Version, string? TempId);

/// <summary>
/// A preview's plan: its user may commit it until it expires, while their changes (<see cref="Version"/>) and the
/// catalog (<see cref="CatalogVersion"/>) are as they were when it was made.
/// </summary>
public sealed class ChangePlan(string id, int userId, int version, string catalogVersion, DmlPlan plan, IReadOnlyList<PlannedChange> changes, DateTime expiresAt)
{
   public string Id { get; } = id;

   public int UserId { get; } = userId;

   public int Version { get; } = version;

   public string CatalogVersion { get; } = catalogVersion;

   public DmlPlan Plan { get; } = plan;

   /// <summary>The changes, by the engine's index of them (<see cref="DmlStatement.ChangeIndex"/>).</summary>
   public IReadOnlyList<PlannedChange> Changes { get; } = changes;

   public DateTime ExpiresAt { get; } = expiresAt;
}

/// <summary>
/// The plans of previews, one for each user: a preview replaces the one before. A plan is taken to be committed,
/// once; plans that expired go as others are made.
/// </summary>
public sealed class ChangePlans(TimeProvider clock)
{
   private readonly ConcurrentDictionary<int, ChangePlan> plans = new();

   /// <summary>The users whose changes are being committed.</summary>
   private readonly ConcurrentDictionary<int, bool> committing = new();

   /// <summary>
   /// Whether the user's changes are being committed: until the changes written are cleared, a preview would plan
   /// them again, and a commit of it write them twice.
   /// </summary>
   public bool IsCommitting(int userId) => committing.ContainsKey(userId);

   /// <summary>Marks the user's changes as being committed; false when they are already.</summary>
   public bool BeginCommit(int userId) => committing.TryAdd(userId, true);

   public void EndCommit(int userId) => committing.TryRemove(userId, out _);

   public void Put(ChangePlan plan)
   {
      ArgumentNullException.ThrowIfNull(plan);
      DateTime now = clock.GetUtcNow().UtcDateTime;
      foreach (KeyValuePair<int, ChangePlan> old in plans)
      {
         if (old.Value.ExpiresAt <= now) { plans.TryRemove(old); }
      }
      plans[plan.UserId] = plan;
   }

   public void Drop(int userId) => plans.TryRemove(userId, out _);

   /// <summary>The user's plan of that id, unless it expired or another replaced it.</summary>
   public ChangePlan? Find(int userId, string id) =>
      plans.TryGetValue(userId, out ChangePlan? plan) && string.Equals(plan.Id, id, StringComparison.Ordinal) && plan.ExpiresAt > clock.GetUtcNow().UtcDateTime
         ? plan
         : null;

   /// <summary>Takes the plan to commit it; false when another took it first, or replaced it.</summary>
   public bool TryTake(ChangePlan plan)
   {
      ArgumentNullException.ThrowIfNull(plan);
      return plans.TryRemove(KeyValuePair.Create(plan.UserId, plan));
   }
}
