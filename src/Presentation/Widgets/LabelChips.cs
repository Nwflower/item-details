using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ItemDetails;

/// <summary>
/// 正文里的「标签色块」：在原版 tooltip 文字下面垫实心矩形，让「效率 / 性能 / 质量」这类标签带上底色。
///
/// 为什么不用 TMP 的 &lt;mark&gt;：这版游戏里 mark 画在字形上层，不透明就盖住文字，半透明又把字色染脏。
/// 矩形层把底色放到文字后面，底色可以完全不透明，还能带左侧亮条。
///
/// 做法：
///   - 逻辑层只在<b>渲染写法</b>里用 &lt;link="idchip:+"&gt; 包住标签（规范文本不变，第三方读到的还是原文）；
///   - 原版窗口显示后，找出 tooltip 正文 TMP 里所有 idchip 链接，按链接范围内字符的位置量出矩形；
///   - 矩形放在一个与正文同父节点、排在正文前面的容器里，容器每帧照抄正文的位置/缩放，
///     所以窗口跟着鼠标移动不必重算；只有文本或排版尺寸变了才重新量。
///
/// 兼容：
///   - 矩形只由「正文里真有 idchip 链接」触发。别的模组重排、替换了正文，链接没了，矩形随之消失，不会残留；
///   - 画不出来（补丁没打上、字段取不到、出错）时 <see cref="Available"/> 为 false，
///     调用方退回不带色块的原文，不会留下孤零零的链接标签。
/// </summary>
internal static class LabelChips
{
    private const string ContainerName = "ItemDetailsLabelChips";
    internal const string LinkPrefix = "idchip:";

    // 配色取自 AdvancedUI 的加成行：深色底 + 同色系亮色左条与字。正向绿、负向红。
    internal static readonly string PositiveText = "#D4F2B0";
    internal static readonly string NegativeText = "#FFD3D0";
    /// <summary>色块右侧数值的正负色（取自 AdvancedUI 的加成数值）。</summary>
    internal static readonly string ValuePositive = "#90C65B";
    internal static readonly string ValueNegative = "#E78885";
    /// <summary>紫色色块（自学习）：取自 AdvancedUI 的性能紫。</summary>
    internal static readonly string PurpleText = "#E3CCF5";
    private static readonly Color32 PurpleBg = new(0x65, 0x37, 0x8B, 0xFF);
    private static readonly Color32 PurpleBar = new(0xB6, 0x8A, 0xDB, 0xFF);
    /// <summary>琥珀色块：提示类（水不多了、将要不够）。</summary>
    internal static readonly string AmberText = "#FFEBB5";
    private static readonly Color32 AmberBg = new(0x7A, 0x5A, 0x1F, 0xFF);
    private static readonly Color32 AmberBar = new(0xE0, 0xB8, 0x4A, 0xFF);

    /// <summary>青蓝色块（自学习）：与原来那一行 LightCyan 文字同色系。</summary>
    internal static readonly string CyanText = "#D2F4F7";
    private static readonly Color32 CyanBg = new(0x27, 0x6C, 0x73, 0xFF);
    private static readonly Color32 CyanBar = new(0x5C, 0xC1, 0xCB, 0xFF);
    private static readonly Color32 PositiveBg = new(0x3E, 0x6B, 0x2E, 0xFF);
    private static readonly Color32 PositiveBar = new(0x90, 0xC6, 0x5B, 0xFF);
    private static readonly Color32 NegativeBg = new(0x7A, 0x33, 0x31, 0xFF);
    private static readonly Color32 NegativeBar = new(0xE7, 0x88, 0x85, 0xFF);
    /// <summary>左侧亮条宽度占行高的比例。</summary>
    private const float BarRatio = 0.14f;
    /// <summary>色块上下各收进的比例（取整后 1–2 像素）。</summary>
    private const float InsetRatio = 0.08f;

    private static bool _installed;
    private static bool _broken;
    private static GameObject? _container;
    private static RectTransform? _containerRect;

    private static TMP_Text? _builtText;
    private static string? _builtString;
    private static Vector2 _builtSize;
    private static readonly List<GameObject> Chips = new();

    /// <summary>补丁打上且没出过错：调用方据此决定写不写 idchip 链接。</summary>
    /// <summary>「提示框文字优化」关闭时同样不可用：调用方走不带色块、亮条与缩进的纯文字写法。</summary>
    internal static bool Available => _installed && !_broken && ModSettings.TooltipFormatting;

    /// <summary>色块种类：'+' 绿底、'-' 红底、'p' 紫底、'c' 青蓝底、'y' 琥珀底。</summary>
    internal static string Wrap(string inner, char kind)
        => "<link=\"" + LinkPrefix + kind + "\">" + inner + "</link>";

    internal static string Wrap(string inner, bool positive) => Wrap(inner, positive ? '+' : '-');

    internal static void Install(PatchScope scope)
    {
        try
        {
            bool patched = false;   // 只认「显示」补丁：没有它就没有任何东西画底色，不能算可用
            foreach (var m in new[]
            {
                AccessTools.Method(typeof(OverlayHandler), "ShowTooltip",
                    new[] { typeof(Vector2), typeof(RichText), typeof(int) }),
                AccessTools.Method(typeof(OverlayHandler), "ShowTooltip",
                    new[] { typeof(Vector2), typeof(Vector2), typeof(RichText), typeof(int) }),
            })
            {
                patched |= scope.Patch(m, postfix: new HarmonyMethod(typeof(LabelChips), nameof(AfterShow)));
            }
            scope.Patch(AccessTools.Method(typeof(OverlayHandler), "HideTooltip"),
                postfix: new HarmonyMethod(typeof(LabelChips), nameof(AfterHide)));

            _installed = patched;
            scope.OnDispose(Remove);
        }
        catch (Exception ex) { Log.Warn("LabelChips.Apply", ex); }
    }

    internal static void Remove()
    {
        _installed = false;
        _broken = false;
        try { if (_container != null) UnityEngine.Object.Destroy(_container); } catch { }
        _container = null;
        _containerRect = null;
        _builtText = null;
        _builtString = null;
        Chips.Clear();
    }

    private static void AfterHide()
    {
        try { Hide(); } catch { }
    }

    private static void Hide()
    {
        try { if (_container != null) _container.SetActive(false); } catch { }
    }

    private static void AfterShow()
    {
        if (_broken) return;   // 出过错就不再每帧重试
        try
        {
            var tmp = SpriteProgressBar.TooltipText();
            string? text = tmp != null ? tmp.text : null;
            if (tmp == null || !tmp.isActiveAndEnabled || text == null
                || text.IndexOf(LinkPrefix, StringComparison.Ordinal) < 0)
            {
                Hide();
                return;
            }
            var parent = tmp.rectTransform.parent;
            if (parent == null) { Hide(); return; }

            if (_container == null || _container.transform.parent != parent) CreateContainer(tmp, parent);
            if (_containerRect == null) { Hide(); return; }

            Mirror(tmp);

            Vector2 size = tmp.rectTransform.rect.size;
            if (!ReferenceEquals(_builtText, tmp) || !string.Equals(_builtString, text, StringComparison.Ordinal)
                || _builtSize != size)
            {
                Rebuild(tmp);
                _builtText = tmp;
                _builtString = text;
                _builtSize = size;
            }
            _container!.SetActive(true);
        }
        catch (Exception ex)
        {
            // 画不出来就停用：之后调用方改回不带色块的原文
            _broken = true;
            Log.Warn("LabelChips.Show", ex);
            Hide();
        }
    }

    private static void CreateContainer(TMP_Text tmp, Transform parent)
    {
        if (_container != null) { try { UnityEngine.Object.Destroy(_container); } catch { } }
        _container = new GameObject(ContainerName, typeof(RectTransform), typeof(LayoutElement));
        _container.layer = tmp.gameObject.layer;
        _container.GetComponent<LayoutElement>().ignoreLayout = true;
        _containerRect = (RectTransform)_container.transform;
        _containerRect.SetParent(parent, false);
        Chips.Clear();
        _builtText = null;
    }

    /// <summary>容器照抄正文的位置、缩放与枢轴，排在正文前面（画在它下面）。</summary>
    private static void Mirror(TMP_Text tmp)
    {
        var src = tmp.rectTransform;
        var dst = _containerRect!;
        dst.anchorMin = src.anchorMin;
        dst.anchorMax = src.anchorMax;
        dst.pivot = src.pivot;
        dst.sizeDelta = src.sizeDelta;
        dst.anchoredPosition = src.anchoredPosition;
        dst.localScale = src.localScale;
        dst.localRotation = src.localRotation;

        int at = src.GetSiblingIndex();
        int mine = dst.GetSiblingIndex();
        if (mine > at || at - mine > 1) dst.SetSiblingIndex(mine > at ? at : at - 1);
    }

    private static void Rebuild(TMP_Text tmp)
    {
        // 子节点先隐藏再销毁：Destroy 要到帧末才生效，免得新旧两套叠一帧
        foreach (var old in Chips)
        {
            try { old.SetActive(false); UnityEngine.Object.Destroy(old); } catch { }
        }
        Chips.Clear();

        tmp.ForceMeshUpdate();
        var info = tmp.textInfo;
        if (info == null || info.linkCount <= 0) return;

        for (int i = 0; i < info.linkCount; i++)
        {
            var link = info.linkInfo[i];
            string id = link.GetLinkID();
            if (!id.StartsWith(LinkPrefix, StringComparison.Ordinal) || link.linkTextLength <= 0) continue;
            char kind = id.Length <= LinkPrefix.Length ? '+' : id[LinkPrefix.Length];

            // 取链接文字的第一行（色块不跨行）
            int first = link.linkTextfirstCharacterIndex;
            int last = first + link.linkTextLength - 1;
            if (first < 0 || last >= info.characterCount) continue;
            int line = info.characterInfo[first].lineNumber;
            while (last > first && info.characterInfo[last].lineNumber != line) last--;

            var a = info.characterInfo[first];
            var b = info.characterInfo[last];
            float left = a.origin;
            float right = b.xAdvance;
            float bottom = Mathf.Min(a.descender, b.descender);
            float top = Mathf.Max(a.ascender, b.ascender);
            // 上下各收一点，色块和相邻行之间留出缝
            float inset = Mathf.Clamp(Mathf.Round((top - bottom) * InsetRatio), 1f, 2f);
            bottom += inset;
            top -= inset;
            if (!(right > left) || !(top > bottom)) continue;

            AddChip(left, bottom, right - left, top - bottom, kind);
        }
    }

    private static void AddChip(float x, float y, float w, float h, char kind)
    {
        var go = new GameObject("Chip", typeof(RectTransform));
        go.layer = _container!.layer;
        var rect = (RectTransform)go.transform;
        rect.SetParent(_containerRect, false);
        rect.pivot = Vector2.zero;
        rect.localPosition = new Vector3(x, y, 0f);
        rect.sizeDelta = new Vector2(w, h);
        Chips.Add(go);

        MakeImage(go, kind == 'y' ? AmberBg : kind == 'c' ? CyanBg : kind == 'p' ? PurpleBg : kind == '-' ? NegativeBg : PositiveBg);

        float barW = Mathf.Max(1f, Mathf.Round(h * BarRatio));
        var bar = new GameObject("Bar", typeof(RectTransform));
        bar.layer = go.layer;
        var barRect = (RectTransform)bar.transform;
        barRect.SetParent(rect, false);
        barRect.anchorMin = Vector2.zero;
        barRect.anchorMax = new Vector2(0f, 1f);
        barRect.pivot = Vector2.zero;
        barRect.anchoredPosition = Vector2.zero;
        barRect.sizeDelta = new Vector2(barW, 0f);
        MakeImage(bar, kind == 'y' ? AmberBar : kind == 'c' ? CyanBar : kind == 'p' ? PurpleBar : kind == '-' ? NegativeBar : PositiveBar);
    }

    private static void MakeImage(GameObject go, Color32 color)
    {
        var img = go.AddComponent<Image>();
        // 不挂 sprite：UI 用白色 1x1 纹理，颜色就是这里给的值，不会被花纹乘色
        img.sprite = null;
        img.type = Image.Type.Simple;
        img.color = color;
        img.raycastTarget = false;
    }
}
