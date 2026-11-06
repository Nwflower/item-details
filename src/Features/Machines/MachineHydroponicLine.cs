using System;
using ItemDetails.Integration;
using HarmonyLib;
using UnityEngine;

namespace ItemDetails;

/// <summary>
/// 水培机状态与进度行改写：
/// 将原版分散在多行的「状态: 生长中……」与「[进度条] 16/43」以及预计工期整合成紧凑单行：
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
            Log.Msg("MachineHydroponicLine: patched MachineHydroponic.CreateMachineTooltip");
        }
        catch (Exception ex)
        {
            Log.Error("MachineHydroponicLine.Apply", ex);
        }
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

            WriteHydroponicLine(builder, item);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("MachineHydroponicLine.Prefix", ex);
            return true;
        }
    }

    private static void WriteHydroponicLine(RichTextBuilder builder, GameItem item)
    {
        var (cur, target) = MachineProgressHelper.GetProgress(item);
        int speed = item.GetTagReadonly("CURRENT_PROCESSING_SPEED_TAG")?.GetInt() ?? 0;

        int waterDays = GetWaterDays(item);
        bool canPower = MachineHelper.CanPower(item);
        bool canWater = waterDays >= 1;

        int daysNeeded = (speed > 0 && target > cur)
            ? (target - cur + speed - 1) / speed
            : 0;

        string label = Loc.T("hydro.status_label", "Status: ");
        string etaText = "";
        RenderHandler.ColorPalette statusColor;

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
            statusColor = RenderHandler.ColorPalette.LightRed;
        }
        else
        {
            string growing = LocHelper.GetLocalizedMechanic("mech_hydroponic_status_growing");
            etaText = daysNeeded > 0
                ? Loc.T("hydro.eta_growing", "est. {0} {0|day|days}, {1}", daysNeeded, growing)
                : growing;
            statusColor = RenderHandler.ColorPalette.LightGreen;
        }

        float ratio = (float)cur / Mathf.Max(1, target);
        string tail = cur + "/" + target + " " + etaText;

        var output = new TooltipSections.Writer(builder, item, "hydroponic-status", TooltipGroupIds.Machine);
        output.RequireWidth(ProgressBarStyle.WidthForLine(ProgressBarStyle.VisibleLength(label) + 13
            + ProgressBarStyle.VisibleLength(tail))); // 10 格 + 两个括号 + 尾部分隔空格
        output.Direct(label, RenderHandler.ColorPalette.LightCyan);
        output.Direct(new TooltipLine("", ratio, 10, tail, statusColor, RenderHandler.ColorPalette.VeryDarkGreen));
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
