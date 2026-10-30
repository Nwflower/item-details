using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 读回调的 IL，判断它是否调用了某个游戏函数——用来证明"这件物品用的就是我们要模拟的那条原版路径"。
///
/// 回调本体直接调用算一层；第三方模组常见的「lambda → 自己的静态方法 → 原版函数」再多看一层，
/// 只展开回调所在程序集里的方法。原版回调不展开（原版条目已在登记表里），避免在游戏程序集里大面积读 IL。
/// 多播委托逐个检查。读不出方法体就算没有证据。
/// </summary>
internal static class CallbackEvidence
{
    private const int MaxDepth = 1;

    internal static bool Reaches(Delegate? callback, MethodInfo? target)
    {
        if (callback == null || target == null) return false;
        try
        {
            foreach (var single in callback.GetInvocationList())
            {
                var method = single.Method;
                if (method == null) continue;
                if (Scan(method, target, 0, new HashSet<MethodBase>())) return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("CallbackEvidence", ex);
        }
        return false;
    }

    private static bool Scan(MethodBase method, MethodInfo target, int depth, HashSet<MethodBase> visited)
    {
        if (!visited.Add(method)) return false;

        IEnumerable<KeyValuePair<System.Reflection.Emit.OpCode, object>> body;
        try { body = PatchProcessor.ReadMethodBody(method); }
        catch { return false; }

        var owner = method.DeclaringType?.Assembly;
        bool expand = depth < MaxDepth && owner != null && owner != typeof(GameItem).Assembly;
        List<MethodBase>? next = null;

        foreach (var instruction in body)
        {
            if (instruction.Value is not MethodBase callee) continue;
            if (callee.DeclaringType == target.DeclaringType && callee.Name == target.Name) return true;
            if (expand && callee.DeclaringType?.Assembly == owner) (next ??= new List<MethodBase>()).Add(callee);
        }

        if (next != null)
            foreach (var callee in next)
                if (Scan(callee, target, depth + 1, visited)) return true;
        return false;
    }
}
