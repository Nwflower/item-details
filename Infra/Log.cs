using System;
using System.Collections.Generic;
using UnityEngine;

namespace ItemDetails;

/// <summary>
/// 限流日志：同一键只报第一次，第 50 次再提示一次；恢复正常（MarkOk）后自动重新计。
/// 避免游戏小版本改动导致某字段每帧报错刷爆日志。
/// </summary>
internal static class Log
{
    private static readonly Dictionary<string, int> _counts = new();

    private static void Once(string key, string detail, bool warn)
    {
        _counts.TryGetValue(key, out int n);
        n++;
        _counts[key] = n;
        if (n == 1)
        {
            if (warn) Debug.LogWarning($"[ItemDetails] {key} 降级（该项本次不显示）: {detail}");
            else Debug.LogError($"[ItemDetails] {key} 异常: {detail}");
        }
        else if (n == 50)
        {
            Debug.LogWarning($"[ItemDetails] {key} 已连续失败 {n} 次，该功能可能不可用；若游戏刚更新请等待模组更新");
        }
    }

    internal static void Warn(string key, string detail) => Once(key, detail, true);
    internal static void Warn(string key, Exception ex) => Once(key, Describe(ex), true);
    internal static void Error(string key, Exception ex) => Once(key, Describe(ex), false);

    /// <summary>已经整理好的说明（例如模块停用原因），不再拼异常细节。</summary>
    internal static void Error(string key, string detail) => Once(key, detail, false);

    /// <summary>首次出现时带上堆栈：限流保证每键最多一次，不会刷屏。</summary>
    private static string Describe(Exception ex)
    {
        string head = ex.GetType().Name + " - " + ex.Message;
        try
        {
            string? stack = ex.StackTrace;
            return string.IsNullOrEmpty(stack) ? head : head + Environment.NewLine + stack;
        }
        catch { return head; }
    }

    /// <summary>清空失败计数。启停时调用，避免上一次会话的计数把新错误静默掉。</summary>
    internal static void Reset()
    {
        try { _counts.Clear(); } catch { }
    }

    /// <summary>该项恢复正常，清掉失败计数，下次再失败会重新从第 1 次开始报。</summary>
    internal static void MarkOk(string key) => _counts.Remove(key);

    internal static void Msg(string msg)
    {
        try { Debug.Log($"[ItemDetails] {msg}"); } catch { }
    }
}
