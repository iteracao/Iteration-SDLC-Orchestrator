# Iteration SDLC Orchestrator - V1 Starter

This package is a real V1 starter for a solution-centric SDLC orchestration platform.

## V1 scope
- Register a target solution
- Create backlog items
- Run an `analyze-solution` workflow
- Produce and persist a structured analysis report
- View basic data through API and simple cockpit pages

## Current implementation state
- Domain, application, infrastructure, solution bridge, API, cockpit and config starter files are included
- The analyst agent is a safe stub returning deterministic structured output
- Config loading works from the local `config/` folder
- Solution bridge is read-only
- SQLite is used for persistence

## Next steps
1. Create the solution and add the projects:
   - `dotnet sln add src/**/*.csproj`
2. Restore packages:
   - `dotnet restore`
3. Create the first EF migration:
   - `dotnet ef migrations add InitialCreate --project src/Iteration.Orchestrator.Infrastructure --startup-project src/Iteration.Orchestrator.Api`
4. Run the API:
   - `dotnet run --project src/Iteration.Orchestrator.Api`
5. Run the cockpit:
   - `dotnet run --project src/Iteration.Orchestrator.Cockpit`

## Notes
- The included `.sln` is a placeholder to avoid excessive generated noise in the ZIP.
- The YAML loader is intentionally lightweight for V1.
- Replace the stub analyst agent with Microsoft Agent Framework + Ollama in V1.5/V2.
