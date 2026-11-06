#Requires -Version 7.4
param([Parameter(Mandatory)][string]$GameDir, [switch]$Update)
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
    // 契约样例：区块 id、group 字段、其余字段（按名字排序）与每段 run 的形状。
    // 数字一律按不变文化格式化，跨机器与区域设置可比。
    // 字段值只写形状：标量按不变文化格式化，集合等复杂值只记类型名，读法由 SDK 的类型化访问器负责。
    private static string Fmt(object v)
    {
        if (v == null) return "null";
        if (v is string s) return s;
        if (v is bool b) return b ? "true" : "false";
        if (v is System.IFormattable f) return f.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
        return "<" + v.GetType().Name + ">";
    }
    public static string Canonical(RichTextBuilder builder)
    {
        if (!ItemDetailsClient.TryGetTooltipSections(builder, out var sections)) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var s in sections)
        {
            var keys = new System.Collections.Generic.List<string>();
            foreach (var f in s.Fields) keys.Add(f.Key);
            keys.Sort(string.CompareOrdinal);
            sb.Append(s.Id).Append('|');
            foreach (var k in keys) sb.Append(k).Append('=').Append(Fmt(s.Fields[k])).Append(';');
            sb.Append('|');
            foreach (var r in s.Runs)
                sb.Append(r.Kind).Append(':').Append(r.Text).Append(':').Append(r.Color).Append(':')
                  .Append(r.Ratio.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                  .Append(r.Length).Append(':').Append(r.Background).Append(':').Append(r.Tail).Append(';');
            sb.Append('\n');
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

    # 契约样例：固定物品的快照逐字比对，双方共用同一份文件测归类。
    $fixture = Join-Path $repo 'tests/fixtures/sections/contract.txt'
    $canonical = $probe::Canonical($first)
    if ($Update) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $fixture) | Out-Null
        [IO.File]::WriteAllText($fixture, $canonical.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
        Write-Output "UPDATED $fixture"
    }
    else {
        Check (Test-Path $fixture) 'section contract fixture exists (run with -Update to create it)'
        $expected = ([IO.File]::ReadAllText($fixture)).Replace("`r`n", "`n")
        Check ($canonical -eq $expected) "section snapshots match the frozen contract fixture$(if ($canonical -ne $expected) { "`n--- expected ---`n$expected--- actual ---`n$canonical" })"
    }

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
# 渲染层片段替换：注解按片段对象身份挂，构建前换入、构建后还原；片段被挪动或重建就不换。
$slicesType = $provider.GetType('ItemDetails.RenderSlices')
$sliceField = [RichTextBuilder].GetField('slices', [Reflection.BindingFlags]'NonPublic,Instance')
$slicesType.GetField('_slices',$flags).SetValue($null,$sliceField)
$sliceType = [RichTextBuilder].GetNestedType('TextSlice','NonPublic')
function New-Slice { [Activator]::CreateInstance($sliceType, [Reflection.BindingFlags]'Public,NonPublic,Instance', $null, @($false), $null) }
$pieces = $slicesType.GetMethod('Pieces',$flags)
Check ((($pieces.Invoke($null,@('a')),$pieces.Invoke($null,@("a`nb")),$pieces.Invoke($null,@("a`n")),$pieces.Invoke($null,@(''))) -join ',') -eq '1,3,2,0') 'piece count follows the native Add split'
$alt = New-Slice
$makeField = $slicesType.GetField('MakeSlices',$flags)
$makeField.SetValue($null, ([scriptblock]{ param($t,$s) ,[object[]]@($alt) }) -as $makeField.FieldType)
$styleType = $provider.GetType('ItemDetails.RenderSlices+Style')
$style = [Activator]::CreateInstance($styleType, $rw, $null, @([RenderHandler+ColorPalette]::None,$false,$false,$false,$false,[RenderHandler+ColorPalette]::None,[RenderHandler+ColorPalette]::None,[RenderHandler+ColorPalette]::None), $null)
$annotate = $slicesType.GetMethod('Annotate',$flags)
$before = $slicesType.GetMethod('BeforeBuild',$flags)
$after = $slicesType.GetMethod('AfterBuild',$flags)
$sb = [RichTextBuilder]::new(40)
$list = $sliceField.GetValue($sb)
$s0 = New-Slice; $s1 = New-Slice; $s2 = New-Slice
$list.Add($s0); $list.Add($s1)
$annotate.Invoke($null,@($sb, 1, "水`u{3000}1 mL`u{3000}50%", $style, 'pos-row'))
$list.Add($s2)
$state = [object[]]@($sb, $null)
$before.Invoke($null,$state)
Check ($state[1] -ne $null -and [object]::ReferenceEquals($list[1],$alt) -and $list.Count -eq 3) 'annotated slice is swapped in before Build'
[void]$after.Invoke($null,@($sb, $state[1], $null))
Check ([object]::ReferenceEquals($list[1],$s1) -and [object]::ReferenceEquals($list[2],$s2)) 'builder is restored to canonical slices after Build'
# 第三方重排：Clear 后写入新片段，注解不再命中。
$sb.Clear(); $list.Add((New-Slice))
$state = [object[]]@($sb, $null); $before.Invoke($null,$state)
Check ($state[1] -eq $null) 'relayout with fresh slices leaves the text alone'
# 描述：只有被别人的条放宽预算时才折；离线没有字宽缩放，所以不换。
$sb2 = [RichTextBuilder]::new(40); $l2 = $sliceField.GetValue($sb2); $d = New-Slice; $l2.Add($d)
$annotate.Invoke($null,@($sb2, 0, ('长' * 60), $style, $null))
$state = [object[]]@($sb2, $null); $before.Invoke($null,$state)
Check ($state[1] -eq $null -and [object]::ReferenceEquals($l2[0],$d)) 'description stays canonical when the budget is not widened'

# 只换写法的行：正文留原版，原版窗口构建时换成显示写法，Build 后还原。
$sb3 = [RichTextBuilder]::new(40); $l3 = $sliceField.GetValue($sb3)
$v0 = New-Slice; $v1 = New-Slice; $l3.Add($v0); $l3.Add($v1)
$annotate.Invoke($null, @($sb3, 1, '预估价值: 1, 基础价值: 2', $style, '售价: 1'))
$state = [object[]]@($sb3, $null); $before.Invoke($null,$state)
Check ($state[1] -ne $null -and $l3.Count -eq 2 -and [object]::ReferenceEquals($l3[0],$v0) -and [object]::ReferenceEquals($l3[1],$alt)) 'native line is swapped for its display text'
[void]$after.Invoke($null,@($sb3, $state[1], $null))
Check ([object]::ReferenceEquals($l3[1],$v1)) 'native line slice comes back after Build'
# 带信息的行必须写进正文并回放：区块 runs 与 builder 文本是同一份，重排方不会丢数据。
$tpSource = Get-Content (Join-Path $repo 'src/Presentation/Tooltip/TooltipPipeline.cs') -Raw
foreach ($id in @('price', 'calories')) {
    $m = [regex]::Match($tpSource, "new TooltipSections\.Writer\([^)]*""$id""[^)]*\)")
    Check ($m.Success) "section $id is produced by the tooltip pipeline"
    $tail = $tpSource.Substring($m.Index, [Math]::Min(1600, $tpSource.Length - $m.Index))
    Check ($tail.Contains('.Emit()')) "section $id is replayed into the builder, not only annotated"
}
Check ((Get-Content (Join-Path $repo 'src/Features/Machines/ModuleEffectAnnotation.cs') -Raw).Contains('output.Emit();')) 'machine-effect is replayed into the builder'
$slicesType.GetField('_slices',$flags).SetValue($null,$null)
