#Requires -Version 7.4
param(
    [string]$GameDir = $env:PS_GAME_DIR,
    [ValidateSet('Release', 'Diag', 'SelfTest')]
    [string]$Variant = 'Release'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if (-not $GameDir) { throw 'Pass -GameDir or set PS_GAME_DIR to the installed game directory.' }
$game = (Resolve-Path -LiteralPath $GameDir).Path
$output = Join-Path $repo "bin/$Variant/netstandard2.1"
$intermediate = Join-Path $repo "obj/$Variant/"
$diag = ($Variant -ne 'Release').ToString().ToLowerInvariant()
$selfTest = ($Variant -eq 'SelfTest').ToString().ToLowerInvariant()

& dotnet build (Join-Path $repo 'ItemDetails.csproj') -c Release "-p:GameDir=$game" `
    "-p:Diag=$diag" "-p:SelfTest=$selfTest" "-p:OutputPath=$output/" `
    "-p:BaseIntermediateOutputPath=$intermediate"
if ($LASTEXITCODE -ne 0) { throw "The $Variant build failed with exit code $LASTEXITCODE." }
$dll = Join-Path $output 'ItemDetails.dll'
Write-Output "DLL: $dll"
if ($Variant -ne 'Release') { return }

# Package source resources so files removed from the repository cannot survive from an old bin folder.
[xml]$manifest = Get-Content -LiteralPath (Join-Path $repo 'manifest.xml') -Raw
$id = [string]$manifest.Manifest.ID
$version = [string]$manifest.Manifest.ModVersion
if ($id -notmatch '^[A-Za-z0-9_-][A-Za-z0-9_.-]{0,63}$') { throw 'Invalid manifest mod ID.' }
if (-not $version -or $version.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
    throw 'ModVersion must be a nonempty filename-safe value.'
}
$files = [ordered]@{ 'ItemDetails.dll' = $dll }
foreach ($name in @('manifest.xml', 'README.md', 'CONTRIBUTING.md', 'LICENSE', 'preview.png')) {
    $files[$name] = Join-Path $repo $name
}
foreach ($folder in @('Localization', 'Textures', 'Sounds', 'LootTables', 'Books')) {
    $source = Join-Path $repo $folder
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
        if ($file.Name -eq '.gitkeep') { continue }
        $relative = [System.IO.Path]::GetRelativePath($repo, $file.FullName).Replace('\', '/')
        $files[$relative] = $file.FullName
    }
}
$dist = Join-Path $repo 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$zip = Join-Path $dist "$id-$version.zip"
$stream = [System.IO.File]::Open($zip, [System.IO.FileMode]::Create)
try {
    $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($entry in $files.GetEnumerator()) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $entry.Value, "$id/$($entry.Key)",
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $archive.Dispose() }
}
finally { $stream.Dispose() }
Write-Output "Package: $zip"
