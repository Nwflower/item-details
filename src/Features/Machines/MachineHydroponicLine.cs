using System;
using ItemDetails.Integration;
using HarmonyLib;
using UnityEngine;

namespace ItemDetails;

/// <summary>
/// 水培机状态与进度行改写：
/// 将原版分散在多行的「状态: 生长中……」与「[进度条] 16/43」以及预计工期整合成紧凑单行：
/// 作业速度行可用时，进度条与预计工期直接并进那一行（见 <see cref="MachineSpeedLine"/>），这里不再写；
/// 速度行用不了（开关、写出失败）时仍按下面的单行写：
///   正常时：`状态: [进度条] 16/28 预计2天`
///   缺水时：`状态: [进度条] 16/43 预计4天，水量不足`
///   断电时：`状态: [进度条] 16/43 预计4天，电力不足`
/// 避免原版状态行与模组 ETA、暂停原因多行重复。
/// </summary>
internal static class MachineHydroponicLine
{

    internal static void Install(PatchScope scope)
    {
        try
        {
            var target = AccessTools.Method(typeof(MachineHydroponic), "CreateMachineTooltip",
                new[] { typeof(RichTextBuilder), typeof(GameItem) });
            if (target == null)
            {
                Log.Warn("MachineHydroponicLine.Apply", "未找到 MachineHydroponic.CreateMachineTooltip，水培状态行改写跳过");
                return;
            }

            scope.Patch(target, prefix: new HarmonyMethod(typeof(MachineHydroponicLine), nameof(Prefix)));
            scope.Patch(AccessTools.Method(typeof(MachineProgressHelper), "CreateMachineTooltip",
                    new[] { typeof(RichTextBuilder), typeof(GameItem) }),
                prefix: new HarmonyMethod(typeof(MachineHydroponicLine), nameof(DefaultStatusPrefix)));
            Log.Msg("MachineHydroponicLine: patched MachineHydroponic.CreateMachineTooltip");
        }
        catch (Exception ex)
        {
            Log.Error("MachineHydroponicLine.Apply", ex);
        }
    }

    /// <summary>用原版默认「状态 + 进度条」写法的进度型机器（水培机有自己的写法，不在此列）。</summary>
    internal static bool IsDefaultProgressMachine(GameItem item)
        => item.IsTag("PROGRESS_TYPE_MACHINE_TAG") && item.IsTag("PROGRESS_TYPE_DEFAULT_TOOLTIP");

    /// <summary>作业速度行已经带上进度条与工期：原版默认的「状态 + 进度条」那几行不再写。</summary>
    private static bool DefaultStatusPrefix(GameItem item)
    {
        try { return !MachineSpeedLine.AbsorbsStatus(item); }
        catch { return true; }
    }

    private static bool Prefix(RichTextBuilder builder, GameItem item)
    {
        try
        {
            if (!ModSettings.TooltipFormatting || !ModSettings.Machines || item == null) return true;
            if (!item.IsTag("HYDROPONIC_MACHINE_TAG")) return true;

            // 只有工作状态进行单行合并改写；就绪状态保留原版显示
            if (!string.Equals(MachineProgressHelper.GetMachineState(item), "STATE_WORKING", StringComparison.Ordinal))
            {
                return true;
            }

            // 作业速度行已经带上了进度条与预计工期：这里不再另起「状态」行
            if (MachineSpeedLine.AbsorbsStatus(item)) return false;

            WriteHydroponicLine(builder, item);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("MachineHydroponicLine.Prefix", ex);
            return true;
        }
    }

    private const int BarFullLength = 10;
    private const int BarMinLength = 6;

    /// <summary>
    /// 水培机工作中的状态：进度比例、尾部文字（「16/43 预计4天，水量不足」）与状态色。
    /// 不是水培机、不在工作、或对应开关关闭时返回 false。作业速度行与单独的状态行共用这一份。
    /// <paramref name="compact"/>：并进作业速度行时写成「24/28（1天）」，缺水不再写原因
    /// （下面的水量行已经说明），状态色仍变红；缺电的原因照写。
    /// </summary>
    internal static bool TryStatus(GameItem item, bool compact, out float ratio, out string tail, out RenderHandler.ColorPalette color)
    {
        ratio = 0f;
        tail = "";
        color = RenderHandler.ColorPalette.LightGreen;
        if (!ModSettings.TooltipFormatting || !ModSettings.Machines || item == null) return false;
        bool hydro = item.IsTag("HYDROPONIC_MACHINE_TAG");
        if (!hydro && !(compact && IsDefaultProgressMachine(item))) return false;
        if (!string.Equals(MachineProgressHelper.GetMachineState(item), "STATE_WORKING", StringComparison.Ordinal))
            return false;

        var (cur, target) = MachineProgressHelper.GetProgress(item);
        int speed = item.GetTagReadonly("CURRENT_PROCESSING_SPEED_TAG")?.GetInt() ?? 0;

        // 只有水培机有用水限制；其它进度型机器只看供电
        int waterDays = hydro ? GetWaterDays(item) : -1;
        bool canPower = MachineHelper.CanPower(item);
        bool canWater = !hydro || waterDays >= 1;

        int daysNeeded = (speed > 0 && target > cur)
            ? (target - cur + speed - 1) / speed
            : 0;

        string etaText;
        if (compact)
        {
            string days = daysNeeded > 0
                ? Loc.T("hydro.progress", "{0}/{1} ({2} {2|day|days})", cur, target, daysNeeded)
                : cur + "/" + target;
            tail = !canPower ? days + " " + LocHelper.GetLocalizedMechanic("mech_hydroponic_status_no_power") : days;
            color = !canPower || !canWater ? RenderHandler.ColorPalette.LightRed : RenderHandler.ColorPalette.LightGreen;
            ratio = (float)cur / Mathf.Max(1, target);
            return true;
        }

        if (!canPower || !canWater)
        {
            string reason = !canPower && !canWater
                ? LocHelper.GetLocalizedMechanic("mech_hydroponic_status_no_water_power")
                : !canPower
                    ? LocHelper.GetLocalizedMechanic("mech_hydroponic_status_no_power")
                    : LocHelper.GetLocalizedMechanic("mech_hydroponic_status_no_water");

            etaText = daysNeeded > 0
                ? Loc.T("hydro.eta_paused", "est. {0} {0|day|days}, {1}", daysNeeded, reason)
                : reason;
            color = RenderHandler.ColorPalette.LightRed;
        }
        else
        {
            string growing = LocHelper.GetLocalizedMechanic("mech_hydroponic_status_growing");
            etaText = daysNeeded > 0
                ? Loc.T("hydro.eta_growing", "est. {0} {0|day|days}, {1}", daysNeeded, growing)
                : growing;
            color = RenderHandler.ColorPalette.LightGreen;
        }

        ratio = (float)cur / Mathf.Max(1, target);
        tail = cur + "/" + target + " " + etaText;
        return true;
    }

    /// <summary>条长随整行文字长度变化：前面的文字加尾部越长，条越短，整行不会长出一大截。</summary>
    internal static int BarLength(int headWidth, string tail, int baseWidth, int min, int max)
        => Math.Clamp(max - (headWidth + ProgressBarStyle.VisibleLength(tail) - baseWidth) / 3, min, max);

    private static void WriteHydroponicLine(RichTextBuilder builder, GameItem item)
    {
        if (!TryStatus(item, false, out float ratio, out string tail, out var statusColor)) return;
        string label = Loc.T("hydro.status_label", "Status: ");

        int barLength = BarLength(ProgressBarStyle.VisibleLength(label), tail, 22, BarMinLength, BarFullLength);
        var output = new TooltipSections.Writer(builder, item, "hydroponic-status", TooltipGroupIds.Machine);
        output.RequireWidth(ProgressBarStyle.WidthForLine(ProgressBarStyle.VisibleLength(label) + barLength + 3
            + ProgressBarStyle.VisibleLength(tail))); // 条 + 两个括号 + 尾部分隔空格
        output.Direct(label, RenderHandler.ColorPalette.LightCyan);
        output.Direct(new TooltipLine("", ratio, barLength, tail, statusColor, RenderHandler.ColorPalette.VeryDarkGreen));
        output.Emit();
    }

    private static int GetWaterDays(GameItem item)
    {
        try
        {
            var waterItem = MachineHydroponic.GetWaterSlotItem(item);
            var seedSlot = MachineHydroponic.GetInputSlot(item);
            var seedItem = seedSlot?.childItem;

            if (seedItem == null || waterItem == null || !GameRead.IsTag(waterItem, "LIQUID_CONTAINER_TAG"))
                return -1;

            int perNight = HydroponicHelper.GetWaterUsePerCycle(seedItem);
            if (perNight <= 0) return -1;

            int ml = WaterHelper.GetCurrentCapacityML(waterItem);
            return Math.Max(0, ml / perNight);
        }
        catch
        {
            return -1;
        }
    }
}
