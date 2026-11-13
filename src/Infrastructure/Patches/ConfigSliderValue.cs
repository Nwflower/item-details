using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ItemDetails;

/// <summary>
/// 「名称标签底色」滑条右边显示当前值（如 60%）。游戏的滑条不带数值，拖动时由滑条回调更新，
/// 「恢复默认」用不触发回调的写法，由 <see cref="ConfigSectionRefresh"/> 的重置后缀调 <see cref="Refresh"/>。
///
/// 只往滑条所在行里加一段文字，不改控件与取值。行结构不是预期样子时不加，记一条日志，面板照常打开。
/// 设置面板同一时间只有一个（CreateWindow 会先关掉同 id 的旧窗口），所以只记最近这一份。
/// </summary>
internal static class ConfigSliderValue
{
    private const string Key = "container_name_bg_opacity";

    private static CustomUIElement? _slider;
    private static TextMeshProUGUI? _text;

    internal static void Attach(CustomUIWindow window)
    {
        _slider = null;
        _text = null;
        var slider = window.Get(Key);
        var label = window.Get("label_" + Key);
        var row = slider?.root != null ? slider.root.transform.parent : null;
        if (slider?.slider == null || label?.text == null || row == null || label.root.transform.parent != row)
        {
            Log.Warn("ConfigSections.Slider", "底色滑条所在行不是预期结构，不显示数值");
            return;
        }

        var go = new GameObject("value_" + Key, typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
        go.layer = row.gameObject.layer;
        go.transform.SetParent(row, false);
        go.transform.SetSiblingIndex(slider.root.transform.GetSiblingIndex() + 1);
        var text = go.GetComponent<TextMeshProUGUI>();
        text.font = label.text.font;
        text.fontSharedMaterial = label.text.fontSharedMaterial;
        text.fontSize = label.text.fontSize;
        text.color = label.text.color;
        text.alignment = TextAlignmentOptions.MidlineRight;
        text.enableWordWrapping = false;
        text.raycastTarget = false;
        var layout = go.GetComponent<LayoutElement>();
        layout.minWidth = layout.preferredWidth = Mathf.Round(label.text.fontSize * 3f);

        _slider = slider;
        _text = text;
        slider.OnSlider(value => Show(text, value));
        Show(text, slider.GetFloat());
    }

    /// <summary>重置后同步一次；没挂上或窗口已关时什么都不做。</summary>
    internal static void Refresh()
    {
        if (_slider?.slider == null || _text == null) return;
        Show(_text, _slider.GetFloat());
    }

    private static void Show(TextMeshProUGUI text, float value)
    {
        if (text != null) text.text = Loc.T("config.slider_percent", "{0}%", Mathf.RoundToInt(value));
    }
}
