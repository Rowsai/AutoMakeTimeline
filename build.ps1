param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'))
$ErrorActionPreference = 'Stop'
dotnet build (Join-Path $PSScriptRoot 'AutoMakeTimeline/AutoMakeTimeline.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
dotnet run --project (Join-Path $PSScriptRoot 'Tests/Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Validation failed.' }
$amtOutput = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($amtOutput) | Out-Null
$amtStage = Join-Path $PSScriptRoot ('work/package-' + [guid]::NewGuid().ToString('N'))
$amtBinary = Join-Path $amtStage 'release'
$amtSource = Join-Path $amtStage 'source/AutoMakeTimeline'
[IO.Directory]::CreateDirectory((Join-Path $amtBinary 'images')) | Out-Null
[IO.Directory]::CreateDirectory($amtSource) | Out-Null
$amtRelease = Join-Path $PSScriptRoot 'AutoMakeTimeline/bin/Release'
foreach ($amtName in @('AutoMakeTimeline.dll','AutoMakeTimeline.json','AutoMakeTimeline.deps.json')) {
    Copy-Item -LiteralPath (Join-Path $amtRelease $amtName) -Destination $amtBinary
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'assets/icon.png') -Destination (Join-Path $amtBinary 'images/icon.png')
foreach ($amtName in @('README.ja.md','LICENSE','BUILD-INFO.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $amtName) -Destination $amtBinary
}
foreach ($amtName in @('README.ja.md','LICENSE','BUILD-INFO.md','build.ps1','.gitignore')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $amtName) -Destination $amtSource
}
foreach ($amtDir in @('AutoMakeTimeline','Tests','assets')) {
    $amtDest = Join-Path $amtSource $amtDir
    [IO.Directory]::CreateDirectory($amtDest) | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $amtDir) -File | Copy-Item -Destination $amtDest
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($amtPair in @(@($amtBinary,'latest.zip'),@((Join-Path $amtStage 'source'),'source.zip'))) {
    $amtZip = Join-Path $amtOutput $amtPair[1]
    $amtTempZip = Join-Path $amtStage $amtPair[1]
    [IO.Compression.ZipFile]::CreateFromDirectory($amtPair[0],$amtTempZip,[IO.Compression.CompressionLevel]::Optimal,$false)
    Move-Item -LiteralPath $amtTempZip -Destination $amtZip -Force
}
Get-FileHash -LiteralPath (Join-Path $amtOutput 'latest.zip'),(Join-Path $amtOutput 'source.zip') -Algorithm SHA256
