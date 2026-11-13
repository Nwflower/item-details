#Requires -Version 7.4
param([string]$GameDir)
$ErrorActionPreference = 'Stop'
# GitHub Windows runners default to cp1252; keep Chinese validator output readable and non-throwing.
$env:PYTHONUTF8 = '1'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if ($GameDir) { $GameDir = (Resolve-Path -LiteralPath $GameDir).Path }
Push-Location -LiteralPath $repo
try {
    & dotnet run --project tests/FormulaTests/FormulaTests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Formula regressions failed.' }
    # tooltip 热路径的极限规模回归：1024 容器 × 2500 件，纯逻辑不启动游戏。
    & dotnet run --project tests/TooltipStress/TooltipStress.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tooltip stress regressions failed.' }
    & python -B -m unittest discover -s tests -p test_check_localization.py
    if ($LASTEXITCODE -ne 0) { throw 'Localization validator regressions failed.' }
    # TooltipPreview 站点构建器：小样例、分块、对比、差异、字体与输出目录保护。
    & python -B -m unittest discover -s tests -p test_tooltip_preview.py
    if ($LASTEXITCODE -ne 0) { throw 'Tooltip preview renderer regressions failed.' }
    & python -B tools/check_localization.py --strict-placeholders
    if ($LASTEXITCODE -ne 0) { throw 'Localization validation failed.' }
    foreach ($script in @('test_localization_runtime.ps1', 'test_selftest_results.ps1', 'test_selftest_fixtures.ps1', 'test_info_settings.ps1')) {
        & (Join-Path $repo "tests/$script")
    }
    Write-Output 'Offline checks passed.'

    if ($GameDir) {
        foreach ($variant in @('Release', 'Diag', 'SelfTest')) {
            & (Join-Path $PSScriptRoot 'build.ps1') -GameDir $GameDir -Variant $variant
        }
        # C# 5 客户端必须能在旧语言版本下编译，第三方才敢原样复制。
        & dotnet build (Join-Path $repo 'tests/CSharp5Client/CSharp5Client.csproj') -c Release -p:GameDir=$GameDir
        if ($LASTEXITCODE -ne 0) { throw 'C# 5 client does not compile with LangVersion 5.' }
        # Fresh processes prevent default-context Assembly.LoadFrom from reusing a prior build.
        foreach ($script in @('test_upstream_contracts.ps1', 'test_liquid_client.ps1', 'test_legacy_compat.ps1', 'test_section_replay.ps1', 'test_tooltip_snapshot.ps1', 'test_build_outputs.ps1')) {
            & pwsh -NoProfile -File (Join-Path $repo "tests/$script") -GameDir $GameDir
            if ($LASTEXITCODE -ne 0) { throw "$script failed." }
        }
        # 发布包要同时过两代模组扫描器：v3 用回放夹具，v4 用游戏自带的扫描器。
        [xml]$manifest = Get-Content -LiteralPath (Join-Path $repo 'manifest.xml') -Raw
        $package = Join-Path $repo ("dist/{0}-{1}.zip" -f $manifest.Manifest.ID, $manifest.Manifest.ModVersion)
        & pwsh -NoProfile -File (Join-Path $repo 'tests/test_mod_scan.ps1') -GameDir $GameDir -Package $package
        if ($LASTEXITCODE -ne 0) { throw 'test_mod_scan.ps1 failed.' }
        Write-Output 'Game-assembly build and package checks passed.'
    }
}
finally { Pop-Location }
