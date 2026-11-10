using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 容器内容物的一行事实：显示名 + 实例 ID + 堆叠数。
///
/// 纯数据，不引用游戏类型，供 <see cref="ContentsRollup"/>、<see cref="ContentsSignature"/>
/// 与离线压测（tests/TooltipStress）共用同一份实现。容器热路径每帧都在这上面做比对与归组，
/// 所以不要往这里加读游戏状态的字段。
/// </summary>
internal readonly struct ContentsFact
{
    internal readonly string Name;
    internal readonly int UniqueId;
    internal readonly int Stack;

    internal ContentsFact(string name, int uniqueId, int stack)
    {
        Name = name ?? "";
        UniqueId = uniqueId;
        Stack = stack > 1 ? stack : 1;
    }
}

/// <summary>
/// 内容物签名比对：顺序敏感（容器按格位添加，顺序本身是内容的一部分）。
/// 每帧至少调用一次，循环里不做分配、不读游戏字段。
/// </summary>
internal static class ContentsSignature
{
    internal static bool Matches(IReadOnlyList<ContentsFact> expected, IReadOnlyList<ContentsFact> current)
    {
        if (expected.Count != current.Count) return false;
        for (int i = 0; i < expected.Count; i++)
        {
            var a = expected[i];
            var b = current[i];
            if (a.UniqueId != b.UniqueId
                || a.Stack != b.Stack
                || !string.Equals(a.Name, b.Name, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>
/// 把容器内容物归并成图标格：按显示名合并，件数累加堆叠数，最多 <c>maxCells</c> 组。
///
/// 只做一次线性扫描；调用方（ContentsGrid）只在内容签名变化时重建，
/// 不需要每帧重算。纯逻辑，离线压测直接喂 1024×2500 条事实。
/// </summary>
internal sealed class ContentsRollup
{
    internal sealed class Group
    {
        internal string Name = "";
        internal int Count;
        /// <summary>这一组第一件物品在事实表中的下标，用来取图标。</summary>
        internal int FirstIndex;
    }

    internal readonly List<Group> Groups;
    /// <summary>所有事实的堆叠数之和。</summary>
    internal readonly int Total;
    /// <summary>真正画出来的组数（超出上限时最后一格留给「+N」）。</summary>
    internal readonly int ShownCount;
    /// <summary>被折进「+N」的件数。</summary>
    internal readonly int Overflow;

    private ContentsRollup(List<Group> groups, int total, int shown, int overflow)
    {
        Groups = groups;
        Total = total;
        ShownCount = shown;
        Overflow = overflow;
    }

    /// <summary>
    /// 归组。组顺序 = 首次出现顺序，与旧 ContentsGrid.Group 一致。
    /// </summary>
    internal static ContentsRollup Build(IReadOnlyList<ContentsFact> facts, int maxCells)
    {
        if (maxCells < 1) maxCells = 1;

        var groups = new List<Group>();
        var byName = new Dictionary<string, Group>(StringComparer.Ordinal);
        int total = 0;

        for (int i = 0; i < facts.Count; i++)
        {
            var fact = facts[i];
            int stack = fact.Stack > 0 ? fact.Stack : 1;
            if (!byName.TryGetValue(fact.Name, out var group))
            {
                group = new Group { Name = fact.Name, FirstIndex = i };
                byName[fact.Name] = group;
                groups.Add(group);
            }
            group.Count += stack;
            total += stack;
        }

        int shown = groups.Count <= maxCells ? groups.Count : maxCells - 1;
        int overflow = 0;
        for (int i = shown; i < groups.Count; i++) overflow += groups[i].Count;
        return new ContentsRollup(groups, total, shown, overflow);
    }
}
