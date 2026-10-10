param([ValidateSet('win-x64', 'linux-x64', 'all')][string]$Runtime = 'all')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$targets = if ($Runtime -eq 'all') { @('win-x64', 'linux-x64') } else { @($Runtime) }
foreach ($rid in $targets) {
    $destination = Join-Path $projectRoot "dist/$rid"
    & dotnet publish (Join-Path $projectRoot 'IsoWriter.csproj') -c Release -r $rid --self-contained true -p:PublishSingleFile=false -o $destination
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $rid" }
    Write-Host "Ready: $destination"
}
