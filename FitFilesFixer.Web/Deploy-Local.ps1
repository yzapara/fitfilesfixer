# Deploy-Local.ps1
# Local development + deployment helper for FitFilesFixer.Web

param(
    [switch]$Build,
    [switch]$Run,
    [switch]$Test,
    [switch]$Push,
    [string]$Branch = "Fix-error-handling"
)

$projectDir = $PSScriptRoot
$solution = Join-Path $projectDir "FitFilesFixer.Web.sln"

if ($Build) {
    Write-Host "[Local] Building solution..."
    dotnet build $solution
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}

if ($Test) {
    Write-Host "[Local] Running tests (none configured, placeholder) ..."
    # dotnet test $solution
}

if ($Run) {
    Write-Host "[Local] Starting app... Ctrl+C to stop"
    dotnet run --project (Join-Path $projectDir "FitFilesFixer.csproj")
}

if ($Push) {
    Write-Host "[Local] Committing and pushing local changes to branch $Branch"

    git -C $projectDir init 2>$null
    git -C $projectDir add .
    git -C $projectDir commit -m "Local update: improved error handling + streaming file downloads" --allow-empty
    git -C $projectDir push --set-upstream origin $Branch
}

Write-Host "Done. Use -Build, -Run, -Test, -Push as needed."