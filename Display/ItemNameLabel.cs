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
///   - 文字用 <c>TreeNodeRender.InstantiateText()</c> 生成两层（本体 + 深色投影），
///     与游戏的堆叠数字同款；父节点挂在 <c>middle</c>（未旋转的矩形），避免随物品旋转颠倒；
///   - 定位用**世界坐标外接框**（GetWorldCorners），并把节点 localScale 取父级缩放的倒数，
///     于是 sizeDelta 与字号都以屏幕像素为单位。之前直接拿世界宽度当 sizeDelta：
///     实测父级 lossyScale=2，于是标签被放大成两倍宽、横向糊满相邻格子；
///   - ValidateSelect 早于布局生效，所以另有 0.2s 节流补位（<see cref="ItemNameTicker"/>）；
///   - 每个元素只建一次、挂在元素节点下随其销毁，用弱引用表记录，不持有已销毁元素。
/// </summary>
internal static class ItemNameLabel
{
    private const string HarmonyId = "item_details.item_name_label";
    private const float LabelHeight = 11f;      // 屏幕像素
    private const float LabelBottomInset = 3f;  // 屏幕像素

    private static Harmony? _harmony;
    private static bool _patched;
    private static bool _geometryLogged;
    private static int _placementLogged;

    private static readonly ConditionalWeakTable<GameItemElement, Label> Labels = new();
    private static readonly System.Collections.Generic.List<WeakReference<GameItemElement>> Tracked = new();
    private static GameObject? _tickerGo;

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

            _tickerGo = new GameObject("ItemDetails_NameLabelTicker");
            UnityEngine.Object.DontDestroyOnLoad(_tickerGo);
            _tickerGo.AddComponent<ItemNameTicker>();

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
        Tracked.Clear();
        if (_tickerGo != null)
        {
            try { UnityEngine.Object.Destroy(_tickerGo); } catch { }
            _tickerGo = null;
        }
    }

    /// <summary>节流补位：ValidateSelect 跑在布局/旋转生效之前，世界坐标可能还是上一帧的。</summary>
    internal sealed class ItemNameTicker : MonoBehaviour
    {
        private const float Interval = 0.2f;
        private float _timer;

        private void Update()
        {
            _timer += Time.unscaledDeltaTime;
            if (_timer < Interval) return;
            _timer = 0f;
            try { ReapplyAll(); } catch { }
        }
    }

    internal static void ReapplyAll()
    {
        for (int i = Tracked.Count - 1; i >= 0; i--)
        {
            GameItemElement? element = null;
            try { Tracked[i].TryGetTarget(out element); } catch { }
            if (element == null)
            {
                Tracked.RemoveAt(i);
                continue;
            }

            if (!Labels.TryGetValue(element, out var label) || !label.Visible) continue;
            Place(element, label);
        }
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
            // handler 的 rect 实测是 0×0，锚点类定位会得到零尺寸；middle 才是物品真实矩形。
            var parent = element.middle;
            if (parent == null) return null;

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
                    Log.Msg($"NameLabel geometry: handler rect={h.rect} sizeDelta={h.sizeDelta}; " +
                            $"middle rect={m.rect} sizeDelta={m.sizeDelta} lossyScale={m.lossyScale}; " +
                            $"item={element.widthPixels}x{element.heightPixels} grid={UISettings.current.gridSize}");
                }
                catch { }
            }

            var label = new Label { Main = main, Shadow = shadow };
            Labels.Add(element, label);
            Tracked.Add(new WeakReference<GameItemElement>(element));
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
        rt.localEulerAngles = Vector3.zero;

        var text = node.text;
        text.color = color;
        text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Truncate;
        text.enableAutoSizing = true;
        text.fontSizeMin = 5f;
        text.fontSizeMax = 9f;   // 屏幕像素（节点 localScale 已抵消父级缩放）
        text.raycastTarget = false;
    }

    /// <summary>
    /// 世界外接框的底边 + 3px，宽度取外接框宽度；
    /// 节点 localScale 取父级缩放的倒数，这样 sizeDelta 与字号都直接是屏幕像素。
    /// </summary>
    private static void Place(GameItemElement element, Label label)
    {
        try
        {
            var middle = element.middle;
            if (middle == null) return;

            var rect = middle.rectTransform;
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);

            float minX = corners[0].x, maxX = corners[0].x, minY = corners[0].y;
            for (int i = 1; i < 4; i++)
            {
                if (corners[i].x < minX) minX = corners[i].x;
                if (corners[i].x > maxX) maxX = corners[i].x;
                if (corners[i].y < minY) minY = corners[i].y;
            }

            var scale = rect.lossyScale;
            float sx = Mathf.Abs(scale.x) < 0.001f ? 1f : Mathf.Abs(scale.x);
            float sy = Mathf.Abs(scale.y) < 0.001f ? 1f : Mathf.Abs(scale.y);

            float width = Mathf.Max(8f, maxX - minX);   // 世界（= 屏幕）像素
            float centerX = (minX + maxX) * 0.5f;
            float y = minY + LabelBottomInset;
            float z = rect.position.z;

            SetRect(label.Main, width, LabelHeight, centerX, y, z, sx, sy);
            SetRect(label.Shadow, width, LabelHeight, centerX, y - 1f, z, sx, sy);

            if (_placementLogged < 3)
            {
                _placementLogged++;
                Log.Msg($"NameLabel place '{label.Text}': world w={width:0} y={y:0} " +
                        $"lossyScale=({scale.x:0.##},{scale.y:0.##}) local=({width / sx:0} x {LabelHeight / sy:0.##})");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("NameLabel.Place", ex);
        }
    }

    private static void SetRect(TreeNodeRender? node, float width, float height,
                                float centerX, float y, float z, float sx, float sy)
    {
        if (node == null) return;
        var rt = node.rectTransform;
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.localScale = new Vector3(1f / sx, 1f / sy, 1f);   // 抵消父级缩放：sizeDelta 即屏幕像素
        rt.sizeDelta = new Vector2(width, height);
        rt.position = new Vector3(centerX, y, z);
    }
}
