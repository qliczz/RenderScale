param([string]$DalamudHome = (Join-Path $env:APPDATA 'XIVLauncherCN\addon\Hooks\dev'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $DalamudHome 'Dalamud.dll'))) { throw 'Dalamud.dll not found.' }
$env:DALAMUD_HOME = (Resolve-Path -LiteralPath $DalamudHome).Path
dotnet run --project (Join-Path $root 'tests\RenderScale.Tests') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
dotnet build (Join-Path $root 'src\RenderScale\RenderScale.csproj') -c Release --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$out = Join-Path $root 'src\RenderScale\bin\Release'
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force $artifacts | Out-Null
$zip = Join-Path $artifacts 'RenderScale.zip'
# Explicit allowlist: do not redistribute Dalamud, game libraries or local settings.
$names = @('RenderScale.dll','RenderScale.json','FloppyUtils.dll','SharpDX.dll','SharpDX.D3DCompiler.dll')
$files = @($names | ForEach-Object {
    $file = Join-Path $out $_
    if (-not (Test-Path -LiteralPath $file)) { throw "Missing package dependency: $_" }
    $file
})
$files += (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD_PARTY_NOTICES.md'), (Join-Path $root 'README.md')
Compress-Archive -LiteralPath $files -DestinationPath $zip -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    if (@($archive.Entries | Where-Object FullName -Match '[/\\]').Count -ne 0) { throw 'Nested ZIP paths.' }
    $entry = $archive.GetEntry('RenderScale.json')
    $reader = [IO.StreamReader]::new($entry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.DalamudApiLevel -ne 15 -or $manifest.InternalName -ne 'RenderScale' -or -not $manifest.CanUnloadAsync) { throw 'Invalid package manifest.' }
} finally { $archive.Dispose() }
Get-FileHash -LiteralPath $zip | Select-Object Hash
Write-Host "Package: $zip"
