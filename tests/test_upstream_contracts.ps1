param(
    [string]$GameDir = $env:PS_GAME_DIR,
    [string]$CandidateDll = (Join-Path $PSScriptRoot '../bin/Release/netstandard2.1/ItemDetails.dll')
)
$ErrorActionPreference = 'Stop'
if (-not $GameDir) { throw 'Pass -GameDir or set PS_GAME_DIR to the installed game directory.' }
$managed = Join-Path $GameDir 'Probably Stolen_Data/Managed'
foreach ($file in @('UnityEngine.CoreModule.dll', 'Assembly-CSharp.dll')) {
    [System.Reflection.Assembly]::LoadFrom((Join-Path $managed $file)) | Out-Null
}
$candidate = [System.Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $CandidateDll).Path)
$static = [System.Reflection.BindingFlags]'NonPublic, Static'
$amount = $candidate.GetType('ItemDetails.WaterInfo', $true).GetMethod('Amount', $static)
$fingerprint = $candidate.GetType('ItemDetails.ItemValueFingerprint', $true).GetMethod('TryRead', $static)
$signature = $candidate.GetType('ItemDetails.ContainerItemSignature', $true)
$capture = $signature.GetMethod('Capture', $static)
$matches = $signature.GetMethod('Matches', $static)

function New-ProbeItem {
    # Avoid constructing a Unity rendering object; these consumers read only managed fields/children.
    $item = [System.Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject([GameItemElement])
    $item.identifier = 'offline_contract_probe'
    $item.uniqueId = 17
    $item.name = 'Probe'
    $item.unitCount = 1
    $item.modifiedState = [TagSystem]::new()
    $item.itemFeatures = [System.Collections.Generic.List[ItemFeature]]::new()
    return $item
}
function Read-Fingerprint($Item) {
    [object[]]$arguments = @($Item, 0L)
    if (-not $fingerprint.Invoke($null, $arguments)) { throw 'Production fingerprint could not read the sample.' }
    return $arguments[1]
}
function Assert-True([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw ('FAIL ' + $Name) }
    Write-Output ('PASS ' + $Name)
}

$previousCulture = [System.Globalization.CultureInfo]::CurrentCulture
try {
    [System.Globalization.CultureInfo]::CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture
    # 游戏的 FormatAmount 向下舍入（截断），不是四舍五入：2999 µl 报 2.99 mL，不是 3.00 mL。
    # 这样显示的液量永远不超过容器实际容量，期望值必须照它写。
    foreach ($case in @(
        @(49, '49 uL'), @(50, '50 uL'), @(600, '600 uL'), @(602, '602 uL'),
        @(999, '999 uL'), @(1000, '1.00 mL'), @(1001, '1.00 mL'), @(1999, '1.99 mL'),
        @(2999, '2.99 mL'), @(3000, '3.00 mL'),
        @(1000000, '1.00 L'), @(1000001, '1.00 L'), @([int]::MaxValue, '2147.48 L')
    )) {
        $actual = $amount.Invoke($null, @([int]$case[0]))
        Assert-True ($actual -eq $case[1]) ('active dose ' + $case[0] + ' => ' + $actual)
    }
}
finally { [System.Globalization.CultureInfo]::CurrentCulture = $previousCulture }

$item = New-ProbeItem
$tag = [TagState]::new('DURABILITY_TAG', 'Durability')
$tag.valueEnabled = $true
$tag.valueInt = 400
$item.modifiedState.dict.Add('DURABILITY_TAG', $tag)
$healthy = Read-Fingerprint $item
Assert-True ($healthy -eq (Read-Fingerprint $item)) 'stable value state has a stable fingerprint'
$tag.valueInt = 0
$exhausted = Read-Fingerprint $item
Assert-True ($healthy -ne $exhausted) 'filter wear invalidates value fingerprint'
$tag.valueEnabled = $false
Assert-True ($exhausted -ne (Read-Fingerprint $item)) 'tag enabled state invalidates value fingerprint'
Assert-True ($item.modifiedState.dict.Count -eq 1) 'fingerprint reads do not insert tags'

$items = [System.Collections.Generic.List[GameItem]]::new()
$items.Add($item)
$snapshot = $capture.Invoke($null, [object[]](,$items))
Assert-True ([bool]$matches.Invoke($null, @($snapshot, $items))) 'unchanged children match'
$item.unitCount = 2
Assert-True (-not [bool]$matches.Invoke($null, @($snapshot, $items))) 'stack change invalidates container signature'
$item.unitCount = 1
$item.name = 'Renamed'
Assert-True (-not [bool]$matches.Invoke($null, @($snapshot, $items))) 'rename invalidates container signature'
$replacement = New-ProbeItem
$replacement.name = $item.name
$replacement.modifiedState.dict.Add('DURABILITY_TAG', $tag)
$items[0] = $replacement
Assert-True (-not [bool]$matches.Invoke($null, @($snapshot, $items))) 'same-count replacement invalidates container signature'
