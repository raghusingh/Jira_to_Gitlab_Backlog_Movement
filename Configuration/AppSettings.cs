namespace JiraGitLabSync.Configuration;

public class JiraSettings
{
    public string BaseUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
    public string ProjectKey { get; set; } = string.Empty;
    public List<string> IssueTypes { get; set; } = ["Story", "Bug", "Task"];
    public string ActiveSprintJqlTemplate { get; set; } = string.Empty;
    public string BacklogJqlTemplate { get; set; } = string.Empty;
}

public class GitLabSettings
{
    public string BaseUrl { get; set; } = string.Empty;
    public string PrivateToken { get; set; } = string.Empty;
    public int ProjectId { get; set; }
    public string DefaultMilestoneTitle { get; set; } = "Backlog";
    public Dictionary<string, string> LabelMappings { get; set; } = new();
}

public class SyncSettings
{
    public int SyncIntervalMinutes { get; set; } = 15;
    public int MaxRetryAttempts { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 5;
}
