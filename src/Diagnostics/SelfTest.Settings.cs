#if ITEMDETAILS_SELFTEST
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ItemDetails;

internal static partial class SelfTest
{
    // ApplyBoxed changes the live value and invokes OnChanged, unlike Set/Reset it does not save.
    private sealed class TestSettings : IDisposable
    {
        private readonly MethodInfo _apply;
        private readonly List<(ConfigEntry Entry, object Value)> _saved = new();

        internal TestSettings()
        {
            var config = ModConfig.Find("item_details")
                ?? throw new InvalidOperationException("Self-test configuration is not registered.");
            _apply = AccessTools.Method(typeof(ConfigEntry), "ApplyBoxed")
                ?? throw new MissingMethodException("ConfigEntry.ApplyBoxed");
            try
            {
                foreach (string key in new[] { "show_authenticity", "show_valuation", "show_info_enhance", "show_cost_performance", "show_container_contents", "tooltip_text_formatting", "show_type_badge", "show_container_name_display", "info_price_modifiers", "info_black_market", "info_lottery", "info_brewing", "info_meat", "info_machines", "info_batteries", "info_progress_bar", "info_container_units", "info_manufacturer_inline", "double_click_inspect", "double_click_scan", "context_menu_scan" })
                {
                    var entry = config.Get(key)
                        ?? throw new InvalidOperationException("Missing self-test setting: " + key);
                    if (entry.ValueType != typeof(bool)) throw new InvalidOperationException("Expected boolean setting: " + key);
                    _saved.Add((entry, entry.BoxedValue));
                    // 鉴定自检直接调用原版 DoubleClickAction；临时禁用扫描以免扫描优先级干扰。
                    _apply.Invoke(entry, new object[] { key != "double_click_scan" });
                }

                // 液体组分分析与植物分析是三档字符串：自检期间固定为「总是开启」，否则没有扫描器 / 分析仪的
                // 测试档位会拿到空的水质段或种子性状，用例会随玩家库存时有时无。
                foreach (var (key, always) in new[]
                {
                    ("liquid_analysis", ModSettings.LiquidChoiceValue(ModSettings.LiquidMode.Always)),
                    ("plant_analysis", ModSettings.PlantChoiceValue(ModSettings.PlantMode.Always)),
                })
                {
                    var choice = config.Get(key)
                        ?? throw new InvalidOperationException("Missing self-test setting: " + key);
                    _saved.Add((choice, choice.BoxedValue));
                    _apply.Invoke(choice, new object[] { always });
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Exception? failed = null;
            for (int i = _saved.Count - 1; i >= 0; i--)
            {
                try { _apply.Invoke(_saved[i].Entry, new[] { _saved[i].Value }); }
                catch (Exception ex) { failed ??= ex; }
            }
            _saved.Clear();
            if (failed != null) throw new InvalidOperationException("Could not restore self-test settings.", failed);
        }
    }
}
#endif
