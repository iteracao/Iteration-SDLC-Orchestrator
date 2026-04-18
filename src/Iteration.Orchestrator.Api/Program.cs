using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Application.Backlog;
using Iteration.Orchestrator.Application.Requirements;
using Iteration.Orchestrator.Application.Solutions;
using Iteration.Orchestrator.Application.Workflows;
using Iteration.Orchestrator.Application.Agents;
using Iteration.Orchestrator.Application.AI;
using Iteration.Orchestrator.AgentHost.Agents;
using Iteration.Orchestrator.Infrastructure.Artifacts;
using Iteration.Orchestrator.Infrastructure.Config;
using Iteration.Orchestrator.Infrastructure.Persistence;
using Iteration.Orchestrator.Infrastructure.AI;
using Iteration.Orchestrator.Infrastructure.Solutions;
using Iteration.Orchestrator.Api.Background;
using Iteration.Orchestrator.SolutionBridge.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var connectionString = builder.Configuration.GetConnectionString("Default") ?? "Data Source=orchestrator.db";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

builder.Services.Configure<OllamaOptions>(
    builder.Configuration.GetSection("Ollama"));

builder.Services.AddHttpClient<IOllamaService, OllamaService>();
builder.Services.AddHttpClient<IGitHubRepositoryMetadataService, GitHubRepositoryMetadataService>();
builder.Services.AddScoped<ICodeAgent, CodeAgent>();

builder.Services.AddScoped<SetupSolutionHandler>();
builder.Services.AddScoped<CreateRequirementHandler>();
builder.Services.AddScoped<CreateBacklogItemHandler>();
builder.Services.AddScoped<StartAnalyzeSolutionRunHandler>();
builder.Services.AddScoped<StartDesignSolutionRunHandler>();
builder.Services.AddScoped<StartPlanImplementationRunHandler>();
builder.Services.AddScoped<StartImplementSolutionChangeRunHandler>();
builder.Services.AddScoped<ValidateWorkflowRunHandler>();
builder.Services.AddScoped<CancelWorkflowRunHandler>();
builder.Services.AddScoped<WorkflowLifecycleService>();
builder.Services.AddScoped<IWorkflowRunExecutor, WorkflowRunExecutor>();

builder.Services.AddSingleton<IWorkflowExecutionQueue, InMemoryWorkflowExecutionQueue>();
builder.Services.AddHostedService<WorkflowExecutionBackgroundService>();

builder.Services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

var configRoot = builder.Configuration["ConfigRoot"]
    ?? Path.Combine("..", "..", "..", "..", "..", "AI", "framework");

var resolvedConfigRoot = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, configRoot));

Console.WriteLine($"CONFIG ROOT = {resolvedConfigRoot}");

builder.Services.AddSingleton<IConfigCatalog>(_ =>
    new FileSystemConfigCatalog(resolvedConfigRoot));

builder.Services.AddSingleton<ISolutionBridge, LocalFileSystemSolutionBridge>();
builder.Services.AddSingleton<ISolutionAnalystAgent>(sp =>
    new MicrosoftAgentFrameworkSolutionAnalystAgent(
        builder.Configuration["Ollama:BaseUrl"] ?? "http://127.0.0.1:11434",
        builder.Configuration["Ollama:AgentModel"] ?? builder.Configuration["Ollama:DefaultModel"] ?? "qwen2.5-coder:7b",
        sp.GetRequiredService<IWorkflowRunLogStore>()));
builder.Services.AddSingleton<ISolutionDesignerAgent>(sp =>
    new MicrosoftAgentFrameworkSolutionDesignerAgent(
        builder.Configuration["Ollama:BaseUrl"] ?? "http://127.0.0.1:11434",
        builder.Configuration["Ollama:AgentModel"] ?? builder.Configuration["Ollama:DefaultModel"] ?? "qwen2.5-coder:7b",
        sp.GetRequiredService<IWorkflowRunLogStore>()));
builder.Services.AddSingleton<ISolutionPlannerAgent>(sp =>
    new MicrosoftAgentFrameworkImplementationPlannerAgent(
        builder.Configuration["Ollama:BaseUrl"] ?? "http://127.0.0.1:11434",
        builder.Configuration["Ollama:AgentModel"] ?? builder.Configuration["Ollama:DefaultModel"] ?? "qwen2.5-coder:7b",
        sp.GetRequiredService<IWorkflowRunLogStore>()));
builder.Services.AddSingleton<ISolutionImplementationAgent>(sp =>
    new MicrosoftAgentFrameworkSolutionImplementationAgent(
        builder.Configuration["Ollama:BaseUrl"] ?? "http://127.0.0.1:11434",
        builder.Configuration["Ollama:AgentModel"] ?? builder.Configuration["Ollama:DefaultModel"] ?? "qwen2.5-coder:7b",
        sp.GetRequiredService<IWorkflowRunLogStore>()));

builder.Services.AddSingleton<ISolutionSetupService, FileSystemSolutionSetupService>();

var artifactRoot = builder.Configuration["ArtifactRoot"] ?? "data";
var resolvedArtifactRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, artifactRoot));
builder.Services.AddSingleton<IArtifactStore>(_ => new FileSystemArtifactStore(resolvedArtifactRoot));
builder.Services.AddSingleton<IWorkflowRunLogStore>(_ => new FileSystemWorkflowRunLogStore(resolvedArtifactRoot));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();

app.Run();
