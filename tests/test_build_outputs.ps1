#Requires -Version 7.4
param([Parameter(Mandatory)][string]$GameDir)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$managed = Join-Path $GameDir 'Probably Stolen_Data/Managed'
foreach ($name in @('DseCecil.dll', '0Harmony.dll', 'UnityEngine.CoreModule.dll', 'Assembly-CSharp.dll')) {
    [System.Reflection.Assembly]::LoadFrom((Join-Path $managed $name)) | Out-Null
}

foreach ($variant in @('Release', 'Diag', 'SelfTest')) {
    $dll = Join-Path $repo "bin/$variant/netstandard2.1/ItemDetails.dll"
    $assembly = [DseCecil.AssemblyDefinition]::ReadAssembly($dll)
    try {
        $hasSelfTest = @($assembly.MainModule.Types | Where-Object FullName -eq 'ItemDetails.SelfTest').Count -gt 0
        if ($hasSelfTest -ne ($variant -eq 'SelfTest')) { throw "Unexpected self-test code in $variant." }
    }
    finally { $assembly.Dispose() }
}

[xml]$manifest = Get-Content -LiteralPath (Join-Path $repo 'manifest.xml') -Raw
$id = [string]$manifest.Manifest.ID
$version = [string]$manifest.Manifest.ModVersion
$zip = Join-Path $repo "dist/$id-$version.zip"
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $names = @($archive.Entries | ForEach-Object FullName)
    foreach ($required in @('ItemDetails.dll', 'manifest.xml', 'README.md', 'CONTRIBUTING.md', 'LICENSE')) {
        if ($names -notcontains "$id/$required") { throw "Missing release entry: $required" }
    }
    $dlls = @($names | Where-Object { $_ -match '\.dll$' })
    if ($dlls.Count -ne 1 -or $dlls[0] -ne "$id/ItemDetails.dll") { throw 'Unexpected bundled assemblies.' }
    if (@($names | Where-Object { $_ -match '\.pdb$|\.deps\.json$|(^|/)\.gitkeep$' }).Count) {
        throw 'Development files were included in the package.'
    }
    $expectedTranslations = @(Get-ChildItem -LiteralPath (Join-Path $repo 'Localization') -Filter '*.csv' |
        ForEach-Object { "$id/Localization/$($_.Name)" })
    $actualTranslations = @($names | Where-Object { $_ -like "$id/Localization/*.csv" })
    if (Compare-Object $expectedTranslations $actualTranslations) { throw 'Release translations differ from source.' }
    $entry = $archive.GetEntry("$id/ItemDetails.dll").Open()
    try { $packagedHash = [System.Security.Cryptography.SHA256]::HashData($entry) }
    finally { $entry.Dispose() }
    $builtHash = (Get-FileHash -LiteralPath (Join-Path $repo 'bin/Release/netstandard2.1/ItemDetails.dll')).Hash
    if ([Convert]::ToHexString($packagedHash) -ne $builtHash) { throw 'Package DLL does not match the Release build.' }
}
finally { $archive.Dispose() }

[string[]]$allowed = @(Get-ChildItem -LiteralPath $managed -Filter '*.dll' | ForEach-Object BaseName)
$releaseDll = Join-Path $repo 'bin/Release/netstandard2.1/ItemDetails.dll'
$scan = [ModScanner]::Scan([string[]]@($releaseDll), $allowed)
if ($scan.Refused -or $scan.Error -or $scan.Capabilities.Count -gt 0) {
    $scan | Format-List *
    throw 'The Release mod failed the expected capability-free scan.'
}
Write-Output 'PASS build variants, release layout, translations, DLL identity and native capability scan.'
