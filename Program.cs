using JiraGitLabSync.Configuration;
using JiraGitLabSync.Services;
using JiraGitLabSync.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Windows Service integration
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "JiraGitLabSync";
});

// Microsoft logging — reads from appsettings.json "Logging" section automatically
builder.Logging
    .ClearProviders()
    .AddConsole()
    .AddEventLog()              // writes to Windows Event Viewer when running as a service
    .AddDebug();                // visible in VS Output window during debugging

// Configuration
builder.Services.Configure<JiraSettings>(
    builder.Configuration.GetSection(nameof(JiraSettings)));
builder.Services.Configure<GitLabSettings>(
    builder.Configuration.GetSection(nameof(GitLabSettings)));
builder.Services.Configure<SyncSettings>(
    builder.Configuration.GetSection(nameof(SyncSettings)));

// HTTP clients
builder.Services.AddHttpClient<IJiraService, JiraService>(client =>
    client.Timeout = TimeSpan.FromSeconds(30));

builder.Services.AddHttpClient<IGitLabService, GitLabService>(client =>
    client.Timeout = TimeSpan.FromSeconds(30));

// Application services
builder.Services.AddScoped<ISyncOrchestrator, SyncOrchestrator>();
builder.Services.AddHostedService<SyncWorker>();

var host = builder.Build();
await host.RunAsync();