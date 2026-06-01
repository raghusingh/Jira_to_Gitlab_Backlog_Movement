# JiraGitLabSync — Windows Service

A .NET 8 Windows Service that replicates Jira issues (Stories, Bugs, Tasks) to GitLab issues, mapping them to the correct sprint milestone or the backlog.

---

## What it does

| Jira | GitLab |
|------|--------|
| Story / Bug / Task | Issue with matching label |
| Story ID (e.g. `PROJ-42`) | Issue title prefix `[PROJ-42]` |
| Description | Issue description (Markdown) |
| Assignee | GitLab user matched by name / e-mail |
| Sprint start → end date | Milestone `start_date` → `due_date` |
| Story Points | Issue **weight** field |
| Active sprint | Sprint milestone (created if absent) |
| No sprint / backlog | **Backlog** milestone |

---

## Prerequisites

- .NET 8 SDK / Runtime
- Windows (runs as a Windows Service)
- Jira Cloud or Server with API token access
- GitLab (self-managed or GitLab.com) with a personal access token (`api` scope)

---

## Configuration (`appsettings.json`)

```json
{
  "SyncSettings": {
    "SyncIntervalMinutes": 15,   // How often the sync runs
    "MaxRetryAttempts": 3,
    "RetryDelaySeconds": 5
  },
  "JiraSettings": {
    "BaseUrl":    "https://your-org.atlassian.net",
    "Username":   "you@company.com",
    "ApiToken":   "YOUR_JIRA_API_TOKEN",
    "ProjectKey": "PROJ",
    "IssueTypes": ["Story", "Bug", "Task"]
  },
  "GitLabSettings": {
    "BaseUrl":       "https://gitlab.com",
    "PrivateToken":  "YOUR_GITLAB_TOKEN",
    "ProjectId":     12345,
    "DefaultMilestoneTitle": "Backlog",
    "LabelMappings": {
      "Story": "story",
      "Bug":   "bug",
      "Task":  "task"
    }
  }
}
```

> **Tip:** Store secrets with `dotnet user-secrets` or environment variables to avoid committing credentials.

### Environment-variable overrides

Any config key can be overridden with an environment variable using double-underscore as separator:

```
JiraSettings__ApiToken=your-token
GitLabSettings__PrivateToken=glpat-xxxxx
```

---

## Story Points field

Jira stores story points in a *custom* field that varies by instance. The service tries three common fields in order:

1. `customfield_10016` (most common — Jira Software default)
2. `customfield_10028` (some older instances)
3. `story_points`

If yours differs, add it to `JiraFields` in `Models/JiraModels.cs` and include the field name in `BuildSearchUrl` inside `Services/JiraService.cs`.

---

## Build & Publish

```powershell
# Self-contained single-file executable for Windows x64
dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o ./publish
```

---

## Install as a Windows Service

Run **PowerShell as Administrator**:

```powershell
.\Install-Service.ps1 -BinaryPath "C:\Services\JiraGitLabSync\JiraGitLabSync.exe"
```

To remove:

```powershell
.\Uninstall-Service.ps1
```

### Manual sc.exe alternative

```powershell
sc.exe create JiraGitLabSync binPath= "C:\Services\JiraGitLabSync\JiraGitLabSync.exe" start= auto
sc.exe start  JiraGitLabSync
```

---

## How sprint → milestone resolution works

```
RunSync()
  │
  ├─ GetActiveMilestoneForTodayAsync()
  │    Looks for a GitLab milestone whose start_date ≤ today ≤ due_date
  │
  ├─ Jira: GetActiveSprintTickets()   ──→  sprint tickets
  │
  └─ For each sprint ticket:
       │
       ├─ Active milestone found? ──Yes──→ use that milestone
       │
       └─ No active milestone? ────────→ GetOrCreateBacklogMilestone()
                                          (creates "Backlog" milestone once)

  ├─ Jira: GetBacklogTickets()   ──→  always go to Backlog milestone
```

---

## Logs

Logs are written to:
- **Console** (visible via `sc query` or Windows Event Viewer when running as a service)
- **`logs/jira-gitlab-sync-YYYYMMDD.log`** — rolling daily, kept 30 days

---

## Project structure

```
JiraGitLabSync/
├── Configuration/
│   └── AppSettings.cs          # Typed settings classes
├── Models/
│   ├── JiraModels.cs           # Jira API + internal SyncTicket model
│   └── GitLabModels.cs         # GitLab API request/response models
├── Services/
│   ├── JiraService.cs          # Jira REST API client
│   ├── GitLabService.cs        # GitLab REST API client
│   └── SyncOrchestrator.cs     # Core sync logic
├── Workers/
│   └── SyncWorker.cs           # BackgroundService (timer loop + retry)
├── Program.cs                  # DI composition root
├── appsettings.json
├── Install-Service.ps1
└── Uninstall-Service.ps1
```
