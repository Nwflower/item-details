#if ITEMDETAILS_SELFTEST
using System;
using UnityEngine;

namespace ItemDetails;

internal static partial class SelfTest
{
    private static string _modId = "item_details";
    private static string _runId = "";
    internal static bool Requested { get; private set; }

    private static void BeginRun(string modId)
    {
        _modId = modId;
        _runId = DateTime.UtcNow.ToString("O") + "/" + Guid.NewGuid().ToString("N");
        Requested = true;
        WriteReport("RUNNING", "Waiting for the requested save and item directories.");
    }

    private static void WriteReport(string status, string details)
    {
        ModHelper.WriteModDataFileText(_modId, "selftest.txt",
            $"run={_runId}{Environment.NewLine}status={status}{Environment.NewLine}{details}");
    }

    private static void Finish(bool success, string details)
    {
        if (!Requested) return;
        Requested = false;
        try
        {
            WriteReport(success ? "PASS" : "FAIL", details);
        }
        catch (Exception ex)
        {
            success = false;
            Log.Error("SelfTest.WriteReport", ex);
        }
        Log.Msg($"[SelfTest] DONE {(success ? "OK" : "FAILED")} {details}");
        Application.Quit(success ? 0 : 1);
    }
}
#endif
