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
    /// 登记一个补丁。<paramref name="target"/> 为 null 表示游戏里找不到该方法（版本改名等）：
    /// 整条登记跳过，功能自行降级，不抛给调用方。
    /// </summary>
    internal void Patch(MethodBase? target, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null,
        HarmonyMethod? finalizer = null)
    {
        if (_disposed || target == null) return;

        var prefixMethod = prefix?.method;
        var postfixMethod = postfix?.method;
        var finalizerMethod = finalizer?.method;
        if (prefixMethod == null && postfixMethod == null && finalizerMethod == null) return;

        try
        {
            Shared.Patch(target, prefix: prefix, postfix: postfix, finalizer: finalizer);
        }
        catch (Exception ex)
        {
            // 打不上就当作没登记：不留半条补丁，调用方照常继续。
            Log.Error("Patch." + _name + "." + target.Name, ex);
            return;
        }

        if (prefixMethod != null) _patches.Add((target, prefixMethod));
        if (postfixMethod != null) _patches.Add((target, postfixMethod));
        if (finalizerMethod != null) _patches.Add((target, finalizerMethod));
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
            try { Shared.Unpatch(target, patch); }
            catch (Exception ex) { Log.Warn("Unpatch." + _name, ex); }
        }
        _patches.Clear();

        for (int i = _cleanups.Count - 1; i >= 0; i--)
        {
            try { _cleanups[i](); }
            catch (Exception ex) { Log.Warn("Cleanup." + _name, ex); }
        }
        _cleanups.Clear();
    }
}
