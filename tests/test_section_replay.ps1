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

public static class ReplayProbe
{
    public static IDisposable Watch() => ItemDetailsClient.WatchTooltipSections();
    public static IDisposable ClaimAll() =>
        ItemDetailsClient.RegisterTooltipSections((item, builder, section) => true);
    public static bool SectionAvailable() => ItemDetailsClient.SectionProtocolAvailable;
    public static bool LiquidAvailable() => ItemDetailsClient.LiquidProtocolAvailable;
    public static string Ids(RichTextBuilder builder) =>
        ItemDetailsClient.TryGetTooltipSections(builder, out var sections)
            ? string.Join(",", System.Linq.Enumerable.Select(sections, s => s.Id)) : "";
    public static string Runs(RichTextBuilder builder)
    {
        if (!ItemDetailsClient.TryGetTooltipSections(builder, out var sections)) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var s in sections)
        {
            sb.Append(s.Id).Append('|');
            foreach (var f in s.Fields) sb.Append(f.Key).Append('=').Append(f.Value).Append(';');
            foreach (var r in s.Runs) sb.Append(r.Kind).Append(':').Append(r.Text).Append(':').Append(r.Color).Append(';');
        }
        return sb.ToString();
    }
    public static bool UsesStableIds(RichTextBuilder builder) =>
        Ids(builder) == TooltipSectionIds.MachineSpeed + "," + TooltipSectionIds.Appraisal;
}
'@
Add-Type -TypeDefinition $source -ReferencedAssemblies ($refs + @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName))
function Check([bool]$condition, [string]$name) {
    if (!$condition) { throw "FAIL $name" }
    Write-Output "PASS $name"
}
$probe = [ItemDetails.Integration.ReplayProbe]
$provider = [Reflection.Assembly]::LoadFrom((Join-Path $repo 'bin/Release/netstandard2.1/ItemDetails.dll'))
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$sectionApi = $provider.GetType('ItemDetails.TooltipSections')
$tp = $provider.GetType('ItemDetails.TooltipPipeline')
$writerType = $provider.GetType('ItemDetails.TooltipSections+Writer')
$rw = [Reflection.BindingFlags]'NonPublic,Instance'
$entryType = $provider.GetType('ItemDetails.ModEntry')
$entryObject = [Activator]::CreateInstance($entryType)
$breakdown = $provider.GetType('ItemDetails.ValueBreakdown')

$item = [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject([GameItemElement])
$item.modifiedState = [TagSystem]::new()
foreach ($pair in @(@('BASE_PROCESSING_SPEED_TAG',6),@('CURRENT_PROCESSING_SPEED_TAG',8),@('APPRAISAL_ITEM',1))) {
    $tag = [TagState]::new($pair[0],$pair[0])
    $tag.valueEnabled = $true
    $tag.valueInt = $pair[1]
    $item.modifiedState.dict.Add($pair[0],$tag)
}
$tp.GetField('_current',$flags).SetValue($null,$item)
$handTags = @{HAND_MATERIAL='CARBON_COMPOSITE';HAND_INTERFACE='NEURAL';HAND_CONDITION='NONE';HAND_ADDON_ARMORED='ARMORED';HAND_ADDON_BLADED='BLADED'}
$hand = $breakdown.GetMethod('ComputeHand',$flags).Invoke($null,@([Func[string,string]]{param($key) if ($handTags.ContainsKey($key)) { return $handTags[$key] } return $null}))
$breakdown.GetField('_memoResult',$flags).SetValue($null,$hand)
$breakdown.GetField('_memoItem',$flags).SetValue($null,$item)

function Render-All([int]$build) {
    $tp.GetField('_buildId',$flags).SetValue($null,$build)
    $breakdown.GetField('_memoBuild',$flags).SetValue($null,$build)
    $b = [RichTextBuilder]::new(40)
    [void]$provider.GetType('ItemDetails.MachineSpeedLine').GetMethod('Write',$flags).Invoke($null,@($b,$item,'Speed:'))
    $wr = [Activator]::CreateInstance($writerType,$rw,$null,@($b,$item,'value'),$null)
    [void]$entryType.GetMethod('TryAppendValue',$rw).Invoke($entryObject,@($wr,$item))
    [void]$writerType.GetMethod('Finish',$rw).Invoke($wr,@($false))
    return $b
}

$sectionApi.GetMethod('Enable',$flags).Invoke($null,@())
$watch = $probe::Watch()
$claim = $probe::ClaimAll()
try {
    Check ($probe::SectionAvailable()) 'section protocol advertised after enable'
    # Watch fills snapshots; claim keeps the offline host away from native Add.
    $first = Render-All 1
    $second = Render-All 2
    Check ($probe::Runs($first) -eq $probe::Runs($second)) 'section snapshots are deterministic across builds'
    Check ($probe::UsesStableIds($first)) "emitted ids match the SDK constants (actual: $($probe::Ids($first)))"
    Check ($probe::Runs($first).Contains('basePerDay=6') -and $probe::Runs($first).Contains('currentPerDay=8')) 'machine speed fields survive into the snapshot'
    Check ($probe::Runs($first).Contains('total=500')) 'appraisal total survives into the snapshot'

    # C# 5 客户端：槽位按委托的运行时类型匹配，只编译通过不够，必须真的读到同一份快照。
    $c5Path = Join-Path $repo 'tests/CSharp5Client/bin/Release/netstandard2.1/ItemDetailsClient5.dll'
    Check (Test-Path $c5Path) 'C# 5 client assembly was built before the contract tests'
    $c5 = [Reflection.Assembly]::LoadFrom($c5Path).GetType('ItemDetails.Integration.ItemDetailsClient')
    $c5Read = $c5.GetMethod('TryGetTooltipSections')
    $c5Args = [object[]]@($first, $null)
    Check ([bool]$c5Read.Invoke($null, $c5Args)) 'C# 5 client reads the live section snapshot'
    $c5Sections = @($c5Args[1])
    $c5Ids = ($c5Sections | ForEach-Object { $_.Id }) -join ','
    Check ($c5Ids -eq ($probe::Ids($first))) "C# 5 client sees the same section ids (actual: $c5Ids)"
    $c5Speed = $c5Sections | Where-Object { $_.Id -eq 'machine-speed' } | Select-Object -First 1
    Check ($c5Speed.Group -eq 'machine' -and $c5Speed.Runs.Count -gt 0 -and $c5Speed.Runs[0].Kind.Length -gt 0) 'C# 5 client exposes group and converted runs'
}
finally { $claim.Dispose(); $watch.Dispose(); $sectionApi.GetMethod('Disable',$flags).Invoke($null,@()) }
Check (!$probe::SectionAvailable()) 'section availability withdrawn on disable'
$queries = $provider.GetType('ItemDetails.LiquidQueries')
$queries.GetMethod('Enable',$flags).Invoke($null,@())
Check ($probe::LiquidAvailable()) 'liquid availability advertised independently'
$queries.GetMethod('Disable',$flags).Invoke($null,@())
Check (!$probe::LiquidAvailable()) 'liquid availability withdrawn independently'

# Exercise the same float arithmetic as RichTextBuilder.Build, including CJK rounding edges.
$barStyle = $provider.GetType('ItemDetails.ProgressBarStyle')
$lineWidth = $barStyle.GetMethod('WidthForLine', $flags, $null, [type[]]@([int], [single]), $null)
foreach ($scale in @([single]1, [single]1.5, [single]2)) {
    foreach ($length in 0..512) {
        $width = [int]$lineWidth.Invoke($null, @([int]$length, $scale))
        $budget = if ($scale -gt 1) { [int][Math]::Truncate([single]($width * [single]0.66)) } else { $width }
        if ($budget -lt $length) { throw "FAIL line width length=$length scale=$scale budget=$budget" }
        if ($scale -le 1 -and $width -ne $length) { throw 'FAIL Latin width unnecessarily expanded' }
    }
}
Check $true 'complete line width fits native Latin/CJK budgets for lengths 0..512'

# 本作的条带不折行标记；构建时把 maxCharWidth 收窄，使 0.875 预算恰好等于原来的 0.66 预算。
$narrow = $barStyle.GetMethod('NarrowForNoBreak', $flags)
foreach ($w in 1..2000) {
    $n = [int]$narrow.Invoke($null, @([int]$w))
    $want = [Math]::Max(1, [int][Math]::Truncate([single]([single]$w * [single]0.66)))
    $got = [Math]::Max(1, [int][Math]::Truncate([single]([single]$n * [single]0.875)))
    if ($got -ne $want -or $n -gt $w) { throw "FAIL narrow width=$w narrowed=$n budget=$got want=$want" }
}
Check $true 'narrowed width restores the 0.66 budget exactly for widths 1..2000'
$writerSource = Get-Content (Join-Path $repo 'src/Bootstrap/ModEntry.Tooltip.cs') -Raw
$marks = [regex]::Matches($writerSource, 'builder\.AddProgressBar\([^;]*\);\s*ProgressBarStyle\.MarkOwnBar\(builder\);').Count
$calls = [regex]::Matches($writerSource, 'builder\.AddProgressBar\(').Count
Check ($calls -gt 0 -and $marks -eq $calls) "every own AddProgressBar call is counted as ours ($marks/$calls)"

# Native Add depends on a live Unity renderer; pin the inline-bar newline contract without starting the game.
foreach ($file in @('MachineBatteryInfo.cs', 'MachineHydroponicLine.cs')) {
    $text = Get-Content (Join-Path $repo "src/Features/Machines/$file") -Raw
    Check ($text -notmatch 'output\.Direct\(new TooltipLine\([\s\S]*?\)\);\s*output\.DirectNextLine\(\);') "$file does not append a break after inline-bar"
    Check ($text.Contains('output.RequireWidth(ProgressBarStyle.WidthForLine(')) "$file reserves complete line width"
}
$powerSource = Get-Content (Join-Path $repo 'src/Features/Machines/MachineBatteryInfo.cs') -Raw
Check ($powerSource.Contains('WidthForLine(headLength + 1 + EnergyBarLength + 3')) 'power width includes existing head, bar brackets and tail separator'
$effectSource = Get-Content (Join-Path $repo 'src/Features/Machines/ModuleEffectAnnotation.cs') -Raw
Check ($effectSource.Contains('+ ProgressBarStyle.VisibleLength(annotation)')) 'module width includes parenthesized annotation'
# 渲染器表同时承载归属组：排版方按 group 归类，新增区块不必让对方补映射。
$renderers = $entryType.GetField('Renderers',$flags).GetValue($null)
$groupField = $renderers[0].GetType().GetField('Group',$rw)
$groups = @($renderers | ForEach-Object { [string]$groupField.GetValue($_) })
Check (($groups -notcontains '') -and ($groups -notcontains $null)) 'every renderer declares a group'
$vocabulary = @($provider.GetType('ItemDetails.Integration.TooltipGroupIds').GetFields($flags) | ForEach-Object { [string]$_.GetValue($null) })
Check (@($groups | Where-Object { $vocabulary -notcontains $_ }).Count -eq 0) "renderer groups all come from the SDK vocabulary (actual: $($groups -join ','))"
Check (($vocabulary -contains 'liquid') -and ($vocabulary -contains 'appraisal') -and ($vocabulary -contains 'contents')) 'group vocabulary covers liquid, appraisal and contents'