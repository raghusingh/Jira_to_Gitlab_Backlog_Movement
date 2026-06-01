using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JiraGitLabSync.Configuration;
using JiraGitLabSync.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JiraGitLabSync.Services;

public interface IGitLabService
{
    Task<GitLabMilestone?> GetActiveMilestoneForTodayAsync(CancellationToken ct = default);
    Task<GitLabMilestone> GetOrCreateBacklogMilestoneAsync(CancellationToken ct = default);
    Task<GitLabMilestone> GetOrCreateSprintMilestoneAsync(SyncTicket ticket, CancellationToken ct = default);
    Task<GitLabIssue?> FindIssueByJiraKeyAsync(string jiraKey, CancellationToken ct = default);
    Task<GitLabIssue> CreateIssueAsync(SyncTicket ticket, int milestoneId, CancellationToken ct = default);
    Task<GitLabIssue> UpdateIssueAsync(int issueIid, SyncTicket ticket, int milestoneId, CancellationToken ct = default);
    Task<GitLabUser?> FindUserByNameOrEmailAsync(string? name, string? email, CancellationToken ct = default);
}

public class GitLabService : IGitLabService
{
    private readonly HttpClient _http;
    private readonly GitLabSettings _settings;
    private readonly ILogger<GitLabService> _log;

    // In-memory caches to reduce API calls during a single sync run
    private readonly Dictionary<string, GitLabMilestone> _milestoneCache = new();
    private readonly Dictionary<string, GitLabUser?> _userCache = new();
    private List<GitLabMilestone>? _allMilestonesCache;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public GitLabService(
        HttpClient http,
        IOptions<GitLabSettings> settings,
        ILogger<GitLabService> log)
    {
        _http = http;
        _settings = settings.Value;
        _log = log;

        _http.BaseAddress = new Uri(_settings.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", _settings.PrivateToken);
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ── Milestone resolution ──────────────────────────────────────────────────

    public async Task<GitLabMilestone?> GetActiveMilestoneForTodayAsync(CancellationToken ct = default)
    {
        // Only look at GitLab milestones with state=active (open milestones).
        // Priority order:
        //   1. Active milestone whose date range covers today  (best match)
        //   2. Active milestone with no dates set              (fallback — treat as current)
        //
        // Milestones with state=closed are ignored entirely.

        var url = $"api/v4/projects/{_settings.ProjectId}/milestones?state=active&per_page=100";
        var active = await GetAsync<List<GitLabMilestone>>(url, ct) ?? [];
        var today = DateTime.UtcNow.Date;

        // Log every milestone found so we can see exactly what GitLab returns
        _log.LogInformation("GitLab returned {Count} active milestone(s):", active.Count);
        foreach (var m in active)
        {
            _log.LogInformation(
                "  → id={Id} title='{Title}' state={State} start_date='{Start}' due_date='{Due}'",
                m.Id, m.Title, m.State,
                m.StartDate ?? "(null)", m.DueDate ?? "(null)");
        }
        _log.LogInformation("Today (UTC) = {Today}", today.ToString("yyyy-MM-dd"));

        // 1. Prefer a milestone whose dates explicitly cover today
        foreach (var m in active)
        {
            var hasStart = DateTime.TryParse(m.StartDate, out var start);
            var hasDue = DateTime.TryParse(m.DueDate, out var due);
            _log.LogInformation(
                "  Checking '{Title}': hasStart={HasStart}({Start}) hasDue={HasDue}({Due}) coversToday={Covers}",
                m.Title, hasStart, start.ToString("yyyy-MM-dd"),
                hasDue, due.ToString("yyyy-MM-dd"),
                hasStart && hasDue && start.Date <= today && today <= due.Date);

            if (hasStart && hasDue && start.Date <= today && today <= due.Date)
            {
                _log.LogInformation(
                    "✓ Matched by date range: '{Title}' ({Start} → {Due})",
                    m.Title, m.StartDate, m.DueDate);
                return m;
            }
        }

        // 2. Fall back: any active milestone with no dates (treat as current sprint)
        var undated = active.FirstOrDefault(m =>
            string.IsNullOrEmpty(m.StartDate) && string.IsNullOrEmpty(m.DueDate));

        if (undated is not null)
        {
            _log.LogInformation(
                "✓ No dated milestone covers today — using undated active milestone: '{Title}'",
                undated.Title);
            return undated;
        }

        // 3. Last resort: use any active milestone that is NOT the Backlog milestone
        var nonBacklog = active.FirstOrDefault(m =>
            !m.Title.Equals(_settings.DefaultMilestoneTitle, StringComparison.OrdinalIgnoreCase));

        if (nonBacklog is not null)
        {
            _log.LogWarning(
                "✓ No milestone covers today and none are undated — " +
                "falling back to first non-backlog active milestone: '{Title}' " +
                "(start={Start}, due={Due}). " +
                "Set start_date and due_date on this milestone in GitLab for accurate date matching.",
                nonBacklog.Title, nonBacklog.StartDate ?? "(none)", nonBacklog.DueDate ?? "(none)");
            return nonBacklog;
        }

        _log.LogInformation(
            "✗ No suitable active milestone found for today ({Today}). Tickets will go to Backlog.",
            today.ToString("yyyy-MM-dd"));
        return null;
    }

    public async Task<GitLabMilestone> GetOrCreateBacklogMilestoneAsync(CancellationToken ct = default)
    {
        var title = _settings.DefaultMilestoneTitle;
        if (_milestoneCache.TryGetValue(title, out var cached)) return cached;

        var milestones = await GetAllActiveMilestonesAsync(ct);
        var existing = milestones.FirstOrDefault(m =>
            m.Title.Equals(title, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            _milestoneCache[title] = existing;
            return existing;
        }

        _log.LogInformation("Creating backlog milestone '{Title}'", title);
        var created = await CreateMilestoneAsync(new CreateGitLabMilestoneRequest
        {
            Title = title,
            Description = "Issues without an active sprint are placed here."
        }, ct);

        _milestoneCache[title] = created;
        _allMilestonesCache = null; // invalidate
        return created;
    }

    public async Task<GitLabMilestone> GetOrCreateSprintMilestoneAsync(
        SyncTicket ticket, CancellationToken ct = default)
    {
        var title = ticket.SprintName ?? _settings.DefaultMilestoneTitle;
        if (_milestoneCache.TryGetValue(title, out var cached)) return cached;

        var milestones = await GetAllActiveMilestonesAsync(ct);
        var existing = milestones.FirstOrDefault(m =>
            m.Title.Equals(title, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            _milestoneCache[title] = existing;
            return existing;
        }

        _log.LogInformation("Creating sprint milestone '{Title}'", title);
        var created = await CreateMilestoneAsync(new CreateGitLabMilestoneRequest
        {
            Title = title,
            StartDate = ticket.StartDate?.ToString("yyyy-MM-dd"),
            DueDate = ticket.DueDate?.ToString("yyyy-MM-dd")
        }, ct);

        _milestoneCache[title] = created;
        _allMilestonesCache = null;
        return created;
    }

    // ── Issue CRUD ────────────────────────────────────────────────────────────

    public async Task<GitLabIssue?> FindIssueByJiraKeyAsync(string jiraKey, CancellationToken ct = default)
    {
        // We embed the Jira key in the issue title as a prefix: "[PROJ-42] Summary"
        var url = $"api/v4/projects/{_settings.ProjectId}/issues" +
                  $"?search={Uri.EscapeDataString($"[{jiraKey}]")}&scope=all&per_page=5";

        var issues = await GetAsync<List<GitLabIssue>>(url, ct) ?? [];
        return issues.FirstOrDefault(i => i.Title.StartsWith($"[{jiraKey}]"));
    }

    public async Task<GitLabIssue> CreateIssueAsync(
        SyncTicket ticket, int milestoneId, CancellationToken ct = default)
    {
        var assigneeIds = await ResolveAssigneeIdsAsync(ticket, ct);

        var payload = new CreateGitLabIssueRequest
        {
            Title = BuildTitle(ticket),
            Description = BuildDescription(ticket),
            Labels = BuildLabels(ticket),
            MilestoneId = milestoneId,
            AssigneeIds = assigneeIds,
            Weight = ticket.StoryPoints.HasValue ? (int)ticket.StoryPoints.Value : null
        };

        var url = $"api/v4/projects/{_settings.ProjectId}/issues";
        _log.LogInformation("Creating GitLab issue for {JiraKey}", ticket.JiraKey);
        return await PostAsync<CreateGitLabIssueRequest, GitLabIssue>(url, payload, ct);
    }

    public async Task<GitLabIssue> UpdateIssueAsync(
        int issueIid, SyncTicket ticket, int milestoneId, CancellationToken ct = default)
    {
        var assigneeIds = await ResolveAssigneeIdsAsync(ticket, ct);

        var payload = new UpdateGitLabIssueRequest
        {
            Title = BuildTitle(ticket),
            Description = BuildDescription(ticket),
            Labels = BuildLabels(ticket),
            MilestoneId = milestoneId,
            AssigneeIds = assigneeIds,
            Weight = ticket.StoryPoints.HasValue ? (int)ticket.StoryPoints.Value : null
        };

        var url = $"api/v4/projects/{_settings.ProjectId}/issues/{issueIid}";
        _log.LogInformation(
            "Updating GitLab issue !{Iid} for {JiraKey} → milestone_id={MilestoneId}",
            issueIid, ticket.JiraKey, milestoneId);
        return await PutAsync<UpdateGitLabIssueRequest, GitLabIssue>(url, payload, ct);
    }

    // ── User resolution ───────────────────────────────────────────────────────

    // GitLab free-tier does not allow searching users via API (returns 403 Forbidden).
    // Once we get a 403, we stop attempting for the rest of this sync run and
    // create issues without an assignee instead.
    private bool _userSearchForbidden = false;

    public async Task<GitLabUser?> FindUserByNameOrEmailAsync(
        string? name, string? email, CancellationToken ct = default)
    {
        if (_userSearchForbidden) return null;

        var key = email ?? name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (_userCache.TryGetValue(key, out var cached)) return cached;

        GitLabUser? found = null;

        if (!string.IsNullOrWhiteSpace(email))
        {
            var url = $"api/v4/users?search={Uri.EscapeDataString(email)}";
            var users = await GetAsync<List<GitLabUser>>(url, ct, returnNullOn403: true);
            if (users is null)
            {
                _log.LogWarning(
                    "GitLab user search returned 403 Forbidden. " +
                    "The /api/v4/users endpoint requires admin scope — not available on free-tier GitLab.com. " +
                    "Issues will be synced without assignees.");
                _userSearchForbidden = true;
                return null;
            }
            found = users.FirstOrDefault(u =>
                u.Name.Equals(email, StringComparison.OrdinalIgnoreCase));
        }

        if (found is null && !string.IsNullOrWhiteSpace(name))
        {
            var url = $"api/v4/users?search={Uri.EscapeDataString(name)}";
            var users = await GetAsync<List<GitLabUser>>(url, ct, returnNullOn403: true);
            if (users is null)
            {
                _userSearchForbidden = true;
                return null;
            }
            found = users.FirstOrDefault(u =>
                u.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        _userCache[key] = found;
        return found;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<List<GitLabMilestone>> GetAllActiveMilestonesAsync(CancellationToken ct)
    {
        if (_allMilestonesCache is not null) return _allMilestonesCache;

        // Fetch both active and closed milestones so we can match sprint names
        var active = await GetAsync<List<GitLabMilestone>>(
            $"api/v4/projects/{_settings.ProjectId}/milestones?state=active&per_page=100", ct) ?? [];
        var closed = await GetAsync<List<GitLabMilestone>>(
            $"api/v4/projects/{_settings.ProjectId}/milestones?state=closed&per_page=100", ct) ?? [];

        _allMilestonesCache = [.. active, .. closed];
        return _allMilestonesCache;
    }

    private async Task<GitLabMilestone> CreateMilestoneAsync(
        CreateGitLabMilestoneRequest req, CancellationToken ct)
    {
        var url = $"api/v4/projects/{_settings.ProjectId}/milestones";
        return await PostAsync<CreateGitLabMilestoneRequest, GitLabMilestone>(url, req, ct);
    }

    private async Task<List<int>> ResolveAssigneeIdsAsync(SyncTicket ticket, CancellationToken ct)
    {
        if (ticket.AssigneeName is null && ticket.AssigneeEmail is null) return [];
        var user = await FindUserByNameOrEmailAsync(ticket.AssigneeName, ticket.AssigneeEmail, ct);
        return user is not null ? [user.Id] : [];
    }

    private string BuildTitle(SyncTicket ticket) =>
        $"[{ticket.JiraKey}] {ticket.Summary}";

    private string BuildDescription(SyncTicket ticket)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Jira Reference: [{ticket.JiraKey}]({_settings.BaseUrl}/browse/{ticket.JiraKey})");
        sb.AppendLine();
        sb.AppendLine($"**Type:** {ticket.IssueType}");

        if (!string.IsNullOrWhiteSpace(ticket.AssigneeName))
            sb.AppendLine($"**Assignee:** {ticket.AssigneeName}");

        if (ticket.StartDate.HasValue || ticket.DueDate.HasValue)
        {
            var from = ticket.StartDate?.ToString("yyyy-MM-dd") ?? "—";
            var to = ticket.DueDate?.ToString("yyyy-MM-dd") ?? "—";
            sb.AppendLine($"**Sprint Period:** {from} → {to}");
        }

        if (ticket.StoryPoints.HasValue)
            sb.AppendLine($"**Story Points:** {ticket.StoryPoints}");

        if (!string.IsNullOrWhiteSpace(ticket.Priority))
            sb.AppendLine($"**Priority:** {ticket.Priority}");

        if (!string.IsNullOrWhiteSpace(ticket.Status))
            sb.AppendLine($"**Jira Status:** {ticket.Status}");

        if (!string.IsNullOrWhiteSpace(ticket.Description))
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("### Description");
            sb.AppendLine();
            sb.AppendLine(ticket.Description);
        }

        return sb.ToString();
    }

    private string BuildLabels(SyncTicket ticket)
    {
        var labels = new List<string>();

        if (_settings.LabelMappings.TryGetValue(ticket.IssueType, out var label))
            labels.Add(label);

        if (!string.IsNullOrWhiteSpace(ticket.Priority))
            labels.Add($"priority::{ticket.Priority.ToLowerInvariant()}");

        return string.Join(",", labels);
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct,
        bool returnNullOn403 = false)
    {
        var response = await _http.GetAsync(url, ct);

        if (returnNullOn403 && response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            return default;

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, _json);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string url, TRequest payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, _json);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<TResponse>(body, _json)
               ?? throw new InvalidOperationException("GitLab returned null response.");
    }

    private async Task<TResponse> PutAsync<TRequest, TResponse>(
        string url, TRequest payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, _json);
        _log.LogInformation("PUT {Url} body={Body}", url, json);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PutAsync(url, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _log.LogError("PUT {Url} failed {Status}: {Body}", url, (int)response.StatusCode, err);
        }
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<TResponse>(body, _json)
               ?? throw new InvalidOperationException("GitLab returned null response.");
        // Log the milestone_id from the response to confirm GitLab accepted it
        _log.LogInformation("PUT response body={Body}", body[..Math.Min(body.Length, 300)]);
        return result;
    }
}