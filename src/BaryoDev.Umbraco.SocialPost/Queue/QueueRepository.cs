using System.Data.Common;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;

namespace BaryoDev.Umbraco.SocialPost.Queue;

public sealed class QueueRepository(IScopeProvider scopeProvider)
{
    public QueueRow? Get(int id)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database.SingleOrDefault<QueueRow>("WHERE Id = @0", id);
    }

    public QueueRow? Find(Guid contentKey, string network)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database.SingleOrDefault<QueueRow>("WHERE ContentKey = @0 AND Network = @1", contentKey, network);
    }

    public List<QueueRow> List(string? status, int take)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        List<QueueRow> rows = status is null
            ? scope.Database.Fetch<QueueRow>("ORDER BY UpdatedUtc DESC")
            : scope.Database.Fetch<QueueRow>("WHERE Status = @0 ORDER BY UpdatedUtc DESC", status);
        return rows.Take(take).ToList();
    }

    public List<QueueRow> Due(DateTime nowUtc, int take)
    {
        using var scope = scopeProvider.CreateScope(autoComplete: true);
        return scope.Database
            .Fetch<QueueRow>("WHERE Status = @0 AND NextAttemptUtc <= @1 ORDER BY NextAttemptUtc", QueueStatus.Pending, nowUtc)
            .Take(take).ToList();
    }

    /// <summary>Marks rows stuck in "sending" as failed. One statement, so it cannot undo a row that just finished.</summary>
    public void FailStale(DateTime olderThanUtc, DateTime nowUtc, string message)
    {
        using var scope = scopeProvider.CreateScope();
        scope.Database.Exec(
            "UPDATE socialPostQueue SET Status = @0, UpdatedUtc = @1, LastError = @2 WHERE Status = @3 AND UpdatedUtc < @4",
            QueueStatus.Failed, nowUtc, message, QueueStatus.Sending, olderThanUtc);
        scope.Complete();
    }

    /// <summary>False when a row for this content and network already exists. Any other insert failure is thrown.</summary>
    public bool TryInsert(QueueRow row)
    {
        try
        {
            using var scope = scopeProvider.CreateScope();
            scope.Database.Insert(row);
            scope.Complete();
            return true;
        }
        catch (DbException)
        {
            if (Find(row.ContentKey, row.Network) is not null)
            {
                return false;
            }

            throw;
        }
    }

    /// <summary>Writes the end of a send. Only applies while the row is still the same send (same generation, still "sending").</summary>
    public bool Finish(QueueRow r, DateTime nowUtc)
    {
        using var scope = scopeProvider.CreateScope();
        int changed = scope.Database.Exec(
            "UPDATE socialPostQueue SET Status = @0, Attempts = @1, NextAttemptUtc = @2, UpdatedUtc = @3, SentUtc = @4, RemoteId = @5, RemoteUrl = @6, LastError = @7 "
            + "WHERE Id = @8 AND Generation = @9 AND Status = @10",
            r.Status, r.Attempts, r.NextAttemptUtc, nowUtc, r.SentUtc, r.RemoteId, r.RemoteUrl, r.LastError,
            r.Id, r.Generation, QueueStatus.Sending);
        scope.Complete();
        return changed == 1;
    }

    /// <summary>Starts the row over: new generation, new snapshot. Not applied to a row that is being sent.</summary>
    public bool Force(int id, DateTime dueUtc, DateTime nowUtc, QueueRow snap)
    {
        using var scope = scopeProvider.CreateScope();
        int changed = scope.Database.Exec(
            "UPDATE socialPostQueue SET Generation = Generation + 1, Attempts = 0, Status = @0, NextAttemptUtc = @1, UpdatedUtc = @2, "
            + "SentUtc = NULL, RemoteId = NULL, RemoteUrl = NULL, LastError = NULL, Title = @3, Url = @4, Summary = @5, ImageUrl = @6, Tags = @7 "
            + "WHERE Id = @8 AND Status <> @9",
            QueueStatus.Pending, dueUtc, nowUtc, snap.Title, snap.Url, snap.Summary, snap.ImageUrl, snap.Tags, id, QueueStatus.Sending);
        scope.Complete();
        return changed == 1;
    }

    /// <summary>Moves a still pending row to a new time and snapshot.</summary>
    public bool Reschedule(int id, DateTime dueUtc, DateTime nowUtc, QueueRow snap)
    {
        using var scope = scopeProvider.CreateScope();
        int changed = scope.Database.Exec(
            "UPDATE socialPostQueue SET NextAttemptUtc = @0, UpdatedUtc = @1, Title = @2, Url = @3, Summary = @4, ImageUrl = @5, Tags = @6 "
            + "WHERE Id = @7 AND Status = @8",
            dueUtc, nowUtc, snap.Title, snap.Url, snap.Summary, snap.ImageUrl, snap.Tags, id, QueueStatus.Pending);
        scope.Complete();
        return changed == 1;
    }

    /// <summary>Failed or cancelled rows go back to pending with the same generation.</summary>
    public bool Retry(int id, DateTime nowUtc)
    {
        using var scope = scopeProvider.CreateScope();
        int changed = scope.Database.Exec(
            "UPDATE socialPostQueue SET Status = @0, Attempts = 0, NextAttemptUtc = @1, UpdatedUtc = @1, LastError = NULL "
            + "WHERE Id = @2 AND Status IN (@3, @4)",
            QueueStatus.Pending, nowUtc, id, QueueStatus.Failed, QueueStatus.Cancelled);
        scope.Complete();
        return changed == 1;
    }

    /// <summary>Moves one row from one status to another in a single statement. True when this caller won.</summary>
    public bool Transition(int id, string from, string to, DateTime nowUtc, string? error = null)
    {
        using var scope = scopeProvider.CreateScope();
        int changed = scope.Database.Exec(
            "UPDATE socialPostQueue SET Status = @0, UpdatedUtc = @1, LastError = COALESCE(@2, LastError) WHERE Id = @3 AND Status = @4",
            to, nowUtc, error, id, from);
        scope.Complete();
        return changed == 1;
    }
}

internal static class DatabaseExtensions
{
    // Nulls are valid statement arguments; the params signature is just not annotated for them.
    public static int Exec(this IUmbracoDatabase db, string sql, params object?[] args) => db.Execute(sql, args!);
}
