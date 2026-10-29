using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace ItemDetails;

/// <summary>
/// 容器与机器类物品：在图标底部常驻显示物品名（不必悬停）。
///
/// 为什么只做这两类：容器（储藏区、保险箱、枪柜…）和机器的图标大量复用同一套像素图，
/// 玩家在一堆格子里很难凭外观分辨哪个是哪个；其余物品悬停 tooltip 就够，逐格加字反而糊屏。
///
/// 实现：
///   - 挂 <c>GameItemElement.ValidateSelect(bool)</c> 的后缀——游戏自己在里面刷新堆叠数字、
///     图钉与外观，物品身份变化/换格子都会走到这里；
///   - 文字用 <c>TreeNodeRender.InstantiateText()</c> 生成两个节点（本体 + 深色投影），
///     与游戏的堆叠数字同款；父节点挂在 <c>handler</c>（未旋转），避免随物品旋转颠倒；
///   - 定位只用锚点（父矩形底边拉伸），不猜父节点的局部原点/缩放约定；
///   - 每个元素只建一次，挂在元素自身节点下，元素销毁时随之销毁；
///     用 <see cref="ConditionalWeakTable{TKey,TValue}"/> 记录，不持有已销毁元素的强引用。
///
/// 历史：第一版按「handler 原点在左上、Y 朝下」的像素偏移放置，实测名字压在图标中间并溢出到
/// 相邻格子（min/max 字号也没生效），所以改为纯锚点定位 + Truncate，并第一次创建时打一条
/// geometry 日志，方便按实机数据继续调。
/// </summary>
internal static class ItemNameLabel
{
    private const string HarmonyId = "item_details.item_name_label";
    private const float LabelHeight = 10f;
    private const float LabelBottomInset = 1f;

    private static Harmony? _harmony;
    private static bool _patched;
    private static bool _geometryLogged;

    private static readonly ConditionalWeakTable<GameItemElement, Label> Labels = new();

    private sealed class Label
    {
        internal TreeNodeRender? Main;
        internal TreeNodeRender? Shadow;
        internal string Text = "";
        internal bool Visible;
    }

    internal static void Apply()
    {
        if (_patched) return;
        try
        {
            var target = AccessTools.Method(typeof(GameItemElement), "ValidateSelect", new[] { typeof(bool) });
            if (target == null)
            {
                Log.Warn("NameLabel.Apply", "未找到 GameItemElement.ValidateSelect，图标名称跳过");
                return;
            }

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(target, postfix: new HarmonyMethod(typeof(ItemNameLabel), nameof(AfterValidateSelect)));
            _patched = true;
            Log.Msg("ItemNameLabel: patched GameItemElement.ValidateSelect");
        }
        catch (Exception ex)
        {
            Log.Error("NameLabel.Apply", ex);
        }
    }

    internal static void Remove()
    {
        try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        _harmony = null;
        _patched = false;
    }

    private static void AfterValidateSelect(GameItemElement __instance)
    {
        try { Refresh(__instance); }
        catch (Exception ex) { Log.Warn("NameLabel.Refresh", ex); }
    }

    private static void Refresh(GameItemElement element)
    {
        if (element == null) return;

        bool want = ModSettings.ItemName && ShouldShow(element);
        if (!want)
        {
            Hide(element);
            return;
        }

        string name = "";
        try { name = element.GetDisplayName() ?? ""; }
        catch (Exception ex) { Log.Warn("NameLabel.DisplayName", ex); }
        if (name.Length == 0)
        {
            Hide(element);
            return;
        }

        var label = GetOrCreate(element);
        if (label?.Main == null || label.Shadow == null) return;

        if (!label.Visible)
        {
            label.Visible = true;
            label.Main.text.enabled = true;
            label.Shadow.text.enabled = true;
        }

        if (label.Text != name)
        {
            label.Text = name;
            label.Main.text.text = name;
            label.Shadow.text.text = name;
        }

        Place(element, label);
    }

    private static void Hide(GameItemElement element)
    {
        if (!Labels.TryGetValue(element, out var label)) return;
        if (!label.Visible || label.Main == null || label.Shadow == null) return;
        label.Visible = false;
        label.Main.text.enabled = false;
        label.Shadow.text.enabled = false;
    }

    /// <summary>容器（有内嵌库存）或机器；判定失败一律不显示，不猜。</summary>
    private static bool ShouldShow(GameItemElement item)
    {
        try
        {
            if (item.IsGameItemType("MACHINE")) return true;
        }
        catch (Exception ex) { Log.Warn("NameLabel.IsMachine", ex); }

        try
        {
            var inventories = GraphUtils.FindChildrenType<GameInventory>(item);
            return inventories != null && inventories.Count > 0;
        }
        catch (Exception ex)
        {
            Log.Warn("NameLabel.IsContainer", ex);
            return false;
        }
    }

    private static Label? GetOrCreate(GameItemElement element)
    {
        if (Labels.TryGetValue(element, out var existing)) return existing;

        try
        {
            // handler 的 rect 实测是 0×0（sizeDelta=(0,0)），锚点贴底会得到零高度 → 标签永远不可见。
            // middle 才是物品的真实矩形（实测 112×96），且位于 rotator 之上，不会随物品旋转。
            var parent = element.middle;
            if (parent == null) return null;

            // 先投影后本体：与游戏堆叠数字同样的两层文字，像素图上才看得清
            var shadow = TreeNodeRender.InstantiateText().SetParent(parent);
            var main = TreeNodeRender.InstantiateText().SetParent(parent);

            Configure(shadow, new Color(0f, 0f, 0f, 0.85f));
            Configure(main, Color.white);
            shadow.text.text = "";
            main.text.text = "";
            shadow.text.enabled = false;
            main.text.enabled = false;

            if (!_geometryLogged)
            {
                _geometryLogged = true;
                try
                {
                    var h = element.handler.rectTransform;
                    var m = element.rectTransform;
                    Log.Msg($"NameLabel geometry: handler sizeDelta={h.sizeDelta} rect={h.rect} pivot={h.pivot} " +
                            $"anchor=({h.anchorMin},{h.anchorMax}); middle sizeDelta={m.sizeDelta} rect={m.rect}; " +
                            $"item={element.widthPixels}x{element.heightPixels} grid={UISettings.current.gridSize}");
                }
                catch { }
            }

            var label = new Label { Main = main, Shadow = shadow };
            Labels.Add(element, label);
            return label;
        }
        catch (Exception ex)
        {
            Log.Error("NameLabel.Create", ex);
            return null;
        }
    }

    private static void Configure(TreeNodeRender node, Color color)
    {
        var rt = node.rectTransform;
        rt.localScale = Vector3.one;
        rt.localEulerAngles = Vector3.zero;

        var text = node.text;
        text.color = color;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Truncate;
        text.enableAutoSizing = true;   // 长名字自动缩小，不撑出格子
        text.fontSizeMin = 5f;
        text.fontSizeMax = 9f;
        text.raycastTarget = false;
    }

    /// <summary>
    /// 贴在物品矩形（middle）的底边内侧。
    ///
    /// 实测坐标系：middle 的局部原点与 pivot 在左上、Y 朝下（rect 为 x:0, y:-96, 112×96），
    /// 所以锚点用左上角、按「物品像素高度 − 行高 − 内缩」下移即可，不依赖任何缩放约定。
    /// 第一版挂在 handler 上：handler 的 rect 是 0×0，锚点贴底得到零高度，标签根本不可见。
    /// </summary>
    private static void Place(GameItemElement element, Label label)
    {
        try
        {
            float width = Mathf.Max(8f, element.widthPixels);
            float height = Mathf.Max(8f, element.heightPixels);
            float top = -(height - LabelHeight - LabelBottomInset);

            foreach (var node in new[] { label.Shadow, label.Main })
            {
                if (node == null) continue;
                var rt = node.rectTransform;

                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                rt.sizeDelta = new Vector2(width, LabelHeight);
                rt.anchoredPosition = new Vector2(0f, top);
            }

            // 投影下移 1px，形成描边感（与游戏堆叠数字的叠法一致）
            if (label.Shadow != null)
                label.Shadow.rectTransform.anchoredPosition = new Vector2(0f, top - 1f);
        }
        catch (Exception ex)
        {
            Log.Warn("NameLabel.Place", ex);
        }
    }
}
