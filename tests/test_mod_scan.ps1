#Requires -Version 7.4
<#
.SYNOPSIS
  发布包必须同时通过两代模组扫描器。
.DESCRIPTION
  v3（100-Rev12 及更早）用 tests/ScannerV3Replay 的回放件，v4 用游戏目录自带的扫描器。
  两版都要求：扫到方法、Refused 为假、Capabilities 与 HardFlags 为空；v4 另要求
  Pending 与 DeprecatedCapabilities 为空。扫描对象是发布包里 manifest 声明的那份 DLL。
#>
param(
    [Parameter(Mandatory)][string]$GameDir,
    [Parameter(Mandatory)][string]$Package
)
$ErrorActionPreference = 'Stop'
$managed = Join-Path (Resolve-Path -LiteralPath $GameDir).Path 'Probably Stolen_Data/Managed'
$replayDir = Join-Path $PSScriptRoot 'ScannerV3Replay'
$sources = Join-Path $replayDir 'sources'

$requiredSources = @('ModScanner', 'ModScanResult', 'ModScanFinding', 'ModSteamRules', 'ModSteamUse')
$missingSources = @($requiredSources | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $sources "$_.cs") -PathType Leaf)
})
if ($missingSources.Count -gt 0) {
    throw ("缺少 v3 扫描器源码：{0}。来源与重建方式见 tests/ScannerV3Replay/README.md。" -f
        ($missingSources -join ', '))
}

foreach ($name in @('DseCecil.dll', '0Harmony.dll', 'UnityEngine.CoreModule.dll', 'Assembly-CSharp.dll')) {
    [System.Reflection.Assembly]::LoadFrom((Join-Path $managed $name)) | Out-Null
}

& dotnet build (Join-Path $replayDir 'ScannerV3Replay.csproj') -c Release --nologo "-p:ManagedDir=$managed" |
    Out-Null
if ($LASTEXITCODE -ne 0) { throw 'v3 扫描器回放件编译失败。' }
$replayPath = Join-Path $replayDir 'bin/Release/netstandard2.1/ScannerV3Replay.dll'
if (-not (Test-Path -LiteralPath $replayPath -PathType Leaf)) { throw "找不到回放件：$replayPath" }

$private = [System.Reflection.BindingFlags]'NonPublic,Static'
$replay = [System.Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $replayPath).Path)
$replayScanner = $replay.GetType('ModScanner', $true)
$replayResult = $replay.GetType('ModScanResult', $true)
# 回放件与游戏程序集都定义了 ModScanner，不能靠 [ModScanner] 解析，否则可能拿到回放件。
$nativeAssembly = [System.Reflection.Assembly]::LoadFrom((Join-Path $managed 'Assembly-CSharp.dll'))
$nativeScanner = $nativeAssembly.GetType('ModScanner', $true)
$nativeResult = $nativeAssembly.GetType('ModScanResult', $true)

# v4 才有的两样：扫描结果里的 Pending 与 DeprecatedCapabilities（「下一版会拒」的预警）。
# 用它们判别回放件与游戏扫描器的版本，比信任文件名可靠。
# 不拿 ModScanner 的 dynamic-code 白名单当判据：安装版本里它可能被内联，取不到字段。
$replayLooksV4 = $null -ne $replayResult.GetField('Pending') -or
    $null -ne $replayResult.GetProperty('DeprecatedCapabilities')
$nativeLooksV4 = $null -ne $nativeResult.GetField('Pending') -and
    $null -ne $nativeResult.GetProperty('DeprecatedCapabilities')
if ($replayLooksV4) {
    throw 'ScannerV3Replay 编出来的不是 v3（带 v4 的 Pending 或 DeprecatedCapabilities）。按 tests/ScannerV3Replay/README.md 重新导出源码。'
}
if (-not $nativeLooksV4) {
    throw '游戏目录里的扫描器不是 v4（缺 Pending 或 DeprecatedCapabilities），本机结论不可用。'
}
Write-Output 'PASS 回放件是 scanner v3，游戏扫描器是 v4'

function Assert-CapabilityFree([string]$Label, $Scan, [switch]$V4) {
    if ($null -eq $Scan) { throw "$Label 没有返回扫描结果。" }
    $problems = [System.Collections.Generic.List[string]]::new()
    if ($Scan.MethodsScanned -le 0) { $problems.Add("扫描方法数为 $($Scan.MethodsScanned)") }
    if ($Scan.Refused) { $problems.Add('Refused') }
    if ($Scan.Error) { $problems.Add("Error: $($Scan.Error)") }
    if (@($Scan.Capabilities).Count -gt 0) {
        $problems.Add('Capabilities: ' + (@($Scan.Capabilities) -join ', '))
    }
    if (@($Scan.HardFlags).Count -gt 0) {
        $problems.Add('HardFlags: ' + (@($Scan.HardFlags | ForEach-Object { $_.ToString() }) -join '; '))
    }
    if ($V4) {
        if (@($Scan.Pending).Count -gt 0) {
            $problems.Add('Pending: ' + (@($Scan.Pending | ForEach-Object { $_.ToString() }) -join '; '))
        }
        if (@($Scan.DeprecatedCapabilities).Count -gt 0) {
            $problems.Add('DeprecatedCapabilities: ' + (@($Scan.DeprecatedCapabilities) -join ', '))
        }
    }
    if ($problems.Count -gt 0) {
        foreach ($finding in @($Scan.Findings)) {
            if ($finding) { Write-Output ('  ' + $finding.ToString()) }
        }
        throw ("{0} 未通过：{1}" -f $Label, ($problems -join '；'))
    }
    $extra = if ($V4) { '、无预警' } else { '' }
    Write-Output ("PASS {0}（{1} 个方法，无能力、无硬拒{2}）" -f $Label, $Scan.MethodsScanned, $extra)
}

$packagePath = (Resolve-Path -LiteralPath $Package).Path
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('item-details-scan-' + [guid]::NewGuid().ToString('N'))
[System.IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $temp)
try {
    $manifests = @(Get-ChildItem -LiteralPath $temp -Recurse -File -Filter 'manifest.xml')
    if ($manifests.Count -ne 1) {
        throw ("发布包里应当恰好有一个 manifest.xml，实际 {0} 个。" -f $manifests.Count)
    }
    $folder = $manifests[0].Directory.FullName
    [xml]$manifest = Get-Content -LiteralPath $manifests[0].FullName -Raw
    [string[]]$assemblies = @($manifest.Manifest.Assemblies.Assembly | ForEach-Object {
        $assemblyPath = Join-Path $folder ([string]$_)
        if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
            throw "manifest 声明的程序集不存在：$_"
        }
        $assemblyPath
    })
    if ($assemblies.Count -eq 0) { throw '发布包的 manifest 没有声明任何程序集。' }

    # 离线没有 Unity 的程序集注册表，用安装目录里的全部程序集名当允许清单。
    [string[]]$allowed = @(Get-ChildItem -LiteralPath $managed -Filter '*.dll' -File |
        ForEach-Object BaseName)
    $arguments = [object[]]@($assemblies, $allowed, $folder, $false)
    Assert-CapabilityFree 'scanner v3' ($replayScanner.GetMethod('Scan').Invoke($null, $arguments))
    Assert-CapabilityFree 'scanner v4' ($nativeScanner.GetMethod('Scan').Invoke($null, $arguments)) -V4
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
