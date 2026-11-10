param(
    [string]$GameDir = $env:PS_GAME_DIR,
    [string]$CandidateDll = (Join-Path $PSScriptRoot '../bin/Release/netstandard2.1/ItemDetails.dll')
)
$ErrorActionPreference = 'Stop'
if (-not $GameDir) { throw 'Pass -GameDir or set PS_GAME_DIR to the installed game directory.' }
$managed = Join-Path $GameDir 'Probably Stolen_Data/Managed'
foreach ($file in @('UnityEngine.CoreModule.dll', 'Unity.ResourceManager.dll', 'Unity.Localization.dll', '0Harmony.dll', 'Assembly-CSharp.dll')) {
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

# Nightly exchange previews use the game's conditions without invoking any settlement callbacks.
$preview = $candidate.GetType('ItemDetails.TrashExchangePreview', $true).GetMethod('Preview', $static)
$exchange = [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject([ExchangeManager])
$exchange.activeBarter = [System.Collections.Generic.List[ExchangeBarter]]::new()
$exchange.promotionalBarter = [System.Collections.Generic.List[ExchangeBarter]]::new()
$exchange.activePromotionalBarterIds = [System.Collections.Generic.List[string]]::new()
$filter = [ExchangeList]::FilterBarter()
$exchange.activeBarter.Add($filter)
$dust = [ExchangeList]::DreamDustBarter()
$exchange.activeBarter.Add($dust)
$promo = [ExchangeList]::ImprovedDreamDustBarter()
$exchange.promotionalBarter.Add($promo)
$stock = [System.Collections.Generic.List[GameItem]]::new()

function Add-ExchangeItem([string]$Id, [int]$Count, [int]$Purity = -1) {
    $sample = New-ProbeItem
    $sample.identifier = $Id
    $sample.unitCount = $Count
    if ($Purity -ge 0) {
        $purityTag = [TagState]::new('CHEMICAL_PRODUCT_PURITY_TAG','Purity')
        $purityTag.valueEnabled = $true
        $purityTag.valueDouble = $Purity
        $sample.modifiedState.dict.Add($purityTag.identifier, $purityTag)
    }
    $stock.Add($sample)
}
function Get-ExchangePreview { $preview.Invoke($null, @($exchange, $stock)) }

Add-ExchangeItem 'water_filter' 3
Assert-True (@(Get-ExchangePreview).Count -eq 0) 'a stack cannot satisfy multiple exchange requirements'
Add-ExchangeItem 'water_filter' 1
Add-ExchangeItem 'water_filter' 1
Assert-True ((@(Get-ExchangePreview) -join '|') -eq 'Water filter') 'fixed exchange reward previews before nightly counters reset'
Assert-True ($stock.Count -eq 3 -and $stock[0].unitCount -eq 3 -and $filter.currentBarterCounter -eq 0) 'preview preserves stock and nightly counters'
$nativeFilter = $filter.onAccept
$replacementCallback = [Action[System.Collections.Generic.List[GameItem]]]{ throw 'Preview ran the settlement callback' }
$filter.onAccept = $replacementCallback
Assert-True (@(Get-ExchangePreview).Count -eq 0) 'a replacement callback with the vanilla ID hides the reward without running it'
$filter.onAccept = $nativeFilter
1..4 | ForEach-Object { Add-ExchangeItem 'dream_dust' 1 29 }
Assert-True ((@(Get-ExchangePreview) -join '|') -eq 'No reward (Dream Dust purity too low)|Water filter') 'inactive promotions do not override the base trade'
$exchange.activePromotionalBarterIds.Add($promo.identifier)
Assert-True ((@(Get-ExchangePreview) -join '|') -eq 'Bandage ×2|Water filter') 'higher-priority promotion reserves shared inputs'
$nativePromo = $promo.onAccept
$promo.onAccept = $replacementCallback
Assert-True ((@(Get-ExchangePreview) -join '|') -eq 'Water filter') 'hidden promotion still reserves its inputs and leaves unrelated previews visible'
$promo.onAccept = $nativePromo
$promo.barterCounterPerNight = 0
Assert-True ((@(Get-ExchangePreview) -join '|') -eq 'No reward (Dream Dust purity too low)|Water filter') 'zero nightly quota skips the offer'

$exchange.activeBarter.Add([ExchangeList]::CommandKeycardBarter())
Add-ExchangeItem 'cmd_keycard' 1
Assert-True ((Get-ExchangePreview) -contains 'Two draws from: Security keycard / Engineering keycard / Medical keycard') 'random keycards list all concrete candidates'
$exchange.activeBarter.Add([ExchangeList]::ModuleBarter())
foreach ($id in @('system_module_performance', 'system_module_quality')) {
    Add-ExchangeItem $id 1
    $moduleTag = [TagState]::new('MODULE','MODULE')
    $moduleTag.valueEnabled = $true
    $stock[$stock.Count - 1].modifiedState.dict.Add('MODULE',$moduleTag)
}
[ExchangeList]::modulePool.Add('probe_modded_module')
[ExchangeList]::modulePoolOdd.Add(15)
try {
    $moduleReward = (Get-ExchangePreview) | Where-Object { $_.Contains('Capped neural core') }
    Assert-True ($moduleReward.Contains('probe_modded_module')) 'module candidates read the live modded reward pool'
    Assert-True (!$moduleReward.Contains('Performance module') -and !$moduleReward.Contains('Quality module')) 'submitted modules are excluded from the candidates'
}
finally {
    [ExchangeList]::modulePool.RemoveAt([ExchangeList]::modulePool.Count - 1)
    [ExchangeList]::modulePoolOdd.RemoveAt([ExchangeList]::modulePoolOdd.Count - 1)
}

# An exhausted installed filter must not hide changes to replacement-filter candidates.
$waterAdvice = $candidate.GetType('ItemDetails.WaterAdvice', $true)
$simulationSignature = $waterAdvice.GetMethod('TryFilterSignature', $static)
$reportType = $candidate.GetType('ItemDetails.PurifySources+Report', $true)
$report = [System.Activator]::CreateInstance($reportType, $true)
$instance = [System.Reflection.BindingFlags]'NonPublic, Instance'
$replacementSignature = $reportType.GetField('FiltersSignature', $instance)
function Read-FilterKey($Installed) {
    [object[]]$arguments = @($report, $Installed, 123L)
    if (-not $simulationSignature.Invoke($null, $arguments)) { throw 'Filter signature could not read the fixture.' }
    return $arguments[2]
}
$installed = New-ProbeItem
$installed.modifiedState.dict.Add('DURABILITY_TAG', [TagState]::new('DURABILITY_TAG', 'Durability'))
$replacementSignature.SetValue($report, 222L)
$emptyKey = Read-FilterKey $installed
$replacementSignature.SetValue($report, 333L)
Assert-True ($emptyKey -ne (Read-FilterKey $installed)) 'exhausted installed filter includes replacement state in water cache key'
$installed.modifiedState.dict['DURABILITY_TAG'].valueInt = 400
$healthyKey = Read-FilterKey $installed
$replacementSignature.SetValue($report, 444L)
Assert-True ($healthyKey -eq (Read-FilterKey $installed)) 'usable installed filter does not depend on unused replacements'
$installed.modifiedState.dict['DURABILITY_TAG'].valueInt = 0
Assert-True ($healthyKey -ne (Read-FilterKey $installed)) 'installed filter exhaustion invalidates the filter selection key'

$usableFilter = $candidate.GetType('ItemDetails.PurifySources', $true).GetMethod('IsUsableFilter', $static)
function Test-UsableFilter($Filter) { return [bool]$usableFilter.Invoke($null, [object[]]@(,$Filter)) }
Assert-True (-not (Test-UsableFilter $null)) 'missing installed filter allows replacement candidates'
Assert-True (-not (Test-UsableFilter $installed)) 'zero-durability installed filter allows replacement candidates'
$installed.modifiedState.dict['DURABILITY_TAG'].valueInt = -1
Assert-True (-not (Test-UsableFilter $installed)) 'negative durability is not a usable filter'
$installed.modifiedState.dict['DURABILITY_TAG'].valueInt = 1
Assert-True (Test-UsableFilter $installed) 'partly worn installed filter still takes priority'
$withoutDurability = New-ProbeItem
Assert-True (-not (Test-UsableFilter $withoutDurability)) 'unknown ordinary-filter durability is not assumed usable'
Assert-True ($withoutDurability.modifiedState.dict.Count -eq 0) 'filter eligibility does not insert missing durability tags'
$withoutDurability.identifier = 'uv_filter'
Assert-True (Test-UsableFilter $withoutDurability) 'UV filter works without a durability tag'

# Public tooltip takeover: composable sections, native MOD_ID_TAG matching, duplicate rejection,
# restoration on Dispose, and the contents-only convenience API.
function Set-SourceModId($Item, [string]$ModId) {
    $sourceTag = [TagState]::new('MOD_ID_TAG', 'Mod ID')
    $sourceTag.valueEnabled = $true
    $sourceTag.valueString = $ModId
    $Item.modifiedState.dict['MOD_ID_TAG'] = $sourceTag
}
$api = $candidate.GetType('ItemDetails.ItemDetailsApi', $true)
$sectionType = $candidate.GetType('ItemDetails.ItemDetailsTooltipSection', $true)
$publicStatic = [System.Reflection.BindingFlags]'Public, Static'
$registerTooltip = $api.GetMethod('RegisterTooltipDisplay', [type[]]@([string], $sectionType))
$registerContents = $api.GetMethod('RegisterContentsDisplay', $publicStatic)
Assert-True ($null -ne $registerTooltip) 'RegisterTooltipDisplay is public'
Assert-True ($sectionType.IsDefined([System.FlagsAttribute], $false)) 'tooltip sections are composable flags'
$parameters = $registerTooltip.GetParameters()
$signatureOk = $parameters.Count -eq 2 -and $parameters[0].ParameterType -eq [string]
$signatureOk = $signatureOk -and $parameters[1].ParameterType -eq $sectionType
$signatureOk = $signatureOk -and $registerTooltip.ReturnType -eq [System.IDisposable]
Assert-True $signatureOk 'RegisterTooltipDisplay has the promised signature'
$handlesTooltip = $candidate.GetType('ItemDetails.ThirdPartyProviders', $true).GetMethod('HandlesTooltip', $static)
$contents = [System.Enum]::Parse($sectionType, 'Contents')
$liquid = [System.Enum]::Parse($sectionType, 'LiquidAnalysis')
$authenticity = [System.Enum]::Parse($sectionType, 'Authenticity')
$combined = [System.Enum]::Parse($sectionType, 'Contents, LiquidAnalysis')
$contentsItem = New-ProbeItem
Set-SourceModId $contentsItem 'offline_contents_mod'
Assert-True (-not [bool]$handlesTooltip.Invoke($null, @($contentsItem, $contents))) 'tooltip display is unchanged before registration'
$tooltipRegistration = $registerTooltip.Invoke($null, @(' offline_contents_mod ', $combined))
try {
    Assert-True ([bool]$handlesTooltip.Invoke($null, @($contentsItem, $contents))) 'combined registration includes contents'
    Assert-True ([bool]$handlesTooltip.Invoke($null, @($contentsItem, $liquid))) 'combined registration includes liquid analysis'
    Assert-True (-not [bool]$handlesTooltip.Invoke($null, @($contentsItem, $authenticity))) 'unselected sections remain visible'
    $otherItem = New-ProbeItem
    Set-SourceModId $otherItem 'other_mod'
    Assert-True (-not [bool]$handlesTooltip.Invoke($null, @($otherItem, $contents))) 'tooltip takeover does not affect other mods'
    $duplicateRejected = $false
    try { $registerTooltip.Invoke($null, @('offline_contents_mod', $combined)) | Out-Null }
    catch {
        for ($ex = $_.Exception; $null -ne $ex; $ex = $ex.InnerException) {
            if ($ex -is [System.InvalidOperationException]) { $duplicateRejected = $true; break }
        }
    }
    Assert-True $duplicateRejected 'duplicate tooltip registration is rejected'
}
finally { $tooltipRegistration.Dispose() }
Assert-True (-not [bool]$handlesTooltip.Invoke($null, @($contentsItem, $contents))) 'disposing restores tooltip display'
$tooltipRegistration.Dispose()
Assert-True (-not [bool]$handlesTooltip.Invoke($null, @($contentsItem, $liquid))) 'tooltip registration disposal is idempotent'

$contentsRegistration = $registerContents.Invoke($null, @('offline_contents_mod'))
try {
    Assert-True ([bool]$handlesTooltip.Invoke($null, @($contentsItem, $contents))) 'contents convenience API suppresses contents'
    Assert-True (-not [bool]$handlesTooltip.Invoke($null, @($contentsItem, $liquid))) 'contents convenience API selects no other section'
}
finally { $contentsRegistration.Dispose() }
Assert-True (-not [bool]$handlesTooltip.Invoke($null, @($contentsItem, $contents))) 'contents convenience disposal restores display'

# Per-item takeover: ignores the source mod, several registrations combine,
# a throwing callback only loses its own part, and Dispose restores.
$itemFunc = [System.Func`2].MakeGenericType([GameItem], $sectionType)
$registerItemTooltip = $api.GetMethod('RegisterTooltipDisplay', [type[]]@($itemFunc))
Assert-True ($null -ne $registerItemTooltip) 'per-item RegisterTooltipDisplay is public'
function New-ItemTakeover([scriptblock]$Body) { [System.Management.Automation.LanguagePrimitives]::ConvertTo($Body, $itemFunc) }
$none = [System.Enum]::Parse($sectionType, 'None')
$ownedItem = New-ProbeItem
$contentsTakeover = New-ItemTakeover { param($it) if ([object]::ReferenceEquals($it, $ownedItem)) { $contents } else { $none } }
$liquidTakeover = New-ItemTakeover { param($it) if ([object]::ReferenceEquals($it, $ownedItem)) { $liquid } else { $none } }
$throwingTakeover = New-ItemTakeover { param($it) throw 'takeover failed' }
$itemRegistrations = @(
    $registerItemTooltip.Invoke($null, @($contentsTakeover)),
    $registerItemTooltip.Invoke($null, @($liquidTakeover)),
    $registerItemTooltip.Invoke($null, @($throwingTakeover)))
try {
    Assert-True ([bool]$handlesTooltip.Invoke($null, @($ownedItem, $contents))) 'per-item takeover covers its item'
    Assert-True ([bool]$handlesTooltip.Invoke($null, @($ownedItem, $liquid))) 'per-item registrations combine'
    Assert-True (-not [bool]$handlesTooltip.Invoke($null, @($ownedItem, $authenticity))) 'per-item takeover selects nothing else'
    Assert-True (-not [bool]$handlesTooltip.Invoke($null, @($contentsItem, $contents))) 'per-item takeover leaves other items alone'
}
finally { foreach ($r in $itemRegistrations) { $r.Dispose() } }
Assert-True (-not [bool]$handlesTooltip.Invoke($null, @($ownedItem, $contents))) 'disposing per-item takeover restores display'

# External tooltip anchors: fixed and dynamic public overloads, duplicate rejection,
# null/exception fallback, and restoration after Dispose.
$anchorOverloads = @($api.GetMethods($publicStatic) | Where-Object Name -eq 'RegisterTooltipAnchor')
Assert-True ($anchorOverloads.Count -eq 2) 'RegisterTooltipAnchor has fixed and dynamic overloads'
$providerType = [System.Func[UnityEngine.RectTransform]]
$registerAnchor = $anchorOverloads | Where-Object { $_.GetParameters()[0].ParameterType -eq $providerType }
Assert-True ($null -ne $registerAnchor) 'dynamic tooltip anchor registration is public'
$resolveAnchor = $candidate.GetType('ItemDetails.TypeBadge', $true).GetMethod('ResolveTooltipAnchor', $static)
Assert-True ($null -eq $resolveAnchor.Invoke($null, @($null))) 'badge uses no external anchor before registration'
$anchorProvider = [System.Func[UnityEngine.RectTransform]] { $null }
$anchorRegistration = $registerAnchor.Invoke($null, @($anchorProvider))
try {
    Assert-True ($null -eq $resolveAnchor.Invoke($null, @($null))) 'null dynamic anchor falls back to the native tooltip'
    $duplicateAnchorRejected = $false
    try { $registerAnchor.Invoke($null, @($anchorProvider)) | Out-Null }
    catch {
        for ($ex = $_.Exception; $null -ne $ex; $ex = $ex.InnerException) {
            if ($ex -is [System.InvalidOperationException]) { $duplicateAnchorRejected = $true; break }
        }
    }
    Assert-True $duplicateAnchorRejected 'duplicate tooltip anchor registration is rejected'
}
finally { $anchorRegistration.Dispose() }
Assert-True ($null -eq $resolveAnchor.Invoke($null, @($null))) 'disposing restores native tooltip anchoring'
$throwingProvider = [System.Func[UnityEngine.RectTransform]] { throw [System.InvalidOperationException]::new('probe') }
$anchorRegistration = $registerAnchor.Invoke($null, @($throwingProvider))
try {
    Assert-True ($null -eq $resolveAnchor.Invoke($null, @($null))) 'anchor provider exceptions fall back to the native tooltip'
}
finally { $anchorRegistration.Dispose() }
