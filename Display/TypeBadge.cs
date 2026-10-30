using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ItemDetails;

/// <summary>
/// 物品分类显示成 tooltip 上方的一排独立小标签（每个分类一个），
/// 不再占用 tooltip 正文的第一行。
///
/// 设计要点：
///   - 分类来源全部走游戏数据：GetGameItemType() + TypeHelper.GetTypeDisplayName()。
///   - 默认“有色实底 + 白字 + 浅色描边”，颜色按语义分组：
///     医疗/水/麻醉品蓝、烟酒黑底白字、奢侈品粉、食物绿、日用品青、
///     武器/护甲青绿、工具/材料紫、违禁/受限暗红、其余灰色。
///     淡化模式下非违禁分类改用原版 tooltip 的深色底 + 提亮后的类型文字色，
///     违禁/受限同样用深色底，只保留红色警示文字。
///     底板必须使用无 sprite 的纯白 quad；复用 tooltip 的深色花纹 sprite 会乘色，
///     把粉色/灰色压成暗红/棕色。
///   - 开关：设置里的「物品分类标签」（`show_type_badge`）。关闭后本模块不再抑制
///     原版正文里的「[类型]」，也不会创建标签，等于完全回到原版。
///   - 显隐跟随 OverlayHandler.ShowTooltip / HideTooltip。
///   - 记录时机在 GetTooltipBasic 前缀（见 TooltipPipeline.BasicPrefix）：原版分类行在
///     构建早期就写进正文，晚了既抑制不到，也会让浮窗用到上一件物品的残留。
///   - 拿不到分类名/颜色/模板时不显示，不做占位兜底。
/// </summary>
internal static class TypeBadge
{
    /// <summary>
    /// tooltip 正文的字号（换算成世界像素），供其它自绘文本对齐同一套字号。
    /// tooltip 还没出现过时返回兜底值。
    /// </summary>
    internal static float TooltipFontWorldSize
    {
        get
        {
            try
            {
                var t = _textTemplate;
                if (t == null) return 14f;
                float scale = t.rectTransform != null ? Mathf.Abs(t.rectTransform.lossyScale.y) : 1f;
                if (scale < 0.001f) scale = 1f;
                float size = t.fontSize * scale;
                return size > 1f ? size : 14f;
            }
            catch { return 14f; }
        }
    }

    /// <summary>tooltip 正文用的 TMP 字体（一定含当前语言字形），供其它自绘文本对齐同一字体。</summary>
    internal static TMP_FontAsset? TooltipFont
    {
        get
        {
            try { return _textTemplate != null ? _textTemplate.font : null; }
            catch { return null; }
        }
    }

    private const string HarmonyId = "item_details.type_badge";
    private const string BadgeName = "ItemDetailsTypeBadge";
    private const float ChipGap = 4f;
    private const float PadX = 6f;    // 带底板时左右留白，避免文字贴边
    private const float PadY = 2f;
    private const float StaleSeconds = 0.5f;

    private sealed class Entry
    {
        internal string TypeConst = "";
        internal string Display = "";
    }

    private static bool _patched;
    private static Harmony? _harmony;
    private static GameObject? _badge;          // 容器（无渲染组件）
    private static Image? _chipTemplate;        // 淡化模式的原 tooltip 深色底层模板
    private static TMP_Text? _textTemplate;     // 文本模板

    private static readonly List<Entry> Pending = new();
    private static readonly List<Entry> Built = new();
    private static readonly List<float> Widths = new();
    private static Transform? _builtParent;
    private static bool _builtMuted;
    private static float _pendingAt = float.MinValue;

#if ITEMDETAILS_DIAG
    // 独立性能窗口：正文 [perf] 不包含 ShowTooltip 后缀，分类标签必须单独计时。
    private static float _perfLogAt;
    private static int _perfNotes;
    private static int _perfShows;
    private static int _perfLayouts;
    private static double _perfNoteMs;
    private static double _perfShowMs;
    private static double _perfMaxShowMs;
#endif

    internal static void Apply()
    {
        if (_patched) return;
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

            _patched = true;
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
        _patched = false;
        DestroyBadge();
    }

    /// <summary>由 GetTooltipBasic 前缀调用：记录本次物品的分类（必须在任何 Add 之前）。</summary>
    internal static void NoteItem(GameItem? item)
    {
#if ITEMDETAILS_DIAG
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        try
        {
            Pending.Clear();
            _pendingAt = UnityEngine.Time.unscaledTime;
            // 开关关闭：不记录分类，也就不会抑制原版正文里的「[类型]」。
            if (!ModSettings.TypeBadge) return;
            if (item == null) return;

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
#if ITEMDETAILS_DIAG
        finally
        {
            _perfNotes++;
            _perfNoteMs += ElapsedMs(started);
        }
#endif
    }

    // ---------------- Harmony 回调 ----------------

    private static bool SuppressAdd(string __0)
    {
        try
        {
            if (!ModSettings.TypeBadge) return true;   // 关闭后放行原版「[类型]」
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
#if ITEMDETAILS_DIAG
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        bool laidOut = false;
        try
        {
            if (!ModSettings.TypeBadge) { Hide(); return; }

            // 超过 StaleSeconds 没有重新构建，说明这是缓存 tooltip，分类可能过期——不显示
            if (Pending.Count == 0 || UnityEngine.Time.unscaledTime - _pendingAt > StaleSeconds)
            {
                Hide();
                return;
            }

            var win = __instance?.GetTooltipWindow();
            if (win == null) { Hide(); return; }

            if (!BuildChips(win.rectTransform, out laidOut)) { Hide(); return; }
            Place(win.rectTransform);
            if (_badge != null) _badge.SetActive(true);
        }
        catch (Exception ex)
        {
            Log.Warn("TypeBadge.AfterShow", ex);
            Hide();
        }
#if ITEMDETAILS_DIAG
        finally
        {
            TrackShowPerf(started, laidOut);
        }
#endif
    }

#if ITEMDETAILS_DIAG
    private static double ElapsedMs(long started) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0
        / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>每 2 秒汇总一次，避免诊断日志本身成为新的卡顿来源。</summary>
    private static void TrackShowPerf(long started, bool laidOut)
    {
        _perfShows++;
        if (laidOut) _perfLayouts++;
        double ms = ElapsedMs(started);
        _perfShowMs += ms;
        if (ms > _perfMaxShowMs) _perfMaxShowMs = ms;

        float now = UnityEngine.Time.unscaledTime;
        if (_perfLogAt <= 0f) _perfLogAt = now + 2f;
        if (now < _perfLogAt) return;

        double noteAvg = _perfNotes > 0 ? _perfNoteMs / _perfNotes : 0.0;
        double showAvg = _perfShows > 0 ? _perfShowMs / _perfShows : 0.0;
        Log.Msg($"[perf] 分类标签（2秒）: 解析={_perfNotes}次/{noteAvg:0.00}ms均次，" +
                $"显示={_perfShows}次/{showAvg:0.00}ms均次/{_perfMaxShowMs:0.00}ms峰值，" +
                $"布局更新={_perfLayouts}次，直接复用={_perfShows - _perfLayouts}次");

        _perfNotes = 0;
        _perfShows = 0;
        _perfLayouts = 0;
        _perfNoteMs = 0.0;
        _perfShowMs = 0.0;
        _perfMaxShowMs = 0.0;
        _perfLogAt = now + 2f;
    }
#endif

    private static void AfterHide()
    {
        try { Hide(); } catch { }
    }

    // ---------------- 构建 ----------------

    private static bool BuildChips(RectTransform tooltipRect, out bool laidOut)
    {
        laidOut = false;
        var parent = tooltipRect.parent;
        if (parent == null) return false;

        // Unity 的"假 null"：界面重建后这些引用会变成已销毁对象，必须重新取。
        if (_textTemplate == null)
            _textTemplate = tooltipRect.GetComponentInChildren<TMP_Text>(true);
        if (_textTemplate == null) { Log.Warn("TypeBadge.Template", "tooltip 内未找到 TMP 文本"); return false; }
        if (_chipTemplate == null) _chipTemplate = FindBackgroundTemplate(tooltipRect);

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

        // tooltip 每次重建都会走到这里。分类没变时整排复用，避免反复 Instantiate/Destroy；
        // 分类变化时也只增减必要节点，并更新现有 TMP。
        if (container.parent == parent && ReferenceEquals(_builtParent, parent)
            && _builtMuted == ModSettings.TypeBadgeMuted
            && container.childCount == Pending.Count && SameContent())
            return true;

        laidOut = true;
        if (container.parent != parent) container.SetParent(parent, false);

        for (int i = container.childCount - 1; i >= Pending.Count; i--)
            try { UnityEngine.Object.Destroy(container.GetChild(i).gameObject); } catch { }

        int count = Math.Min(container.childCount, Pending.Count);
        for (int i = count; i < Pending.Count; i++)
        {
            var chip = CreateChip(Pending[i]);
            if (chip == null) break;
            chip.transform.SetParent(container, false);
            count++;
        }

        Widths.Clear();
        float height = 0f;
        for (int i = 0; i < count; i++)
        {
            float w, h;
            try
            {
                var pref = _textTemplate.GetPreferredValues(Pending[i].Display);
                w = Mathf.Ceil(pref.x) + PadX * 2f;
                h = Mathf.Ceil(pref.y) + PadY * 2f;
            }
            catch { w = 60f; h = 18f; }
            Widths.Add(w);
            height = Mathf.Max(height, h);
        }

        float total = ChipGap * Mathf.Max(0, count - 1);
        foreach (float w in Widths) total += w;
        container.sizeDelta = new Vector2(total, height);

        float x = -total * 0.5f;
        for (int i = 0; i < count; i++)
        {
            float w = Widths[i];
            var chip = container.GetChild(i).gameObject;
            ConfigureChip(chip, Pending[i]);

            var r = chip.GetComponent<RectTransform>();
            r.anchorMin = new Vector2(0.5f, 0.5f);
            r.anchorMax = new Vector2(0.5f, 0.5f);
            r.pivot = new Vector2(0.5f, 0.5f);
            r.sizeDelta = new Vector2(w, height);
            r.anchoredPosition = new Vector2(x + w * 0.5f, 0f);

            x += w + ChipGap;
        }

        if (count == Pending.Count)
        {
            Built.Clear();
            Built.AddRange(Pending);
            _builtParent = parent;
            _builtMuted = ModSettings.TypeBadgeMuted;
        }
        return count > 0;
    }

    private static bool SameContent()
    {
        if (Built.Count != Pending.Count) return false;
        for (int i = 0; i < Built.Count; i++)
            if (Built[i].TypeConst != Pending[i].TypeConst || Built[i].Display != Pending[i].Display)
                return false;
        return true;
    }

    /// <summary>找 tooltip 内最大的带 Sprite Image，沿用原版的深色底、圆角和九宫格参数。</summary>
    private static Image? FindBackgroundTemplate(RectTransform tooltipRect)
    {
        try
        {
            Image? best = null;
            foreach (var img in tooltipRect.GetComponentsInChildren<Image>(true))
            {
                if (img == null || img.sprite == null) continue;
                if (best == null
                    || img.rectTransform.rect.width * img.rectTransform.rect.height
                    > best.rectTransform.rect.width * best.rectTransform.rect.height)
                {
                    best = img;
                }
            }
            return best;
        }
        catch { return null; }
    }

    /// <summary>一个分类标签：默认彩色底白字；淡化模式使用深色底与提亮后的类型文字色。</summary>
    private static GameObject? CreateChip(Entry e)
    {
        GameObject chip;
        try
        {
            chip = new GameObject(BadgeName + "_Chip", typeof(RectTransform), typeof(Image));
            var img = chip.GetComponent<Image>();
            if (img != null)
            {
                // 不使用 tooltip 的深色花纹 sprite：Image.color 会与纹理相乘，
                // 导致粉色/灰色整体变暗偏红。sprite=null 时 UI 使用白色 1x1 纹理，
                // 颜色就是这里的 bg 本身。
                img.sprite = null;
                img.type = Image.Type.Simple;
                img.fillCenter = true;
                img.raycastTarget = false;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("TypeBadge.Chip", ex);
            return null;
        }

        // 淡化模式不再叠浅色块：chip 根节点本身就是原版深色底，
        // 文字改用提亮后的分类色；否则深色底会被盖住，看起来仍是白底。
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

        var tr = textGo.GetComponent<RectTransform>();
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.pivot = new Vector2(0.5f, 0.5f);
        tr.offsetMin = new Vector2(2f, PadY * 0.5f);
        tr.offsetMax = new Vector2(-2f, -PadY * 0.5f);

        ConfigureChip(chip, e);
        return chip;
    }

    private static void ConfigureChip(GameObject chip, Entry e)
    {
        bool muted = ModSettings.TypeBadgeMuted;
        ChipColors(e.TypeConst, out Color bg, out Color border);
        Color fg = Color.white;

        if (muted)
        {
            // 淡化模式：底色恢复成原版 tooltip 的深色底，文字用提亮后的分类色。
            // 游戏原始类型色偏暗（默认 #4f464b、水 #355271），直接放深色底上几乎不可读，
            // 所以只提亮、不改色相，保证还能分辨类别。
            Color typeColor = TypeHelper.GetTypeColor(e.TypeConst);
            fg = Color.Lerp(typeColor, Color.white, MutedTextLighten);
            fg.a = 1f;
            border = new Color(fg.r, fg.g, fg.b, 0.45f);
            if (IsWarning(e.TypeConst))
            {
                fg = WarningText;
                border = new Color(WarningText.r, WarningText.g, WarningText.b, 0.45f);
            }
        }

        var img = chip.GetComponent<Image>();
        if (img != null)
        {
            if (muted)
            {
                ApplyMutedBackground(img);
            }
            else
            {
                img.sprite = null;
                img.type = Image.Type.Simple;
                img.fillCenter = true;
                img.color = bg;
            }
            img.raycastTarget = false;
        }

        // 旧版本残留的浅色填充层：一律关闭，避免再次盖住深色底。
        var staleFill = chip.transform.Find("Fill")?.GetComponent<Image>();
        if (staleFill != null) staleFill.enabled = false;

        var tmp = chip.GetComponentInChildren<TMP_Text>(true);
        if (tmp != null)
        {
            if (_textTemplate != null)
            {
                tmp.font = _textTemplate.font;
                tmp.fontSharedMaterial = _textTemplate.fontSharedMaterial;
                tmp.fontSize = Mathf.Max(10f, _textTemplate.fontSize * 0.85f);
            }
            tmp.color = fg;
            if (tmp.text != e.Display) tmp.text = e.Display;
        }

        var outline = chip.GetComponent<Outline>();
        if (outline == null) outline = chip.AddComponent<Outline>();
        if (outline != null)
        {
            // 深色底上再描边只会糊住字形，淡化模式直接关掉。
            outline.enabled = !muted;
            outline.effectColor = border;
            outline.effectDistance = new Vector2(1f, 1f);
            outline.useGraphicAlpha = false;
        }
    }

    /// <summary>淡化模式的底色：优先复用 tooltip 自身的深色底 sprite，取不到时用同色系纯色。</summary>
    private static void ApplyMutedBackground(Image img)
    {
        if (_chipTemplate != null && _chipTemplate.sprite != null)
        {
            img.sprite = _chipTemplate.sprite;
            img.type = _chipTemplate.type;
            img.fillCenter = _chipTemplate.fillCenter;
            img.color = _chipTemplate.color;
            return;
        }

        img.sprite = null;
        img.type = Image.Type.Simple;
        img.fillCenter = true;
        img.color = FallbackDarkBg;
    }

    private static readonly Color BlueBg = new Color32(21, 101, 192, 250);
    private static readonly Color BlueBorder = new Color32(115, 192, 255, 255);
    private static readonly Color PinkBg = new Color32(196, 44, 122, 250);
    private static readonly Color PinkBorder = new Color32(255, 154, 213, 255);
    private static readonly Color BlackBg = new Color32(18, 18, 18, 250);
    private static readonly Color BlackBorder = new Color32(208, 208, 208, 255);
    private static readonly Color CyanBg = new Color32(0, 124, 145, 250);
    private static readonly Color CyanBorder = new Color32(92, 225, 242, 255);
    private static readonly Color FoodGreenBg = new Color32(46, 125, 50, 250);
    private static readonly Color FoodGreenBorder = new Color32(130, 217, 139, 255);
    private static readonly Color WeaponTealBg = new Color32(0, 105, 92, 250);
    private static readonly Color WeaponTealBorder = new Color32(99, 216, 204, 255);
    private static readonly Color PurpleBg = new Color32(85, 58, 133, 250);
    private static readonly Color PurpleBorder = new Color32(195, 166, 255, 255);
    private static readonly Color WarningBg = new Color32(87, 26, 28, 250);
    private static readonly Color WarningBorder = new Color32(255, 148, 133, 255);
    private static readonly Color NeutralBg = new Color32(85, 85, 85, 250);
    private static readonly Color NeutralBorder = new Color32(192, 192, 192, 255);
    /// <summary>淡化模式的兜底底色，取自 tooltip 自身的深紫黑。</summary>
    private static readonly Color FallbackDarkBg = new Color32(36, 31, 43, 255);
    /// <summary>违禁/受限在深色底上的警示文字色。</summary>
    private static readonly Color WarningText = new Color32(255, 148, 133, 255);
    /// <summary>与白色混合的比例：提亮并降低饱和度，保持色相。</summary>
    private const float MutedTextLighten = 0.55f;

    private static bool IsWarning(string typeConst)
    {
        switch ((typeConst ?? "").ToUpperInvariant())
        {
            case "CONTRABAND":
            case "RESTRICTED_1":
            case "RESTRICTED_2":
            case "RESTRICTED_3":
                return true;
            default:
                return false;
        }
    }

    private static void ChipColors(string typeConst, out Color bg, out Color border)
    {
        switch ((typeConst ?? "").ToUpperInvariant())
        {
            case "CONTRABAND":
            case "RESTRICTED_1":
            case "RESTRICTED_2":
            case "RESTRICTED_3":
                bg = WarningBg; border = WarningBorder; return;

            case "MEDICAL":
            case "MEDICAL_TOOL":
            case "ORGAN":
            case "WATER":
            case "WATER_RATION":
            case "WATER_PURIFICATION_SUPPLY":
            case "NARCOTIC":
            case "POISON":
                bg = BlueBg; border = BlueBorder; return;

            case "LUXURY_ITEM":
            case "ART":
                bg = PinkBg; border = PinkBorder; return;

            case "ALCOHOL":
            case "SUBSTANCE":
                bg = BlackBg; border = BlackBorder; return;

            case "HOUSEHOLD_GOOD":
                bg = CyanBg; border = CyanBorder; return;

            case "FOOD":
            case "FRESH_FOOD":
            case "PROCESSED_FOOD":
            case "TREAT":
            case "BREVAGE":
            case "FARMING_SUPPLIES":
            case "SEED":
            case "MODIFIED_SEED":
                bg = FoodGreenBg; border = FoodGreenBorder; return;

            case "WEAPON":
            case "MELEE_WEAPON":
            case "FIREARM":
            case "MODDABLE":
            case "GUN_MOD":
            case "AMMUNITION":
            case "MUNITION":
            case "ARMOR":
                bg = WeaponTealBg; border = WeaponTealBorder; return;

            case "TOOL":
            case "MACHINE":
            case "MATERIAL":
            case "BASIC_MATERIAL":
            case "REFINED_MATERIAL":
            case "ENERGY_CELL":
            case "MODULE":
            case "NODE":
            case "HYDROPONIC_MODULE":
            case "ALARM_MODULE":
            case "PRINTER_MODULE":
            case "EVAPORATOR_MODULE":
            case "FILTER":
                bg = PurpleBg; border = PurpleBorder; return;

            default:
                bg = NeutralBg; border = NeutralBorder; return;
        }
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
        _builtParent = null;
        _builtMuted = false;
        Pending.Clear();
        Built.Clear();
        Widths.Clear();
    }
}
