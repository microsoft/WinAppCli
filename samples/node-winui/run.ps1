$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$bindings = Join-Path $PSScriptRoot '.winapp\bindings\index.js'
if (-not (Test-Path -LiteralPath $bindings)) {
    throw 'Bindings are missing. Run npm run restore first.'
}

$nodeSource = (Get-Command node -ErrorAction Stop).Source
$localNodeDirectory = Join-Path $PSScriptRoot '.local-node'
$localNode = Join-Path $localNodeDirectory 'node.exe'
$null = New-Item -ItemType Directory -Path $localNodeDirectory -Force

Copy-Item -LiteralPath $nodeSource -Destination $localNode -Force

& npx --no-install winapp run . `
    --executable '.local-node\node.exe' `
    --with-alias `
    --args 'main.js' `
    --unregister-on-exit

if ($LASTEXITCODE -ne 0) {
    throw "winapp run failed with exit code $LASTEXITCODE."
}
