using System;
using HarmonyLib;
using UnityEngine.UI;

namespace ItemDetails;

/// <summary>
/// 结果预填：只填面板控件，不提交、不确认。
///
///   - 赝品检测：打开有多个问题勾选的 confront 面板时，按物品真实 ISSUE_* 标签
///     勾选问题项；单选项的“genuine”面板不碰。
///   - 鉴定价格：打开鉴定面板时一次性把最优报价写入 APPRAISAL_DEFAULT_VALUE，
///     让游戏自己的 UpdateAppraisalPanel 正常显示；不常驻覆盖，玩家编辑和 ±
///     调价都按原版逻辑工作。
///
/// 开关默认关闭。所有补丁只调用游戏的公开 Inspector/标签接口，不改判定结果。
/// </summary>
internal static class ResultPrefill
{
    private const string HarmonyId = "item_details.result_prefill";
    private static Harmony? _harmony;
    private static bool _patched;

    internal static void Apply()
    {
        if (_patched) return;
        try
        {
            var inspect = AccessTools.Method(typeof(InspectionUIManager), "OnInspectItem",
                new[] { typeof(GameItem) });
            if (inspect == null)
            {
                Log.Warn("ResultPrefill.Apply", "未找到 OnInspectItem，预填跳过");
                return;
            }

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(inspect,
                postfix: new HarmonyMethod(typeof(ResultPrefill), nameof(AfterInspect)));

            _patched = true;
            Log.Msg("ResultPrefill: patched OnInspectItem");
        }
        catch (Exception ex)
        {
            Log.Error("ResultPrefill.Apply", ex);
        }
    }

    internal static void Remove()
    {
        try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        _harmony = null;
        _patched = false;
    }

    private static void AfterInspect(GameItem gameItem)
    {
        try
        {
            if (!ModSettings.ResultPrefill || gameItem == null) return;

            if (GameRead.IsTag(gameItem, "APPRAISAL_ITEM"))
            {
                int trueValue = ResolveTrueValue(gameItem);
                if (trueValue <= 0) return;

                int best = AppraisalRanges.Perfect(trueValue).High;
                gameItem.ModifyTag("APPRAISAL_DEFAULT_VALUE",
                    state => state.Enable().SetInt(best));

                // 面板刚打开时先同步一次控件；下一帧起由游戏按标签正常刷新。
                try
                {
                    var insp = InspectionUIManager.Instance;
                    if (insp != null && insp.apprisalServiceInputField != null)
                        insp.apprisalServiceInputField.SetTextWithoutNotify(best.ToString());
                }
                catch { }
                return;
            }

            FillCounterfeitIssues(gameItem);
        }
        catch (Exception ex)
        {
            Log.Warn("ResultPrefill.Inspect", ex);
        }
    }

    private static void FillCounterfeitIssues(GameItem item)
    {
        var kind = Authenticity.DetectKind(item);
        if (kind == Authenticity.ItemKind.None) return;

        var insp = InspectionUIManager.Instance;
        if (insp == null || insp.confrontPanel == null || !insp.confrontPanel.activeSelf) return;

        // 单选项面板显示的是“genuine/正品”声明，不对应具体 ISSUE_*；
        // 只有多个问题项都被激活时，才按真实问题标签勾选。
        if (insp.toggle2 == null || !insp.toggle2.gameObject.activeSelf) return;

        var checks = Authenticity.ChecksFor(kind);
        if (checks.Length != 5) return;

        for (int i = 0; i < 5; i++)
        {
            var toggle = ToggleAt(insp, i);
            if (toggle == null) continue;
            bool bad;
            try { bad = GameRead.IsTag(item, checks[i].Tag); } catch { bad = false; }
            toggle.SetIsOnWithoutNotify(bad);
        }
    }

    private static Toggle? ToggleAt(InspectionUIManager insp, int index)
    {
        return index switch
        {
            0 => insp.toggle1,
            1 => insp.toggle2,
            2 => insp.toggle3,
            3 => insp.toggle4,
            4 => insp.toggle5,
            _ => null,
        };
    }

    private static int ResolveTrueValue(GameItem item)
    {
        try
        {
            int? native = GameRead.TrueValue(item);
            if (native is int n && n > 0) return n;
        }
        catch { }
        try { return ValueBreakdown.Analyze(item)?.Total ?? 0; } catch { return 0; }
    }
}
