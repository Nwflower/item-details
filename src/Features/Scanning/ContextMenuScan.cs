using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ItemDetails;

/// <summary>
/// 右键菜单添加扫描：拥有对应设备时，物品右键菜单多一行「扫描」，点它直接打开该设备的扫描结果面板。
///
/// 对应关系（照 0.46D 原版各扫描器自己的目标判定）：
///   · 上锁补给箱（SUPPLY_CRATE + LOCK_STATUS）   → 便携式X射线扫描仪（xray_scanner）
///   · 化学品（CHEM_ITEM_TAG / CHEMICAL_PRODUCT_TAG）→ 化学扫描仪（chem_scanner）
///   · 普通液体容器（LIQUID_CONTAINER_TAG）        → 水质扫描仪优先，没有则化学扫描仪
///   · 种子（SEED_ITEM_TAG）                       → 植物分析仪（plant_analyser）
///
/// 为什么不用双击：原版和各模组已经把双击用满了——开容器、激活、拿起（瓶子、移液枪靠双击装备）、
/// 倒水。抢双击必然顶掉其中某一个，让开又会让液体扫描几乎用不上。右键菜单是原版给“对物品做
/// 某个动作”留的入口，加一行不碰任何既有手势。
///
/// 菜单做法与 SmartInventoryArrange 扩展排序菜单相同：把原版 1×7 按钮网格换成 1×8，原版七个按钮
/// 原样搬进去，「扫描」插在「固定」之前。不适用时把这一行 SetActive(false)——
/// <c>GridPixelElement</c> 对隐藏行既不算高度也不加行距，菜单与原版逐像素一致。
/// 原版菜单结构认不出来（别的模组先改过）就整个放弃，不硬塞。
///
/// 不接管的三类：基因扫描仪是常开开关、没有结果面板；拾荒扫描仪与改造体扫描仪不针对
/// 物品 / 只针对顾客；成分光谱仪（spectral_analyser）在 0.46D 没有任何投放途径。
///
/// 走的都是原版公开入口，效果与玩家把设备拖到物品上一致（包括原版自己写入的扫描结果、
/// 补给箱的 LOOT_CRATE_SCANNED_TAG、化学品/水的对质按钮状态）。
/// </summary>
internal static class ContextMenuScan
{
    private const string HarmonyId = "item_details.context_scan";
    private const string MenuNodeTag = "ContextMenuNode";

    /// <summary>「扫描」所在行：原版第 7 行「固定」之前。</summary>
    private const int ScanRow = 6;

    private static Harmony? _harmony;

    // 原版私有字段：直接写 typeof(...)，官方扫描器才认得出反射目标。
    private static readonly FieldInfo? WindowField = AccessTools.Field(typeof(ItemContextHandler), "contextWindow");
    private static readonly FieldInfo? ItemField = AccessTools.Field(typeof(ItemContextHandler), "currentItem");
    private static readonly FieldInfo? TextColorField = AccessTools.Field(typeof(ItemContextHandler), "textColor");
    private static readonly FieldInfo? SelectColorField = AccessTools.Field(typeof(ItemContextHandler), "textSelectColor");
    private static readonly FieldInfo? XSpacingField = AccessTools.Field(typeof(GridPixelElement), "_xSpacing");
    private static readonly FieldInfo? YSpacingField = AccessTools.Field(typeof(GridPixelElement), "_ySpacing");

    /// <summary>原版按钮，按菜单从上到下的顺序。</summary>
    private static readonly FieldInfo?[] ButtonFields =
    {
        AccessTools.Field(typeof(ItemContextHandler), "buttonOpen"),
        AccessTools.Field(typeof(ItemContextHandler), "buttonUse"),
        AccessTools.Field(typeof(ItemContextHandler), "buttonEquip"),
        AccessTools.Field(typeof(ItemContextHandler), "buttonActivate"),
        AccessTools.Field(typeof(ItemContextHandler), "buttonToggle"),
        AccessTools.Field(typeof(ItemContextHandler), "buttonUnload"),
        AccessTools.Field(typeof(ItemContextHandler), "buttonPin"),
    };

    // 注入状态：只对一个 contextWindow 生效；换场景后处理器重建，按新窗口重来。
    private static PixelWindow? _window;
    private static GridPixelElement? _nativeGrid;
    private static GridPixelElement? _grid;
    private static RichTextElement[]? _nativeButtons;
    private static RichTextElement? _button;
    private static GameItem? _target;   // 本次菜单要扫描的物品；为 null 表示「扫描」行隐藏

    internal static void Apply()
    {
        if (_harmony != null) return;
        try
        {
            _harmony = new Harmony(HarmonyId);
            Patch(AccessTools.Method(typeof(ItemContextHandler), "UpdateConditions"), postfix: nameof(AfterUpdateConditions));
            Patch(AccessTools.Method(typeof(ItemContextHandler), "UpdateSelectButtonColor"), postfix: nameof(AfterUpdateSelectButtonColor));
            Patch(AccessTools.Method(typeof(ItemContextHandler), "EqualizeButtonWidths"), postfix: nameof(AfterEqualizeButtonWidths));
            Patch(AccessTools.Method(typeof(ItemContextHandler), "TriggerButton"), prefix: nameof(BeforeTriggerButton));
            Log.Msg("ContextMenuScan: patched ItemContextHandler");
        }
        catch (Exception ex)
        {
            Log.Error("ContextScan.Apply", ex);
            Remove();
        }
    }

    private static void Patch(MethodInfo? target, string? prefix = null, string? postfix = null)
    {
        if (target == null) throw new MissingMethodException("ItemContextHandler", prefix ?? postfix ?? "?");
        _harmony!.Patch(target,
            prefix: prefix == null ? null : new HarmonyMethod(typeof(ContextMenuScan), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(ContextMenuScan), postfix));
    }

    internal static void Remove()
    {
        try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        _harmony = null;
        try { RestoreNativeMenu(); } catch (Exception ex) { Log.Warn("ContextScan.Restore", ex); }
        _window = null;
        _nativeGrid = null;
        _grid = null;
        _nativeButtons = null;
        _button = null;
        _target = null;
    }

    // ---------------- 菜单 ----------------

    /// <summary>每次打开物品菜单时：确保菜单已扩成 1×8，再决定「扫描」行显示与否。</summary>
    private static void AfterUpdateConditions(ItemContextHandler __instance)
    {
        try
        {
            _target = null;
            var item = ItemField?.GetValue(__instance) as GameItem;
            bool show = ModSettings.ContextMenuScan && Classify(item) != Kind.None && EnsureMenu(__instance);
            if (show) _target = item;
            if (_button != null) _button.handler.gameObject.SetActive(show);
        }
        catch (Exception ex)
        {
            Log.Warn("ContextScan.Conditions", ex);
            _target = null;
            try { _button?.handler.gameObject.SetActive(false); } catch { }
        }
    }

    /// <summary>原版每帧按悬停状态重写按钮文字与颜色；「扫描」行照同样的配色。</summary>
    private static void AfterUpdateSelectButtonColor(ItemContextHandler __instance, RichTextElement selectedButton)
    {
        try
        {
            if (_target == null || _button == null) return;
            var field = ReferenceEquals(selectedButton, _button) ? SelectColorField : TextColorField;
            var color = field?.GetValue(__instance) is RenderHandler.ColorPalette c ? c : RenderHandler.ColorPalette.LightGray;
            _button.SetText(Loc.T("menu.scan", "Scan"), 10000, color);
        }
        catch (Exception ex)
        {
            Log.Warn("ContextScan.Color", ex);
        }
    }

    /// <summary>原版已把七个按钮设为同宽；「扫描」更宽时整列一起放宽。</summary>
    private static void AfterEqualizeButtonWidths()
    {
        try
        {
            if (_target == null || _button == null || _nativeButtons == null) return;
            _button.SetFixedSize(-1, -1);
            int width = Math.Max(_button.widthPixels, _nativeButtons[0].widthPixels);
            if (width > _nativeButtons[0].widthPixels)
            {
                foreach (var button in _nativeButtons) button.SetFixedSize(width, -1);
            }
            _button.SetFixedSize(width, -1);
        }
        catch (Exception ex)
        {
            Log.Warn("ContextScan.Width", ex);
        }
    }

    /// <summary>点到「扫描」：先按原版方式关菜单，再打开扫描面板。</summary>
    private static bool BeforeTriggerButton(ItemContextHandler __instance, RichTextElement button)
    {
        try
        {
            var item = _target;
            if (item == null || _button == null || !ReferenceEquals(button, _button)) return true;
            if (!_button.handler.gameObject.activeInHierarchy) return true;
            _target = null;
            __instance.Stop();
            TryScan(item, "context");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("ContextScan.Trigger", ex);
            return true;
        }
    }

    /// <summary>
    /// 把当前处理器的物品菜单扩成 1×8。已经扩过返回 true；原版结构认不出或失败返回 false，
    /// 同一个窗口不再重试。
    /// </summary>
    private static bool EnsureMenu(ItemContextHandler handler)
    {
        var window = WindowField?.GetValue(handler) as PixelWindow;
        if (window == null) return false;
        if (ReferenceEquals(window, _window)) return _grid != null && ReferenceEquals(window.childElement, _grid);

        _window = window;
        _nativeGrid = null;
        _grid = null;
        _nativeButtons = null;
        _button = null;
        try
        {
            Inject(handler, window);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("ContextScan.Inject", ex);
            _grid = null;
            _button = null;
            return false;
        }
    }

    private static void Inject(ItemContextHandler handler, PixelWindow window)
    {
        var native = window.childElement as GridPixelElement
            ?? throw new InvalidOperationException("context window child is not a grid");
        var buttons = new RichTextElement[ButtonFields.Length];
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i] = ButtonFields[i]?.GetValue(handler) as RichTextElement
                ?? throw new InvalidOperationException("missing native context button " + i);
        }
        // 只认原版 1×7 布局；别的模组改过就放弃，不叠加改动。
        if (native.gridWidth != 1 || native.gridHeight != buttons.Length)
            throw new InvalidOperationException($"unexpected context grid {native.gridWidth}x{native.gridHeight}");
        for (int i = 0; i < buttons.Length; i++)
        {
            if (!ReferenceEquals(native.GetElement(0, i), buttons[i]))
                throw new InvalidOperationException("context grid row " + i + " is not the native button");
        }

        var scan = new RichTextElement();
        scan.handler.gameObject.name = "nodeButtonItemDetailsScan";
        var grid = new GridPixelElement(1, buttons.Length + 1)
            .SetSpacing(Spacing(XSpacingField, native), Spacing(YSpacingField, native));
        grid.justifyWidth = native.justifyWidth;
        grid.searchUsingPriority = native.searchUsingPriority;

        if (!window.Detach(native)) throw new InvalidOperationException("failed to detach native context grid");
        native.DetachAll();
        try
        {
            int row = 0;
            for (int i = 0; i < buttons.Length; i++)
            {
                if (i == ScanRow && !grid.AttachPos(scan, 0, row++))
                    throw new InvalidOperationException("failed to attach scan row");
                if (!grid.AttachPos(buttons[i], 0, row++))
                    throw new InvalidOperationException("failed to attach native row " + i);
            }
            if (!window.Attach(grid)) throw new InvalidOperationException("failed to attach expanded context grid");

            // 原版靠这个 tag 判断鼠标还在菜单上；新建的网格与按钮也要带上，否则悬停「扫描」会关菜单。
            foreach (var t in window.handler.GetComponentsInChildren<Transform>(true))
                t.gameObject.tag = MenuNodeTag;
            scan.handler.gameObject.SetActive(false);
            window.Validate();
        }
        catch
        {
            try { window.Detach(grid); } catch { }
            grid.DetachAll();
            for (int i = 0; i < buttons.Length; i++) native.AttachPos(buttons[i], 0, i);
            window.Attach(native);
            window.Validate();
            throw;
        }

        _nativeGrid = native;
        _grid = grid;
        _nativeButtons = buttons;
        _button = scan;
        Log.Msg($"ContextMenuScan: context menu expanded to 1x{grid.gridHeight}");
    }

    private static int Spacing(FieldInfo? field, GridPixelElement grid)
        => field?.GetValue(grid) is int value ? value : -1;

    /// <summary>停用模组时把原版 1×7 网格放回去。</summary>
    private static void RestoreNativeMenu()
    {
        var window = _window;
        var grid = _grid;
        var native = _nativeGrid;
        var buttons = _nativeButtons;
        if (window == null || grid == null || native == null || buttons == null) return;
        if (!ReferenceEquals(window.childElement, grid)) return;

        window.Detach(grid);
        grid.DetachAll();
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].SetFixedSize(-1, -1);
            native.AttachPos(buttons[i], 0, i);
        }
        window.Attach(native);
        window.Validate();
    }

    // ---------------- 扫描 ----------------

    /// <summary>执行扫描面板；处理了返回 true。</summary>
    internal static bool TryScan(GameItem? item, string source)
    {
        try
        {
            switch (Classify(item))
            {
                case Kind.Xray:
                    StoreResponse.ShowSupplyCrateResponse(item!);
                    Log.Msg($"ContextMenuScan: {source} xray -> {GameRead.ItemName(item)}");
                    return true;

                case Kind.Chem:
                {
                    var scanner = ScannerInventory.Find(ScannerInventory.ChemScanner);
                    var ui = ChemScannerUIManager.Instance;
                    if (scanner == null || ui == null) return false;
                    ui.Open(scanner, item!);
                    Log.Msg($"ContextMenuScan: {source} chem -> {GameRead.ItemName(item)}");
                    return true;
                }

                case Kind.Water:
                {
                    var scanner = ScannerInventory.Find(ScannerInventory.WaterScanner);
                    var ui = WaterScannerUIManager.Instance;
                    if (scanner == null || ui == null) return false;
                    ui.Open(scanner, item!);
                    Log.Msg($"ContextMenuScan: {source} water -> {GameRead.ItemName(item)}");
                    return true;
                }

                case Kind.Plant:
                {
                    var ui = PlantAnalyserUIManager.Instance;
                    if (ui == null) return false;
                    ui.OpenUI(item!);
                    Log.Msg($"ContextMenuScan: {source} plant -> {GameRead.ItemName(item)}");
                    return true;
                }

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("ContextMenuScan", ex);
            return false;
        }
    }

    private enum Kind
    {
        None,
        Xray,
        Chem,
        Water,
        Plant,
    }

    /// <summary>
    /// 物品 → 设备。顺序即优先级：化学品先于液体容器（化学品往往也带 LIQUID_CONTAINER_TAG，
    /// 但它只能用化学扫描仪读到纯度与对质），补给箱最先（它同时是容器）。
    /// </summary>
    private static Kind Classify(GameItem? item)
    {
        if (item == null) return Kind.None;
        try
        {
            if (PlayerStore.Instance == null) return Kind.None;   // 只在店内生效

            if (IsLockedSupplyCrate(item) && ScannerInventory.Has(ScannerInventory.XrayScanner))
                return Kind.Xray;

            if (IsChem(item) && ScannerInventory.Has(ScannerInventory.ChemScanner))
                return Kind.Chem;

            if (GameRead.IsTag(item, "LIQUID_CONTAINER_TAG"))
            {
                if (ScannerInventory.Has(ScannerInventory.WaterScanner)) return Kind.Water;
                if (ScannerInventory.Has(ScannerInventory.ChemScanner)) return Kind.Chem;
            }

            if (GameRead.IsTag(item, "SEED_ITEM_TAG") && ScannerInventory.Has(ScannerInventory.PlantAnalyser))
                return Kind.Plant;

            return Kind.None;
        }
        catch { return Kind.None; }
    }

    /// <summary>与原版 X 射线扫描仪的目标判定一致：锁定中的补给箱。</summary>
    private static bool IsLockedSupplyCrate(GameItem item)
    {
        try
        {
            return item.IsGameItemType("SUPPLY_CRATE") && GameRead.TagBool(item, "LOCK_STATUS");
        }
        catch { return false; }
    }

    /// <summary>与原版化学扫描仪的目标判定一致：固体化学品与化学制品。</summary>
    private static bool IsChem(GameItem item)
        => GameRead.IsTag(item, "CHEM_ITEM_TAG") || GameRead.IsTag(item, "CHEMICAL_PRODUCT_TAG");
}
