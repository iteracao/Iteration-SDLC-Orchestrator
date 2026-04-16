using Iteration.Orchestrator.Cockpit.Services;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor(options =>
{
    options.DetailedErrors = true;
});

builder.Services.AddMudServices(config =>
{
    config.PopoverOptions.ThrowOnDuplicateProvider = false;
});

builder.Services.AddScoped<SelectedSolutionState>();

var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:54832/";
if (!apiBaseUrl.EndsWith("/", StringComparison.Ordinal))
{
    apiBaseUrl += "/";
}

builder.Services.AddHttpClient("api", client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
});

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();