using System.Text.Json.Serialization;

namespace JiraGitLabSync.Models;

// ── Jira API response models ──────────────────────────────────────────────────

public class JiraSearchResponse
{
    [JsonPropertyName("issues")]
    public List<JiraIssue> Issues { get; set; } = [];

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("maxResults")]
    public int MaxResults { get; set; }

    [JsonPropertyName("startAt")]
    public int StartAt { get; set; }
}

public class JiraIssue
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("fields")]
    public JiraFields Fields { get; set; } = new();
}

public class JiraFields
{
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public JiraDescription? Description { get; set; }

    [JsonPropertyName("issuetype")]
    public JiraIssueType IssueType { get; set; } = new();

    [JsonPropertyName("assignee")]
    public JiraUser? Assignee { get; set; }

    [JsonPropertyName("story_points")]
    public double? StoryPoints { get; set; }

    // Story points may be in a custom field — common field names below
    [JsonPropertyName("customfield_10016")]
    public double? StoryPointsCustom { get; set; }

    [JsonPropertyName("customfield_10028")]
    public double? StoryPointsAlt { get; set; }

    [JsonPropertyName("duedate")]
    public string? DueDate { get; set; }

    [JsonPropertyName("created")]
    public string? Created { get; set; }

    [JsonPropertyName("updated")]
    public string? Updated { get; set; }

    [JsonPropertyName("sprint")]
    public JiraSprint? Sprint { get; set; }

    // Sprint info is commonly stored in a custom field
    [JsonPropertyName("customfield_10020")]
    public List<JiraSprint>? Sprints { get; set; }

    [JsonPropertyName("status")]
    public JiraStatus Status { get; set; } = new();

    [JsonPropertyName("priority")]
    public JiraPriority? Priority { get; set; }

    /// <summary>Resolved effective story points across all possible field locations.</summary>
    public double? ResolvedStoryPoints =>
        StoryPointsCustom ?? StoryPointsAlt ?? StoryPoints;

    /// <summary>The active sprint, if any.</summary>
    public JiraSprint? ActiveSprint =>
        Sprint ?? Sprints?.FirstOrDefault(s => s.State == "active");
}

public class JiraDescription
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public List<JiraContent>? Content { get; set; }

    /// <summary>Flatten ADF (Atlassian Document Format) to plain text.</summary>
    public string ToPlainText()
    {
        if (Content is null) return string.Empty;
        var sb = new System.Text.StringBuilder();
        ExtractText(Content, sb);
        return sb.ToString().Trim();
    }

    private static void ExtractText(List<JiraContent> nodes, System.Text.StringBuilder sb)
    {
        foreach (var node in nodes)
        {
            if (node.Type == "text" && node.Text is not null)
                sb.Append(node.Text);
            else if (node.Type is "paragraph" or "heading")
                sb.AppendLine();

            if (node.Content is not null)
                ExtractText(node.Content, sb);

            if (node.Type is "paragraph" or "bulletList" or "orderedList")
                sb.AppendLine();
        }
    }
}

public class JiraContent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("content")]
    public List<JiraContent>? Content { get; set; }
}

public class JiraIssueType
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class JiraUser
{
    [JsonPropertyName("accountId")]
    public string AccountId { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("emailAddress")]
    public string EmailAddress { get; set; } = string.Empty;
}

public class JiraSprint
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("startDate")]
    public DateTime? StartDate { get; set; }

    [JsonPropertyName("endDate")]
    public DateTime? EndDate { get; set; }

    /// <summary>True if today falls within this sprint's date range.</summary>
    public bool IsCurrentSprint()
    {
        var today = DateTime.UtcNow.Date;
        return State == "active"
            && (StartDate is null || StartDate.Value.Date <= today)
            && (EndDate is null || EndDate.Value.Date >= today);
    }
}

public class JiraStatus
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class JiraPriority
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

// ── Internal sync model ───────────────────────────────────────────────────────

public class SyncTicket
{
    public string JiraKey { get; set; } = string.Empty;       // e.g. PROJ-42
    public string IssueType { get; set; } = string.Empty;     // Story / Bug / Task
    public string Summary { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? AssigneeName { get; set; }
    public string? AssigneeEmail { get; set; }
    public DateTime? StartDate { get; set; }                   // Sprint start (From)
    public DateTime? DueDate { get; set; }                     // Sprint end  (To)
    public double? StoryPoints { get; set; }
    public string? SprintName { get; set; }
    public bool HasActiveSprint { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Priority { get; set; }
}
