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
                foreach (string key in new[] { "show_item_appraisal", "show_info_enhance", "double_click_inspect", "show_water_purity" })
                {
                    var entry = config.Get(key)
                        ?? throw new InvalidOperationException("Missing self-test setting: " + key);
                    if (entry.ValueType != typeof(bool)) throw new InvalidOperationException("Expected boolean setting: " + key);
                    _saved.Add((entry, entry.BoxedValue));
                    _apply.Invoke(entry, new object[] { true });
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
