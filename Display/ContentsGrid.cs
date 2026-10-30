using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ItemDetails;

/// <summary>
/// 容器内容物图标格：tooltip 右侧一块独立面板，按物品图标 + 件数列出箱内物品，
/// 正文里的长清单随之缩成一行「N 类 M 件」。
///
/// 设计要点：
///   - 认领：<see cref="StorageInfo"/> / <see cref="SealedBoxInfo"/> 构建正文时调用 <see cref="Claim"/>，
///     返回 true 才把清单缩成摘要；面板在 OverlayHandler.ShowTooltip 后缀里按认领的物品现读现画。
///     tooltip 还从没显示过（拿不到字体模板）或面板出过错时不认领，正文保持完整清单。
///   - 分组口径与正文一致：按显示名合并，件数累加堆叠数；最多 15 格，超出时最后一格写「+件数」。
///   - 图标用物品自己的贴图（GameItemElement.background.image.sprite），不另建图集；
///     按整数倍缩放，保持像素风清晰。
///   - 底板复制 tooltip 窗口里覆盖大半窗口的 Image（外框与底色），和原版窗口一个样子；找不到时用纯色兜底。
///   - 放在 tooltip 右侧顶对齐，右边放不下就换到左侧；纵向不出屏幕。
///   - 与「当铺估价单」(amres.sectioned_tooltips) 共存：它也在右侧画未上锁容器的内容物网格，
///     载入它时未上锁容器让给它，只画它不画的带锁容器（透视）。
///   - 超过 StaleSeconds 没有重新构建（缓存 tooltip）时不显示，避免画成上一件物品。
/// </summary>
internal static class ContentsGrid
{
    private const string HarmonyId = "item_details.contents_grid";
    private const string PanelName = "ItemDetailsContentsGrid";
    private const float StaleSeconds = 0.5f;
    private const int MaxColumns = 5;
    private const int MaxCells = 15;
    private const float CountScale = 0.65f;

    private sealed class Cell
    {
        internal string Name = "";
        internal GameItem Item = null!;
        internal int Count;
    }

    private struct FramePart
    {
        internal Image Source;
        internal float Left, Bottom, Right, Top;
    }

    private static bool _patched;
    private static Harmony? _harmony;
    private static GameObject? _panel;
    private static TMP_Text? _template;
    private static bool _broken;
    private static bool? _slipLoaded;

    private static GameItem? _pendingItem;
    private static bool _pendingSealed;
    private static float _pendingAt = float.MinValue;

    // 已画好的内容；相同就只重新定位
    private static readonly List<Cell> Built = new();
    private static GameItem? _builtItem;
    private static bool _builtSealed;
    private static Transform? _builtParent;
    private static Vector2 _builtSize;

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
                    _harmony.Patch(m, postfix: new HarmonyMethod(typeof(ContentsGrid), nameof(AfterShow)));
            }
            var hide = AccessTools.Method(typeof(OverlayHandler), "HideTooltip");
            if (hide != null)
                _harmony.Patch(hide, postfix: new HarmonyMethod(typeof(ContentsGrid), nameof(AfterHide)));

            _patched = true;
            Log.Msg("ContentsGrid: patched OverlayHandler tooltip");
        }
        catch (Exception ex)
        {
            Log.Warn("ContentsGrid.Apply", ex);
        }
    }

    internal static void Remove()
    {
        try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        _harmony = null;
        _patched = false;
        try { if (_panel != null) UnityEngine.Object.Destroy(_panel); } catch { }
        _panel = null;
        _template = null;
        _broken = false;
        _pendingItem = null;
        Built.Clear();
        _builtItem = null;
        _builtParent = null;
    }

    /// <summary>由 GetTooltipBasic 前缀调用：新一次构建开始，清掉上一件物品的认领。</summary>
    internal static void Begin()
    {
        _pendingItem = null;
    }

    /// <summary>
    /// 正文构建时调用：本次 tooltip 的内容物交给图标格显示。返回 false 时调用方照常输出完整清单。
    /// </summary>
    internal static bool Claim(GameItem item, bool sealedBox)
    {
        try
        {
            if (!_patched || _broken || !ModSettings.ContentsGrid) return false;
            if (!TooltipPipeline.IsBuilding) return false;
            if (_template == null) return false;              // tooltip 还没显示过，画不出来
            if (!sealedBox && SlipLoaded) return false;        // 未上锁容器让给估价单的内容物面板

            _pendingItem = item;
            _pendingSealed = sealedBox;
            _pendingAt = UnityEngine.Time.unscaledTime;
            return true;
        }
        catch { return false; }
    }

    private static bool SlipLoaded => _slipLoaded ??= DetectSlip();

    private static bool DetectSlip()
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetName().Name == "SectionedTooltips") return true;
        }
        catch { }
        return false;
    }

    // ---------------- Harmony 回调 ----------------

    private static void AfterShow(OverlayHandler __instance)
    {
        try
        {
            var win = __instance?.GetTooltipWindow();
            var tooltipRect = win != null ? win.rectTransform : null;
            if (tooltipRect == null) { Hide(); return; }

            // 每次都顺手取模板：认领要先知道画得出来
            if (_template == null) _template = tooltipRect.GetComponentInChildren<TMP_Text>(true);

            var item = _pendingItem;
            if (item == null || _broken || !ModSettings.ContentsGrid
                || UnityEngine.Time.unscaledTime - _pendingAt > StaleSeconds)
            {
                Hide();
                return;
            }

            var cells = Group(item, out int total);
            if (cells == null || cells.Count == 0 || _template == null) { Hide(); return; }

            var parent = tooltipRect.parent;
            if (parent == null) { Hide(); return; }

            if (_panel == null || _panel.transform.parent != parent || !ReferenceEquals(_builtParent, parent)
                || !ReferenceEquals(_builtItem, item) || _builtSealed != _pendingSealed || !Same(cells))
            {
                Build(tooltipRect, parent, cells, total, _pendingSealed);
                _builtItem = item;
                _builtSealed = _pendingSealed;
                _builtParent = parent;
                Built.Clear();
                Built.AddRange(cells);
            }

            if (_panel == null) return;
            Place(tooltipRect, parent);
            _panel.transform.SetAsLastSibling();
            _panel.SetActive(true);
        }
        catch (Exception ex)
        {
            // 画不出来就停用：之后的 tooltip 不再认领，正文恢复完整清单
            _broken = true;
            Log.Warn("ContentsGrid.Show", ex);
            Hide();
        }
    }

    private static void AfterHide()
    {
        try { Hide(); } catch { }
    }

    private static void Hide()
    {
        try { if (_panel != null) _panel.SetActive(false); } catch { }
    }

    // ---------------- 数据 ----------------

    private static List<Cell>? Group(GameItem item, out int total)
    {
        total = 0;
        GameGridInventory? grid = null;
        try { grid = item.contentWindow?.child as GameGridInventory; } catch { }
        List<GameItem>? items = null;
        try { items = grid?.childItems; } catch { }
        if (items == null) return null;

        var cells = new List<Cell>();
        var byName = new Dictionary<string, Cell>(StringComparer.Ordinal);
        foreach (var child in items)
        {
            if (child == null) continue;
            string name = ContainerItemSignature.DisplayName(child);
            int stack = Math.Max(1, ContainerItemSignature.ReadStack(child));
            if (!byName.TryGetValue(name, out var cell))
            {
                cell = new Cell { Name = name, Item = child };
                byName[name] = cell;
                cells.Add(cell);
            }
            cell.Count += stack;
            total += stack;
        }
        return cells;
    }

    private static bool Same(List<Cell> cells)
    {
        if (Built.Count != cells.Count) return false;
        for (int i = 0; i < cells.Count; i++)
        {
            if (Built[i].Name != cells[i].Name || Built[i].Count != cells[i].Count
                || !ReferenceEquals(Built[i].Item, cells[i].Item))
                return false;
        }
        return true;
    }

    // ---------------- 构建 ----------------

    private static void Build(RectTransform tooltipRect, Transform parent, List<Cell> cells, int total, bool sealedBox)
    {
        var tmp = _template!;

        if (_panel == null)
        {
            _panel = new GameObject(PanelName, typeof(RectTransform));
            _panel.layer = tooltipRect.gameObject.layer;
        }
        var panel = (RectTransform)_panel.transform;
        if (panel.parent != parent) panel.SetParent(parent, false);
        panel.anchorMin = new Vector2(0.5f, 0.5f);
        panel.anchorMax = new Vector2(0.5f, 0.5f);
        panel.pivot = new Vector2(0f, 1f);
        panel.localRotation = Quaternion.identity;
        panel.localScale = tooltipRect.localScale;
        // Destroy 要到帧末才生效，先隐藏，免得新旧两套格子叠一帧
        for (int i = panel.childCount - 1; i >= 0; i--)
        {
            var old = panel.GetChild(i).gameObject;
            old.SetActive(false);
            UnityEngine.Object.Destroy(old);
        }

        // 尺寸都按 tooltip 窗口的本地单位算：TMP 字号先换算到窗口空间
        float winScale = Mathf.Abs(tooltipRect.lossyScale.y);
        float tmpScale = Mathf.Abs(tmp.rectTransform.lossyScale.y);
        float font = tmp.fontSize * (winScale > 0.0001f && tmpScale > 0.0001f ? tmpScale / winScale : 1f);
        if (font < 4f) font = 12f;
        float line = Mathf.Round(font * 1.25f);
        float cellSize = Mathf.Round(line * 1.8f);
        float gap = Mathf.Max(2f, Mathf.Round(font * 0.25f));
        float pad = Mathf.Max(4f, Mathf.Round(font * 0.6f));

        int shown = cells.Count <= MaxCells ? cells.Count : MaxCells - 1;
        int overflow = 0;
        for (int i = shown; i < cells.Count; i++) overflow += cells[i].Count;
        int slots = shown + (overflow > 0 ? 1 : 0);
        int cols = Math.Min(MaxColumns, slots);
        int rows = (slots + cols - 1) / cols;

        string title = sealedBox
            ? Loc.T("grid.title_sealed", "Inside {0}", total)
            : Loc.T("grid.title", "Contents {0}", total);
        float titleWidth = Mathf.Ceil(Measure(title, font));

        float gridWidth = cols * cellSize + (cols - 1) * gap;
        float width = Mathf.Ceil(Mathf.Max(2f * pad + gridWidth, 2f * pad + titleWidth));
        float top = pad + line + gap * 2f;
        float height = top + rows * cellSize + (rows - 1) * gap + pad;
        panel.sizeDelta = new Vector2(width, height);
        _builtSize = new Vector2(width, height);

        AddFrame(panel, tooltipRect, tmp);

        var title1 = NewText(panel, title, font, new Color(0.85f, 0.85f, 0.85f, 1f), TextAlignmentOptions.TopLeft);
        PlaceRect(title1.rectTransform, pad, pad, width - 2f * pad, line);

        var outer = new Color(1f, 1f, 1f, 0.07f);
        var inner = new Color(0f, 0f, 0f, 0.35f);
        for (int i = 0; i < slots; i++)
        {
            float x = pad + (i % cols) * (cellSize + gap);
            float y = top + (i / cols) * (cellSize + gap);
            PlaceRect(NewImage(panel, outer).rectTransform, x, y, cellSize, cellSize);
            PlaceRect(NewImage(panel, inner).rectTransform, x + 1f, y + 1f, cellSize - 2f, cellSize - 2f);

            if (i >= shown)
            {
                var more = NewText(panel, "+" + overflow, font, Color.white, TextAlignmentOptions.Center);
                PlaceRect(more.rectTransform, x, y, cellSize, cellSize);
                continue;
            }

            var sprite = IconOf(cells[i].Item);
            if (sprite != null)
            {
                Fit(sprite, cellSize - 4f, cellSize - 4f, out float w, out float h);
                if (w > 0f && h > 0f)
                {
                    var icon = NewImage(panel, Color.white);
                    icon.sprite = sprite;
                    PlaceRect(icon.rectTransform, x + Mathf.Round((cellSize - w) / 2f),
                        y + Mathf.Round((cellSize - h) / 2f), w, h);
                }
            }

            if (cells[i].Count > 1)
            {
                string count = cells[i].Count.ToString();
                float size = font * CountScale;
                var shadow = NewText(panel, count, size, new Color(0f, 0f, 0f, 0.9f), TextAlignmentOptions.BottomRight);
                PlaceRect(shadow.rectTransform, x + 1f, y + 1f, cellSize - 2f, cellSize - 1f);
                var label = NewText(panel, count, size, Color.white, TextAlignmentOptions.BottomRight);
                PlaceRect(label.rectTransform, x, y, cellSize - 2f, cellSize - 1f);
            }
        }
    }

    /// <summary>
    /// 复制 tooltip 窗口的外框与底色：窗口里覆盖大半窗口的 Image 按原内缩量铺到面板上。
    /// 一个都找不到时用深底 + 细边兜底。
    /// </summary>
    private static void AddFrame(RectTransform panel, RectTransform win, TMP_Text tmp)
    {
        var parts = new List<FramePart>();
        try
        {
            var r = win.rect;
            var corners = new Vector3[4];
            foreach (var img in win.GetComponentsInChildren<Image>(false))
            {
                if (img == null || !img.enabled || img.transform.IsChildOf(tmp.transform)) continue;
                img.rectTransform.GetWorldCorners(corners);
                Vector3 bl = win.InverseTransformPoint(corners[0]);
                Vector3 tr = win.InverseTransformPoint(corners[2]);
                if (tr.x - bl.x < r.width * 0.5f || tr.y - bl.y < r.height * 0.5f) continue;
                parts.Add(new FramePart
                {
                    Source = img,
                    Left = bl.x - r.xMin,
                    Bottom = bl.y - r.yMin,
                    Right = r.xMax - tr.x,
                    Top = r.yMax - tr.y,
                });
            }
        }
        catch (Exception ex) { Log.Warn("ContentsGrid.Frame", ex); parts.Clear(); }

        if (parts.Count == 0)
        {
            Stretch(NewImage(panel, new Color32(115, 56, 51, 255)), 0f, 0f, 0f, 0f);
            Stretch(NewImage(panel, new Color32(43, 39, 51, 255)), 2f, 2f, 2f, 2f);
            return;
        }

        foreach (var part in parts)
        {
            var src = part.Source;
            var img = NewImage(panel, src.color);
            img.sprite = src.sprite;
            img.type = src.type;
            img.fillCenter = src.fillCenter;
            img.pixelsPerUnitMultiplier = src.pixelsPerUnitMultiplier;
            img.material = src.material;
            Stretch(img, part.Left, part.Bottom, part.Right, part.Top);
        }
    }

    /// <summary>放在 tooltip 右侧顶对齐；右边出屏就换到左侧，纵向夹在屏幕内。</summary>
    private static void Place(RectTransform tooltipRect, Transform parent)
    {
        if (_panel == null) return;
        var corners = new Vector3[4];
        tooltipRect.GetWorldCorners(corners);
        Vector3 bl = parent.InverseTransformPoint(corners[0]);
        Vector3 tr = parent.InverseTransformPoint(corners[2]);

        float sx = Mathf.Abs(tooltipRect.localScale.x);
        float sy = Mathf.Abs(tooltipRect.localScale.y);
        float w = _builtSize.x * sx;
        float h = _builtSize.y * sy;
        float gap = Mathf.Round(4f * sx);

        Rect screen = ScreenRect(tooltipRect, parent);
        float x = tr.x + gap;
        if (x + w > screen.xMax && bl.x - gap - w >= screen.xMin) x = bl.x - gap - w;
        float y = tr.y;
        if (y - h < screen.yMin) y = screen.yMin + h;
        if (y > screen.yMax) y = screen.yMax;

        _panel.transform.localPosition = new Vector3(Mathf.Round(x), Mathf.Round(y), 0f);
    }

    private static Rect ScreenRect(RectTransform tooltipRect, Transform parent)
    {
        var canvas = tooltipRect.GetComponentInParent<Canvas>();
        var root = canvas != null ? canvas.rootCanvas.transform as RectTransform : null;
        if (root == null) return new Rect(float.MinValue / 4, float.MinValue / 4, float.MaxValue / 2, float.MaxValue / 2);
        var corners = new Vector3[4];
        root.GetWorldCorners(corners);
        Vector3 a = parent.InverseTransformPoint(corners[0]);
        Vector3 b = parent.InverseTransformPoint(corners[2]);
        return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
    }

    // ---------------- 小工具 ----------------

    private static Sprite? IconOf(GameItem item)
    {
        try
        {
            var bg = (item as GameItemElement)?.background;
            if (bg == null || bg.image == null) return null;
            return bg.image.sprite;
        }
        catch { return null; }
    }

    /// <summary>等比缩放进格子；放大时取整数倍，像素图不糊。</summary>
    private static void Fit(Sprite sprite, float w, float h, out float outW, out float outH)
    {
        outW = outH = 0f;
        float sw = sprite.rect.width, sh = sprite.rect.height;
        if (sw <= 0f || sh <= 0f || w <= 0f || h <= 0f) return;
        float k = Mathf.Min(w / sw, h / sh);
        if (k >= 1f) k = Mathf.Floor(k);
        outW = Mathf.Round(sw * k);
        outH = Mathf.Round(sh * k);
    }

    private static float Measure(string text, float size)
    {
        try
        {
            var t = _template!;
            float k = t.fontSize > 0.01f ? size / t.fontSize : 1f;
            return t.GetPreferredValues(text).x * k;
        }
        catch { return text.Length * size; }
    }

    private static Image NewImage(RectTransform parent, Color color)
    {
        var go = new GameObject("img", typeof(RectTransform), typeof(Image));
        go.layer = parent.gameObject.layer;
        go.transform.SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    private static TextMeshProUGUI NewText(RectTransform parent, string text, float size, Color color,
        TextAlignmentOptions align)
    {
        var go = new GameObject("text", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.layer = parent.gameObject.layer;
        go.transform.SetParent(parent, false);
        var tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.font = _template!.font;
        tmp.fontSharedMaterial = _template.fontSharedMaterial;
        tmp.fontSize = size;
        tmp.color = color;
        tmp.alignment = align;
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Overflow;
        tmp.raycastTarget = false;
        tmp.text = text;
        return tmp;
    }

    /// <summary>按面板左上角为原点、向下为正放置。</summary>
    private static void PlaceRect(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
    }

    private static void Stretch(Image img, float left, float bottom, float right, float top)
    {
        var rt = img.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
    }
}
