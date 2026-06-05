[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "cx\bin"),
    [string]$Configuration = "Release",
    [switch]$SkipPath
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "src\Cx.Cli\Cx.Cli.csproj"

if (-not (Test-Path $projectPath)) {
    throw "Could not find CLI project at '$projectPath'."
}

$installPath = [System.IO.Path]::GetFullPath($InstallDir)
New-Item -ItemType Directory -Force -Path $installPath | Out-Null

Write-Host "Publishing CX CLI to $installPath"
dotnet publish $projectPath `
    --configuration $Configuration `
    --output $installPath `
    --self-contained false `
    --nologo

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$cmdPath = Join-Path $installPath "cx.cmd"
$exePath = Join-Path $installPath "Cx.Cli.exe"
$dllPath = Join-Path $installPath "Cx.Cli.dll"

if (Test-Path $exePath) {
    $cmdBody = @"
@echo off
"$exePath" %*
"@
}
else {
    $cmdBody = @"
@echo off
dotnet "$dllPath" %*
"@
}

Set-Content -Path $cmdPath -Value $cmdBody -Encoding ASCII
Write-Host "Installed command wrapper $cmdPath"

if (-not $SkipPath) {
    $userPath = [Environment]::GetEnvironmentVariable("PATH", "User")
    $pathEntries = @()
    if (-not [string]::IsNullOrWhiteSpace($userPath)) {
        $pathEntries = $userPath.Split([System.IO.Path]::PathSeparator) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    }

    $alreadyInPath = $false
    foreach ($entry in $pathEntries) {
        try {
            if ([System.IO.Path]::GetFullPath($entry).TrimEnd('\') -ieq $installPath.TrimEnd('\')) {
                $alreadyInPath = $true
                break
            }
        }
        catch {
            if ($entry.TrimEnd('\') -ieq $installPath.TrimEnd('\')) {
                $alreadyInPath = $true
                break
            }
        }
    }

    if (-not $alreadyInPath) {
        $newUserPath = if ([string]::IsNullOrWhiteSpace($userPath)) {
            $installPath
        }
        else {
            $userPath.TrimEnd([System.IO.Path]::PathSeparator) + [System.IO.Path]::PathSeparator + $installPath
        }

        [Environment]::SetEnvironmentVariable("PATH", $newUserPath, "User")
        Write-Host "Added $installPath to the User PATH."
        Write-Host "Open a new terminal to use cx from PATH."
    }
    else {
        Write-Host "$installPath is already in the User PATH."
    }

    if (-not (($env:PATH.Split([System.IO.Path]::PathSeparator)) -contains $installPath)) {
        $env:PATH = $env:PATH + [System.IO.Path]::PathSeparator + $installPath
    }
}

Write-Host ""
Write-Host "Installed. Try:"
Write-Host "  cx --help"
Write-Host "  cx run"
