param(
    [ValidateSet("Local", "Global")]
    [string]$Scope = "Local",
    [string]$Version = "0.1.0-local",
    [switch]$CliOnly,
    [switch]$DashboardOnly
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$packageOutputPath = Join-Path $repoRoot ".artifacts/tool"
$manifestPath = Join-Path $repoRoot "dotnet-tools.json"

$tools = @(
    @{ ProjectPath = "src/Ado.AutonomousWorker.Cli/Ado.AutonomousWorker.Cli.csproj";           PackageId = "ado-worker";           CommandName = "ado-worker"           }
    @{ ProjectPath = "src/Ado.AutonomousWorker.Dashboard/Ado.AutonomousWorker.Dashboard.csproj"; PackageId = "ado-worker-dashboard"; CommandName = "ado-worker-dashboard" }
)

if ($CliOnly)       { $tools = $tools | Where-Object { $_.PackageId -eq "ado-worker" } }
if ($DashboardOnly) { $tools = $tools | Where-Object { $_.PackageId -eq "ado-worker-dashboard" } }

Push-Location $repoRoot
try
{
    Write-Host "Repository root: $repoRoot"
    New-Item -ItemType Directory -Force -Path $packageOutputPath | Out-Null

    if ($Scope -eq "Local" -and -not (Test-Path $manifestPath))
    {
        Write-Host "Creating tool manifest..."
        dotnet new tool-manifest --force | Out-Null
    }

    foreach ($tool in $tools)
    {
        $projectPath = Join-Path $repoRoot $tool.ProjectPath
        $packageId   = $tool.PackageId
        $commandName = $tool.CommandName

        Write-Host ""
        Write-Host "Packing $packageId..."
        Get-ChildItem -Path $packageOutputPath -Filter "$packageId.*.nupkg" -File -ErrorAction SilentlyContinue | Remove-Item -Force
        dotnet pack $projectPath -c Release -o $packageOutputPath /p:PackageVersion=$Version /p:Version=$Version

        $scopeFlag = if ($Scope -eq "Local") { "--local" } else { "--global" }

        $toolListOutput = dotnet tool list $scopeFlag | Out-String
        if ($toolListOutput -match "(?m)^\s*$packageId\s")
        {
            Get-Process -Name $commandName -ErrorAction SilentlyContinue | ForEach-Object {
                Write-Host "Stopping running process: $commandName (PID $($_.Id))..."
                $_ | Stop-Process -Force
            }

            Write-Host "Removing existing $Scope $packageId installation..."
            $uninstalled = $false
            for ($attempt = 1; $attempt -le 5; $attempt++)
            {
                dotnet tool uninstall $scopeFlag $packageId 2>&1 | Out-Null
                if ($LASTEXITCODE -eq 0) { $uninstalled = $true; break }
                Write-Host "  Uninstall attempt $attempt failed (files still locked), retrying in 2s..."
                Start-Sleep -Seconds 2
            }
            if (-not $uninstalled) { throw "Uninstall failed for $packageId after 5 attempts. Reopen a fresh terminal and retry." }
        }

        Write-Host "Installing $packageId ($Scope)..."
        dotnet tool install $scopeFlag $packageId --version $Version --add-source $packageOutputPath
        if ($LASTEXITCODE -ne 0) { throw "Install failed for $packageId (exit $LASTEXITCODE)." }
    }

    Write-Host ""
    Write-Host "Done. Installed tools:"

    foreach ($tool in $tools)
    {
        $commandName = $tool.CommandName
        if ($Scope -eq "Local")
        {
            Write-Host "  dotnet tool run $commandName"
        }
        else
        {
            Write-Host "  $commandName"
        }
    }

    if ($tools | Where-Object { $_.PackageId -eq "ado-worker" })
    {
        Write-Host ""
        if ($Scope -eq "Local")
        {
            Write-Host "To use the shorter command name in PowerShell for the current session:"
            Write-Host "  function ado-worker { dotnet tool run ado-worker -- @args }"
            Write-Host ""
            Write-Host "Enable completion in the current session:"
            Write-Host "  Invoke-Expression (& { dotnet tool run ado-worker -- completion powershell })"
        }
        else
        {
            Write-Host "Enable completion in the current session:"
            Write-Host "  Invoke-Expression (& ado-worker completion powershell)"
            Write-Host ""
            Write-Host "If commands are not found, ensure this path is on PATH:"
            Write-Host "  $HOME\\.dotnet\\tools"
        }
    }
}
finally
{
    Pop-Location
}
