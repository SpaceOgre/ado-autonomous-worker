param(
    [ValidateSet("Local", "Global")]
    [string]$Scope = "Local",
    [string]$Version = "0.1.0-local"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectPath = Join-Path $repoRoot "src/Ado.AutonomousWorker.Cli/Ado.AutonomousWorker.Cli.csproj"
$packageOutputPath = Join-Path $repoRoot ".artifacts/tool"
$manifestPath = Join-Path $repoRoot "dotnet-tools.json"
$packageId = "ado-worker"
$commandName = "ado-worker"

Push-Location $repoRoot
try
{
Write-Host "Repository root: $repoRoot"
Write-Host "Packing tool package..."

New-Item -ItemType Directory -Force -Path $packageOutputPath | Out-Null
Get-ChildItem -Path $packageOutputPath -Filter "*.nupkg" -File -ErrorAction SilentlyContinue | Remove-Item -Force

dotnet pack $projectPath -c Release -o $packageOutputPath /p:PackageVersion=$Version

if ($Scope -eq "Local" -and -not (Test-Path $manifestPath))
{
    Write-Host "Creating tool manifest..."
    dotnet new tool-manifest --force | Out-Null
}

if ($Scope -eq "Local")
{
    $toolListOutput = dotnet tool list --local | Out-String
    if ($toolListOutput -match "(?m)^\s*$packageId\s")
    {
        Write-Host "Removing existing local tool installation..."
        dotnet tool uninstall --local $packageId | Out-Null
    }

    Write-Host "Installing local tool..."
    dotnet tool install --local $packageId --version $Version --add-source $packageOutputPath | Out-Null

    Write-Host ""
    Write-Host "Local tool installed."
    Write-Host "Run it with:"
    Write-Host "  dotnet tool run $commandName -- queue"
    Write-Host ""
    Write-Host "To use the shorter command name in PowerShell for the current session:"
    Write-Host "  function $commandName { dotnet tool run $commandName -- @args }"
    Write-Host ""
    Write-Host "Then enable completion in the current session:"
    Write-Host "  Invoke-Expression (& { dotnet tool run $commandName -- completion powershell })"
}
else
{
    $toolListOutput = dotnet tool list --global | Out-String
    if ($toolListOutput -match "(?m)^\s*$packageId\s")
    {
        Write-Host "Removing existing global tool installation..."
        dotnet tool uninstall --global $packageId | Out-Null
    }

    Write-Host "Installing global tool..."
    dotnet tool install --global $packageId --version $Version --add-source $packageOutputPath | Out-Null

    Write-Host ""
    Write-Host "Global tool installed."
    Write-Host "Run it with:"
    Write-Host "  $commandName queue"
    Write-Host ""
    Write-Host "Enable completion in the current session:"
    Write-Host "  Invoke-Expression (& $commandName completion powershell)"
    Write-Host ""
    Write-Host "If the command is not found, ensure this path is on PATH:"
    Write-Host "  $HOME\\.dotnet\\tools"
}
}
finally
{
    Pop-Location
}
