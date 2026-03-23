# Validation Notes (2026-03)

This note captures the CLI behavior changes that were validated after the
March 2026 fixes.

## Workspace Resolution

- `repos add`, `repos list`, `index`, `query`, and `serve` always resolve the
  CodeBrain workspace by locating `CodeBrain.sln`.
- The repository catalog and persisted index databases therefore stay under the
  same `agent_artifacts/index` directory even when a command is launched from a
  different shell working directory.
- `init` and `run` resolve the target repository workspace from the explicit
  `--sln` or `--project` path.
- Generated `codebrain.config.json`, `agent_artifacts`, and test project
  discovery are now tied to the target repository instead of the current shell
  directory.

## Incremental Indexing

- Incremental indexing now uses one shared file scanner for both change
  planning and manifest persistence.
- The scanner tracks `.cs`, `.csproj`, `.sln`, `.json`, and `.md` files while
  skipping `bin`, `obj`, `.git`, and `.vs`.
- The first rebuild after upgrading the scanner can report one-time additions
  because older manifests did not include every tracked file type.
- From the second rebuild onward, an unchanged repository should report
  `added=0`, `modified=0`, and `removed=0`.

## Drilldown Symbol Resolution

- `drill` accepts the exact symbol returned by `query --intent symbol`.
- `drill` now also tolerates common formatting differences, including:
  - `global::` prefix differences
  - namespace separator differences such as `OpenAi.ManagedApis` vs
    `OpenAiManagedApis`
  - parameter namespace formatting differences such as `Domain.Models` vs
    `DomainModels`

## CLI Validation Tips

- Build once with `dotnet build CodeBrain.sln`.
- Prefer `dotnet run --no-build --project ...` for repeat validation to skip
  the normal restore/build check that `dotnet run` performs before every launch.
- A minimal validation pass for a registered repository is:

```bash
dotnet run --no-build --project .\src\CodeBrain.Cli -- index --repo <repo-id>
dotnet run --no-build --project .\src\CodeBrain.Cli -- index --repo <repo-id>
dotnet run --no-build --project .\src\CodeBrain.Cli -- query --repo <repo-id> --q CalculateAccountingFromTokens --intent symbol
dotnet run --no-build --project .\src\CodeBrain.Cli -- drill --sln <path-to-sln> --symbol "<symbol-from-query>"
```
