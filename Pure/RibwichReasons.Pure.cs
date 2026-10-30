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

    /// <summary>
    /// 原因的本地化键。文案与参数分开放：纯逻辑层不认识语言，
    /// 由 <see cref="RibwichCounterfeit"/> 调 <c>Loc.T</c> 拼出最终文本。
    /// </summary>
    internal static string Key(Reason r) => r switch
    {
        Reason.ProductionDateMissing => "ribwich.reason_date_missing",
        Reason.MonthOutOfRange => "ribwich.reason_month_range",
        Reason.YearOutOfRange => "ribwich.reason_year_range",
        Reason.FacilityMissing => "ribwich.reason_plant_missing",
        Reason.FacilityOutOfRange => "ribwich.reason_plant_range",
        Reason.DavreMissing => "ribwich.reason_davre_missing",
        Reason.DavreWrong => "ribwich.reason_davre_wrong",
        Reason.FirstRunHasSticker => "ribwich.reason_first_sticker",
        Reason.SecondRunStickerMissing => "ribwich.reason_second_sticker_missing",
        Reason.SecondRunStickerWrong => "ribwich.reason_second_sticker_wrong",
        Reason.BunWithoutPickles => "ribwich.reason_bun_no_pickles",
        Reason.PicklesWithoutBun => "ribwich.reason_pickles_no_bun",
        _ => "ribwich.reason_unknown",
    };

    /// <summary>对应的英文兜底文案（缺本地化词条时直接用它）。</summary>
    internal static string English(Reason r) => r switch
    {
        Reason.ProductionDateMissing => "No production date",
        Reason.MonthOutOfRange => "Month {0} not within 1-12",
        Reason.YearOutOfRange => "Year {0} not within 425-429 or 432-438",
        Reason.FacilityMissing => "No packing facility number",
        Reason.FacilityOutOfRange => "Facility {0} not within 01-35",
        Reason.DavreMissing => "Davre logo missing",
        Reason.DavreWrong => "Wrong Davre logo",
        Reason.FirstRunHasSticker => "First run should have no Ribwich sticker",
        Reason.SecondRunStickerMissing => "Second run is missing its Ribwich sticker",
        Reason.SecondRunStickerWrong => "Second run has the wrong Ribwich sticker",
        Reason.BunWithoutPickles => "Sesame bun without extra pickles (Deluxe needs both)",
        Reason.PicklesWithoutBun => "Extra pickles without sesame bun (Deluxe needs both)",
        _ => "{0}",
    };

    /// <summary>占位符参数，顺序与 <see cref="English"/> 里的 {0}/{1} 一致。</summary>
    internal static object[] Args(Reason r, int month, int year, int facility) => r switch
    {
        Reason.MonthOutOfRange => new object[] { month },
        Reason.YearOutOfRange => new object[] { year },
        Reason.FacilityOutOfRange => new object[] { facility },
        // 未登记的原因（未来新增枚举值）至少把枚举名显示出来，不要露出 "{0}"
        _ => new object[] { r.ToString() },
    };
}
