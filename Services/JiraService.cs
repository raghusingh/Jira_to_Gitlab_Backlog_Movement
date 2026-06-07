using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JiraGitLabSync.Configuration;
using JiraGitLabSync.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JiraGitLabSync.Services;

public interface IJiraService
{
    /// <summary>Fetch ALL project tickets in one query — no sprint filter.</summary>
    Task<List<SyncTicket>> GetAllTicketsAsync(CancellationToken ct = default);

    // Kept for interface compatibility — both return empty lists
    Task<List<SyncTicket>> GetActiveSprintTicketsAsync(CancellationToken ct = default);
    Task<List<SyncTicket>> GetBacklogTicketsAsync(CancellationToken ct = default);
}

public class JiraService : IJiraService
{
    private readonly HttpClient _http;
    private readonly JiraSettings _settings;
    private readonly ILogger<JiraService> _log;

    private static readonly JsonSerializerOptions _reqOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions _resOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] _searchFields =
    [
        "summary", "description", "issuetype", "assignee",
        "status", "priority", "duedate", "created", "updated",
        "customfield_10016",
        "customfield_10028",
        "customfield_10020"
    ];

    public JiraService(
        HttpClient http,
        IOptions<JiraSettings> settings,
        ILogger<JiraService> log)
    {
        _http = http;
        _settings = settings.Value;
        _log = log;

        // Basic Auth: base64(username:apitoken)
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_settings.Username}:{_settings.ApiToken}"));

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", credentials);
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        _http.BaseAddress = new Uri(_settings.BaseUrl.TrimEnd('/') + "/");
    }

    // -------------------------------------------------------------------------
    // Public
    // -------------------------------------------------------------------------

    /// <summary>
    /// Single unified query — fetches ALL tickets regardless of sprint/backlog.
    /// Fix 1: Uses .Distinct() to prevent duplicate issue types in JQL.
    /// Fix 2: No sprint filter — works for tickets in sprint AND backlog.
    /// Fix 3: No JQL templates from config — built directly here.
    /// </summary>
    public async Task<List<SyncTicket>> GetAllTicketsAsync(CancellationToken ct = default)
    {
        // Fix 1: Deduplicate issue types to prevent:
        // issuetype in ("Story","Bug","Task","Story","Bug","Task")
        var distinctTypes = _settings.IssueTypes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var issueTypesCsv = string.Join(",", distinctTypes.Select(t => $"\"{t}\""));

        // Fix 2: No sprint filter — fetches sprint tickets AND backlog tickets
        var jql = $"project = \"{_settings.ProjectKey}\" " +
                  $"AND issuetype in ({issueTypesCsv}) " +
                  $"ORDER BY created DESC";

        _log.LogInformation("Fetching all tickets from Jira. JQL: {Jql}", jql);
        var issues = await FetchAllIssuesAsync(jql, ct);
        _log.LogInformation("Jira returned {Count} ticket(s) total.", issues.Count);
        return issues.Select(ToSyncTicket).ToList();
    }

    // Kept for interface compatibility — not called by SyncOrchestrator anymore
    public Task<List<SyncTicket>> GetActiveSprintTicketsAsync(CancellationToken ct = default)
        => Task.FromResult(new List<SyncTicket>());

    public Task<List<SyncTicket>> GetBacklogTicketsAsync(CancellationToken ct = default)
        => Task.FromResult(new List<SyncTicket>());

    // -------------------------------------------------------------------------
    // POST /rest/api/3/search/jql  (cursor-based pagination)
    // -------------------------------------------------------------------------

    private async Task<List<JiraIssue>> FetchAllIssuesAsync(string jql, CancellationToken ct)
    {
        var all = new List<JiraIssue>();
        string? nextPageToken = null;

        while (true)
        {
            var request = new JiraSearchRequest
            {
                Jql = jql,
                MaxResults = 50,
                Fields = _searchFields,
                NextPageToken = nextPageToken
            };

            var bodyJson = JsonSerializer.Serialize(request, _reqOpts);
            _log.LogDebug("Jira POST body: {Body}", bodyJson);

            var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("rest/api/3/search/jql", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                _log.LogError("Jira {Status} {Reason}: {Body}",
                    (int)response.StatusCode, response.ReasonPhrase, err);
                response.EnsureSuccessStatusCode();
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var page = JsonSerializer.Deserialize<JiraSearchPageResponse>(json, _resOpts)
                       ?? throw new InvalidOperationException("Jira returned null response.");

            all.AddRange(page.Issues);
            _log.LogDebug("Page: {Count} issues, isLast={IsLast}, nextToken={Token}",
                page.Issues.Count, page.IsLast, page.NextPageToken ?? "none");

            if (page.IsLast || page.Issues.Count == 0 || string.IsNullOrEmpty(page.NextPageToken))
                break;

            nextPageToken = page.NextPageToken;
        }

        return all;
    }

    // -------------------------------------------------------------------------
    // Mapping
    // -------------------------------------------------------------------------

    private static SyncTicket ToSyncTicket(JiraIssue issue)
    {
        var sprint = issue.Fields.ActiveSprint;
        return new SyncTicket
        {
            JiraKey = issue.Key,
            IssueType = issue.Fields.IssueType.Name,
            Summary = issue.Fields.Summary,
            Description = issue.Fields.Description?.ToPlainText() ?? string.Empty,
            AssigneeName = issue.Fields.Assignee?.DisplayName,
            AssigneeEmail = issue.Fields.Assignee?.EmailAddress,
            StartDate = sprint?.StartDate,
            DueDate = sprint?.EndDate
                              ?? (DateTime.TryParse(issue.Fields.DueDate, out var d) ? d : null),
            StoryPoints = issue.Fields.ResolvedStoryPoints,
            SprintName = sprint?.Name,
            HasActiveSprint = sprint?.IsCurrentSprint() == true,
            Status = issue.Fields.Status.Name,
            Priority = issue.Fields.Priority?.Name
        };
    }
}

// -------------------------------------------------------------------------
// DTOs
// -------------------------------------------------------------------------

internal sealed class JiraSearchRequest
{
    public string Jql { get; set; } = string.Empty;
    public int MaxResults { get; set; } = 50;
    public string[] Fields { get; set; } = [];
    public string? NextPageToken { get; set; }
}

internal sealed class JiraSearchPageResponse
{
    [JsonPropertyName("issues")]
    public List<JiraIssue> Issues { get; set; } = [];

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; set; }

    [JsonPropertyName("isLast")]
    public bool IsLast { get; set; }

    [JsonPropertyName("total")]
    public int? Total { get; set; }
}