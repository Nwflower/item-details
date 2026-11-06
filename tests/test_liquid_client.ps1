#Requires -Version 7.4
param([Parameter(Mandatory)][string]$GameDir)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$managed = Join-Path $GameDir 'Probably Stolen_Data/Managed'
$refs = @('Assembly-CSharp.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.UI.dll', '0Harmony.dll') | ForEach-Object {
    $path = Join-Path $managed $_
    [void][Reflection.Assembly]::LoadFrom($path)
    $path
}
# Compile a separate consumer assembly using only game/framework references.
$source = Get-Content (Join-Path $repo 'sdk/ItemDetailsClient.cs') -Raw
$source += @'

public static class ConsumerProbe
{
    public static IDisposable Watch() => ItemDetailsClient.WatchTooltipSections();
    public static IDisposable ClaimSection(string id) =>
        ItemDetailsClient.RegisterTooltipSections((item,builder,section) => section.Id == id);
    public static bool SnapshotMatches(RichTextBuilder builder) =>
        ItemDetailsClient.TryGetTooltipSections(builder, out var sections) && sections.Count == 1 &&
        sections[0].Id == "probe" && sections[0].Runs.Count == 1 && sections[0].Runs[0].Kind == "break" &&
        sections[0].TryGet<int>("amount", out var amount) && amount == 12;
    public static IDisposable FaultyDisplay() =>
        ItemDetailsClient.RegisterTooltipSections((item,builder,section) => throw new InvalidOperationException("probe"));
    public static bool HasSpeed(RichTextBuilder builder) =>
        ItemDetailsClient.TryGetTooltipSections(builder, out var sections) && sections.Count == 1 &&
        sections[0].Id == "machine-speed" && sections[0].CanReplace && sections[0].Runs.Count == 4 &&
        sections[0].TryGet<int>("basePerDay", out var before) && before == 6 &&
        sections[0].TryGet<int>("currentPerDay", out var after) && after == 8;
    public static bool HasAppraisal(RichTextBuilder builder) =>
        ItemDetailsClient.TryGetTooltipSections(builder, out var sections) && sections.Count == 1 &&
        sections[0].TryGetAppraisal(out var a) && a.Total > 0 && a.PerfectMin <= a.Total &&
        a.Total <= a.PerfectMax && a.Parts.Count > 0 && a.Parts[0].IsBase;
    public static bool HasSections(RichTextBuilder builder) =>
        ItemDetailsClient.TryGetTooltipSections(builder, out _);
    public static bool HasAnalysis(GameItem item) => ItemDetailsClient.TryGetLiquidAnalysis(item, out _);
    public static bool HasAdvice(GameItem item) => ItemDetailsClient.TryGetWaterAdvice(item, out _);
    public static bool HasData(GameItem item) =>
        ItemDetailsClient.TryGetLiquidAnalysis(item, out var data) && data.LabelMismatch &&
        data.TotalMicroliters == 2000000 && data.CapacityMicroliters == 2000000 &&
        data.HiddenComponents == 1 && data.Components.Count == 2 && data.Effects.Toxic && data.Effects.Text.Length > 0 &&
        data.Components[0] == ("water", "水", 1925000, "water") &&
        data.Components[1] == ("acid", "酸", 74999, "other");
    public static bool EmptyAdvice(GameItem item) =>
        ItemDetailsClient.TryGetWaterAdvice(item, out var methods) && methods.Count == 0;
    public static IDisposable Subscribe(bool accept) =>
        ItemDetailsClient.RegisterLiquidAnalysisDisplay((item, data) => data.Effects.Toxic && accept);
    public static IDisposable Layout(GameItem item) =>
        ItemDetailsClient.RegisterTooltipLayout(current =>
            ReferenceEquals(current, item) ? TooltipLayoutOptions.NativeProgressBars | TooltipLayoutOptions.DrawsContents : null);
    public static int AnchorCalls;
    public static IDisposable Anchor() =>
        ItemDetailsClient.RegisterTooltipAnchor(() => { AnchorCalls++; return null; });
    public static bool Bound() =>
        AppDomain.CurrentDomain.GetData(ItemDetailsClient.AnalysisSlot) is Func<GameItem, LiquidPayload?> &&
        AppDomain.CurrentDomain.GetData(ItemDetailsClient.AdviceSlot) is Func<GameItem, Advice?>;
    public static void WrongProtocol() =>
        AppDomain.CurrentDomain.SetData(ItemDetailsClient.AnalysisSlot, "unrecognized provider");
}
'@
Add-Type -TypeDefinition $source -ReferencedAssemblies ($refs + @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName))
function Check([bool]$condition, [string]$name) {
    if (!$condition) { throw "FAIL $name" }
    Write-Output "PASS $name"
}
$client = [ItemDetails.Integration.ConsumerProbe]
Check (!$client::HasAnalysis($null) -and !$client::HasAdvice($null)) 'client runs before Item Details is loaded'
$declines = $client::Subscribe($false)
$client::WrongProtocol()
Check (!$client::HasAnalysis($null)) 'incompatible endpoint falls back'
$provider = [Reflection.Assembly]::LoadFrom((Join-Path $repo 'bin/Release/netstandard2.1/ItemDetails.dll'))
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$queries = $provider.GetType('ItemDetails.LiquidQueries')
$enable = $queries.GetMethod('Enable',$flags)
$disable = $queries.GetMethod('Disable',$flags)
$enable.Invoke($null,@())
Check ($client::Bound()) 'late provider publishes both V1 signatures across assemblies'
try {
    $item = [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject([GameItemElement])
    $item.modifiedState = [TagSystem]::new()
    $layout = $provider.GetType('ItemDetails.SectionedLayout')
    $flow = $layout.GetMethod('UsesFlowingText',$flags)
    $mod = [LoadedMod]::new()
    $mod.Manifest = [ModManifest]::new()
    $mod.Manifest.ID = 'amres.sectioned_tooltips'
    [ModLoader]::Mods.Add($mod)
    # 只有加载、没有排版声明：不按程序集名嗅探，也不读它的配置项。
    Check (!$flow.Invoke($null,@($item))) 'a loaded layout mod without a declaration changes nothing'
    $mod.IsEnabled = $true
    Check (!$flow.Invoke($null,@($item))) 'enabling that mod still changes nothing without a declaration'
    $null = [ModLoader]::Mods.Remove($mod)

    $custom = $client::Layout($item)
    $tp = $provider.GetType('ItemDetails.TooltipPipeline')
    $tp.GetField('_current',$flags).SetValue($null,$item)
    $tp.GetField('_buildId',$flags).SetValue($null,1)
    $nativeBars = $layout.GetProperty('UsesNativeBars',$flags)
    $sup = $provider.GetType('ItemDetails.ThirdPartyProviders').GetMethod('TooltipSuppressions',$flags)
    Check ($nativeBars.GetValue($null) -and !$flow.Invoke($null,@($item))) 'renderer requests native bars independently of column layout'
    Check ($sup.Invoke($null,@($item)).ToString() -eq 'Contents') 'optional client claims only the current item contents'
    $other = [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject([GameItemElement])
    Check ($sup.Invoke($null,@($other)).ToString() -eq 'None') 'unclaimed item keeps its contents'
    $custom.Dispose()
    $tp.GetField('_buildId',$flags).SetValue($null,2)
    Check (!$nativeBars.GetValue($null) -and $sup.Invoke($null,@($item)).ToString() -eq 'None') 'next tooltip after disposal restores default styling and contents'
    $anchor = $client::Anchor()
    $getAnchor = $provider.GetType('ItemDetails.ThirdPartyProviders').GetMethod('TryTooltipAnchor',$flags)
    Check (!$getAnchor.Invoke($null,@($null)) -and $client::AnchorCalls -eq 1) 'optional anchor queried and null falls back'
    $anchor.Dispose()
    Check (!$getAnchor.Invoke($null,@($null)) -and $client::AnchorCalls -eq 1) 'disposed anchor is no longer queried'
    $capacity = [TagState]::new('LIQUID_CONTAINER_CAPACITY','capacity')
    $capacity.valueInt = 2000000
    $item.modifiedState.dict.Add('LIQUID_CONTAINER_CAPACITY',$capacity)
    # Use only the engine-independent NextLine operation. The real builder's slice count distinguishes
    # observation (one replay), successful takeover (zero), and fallback to normal output after disposal.
    $sectionApi = $provider.GetType('ItemDetails.TooltipSections')
    $writerType = $provider.GetType('ItemDetails.TooltipSections+Writer')
    $rw = [Reflection.BindingFlags]'NonPublic,Instance'
    $slices = [RichTextBuilder].GetField('slices',$rw)
    $watch = $client::Watch()
    $sectionApi.GetMethod('Enable',$flags).Invoke($null,@())
    function Write-ProbeSection([string]$id) {
        $tp.GetField('_buildId',$flags).SetValue($null,1 + [int]$tp.GetField('_buildId',$flags).GetValue($null))
        $b = [RichTextBuilder]::new(40)
        $wr = [Activator]::CreateInstance($writerType,$rw,$null,@($b,$item,$id),$null)
        [void]$writerType.GetMethod('Field',$rw).MakeGenericMethod([int]).Invoke($wr,@('amount',12))
        [void]$writerType.GetMethod('NextLine',$rw).Invoke($wr,@())
        [void]$writerType.GetMethod('Finish',$rw).Invoke($wr,@($false))
        return $b
    }
    $unclaimed = Write-ProbeSection 'probe'
    Check (($slices.GetValue($unclaimed)).Count -eq 1 -and $client::SnapshotMatches($unclaimed)) 'snapshot reads the exact builder and native fallback emits once'
    $claim = $client::ClaimSection('probe')
    $claimed = Write-ProbeSection 'probe'
    Check (($slices.GetValue($claimed)).Count -eq 0 -and $client::SnapshotMatches($claimed)) 'generic consumer takes over a section without native duplication'
    $untouched = Write-ProbeSection 'other'
    Check (($slices.GetValue($untouched)).Count -eq 1) 'declining a different section preserves its output'
    $claim.Dispose()
    $fault = $client::FaultyDisplay()
    # The offline runtime has no Unity console; skip the first diagnostic write for this fixture.
    $counts = $provider.GetType('ItemDetails.Log').GetField('_counts',$flags).GetValue($null)
    $counts['TooltipDisplay.probe'] = 1
    $failed = Write-ProbeSection 'probe'
    Check (($slices.GetValue($failed)).Count -eq 1 -and $client::SnapshotMatches($failed)) 'a throwing observer leaves the original section exactly once'
    $fault.Dispose()
    $speedClaim = $client::ClaimSection('machine-speed')
    foreach ($pair in @(@('BASE_PROCESSING_SPEED_TAG',6),@('CURRENT_PROCESSING_SPEED_TAG',8),@('APPRAISAL_ITEM',1))) {
        $tag = [TagState]::new($pair[0],$pair[0])
        $tag.valueEnabled = $true
        $tag.valueInt = $pair[1]
        $item.modifiedState.dict.Add($pair[0],$tag)
    }
    $tp.GetField('_buildId',$flags).SetValue($null,100)
    $b = [RichTextBuilder]::new(40)
    $provider.GetType('ItemDetails.MachineSpeedLine').GetMethod('Write',$flags).Invoke($null,@($b,$item,'Speed:'))
    Check ($client::HasSpeed($b) -and ($slices.GetValue($b)).Count -eq 0) 'real machine speed renderer provides 6 -> 8 and retains color segments'
    $speedClaim.Dispose()
    $valueClaim = $client::ClaimSection('value')
    $tp.GetField('_buildId',$flags).SetValue($null,101)
    $breakdown = $provider.GetType('ItemDetails.ValueBreakdown')
    $handTags = @{HAND_MATERIAL='CARBON_COMPOSITE';HAND_INTERFACE='NEURAL';HAND_CONDITION='NONE';HAND_ADDON_ARMORED='ARMORED';HAND_ADDON_BLADED='BLADED'}
    $hand = $breakdown.GetMethod('ComputeHand',$flags).Invoke($null,@([Func[string,string]]{param($key) if ($handTags.ContainsKey($key)) { return $handTags[$key] } return $null}))
    $breakdown.GetField('_memoResult',$flags).SetValue($null,$hand)
    $breakdown.GetField('_memoItem',$flags).SetValue($null,$item)
    $breakdown.GetField('_memoBuild',$flags).SetValue($null,101)
    $b = [RichTextBuilder]::new(40)
    $wr = [Activator]::CreateInstance($writerType,$rw,$null,@($b,$item,'value'),$null)
    $entryType = $provider.GetType('ItemDetails.ModEntry')
    $entryObject = [Activator]::CreateInstance($entryType)
    Check ($entryType.GetMethod('TryAppendValue',$rw).Invoke($entryObject,@($wr,$item))) 'built-in appraisal yields a value section'
    [void]$writerType.GetMethod('Finish',$rw).Invoke($wr,@($false))
    Check ($client::HasAppraisal($b) -and ($slices.GetValue($b)).Count -eq 0) 'real appraisal renderer exposes localized parts, total and perfect range'
    $valueClaim.Dispose()
    $watch.Dispose()
    $later = Write-ProbeSection 'probe'
    Check (($slices.GetValue($later)).Count -eq 1 -and !$client::HasSections($later)) 'no snapshot or suppression after the consumer disconnects'
    $sectionApi.GetMethod('Disable',$flags).Invoke($null,@())
    Check (!$client::HasSections($claimed)) 'disabling the producer withdraws the snapshot endpoint'
    $raw = [Activator]::CreateInstance($provider.GetType('ItemDetails.WaterInfo+Result'))
    $inst = [Reflection.BindingFlags]'NonPublic,Instance'
    foreach ($entry in @{TotalParts=2000000;WaterParts=1925000;WaterName='水';Tier='实测';Label='标签';Toxic=$true}.GetEnumerator()) {
        $raw.GetType().GetField($entry.Key,$inst).SetValue($raw,$entry.Value)
    }
    $impurities = $raw.GetType().GetField('Impurities',$inst).GetValue($raw)
    $impurities.Add([ValueTuple[string,int,string]]::new('酸',74999,'acid'))
    $impurities.Add([ValueTuple[string,int,string]]::new('微量',1,'trace'))
    $snapshot = $queries.GetMethod('Snapshot',$flags).Invoke($null,@($raw,$item))
    # Prime the normal per-tooltip cache so queries need no Unity scene.
    $provider.GetType('ItemDetails.TooltipPipeline').GetField('_current',$flags).SetValue($null,$item)
    $queries.GetField('currentAnalysis',$flags).SetValue($null,$snapshot)
    $queries.GetField('analyzed',$flags).SetValue($null,$true)
    $queries.GetField('currentAdvice',$flags).SetValue($null,[ValueTuple[string,double][]]::new(0))
    $queries.GetField('simulated',$flags).SetValue($null,$true)
    Check ($client::HasData($item)) 'actual snapshot preserves units, visible components, label and effects'
    Check ($client::EmptyAdvice($item)) 'empty advice is an available result'
    $choice = $provider.GetType('ItemDetails.ModSettings').GetField('LiquidChoice',$flags).GetValue($null)
    $entry = $choice.GetType().GetField('_entry',$inst)
    $off = [Activator]::CreateInstance([ConfigEntry[string]],$inst,$null,@('关闭 (Off)'),$null)
    $entry.SetValue($choice,$off)
    Check (!$client::HasAnalysis($item) -and !$client::HasAdvice($item)) 'turning off analysis hides even a cached snapshot and advice'
    $entry.SetValue($choice,$null)
    $payload = $snapshot.GetType().GetProperty('Data',$inst).GetValue($snapshot)
    $display = $queries.GetMethod('TryDisplay',$flags).MakeGenericMethod($payload.GetType())
    $argsForDisplay = @('item_details.liquid_analysis.v1.display',$item,$payload)
    Check (!$display.Invoke($null,$argsForDisplay)) 'declined replacement keeps built-in section'
    $accepts = $client::Subscribe($true)
    Check ($display.Invoke($null,$argsForDisplay)) 'ready independent consumer takes over just the analysis'
    $accepts.Dispose()
    $accepts.Dispose()
    Check (!$display.Invoke($null,$argsForDisplay)) 'disposed display restores built-in section'
}
finally { $declines.Dispose(); $disable.Invoke($null,@()) }
Check (!$client::HasAnalysis($item) -and !$client::HasAdvice($item)) 'disabled provider restores fallback'
