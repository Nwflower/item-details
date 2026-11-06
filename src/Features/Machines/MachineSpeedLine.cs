using System;
using ItemDetails.Integration;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 机器作业速度行：把原版「作业速度：9/天 （基础：8/天）」改成和耗能行一样的写法
///   `作业速度：8 → 9/天`
/// 基础值灰色；当前值比基础快绿、慢红（与耗能相反：速度越高越好），不变时只写当前值。
/// 标签沿用原版文案，不另起词条。
///
/// 原版在 CreateMachineryTooltip 里先 Add(速度标签)，速度有变化时再 Add("X/天")，
/// 最后 AddLine 结束这一行。这里跟踪这次构建：吞掉标签与数值段，在那次 AddLine 处写出整行。
/// 写出失败时补回标签，让原版那一行照常输出。归「提示框文字优化」开关。
/// </summary>
internal static class MachineSpeedLine
{

    private static GameItem? _machine;
    private static string? _label;   // 吞掉的原版标签原文；非空表示正在等这一行的 AddLine
    private static bool _written;
    private static string _labelLocale = "";
    private static string _speedLabel = "";

    internal static void Install(PatchScope scope)
    {
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
                Log.Warn("MachineSpeed.Apply", "未找到机器 tooltip 构建目标，速度行改写跳过");
                return;
            }

            scope.Patch(machineTooltip,
                prefix: new HarmonyMethod(typeof(MachineSpeedLine), nameof(BeforeMachineTooltip)),
                finalizer: new HarmonyMethod(typeof(MachineSpeedLine), nameof(AfterMachineTooltip)));
            scope.Patch(add, prefix: new HarmonyMethod(typeof(MachineSpeedLine), nameof(AddPrefix)));
            scope.Patch(addLine, prefix: new HarmonyMethod(typeof(MachineSpeedLine), nameof(AddLinePrefix)));
            Log.Msg("MachineSpeedLine: patched CreateMachineryTooltip/Add/AddLine");
        }
        catch (Exception ex)
        {
            Log.Error("MachineSpeed.Apply", ex);
        }
        scope.OnDispose(Remove);
    }

    internal static void Remove()
    {
        Reset();
    }

    private static void BeforeMachineTooltip(GameItem item)
    {
        Reset();
        if (!ModSettings.TooltipFormatting || item == null) return;
        // 与原版同一条件：只有进度型机器写速度行
        if (!GameRead.IsTag(item, "PROGRESS_TYPE_MACHINE_TAG")) return;
        if (SpeedLabel().Trim().Length == 0) return;
        _machine = item;
    }

    private static void AfterMachineTooltip()
    {
        Reset();
    }

    /// <summary>吞掉速度标签，以及速度有变化时紧跟其后的「X/天」那段。</summary>
    private static bool AddPrefix(string __0)
    {
        if (_machine == null || _written) return true;
        if (_label != null) return false;
        try
        {
            // 本地化标签可能带尾随空格，按 Trim 后比较
            if (string.Equals((__0 ?? "").Trim(), SpeedLabel().Trim(), StringComparison.Ordinal))
            {
                _label = __0 ?? "";
                return false;
            }
        }
        catch { }
        return true;
    }

    /// <summary>速度行以一次 AddLine 结束（「X/天」或「基础 Y/天」），在这里写出改好的整行。</summary>
    private static bool AddLinePrefix(RichTextBuilder __instance)
    {
        if (_machine == null || _written || _label == null) return true;

        _written = true;   // 先置位：下面自己的 Add 不能再被吞
        try
        {
            Write(__instance, _machine, _label);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("MachineSpeed.Write", ex);
            try { __instance.Add(_label, RenderHandler.ColorPalette.LightCyan); } catch { }
            return true;
        }
    }

    private static void Write(RichTextBuilder builder, GameItem machine, string label)
    {
        int baseSpeed = GameRead.TagInt(machine, "BASE_PROCESSING_SPEED_TAG")
            ?? throw new InvalidOperationException("读不到基础速度");
        int speed = GameRead.TagInt(machine, "CURRENT_PROCESSING_SPEED_TAG")
            ?? throw new InvalidOperationException("读不到当前速度");
        string unit = Loc.T("machine.energy_unit", "/day");

        // 中文标签以全角冒号收尾，不再补空格；西文「Speed:」后补一个空格，与耗能行一致。
        string head = label.TrimEnd();
        if (head.Length > 0 && head[head.Length - 1] != '：') head += " ";

        var output = new TooltipSections.Writer(builder, machine, "machine-speed", TooltipGroupIds.Machine);
        output.Field("basePerDay", baseSpeed);
        output.Field("currentPerDay", speed);
        output.Add(head, RenderHandler.ColorPalette.LightCyan);
        if (baseSpeed != speed)
        {
            output.Add(baseSpeed + " → ", RenderHandler.ColorPalette.Gray);
            var color = speed > baseSpeed
                ? RenderHandler.ColorPalette.LightGreen   // 比基础快
                : RenderHandler.ColorPalette.LightRed;    // 比基础慢
            output.Add(speed + unit, color);
        }
        else
        {
            output.Add(speed + unit, RenderHandler.ColorPalette.LightCyan);
        }
        output.NextLine();
        output.Emit();
    }

    private static void Reset()
    {
        _machine = null;
        _label = null;
        _written = false;
    }

    private static string SpeedLabel()
    {
        string locale = Loc.Code;
        if (_labelLocale == locale && _speedLabel.Length > 0) return _speedLabel;
        try
        {
            _speedLabel = LocHelper.GetLocalizedMechanic("mech_machinery_progress_speed") ?? "";
        }
        catch { _speedLabel = ""; }
        _labelLocale = locale;
        return _speedLabel;
    }
}
