#Requires -Version 7.4
# 离线夹具：游戏外用真实游戏程序集与 SelfTest 构建跑一遍"本模组渲染器 → builder → 快照 JSON → HTML"。
# 序列化与游戏内导出走同一段代码（TooltipSnapshot.WriteItem），只是片段的录制方式不同：
#   - 原版 Add 读 RenderHandler.current.windowUICanvas.scaleFactor（Unity 内部调用），游戏外会抛错；
#   - 游戏内用 Harmony 接 TextSlice.GetString，而游戏自带的 Harmony 在 .NET 9+ 宿主里打不了补丁。
# 所以这里用游戏自带的 DseCecil 把 Assembly-CSharp 复制一份并静态改写两处（游戏目录不动）：
#   1. 画布缩放读取换成常量 2（按 UI 缩放 > 1 处理，粗体保留）；
#   2. TextSlice.GetString 开头调用新加的静态字段 RichTextBuilder.ItemDetailsSnapshotHook。
# 产物：out/tooltip-preview/offline/snapshot.json 与 out/tooltip-preview/sites/offline-fixture/（均被忽略）。
param([Parameter(Mandatory)][string]$GameDir)
$ErrorActionPreference = 'Stop'
$env:PYTHONUTF8 = '1'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$managed = Join-Path (Resolve-Path -LiteralPath $GameDir).Path 'Probably Stolen_Data/Managed'
$outDir = Join-Path $repo 'out/tooltip-preview'
$offline = Join-Path $outDir 'offline'
$harness = Join-Path $offline 'harness'
New-Item -ItemType Directory -Force -Path $harness, $offline | Out-Null

function Check([bool]$condition, [string]$name) {
    if (!$condition) { throw "FAIL $name" }
    Write-Output "PASS $name"
}

$selfTestDll = Join-Path $repo 'bin/SelfTest/netstandard2.1/ItemDetails.dll'
Check (Test-Path -LiteralPath $selfTestDll) 'SelfTest build exists (run scripts/build.ps1 -Variant SelfTest first)'

# Unity 与框架依赖从游戏目录加载；Assembly-CSharp 必须是第一份被加载的副本，之后按名字绑定都落到它。
foreach ($name in @('UnityEngine.CoreModule.dll', 'UnityEngine.UIModule.dll', 'UnityEngine.UI.dll', 'Unity.TextMeshPro.dll',
        'UnityEngine.TextCoreFontEngineModule.dll', 'UnityEngine.InputLegacyModule.dll', 'Unity.ResourceManager.dll',
        'Unity.Localization.dll', '0Harmony.dll')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $managed $name))
}
$cecil = [Reflection.Assembly]::LoadFrom((Join-Path $managed 'DseCecil.dll'))
$frameworkRefs = @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName)
Add-Type -ReferencedAssemblies (@($cecil.Location) + $frameworkRefs) -TypeDefinition @'
using System;
using System.Linq;
using DseCecil;
using DseCecil.Cil;

namespace ItemDetailsSnapshotHarness
{
    public static class Patcher
    {
        // 短跳转在插入指令后可能越界：改写的方法先全部换成长跳转。
        static void LongBranches(MethodDefinition method)
        {
            foreach (var ins in method.Body.Instructions)
            {
                var op = ins.OpCode;
                if (op == OpCodes.Br_S) ins.OpCode = OpCodes.Br;
                else if (op == OpCodes.Brfalse_S) ins.OpCode = OpCodes.Brfalse;
                else if (op == OpCodes.Brtrue_S) ins.OpCode = OpCodes.Brtrue;
                else if (op == OpCodes.Beq_S) ins.OpCode = OpCodes.Beq;
                else if (op == OpCodes.Bge_S) ins.OpCode = OpCodes.Bge;
                else if (op == OpCodes.Bgt_S) ins.OpCode = OpCodes.Bgt;
                else if (op == OpCodes.Ble_S) ins.OpCode = OpCodes.Ble;
                else if (op == OpCodes.Blt_S) ins.OpCode = OpCodes.Blt;
                else if (op == OpCodes.Bne_Un_S) ins.OpCode = OpCodes.Bne_Un;
                else if (op == OpCodes.Bge_Un_S) ins.OpCode = OpCodes.Bge_Un;
                else if (op == OpCodes.Bgt_Un_S) ins.OpCode = OpCodes.Bgt_Un;
                else if (op == OpCodes.Ble_Un_S) ins.OpCode = OpCodes.Ble_Un;
                else if (op == OpCodes.Blt_Un_S) ins.OpCode = OpCodes.Blt_Un;
                else if (op == OpCodes.Leave_S) ins.OpCode = OpCodes.Leave;
            }
        }

        public static int Patch(string source, string target)
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(source));
            var asm = AssemblyDefinition.ReadAssembly(source, new ReaderParameters { ReadingMode = ReadingMode.Immediate, AssemblyResolver = resolver });
            var module = asm.MainModule;
            var builder = module.GetType("RichTextBuilder");
            int replaced = 0;
            foreach (var m in builder.Methods.Where(m => m.HasBody))
            {
                var ins = m.Body.Instructions;
                for (int i = 0; i + 2 < ins.Count; i++)
                {
                    var f0 = ins[i].Operand as FieldReference;
                    var f1 = ins[i + 1].Operand as FieldReference;
                    var m2 = ins[i + 2].Operand as MethodReference;
                    if (ins[i].OpCode != OpCodes.Ldsfld || f0 == null || f0.Name != "current" || f0.DeclaringType.Name != "RenderHandler") continue;
                    if (f1 == null || f1.Name != "windowUICanvas" || m2 == null || m2.Name != "get_scaleFactor") continue;
                    LongBranches(m);
                    var il = m.Body.GetILProcessor();
                    ins[i].OpCode = OpCodes.Ldc_R4;
                    ins[i].Operand = 2f;
                    il.Remove(ins[i + 2]);
                    il.Remove(ins[i + 1]);
                    replaced++;
                }
            }
            var action = module.ImportReference(typeof(Action<object>));
            var invoke = module.ImportReference(typeof(Action<object>).GetMethod("Invoke"));
            var hook = new FieldDefinition("ItemDetailsSnapshotHook", FieldAttributes.Public | FieldAttributes.Static, action);
            builder.Fields.Add(hook);
            var slice = builder.NestedTypes.First(t => t.Name == "TextSlice");
            var getString = slice.Methods.First(m => m.Name == "GetString");
            LongBranches(getString);
            var p = getString.Body.GetILProcessor();
            var first = getString.Body.Instructions[0];
            p.InsertBefore(first, p.Create(OpCodes.Ldsfld, hook));
            p.InsertBefore(first, p.Create(OpCodes.Brfalse, first));
            p.InsertBefore(first, p.Create(OpCodes.Ldsfld, hook));
            p.InsertBefore(first, p.Create(OpCodes.Ldarg_0));
            p.InsertBefore(first, p.Create(OpCodes.Callvirt, invoke));
            asm.Write(target);
            return replaced;
        }
    }
}
'@
$patched = Join-Path $harness 'Assembly-CSharp.dll'
$replaced = [ItemDetailsSnapshotHarness.Patcher]::Patch((Join-Path $managed 'Assembly-CSharp.dll'), $patched)
Check ($replaced -ge 1) "canvas scale read replaced in the harness copy ($replaced)"
$game = [Reflection.Assembly]::LoadFrom($patched)
Check ($game.Location -eq (Resolve-Path -LiteralPath $patched).Path) 'harness copy of Assembly-CSharp is the one loaded'
Check ($null -ne [RichTextBuilder].GetField('ItemDetailsSnapshotHook')) 'harness copy carries the slice hook'

$provider = [Reflection.Assembly]::LoadFrom($selfTestDll)
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$rw = [Reflection.BindingFlags]'NonPublic,Instance'
$snapshot = $provider.GetType('ItemDetails.TooltipSnapshot')
Check ($null -ne $snapshot) 'SelfTest build contains the snapshot serializer'
Check ($null -eq $snapshot.GetMethod('Bind', $flags).Invoke($null, @())) 'TextSlice fields and methods bind by name'
$offlineItem = $snapshot.GetMethod('OfflineItem', $flags)
$offlineDocument = $snapshot.GetMethod('OfflineDocument', $flags)
$writerType = $provider.GetType('ItemDetails.TooltipSections+Writer')
$entryType = $provider.GetType('ItemDetails.ModEntry')
$entryObject = [Activator]::CreateInstance($entryType)
$tp = $provider.GetType('ItemDetails.TooltipPipeline')
$breakdown = $provider.GetType('ItemDetails.ValueBreakdown')
$white = [RenderHandler+ColorPalette]::White
$turquoise = [RenderHandler+ColorPalette]::LightTurquoise

# 本模组在游戏内还会读的私有成员：改名会让快照悄悄少一块，这里先钉住。
$export = $provider.GetType('ItemDetails.TooltipSnapshotExport')
foreach ($pair in @(@('TypeBadge', 'Pending'), @('TypeBadge', '_textTemplate'), @('TypeBadge', '_badge'),
        @('ContentsGrid', '_pendingItem'), @('ContentsGrid', '_pendingContents'), @('ContentsGrid', '_pendingSealed'),
        @('ContentsGrid', '_pendingSealedEstimate'), @('ContentsGrid', '_template'), @('ProgressBarStyle', 'RichTextBars'),
        @('ManufacturerInline', '_pendingBuilder'), @('ManufacturerInline', '_pendingCompany'))) {
    $field = $provider.GetType("ItemDetails.$($pair[0])").GetField($pair[1], $flags)
    Check ($null -ne $field) "exporter reads $($pair[0]).$($pair[1])"
}
foreach ($name in @('ChipColors', 'IsWarning')) {
    Check ($null -ne $provider.GetType('ItemDetails.TypeBadge').GetMethod($name, $flags)) "exporter calls TypeBadge.$name"
}
Check ($null -ne [OverlayHandler].GetField('tooltipElement', [Reflection.BindingFlags]'NonPublic,Instance')) 'exporter reads OverlayHandler.tooltipElement'
Check ($null -ne $export) 'SelfTest build contains the in-game exporter'

function New-FixtureItem([string]$id, [object[]]$tags) {
    $item = [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject([GameItemElement])
    $item.identifier = $id
    $item.modifiedState = [TagSystem]::new()
    foreach ($pair in $tags) {
        $tag = [TagState]::new($pair[0], $pair[0])
        $tag.valueEnabled = $true
        if ($pair[1] -is [string]) { $tag.valueString = $pair[1] } else { $tag.valueInt = $pair[1] }
        $item.modifiedState.dict.Add($pair[0], $tag)
    }
    return $item
}
function New-Writer($builder, $item, [string]$id, [string]$group) {
    [Activator]::CreateInstance($writerType, $rw, $null, @($builder, $item, $id, $group), $null)
}
function Finish-Writer($writer) { [void]$writerType.GetMethod('Finish', $rw).Invoke($writer, @($false)) }
function Capture($builder, $item, [string]$label) { [string]$offlineItem.Invoke($null, @($builder, $item, $label)) }

$items = [System.Collections.Generic.List[string]]::new()

# 1) 机器速度 + 鉴定价值：与区块契约同一组标签；表头几行按原版写法手写（名字粗体白字、14px 空行、估价青字）。
$hand = New-FixtureItem 'fixture_hand_machine' @(@('BASE_PROCESSING_SPEED_TAG', 6), @('CURRENT_PROCESSING_SPEED_TAG', 8), @('APPRAISAL_ITEM', 1))
$tp.GetField('_current', $flags).SetValue($null, $hand)
$handTags = @{HAND_MATERIAL = 'CARBON_COMPOSITE'; HAND_INTERFACE = 'NEURAL'; HAND_CONDITION = 'NONE'; HAND_ADDON_ARMORED = 'ARMORED'; HAND_ADDON_BLADED = 'BLADED' }
$computed = $breakdown.GetMethod('ComputeHand', $flags).Invoke($null, @([Func[string, string]] { param($key) if ($handTags.ContainsKey($key)) { return $handTags[$key] } return $null }))
$breakdown.GetField('_memoResult', $flags).SetValue($null, $computed)
$breakdown.GetField('_memoItem', $flags).SetValue($null, $hand)
$tp.GetField('_buildId', $flags).SetValue($null, 7)
$breakdown.GetField('_memoBuild', $flags).SetValue($null, 7)
$b1 = [RichTextBuilder]::new(40)
[void]$b1.AddLine('Fixture Hand Unit', $true, $white, $true)
[void]$b1.AddEmptyLine(14)
[void]$b1.AddLine('Estimated value: 500, base value: 450', $true, $turquoise)
[void]$provider.GetType('ItemDetails.MachineSpeedLine').GetMethod('Write', $flags).Invoke($null, @($b1, $hand, 'Speed:'))
$w1 = New-Writer $b1 $hand 'value' 'appraisal'
[void]$entryType.GetMethod('TryAppendValue', $rw).Invoke($entryObject, @($w1, $hand))
Finish-Writer $w1
$items.Add((Capture $b1 $hand 'machine speed + appraisal'))
$tp.GetField('_current', $flags).SetValue($null, $null)

# 2) 进度条行（本模组 LineOutput → 原版 AddProgressBar）、中日韩行、超过折行预算的长描述。
$battery = New-FixtureItem 'fixture_battery' @(@('IS_BATTERY_POWERED_TAG', 1))
$lineType = $provider.GetType('ItemDetails.TooltipLine')
$barLine = [Activator]::CreateInstance($lineType, $rw, $null,
    @('Charge ', [single]0.62, 10, ' 62%', [RenderHandler+ColorPalette]::Green, [RenderHandler+ColorPalette]::DarkGreen), $null)
$lines = [Activator]::CreateInstance([System.Collections.Generic.List`1].MakeGenericType($lineType))
$lines.Add($barLine)
$appendLines = $provider.GetType('ItemDetails.LineOutput').GetMethod('AppendLines', $flags, $null,
    [type[]]@($writerType, [System.Collections.Generic.List`1].MakeGenericType($lineType)), $null)
$b2 = [RichTextBuilder]::new(40)
[void]$b2.AddLine('Fixture Battery Pack', $true, $white, $true)
[void]$b2.AddEmptyLine(14)
$w2 = New-Writer $b2 $battery 'machine-interior' 'machine'
[void]$appendLines.Invoke($null, @($w2, $lines))
Finish-Writer $w2
[void]$b2.AddLine('水质：优良（含少量化学污染物）', $true, [RenderHandler+ColorPalette]::LightCyan)
[void]$b2.AddLine('A rechargeable cell that keeps machines running through the night shift.', $true, $white)
[void]$b2.AddLine(' Ready in <1 day', $true, [RenderHandler+ColorPalette]::Gray, $false, $true)
$items.Add((Capture $b2 $battery 'bar + CJK + wrapped description'))

# 3) 动物成长倒计时：本模组追加器直接写原版 builder。
$rat = New-FixtureItem 'fixture_rat' @(@('ANIMAL_ITEM_TAG', 1), @('ANIMAL_SPECIES_TAG', [AnimalSpecies]::Rat.speciesID))
$b3 = [RichTextBuilder]::new(40)
[void]$b3.AddLine('Fixture Rat', $true, $white, $true)
$w3 = New-Writer $b3 $rat 'stage-progress' 'condition'
$wrote = [bool]$provider.GetType('ItemDetails.AnimalStageProgress').GetMethod('Append', $flags).Invoke($null, @($w3, $rat))
if ($wrote) { Finish-Writer $w3 }
Check $wrote 'stage progress renders for the fixture rat'
$items.Add((Capture $b3 $rat 'animal stage progress'))

$document = [string]$offlineDocument.Invoke($null, @([string[]]$items.ToArray(), 'offline', 'en'))
$snapshotPath = Join-Path $offline 'snapshot.json'
[IO.File]::WriteAllText($snapshotPath, $document, [Text.UTF8Encoding]::new($false))
Write-Output "SNAPSHOT $snapshotPath"

# ---------------- 快照形状 ----------------
$doc = $document | ConvertFrom-Json -Depth 64
Check ($doc.schema -eq 1) 'snapshot declares schema 1'
Check ($doc.meta.source -eq 'offline-fixture') 'snapshot meta names its source'
Check (@($doc.items).Count -eq 3) 'one record per captured item'
foreach ($it in $doc.items) {
    Check ($it.check.segmentsMatchBuild -eq $true) "$($it.id): recorded slices recompose the exact Build output"
    Check ($it.source -eq 'offline-fixture') "$($it.id): record names its source"
    Check (@($it.lines).Count -eq $it.layers.recommendedHeight) "$($it.id): one line per Build line"
    $naked = $it.layers.naked -split "`n"
    Check (@($naked).Count -eq @($it.lines).Count) "$($it.id): line count matches the naked text"
}
$first = $doc.items[0]
$title = $first.lines[0].segments[0]
Check ($title.text -eq 'Fixture Hand Unit' -and $title.bold -eq $true -and $title.color -eq '#c4c4b0' -and $title.colorName -eq 'White') 'name line keeps text, bold and palette color'
Check ($first.lines[1].spacer -eq 14 -and $first.lines[1].segments[1].text -like 'Estimated value*') 'AddEmptyLine(14) is recorded as a 14px spacer at the start of the next line'
Check (@($first.tags | Where-Object { $_.k -eq 'BASE_PROCESSING_SPEED_TAG' -and $_.i -eq 6 -and $_.on }).Count -eq 1) 'tag snapshot keeps keys and values'
Check (($first.lines | ForEach-Object { ($_.segments | ForEach-Object text) -join '' }) -contains 'Total: 500 | perfect 475-525') 'appraisal total from the mod renderer is in the lines'
$second = $doc.items[1]
$bar = @($second.lines | ForEach-Object { $_.segments } | Where-Object { $_.bar })
Check ($bar.Count -eq 1 -and $bar[0].bar.length -eq 10 -and $bar[0].bar.fill -eq 6 -and $bar[0].noBreak -and $bar[0].bar.recorded -eq $false) 'native AddProgressBar slice is recognised as a 10-cell bar with 6 filled'
$widths = @($second.lines | ForEach-Object { ($_.segments | Measure-Object -Property len -Sum).Sum })
Check (@($widths | Where-Object { $_ -gt 40 }).Count -eq 0) 'every Build line fits maxCharWidth 40 (no line was re-joined)'
Check (@($second.lines | Where-Object { ($_.segments.text -join '') -like 'A rechargeable*' }).Count -eq 1 -and
    @($second.lines | Where-Object { ($_.segments.text -join '') -like '*night shift.' }).Count -eq 1) 'long description is split by the native Build, not by the snapshot'
Check (@($second.lines | Where-Object { ($_.segments.text -join '') -eq ' Ready in <1 day' -and $_.segments[0].italic }).Count -eq 1) 'literal angle bracket text survives'

# ---------------- 渲染 ----------------
$site = Join-Path $outDir 'sites/offline-fixture'
& python -X utf8 -B (Join-Path $repo 'tests/TooltipPreview/build.py') $snapshotPath -o $site --title 'Item Details · 离线夹具'
if ($LASTEXITCODE -ne 0) { throw 'FAIL TooltipPreview/build.py did not build the offline site' }
$page = Get-Content -LiteralPath (Join-Path $site 'index.html') -Raw
Check ($page.Contains('data/index.js') -and -not $page.Contains('fixture_hand_machine')) 'HTML shell has no item data'
$data = (Get-ChildItem (Join-Path $site 'data') -Filter '*.js' -File | Get-Content -Raw) -join "`n"
Check ($data.Contains('fixture_hand_machine') -and $data.Contains('fixture_battery') -and $data.Contains('fixture_rat')) 'data chunks have a card per fixture item'
Check ($data.Contains('offline-fixture')) 'data chunks show their source'
Write-Output "HTML $site/index.html"
