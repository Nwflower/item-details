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
# Deliberately never read sdk/ here: old consumers must work without replacing their client.
$source = Get-Content (Join-Path $repo 'tests/fixtures/legacy-v1/ItemDetailsClient.cs') -Raw
$source += @'

public static class LegacyProbe
{
    public static IDisposable Watch() => ItemDetailsClient.WatchTooltipSections();
    public static IDisposable Claim() => ItemDetailsClient.RegisterTooltipSections((item, builder, section) => section.Id == "value");
    public static IDisposable Layout(int? options) => ItemDetailsClient.RegisterTooltipLayout(item => (TooltipLayoutOptions?)options);
    public static IDisposable ThrowingLayout() => ItemDetailsClient.RegisterTooltipLayout(item => throw new InvalidOperationException("legacy test"));
    public static int AnchorCalls;
    public static IDisposable Anchor() => ItemDetailsClient.RegisterTooltipAnchor(() => { AnchorCalls++; return null; });
    public static bool Read(RichTextBuilder builder, bool replace)
    {
        if (!ItemDetailsClient.TryGetTooltipSections(builder, out var sections) || sections.Count != 1) return false;
        var section = sections[0];
        return section.Id == "value" && section.CanReplace == replace && section.Runs.Count == 2 &&
            section.Runs[0].Kind == "line" && section.Runs[0].Text == "old consumer" &&
            section.Runs[0].Color == RenderHandler.ColorPalette.LightCyan &&
            section.Runs[1].Kind == "break" &&
            section.TryGet<int>("total", out var total) && total == 123;
    }
    public static bool Available(RichTextBuilder builder) => ItemDetailsClient.TryGetTooltipSections(builder, out _);
}
'@
Add-Type -TypeDefinition $source -ReferencedAssemblies ($refs + @(Get-ChildItem "$PSHOME/ref/*.dll" | ForEach-Object FullName))
function Check([bool]$condition, [string]$name) {
    if (!$condition) { throw "FAIL $name" }
    Write-Output "PASS $name"
}
$client = [ItemDetails.Integration.LegacyProbe]
Check (!$client::Available([RichTextBuilder]::new(40))) 'frozen consumer works before producer load'
$provider = [Reflection.Assembly]::LoadFrom((Join-Path $repo 'bin/Release/netstandard2.1/ItemDetails.dll'))
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$inst = [Reflection.BindingFlags]'NonPublic,Instance'
$legacy = $provider.GetType('ItemDetails.LegacySectionedTooltips').GetMethod('YieldsContentsGrid', $flags)
$mod = [LoadedMod]::new()
$mod.Manifest = [ModManifest]::new()
$mod.Manifest.ID = 'amres.sectioned_tooltips'
$mod.Manifest.ModVersion = '1.6.2'
# 0.46D 的补丁起 ModLoader.Mods 是只读快照（`=> new List<LoadedMod>(mods)`），
# 往它上面 Add 只会写进一个临时副本；伪造的 mod 必须落到加载器自己的列表里。
$liveMods = [ModLoader]::Mods   # 先触发静态构造：反射读静态字段不会跑 cctor，未初始化时读到 null
$modsField = [ModLoader].GetField('mods', $flags)
# 不要写成 `$fakeMods = if (...) { ... }`：if 会把空列表枚举成 $null。
if ($modsField) { $fakeMods = $modsField.GetValue($null) } else { $fakeMods = $liveMods }
if ($null -eq $fakeMods) { $fakeMods = $liveMods }
if ($null -eq $fakeMods) { $fakeMods = [Collections.Generic.List[LoadedMod]]::new() }
$fakeMods.Add($mod)
$configs = [ModConfig].GetField('configs', $flags).GetValue($null)
$config = [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject([ModConfig])
$entries = [Collections.Generic.List[ConfigEntry]]::new()
[ModConfig].GetField('entries', $inst).SetValue($config, $entries)
try {
    Check (!$legacy.Invoke($null, @($false))) 'disabled legacy mod does not hide our grid'
    $mod.IsEnabled = $true
    Check ($legacy.Invoke($null, @($false))) '1.6.2 without protocol keeps the promised default panel ownership'
    Check (!$legacy.Invoke($null, @($true))) 'legacy panel never takes sealed-box inspection'
    $mod.Manifest.ModVersion = '1.6.3'
    Check ($legacy.Invoke($null, @($false))) '1.6.3 with unchanged panel settings remains supported'
    foreach ($version in @('1.6.4', '2.0.0', '', 'unknown')) {
        $mod.Manifest.ModVersion = $version
        Check (!$legacy.Invoke($null, @($false))) "unknown or protocol-era version is not guessed: $version"
    }
    $mod.Manifest.ModVersion = '1.6.2'
    $mod.Manifest.ID = 'another.mod'
    Check (!$legacy.Invoke($null, @($false))) 'unrelated mods are not treated as legacy consumers'
    $mod.Manifest.ID = 'amres.sectioned_tooltips'
    $configs.Add($mod.Manifest.ID, $config)
    foreach ($key in @('ContentsPanel', 'Enabled')) {
        $off = [Activator]::CreateInstance([ConfigEntry[bool]], $inst, $null, @($false), $null)
        [ConfigEntry].GetProperty('Key').SetValue($off, $key)
        $entries.Add($off)
        Check (!$legacy.Invoke($null, @($false))) "legacy $key=false restores our grid"
        $entries.Clear()
        Check ($legacy.Invoke($null, @($false))) "legacy $key default restores ownership on the next query"
    }
    foreach ($options in @($null, 0, 3, 7)) {
        $hook = $client::Layout($options)
        try { Check (!$legacy.Invoke($null, @($false))) "explicit v1 layout overrides config fallback, including null/None: $options" }
        finally { $hook.Dispose() }
    }
    $hook = $client::ThrowingLayout()
    try { Check (!$legacy.Invoke($null, @($false))) 'a faulty explicit provider does not reactivate legacy guessing' }
    finally { $hook.Dispose() }
    $third = $provider.GetType('ItemDetails.ThirdPartyProviders')
    $register = $third.GetMethods($flags) | Where-Object { $_.Name -eq 'RegisterTooltip' -and $_.GetParameters().Count -eq 1 }
    $callbackType = $register.GetParameters()[0].ParameterType
    $callback = { param($item) 0 } -as $callbackType
    $hook = $register.Invoke($null, @($callback))
    try { Check (!$legacy.Invoke($null, @($false))) 'direct per-item API declining contents also overrides legacy config' }
    finally { $hook.Dispose() }
    Check ($legacy.Invoke($null, @($false))) 'disposing explicit consumers restores old panel compatibility'

    $item = [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject([GameItemElement])
    $item.modifiedState = [TagSystem]::new()
    $suppression = $third.GetMethod('TooltipSuppressions', $flags).Invoke($null, @($item))
    Check ($suppression.ToString() -eq 'None') 'legacy grid fallback does not claim Contents, compression or machine information'
    $draws = $provider.GetType('ItemDetails.SectionedLayout').GetMethod('DrawsContents', $flags)
    foreach ($options in @(3, 7)) {
        $hook = $client::Layout($options)
        try { Check ($draws.Invoke($null, @($item)) -eq ($options -eq 7)) "frozen layout flags retain their contents ownership meaning: $options" }
        finally { $hook.Dispose() }
    }
    Check (!$draws.Invoke($null, @($item))) 'disposing frozen layout withdraws its contents claim'
}
finally {
    [void]$configs.Remove('amres.sectioned_tooltips')
    $null = $fakeMods.Remove($mod)
}
Check (!$legacy.Invoke($null, @($false))) 'removing the old mod restores our grid'

$sections = $provider.GetType('ItemDetails.TooltipSections')
$writerType = $provider.GetType('ItemDetails.TooltipSections+Writer')
$addLine = $writerType.GetMethod('AddLine', $inst, $null, [Type[]]@([string], [RenderHandler+ColorPalette]), $null)
$tp = $provider.GetType('ItemDetails.TooltipPipeline')
$sections.GetMethod('Enable', $flags).Invoke($null, @())
$watch = $client::Watch()
$claim = $null
try {
    $builder = [RichTextBuilder]::new(40)
    $writer = [Activator]::CreateInstance($writerType, $inst, $null, @($builder, $item, 'value', 'appraisal'), $null)
    [void]$writerType.GetMethod('Field', $inst).MakeGenericMethod([int]).Invoke($writer, @('total', 123))
    [void]$addLine.Invoke($writer, @('old consumer', [RenderHandler+ColorPalette]::LightCyan))
    [void]$writerType.GetMethod('NextLine', $inst).Invoke($writer, @())
    # Observe records real text without calling Unity rendering in this offline host.
    [void]$writerType.GetMethod('Finish', $inst).Invoke($writer, @($true))
    Check ($client::Read($builder, $false)) 'frozen SDK reads v1 ids, runs, colors and fields while ignoring new group metadata'
    $claim = $client::Claim()
    $tp.GetField('_buildId', $flags).SetValue($null, 1)
    $builder = [RichTextBuilder]::new(40)
    $writer = [Activator]::CreateInstance($writerType, $inst, $null, @($builder, $item, 'value', 'appraisal'), $null)
    [void]$writerType.GetMethod('Field', $inst).MakeGenericMethod([int]).Invoke($writer, @('total', 123))
    [void]$addLine.Invoke($writer, @('old consumer', [RenderHandler+ColorPalette]::LightCyan))
    [void]$writerType.GetMethod('NextLine', $inst).Invoke($writer, @())
    Check ($writerType.GetMethod('Finish', $inst).Invoke($writer, @($false))) 'frozen display delegate still claims its section'
    Check ($client::Read($builder, $true)) 'frozen consumer still reads a claimed section'
    $slices = [RichTextBuilder].GetField('slices', $inst)
    Check ($slices.GetValue($builder).Count -eq 0) 'old consumer takeover does not duplicate native output'
    $claim.Dispose(); $claim = $null
    $watch.Dispose()
    $builder = [RichTextBuilder]::new(40)
    $writer = [Activator]::CreateInstance($writerType, $inst, $null, @($builder, $item, 'value'), $null)
    [void]$writerType.GetMethod('NextLine', $inst).Invoke($writer, @())
    [void]$writerType.GetMethod('Finish', $inst).Invoke($writer, @($false))
    Check ($slices.GetValue($builder).Count -eq 1 -and !$client::Available($builder)) 'old consumer disposal restores native output exactly once'
    $anchor = $client::Anchor()
    try {
        Check (!$third.GetMethod('TryTooltipAnchor', $flags).Invoke($null, @($null)) -and $client::AnchorCalls -eq 1) 'frozen anchor callback remains callable and null falls back'
    }
    finally { $anchor.Dispose() }
}
finally {
    if ($claim) { $claim.Dispose() }
    $watch.Dispose()
    $sections.GetMethod('Disable', $flags).Invoke($null, @())
}
Check (!$client::Available($builder)) 'producer disable withdraws old endpoint cleanly'
# The legacy consumer finds this exact sibling name. Unity visibility/geometry still needs in-game QA.
$badge = $provider.GetType('ItemDetails.TypeBadge')
Check ($badge.GetField('BadgeName', $flags).GetRawConstantValue() -eq 'ItemDetailsTypeBadge') 'legacy badge node name remains stable without adopting the new slot'
