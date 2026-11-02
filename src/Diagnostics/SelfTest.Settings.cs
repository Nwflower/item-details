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
                foreach (string key in new[] { "show_authenticity", "show_valuation", "show_info_enhance", "double_click_inspect", "double_click_scan", "context_menu_scan" })
                {
                    var entry = config.Get(key)
                        ?? throw new InvalidOperationException("Missing self-test setting: " + key);
                    if (entry.ValueType != typeof(bool)) throw new InvalidOperationException("Expected boolean setting: " + key);
                    _saved.Add((entry, entry.BoxedValue));
                    // 鉴定自检直接调用原版 DoubleClickAction；临时禁用扫描以免扫描优先级干扰。
                    _apply.Invoke(entry, new object[] { key != "double_click_scan" });
                }

                // 液体组分分析是三档字符串：自检期间固定为「总是开启」，否则没有扫描器的
                // 测试档位会拿到空的水质段，用例会随玩家库存时有时无。
                var liquid = config.Get("liquid_analysis")
                    ?? throw new InvalidOperationException("Missing self-test setting: liquid_analysis");
                _saved.Add((liquid, liquid.BoxedValue));
                _apply.Invoke(liquid, new object[] { ModSettings.LiquidChoiceValue(ModSettings.LiquidMode.Always) });
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
