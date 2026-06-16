param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDirectory
Set-Location $repoRoot

[xml]$project = Get-Content -LiteralPath "OwTranslateLite.csproj" -Encoding UTF8
$version = $project.Project.PropertyGroup.Version | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Unable to read Version from OwTranslateLite.csproj."
}

$dotnet = Join-Path (Split-Path -Parent $repoRoot) ".dotnet\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = "dotnet"
}

$distDirectory = Join-Path $repoRoot "dist"
$packageRoot = Join-Path $distDirectory "OWTranslatorLite"
$zipPath = Join-Path $distDirectory "OWTranslatorLite-v$version-portable-win-x64.zip"
$sha256Path = "$zipPath.sha256.txt"

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null

& $dotnet publish "OwTranslateLite.csproj" -c $Configuration -o (Join-Path $packageRoot "app")
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

$cscCandidates = @(
    (Join-Path -Path $env:WINDIR -ChildPath "Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
    (Join-Path -Path $env:WINDIR -ChildPath "Microsoft.NET\Framework\v4.0.30319\csc.exe")
)
$csc = $cscCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($csc)) {
    throw "Unable to find .NET Framework csc.exe for the outer launcher."
}

$launcherPath = Join-Path $packageRoot "OWTranslatorLite.exe"
$iconPath = Join-Path $repoRoot "Resources\UI\ow-translator-lite-icon.ico"
$launcherSource = Join-Path $repoRoot "Launcher\Program.cs"
$launcherArgs = @(
    "/nologo",
    "/target:winexe",
    "/platform:x64",
    "/optimize+",
    "/win32icon:$iconPath",
    "/reference:System.Windows.Forms.dll",
    "/out:$launcherPath",
    $launcherSource
)
& $csc $launcherArgs
if ($LASTEXITCODE -ne 0) {
    throw "Launcher build failed."
}

$updaterPath = Join-Path $packageRoot "OWTranslatorLiteUpdater.exe"
$updaterSource = Join-Path $repoRoot "Updater\Program.cs"
$updaterArgs = @(
    "/nologo",
    "/target:winexe",
    "/platform:x64",
    "/optimize+",
    "/win32icon:$iconPath",
    "/reference:System.Windows.Forms.dll",
    "/reference:System.Drawing.dll",
    "/reference:System.IO.Compression.dll",
    "/reference:System.IO.Compression.FileSystem.dll",
    "/out:$updaterPath",
    $updaterSource
)
& $csc $updaterArgs
if ($LASTEXITCODE -ne 0) {
    throw "Updater build failed."
}

$readmeSource = Join-Path $repoRoot "Docs\BetaTest-v$version.md"
if (-not (Test-Path -LiteralPath $readmeSource)) {
    $readmeSource = Join-Path $repoRoot "README.md"
}
Copy-Item -LiteralPath $readmeSource -Destination (Join-Path $packageRoot "README-BETA.md") -Force

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
if (Test-Path -LiteralPath $sha256Path) {
    Remove-Item -LiteralPath $sha256Path -Force
}
Compress-Archive -Path $packageRoot -DestinationPath $zipPath -Force

$hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
$shaLine = "$($hash.Hash.ToLowerInvariant())  $(Split-Path -Leaf $zipPath)"
[System.IO.File]::WriteAllText($sha256Path, $shaLine + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

Write-Host "Package folder: $packageRoot"
Write-Host "Package zip:    $zipPath"
Write-Host "SHA256 file:    $sha256Path"
