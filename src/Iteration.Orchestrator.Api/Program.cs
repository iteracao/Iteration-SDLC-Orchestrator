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
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var connectionString = builder.Configuration.GetConnectionString("Default") ?? "Data Source=orchestrator.db";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

builder.Services.Configure<OllamaOptions>(
    builder.Configuration.GetSection("Ollama"));
builder.Services.Configure<OpenAIOptions>(
    builder.Configuration.GetSection("OpenAI"));

builder.Services.AddHttpClient();
builder.Services.AddHttpClient<OllamaService>();
builder.Services.AddHttpClient<OpenAITextGenerationService>();
builder.Services.AddHttpClient<IGitHubRepositoryMetadataService, GitHubRepositoryMetadataService>();
builder.Services.AddSingleton(sp =>
{
    var openAiOptions = sp.GetRequiredService<IOptions<OpenAIOptions>>().Value;
    var ollamaOptions = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
    var isOpenAiComplete = openAiOptions.IsComplete();

    return isOpenAiComplete
        ? new LlmProviderSelection(
            "OpenAI",
            NormalizeBaseUrl(openAiOptions.BaseUrl, "https://api.openai.com/v1"),
            NormalizeModel(openAiOptions.Model, "gpt-5.4"),
            Math.Max(1, openAiOptions.TimeoutSeconds),
            IsOpenAiConfigurationComplete: true,
            ApiKey: openAiOptions.ApiKey)
        : new LlmProviderSelection(
            "Ollama",
            NormalizeBaseUrl(ollamaOptions.BaseUrl, "http://127.0.0.1:11434"),
            NormalizeModel(ollamaOptions.AgentModel, ollamaOptions.DefaultModel),
            NormalizeAgentResponseTimeoutSeconds(ollamaOptions.AgentResponseTimeoutSeconds),
            IsOpenAiConfigurationComplete: false);
});
builder.Services.AddSingleton<IAgentConversationFactory>(sp =>
{
    var selection = sp.GetRequiredService<LlmProviderSelection>();
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    return new SelectedAgentConversationFactory(selection, () => httpClientFactory.CreateClient());
});
builder.Services.AddScoped<ITextGenerationService>(sp =>
{
    var selection = sp.GetRequiredService<LlmProviderSelection>();
    return string.Equals(selection.ProviderName, "OpenAI", StringComparison.OrdinalIgnoreCase)
        ? sp.GetRequiredService<OpenAITextGenerationService>()
        : sp.GetRequiredService<OllamaService>();
});
builder.Services.AddScoped<ICodeAgent, CodeAgent>();

builder.Services.AddScoped<SetupSolutionHandler>();
builder.Services.AddScoped<SetupDocumentationHandler>();
builder.Services.AddScoped<CreateRequirementHandler>();
builder.Services.AddScoped<UpdateRequirementHandler>();
builder.Services.AddScoped<DeleteRequirementHandler>();
builder.Services.AddScoped<CommitRequirementHandler>();
builder.Services.AddScoped<CancelRequirementHandler>();
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
builder.Services.AddSingleton<IWorkflowRunCancellationRegistry, InMemoryWorkflowRunCancellationRegistry>();
builder.Services.AddHostedService<WorkflowExecutionBackgroundService>();

builder.Services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
builder.Services.AddScoped<IWorkflowPayloadStore, WorkflowPayloadStore>();

var configRoot = builder.Configuration["ConfigRoot"]
    ?? Path.Combine("..", "..", "..", "..", "..", "AI", "framework");

var resolvedConfigRoot = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, configRoot));

Console.WriteLine($"CONFIG ROOT = {resolvedConfigRoot}");

builder.Services.AddSingleton<IConfigCatalog>(_ =>
    new FileSystemConfigCatalog(resolvedConfigRoot));

builder.Services.AddSingleton<ISolutionBridge, LocalFileSystemSolutionBridge>();
builder.Services.AddScoped<ISolutionAnalystAgent>(sp =>
{
    var selectedProvider = sp.GetRequiredService<LlmProviderSelection>();
    return new MicrosoftAgentFrameworkSolutionAnalystAgent(
        sp.GetRequiredService<IAgentConversationFactory>(),
        sp.GetRequiredService<IWorkflowRunLogStore>(),
        sp.GetRequiredService<IWorkflowPayloadStore>(),
        sp.GetRequiredService<IArtifactStore>(),
        sp.GetRequiredService<ISolutionBridge>(),
        sp.GetRequiredService<IConfigCatalog>(),
        selectedProvider.TimeoutSeconds);
});
builder.Services.AddScoped<ISolutionDesignerAgent>(sp =>
{
    var selectedProvider = sp.GetRequiredService<LlmProviderSelection>();
    return new MicrosoftAgentFrameworkSolutionDesignerAgent(
        sp.GetRequiredService<IAgentConversationFactory>(),
        sp.GetRequiredService<IWorkflowRunLogStore>(),
        sp.GetRequiredService<IWorkflowPayloadStore>(),
        selectedProvider.TimeoutSeconds);
});
builder.Services.AddScoped<ISolutionDocumentationSetupAgent>(sp =>
{
    var selectedProvider = sp.GetRequiredService<LlmProviderSelection>();
    return new MicrosoftAgentFrameworkSolutionDocumentationSetupAgent(
        sp.GetRequiredService<IAgentConversationFactory>(),
        sp.GetRequiredService<IWorkflowRunLogStore>(),
        sp.GetRequiredService<IWorkflowPayloadStore>(),
        sp.GetRequiredService<IArtifactStore>(),
        selectedProvider.TimeoutSeconds);
});
builder.Services.AddScoped<ISolutionPlannerAgent>(sp =>
{
    var selectedProvider = sp.GetRequiredService<LlmProviderSelection>();
    return new MicrosoftAgentFrameworkImplementationPlannerAgent(
        sp.GetRequiredService<IAgentConversationFactory>(),
        sp.GetRequiredService<IWorkflowRunLogStore>(),
        sp.GetRequiredService<IWorkflowPayloadStore>(),
        selectedProvider.TimeoutSeconds);
});
builder.Services.AddScoped<ISolutionImplementationAgent>(sp =>
{
    var selectedProvider = sp.GetRequiredService<LlmProviderSelection>();
    return new MicrosoftAgentFrameworkSolutionImplementationAgent(
        sp.GetRequiredService<IAgentConversationFactory>(),
        sp.GetRequiredService<IWorkflowRunLogStore>(),
        sp.GetRequiredService<IWorkflowPayloadStore>(),
        selectedProvider.TimeoutSeconds);
});

builder.Services.AddSingleton<ISolutionSetupService, FileSystemSolutionSetupService>();

var artifactRoot = builder.Configuration["ArtifactRoot"] ?? "data";
var resolvedArtifactRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, artifactRoot));
builder.Services.AddSingleton<IArtifactStore>(_ => new FileSystemArtifactStore(resolvedArtifactRoot));
builder.Services.AddSingleton<IWorkflowRunLogStore>(_ => new FileSystemWorkflowRunLogStore(resolvedArtifactRoot));

var app = builder.Build();
var selectedLlmProvider = app.Services.GetRequiredService<LlmProviderSelection>();

app.Logger.LogInformation(
    "Selected LLM provider: {Provider}. Selected model: {Model}. OpenAI config complete: {OpenAIConfigComplete}.",
    selectedLlmProvider.ProviderName,
    selectedLlmProvider.Model,
    selectedLlmProvider.IsOpenAiConfigurationComplete);

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();

app.Run();

static int NormalizeAgentResponseTimeoutSeconds(int? configuredTimeoutSeconds)
    => Math.Clamp(configuredTimeoutSeconds ?? 180, 1, 180);

static string NormalizeBaseUrl(string? configuredValue, string fallback)
    => string.IsNullOrWhiteSpace(configuredValue) ? fallback : configuredValue.Trim();

static string NormalizeModel(string? configuredValue, string fallback)
    => string.IsNullOrWhiteSpace(configuredValue) ? fallback : configuredValue.Trim();
