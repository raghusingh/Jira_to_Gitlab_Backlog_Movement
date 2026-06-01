using System.Text.Json.Serialization;

namespace JiraGitLabSync.Models;

// ── GitLab API request/response models ────────────────────────────────────────

public class GitLabIssue
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("iid")]
    public int Iid { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("labels")]
    public List<string> Labels { get; set; } = [];

    [JsonPropertyName("milestone")]
    public GitLabMilestone? Milestone { get; set; }

    [JsonPropertyName("assignees")]
    public List<GitLabUser> Assignees { get; set; } = [];

    [JsonPropertyName("web_url")]
    public string WebUrl { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

public class GitLabMilestone
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("iid")]
    public int Iid { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("start_date")]
    public string? StartDate { get; set; }

    [JsonPropertyName("due_date")]
    public string? DueDate { get; set; }

    [JsonPropertyName("web_url")]
    public string WebUrl { get; set; } = string.Empty;
}

public class GitLabUser
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

// ── GitLab API create/update payloads ─────────────────────────────────────────

public class CreateGitLabIssueRequest
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("labels")]
    public string? Labels { get; set; }          // comma-separated

    [JsonPropertyName("milestone_id")]
    public int? MilestoneId { get; set; }

    [JsonPropertyName("assignee_ids")]
    public List<int>? AssigneeIds { get; set; }

    [JsonPropertyName("weight")]
    public int? Weight { get; set; }             // story points → issue weight
}

public class UpdateGitLabIssueRequest
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("labels")]
    public string? Labels { get; set; }

    [JsonPropertyName("milestone_id")]
    public int? MilestoneId { get; set; }

    [JsonPropertyName("assignee_ids")]
    public List<int>? AssigneeIds { get; set; }

    [JsonPropertyName("weight")]
    public int? Weight { get; set; }
}

public class CreateGitLabMilestoneRequest
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("start_date")]
    public string? StartDate { get; set; }       // yyyy-MM-dd

    [JsonPropertyName("due_date")]
    public string? DueDate { get; set; }         // yyyy-MM-dd
}

// ── Internal sync state ────────────────────────────────────────────────────────

public class SyncResult
{
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; set; } = [];
    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;

    public override string ToString() =>
        $"Created={Created}, Updated={Updated}, Skipped={Skipped}, Failed={Failed}";
}
