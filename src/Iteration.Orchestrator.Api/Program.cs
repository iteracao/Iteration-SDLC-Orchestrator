using Iteration.Orchestrator.Application.Abstractions;
using Iteration.Orchestrator.Application.Backlog;
using Iteration.Orchestrator.Application.Solutions;
using Iteration.Orchestrator.Application.Workflows;
using Iteration.Orchestrator.AgentHost.Agents;
using Iteration.Orchestrator.Infrastructure.Artifacts;
using Iteration.Orchestrator.Infrastructure.Config;
using Iteration.Orchestrator.Infrastructure.Persistence;
using Iteration.Orchestrator.SolutionBridge.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var connectionString = builder.Configuration.GetConnectionString("Default") ?? "Data Source=orchestrator.db";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

builder.Services.AddScoped<RegisterSolutionTargetHandler>();
builder.Services.AddScoped<CreateBacklogItemHandler>();
builder.Services.AddScoped<StartAnalyzeSolutionRunHandler>();

builder.Services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

var configRoot = builder.Configuration["ConfigRoot"] ?? "../../config";
builder.Services.AddSingleton<IConfigCatalog>(_ => new FileSystemConfigCatalog(
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configRoot))));

builder.Services.AddSingleton<ISolutionBridge, LocalFileSystemSolutionBridge>();
builder.Services.AddSingleton<ISolutionAnalystAgent, OllamaSolutionAnalystAgent>();

var artifactRoot = builder.Configuration["ArtifactRoot"] ?? "data";
builder.Services.AddSingleton<IArtifactStore>(_ => new FileSystemArtifactStore(
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, artifactRoot))));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();

app.Run();
