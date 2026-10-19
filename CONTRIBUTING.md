# Contributing

## Development setup

- Install the .NET 10 SDK.
- Clone the repository and create a short-lived branch from `main`, for example `fix/event-cursor-commit` or `docs/contribution-guide`.
- Keep changes focused and avoid committing credentials, local databases, generated build output, or installer packages.

## Validate changes

Run these commands from the repository root:

```powershell
dotnet restore .\SqlDam.slnx
dotnet build .\SqlDam.slnx --configuration Release
dotnet test .\SqlDam.slnx --configuration Release
```

The solution currently has no test projects, so `dotnet test` restores and builds the applications but does not execute behavioral tests. Add focused tests when introducing testable behavior.

The Windows MSI requires Windows and the WiX Toolset extensions described in [installer/README.md](installer/README.md). The Linux package can be built with the .NET 10 SDK and PowerShell using the instructions in [installer-linux/README.md](installer-linux/README.md).

## Commits and pull requests

- Use imperative, specific commit subjects. A type prefix such as `fix:`, `feat:`, `docs:`, or `build:` is encouraged.
- Update `CHANGELOG.md` under `Unreleased` for user-visible behavior, security, or operational changes. Do not add entries for routine formatting or internal-only refactors.
- Open a pull request against `main`. Explain the user impact, list validation performed, link related issues, and include screenshots for user-visible UI changes.
- Call out configuration, database schema, deployment, or compatibility changes explicitly. Never include secrets or customer data in code, logs, screenshots, or test fixtures.