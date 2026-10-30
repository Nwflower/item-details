using System;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 让池化探针壳挺过原版"用完即 Destroy"的函数（如 <c>ChemicalHelper.DissolveInto</c> 末尾的
/// <c>chemicalItem.Destroy()</c>），省掉每次 <c>DirectoryMaster.Create</c> 约 55ms 的建壳成本。
///
/// 范围收得很窄：只有 <see cref="Run"/> 正在执行、且被销毁的正是登记的那个壳时才跳过原方法；
/// 其它物品只多一次引用比较，照常走原版。
/// 返回 false 只跳过原方法本体，其它模组对 Destroy 的前缀/后缀照常执行——它们本来就会
/// 收到这次调用（旧做法是每次克隆一件再让原版销毁），并没有新增调用。
/// 调用方必须在 Run 之后检查壳是否仍然可用：第三方前缀若自己销毁了它，就别再池化。
/// </summary>
internal static class ProbeDestroyGuard
{
    private const string HarmonyId = "item_details.probe_destroy_guard";
    private static Harmony? _harmony;

    /// <summary>当前受保护的壳；只在 <see cref="Run"/> 期间非空，且只在主线程使用。</summary>
    private static GameItem? _protected;

    internal static bool Active { get; private set; }

    internal static void Apply()
    {
        if (Active) return;
        try
        {
            var target = AccessTools.Method(typeof(GameItemElement), "Destroy", Type.EmptyTypes);
            if (target == null)
            {
                Log.Warn("ProbeDestroyGuard.Apply", "未找到 GameItemElement.Destroy，化学剂探针退回逐次克隆");
                return;
            }
            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(target, prefix: new HarmonyMethod(typeof(ProbeDestroyGuard), nameof(Prefix))
            {
                priority = Priority.First,
            });
            Active = true;
            Log.Msg("ProbeDestroyGuard patched GameItemElement.Destroy");
        }
        catch (Exception ex)
        {
            Log.Error("ProbeDestroyGuard.Apply", ex);
        }
    }

    internal static void Remove()
    {
        try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        _harmony = null;
        _protected = null;
        Active = false;
    }

    /// <summary>
    /// 执行 <paramref name="action"/>，期间挡住对 <paramref name="shell"/> 的 Destroy。
    /// action 总会执行；返回 false 表示之后壳已不可用（未打补丁，或被第三方销毁），调用方别再池化它。
    /// </summary>
    internal static bool Run(GameItem shell, Action action)
    {
        var previous = _protected;
        _protected = shell;
        try { action(); }
        finally { _protected = previous; }
        return Active && !IsDestroyed(shell);
    }

    internal static bool IsDestroyed(GameItem item)
    {
        // 不是 GameItemElement 就不在补丁范围内，按"已销毁"处理，别再池化
        try { return item is not GameItemElement e || e.IsDestroyed(); }
        catch { return true; }
    }

    private static bool Prefix(GameItemElement __instance)
    {
        var shell = _protected;
        return shell == null || !ReferenceEquals(__instance, shell);
    }
}
