using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ItemDetails;

/// <summary>
/// 物品分类显示成 tooltip 上方的一排独立小框（每个分类一个框），
/// 不再占用 tooltip 正文的第一行。
///
/// 设计要点：
///   - 分类来源全部走游戏数据：GetGameItemType() + TypeHelper.GetTypeDisplayName()；
///     文字配色走 TypeHelper.GetTypeColor()（医疗/奢侈品/工具/武器等各有原版颜色），
///     不硬编码任何类型名与色值。
///   - 底板从 tooltip 自身的 Image 克隆，保持圆角与描边风格一致。
///   - 显隐跟随 OverlayHandler.ShowTooltip / HideTooltip。
///   - 记录时机在 GetTooltipBasic 前缀（见 TooltipPipeline.BasicPrefix）：原版分类行在
///     构建早期就写进正文，晚了既抑制不到，也会让浮窗用到上一件物品的残留。
///   - 拿不到分类名/颜色/模板时不显示，不做占位兜底。
/// </summary>
internal static class TypeBadge
{
    private const string HarmonyId = "item_details.type_badge";
    private const string BadgeName = "ItemDetailsTypeBadge";
    private const float ChipGap = 4f;
    private const float PadX = 6f;
    private const float PadY = 2f;
    private const float StaleSeconds = 0.5f;

    private sealed class Entry
    {
        internal string TypeConst = "";
        internal string Display = "";
    }

    private static Harmony? _harmony;
    private static GameObject? _badge;          // 容器（无渲染组件）
    private static Image? _chipTemplate;        // 底板模板（tooltip 自身背景）
    private static TMP_Text? _textTemplate;     // 文本模板

    private static readonly List<Entry> Pending = new();
    private static float _pendingAt = float.MinValue;

    internal static void Apply()
    {
        try
        {
            _harmony = new Harmony(HarmonyId);

            foreach (var m in new[]
            {
                AccessTools.Method(typeof(OverlayHandler), "ShowTooltip",
                    new[] { typeof(Vector2), typeof(RichText), typeof(int) }),
                AccessTools.Method(typeof(OverlayHandler), "ShowTooltip",
                    new[] { typeof(Vector2), typeof(Vector2), typeof(RichText), typeof(int) }),
            })
            {
                if (m != null)
                    _harmony.Patch(m, postfix: new HarmonyMethod(typeof(TypeBadge), nameof(AfterShow)));
            }

            // 正文里不再重复显示分类：拦截原版 " [类型]" 片段
            var add = AccessTools.Method(typeof(RichTextBuilder), "Add", new[]
            {
                typeof(string), typeof(RenderHandler.ColorPalette), typeof(string), typeof(bool), typeof(bool),
                typeof(bool), typeof(bool), typeof(RenderHandler.ColorPalette), typeof(RenderHandler.ColorPalette),
                typeof(RenderHandler.ColorPalette)
            });
            if (add != null)
                _harmony.Patch(add, prefix: new HarmonyMethod(typeof(TypeBadge), nameof(SuppressAdd)));

            var hide = AccessTools.Method(typeof(OverlayHandler), "HideTooltip");
            if (hide != null)
                _harmony.Patch(hide, postfix: new HarmonyMethod(typeof(TypeBadge), nameof(AfterHide)));

            Log.Msg("TypeBadge: patched OverlayHandler tooltip");
        }
        catch (Exception ex)
        {
            Log.Warn("TypeBadge.Apply", ex);
        }
    }

    internal static void Remove()
    {
        try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        _harmony = null;
        DestroyBadge();
    }

    /// <summary>由 GetTooltipBasic 前缀调用：记录本次物品的分类（必须在任何 Add 之前）。</summary>
    internal static void NoteItem(GameItem? item)
    {
        Pending.Clear();
        _pendingAt = UnityEngine.Time.unscaledTime;
        if (item == null) return;

        try
        {
            var types = item.GetGameItemType();
            if (types == null || types.Count == 0) return;

            foreach (string t in types)
            {
                if (string.IsNullOrEmpty(t)) continue;
                string name;
                try { name = TypeHelper.GetTypeDisplayName(t) ?? ""; } catch { name = ""; }
                if (name.Length == 0) continue;      // 取不到名字就不显示该分类
                Pending.Add(new Entry { TypeConst = t, Display = name });
            }
        }
        catch (Exception ex) { Log.Warn("TypeBadge.NoteItem", ex); }
    }

    // ---------------- Harmony 回调 ----------------

    private static bool SuppressAdd(string __0)
    {
        try
        {
            if (!TooltipPipeline.IsBuilding || Pending.Count == 0) return true;
            if (_textTemplate == null) return true;   // 还渲染不了浮窗时不要删掉原版分类行
            if (string.IsNullOrEmpty(__0)) return true;

            string probe = __0.Trim();
            foreach (var e in Pending)
                if (string.Equals(probe, "[" + e.Display + "]", StringComparison.Ordinal)) return false;
        }
        catch { }
        return true;
    }

    private static void AfterShow(OverlayHandler __instance)
    {
        try
        {
            // 超过 StaleSeconds 没有重新构建，说明这是缓存 tooltip，分类可能过期——不显示
            if (Pending.Count == 0 || UnityEngine.Time.unscaledTime - _pendingAt > StaleSeconds)
            {
                Hide();
                return;
            }

            var win = __instance?.GetTooltipWindow();
            if (win == null) { Hide(); return; }

            if (!BuildChips(win.rectTransform)) { Hide(); return; }
            Place(win.rectTransform);
            if (_badge != null) _badge.SetActive(true);
        }
        catch (Exception ex)
        {
            Log.Warn("TypeBadge.AfterShow", ex);
            Hide();
        }
    }

    private static void AfterHide()
    {
        try { Hide(); } catch { }
    }

    // ---------------- 构建 ----------------

    private static bool BuildChips(RectTransform tooltipRect)
    {
        var parent = tooltipRect.parent;
        if (parent == null) return false;

        if (_textTemplate == null)
            _textTemplate = tooltipRect.GetComponentInChildren<TMP_Text>(true);
        if (_textTemplate == null) { Log.Warn("TypeBadge.Template", "tooltip 内未找到 TMP 文本"); return false; }

        if (_chipTemplate == null)
            _chipTemplate = FindBackgroundTemplate(tooltipRect);

        if (_badge == null)
        {
            _badge = new GameObject(BadgeName, typeof(RectTransform));
            _badge.transform.SetParent(parent, false);
            var c = _badge.GetComponent<RectTransform>();
            c.anchorMin = new Vector2(0.5f, 0.5f);
            c.anchorMax = new Vector2(0.5f, 0.5f);
            c.pivot = new Vector2(0.5f, 0.5f);
        }

        var container = _badge.GetComponent<RectTransform>();

        // 清掉上一轮的框
        for (int i = container.childCount - 1; i >= 0; i--)
            try { UnityEngine.Object.Destroy(container.GetChild(i).gameObject); } catch { }

        // 先按文字算每个框的尺寸
        var widths = new List<float>();
        float height = 0f;
        foreach (var e in Pending)
        {
            float w, h;
            try
            {
                var pref = _textTemplate.GetPreferredValues(e.Display);
                w = Mathf.Ceil(pref.x) + PadX * 2f;
                h = Mathf.Ceil(pref.y) + PadY * 2f;
            }
            catch { w = 60f; h = 18f; }
            widths.Add(w);
            height = Mathf.Max(height, h);
        }

        float total = ChipGap * Mathf.Max(0, widths.Count - 1);
        foreach (float w in widths) total += w;

        container.sizeDelta = new Vector2(total, height);

        float x = -total * 0.5f;
        for (int i = 0; i < Pending.Count; i++)
        {
            float w = widths[i];
            var chip = CreateChip(Pending[i]);
            if (chip == null) continue;

            var r = chip.GetComponent<RectTransform>();
            r.SetParent(container, false);
            r.anchorMin = new Vector2(0.5f, 0.5f);
            r.anchorMax = new Vector2(0.5f, 0.5f);
            r.pivot = new Vector2(0.5f, 0.5f);
            r.sizeDelta = new Vector2(w, height);
            r.anchoredPosition = new Vector2(x + w * 0.5f, 0f);

            x += w + ChipGap;
        }

        return container.childCount > 0;
    }

    private static Image? FindBackgroundTemplate(RectTransform tooltipRect)
    {
        try
        {
            Image? best = null;
            foreach (var img in tooltipRect.GetComponentsInChildren<Image>(true))
            {
                if (img == null || img.sprite == null) continue;
                if (img.gameObject.name == BadgeName) continue;
                if (best == null ||
                    img.rectTransform.rect.width * img.rectTransform.rect.height >
                    best.rectTransform.rect.width * best.rectTransform.rect.height)
                    best = img;
            }
            return best;
        }
        catch { return null; }
    }

    /// <summary>一个分类框：底板（克隆 tooltip 背景）+ 居中文字（颜色取游戏类型色）。</summary>
    private static GameObject? CreateChip(Entry e)
    {
        GameObject chip;
        try
        {
            if (_chipTemplate != null && _chipTemplate.sprite != null)
            {
                chip = UnityEngine.Object.Instantiate(_chipTemplate.gameObject);
                chip.name = BadgeName + "_Chip";
                for (int i = chip.transform.childCount - 1; i >= 0; i--)
                    try { UnityEngine.Object.Destroy(chip.transform.GetChild(i).gameObject); } catch { }
                var img0 = chip.GetComponent<Image>();
                if (img0 != null) img0.raycastTarget = false;
            }
            else
            {
                chip = new GameObject(BadgeName + "_Chip", typeof(RectTransform), typeof(Image));
                var img1 = chip.GetComponent<Image>();
                img1.color = new Color(0.16f, 0.14f, 0.20f, 0.95f);
                img1.raycastTarget = false;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("TypeBadge.Chip", ex);
            return null;
        }

        if (_textTemplate == null) return chip;

        var textGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGo.transform.SetParent(chip.transform, false);
        var tmp = textGo.GetComponent<TextMeshProUGUI>();
        tmp.font = _textTemplate.font;
        tmp.fontSharedMaterial = _textTemplate.fontSharedMaterial;
        tmp.fontSize = Mathf.Max(10f, _textTemplate.fontSize * 0.85f);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.raycastTarget = false;

        // 配色：类型色作背景，文字白色；背景过亮时文字转黑
        Color bg = new Color(0.16f, 0.14f, 0.20f, 0.95f);
        try { bg = TypeHelper.GetTypeColor(e.TypeConst); } catch { }
        bg.a = 1f;

        var chipImage = chip.GetComponent<Image>();
        if (chipImage != null) chipImage.color = bg;

        Color fg = Luminance(bg) > 0.6f ? Color.black : Color.white;
        tmp.color = fg;
        tmp.text = e.Display;

        // 边框与文字同色
        var outline = chip.GetComponent<Outline>();
        if (outline == null) outline = chip.AddComponent<Outline>();
        if (outline != null)
        {
            outline.effectColor = fg;
            outline.effectDistance = new Vector2(1f, 1f);
            outline.useGraphicAlpha = false;
        }

        var tr = textGo.GetComponent<RectTransform>();
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.pivot = new Vector2(0.5f, 0.5f);
        tr.offsetMin = new Vector2(1f, PadY * 0.5f);
        tr.offsetMax = new Vector2(-1f, -PadY * 0.5f);

        return chip;
    }

    /// <summary>整排放在 tooltip 正上方并水平居中。</summary>
    private static void Place(RectTransform tooltipRect)
    {
        if (_badge == null) return;
        var rect = _badge.GetComponent<RectTransform>();
        if (rect == null) return;

        // 不能用 tooltipRect.position 当中心：该 RectTransform 的 pivot 不一定在中心
        // （像素风窗口常以左上为 pivot）。改用顶边中点换算到世界坐标，与 pivot 无关。
        // 左对齐：容器左边缘贴住 tooltip 左边缘（不再整排居中）
        Vector3 leftTop = tooltipRect.TransformPoint(
            new Vector3(tooltipRect.rect.xMin, tooltipRect.rect.yMax, 0f));

        float scale = tooltipRect.lossyScale.y;
        if (scale <= 0f) scale = 1f;

        float gap = 4f * scale;
        float halfBadge = rect.rect.height * 0.5f * scale;
        float halfWidth = rect.rect.width * 0.5f * scale;

        Vector3 pos = leftTop + new Vector3(halfWidth, gap + halfBadge, 0f);
        pos.z = leftTop.z;
        rect.position = pos;
    }

    /// <summary>感知亮度（0..1），用于决定文字用白还是黑。</summary>
    private static float Luminance(Color c) => 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
    private static void Hide()
    {
        try { if (_badge != null) _badge.SetActive(false); } catch { }
    }

    private static void DestroyBadge()
    {
        try { if (_badge != null) UnityEngine.Object.Destroy(_badge); } catch { }
        _badge = null;
        _chipTemplate = null;
        _textTemplate = null;
        Pending.Clear();
    }
}
