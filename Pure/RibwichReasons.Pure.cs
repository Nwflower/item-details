using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 赝品肋明治的原因判定（纯逻辑，可离线测试）。
///
/// 逐条对应游戏 RibwichHelper.IsCounterfeit（0.46D）：
///   1. 生产日期无效（月份 1–12；年份 425–429 或 432–438）
///   2. 包装厂编号无效（1–35）
///   3. 达夫雷标志 != 0（-1 缺失 / 其他为图案错误）
///   4. 批次贴纸：第一批次(425–429) 必须"没有"Ribwich 贴纸(-1)，第二批次(432–438) 必须是 0
///   5. 生产日期标记缺失（RIBWICH_PRODUCTION_TAG == -1）
///   6. 包装厂标记缺失（RIBWICH_FACILITY_TAG == -1）
///   7. 豪华版组合不合法（面包与酸黄瓜必须同为 0 或同为 1）
/// </summary>
internal static class RibwichReasons
{
    internal const int MissingMark = -1;

    internal enum Reason
    {
        ProductionDateMissing,
        MonthOutOfRange,
        YearOutOfRange,
        FacilityMissing,
        FacilityOutOfRange,
        DavreMissing,
        DavreWrong,
        FirstRunHasSticker,
        SecondRunStickerMissing,
        SecondRunStickerWrong,
        BunWithoutPickles,
        PicklesWithoutBun,
    }

    internal static bool IsValidProductionDate(int month, int year)
        => month >= 1 && month <= 12
        && ((year >= 425 && year <= 429) || (year >= 432 && year <= 438));

    internal static bool IsValidFacility(int facility) => facility >= 1 && facility <= 35;

    /// <summary>1 = 425–429，2 = 432–438，0 = 月份非法或年份不在两段区间。</summary>
    internal static int Period(int month, int year)
    {
        if (month < 1 || month > 12) return 0;
        if (year >= 425 && year <= 429) return 1;
        if (year >= 432 && year <= 438) return 2;
        return 0;
    }

    /// <summary>返回该物品命中的全部赝品原因（顺序与游戏判定顺序一致）。</summary>
    internal static List<Reason> Evaluate(
        int month, int year, int facility,
        int davreLogo, int ribwichLogo,
        int productionMark, int facilityMark,
        int bun, int pickles)
    {
        var list = new List<Reason>();

        // 1. 生产日期：越界与"缺失"是两项独立判定，可同时成立
        if (!IsValidProductionDate(month, year))
        {
            if (month < 1 || month > 12) list.Add(Reason.MonthOutOfRange);
            if (!((year >= 425 && year <= 429) || (year >= 432 && year <= 438)))
                list.Add(Reason.YearOutOfRange);
        }
        if (productionMark == MissingMark) list.Add(Reason.ProductionDateMissing);

        // 2. 包装厂编号：同样独立
        if (!IsValidFacility(facility)) list.Add(Reason.FacilityOutOfRange);
        if (facilityMark == MissingMark) list.Add(Reason.FacilityMissing);

        // 3. 达夫雷标志
        if (davreLogo != 0)
            list.Add(davreLogo == MissingMark ? Reason.DavreMissing : Reason.DavreWrong);

        // 4. 批次贴纸
        int period = Period(month, year);
        if (period == 1)
        {
            // 第一批次正确值就是 -1（没有贴纸）
            if (ribwichLogo != MissingMark) list.Add(Reason.FirstRunHasSticker);
        }
        else if (period == 2)
        {
            if (ribwichLogo == MissingMark) list.Add(Reason.SecondRunStickerMissing);
            else if (ribwichLogo != 0) list.Add(Reason.SecondRunStickerWrong);
        }

        // 5. 豪华版组合
        if (bun == 1 && pickles != 1) list.Add(Reason.BunWithoutPickles);
        else if (bun != 1 && pickles == 1) list.Add(Reason.PicklesWithoutBun);

        return list;
    }

    internal static string Text(Reason r, bool zh, int month, int year, int facility)
    {
        switch (r)
        {
            case Reason.ProductionDateMissing:
                return zh ? "生产日期缺失" : "No production date stamped";
            case Reason.MonthOutOfRange:
                return zh ? $"生产月份 {month}：应为 1–12" : $"Month {month} outside 1-12";
            case Reason.YearOutOfRange:
                return zh ? $"生产年份 {year}：应为 425–429 或 432–438"
                          : $"Year {year} outside 425-429 / 432-438";
            case Reason.FacilityMissing:
                return zh ? "包装厂编号缺失" : "No packing plant number";
            case Reason.FacilityOutOfRange:
                return zh ? $"包装厂编号 {facility}：应为 01–35" : $"Plant no. {facility} outside 01-35";
            case Reason.DavreMissing:
                return zh ? "缺少达夫雷标志" : "Davre mark absent";
            case Reason.DavreWrong:
                return zh ? "达夫雷标志图案不符" : "Davre mark wrong";
            case Reason.FirstRunHasSticker:
                return zh ? "第一批次不应有 Ribwich 贴纸" : "First batch should carry no Ribwich sticker";
            case Reason.SecondRunStickerMissing:
                return zh ? "第二批次缺少 Ribwich 贴纸" : "Second batch lacks its Ribwich sticker";
            case Reason.SecondRunStickerWrong:
                return zh ? "第二批次 Ribwich 贴纸图案错误" : "Second batch has the wrong Ribwich sticker";
            case Reason.BunWithoutPickles:
                return zh ? "有芝麻面包却没有加量酸黄瓜（豪华版需两者同时）"
                          : "Sesame bun but no extra pickles; deluxe needs both";
            case Reason.PicklesWithoutBun:
                return zh ? "有加量酸黄瓜却没有芝麻面包（豪华版需两者同时）"
                          : "Extra pickles but no sesame bun; deluxe needs both";
            default:
                return r.ToString();
        }
    }
}
