#if ITEMDETAILS_SELFTEST
using System;
using System.Globalization;

namespace ItemDetails;

internal static partial class SelfTest
{
    private const string SlotFlag = "-itemdetails-selftest-slot";
    private static int _saveSlot;

    private static bool TryReadSlot(string[] args, string? trigger, out int slot, out string error)
    {
        int? selected = null;
        slot = -1;
        error = "Specify an existing disposable save with -itemdetails-selftest-slot <id> or put its slot ID in selftest.flag.";
        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], SlotFlag, StringComparison.OrdinalIgnoreCase)) continue;
            if (++i >= args.Length || !TrySlot(args[i], out int value)) return false;
            if (selected.HasValue && selected.Value != value)
            {
                error = "Conflicting self-test save slots.";
                return false;
            }
            selected = value;
        }
        if (trigger != null)
        {
            if (!TrySlot(trigger.Trim(), out int value)) return false;
            if (selected.HasValue && selected.Value != value)
            {
                error = "Command-line and trigger-file self-test slots differ.";
                return false;
            }
            selected = value;
        }
        if (!selected.HasValue) return false;
        slot = selected.Value;
        error = "";
        return true;
    }

    private static bool TrySlot(string text, out int slot) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out slot) && slot >= 0;
}
#endif
