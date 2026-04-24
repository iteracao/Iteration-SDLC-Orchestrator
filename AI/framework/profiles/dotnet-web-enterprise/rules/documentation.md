# Documentation Rules

## Purpose
- Document the current operational shape of a .NET web enterprise solution clearly enough for later analysis, design, planning, and maintenance workflows.

## Expected Solution Areas
- Describe the ASP.NET Core host, API surface, and request pipeline when evident.
- Describe Blazor or other web UI shells, navigation boundaries, and user-facing application areas when evident.
- Describe authentication, session handling, identity boundaries, and RBAC behavior when evidence exists.
- Describe EF Core persistence, database contexts, repositories, migrations, and data ownership boundaries when present.
- Describe security, resilience, availability, and operational controls that are visible in code or deployment assets.
- Describe deployment, environment, CI/CD, and hosting behavior when repository evidence shows them.
- Describe testing layers, test project boundaries, and notable coverage shape when present.
- Describe project and module boundaries so the architecture map reflects the real solution structure.
- Describe configuration sources, environment-specific behavior, and secrets handling conventions that are observable.
- Describe UI or application shell conventions when they are evident from the host, layout, or component structure.

## Evidence Expectations
- Prefer actual startup, host, composition-root, project-file, and configuration evidence over naming assumptions.
- If a common enterprise concern is not evident, mark it as unknown instead of assuming the default .NET pattern.
- Distinguish framework capability from solution-specific implementation.

## Style
- Keep the documentation implementation-aware but not code-dump-heavy.
- Focus on stable behavior, boundaries, and responsibilities.
- Record inferred enterprise concerns only when repository evidence supports them directly.
