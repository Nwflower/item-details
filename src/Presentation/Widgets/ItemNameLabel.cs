using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace ItemDetails;

/// <summary>
/// 物品图标上常驻显示名字（不必悬停）。
///
/// 显示范围由设置决定：无（默认）/ 容器名（容器与机器）/ 仅改名物品（用标签打印机改过名的）。
///
/// 实现要点：
///   - 挂 <c>GameItemElement.ValidateSelect(bool)</c> 的后缀——游戏自己在里面刷新堆叠数字、
///     图钉与外观，物品身份变化/换格子都会走到这里；
///   - 文字用 <c>TreeNodeRender.InstantiateText()</c> 生成两层（本体 + 深色投影），
///     与游戏的堆叠数字同款；父节点挂在物品根节点 <c>handler</c>，避开内部裁剪且不随物品旋转；
///   - 定位用**世界坐标外接框**（GetWorldCorners），并把节点 localScale 取父级缩放的倒数，
///     于是 sizeDelta 与字号都以屏幕像素为单位（实测父级 lossyScale=2，直接写世界宽度会宽一倍）；
///   - ValidateSelect 早于布局生效，所以另有 0.2s 节流补位（<see cref="ItemNameTicker"/>）；
///   - 每个元素只建一次、挂在元素节点下随其销毁，用弱引用表记录，不持有已销毁元素。
/// </summary>
internal static class ItemNameLabel
{
    private const string HarmonyId = "item_details.item_name_label";
    private const float LabelBottomInset = 3f;   // 屏幕像素

    private static Harmony? _harmony;
    private static bool _patched;
#if ITEMDETAILS_DIAG
    private static bool _geometryLogged;
    private static int _placementLogged;
    private static int _rotatedLogged;
#endif

    private static readonly ConditionalWeakTable<GameItemElement, Label> Labels = new();
    private static readonly System.Collections.Generic.List<WeakReference<GameItemElement>> Tracked = new();
    private static readonly Vector3[] WorldCorners = new Vector3[4];
    private static GameObject? _tickerGo;

    private sealed class Label
    {
        internal TreeNodeRender? Main;
        internal TreeNodeRender? Shadow;
        internal TreeNodeRender? Bg;
        internal string Text = "";
        internal bool Visible;
        internal bool HasPlacement;
        internal float Width;
        internal float Height;
        internal float CenterX;
        internal float Y;
        internal float Z;
        internal float ScaleX;
        internal float ScaleY;
        internal float FontSize;
        internal bool Background;
        internal float BackgroundAlpha;
        internal bool HasMeasurement;
        internal string MeasuredText = "";
        internal float MeasuredFontSize;
        internal float MeasuredIconWidth;
        internal float MeasuredWidth;
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

        // 节点挂在物品元素下，不会随 ticker 自动销毁；热停用时必须逐个清理。
        for (int i = Tracked.Count - 1; i >= 0; i--)
        {
            try
            {
                if (Tracked[i].TryGetTarget(out var element) && element != null
                    && Labels.TryGetValue(element, out var label))
                {
                    DestroyLabel(label);
                    Labels.Remove(element);
                }
            }
            catch { }
        }
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

            try { if (element.rectTransform != null && !element.rectTransform.gameObject.activeInHierarchy) continue; }
            catch { }
            if (!Labels.TryGetValue(element, out var label) || !label.Visible) continue;
            if (label.Main == null || label.Shadow == null || label.Bg == null)
            {
                // 界面重建可能保留元素、只销毁其子节点；重新创建而不是永久丢失名称。
                Refresh(element);
                continue;
            }
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

        string mode = ModSettings.ItemNameMode;
        if (mode.StartsWith("无", StringComparison.Ordinal) || mode.StartsWith("Off", StringComparison.OrdinalIgnoreCase))
        {
            Hide(element);
            return;
        }

        bool renamedOnly = mode.StartsWith("仅", StringComparison.Ordinal)
                        || mode.StartsWith("Renamed", StringComparison.OrdinalIgnoreCase);

        string name = "";
        bool want = false;

        if (renamedOnly)
        {
            // 只认「用标签打印机改过名」的物品，显示改后的名字
            try
            {
                if (GeneralHelper.HasCustomName(element))
                {
                    want = true;
                    name = GeneralHelper.GetCustomName(element) ?? "";
                }
            }
            catch (Exception ex) { Log.Warn("NameLabel.CustomName", ex); }
        }
        else
        {
            want = ShouldShowContainer(element);
            if (want && IsHiddenItem(element)) want = false;   // 垃圾桶 / 档案按稳定 ID 排除
            if (want)
            {
                // preferCustomName：改过名的容器优先显示改后的名字。
                // 用无参版本会走回 name 字段，标签打印机改的名字被忽略（「我的保险箱」又变回「保险箱」）。
                try { name = element.GetDisplayName(preferCustomName: true) ?? ""; }
                catch (Exception ex) { Log.Warn("NameLabel.DisplayName", ex); }
            }
        }

        if (!want || name.Length == 0)
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
            try
            {
                label.Main.text.ForceMeshUpdate();
                label.Shadow.text.ForceMeshUpdate();
            }
            catch { }
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
        if (!Labels.TryGetValue(element, out var label) || !label.Visible) return;
        label.Visible = false;
        label.HasPlacement = false;
        if (label.Main != null) label.Main.text.enabled = false;
        if (label.Shadow != null) label.Shadow.text.enabled = false;
        if (label.Bg != null && label.Bg.image != null) label.Bg.image.enabled = false;
    }

    /// <summary>这几类不带标签：垃圾桶、治安部档案；ID 不随语言或自定义显示名变化。</summary>
    private static readonly string[] HiddenItemIds = { "trashcan", "intel_sec" };

    private static bool IsHiddenItem(GameItemElement item)
    {
        try
        {
            string id = item.identifier;
            foreach (string hidden in HiddenItemIds)
                if (string.Equals(id, hidden, StringComparison.Ordinal)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>容器（有内嵌库存）或机器；判定失败一律不显示，不猜。</summary>
    private static bool ShouldShowContainer(GameItemElement item)
    {
        try
        {
            if (item.IsGameItemType("MACHINE")) return true;
        }
        catch (Exception ex) { Log.Warn("NameLabel.IsMachine", ex); }

        try
        {
            // contentWindow.child 就是容器内嵌库存。不能在 ValidateSelect 后缀里做
            // GraphUtils.FindChildrenType：原版已经搜过一次，再搜会让每个普通物品
            // （包括储物区里的每个酒瓶）都重复分配 Queue/HashSet/List。
            var window = item.contentWindow;
            if (window == null) return false;
            if (window.child is GameInventory) return true;

            // 储藏区扩建后根换成 MachineryPanelRow（每个标签页一个网格），child 拿到的是那一行而不是库存。
            // 只在这种结构下收集，且要求全是网格——槽位型的工具箱等仍不算容器。
            return window.childElement is MachineryPanelRow && StorageInfo.GridStorage(item) != null;
        }
        catch (Exception ex)
        {
            Log.Warn("NameLabel.IsContainer", ex);
            return false;
        }
    }

    private static Label? GetOrCreate(GameItemElement element)
    {
        if (Labels.TryGetValue(element, out var existing))
        {
            if (existing.Main != null && existing.Shadow != null && existing.Bg != null)
                return existing;
            RemoveEntry(element, existing);
        }

        try
        {
            // 现在用世界坐标定位，不依赖父节点矩形；挂在物品根节点可避免 middle 内部裁剪/遮挡，
            // 同时保持文字不随 rotator 旋转。handler 的 0×0 rect 不影响这种定位方式。
            var parent = element.handler;
            if (parent == null) return null;

            // 半透明底（可选）：无 sprite 的 Image 就是纯色方块，先建它保证画在文字下面
            var bg = TreeNodeRender.Instantiate().SetParent(parent);
            var shadow = TreeNodeRender.InstantiateText().SetParent(parent);
            var main = TreeNodeRender.InstantiateText().SetParent(parent);

            try
            {
                var img = bg.image;
                if (img != null) { img.sprite = null; img.raycastTarget = false; img.enabled = false; }
                bg.rectTransform.localScale = Vector3.one;
            }
            catch { }

            Configure(shadow, new Color(0f, 0f, 0f, 0.85f));
            Configure(main, Color.white);
            shadow.text.text = "";
            main.text.text = "";
            shadow.text.enabled = false;
            main.text.enabled = false;

#if ITEMDETAILS_DIAG
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
#endif

            var label = new Label { Main = main, Shadow = shadow, Bg = bg };
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

    private static void RemoveEntry(GameItemElement element, Label label)
    {
        DestroyLabel(label);
        Labels.Remove(element);
        for (int i = Tracked.Count - 1; i >= 0; i--)
        {
            try
            {
                if (Tracked[i].TryGetTarget(out var tracked) && ReferenceEquals(tracked, element))
                    Tracked.RemoveAt(i);
            }
            catch { }
        }
    }

    private static void DestroyLabel(Label label)
    {
        try { if (label.Main != null) UnityEngine.Object.Destroy(label.Main.gameObject); } catch { }
        try { if (label.Shadow != null) UnityEngine.Object.Destroy(label.Shadow.gameObject); } catch { }
        try { if (label.Bg != null) UnityEngine.Object.Destroy(label.Bg.gameObject); } catch { }
        label.Main = null;
        label.Shadow = null;
        label.Bg = null;
        label.Visible = false;
    }

    /// <summary>
    /// 显式挑一个含当前语言字形的字体。
    ///
    /// TreeNodeRender.InstantiateText() 内部只在 FontLoader.Instance 与 SelectedLocale 都就绪时
    /// 才套用本地化字体；启动早期创建的节点会停在预制体的拉丁字体上，
    /// 中文被 TMP 替换成空格 —— 表现出来就是"文字消失"（日志里会刷
    /// "The character with Unicode value \u81EA was not found in the [NotoSans-Medium SDF]"）。
    /// 因此这里按「tooltip 正文字体 → FontLoader 的 Noto_Sans → 任意已有 TMP 字体」三级兜底。
    /// </summary>
    private static TMP_FontAsset? ResolveFont()
    {
        try
        {
            var fromTooltip = TypeBadge.TooltipFont;
            if (fromTooltip != null) return fromTooltip;
        }
        catch { }

        try
        {
            // 扫描已加载的字体资源，优先挑名字里带 CJK 线索的（SC / CN / Chinese / Han / Noto）
            var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            if (fonts != null)
            {
                for (int i = 0; i < fonts.Length; i++)
                {
                    var f = fonts[i];
                    if (f == null) continue;
                    string n = "";
                    try { n = f.name ?? ""; } catch { }
                    if (n.IndexOf("SC", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("CN", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("Chinese", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("Han", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("Noto", StringComparison.OrdinalIgnoreCase) >= 0)
                        return f;
                }

                for (int i = 0; i < fonts.Length; i++)
                    if (fonts[i] != null) return fonts[i];
            }
        }
        catch { }

        return null;
    }

    private static void Configure(TreeNodeRender node, Color color)
    {
        var rt = node.rectTransform;
        rt.localEulerAngles = Vector3.zero;

        var text = node.text;
        text.color = color;
        text.fontStyle = FontStyles.Normal;
        text.alignment = TextAlignmentOptions.Center;
        text.enableWordWrapping = false;
        // 与参考模组 StorageNameDisplay 一致：固定字号 + Truncate。
        // Ellipsis 在本游戏的 NotoSansSC / 当前行高下会让 TMP 输出 0 个字符。
        text.overflowMode = TextOverflowModes.Truncate;
        text.enableAutoSizing = false;
        text.fontSize = 12f;
        text.raycastTarget = false;
    }

    /// <summary>上/中/下三档位置：按设置把标签放在外接框的对应一侧（屏幕像素）。</summary>
    private static float RowY(string mode, float minY, float maxY, float height)
    {
        if (mode.StartsWith("顶", StringComparison.Ordinal) || mode.StartsWith("Top", StringComparison.OrdinalIgnoreCase))
            return maxY - height - LabelBottomInset;
        if (mode.StartsWith("中", StringComparison.Ordinal) || mode.StartsWith("Middle", StringComparison.OrdinalIgnoreCase))
            return (minY + maxY) * 0.5f - height * 0.5f;
        return minY + LabelBottomInset;   // 默认底部
    }

    private static void Place(GameItemElement element, Label label)
    {
        try
        {
            // 用 sprite 节点（background）的外接框，而不是 middle 的占格矩形：
            // 像素图的原生尺寸通常大于占格（例如 2×2 格 32px，图 48px 且带偏移），
            // 按占格放会让名字落在图标外面或压到相邻行。
            var sprite = element.background != null ? element.background.rectTransform
                       : element.middle != null ? element.middle.rectTransform
                       : null;
            if (sprite == null) return;

            // Unity 主线程内复用坐标缓冲，避免每个标签每 0.2 秒都分配一个数组。
            var corners = WorldCorners;
            sprite.GetWorldCorners(corners);

            float minX = corners[0].x, maxX = corners[0].x, minY = corners[0].y, maxY = corners[0].y;
            for (int i = 1; i < 4; i++)
            {
                if (corners[i].x < minX) minX = corners[i].x;
                if (corners[i].x > maxX) maxX = corners[i].x;
                if (corners[i].y < minY) minY = corners[i].y;
                if (corners[i].y > maxY) maxY = corners[i].y;
            }

            // 标签的直接父级是 handler；缩放必须按这个父级抵消，不能混入 rotator/background 自身变换。
            var scale = element.handler.rectTransform.lossyScale;
            float sx = Mathf.Abs(scale.x) < 0.001f ? 1f : Mathf.Abs(scale.x);
            float sy = Mathf.Abs(scale.y) < 0.001f ? 1f : Mathf.Abs(scale.y);

            // 图标名使用 tooltip 正文的 50% + 常规字重，降低常驻标签的视觉权重；
            // 从首次显示就按当前 UI 缩放固定，不再等 tooltip 模板出现后跳变。
            float iconWidth = Mathf.Max(8f, maxX - minX);
            float size = Mathf.Clamp(14f * sy * 0.50f, 6f, 40f);
            float height = size * 1.5f;   // 与参考模组一致，给 NotoSansSC 足够的单行布局高度
            float z = sprite.position.z;

            // 位置按**旋转后的四边形**算：取四条边里朝下那条（边中点 Y 最小）的中点作为基准，
            // 再按上/中/下档位偏移。旋转 90/180/270 或任意角度时都跟着图标走，
            // 不会停在旋转前的格子上。
            int bottomEdge = 0;
            float bottomMidY = float.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                float midY = (corners[i].y + corners[(i + 1) % 4].y) * 0.5f;
                if (midY < bottomMidY) { bottomMidY = midY; bottomEdge = i; }
            }

            Vector3 e0 = corners[bottomEdge];
            Vector3 e1 = corners[(bottomEdge + 1) % 4];
            float centerX = (e0.x + e1.x) * 0.5f;

            float y;
            var posMode = ModSettings.ItemNamePosition;
            if (posMode.StartsWith("顶", StringComparison.Ordinal) || posMode.StartsWith("Top", StringComparison.OrdinalIgnoreCase))
                y = maxY - height - LabelBottomInset;
            else if (posMode.StartsWith("中", StringComparison.Ordinal) || posMode.StartsWith("Middle", StringComparison.OrdinalIgnoreCase))
                y = (minY + maxY) * 0.5f - height * 0.5f;
            else
                y = bottomMidY + LabelBottomInset;

            // 必须先应用最终字号再测宽；否则水罐这类短名字会按旧的 12 号字测宽，
            // 随后以更大的字号写进同一个窄框，被 Truncate 截成一个字。
            if (label.Main != null && label.Main.text.fontSize != size) label.Main.text.fontSize = size;
            if (label.Shadow != null && label.Shadow.text.fontSize != size) label.Shadow.text.fontSize = size;

            // 文字宽度按实际文本量：底板跟着文字走，不跟着容器宽度走。
            // 0.2 秒补位时复用测量结果，只有文字、字号或图标宽度变化才问 TMP。
            float textWidth = iconWidth;
            if (label.Main != null)
            {
                if (!label.HasMeasurement || label.MeasuredText != label.Text
                    || label.MeasuredFontSize != size || label.MeasuredIconWidth != iconWidth)
                {
                    label.HasMeasurement = true;
                    label.MeasuredText = label.Text;
                    label.MeasuredFontSize = size;
                    label.MeasuredIconWidth = iconWidth;
                    label.MeasuredWidth = iconWidth;
                    try
                    {
                        var pref = label.Main.text.GetPreferredValues(label.Text);
                        // 宽度上限 = 图标宽度：超出就必须截断（试剂盒这类小格子尤其明显）
                        if (pref.x > 1f)
                            label.MeasuredWidth = Mathf.Clamp(pref.x + 2f, 8f, iconWidth);
                    }
                    catch { }
                }
                textWidth = label.MeasuredWidth;
            }

            bool bgOn = ModSettings.ItemNameBackground;
            float bgAlpha = Mathf.Clamp01(ModSettings.ItemNameBackgroundAlpha);
            if (label.HasPlacement
                && label.Width == textWidth && label.Height == height
                && label.CenterX == centerX && label.Y == y && label.Z == z
                && label.ScaleX == sx && label.ScaleY == sy && label.FontSize == size
                && label.Background == bgOn && label.BackgroundAlpha == bgAlpha)
                return;

            label.HasPlacement = true;
            label.Width = textWidth;
            label.Height = height;
            label.CenterX = centerX;
            label.Y = y;
            label.Z = z;
            label.ScaleX = sx;
            label.ScaleY = sy;
            label.FontSize = size;
            label.Background = bgOn;
            label.BackgroundAlpha = bgAlpha;

            SetRect(label.Main, textWidth, height, centerX, y, z, sx, sy);
            SetRect(label.Shadow, textWidth, height, centerX, y - 1f, z, sx, sy);

            // 固定字号 + 刚启用节点时 TMP 不会自己重建网格，表现为"文字消失"。
            // 这里只在参数真的变化后强制刷一次（0.2s 的补位会被上面的缓存挡掉）。
            try
            {
                label.Main?.text?.ForceMeshUpdate();
                label.Shadow?.text?.ForceMeshUpdate();
            }
            catch { }

            if (label.Bg != null && label.Bg.image != null)
            {
                label.Bg.image.enabled = bgOn;
                if (bgOn)
                {
                    label.Bg.image.color = new Color(0f, 0f, 0f, bgAlpha);
                    SetRect(label.Bg, Mathf.Min(iconWidth, textWidth + 4f), height + 2f, centerX, y - 1f, z, sx, sy);
                }
            }

#if ITEMDETAILS_DIAG
            // 旋转物品专项诊断：四角与 rotator 角度一起打，确认角点是否已含旋转
            if (_rotatedLogged < 6)
            {
                try
                {
                    float rotZ = element.rotator != null ? element.rotator.rectTransform.localEulerAngles.z : 0f;
                    if (Mathf.Abs(Mathf.DeltaAngle(0f, rotZ)) > 0.5f)
                    {
                        _rotatedLogged++;
                        Log.Msg($"NameLabel rotated '{label.Text}': rotatorZ={rotZ:0.#} " +
                                $"c0=({corners[0].x:0},{corners[0].y:0}) c1=({corners[1].x:0},{corners[1].y:0}) " +
                                $"c2=({corners[2].x:0},{corners[2].y:0}) c3=({corners[3].x:0},{corners[3].y:0}) " +
                                $"-> bbox=({minX:0},{minY:0})-({maxX:0},{maxY:0}) posX={centerX:0} y={y:0}");
                    }
                }
                catch { }
            }

            if (_placementLogged < 3)
            {
                _placementLogged++;
                Log.Msg($"NameLabel place '{label.Text}': icon w={iconWidth:0} text w={textWidth:0} y={y:0} " +
                        $"lossyScale=({scale.x:0.##},{scale.y:0.##}) size={size:0.#}");
                LogRenderState(label);
            }
#endif
        }
        catch (Exception ex)
        {
            Log.Warn("NameLabel.Place", ex);
        }
    }

#if ITEMDETAILS_DIAG
    private static void LogRenderState(Label label)
    {
        try
        {
            var node = label.Main;
            var text = node?.text;
            if (node == null || text == null) { Log.Msg("NameLabel render: main text missing"); return; }
            var rt = node.rectTransform;
            var canvasRenderer = text.canvasRenderer;
            string parent = rt.parent != null ? rt.parent.name : "<none>";
            string font = text.font != null ? text.font.name : "<none>";
            Log.Msg($"NameLabel render '{label.Text}': active={node.gameObject.activeInHierarchy} enabled={text.isActiveAndEnabled} " +
                    $"chars={text.textInfo.characterCount} lines={text.textInfo.lineCount} vertices={text.mesh.vertexCount} " +
                    $"cull={canvasRenderer.cull} alpha={text.color.a:0.##} font={font} fontSize={text.fontSize:0.#} " +
                    $"auto={text.enableAutoSizing} overflow={text.overflowMode} rect={rt.rect} pos={rt.position} " +
                    $"scale={rt.lossyScale} parent={parent} sibling={rt.GetSiblingIndex()}");
            if (label.Bg?.image != null)
                Log.Msg($"NameLabel background '{label.Text}': active={label.Bg.gameObject.activeInHierarchy} " +
                        $"enabled={label.Bg.image.isActiveAndEnabled} cull={label.Bg.image.canvasRenderer.cull} " +
                        $"color={label.Bg.image.color} rect={label.Bg.rectTransform.rect} pos={label.Bg.rectTransform.position}");
        }
        catch (Exception ex) { Log.Warn("NameLabel.RenderState", ex); }
    }
#endif

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
