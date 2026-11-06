using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 一次装配期登记的补丁与清理动作。
///
/// 全 Mod 只有一个 Harmony 实例（<see cref="Shared"/>）：过去每个补丁类各自 `new Harmony(id)`、
/// 拆卸靠 `UnpatchAll(自己的 id)`，于是 <see cref="ModEntry"/> 里必须维护一张与装配平行的
/// 手写拆卸清单，两边靠人保持对称。现在撤销按「原方法 + 补丁方法」逐项进行
/// （游戏自带 0Harmony 2.4.2 支持 `Unpatch(MethodBase, MethodInfo)`），谁登记谁撤销。
///
/// **一个功能一个作用域**：装配中途抛错时 <see cref="Dispose"/> 会把该类已打上的补丁回滚，
/// 不影响其它功能；停用时按登记逆序撤销补丁与清理动作。
///
/// 只在语言就绪后的延迟装配里创建并打补丁（理由见 <see cref="DeferredPatchRunner"/>）。
/// </summary>
internal sealed class PatchScope : IDisposable
{
    /// <summary>所有者标识。第三方据此区分补丁归属，前缀保持不变。</summary>
    internal const string HarmonyId = "item_details";

    private static Harmony? _shared;

    private readonly List<(MethodBase Target, MethodInfo Patch)> _patches = new();
    private readonly List<Action> _cleanups = new();
    private readonly string _name;
    private bool _disposed;

    internal PatchScope(string name) => _name = name;

    /// <summary>唯一的 Harmony 实例；首次使用时创建（此时语言已就绪）。</summary>
    internal static Harmony Shared => _shared ??= new Harmony(HarmonyId);

    /// <summary>
    /// 登记一个补丁，返回是否真的打上。<paramref name="target"/> 为 null 表示游戏里找不到该方法
    /// （版本改名等）：整条登记跳过，功能自行降级，不抛给调用方。
    /// 调用方在拿到 false 时必须按"没有这个补丁"处理——不要置"已就绪"标志，
    /// 也不要据此跳过自己的兜底路径（见 <see cref="ProbeDestroyGuard"/>、双击判定的 ticker）。
    /// </summary>
    internal bool Patch(MethodBase? target, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null,
        HarmonyMethod? finalizer = null)
    {
        if (_disposed || target == null) return false;

        var prefixMethod = prefix?.method;
        var postfixMethod = postfix?.method;
        var finalizerMethod = finalizer?.method;
        if (prefixMethod == null && postfixMethod == null && finalizerMethod == null) return false;

        try
        {
            Shared.Patch(target, prefix: prefix, postfix: postfix, finalizer: finalizer);
        }
        catch (Exception ex)
        {
            // 可能已经挂上了其中一部分（例如 prefix 成功、postfix 失败）：把这次请求的全部撤掉，
            // 不留半条补丁，调用方按"没打上"处理。
            Log.Error("Patch." + _name + "." + target.Name, ex);
            TryUnpatch(target, prefixMethod);
            TryUnpatch(target, postfixMethod);
            TryUnpatch(target, finalizerMethod);
            return false;
        }

        if (prefixMethod != null) _patches.Add((target, prefixMethod));
        if (postfixMethod != null) _patches.Add((target, postfixMethod));
        if (finalizerMethod != null) _patches.Add((target, finalizerMethod));
        return true;
    }

    /// <summary>停用时要做的额外清理（重置缓存、撤销订阅、停掉 ticker）；按登记逆序执行。</summary>
    internal void OnDispose(Action cleanup)
    {
        if (cleanup == null || _disposed) return;
        _cleanups.Add(cleanup);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        for (int i = _patches.Count - 1; i >= 0; i--)
        {
            var (target, patch) = _patches[i];
            TryUnpatch(target, patch);
        }
        _patches.Clear();

        for (int i = _cleanups.Count - 1; i >= 0; i--)
        {
            try { _cleanups[i](); }
            catch (Exception ex) { Log.Warn("Cleanup." + _name, ex); }
        }
        _cleanups.Clear();
    }

    private void TryUnpatch(MethodBase target, MethodInfo? patch)
    {
        if (patch == null) return;
        try { Shared.Unpatch(target, patch); }
        catch (Exception ex) { Log.Warn("Unpatch." + _name, ex); }
    }
}
