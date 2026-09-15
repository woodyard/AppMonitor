# Build environment notes

The machine has no system-wide .NET SDK. A user-local .NET 10 SDK (10.0.401) is installed at
`%LOCALAPPDATA%\Microsoft\dotnet`. Use it like this:

PowerShell:
    $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"; $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
    dotnet build Arkimentum.AppMonitor.sln

Bash:
    export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet"; export PATH="$DOTNET_ROOT:$PATH"

Set DOTNET_CLI_TELEMETRY_OPTOUT=1 and DOTNET_NOLOGO=1 to keep output short.
