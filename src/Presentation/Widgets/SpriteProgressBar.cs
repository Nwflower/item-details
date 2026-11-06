using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;

namespace ItemDetails;

/// <summary>
/// 用 TMP 行内 sprite 画进度条：外框 + 暗色底 + 内缩填充，染色跟随所在文字的颜色。
///
/// 做法：
///   - 运行时建一个只含 4×4 纯白纹理的 sprite 资源，每根条由三个实心矩形叠出来——
///     外框（前景色）、内底（暗色）、填充（前景色），矩形的位置和大小全写在字形度量里，
///     纹理只拉伸纯白，不会出现缩放模糊或接缝。
///   - 三个 sprite 的步进都为 0，后面再跟 length 个透明 `█` 撑出宽度。这样原版
///     RichTextBuilder 按可见字符数算折行时，进度条仍占 length 格，和旧的 `█` 条一致。
///   - 字形索引低 16 位固定为 0xFFFF：TMP 对 sprite 也查字距表，键是两个字形索引拼成的
///     32 位数，这样拼出来的一半永远是 65535，不会撞上字体里真实存在的字距对。
///   - 度量取自当前 tooltip 字体里 `█` 的字形，能拿到屏幕缩放时把边框、高度对齐到整像素；
///     字体或缩放变了就整表重建。
///   - 只用 TMP 公开 API（本模组不申请反射能力）。资源的 faceInfo 留空（pointSize = 0），
///     TMP 走"按主字体上升线缩放 sprite"的分支：缩放 = 上升线 / 字形高 × 字形 scale，
///     于是给每个字形设 scale = 字形高 / 上升线，度量就等于主字体单位。
///
/// 只在物品 tooltip 构建期间使用（<see cref="TooltipPipeline.InTooltip"/>）；
/// 资源建不出来或任何一步出错，调用方回退到 `█` 文字条，本会话不再尝试。
/// </summary>
internal static class SpriteProgressBar
{
    internal const string AssetName = "item_details_bar";

    private const char Block = '█';
    /// <summary>每格的细分档数：10 格的条有 80 档，比原版按整格取整细得多。</summary>
    private const int SubSteps = 8;
    /// <summary>超过这个格数的条不画 sprite（原版最长也就十几格）。</summary>
    private const int MaxLength = 40;
    /// <summary>调用方没给暗色时，内底用这层半透明黑压暗前景色。</summary>
    private const string DefaultDarkOpen = "<color=#000000B8>";

    private static readonly string TagHead = "<sprite=\"" + AssetName + "\" name=\"";
    private const string TagTail = "\" tint=1>";

    private static TMP_SpriteAsset? _asset;
    private static Texture2D? _texture;
    private static bool _failed;

    private static int _geometryKey;
    private static Geometry _g;
    private static uint _nextSerial = 1;
    private static bool _tablesDirty;
    private static readonly HashSet<string> _glyphs = new();

    private static FieldInfo? _overlayTooltipElement;
    private static FieldInfo? _elementTextNode;

    private struct Geometry
    {
        internal float Advance;   // 一格 `█` 的步进
        internal float Top;       // 外框顶边（相对基线）
        internal float Height;
        internal float Border;
        internal float Gap;       // 填充与外框之间的暗色缝
        internal float RightMargin;
        internal float PixelsPerUnit; // 0 表示拿不到屏幕缩放，不做像素对齐
        internal float Ascent;        // 主字体上升线，用来抵消 TMP 对 sprite 的高度归一
    }

    /// <summary>
    /// 返回 sprite 进度条文本；不适用或失败时返回 null，由调用方画 `█` 条。
    /// 文本不带前景色，外层 slice 的颜色就是条的颜色。
    /// </summary>
    internal static string? TryRender(float rawRatio, int length, RenderHandler.ColorPalette dark, TMP_Text? targetText = null)
    {
        if (_failed || length <= 0 || length > MaxLength) return null;
        try
        {
            if (!EnsureAsset() || !EnsureGeometry(targetText)) return null;

            int steps = length * SubSteps;
            int step = Step(rawRatio, steps);

            string outer = EnsureGlyph('o', length, 0, steps);
            string inner = EnsureGlyph('i', length, 0, steps);
            string? fill = step > 0 ? EnsureGlyph('f', length, step, steps) : null;
            if (_tablesDirty) { _asset!.UpdateLookupTables(); _tablesDirty = false; }

            var sb = new StringBuilder(160 + length);
            AppendTag(sb, outer);
            string innerTag = TagHead + inner + TagTail;
            if (dark != RenderHandler.ColorPalette.None)
                sb.Append(RenderHandler.ApplyRichTextColorTag(innerTag, dark));
            else
                sb.Append(DefaultDarkOpen).Append(innerTag).Append("</color>");
            if (fill != null) AppendTag(sb, fill);
            sb.Append("<alpha=#00>").Append(Block, length).Append("<alpha=#FF>");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _failed = true;
            Log.Warn("SpriteBar.Render", ex);
            return null;
        }
    }

    /// <summary>填充档位：接近满时按满画；只要比例大于 0 至少画一档，避免"有一点"显示成空条。</summary>
    internal static int Step(float rawRatio, int steps)
    {
        if (steps <= 0) return 0;
        float ratio = float.IsFinite(rawRatio) ? Math.Clamp(rawRatio, 0f, 1f) : 0f;
        if (ratio >= ProgressFill.FullThreshold) return steps;
        if (ratio <= 0f) return 0;
        return Math.Clamp((int)(ratio * steps), 1, steps - 1);
    }

    private static void AppendTag(StringBuilder sb, string name)
        => sb.Append(TagHead).Append(name).Append(TagTail);

    // ---------------- 资源 ----------------

    private static bool EnsureAsset()
    {
        if (_asset != null && _texture != null) return true;

        var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false)
        {
            name = AssetName,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };
        var white = new Color32[16];
        for (int i = 0; i < white.Length; i++) white[i] = new Color32(255, 255, 255, 255);
        texture.SetPixels32(white);
        texture.Apply(false, true);

        Material? source = null;
        try { source = TMP_Settings.defaultSpriteAsset != null ? TMP_Settings.defaultSpriteAsset.material : null; }
        catch { }
        Material material;
        if (source != null) material = new Material(source);
        else
        {
            var shader = Shader.Find("TextMeshPro/Sprite");
            if (shader == null) return Fail("找不到 TextMeshPro/Sprite 着色器");
            material = new Material(shader);
        }
        material.name = AssetName;
        material.hideFlags = HideFlags.HideAndDontSave;
        material.SetTexture(ShaderUtilities.ID_MainTex, texture);

        var asset = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
        asset.name = AssetName;
        asset.hideFlags = HideFlags.HideAndDontSave;
        asset.spriteSheet = texture;
        asset.material = material;
        asset.spriteInfoList = new List<TMP_Sprite>();
        asset.fallbackSpriteAssets = new List<TMP_SpriteAsset>();
        asset.hashCode = TMP_TextParsingUtilities.GetHashCodeCaseSensitive(AssetName);
        asset.materialHashCode = TMP_TextParsingUtilities.GetHashCodeCaseSensitive(material.name);
        // 新建资源的版本号为空，第一次 UpdateLookupTables 会按旧格式"升级"：写入版本号并按
        // 空的 spriteInfoList 重建字形表（日志里会有一行 Upgrading sprite asset）。之后再加字形就不会被清掉。
        asset.UpdateLookupTables();
        if (string.IsNullOrEmpty(asset.version)) return Fail("sprite 资源版本号未写入");

        MaterialReferenceManager.AddSpriteAsset(asset.hashCode, asset);
        if (!MaterialReferenceManager.TryGetSpriteAsset(asset.hashCode, out var registered)
            || !ReferenceEquals(registered, asset))
            return Fail("同名 sprite 资源已被占用");

        _texture = texture;
        _asset = asset;
        Log.Msg("SpriteProgressBar: sprite asset registered");
        return true;
    }

    private static bool Fail(string reason)
    {
        _failed = true;
        Log.Warn("SpriteBar.Asset", reason + "，进度条回退为文字块");
        return false;
    }

    // ---------------- 度量 ----------------

    /// <summary>按当前 tooltip 字体与屏幕缩放算出条的几何；字体或缩放变化时清空已建的字形。</summary>
    private static bool EnsureGeometry(TMP_Text? targetText = null)
    {
        TMP_Text? text = targetText ?? TooltipText();
        TMP_FontAsset? font = text != null ? text.font : null;
        if (font == null) font = TypeBadge.TooltipFont;
        if (font == null) font = TMP_Settings.defaultFontAsset;
        if (font == null) return false;

        var ch = TMP_FontAssetUtilities.GetCharacterFromFontAsset(Block, font, true,
            FontStyles.Normal, FontWeight.Regular, out _);
        var owner = ch?.textAsset as TMP_FontAsset ?? font;
        if (ch?.glyph == null) return false;
        var face = owner.faceInfo;
        if (face.pointSize <= 0) return false;

        var main = font.faceInfo;
        if (main.pointSize <= 0 || main.ascentLine <= 0f || main.scale <= 0f) return false;

        float ppu = PixelsPerFontUnit(text, main);
        // 条高对齐正文字形：中日韩取汉字「国」，其余取大写 H（字宽缩放 > 1 即中日韩排版）。
        bool cjk = UISettings.current != null && UISettings.current.GetCharWidthScale() > 1f;
        int key = Mix(Mix(Mix(font.GetInstanceID(), owner.GetInstanceID()), Mathf.RoundToInt(ppu * 10000f)), cjk ? 1 : 0);
        if (key == _geometryKey && _g.Advance > 0f) return true;

        // `█` 可能来自回退字体：换算到主字体单位（sprite 按主字体缩放）。
        float toMain = (face.scale / face.pointSize) / (main.scale / main.pointSize);
        var m = ch.glyph.metrics;
        float glyphScale = ch.scale * ch.glyph.scale * toMain;
        float advance = m.horizontalAdvance * glyphScale;
        float blockTop = m.horizontalBearingY * glyphScale;
        float blockHeight = m.height * glyphScale;
        if (advance <= 0f) return false;
        if (blockHeight <= 0f)
        {
            blockTop = main.ascentLine;
            blockHeight = main.ascentLine - main.descentLine;
        }

        // 参照字形读不到时退回 `█` 的 62%，居中放。
        if (!GlyphBox(cjk ? '国' : 'H', font, main, out float refTop, out float refHeight))
        {
            refHeight = blockHeight * 0.62f;
            refTop = blockTop - (blockHeight - refHeight) / 2f;
        }
        float center = refTop - refHeight / 2f;

        var g = new Geometry { Advance = advance, PixelsPerUnit = ppu, Ascent = main.ascentLine };
        if (ppu > 0f)
        {
            // 外框、暗缝各约条高的 1/8、1/7，至少 1 像素；填充至少留 2 像素。
            int hpx = Math.Max(6, Mathf.RoundToInt(refHeight * ppu));
            int bpx = Math.Max(1, Mathf.RoundToInt(hpx / 8f));
            int gpx = Math.Max(1, Mathf.RoundToInt(hpx / 7f));
            while (hpx - 2 * (bpx + gpx) < 2 && gpx > 1) gpx--;
            while (hpx - 2 * (bpx + gpx) < 2 && bpx > 1) bpx--;
            g.Height = hpx / ppu;
            g.Border = bpx / ppu;
            g.Gap = gpx / ppu;
        }
        else
        {
            g.Height = refHeight;
            g.Border = refHeight / 8f;
            g.Gap = refHeight / 7f;
        }
        g.Top = center + g.Height / 2f;
        g.RightMargin = advance * 0.3f;

        ClearGlyphs();
        _g = g;
        _geometryKey = key;
        return true;
    }

    /// <summary>字符在主字体单位下的顶边（相对基线）与高度；字形来自回退字体时按其缩放换算。</summary>
    private static bool GlyphBox(char c, TMP_FontAsset font, FaceInfo main, out float top, out float height)
    {
        top = height = 0f;
        var ch = TMP_FontAssetUtilities.GetCharacterFromFontAsset(c, font, true,
            FontStyles.Normal, FontWeight.Regular, out _);
        if (ch?.glyph == null) return false;
        var face = (ch.textAsset as TMP_FontAsset ?? font).faceInfo;
        if (face.pointSize <= 0) return false;
        float k = ch.scale * ch.glyph.scale * (face.scale / face.pointSize) / (main.scale / main.pointSize);
        var m = ch.glyph.metrics;
        top = m.horizontalBearingY * k;
        height = m.height * k;
        return height > 0f;
    }

    /// <summary>屏幕像素 / 字体单位。拿不到原版 tooltip 文本或画布时返回 0。</summary>
    private static float PixelsPerFontUnit(TMP_Text? text, FaceInfo face)
    {
        try
        {
            if (text == null || text.canvas == null) return 0f;
            var root = text.canvas.rootCanvas;
            float canvasScale = Mathf.Abs(root.transform.lossyScale.y);
            if (canvasScale < 1e-6f) return 0f;
            float local = Mathf.Abs(text.rectTransform.lossyScale.y) / canvasScale;
            float px = text.fontSize * local * root.scaleFactor / face.pointSize * face.scale;
            return px > 0f && float.IsFinite(px) ? px : 0f;
        }
        catch { return 0f; }
    }

    /// <summary>原版 tooltip 正文的 TMP：OverlayHandler.current.tooltipElement.textNode。</summary>
    internal static TMP_Text? TooltipText()
    {
        try
        {
            var handler = OverlayHandler.current;
            if (handler == null) return null;
            _overlayTooltipElement ??= AccessTools.Field(typeof(OverlayHandler), "tooltipElement");
            _elementTextNode ??= AccessTools.Field(typeof(RichTextElement), "textNode");
            if (_overlayTooltipElement == null || _elementTextNode == null) return null;
            var element = _overlayTooltipElement.GetValue(handler) as RichTextElement;
            if (element == null) return null;
            var node = _elementTextNode.GetValue(element) as Component;
            return node != null ? node.GetComponentInChildren<TMP_Text>(true) : null;
        }
        catch { return null; }
    }

    private static int Mix(int a, int b) => unchecked(a * 397 ^ b);

    /// <summary>几何变了：已建字形全部作废，按需重建。</summary>
    private static void ClearGlyphs()
    {
        var asset = _asset!;
        asset.spriteCharacterTable.Clear();
        asset.spriteGlyphTable.Clear();
        _glyphs.Clear();
        _nextSerial = 1;
        _tablesDirty = false;
        asset.UpdateLookupTables();
    }

    // ---------------- 字形 ----------------

    /// <summary>kind：o 外框、i 内底、f 填充（step / steps）。名字带格数，长度不同的条各用一套。</summary>
    private static string EnsureGlyph(char kind, int length, int step, int steps)
    {
        string name = kind == 'f' ? $"f{length}_{step}" : $"{kind}{length}";
        if (_glyphs.Contains(name)) return name;

        var g = _g;
        float width = length * g.Advance - g.RightMargin;
        float x, y, w, h;
        switch (kind)
        {
            case 'o':
                x = 0f; y = g.Top; w = width; h = g.Height;
                break;
            case 'i':
                x = g.Border; y = g.Top - g.Border; w = width - 2f * g.Border; h = g.Height - 2f * g.Border;
                break;
            default:
                float inset = g.Border + g.Gap;
                float full = width - 2f * inset;
                w = full * step / steps;
                if (g.PixelsPerUnit > 0f)
                    w = Math.Max(1f, Mathf.Round(w * g.PixelsPerUnit)) / g.PixelsPerUnit;
                x = inset; y = g.Top - inset; h = g.Height - 2f * inset;
                break;
        }
        if (w <= 0f || h <= 0f) throw new InvalidOperationException($"进度条字形尺寸无效：{name} {w}x{h}");

        var asset = _asset!;
        uint index = (_nextSerial++ << 16) | 0xFFFFu;
        // TMP 把 sprite 缩放成 上升线 / 字形高 × scale；scale 取 字形高 / 上升线，两者抵消，度量即主字体单位。
        float scale = h / g.Ascent;
        var glyph = new TMP_SpriteGlyph(index, new GlyphMetrics(w, h, x, y, 0f), new GlyphRect(1, 1, 2, 2), scale, 0);
        var character = new TMP_SpriteCharacter(0xFFFE, asset, glyph) { name = name };
        asset.spriteGlyphTable.Add(glyph);
        asset.spriteCharacterTable.Add(character);
        _tablesDirty = true;
        _glyphs.Add(name);
        return name;
    }
}
