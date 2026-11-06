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
        ["furnace"] = Furnace,
        ["recharger"] = Recharger,
        ["hydroponic"] = Hydroponic,
        ["fridge"] = Fridge,
        ["water_purifier"] = Purifier,
        ["wine_rack"] = WineRack,
        // 酿酒拓展（工坊 3829730904）的流年：由 MachineWineRack.AgeWell() 改 ID 而来，陈化规则同原版
        ["be_liunian_ager"] = WineRack,
    };

    internal static void Run(MachineInterior.Context ctx, List<TooltipLine> lines)
    {
        // 登记表先跑：水培算出的"水还够几天"要给进度推算用。
        string id = "";
        try { id = ctx.Machine.identifier ?? ""; } catch { }
        // 自学习属于模组效果，紧跟在「效率/质量」后面；机器自己的色块行（水量等）排在它后面，
        // 这样下面缩进的槽位内容挂在最后一个一级行上，不会像是自学习的附注。
        Learning(ctx, lines, id);
        if (ById.TryGetValue(id, out var adapter)) adapter(ctx, lines);

        Progress(ctx, lines);
    }

    /// <summary>
    /// 熔炉的预期产物独立于模组百分比行，质量 0% 或没有质量模组也应显示。
    /// 投料与模块直接取自机器摘要（按 role 归类），内容物变化由摘要的签名缓存负责失效。
    /// </summary>
    private static void Furnace(MachineInterior.Context ctx, List<TooltipLine> lines)
    {
        // 出锭预测始终独立成一行；质量行（色块模式）的第三列只放「纯度+N」（ModuleEffectAnnotation）。
        // 没有色块模式时质量行没有第三列，纯度变化并回这一行
        bool qualityRow = GameRead.TagBool(ctx.Machine, "IS_RECEIVING_BONUS_FROM_MODULE_BOOL")
            && (GameRead.TagInt(ctx.Machine, "TOTAL_PERCENTAGE_QUALITY_BONUS_INT") ?? 0) != 0;
        bool gainInQualityRow = qualityRow && ModSettings.ModuleEffects;
        var part = qualityRow && !gainInQualityRow ? FurnaceQuality.Part.All : FurnaceQuality.Part.Rest;
        string? note = FurnaceQuality.Note(ctx, out var data, part);
        if (string.IsNullOrEmpty(note)) return;
        var line = new TooltipLine(note, RenderHandler.ColorPalette.Gray);
        lines.Add(data.HasValue ? line.WithData("furnace", data.Value) : line);
    }

    // ---------------- 充能器：每块电池的电量与今晚充多少 ----------------

    private const int RechargerBar = 6;

    /// <summary>
    /// 充能行尾注的呈现写法：今晚充入的「+N」画成灰色，规范文本不变；不是充能行或没有「+N」时返回 null。
    /// </summary>
    internal static string? RechargeTailRender(TooltipLine ln)
        => ln.DataKind == "recharge" && ln.Data is ValueTuple<string, int, int, int> d
            ? RechargeTailRender(ln.Tail ?? "", d.Item3) : null;

    /// <summary>行首呈现里「\u0006充入量」标记的字符：条行经 Writer 重建后拿不到 Data，用它把充入量带到 AddBarLine。</summary>
    internal const char GainMark = '\u0006';

    internal static string? RechargeTailRender(string tail, int gainPoints)
    {
        if (gainPoints <= 0) return null;
        string gain = "+" + gainPoints + "/";
        int at = tail.LastIndexOf(gain, StringComparison.Ordinal);
        if (at < 0) return null;
        return tail.Substring(0, at)
             + RenderHandler.ApplyRichTextColorTag("+" + gainPoints, RenderHandler.ColorPalette.Gray)
             + tail.Substring(at + 1 + gainPoints.ToString().Length);
    }

    /// <summary>
    /// 充能器（<c>recharger</c>）：每晚给槽里每块可充电电池充 3（开通商业用电后 6）点，充到满为止
    /// （原版 <c>MachineRecharger</c> 夜间回调 → <c>PowerHelper.ChargeBattery</c>，一次性电池、电流转换器、
    /// 超科技电池不充）。仿照自学习的写法：「充能」标签 + 进度条 + 电池名 + 「现有+今晚/上限kJ」，
    /// 一块电池一行；电池面板由这里完整给出，通用内容行不再重复。
    /// </summary>
    private static void Recharger(MachineInterior.Context ctx, List<TooltipLine> lines)
    {
        // 美化等第三方有自己的电池/电量识别，会读原版的「电池：能量 x/y」行；这里不认领，原行照出
        if (TooltipSections.HasListeners) return;
        var panel = ctx.FindPanel("batteries");
        if (panel == null || panel.Items.Count == 0) return;

        int rate = 3;
        try { rate = NetworkUpgrade.IsCommercialPowerUnlocked() ? 6 : 3; } catch { }
        bool powered = false;
        try { powered = PlayerStore.Instance != null && PlayerStore.Instance.isPowerOn; } catch { }

        string label = Loc.T("recharger.label", "Charging");
        float? col = ModuleEffectAnnotation.ChipEm(label) + 0.7f;
        var bars = new List<TooltipLine>();
        foreach (var battery in panel.Items)
        {
            if (battery == null) continue;
            int energy = GameRead.TagInt(battery, "power_source_item_energy") ?? 0;
            int max = GameRead.TagInt(battery, "power_source_item_max_energy") ?? 0;
            if (max <= 0) continue;

            bool chargeable = powered;
            try
            {
                chargeable &= !PowerHelper.IsDisposablePowerSource(battery)
                    && !CurrentConverterHelper.IsCurrentConverter(battery)
                    && !HypertechBatteryHelper.IsHypertechBattery(battery);
            }
            catch { }
            int gain = chargeable ? Math.Clamp(max - energy, 0, rate) : 0;

            string name = GameRead.ItemDisplayName(battery);
            string amount = energy + (gain > 0 ? "+" + gain : "") + "/" + max + "kJ";
            bool full = energy >= max;
            string prefix = bars.Count == 0 ? label + " " : " ";
            var line = new TooltipLine(prefix, Math.Clamp((float)energy / max, 0f, 1f), RechargerBar,
                    name + " " + amount, full ? Good : Info, RenderHandler.ColorPalette.VeryDarkCyan)
                .WithData("recharge", (battery.identifier, energy, gain, max));
            if (col is float c)
            {
                string head = bars.Count == 0
                    ? ModuleEffectAnnotation.ChipMarkup(label, 'c', ColumnLayout.CyanText) + ModuleEffectAnnotation.Pos(c)
                    : ModuleEffectAnnotation.Pos(c);
                line = line.WithRender(gain > 0 ? head + GainMark + gain : head);
            }
            bars.Add(line);
        }
        if (bars.Count == 0) return;
        panel.Claimed = true;
        lines.AddRange(bars);
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

        // 作业速度行已并入进度条与工期的机器，不在内部列表重复追加；
        // 水培机即使速度行没写成（回退到单独的状态行），也同样带着工期，同样跳过
        if (MachineSpeedLine.AbsorbsStatus(m)) return;
        if (ModSettings.TooltipFormatting && ModSettings.Machines && m.IsTag("HYDROPONIC_MACHINE_TAG"))
            return;

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
        string text = Loc.T("machine.eta", "About {0} more {0|day|days} to finish", days);
        var color = Good;

        int batteryDays = BatteryDays(ctx);
        if (ModSettings.MachineBattery && batteryDays >= 0 && batteryDays < days)
        {
            text += BatteryShort(batteryDays);
            color = Warn;
        }
        if (ctx.WaterDays > 0 && ctx.WaterDays < days)
        {
            text += Loc.T("machine.eta_water", " (water lasts {0} {0|day|days})", ctx.WaterDays);
            color = Warn;
        }
        lines.Add(new TooltipLine(text, color).WithData("eta", (cur, target, speed, days)));
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
        bool liters = ModSettings.ContainerUnits && Volume.UseLiters(WaterHelper.GetTotalCapacityML(water));
        string unit = liters ? WaterLayout.LiterUnit : WaterLayout.MilliliterUnit;
        string currentAmount = Volume.Number(ml, liters, Loc.Culture);
        string rateAmount = Volume.Number(perNight, liters, Loc.Culture);

        string text = days == 0
            ? Loc.T("hydro.water_short", "Water: {0}{3} ({2}{3}/day), <1 day", currentAmount, days, rateAmount, unit)
            : Loc.T("hydro.water", "Water: {0}{3} ({2}{3}/day), {1} {1|day|days}", currentAmount, days, rateAmount, unit);

        var tone = days == 0 ? Bad : days < 3 ? Warn : Info;
        string note = days == 0 ? Loc.T("hydro.left_short", "<1 day") : Loc.T("hydro.left", "{0} {0|day|days}", days);
        lines.Add(ChipLine(text, Loc.T("hydro.label", "Water"),
                Loc.T("hydro.value", "{0}{1} ({2}{1}/day)", currentAmount, unit, rateAmount), note, tone)
            .WithData("waterSupply", (ml, perNight, days)));
    }

    // ---------------- 一级信息行：色块标签 + 数值 + 说明 ----------------

    /// <summary>
    /// 机器信息分两层：一级是「[标签] 数值 说明」的色块行（与模组效果、自学习同一种样式），
    /// 二级是缩进的普通文字（库存、内容物）。规范文本仍是 <paramref name="canonical"/>，
    /// 色块、列位置只在原版窗口构建时换入；色块不可用时就是普通一行。
    /// 色块颜色随这一行的语气：正常青蓝、良好绿、提示琥珀、不足红。
    /// </summary>
    internal static TooltipLine ChipLine(string canonical, string label, string value, string note,
        RenderHandler.ColorPalette tone)
    {
        var line = new TooltipLine(canonical, tone);
        if (!ModSettings.ModuleEffects || label.Length == 0) return line;

        char kind = tone == Good ? '+' : tone == Warn ? 'y' : tone == Bad ? '-' : 'c';
        string fore = kind == '+' ? ColumnLayout.PositiveText : kind == 'y' ? ColumnLayout.AmberText
            : kind == '-' ? ColumnLayout.NegativeText : ColumnLayout.CyanText;
        float col2 = Math.Max(ModuleEffectAnnotation.ColumnEm(), ModuleEffectAnnotation.ChipEm(label) + 0.7f);
        string render = ModuleEffectAnnotation.ChipMarkup(label, kind, fore) + ModuleEffectAnnotation.Pos(col2) + value;
        if (note.Length > 0)
            render += ModuleEffectAnnotation.Pos(col2 + WaterColumns.TextEm(value) + 0.7f)
                    + RenderHandler.ApplyRichTextColorTag(note, RenderHandler.ColorPalette.Gray);
        return line.WithRender(render);
    }

    /// <summary>去掉拼接用的前导标点与空格（「，1 件无效果」→「1 件无效果」）。</summary>
    internal static string TrimLead(string text) => text.TrimStart(',', '，', '、', ';', '；', ' ');

    /// <summary>二级信息行缩进到第二列（与一级行的数值对齐）；已有渲染写法或带条的行不动。</summary>
    internal static TooltipLine Indented(TooltipLine line)
    {
        if (line.Render != null || line.HasBar || line.Text.Length == 0) return line;
        return line.WithRender(ModuleEffectAnnotation.Pos(ModuleEffectAnnotation.ColumnEm()) + line.Text.TrimStart());
    }

    // ---------------- 净水器：今晚还能净化多少、净化到哪一档 ----------------

    /// <summary>
    /// 在深拷贝的机器与容器上跑一遍原版的 <c>MachinePurifier.PurifyContainer</c>，
    /// 预告今晚按当前加成、水量与水质能清掉多少 ml 杂质，并显示净化前后档位；未升档也显示。
    /// 预测基于当前液体的真实成分，可能间接揭露未经试纸检测的水质。
    /// 属于默认开启的「信息增强」，不依赖独立的水质分析开关。
    /// 不认领容器面板：<see cref="MachineInterior"/> 的通用液体行继续显示当前水量。
    /// </summary>
    private static void Purifier(MachineInterior.Context ctx, List<TooltipLine> lines)
    {
        var tank = ctx.FindPanel("container");
        if (tank == null || tank.Items.Count == 0) return;
        var water = tank.Items[0];
        if (!GameRead.IsTag(water, "LIQUID_CONTAINER_TAG")) return;
        if (WaterHelper.IsEmpty(water)) return;

        // "今晚"预测以有电为前提；断电时保留原版的暂停提示，不显示无法兑现的净化量。
        if (ctx.HasPowerPanel && !CanPower(ctx))
        {
            lines.Add(Paused(NoPower));
            return;
        }

        // 不对真实容器调用 WaterHelper.GetWaterPurity：该方法会插入缺失标签。
        int purityBefore = WaterAdvice.SafePurity(water);
        if (purityBefore < 0) return;
        var (parts, purityAfter) = WaterAdvice.SimulateMachineRemoval(ctx.Machine, water);
        if (parts < 0 || purityAfter < 0) return;

        string text = Loc.T("purifier.estimate", "Estimated removal tonight: {0} ml", WaterInfo.Ml(parts));
        text += TierChange(purityBefore, purityAfter);
        // 色块行：[净化] 58.8mL/今晚  幽灵水 → 纯水（规范文本仍是上面那句）；档位名取不到时退回纯度百分比
        lines.Add(ChipLine(text, Loc.T("purifier.label", "Purify"),
                Loc.T("purifier.rate", "{0}mL/tonight", WaterInfo.Ml(parts)),
                TierPair(purityBefore, purityAfter) ?? Pct(purityBefore) + " → " + Pct(purityAfter), Info)
            .WithData("purification", (parts, purityBefore, purityAfter)));
    }

    /// <summary>纯度（万分比）写成「96.37%」。</summary>
    private static string Pct(int purity)
        => (purity / (WaterInfo.PurityFull / 100.0)).ToString("0.00", Loc.Culture) + "%";

    /// <summary>「幽灵水(96.37%)→优质水(99.00%)」：净化前后的档位名（游戏自己的名字）加纯度；取不到返回 null。</summary>
    private static string? TierPair(int purityBefore, int purityAfter)
    {
        try
        {
            string from = WaterFeatureHelper.GetWaterDisplayName(purityBefore) ?? "";
            string to = WaterFeatureHelper.GetWaterDisplayName(purityAfter) ?? "";
            return from.Length == 0 || to.Length == 0 ? null
                : from + "(" + Pct(purityBefore) + ")→" + to + "(" + Pct(purityAfter) + ")";
        }
        catch { return null; }
    }

    /// <summary>
    /// 使用真实水质计算净化前后档位，包括同档位和零净化量的情况。
    /// 档位名由游戏本身决定；不能解析时只显示可信的净化量。
    /// </summary>
    private static string TierChange(int purityBefore, int purityAfter)
    {
        try
        {
            string from = WaterFeatureHelper.GetWaterDisplayName(purityBefore) ?? "";
            string to = WaterFeatureHelper.GetWaterDisplayName(purityAfter) ?? "";
            if (from.Length == 0 || to.Length == 0) return "";
            return Loc.T("purifier.tier", " ({0} → {1})", from, to);
        }
        catch { return ""; }
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

        // 直接列出各分区里有什么（分区名取面板自带的本地化标题）；内容由这一行完整给出，通用内容行不再重复。
        string summary = "";
        foreach (var (panel, key) in new[] { (chill, "mech_fridge_label_chill"), (freeze, "mech_fridge_label_freeze") })
        {
            panel.Claimed = true;
            if (panel.Items.Count == 0) continue;
            if (summary.Length > 0) summary += Loc.T("list.zone_sep", "; ");
            summary += Loc.T("inner.line", "{0}: {1}", ZoneLabel(panel, key), MachineInterior.Names(panel.Items));
        }
        string noEffect = idle > 0 ? Loc.T("fridge.no_effect", ", {0} get no effect", idle) : "";
        // [冰箱] 冷藏区：牛奶；冷冻区：汽水  1 件无效果
        lines.Add(ChipLine(summary + noEffect, Loc.T("fridge.label", "Fridge"), summary, TrimLead(noEffect),
                idle > 0 ? Warn : Info)
            .WithData("fridge", (chill.Items.Count, freeze.Items.Count, idle)));

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

        string rackText = Loc.T("wine.rack", "{0} {0|bottle|bottles} aging, +{1} {1|day|days}/day", aging, days);
        string idleText = idle > 0 ? Loc.T("wine.rack_idle", ", {0} not aging", idle) : "";
        lines.Add(ChipLine(rackText + idleText, Loc.T("wine.rack_label", "Aging"), rackText, TrimLead(idleText),
                idle > 0 ? Warn : Info)
            .WithData("aging", (aging, days, idle)));

        if (aging > 0 && ctx.HasPowerPanel && !CanPower(ctx))
            lines.Add(Paused(NoPower));
    }

    // ---------------- 自学习算法模块：学习进度 ----------------

    /// <summary>学习进度条格数：比原版的 10 格短，给条后的模组名留位置。</summary>
    private const int LearningBar = 6;

    /// <summary>已学满的条：紫色。</summary>
    private const RenderHandler.ColorPalette Maxed = RenderHandler.ColorPalette.LightPurple;

    /// <summary>Pen 的自学习强化机（工坊 3828925546，v1.0.5 的 identifier）。</summary>
    private const string PenLearningMachine = "Pen_mod_selflearning.machine";

    /// <summary>更早版本用的 identifier；Pen 读档时会自己迁移到上面那个，这里两个都认。</summary>
    private const string PenLearningMachineLegacy = "pen_selflearning.machine";

    private static bool IsPenLearningMachine(string id)
        => string.Equals(id, PenLearningMachine, StringComparison.Ordinal)
        || string.Equals(id, PenLearningMachineLegacy, StringComparison.Ordinal);

    /// <summary>
    /// 模块舱里有「自学习算法」模块时：「1/3 个模组学习中」（分母是全部模组），
    /// 在学中的每个模组一条进度条（上限只读标签，见 <see cref="ModuleLearning"/>）；不会学习的模组不画。
    /// 进度 = 各项加成之和 / 各项上限之和；在学的条后写模组名和最多还差几点。
    /// 已学满的条只在 Pen 的自学习强化机上显示（紫色）：只有那台机器会把模块练到上限，
    /// 其它机器上显示“已学满”既没有信息量，也白占版面。
    /// 哪天运转以及每晚推进几次取决于具体机器；没有显式规则时不折算天数。
    /// </summary>
    private static void Learning(MachineInterior.Context ctx, List<TooltipLine> lines, string machineId)
    {
        var bars = new List<TooltipLine>();
        int learning = 0, total = 0;
        foreach (var module in ctx.Modules)
        {
            if (module == null) continue;
            total++;
            var stats = ModuleLearning.Read(module);
            if (stats.Count == 0) continue;

            // 进度比例只算有上限的正面项；负面项向 0 学，起点没有记录，不参与比例，只参与「还差几点」。
            // 负面项没学到 0 就不算学满：比例封顶在 99%，条上不会出现「满格但还在学」
            int cur = 0, max = 0, remaining = 0;
            // 文字进度：各项当前值直接相加 / 各项上限相加（负项上限为 0），如 性能 32/40 + 质量 -18/0 → 14/40
            int sumCur = 0, sumMax = 0;
            bool negativePending = false;
            foreach (var s in stats)
            {
                sumCur += s.Current;
                sumMax += s.Max;
                if (!s.Negative)
                {
                    cur += Math.Clamp(s.Current, 0, s.Max);
                    max += s.Max;
                }
                else if (s.Remaining > 0) negativePending = true;
                remaining = Math.Max(remaining, s.Remaining);
            }
            // 条后面写模组名，具体进度留给模块自己的提示框
            string moduleName = GameRead.ItemDisplayName(module);
            if (remaining == 0)
            {
                if (!IsPenLearningMachine(machineId)) continue;
                bars.Add(MakeLearningBar(1f, moduleName, Maxed, RenderHandler.ColorPalette.DarkPurple)
                    .WithData("learning", (module.identifier, cur, max, remaining)));
                continue;
            }

            learning++;
            float ratio = max > 0 ? (float)cur / max : 0f;
            if (negativePending) ratio = Math.Min(ratio, 0.99f);
            bars.Add(MakeLearningBar(ratio, moduleName, Info, RenderHandler.ColorPalette.VeryDarkCyan,
                    remaining, sumCur, sumMax, IsPenLearningMachine(machineId))
                .WithData("learning", (module.identifier, cur, max, remaining)));
        }
        if (bars.Count == 0) return;

        string header = Loc.T("learning.machine", "Learning: {0}/{1} {1|module|modules}", learning, total);
        var headerLine = new TooltipLine(header, learning == 0 ? Good : Info)
            .WithData("learningCount", (learning, total));
        string? render = LearningHeaderRender(header);
        lines.Add(render != null ? headerLine.WithRender(render) : headerLine);
        lines.AddRange(bars);
    }

    /// <summary>
    /// 一根学习进度条。行首的一个空格在原版窗口里换成缩进（对齐表头「1/2 个模组学习中」那一列），
    /// 规范文本仍是原来的空格；色块不可用时不缩进。
    /// </summary>
    /// <param name="remainingPoints">还差多少点学满（各项取最大）；大于 0 时在名字后面补「当前/上限」，Pen 的机器再补「（N天）」（构建时按当晚训练次数换算）。</param>
    private static TooltipLine MakeLearningBar(float ratio, string tail, RenderHandler.ColorPalette color,
        RenderHandler.ColorPalette dark, int remainingPoints = 0, int progress = 0, int progressMax = 0, bool estimateDays = false)
    {
        var line = new TooltipLine(" ", ratio, LearningBar, tail, color, dark);
        if (LearningColumn() is not float col) return line;
        string render = ModuleEffectAnnotation.Pos(col);
        // 标记后面依次是：还差点数;当前合计;上限合计;是否估算天数（1/0）
        if (remainingPoints > 0)
            render += SelfLearningMachine.RemainingMark.ToString() + remainingPoints + ";" + progress + ";" + progressMax + ";" + (estimateDays ? "1" : "0");
        return line.WithRender(render);
    }

    /// <summary>表头里「自学习」那个词；取不出冒号分隔的标签时为空。</summary>
    private static string LearningLabel(string header)
    {
        int at = header.IndexOfAny(new[] { ':', '：' });
        return at > 0 ? header.Substring(0, at).Trim() : "";
    }

    /// <summary>表头第二列的起点（em）：不小于加成色块的第二列，也不压住「自学习」自己的色块。</summary>
    private static float? LearningColumn()
    {
        if (!ModSettings.ModuleEffects) return null;
        string label = LearningLabel(Loc.T("learning.machine", "Learning: {0}/{1} {1|module|modules}", 0, 0));
        if (label.Length == 0) return null;
        return Math.Max(ModuleEffectAnnotation.ColumnEm(), ModuleEffectAnnotation.ChipEm(label) + 0.7f);
    }

    /// <summary>表头的渲染写法：紫色色块「自学习」+ 第二列文字；不可用时返回 null，按原文显示。</summary>
    private static string? LearningHeaderRender(string header)
    {
        float? col = LearningColumn();
        string label = LearningLabel(header);
        if (col == null || label.Length == 0) return null;
        int rest = header.IndexOfAny(new[] { ':', '：' }) + 1;
        while (rest < header.Length && header[rest] == ' ') rest++;
        return ModuleEffectAnnotation.ChipMarkup(label, 'c', ColumnLayout.CyanText)
             + ModuleEffectAnnotation.Pos(col.Value) + header.Substring(rest);
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
        // [暂停] 电力不足
        return ChipLine(Loc.T("machine.paused", "Paused: {0}", reason), Loc.T("machine.paused_label", "Paused"),
            reason, "", Bad).WithData("paused", mechanicKey);
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
            : Loc.T("machine.eta_battery", " (battery lasts {0} {0|day|days})", batteryDays);

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
