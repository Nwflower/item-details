using System;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 机器电池电量：显示剩余能量与按当前每日耗能估算的可运行天数。
///
/// 位置要求：紧贴在游戏自己的「所需能量：X/天」段之后，而不是 tooltip 末尾。
/// 游戏先 Add("所需能量")，再 AddLine("X/天")；修改耗能时还会再 AddLine 基准值。
/// 因此这里跟踪 CreateMachineryTooltip 的构建，在能量行块结束时插入。
///
/// 电池按 role 为 power 的面板找（见 <see cref="MachineInterior.FindPowerSource"/>），
/// 只认普通电池；第三方机器面板顺序不同也不会把别的物品当成电池。
/// </summary>
internal static class MachineBatteryInfo
{
    private const string HarmonyId = "item_details.machine_battery";
    private static Harmony? _harmony;
    private static bool _patched;

    private static GameItem? _machine;
    private static bool _labelSeen;
    private static bool _inserted;
    private static int _energyLines;
    private static int _expectedEnergyLines;
    private static string _labelLocale = "";
    private static string _requiredLabel = "";

    internal static void Apply()
    {
        if (_patched) return;
        try
        {
            var machineTooltip = AccessTools.Method(typeof(MachineryHelper), "CreateMachineryTooltip",
                new[] { typeof(RichTextBuilder), typeof(GameItem) });
            var add = AccessTools.Method(typeof(RichTextBuilder), "Add", new[]
            {
                typeof(string), typeof(RenderHandler.ColorPalette), typeof(string), typeof(bool),
                typeof(bool), typeof(bool), typeof(bool), typeof(RenderHandler.ColorPalette),
                typeof(RenderHandler.ColorPalette), typeof(RenderHandler.ColorPalette)
            });
            var addLine = AccessTools.Method(typeof(RichTextBuilder), "AddLine", new[]
            {
                typeof(string), typeof(bool), typeof(RenderHandler.ColorPalette), typeof(bool),
                typeof(bool), typeof(bool), typeof(bool), typeof(RenderHandler.ColorPalette),
                typeof(RenderHandler.ColorPalette), typeof(RenderHandler.ColorPalette)
            });
            if (machineTooltip == null || add == null || addLine == null)
            {
                Log.Warn("MachineBattery.Apply", "未找到机器 tooltip 构建目标，电池行跳过");
                return;
            }

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(machineTooltip,
                prefix: new HarmonyMethod(typeof(MachineBatteryInfo), nameof(BeforeMachineTooltip)),
                postfix: new HarmonyMethod(typeof(MachineBatteryInfo), nameof(AfterMachineTooltip)));
            _harmony.Patch(add, prefix: new HarmonyMethod(typeof(MachineBatteryInfo), nameof(AddPrefix)));
            _harmony.Patch(addLine, postfix: new HarmonyMethod(typeof(MachineBatteryInfo), nameof(AddLinePostfix)));
            _patched = true;
            Log.Msg("MachineBatteryInfo: patched CreateMachineryTooltip/Add/AddLine");
        }
        catch (Exception ex)
        {
            Log.Error("MachineBattery.Apply", ex);
        }
    }

    internal static void Remove()
    {
        try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        _harmony = null;
        _patched = false;
        Reset();
    }

    private static void BeforeMachineTooltip(RichTextBuilder builder, GameItem item)
    {
        Reset();
        if (!ModSettings.MachineBattery || item == null) return;

        _machine = item;
        int drawn = GameRead.TagInt(item, "machinery_drawn_power") ?? 0;
        // 游戏在「所需能量」之后只调用一次 AddLine：
        //   耗能未变 → AddLine("X/天")；
        //   耗能变化 → Add("X/天") + AddLine("基准 Y/天")。
        // 两种情况的能量行块都结束在这次 AddLine，所以预期固定为 1。
        _expectedEnergyLines = drawn > 0 ? 1 : 0;
    }

    private static void AfterMachineTooltip()
    {
        Reset();
    }

    /// <summary>识别游戏单独 Add 的“所需能量”标签，随后开始计数能量行。</summary>
    private static void AddPrefix(string __0)
    {
        if (_machine == null || _inserted || _expectedEnergyLines <= 0 || _labelSeen) return;
        try
        {
            // 本地化标签可能带尾随空格；按 Trim 后比较，避免“所需能量： ”
            // 与“所需能量：”不相等而永远不触发插入。
            string text = (__0 ?? "").Trim();
            string label = RequiredLabel().Trim();
            if (label.Length > 0 && string.Equals(text, label, StringComparison.Ordinal))
                _labelSeen = true;
        }
        catch { }
    }

    private static void AddLinePostfix(RichTextBuilder __instance, string __0)
    {
        if (_machine == null || !_labelSeen || _inserted) return;
        _energyLines++;
        if (_energyLines < _expectedEnergyLines) return;

        _inserted = true;
        var line = Collect(_machine);
        if (line == null) return;
        try { __instance.AddLine(line.Value.Text, false, line.Value.Color); }
        catch (Exception ex) { Log.Warn("MachineBattery.Insert", ex); }
    }

    private static void Reset()
    {
        _machine = null;
        _labelSeen = false;
        _inserted = false;
        _energyLines = 0;
        _expectedEnergyLines = 0;
    }

    private static string RequiredLabel()
    {
        string locale = Loc.Code;
        if (_labelLocale == locale && _requiredLabel.Length > 0) return _requiredLabel;
        try
        {
            _requiredLabel = LocHelper.GetLocalizedMechanic("mech_machinery_required_energy") ?? "";
        }
        catch { _requiredLabel = ""; }
        _labelLocale = locale;
        return _requiredLabel;
    }

    /// <summary>剩余能量 + 按当前每日耗能向下取整的可运行天数。</summary>
    internal static TooltipLine? Collect(GameItem? item)
    {
        if (item == null) return null;
        try
        {
            if (!item.IsGameItemType("MACHINE")) return null;

            // 不用原版 GetBatterySlot（固定取第 0 个槽）：没有电池面板的机器第 0 格是投料口或模块格。
            // 转换器、超科技电池的能量标签恒为 0，按普通电池读会误报"0 天"。
            var battery = MachineInterior.FindPowerSource(item);
            if (!MachineInterior.IsPlainBattery(battery)) return null;

            int energy = GameRead.TagInt(battery, "power_source_item_energy") ?? 0;
            int drawn = GameRead.TagInt(item, "machinery_drawn_power") ?? 0;
            if (energy < 0) energy = 0;
            if (drawn <= 0) return null;

            int days = energy / drawn;
            var color = days >= 3 ? RenderHandler.ColorPalette.LightGreen
                      : days >= 1 ? RenderHandler.ColorPalette.LightYellow
                      : RenderHandler.ColorPalette.LightRed;

            return new TooltipLine(
                Loc.T("machine.battery_line", "Remaining energy: {0} ({1} days)", energy, days),
                color);
        }
        catch (Exception ex)
        {
            Log.Warn("MachineBattery.Collect", ex);
            return null;
        }
    }
}
