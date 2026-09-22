# Build environment notes

The .NET 10 SDK (10.0.401) is installed machine-wide at `C:\Program Files\dotnet` and is on PATH, so
everything works in any shell with no setup:

    dotnet build Arkimentum.AppMonitor.slnx
    dotnet test src\Arkimentum.AppMonitor.Tests

Until 2026-09-22 the only SDK was a user-local copy at `%LOCALAPPDATA%\Microsoft\dotnet`, and every
shell had to put it on `PATH`/`DOTNET_ROOT` first. That is no longer needed. The machine-wide install
also fixes the C# extension in VS Code, which asks the `dotnet` on `PATH` which SDKs exist and used to
get an empty answer from the runtime-only install in `C:\Program Files\dotnet`.

The user-local copy is still on disk and unused; it can be deleted. Do not put it back on `PATH`
ahead of the machine-wide one: it carries only the .NET 10 runtimes, so anything needing 8.0.x would
stop resolving.

Set `DOTNET_CLI_TELEMETRY_OPTOUT=1` and `DOTNET_NOLOGO=1` to keep output short.
