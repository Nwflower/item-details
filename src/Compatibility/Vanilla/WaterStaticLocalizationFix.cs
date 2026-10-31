using System;

namespace ItemDetails;

/// <summary>
/// 运行时把「水品质档位」的两张静态表按当前语言重写一遍。
///
/// 为什么需要：`WaterFeatureHelper` 的六个 `*_DISPLAY` 字段、`WATER_DISPLAY` 数组，以及
/// `ItemConditionList.conditionWaterPurity` 里的 `ItemCondition.display/identifier`，都是在
/// 类型静态构造时用当时的语言定格。只要有任何模组在 `ModLoader.Awake` 阶段 Patch 了
/// 引用这些类型的方法（Mono 打补丁前会 JIT，JIT 会跑被引用类型的静态构造），它们就会在
/// `locale` 仍是默认语言时被冻住；之后切到中文也不更新，表现为：
///   - 交易面板「品相」显示英文（读 `ItemCondition.display`）；
///   - `WaterHelper.WillAcceptWater` 用英文常量比中文动态名，买水顾客永远拒收。
///
/// 本类在 `DeferredPatchRunner` 里、语言已经应用之后调用，把这些静态字段改回当前语言。
/// 只改这些「本应本地化」的字符串，不碰存档、不改数值。属于兼容兜底：即便本模组自己已经
/// 把补丁推迟，其它模组（实测 Pen_mod_QoL_Tweaks / BrewingExpansion）仍可能提前触发。
/// </summary>
internal static class WaterStaticLocalizationFix
{
    private static readonly string[] MechanicKeys =
    {
        "mech_condition_pure_water",
        "mech_condition_high_quality_water",
        "mech_condition_basewater",
        "mech_condition_ghostwater",
        "mech_condition_rustwater",
        "mech_condition_gutterflow",
    };

    internal static void Refresh()
    {
        try
        {
            var names = new string[MechanicKeys.Length];
            for (int i = 0; i < MechanicKeys.Length; i++)
                names[i] = LocHelper.GetLocalizedMechanic(MechanicKeys[i]) ?? "";

            // WaterFeatureHelper：六个字段 + 数组。WillAcceptWater 与各种对白比较用的就是它们。
            WaterFeatureHelper.PURE_WATER_DISPLAY = names[0];
            WaterFeatureHelper.HIGH_QUALITY_WATER_DISPLAY = names[1];
            WaterFeatureHelper.BASE_WATER_DISPLAY = names[2];
            WaterFeatureHelper.GHOSTWATER_DISPLAY = names[3];
            WaterFeatureHelper.RUST_WATER_DISPLAY = names[4];
            WaterFeatureHelper.GUTTERFLOW_WATER_DISPLAY = names[5];
            WaterFeatureHelper.WATER_DISPLAY = names;

            // ItemConditionList：静态条件对象本身被 ItemFeature 直接引用，改回当前语言后
            // 「品相」面板与后续 InitWaterFeature/UpdateWaterFeature 都是正确文案。
            var water = ItemConditionList.conditionWaterPurity;
            if (water != null)
            {
                for (int i = 0; i < water.Count && i < names.Length; i++)
                {
                    var cond = water[i];
                    if (cond == null) continue;
                    cond.display = names[i];
                    if (!string.IsNullOrEmpty(cond.identifier)) cond.identifier = names[i];
                }
            }

            Log.Msg("WaterStaticLocalizationFix: 已按当前语言重写水质静态表");
        }
        catch (Exception ex)
        {
            Log.Warn("WaterStaticLocalizationFix.Refresh", ex);
        }
    }
}
