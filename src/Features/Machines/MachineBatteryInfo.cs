using System;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 机器能量行：把原版「所需能量：8/天 （基础：10/天）」与电池余量压成一行
///   `耗能：10 → 8/天（剩余12，可用1天）`
/// 基础值灰色；当前值比基础低绿、高红，不变时只写当前值；括号按可运行天数着色。
///
/// 原版在 CreateMachineryTooltip 里先 Add("所需能量")，耗能变化时再 Add("X/天")，
/// 最后 AddLine 结束能量块。这里跟踪这次构建：吞掉标签与数值段，在那次 AddLine 处改写出整行。
/// 原版在能量块前、以及后面模组加成/加工进度段前各写一行 AddLine(" ")：去掉前一行，
/// 保留后一行，让能量行与下面的段落隔开。
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
    private static bool _written;
    private static bool _leadBlankSkipped;
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
                Log.Warn("MachineBattery.Apply", "未找到机器 tooltip 构建目标，能量行改写跳过");
                return;
            }

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(machineTooltip,
                prefix: new HarmonyMethod(typeof(MachineBatteryInfo), nameof(BeforeMachineTooltip)),
                postfix: new HarmonyMethod(typeof(MachineBatteryInfo), nameof(AfterMachineTooltip)));
            _harmony.Patch(add, prefix: new HarmonyMethod(typeof(MachineBatteryInfo), nameof(AddPrefix)));
            _harmony.Patch(addLine, prefix: new HarmonyMethod(typeof(MachineBatteryInfo), nameof(AddLinePrefix)));
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
        // 与原版同一条件：耗能为 0 时不写能量块
        if ((GameRead.TagInt(item, "machinery_drawn_power") ?? 0) <= 0) return;
        _machine = item;
    }

    private static void AfterMachineTooltip()
    {
        Reset();
    }

    /// <summary>
    /// 吞掉能量块里的 Add：先是“所需能量”标签，耗能变化时还有一段“X/天”。
    /// AddLine 内部也走 Add，但能量块的那次 AddLine 已在 <see cref="AddLinePrefix"/> 被拦下。
    /// </summary>
    private static bool AddPrefix(string __0)
    {
        if (_machine == null || _written) return true;
        if (_labelSeen) return false;
        try
        {
            // 本地化标签可能带尾随空格；按 Trim 后比较，避免“所需能量： ”
            // 与“所需能量：”不相等而永远不触发改写。
            string text = (__0 ?? "").Trim();
            string label = RequiredLabel().Trim();
            if (label.Length > 0 && string.Equals(text, label, StringComparison.Ordinal))
            {
                _labelSeen = true;
                return false;
            }
        }
        catch { }
        return true;
    }

    /// <summary>
    /// 能量块以一次 AddLine 结束（“X/天”或“基础 Y/天”），在这里写出合并后的整行；
    /// 能量块前的那行空行直接丢掉。
    /// </summary>
    private static bool AddLinePrefix(RichTextBuilder __instance, string __0)
    {
        if (_machine == null || _written) return true;

        if (!_labelSeen)
        {
            bool blank = __0 != null && __0.Trim().Length == 0;
            if (blank && !_leadBlankSkipped && RequiredLabel().Trim().Length > 0)
            {
                _leadBlankSkipped = true;
                return false;
            }
            return true;
        }

        _written = true;   // 先置位：下面自己的 Add 不能再被吞
        try
        {
            WriteEnergyLine(__instance, _machine);
            return false;
        }
        catch (Exception ex)
        {
            // 标签已被吞掉，补回去再让原版这行照常写出
            Log.Warn("MachineBattery.Write", ex);
            try { __instance.Add(RequiredLabel(), RenderHandler.ColorPalette.LightCyan); } catch { }
            return true;
        }
    }

    private static void WriteEnergyLine(RichTextBuilder builder, GameItem machine)
    {
        int drawn = GameRead.TagInt(machine, "machinery_drawn_power") ?? 0;
        int baseDrawn = GameRead.TagInt(machine, "MACHINERY_DRAWN_POWER_BASE") ?? 0;
        string unit = Loc.T("machine.energy_unit", "/day");

        builder.Add(Loc.T("machine.energy_label", "Power: "), RenderHandler.ColorPalette.LightCyan);
        if (baseDrawn > 0 && baseDrawn != drawn)
        {
            builder.Add(baseDrawn + " → ", RenderHandler.ColorPalette.Gray);
            var color = drawn < baseDrawn
                ? RenderHandler.ColorPalette.LightGreen   // 比基础省电
                : RenderHandler.ColorPalette.LightRed;    // 比基础费电
            builder.Add(drawn + unit, color);
        }
        else
        {
            builder.Add(drawn + unit, RenderHandler.ColorPalette.LightCyan);
        }

        var battery = Collect(machine);
        if (battery != null) builder.Add(battery.Value.Text, battery.Value.Color);
        builder.NextLine();
    }

    private static void Reset()
    {
        _machine = null;
        _labelSeen = false;
        _written = false;
        _leadBlankSkipped = false;
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

    /// <summary>能量行末尾的括号：剩余能量 + 按当前每日耗能向下取整的可运行天数。</summary>
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
                Loc.T("machine.energy_battery", " ({0} left, lasts {1} {1|day|days})", energy, days),
                color);
        }
        catch (Exception ex)
        {
            Log.Warn("MachineBattery.Collect", ex);
            return null;
        }
    }
}
