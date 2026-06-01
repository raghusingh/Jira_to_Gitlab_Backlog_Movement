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
    Task<List<SyncTicket>> GetActiveSprintTicketsAsync(CancellationToken ct = default);
    Task<List<SyncTicket>> GetBacklogTicketsAsync(CancellationToken ct = default);
}

public class JiraService : IJiraService
{
    private readonly HttpClient _http;
    private readonly JiraSettings _settings;
    private readonly ILogger<JiraService> _log;

    // Used ONLY for serializing request bodies:
    //   - WhenWritingNull  => nextPageToken is omitted on first page
    //   - CamelCase        => property names match Jira's expected JSON keys
    private static readonly JsonSerializerOptions _reqOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Used ONLY for deserializing responses (case-insensitive)
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

    public async Task<List<SyncTicket>> GetActiveSprintTicketsAsync(CancellationToken ct = default)
    {
        var issueTypesCsv = string.Join(",", _settings.IssueTypes.Select(t => $"\"{t}\""));
        var jql = string.Format(_settings.ActiveSprintJqlTemplate,
            _settings.ProjectKey, issueTypesCsv);

        _log.LogInformation("Fetching active-sprint tickets. JQL: {Jql}", jql);
        return (await FetchAllIssuesAsync(jql, ct)).Select(ToSyncTicket).ToList();
    }

    public async Task<List<SyncTicket>> GetBacklogTicketsAsync(CancellationToken ct = default)
    {
        var issueTypesCsv = string.Join(",", _settings.IssueTypes.Select(t => $"\"{t}\""));
        var jql = string.Format(_settings.BacklogJqlTemplate,
            _settings.ProjectKey, issueTypesCsv);

        _log.LogInformation("Fetching backlog tickets. JQL: {Jql}", jql);
        return (await FetchAllIssuesAsync(jql, ct)).Select(ToSyncTicket).ToList();
    }

    // -------------------------------------------------------------------------
    // POST /rest/api/3/search/jql
    //
    // Confirmed body schema (from Atlassian official docs):
    //   jql            string    required
    //   maxResults     int       optional  (NOT "limit" — that was wrong)
    //   fields         string[]  optional
    //   nextPageToken  string    optional  (cursor; omit entirely on first page)
    //   expand         string    optional
    //   properties     string[]  optional
    //   fieldsByKeys   bool      optional
    //
    // Pagination: response contains nextPageToken (string) and isLast (bool)
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
                NextPageToken = nextPageToken  // null on first call — omitted by WhenWritingNull
            };

            var bodyJson = JsonSerializer.Serialize(request, _reqOpts);
            _log.LogDebug("Jira POST /rest/api/3/search/jql body: {Body}", bodyJson);

            var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("rest/api/3/search/jql", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                _log.LogError("Jira {Status} {Reason} body: {Err}",
                    (int)response.StatusCode, response.ReasonPhrase, err);
                response.EnsureSuccessStatusCode();
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var page = JsonSerializer.Deserialize<JiraSearchPageResponse>(json, _resOpts)
                       ?? throw new InvalidOperationException("Jira returned null response.");

            all.AddRange(page.Issues);
            _log.LogDebug("Page: {Count} issues, isLast={IsLast}, nextToken={Token}",
                page.Issues.Count, page.IsLast, page.NextPageToken ?? "—");

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
// DTOs — property names match what Jira's API actually sends/receives
// -------------------------------------------------------------------------

/// <summary>Request body for POST /rest/api/3/search/jql</summary>
internal sealed class JiraSearchRequest
{
    // CamelCase policy maps these to: jql, maxResults, fields, nextPageToken
    public string Jql { get; set; } = string.Empty;
    public int MaxResults { get; set; } = 50;
    public string[] Fields { get; set; } = [];
    public string? NextPageToken { get; set; }  // omitted when null
}

/// <summary>Response from POST /rest/api/3/search/jql</summary>
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