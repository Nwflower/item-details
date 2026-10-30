#Requires -Version 7.4
param([string]$GameDir)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if ($GameDir) { $GameDir = (Resolve-Path -LiteralPath $GameDir).Path }
Push-Location -LiteralPath $repo
try {
    & dotnet run --project tools/FormulaTests/FormulaTests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Formula regressions failed.' }
    & python -B -m unittest discover -s tools -p test_check_localization.py
    if ($LASTEXITCODE -ne 0) { throw 'Localization validator regressions failed.' }
    & python -B tools/check_localization.py --strict-placeholders
    if ($LASTEXITCODE -ne 0) { throw 'Localization validation failed.' }
    foreach ($script in @('test_localization_runtime.ps1', 'test_selftest_results.ps1', 'test_selftest_fixtures.ps1')) {
        & (Join-Path $repo "tools/$script")
    }
    Write-Output 'Offline checks passed.'

    if ($GameDir) {
        foreach ($variant in @('Release', 'Diag', 'SelfTest')) {
            & (Join-Path $PSScriptRoot 'build.ps1') -GameDir $GameDir -Variant $variant
        }
        # Fresh processes prevent default-context Assembly.LoadFrom from reusing a prior build.
        foreach ($script in @('test_upstream_contracts.ps1', 'test_build_outputs.ps1')) {
            & pwsh -NoProfile -File (Join-Path $repo "tools/$script") -GameDir $GameDir
            if ($LASTEXITCODE -ne 0) { throw "$script failed." }
        }
        Write-Output 'Game-assembly build and package checks passed.'
    }
}
finally { Pop-Location }
