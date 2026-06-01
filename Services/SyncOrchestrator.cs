using JiraGitLabSync.Models;
using Microsoft.Extensions.Logging;

namespace JiraGitLabSync.Services;

public interface ISyncOrchestrator
{
    Task<SyncResult> RunSyncAsync(CancellationToken ct = default);
}

public class SyncOrchestrator : ISyncOrchestrator
{
    private readonly IJiraService _jira;
    private readonly IGitLabService _gitLab;
    private readonly ILogger<SyncOrchestrator> _log;

    public SyncOrchestrator(
        IJiraService jira,
        IGitLabService gitLab,
        ILogger<SyncOrchestrator> log)
    {
        _jira = jira;
        _gitLab = gitLab;
        _log = log;
    }

    public async Task<SyncResult> RunSyncAsync(CancellationToken ct = default)
    {
        _log.LogInformation("=== Sync run started at {Time} UTC ===", DateTime.UtcNow);
        var result = new SyncResult();

        // ── 1. Check if a current sprint milestone exists in GitLab ───────────
        //
        // Rule:
        //   Active milestone exists today  →  ALL tickets go into that milestone
        //   No active milestone            →  ALL tickets go into Backlog
        //
        var activeMilestone = await _gitLab.GetActiveMilestoneForTodayAsync(ct);

        if (activeMilestone is not null)
        {
            _log.LogInformation(
                "Active GitLab milestone found: '{Title}' (id={Id}, {Start} → {End}). " +
                "All Jira tickets will be placed here.",
                activeMilestone.Title, activeMilestone.Id,
                activeMilestone.StartDate ?? "?", activeMilestone.DueDate ?? "?");
        }
        else
        {
            _log.LogInformation(
                "No active GitLab milestone found for today ({Today}). " +
                "All tickets will be placed in the Backlog milestone.",
                DateTime.UtcNow.ToString("yyyy-MM-dd"));
        }

        // ── 2. Fetch all Jira tickets ─────────────────────────────────────────
        var sprintTickets = await _jira.GetActiveSprintTicketsAsync(ct);
        var backlogTickets = await _jira.GetBacklogTicketsAsync(ct);
        var allTickets = sprintTickets.Concat(backlogTickets).ToList();

        _log.LogInformation(
            "Jira returned {Sprint} sprint ticket(s) and {Backlog} backlog ticket(s). " +
            "Total to sync: {Total}.",
            sprintTickets.Count, backlogTickets.Count, allTickets.Count);

        // ── 3. Resolve the single target milestone for this sync run ──────────
        //
        // If an active GitLab milestone exists → use it for every ticket.
        // Otherwise → create/find the Backlog milestone and use that.
        //
        GitLabMilestone targetMilestone = activeMilestone
            ?? await _gitLab.GetOrCreateBacklogMilestoneAsync(ct);

        _log.LogInformation(
            "Target milestone for this run: '{Title}' (id={Id})",
            targetMilestone.Title, targetMilestone.Id);

        // ── 4. Sync every ticket into the target milestone ────────────────────
        foreach (var ticket in allTickets)
        {
            if (ct.IsCancellationRequested) break;
            await SyncTicketAsync(ticket, targetMilestone, result, ct);
        }

        _log.LogInformation(
            "=== Sync run complete — Created={Created}, Updated={Updated}, " +
            "Skipped={Skipped}, Failed={Failed} ===",
            result.Created, result.Updated, result.Skipped, result.Failed);

        return result;
    }

    // ── Per-ticket sync ───────────────────────────────────────────────────────

    private async Task SyncTicketAsync(
        SyncTicket ticket, GitLabMilestone targetMilestone,
        SyncResult result, CancellationToken ct)
    {
        try
        {
            var existing = await _gitLab.FindIssueByJiraKeyAsync(ticket.JiraKey, ct);

            if (existing is null)
            {
                await _gitLab.CreateIssueAsync(ticket, targetMilestone.Id, ct);
                _log.LogInformation(
                    "Created : [{JiraKey}] '{Summary}' → milestone '{Milestone}'",
                    ticket.JiraKey, Truncate(ticket.Summary, 60), targetMilestone.Title);
                result.Created++;
            }
            else
            {
                // Update the existing issue — this also moves it to the correct
                // milestone if it was previously in Backlog and a sprint started.
                await _gitLab.UpdateIssueAsync(existing.Iid, ticket, targetMilestone.Id, ct);
                _log.LogInformation(
                    "Updated : [{JiraKey}] !{Iid} '{Summary}' → milestone '{Milestone}'",
                    ticket.JiraKey, existing.Iid, Truncate(ticket.Summary, 60), targetMilestone.Title);
                result.Updated++;
            }
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            _log.LogWarning(
                "Rate-limited by GitLab on {JiraKey}. Will retry next cycle.",
                ticket.JiraKey);
            result.Skipped++;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to sync ticket {JiraKey}", ticket.JiraKey);
            result.Failed++;
            result.Errors.Add($"{ticket.JiraKey}: {ex.Message}");
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}