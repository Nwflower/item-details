using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 机器内部的第三层：已知机器的专门显示。由 <see cref="MachineInterior"/> 在通用列表之前调用。
///
/// 分两类：
///   - 泛型：按游戏的显式标签或结构识别，第三方机器只要遵守同一约定就自动适用——
///     进度型机器（PROGRESS_TYPE_MACHINE_TAG）、模块舱里的自学习模块；
///   - 登记表 <see cref="ById"/>：只对某一种原版机器成立的显示（水培、冰箱、催陈器），按 ID 分发，
///     不再对已登记的 ID 重复核对机器标签。
/// 约定：
///   - 天数与游戏一致按「天」计（每晚结算一次，原版说明写作「/天」「每个周期」）。
///   - 槽位按 role 找，找不到对应面板就不显示，不猜下标。
///   - 规则调用游戏的公开函数（GetAgingDays、IsChillable 等），不扫描回调或补丁归属。
/// </summary>
internal static class MachineAdapters
{
    private const RenderHandler.ColorPalette Good = RenderHandler.ColorPalette.LightGreen;
    private const RenderHandler.ColorPalette Info = RenderHandler.ColorPalette.LightCyan;
    private const RenderHandler.ColorPalette Warn = RenderHandler.ColorPalette.LightYellow;
    private const RenderHandler.ColorPalette Bad = RenderHandler.ColorPalette.LightRed;

    private delegate void Adapter(MachineInterior.Context ctx, List<TooltipLine> lines);

    /// <summary>原版机器 ID → 专门显示。第三方机器的专门显示也登记在这里。</summary>
    private static readonly Dictionary<string, Adapter> ById = new(StringComparer.Ordinal)
    {
        ["hydroponic"] = Hydroponic,
        ["fridge"] = Fridge,
        ["wine_rack"] = WineRack,
        // 酿酒拓展（工坊 3829730904）的流年：由 MachineWineRack.AgeWell() 改 ID 而来，陈化规则同原版
        ["be_liunian_ager"] = WineRack,
    };

    internal static void Run(MachineInterior.Context ctx, List<TooltipLine> lines)
    {
        // 登记表先跑：水培算出的"水还够几天"要给进度推算用。
        string id = "";
        try { id = ctx.Machine.identifier ?? ""; } catch { }
        if (ById.TryGetValue(id, out var adapter)) adapter(ctx, lines);

        Progress(ctx, lines);
        Learning(ctx, lines);
    }

    // ---------------- 进度型机器：还需几天 ----------------

    /// <summary>
    /// 原版进度型机器每晚把 MACHINE_PROGRESS_CURRENT_TAG 加上 CURRENT_PROCESSING_SPEED_TAG
    /// （MachineProgressHelper.ContinueProgressTypeMachine），加到目标即完成；原版 tooltip 只写「当前/目标」。
    /// 前提是当晚能供电，水培还要有水；条件不满足时写原因，不给天数。
    /// </summary>
    private static void Progress(MachineInterior.Context ctx, List<TooltipLine> lines)
    {
        var m = ctx.Machine;
        if (!GameRead.IsTag(m, "PROGRESS_TYPE_MACHINE_TAG")) return;
        if (GameRead.TagString(m, "MACHINE_STATE_TAG") != "STATE_WORKING") return;
        // 没有电源面板的机器不知道它怎么取电，不推算。
        if (!ctx.HasPowerPanel) return;

        int cur = GameRead.TagInt(m, "MACHINE_PROGRESS_CURRENT_TAG") ?? 0;
        int target = GameRead.TagInt(m, "MACHINE_PROGRESS_TARGET_TAG") ?? 0;
        int speed = GameRead.TagInt(m, "CURRENT_PROCESSING_SPEED_TAG") ?? 0;
        if (speed <= 0 || target <= cur) return;

        if (!CanPower(ctx))
        {
            lines.Add(Paused(NoPower));
            return;
        }
        if (ctx.WaterDays == 0)
        {
            lines.Add(Paused(NoWater));
            return;
        }

        int days = (target - cur + speed - 1) / speed;
        string text = Loc.T("machine.eta", "About {0} more day(s) to finish", days);
        var color = Good;

        int batteryDays = BatteryDays(ctx);
        if (batteryDays >= 0 && batteryDays < days)
        {
            text += BatteryShort(batteryDays);
            color = Warn;
        }
        if (ctx.WaterDays > 0 && ctx.WaterDays < days)
        {
            text += Loc.T("machine.eta_water", " (water lasts {0})", ctx.WaterDays);
            color = Warn;
        }
        lines.Add(new TooltipLine(text, color));
    }

    // ---------------- 水培机：水还够浇几天 ----------------

    /// <summary>
    /// 浇水不足时游戏不推进进度（MachineHydroponic.CanWater），所以剩余水量比进度本身更有用。
    /// 水槽由这里完整显示，认领后通用的液体行不再重复。
    /// </summary>
    private static void Hydroponic(MachineInterior.Context ctx, List<TooltipLine> lines)
    {
        var waterPanel = ctx.FindPanel("water");
        var seedPanel = ctx.FindPanel("seed");
        if (waterPanel == null || seedPanel == null) return;

        var seed = seedPanel.Items.Count > 0 ? seedPanel.Items[0] : null;
        var water = waterPanel.Items.Count > 0 ? waterPanel.Items[0] : null;
        if (seed == null || water == null) return;
        if (!GameRead.IsTag(water, "LIQUID_CONTAINER_TAG")) return;

        int perNight = HydroponicHelper.GetWaterUsePerCycle(seed);
        if (perNight <= 0) return;
        int ml = WaterHelper.GetCurrentCapacityML(water);
        int days = Math.Max(0, ml / perNight);

        ctx.WaterDays = days;
        waterPanel.Claimed = true;
        lines.Add(new TooltipLine(
            Loc.T("hydro.water", "Water {0} ml, enough for {1} day(s) ({2} ml/day)", ml, days, perNight),
            days == 0 ? Bad : days < 3 ? Warn : Info));
    }

    // ---------------- 冰箱：几件真正有效果 ----------------

    /// <summary>
    /// 每晚有电且冰箱非空时：可冷藏的物品在任一区都得到冷藏，烈酒放冷藏区冷藏、放冷冻区冰镇，
    /// 冰淇淋只在冷冻区冷冻；液体容器每秒降温。其余物品放进去只是占位置。
    /// 判定直接调用 MachineFridge 的公开函数，不照抄物品 ID。
    /// </summary>
    private static void Fridge(MachineInterior.Context ctx, List<TooltipLine> lines)
    {
        var chill = ctx.FindPanel("chill");
        var freeze = ctx.FindPanel("freeze");
        if (chill == null || freeze == null) return;
        if (chill.Items.Count + freeze.Items.Count == 0) return;

        int idle = 0;
        foreach (var it in chill.Items)
            if (!Cooled(it, freezeZone: false)) idle++;
        foreach (var it in freeze.Items)
            if (!Cooled(it, freezeZone: true)) idle++;

        // 分区名直接用面板自带的本地化标题，与机器窗口里看到的一致。
        string text = Loc.T("fridge.summary", "{0}: {1}; {2}: {3}",
            ZoneLabel(chill, "mech_fridge_label_chill"), chill.Items.Count,
            ZoneLabel(freeze, "mech_fridge_label_freeze"), freeze.Items.Count);
        if (idle > 0) text += Loc.T("fridge.no_effect", ", {0} get no effect", idle);
        lines.Add(new TooltipLine(text, idle > 0 ? Warn : Info));

        if (ctx.HasPowerPanel && !CanPower(ctx))
            lines.Add(Paused(NoPower));
    }

    private static string ZoneLabel(MachineInterior.Panel panel, string mechanicKey)
    {
        if (panel.Label.Length > 0) return panel.Label;
        try { return LocHelper.GetLocalizedMechanic(mechanicKey) ?? panel.Role; }
        catch { return panel.Role; }
    }

    private static bool Cooled(GameItem item, bool freezeZone)
    {
        try
        {
            if (item.IsTag("LIQUID_CONTAINER_TAG")) return true;
            if (MachineFridge.IsChillable(item) || MachineFridge.IsSpirit(item)) return true;
            return freezeZone && MachineFridge.IsFreezable(item);
        }
        catch { return true; }   // 判不出来就不算进"无效果"，宁可少报
    }

    // ---------------- 催陈器（酒架）：每天陈化几天 ----------------

    /// <summary>
    /// 陈化速度 = 1 + max(0, 质量加成) / 125 天/周期（MachineWineRack.GetAgingDays）。
    /// 原版只在说明书里写了公式，这里给出当前机器实际的值。假酒和未装瓶的酒不陈化（CanAgeWine）。
    /// </summary>
    private static void WineRack(MachineInterior.Context ctx, List<TooltipLine> lines)
    {
        var m = ctx.Machine;
        var rack = ctx.FindPanel("wine");
        if (rack == null || rack.Items.Count == 0) return;

        int aging = 0;
        foreach (var it in rack.Items)
        {
            try { if (MachineWineRack.CanAgeWine(it)) aging++; }
            catch { }
        }
        int idle = rack.Items.Count - aging;
        int days = MachineWineRack.GetAgingDays(m);

        string text = Loc.T("wine.rack", "{0} bottle(s) aging, +{1} day(s) of age per day", aging, days);
        if (idle > 0) text += Loc.T("wine.rack_idle", ", {0} not aging", idle);
        lines.Add(new TooltipLine(text, idle > 0 ? Warn : Info));

        if (aging > 0 && ctx.HasPowerPanel && !CanPower(ctx))
            lines.Add(Paused(NoPower));
    }

    // ---------------- 自学习算法模块：学习进度 ----------------

    /// <summary>学习进度条格数：比原版的 10 格短，给条后的模组名留位置。</summary>
    private const int LearningBar = 6;

    /// <summary>
    /// 模块舱里有「自学习算法」模块时：「1/3 个模组学习中」（分母是全部模组），
    /// 下面每个会学习的模组一条进度条（上限只读标签，见 <see cref="ModuleLearning"/>）；不会学习的模组不画。
    /// 进度 = 各项加成之和 / 各项上限之和；在学的条后写模组名和最多还差几点，学满写已学满。
    /// 哪天运转以及每晚推进几次取决于具体机器；没有显式规则时不折算天数。
    /// </summary>
    private static void Learning(MachineInterior.Context ctx, List<TooltipLine> lines)
    {
        var bars = new List<TooltipLine>();
        int learning = 0, total = 0;
        foreach (var module in ctx.Modules)
        {
            if (module == null) continue;
            total++;
            var stats = ModuleLearning.Read(module);
            if (stats.Count == 0) continue;

            int cur = 0, max = 0, remaining = 0;
            foreach (var s in stats)
            {
                cur += Math.Clamp(s.Current, 0, s.Max);
                max += s.Max;
                remaining = Math.Max(remaining, s.Remaining);
            }
            if (remaining == 0)
            {
                bars.Add(new TooltipLine(" ", 1f, LearningBar, Loc.T("learning.bar_full", "Maxed"),
                    Good, RenderHandler.ColorPalette.VeryDarkGreen));
                continue;
            }

            learning++;
            bars.Add(new TooltipLine(" ", (float)cur / max, LearningBar,
                Loc.T("learning.bar_left", "{0}: {1} point(s) to go", ContainerItemSignature.DisplayName(module), remaining),
                Info, RenderHandler.ColorPalette.VeryDarkCyan));
        }
        if (bars.Count == 0) return;

        lines.Add(new TooltipLine(
            Loc.T("learning.machine", "Learning: {0}/{1} module(s) training", learning, total),
            learning == 0 ? Good : Info));
        lines.AddRange(bars);
    }

    // ---------------- 共用 ----------------

    // 暂停原因直接用游戏水培机状态的原文（「电力不足」「水量不足」），各语言用词与原版一致。
    private const string NoPower = "mech_hydroponic_status_no_power";
    private const string NoWater = "mech_hydroponic_status_no_water";

    private static TooltipLine Paused(string mechanicKey)
    {
        string reason;
        try { reason = LocHelper.GetLocalizedMechanic(mechanicKey) ?? ""; }
        catch { reason = ""; }
        if (reason.Length == 0) reason = mechanicKey == NoWater ? "Not enough water" : "Not enough power";
        return new TooltipLine(Loc.T("machine.paused", "Paused: {0}", reason), Bad);
    }

    /// <summary>今晚能否供电：与原版 MachineHelper.CanPower 相同的判定，但电源按 role 找。</summary>
    private static bool CanPower(MachineInterior.Context ctx)
    {
        var source = ctx.PowerSource;
        if (source == null) return false;
        int usage = GameRead.TagInt(ctx.Machine, "machinery_drawn_power") ?? 0;
        try { return PowerHelper.CanDrawPowerSource(source, usage); }
        catch { return false; }
    }

    /// <summary>
    /// 电量撑不到完成时的提示。能量行改写（MachineBatteryInfo）开着时，「耗能」行括号里已写了可运行天数，
    /// 这里只说不够，不再重复天数。
    /// </summary>
    private static string BatteryShort(int batteryDays)
        => ModSettings.MachineBattery
            ? Loc.T("machine.eta_battery_short", " (not enough power)")
            : Loc.T("machine.eta_battery", " (battery lasts {0})", batteryDays);

    /// <summary>普通电池按当前每天耗能还能撑几天；转换器、超科技电池等返回 -1（不适用）。</summary>
    private static int BatteryDays(MachineInterior.Context ctx)
    {
        var source = ctx.PowerSource;
        if (!MachineInterior.IsPlainBattery(source)) return -1;
        int usage = GameRead.TagInt(ctx.Machine, "machinery_drawn_power") ?? 0;
        if (usage <= 0) return -1;
        int energy = GameRead.TagInt(source, "power_source_item_energy") ?? 0;
        return Math.Max(0, energy / usage);
    }
}
