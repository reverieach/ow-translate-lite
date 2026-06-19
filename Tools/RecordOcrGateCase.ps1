param(
    [ValidateSet("no-text", "text", "unknown")]
    [string]$Label = "no-text",

    [ValidateRange(1, 180)]
    [int]$DurationSeconds = 60,

    [ValidateRange(250, 5000)]
    [int]$IntervalMs = 1000,

    [ValidateRange(1, 360)]
    [int]$MaxFrames = 360,

    [string]$Region = "",

    [string]$Output = ""
)

$ErrorActionPreference = "Stop"

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDirectory
$dotnet = Join-Path (Split-Path -Parent $repoRoot) ".dotnet\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = "dotnet"
}

if ([string]::IsNullOrWhiteSpace($Output)) {
    $safeLabel = $Label -replace '[^\w.-]', '_'
    $Output = Join-Path $repoRoot ("Docs\ocr-lab-output\gate-recordings\{0}-{1}" -f (Get-Date -Format "yyyyMMdd-HHmmss"), $safeLabel)
}

$arguments = @(
    "run",
    "--project", "Tools\OcrPreprocessLab\OcrPreprocessLab.csproj",
    "-c", "Release",
    "--",
    "--mode", "record",
    "--label", $Label,
    "--duration", $DurationSeconds,
    "--interval", $IntervalMs,
    "--max-frames", $MaxFrames,
    "--output", $Output
)

if (-not [string]::IsNullOrWhiteSpace($Region)) {
    $arguments += @("--region", $Region)
}

Set-Location $repoRoot
& $dotnet $arguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Record output: $Output"
Write-Host "Evaluate with:"
Write-Host "$dotnet run --project Tools\OcrPreprocessLab\OcrPreprocessLab.csproj -c Release -- --mode gate --input `"$Output`""
