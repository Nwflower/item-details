using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// P3 价值增强：原版已给单件/整组价值，这里补"拆解与比较"维度。
///  - 单价 × 数量 = 总价（原版是合并显示，这里显式拆开）
///  - 性价比 = 总价值 / 占用格数（帮玩家判断"占地方的值不值"）
/// 只读；逐项 try/catch。
///
/// 依据：GameItem.GetCurrentValue()（单件当前价值，不含数量）、unitCount、
/// GameRead.OccupiedCells()（实心占格）。整组价值必须在此基础上乘数量。
/// </summary>
internal static class ValueEnhance
{
    private const RenderHandler.ColorPalette Color = RenderHandler.ColorPalette.LightTurquoise;

    internal static List<TooltipLine> Collect(GameItem? item, bool zh)
    {
        var lines = new List<TooltipLine>();
        if (item == null) return lines;

        try
        {
            // GetCurrentValue() 是「单件当前价值」，原版不会在这里乘 unitCount；
            // 整组价值 = 单件当前价值 × 数量。不能用 GetValue() 回退，它已经乘过数量；
            // 也不能退回 unitValue——那会丢掉子物、特性与事件修正，口径完全不同，
            // 却仍然标成「总价/性价比」，属于给出错数。读不到就不显示。
            long unit;
            try
            {
                unit = item.GetCurrentValue(useRetailMarkup: false, withChild: true,
                                            includeEvents: true, forceMarkup: false);
            }
            catch (Exception ex)
            {
                Log.Warn("ValueEnhance.Value", ex);
                return lines;
            }
            if (unit <= 0) return lines;   // 无价值物品不显示

            int count = 1;
            try { count = item.unitCount; } catch { }
            if (count < 1) count = 1;

            if (!StackValue.TryTotal(unit, count, out long total)) return lines;

            // 占格：按实心格计算，非矩形物品不能用外接矩形宽×高。
            int cells = GameRead.OccupiedCells(item.modifiedShape);

            Log.MarkOk("ValueEnhance");

            // 单价 × 数量 = 总价（数量>1 才有意义）
            if (count > 1)
            {
                lines.Add(new TooltipLine(
                    zh ? $"总价：{unit} × {count} = {total}" : $"Total: {unit} × {count} = {total}",
                    Color));
            }

            // 单格价值 = 总价值 / 占格
            if (cells > 0 && total > 0)
            {
                double perCell = StackValue.PerCell(total, cells);
                // 格式参照压缩率：比例 +（总额/格数格）
                lines.Add(new TooltipLine(
                    zh ? $"性价比：{perCell:0.#}（{total}/{cells}格）"
                       : $"Value/cell: {perCell:0.#} ({total}/{cells} cells)",
                    Color));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("ValueEnhance", ex);
        }

        return lines;
    }
}
