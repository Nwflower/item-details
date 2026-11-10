using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ItemDetails;

/// <summary>
/// 容器内容物的一次快照：物品列表 + 签名事实 + 归组结果。
///
/// tooltip 每帧重建，但内容物通常不变。旧实现每帧都复制 <c>GameGridInventory.childItems</c>
/// 并跑两次全量签名比对；现在由 <see cref="StorageInfo"/> 按短间隔刷新快照，
/// 其余时间把同一个对象交给 <see cref="ContentsGrid"/>，每帧只需比一次引用（O(1)）。
///
/// 快照只在内容物真的变化时换新对象，<see cref="Revision"/> 因此可以当作缓存失效键。
/// 只持有一份物品引用副本，不复制游戏状态；停用时随 Entry 一起丢弃。
/// </summary>
internal sealed class ContainerContents
{
    private static int _lastRevision;

    internal readonly List<GameItem> Items;
    internal ContentsFact[] Facts;
    internal readonly bool Sealed;
    /// <summary>每次换新对象递增；同一对象的内容不会变。</summary>
    internal readonly int Revision;

    /// <summary>图标格的归组结果，由 ContentsGrid 在首次绘制时写入。</summary>
    internal ContentsRollup? Rollup;

    /// <summary>签名/名字是按哪个语言取的；换语言要重算（名字跟随游戏当前语言）。</summary>
    internal string Locale = "";

    private ReadOnlyCollection<GameItem>? _view;

    private ContainerContents(List<GameItem> items, ContentsFact[] facts, bool sealedBox, string locale)
    {
        Items = items;
        Facts = facts;
        Sealed = sealedBox;
        Locale = locale;
        Revision = ++_lastRevision;
    }

    internal static ContainerContents Capture(List<GameItem> items, bool sealedBox)
        => Capture(items, sealedBox, Loc.Code);

    internal static ContainerContents Capture(List<GameItem> items, bool sealedBox, string locale)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));

        // 旧 ContentsGrid.Group 会跳过 null；这里保持同一口径（正常库存不会有 null）。
        List<GameItem> clean = items;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] != null) continue;
            clean = new List<GameItem>(items.Count);
            foreach (var item in items)
                if (item != null) clean.Add(item);
            break;
        }
        return new ContainerContents(clean, FactsOf(clean), sealedBox, locale);
    }

    /// <summary>内容物没变就返回 <paramref name="previous"/>（引用相同，调用方可据此判断无需重绘）。</summary>
    internal static ContainerContents Refresh(ContainerContents? previous, List<GameItem> items, bool sealedBox)
    {
        if (previous != null && previous.Sealed == sealedBox && previous.SameAs(items)) return previous;
        return Capture(items, sealedBox);
    }

    internal bool SameAs(List<GameItem> current)
    {
        if (current == null || Facts.Length != current.Count) return false;
        for (int i = 0; i < current.Count; i++)
            if (!ReferenceEquals(Items[i], current[i])) return false;
        return ContentsSignature.Matches(Facts, FactsOf(current));
    }

    /// <summary>换语言后按当前语言重取名字；不重读库存。返回是否真的重建了事实表。</summary>
    internal bool RebindLocale(string locale)
    {
        if (string.Equals(Locale, locale, StringComparison.Ordinal)) return false;
        Locale = locale;
        Facts = FactsOf(Items);
        Rollup = null;
        _view = null;
        return true;
    }

    /// <summary>给第三方区块字段用的只读视图；只建一次，避免每帧 ToArray。</summary>
    internal ReadOnlyCollection<GameItem> ItemsView
    {
        get { return _view ??= Array.AsReadOnly(Items.ToArray()); }
    }

    internal static ContentsFact[] FactsOf(List<GameItem> items)
    {
        var facts = new ContentsFact[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            // 名字按当前语言取：item.name 是创建/读档时固化的，见 GameRead.ItemDisplayName。
            facts[i] = new ContentsFact(
                GameRead.ItemDisplayName(item),
                GameRead.UniqueId(item),
                ContainerItemSignature.ReadStack(item));
        }
        return facts;
    }
}
