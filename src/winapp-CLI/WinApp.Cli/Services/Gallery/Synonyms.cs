// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Gallery;

internal static class Synonyms
{
    /// <summary>
    /// Multi-word phrases that should be merged into a single token before
    /// running synonym expansion. Keys must be lowercase. Match is exact-substring.
    /// </summary>
    public static readonly Dictionary<string, string> Phrases = new(StringComparer.OrdinalIgnoreCase)
    {
        // Cross-framework / multi-word terms agents may use
        ["data grid"]              = "datagrid",
        ["data-grid"]              = "datagrid",
        ["pull to refresh"]        = "pulltorefresh",
        ["pull-to-refresh"]        = "pulltorefresh",
        ["infinite scroll"]        = "infinite",
        ["lazy load"]              = "lazy",
        ["lazy loading"]           = "lazy",
        ["floating action button"] = "fab",
        ["context menu"]           = "contextmenu",
        ["right click menu"]       = "contextmenu",
        ["right-click menu"]       = "contextmenu",
        ["nav bar"]                = "navbar",
        ["tab bar"]                = "tabbar",
        ["status bar"]             = "infobar",
        ["text area"]              = "textarea",
        ["text field"]             = "textbox",
        ["text input"]             = "textbox",
        ["date picker"]            = "datepicker",
        ["time picker"]            = "timepicker",
        ["color picker"]           = "colorpicker",
        ["file picker"]            = "filepicker",
        ["info bar"]               = "infobar",
        ["search box"]             = "searchbox",
        ["search bar"]             = "searchbox",
        ["scroll view"]            = "scrollview",
        ["scroll viewer"]          = "scrollviewer",
        ["check box"]              = "checkbox",
        ["radio button"]           = "radio",
        ["radio group"]            = "radiogroup",
        ["combo box"]              = "combobox",
        ["progress bar"]           = "progressbar",
        ["progress ring"]          = "progressring",
        ["loading indicator"]      = "loading",
        ["split button"]           = "splitbutton",
        ["split view"]             = "splitview",
        ["dropdown button"]        = "dropdownbutton",
        ["icon button"]            = "iconbutton",
        ["app bar"]                = "commandbar",
        ["tab view"]               = "tabview",
        ["tree view"]              = "treeview",
        ["list view"]              = "listview",
        ["grid view"]              = "gridview",
        ["web view"]               = "webview",
        ["map view"]               = "map",
        ["image view"]             = "image",
        ["video player"]           = "video",
        ["audio player"]           = "audio",
        ["media player"]           = "player",
        ["calendar view"]          = "calendar",
        ["number box"]             = "stepper",
        ["numeric input"]          = "stepper",
        ["password box"]           = "password",
        ["rich text"]              = "richeditbox",
        ["plain text"]             = "textbox",
        ["title bar"]              = "titlebar",
        ["status indicator"]       = "infobadge",
        ["badge count"]            = "badge",
        ["share sheet"]            = "share",
        ["action sheet"]           = "actionsheet",
        ["bottom sheet"]           = "contentdialog",
        ["app notification"]       = "appnotification",
        ["push notification"]      = "appnotification",
        ["system tray"]            = "systemtray",
        ["jump list"]              = "jumplist",
        ["recent files"]           = "recent",
        ["drag and drop"]          = "dragdrop",
        ["drag drop"]              = "dragdrop",
    };

    /// <summary>Replace known multi-word phrases with their single-token equivalent.</summary>
    public static string Preprocess(string query)
    {
        var lower = query.ToLowerInvariant();
        foreach (var (phrase, replacement) in Phrases)
        {
            // Word-boundary-aware replace: avoid matching inside other words.
            // Surround with whitespace check by padding.
            int idx;
            while ((idx = lower.IndexOf(phrase, StringComparison.Ordinal)) >= 0)
            {
                bool leftOk = idx == 0 || !IsWordChar(lower[idx - 1]);
                int after = idx + phrase.Length;
                bool rightOk = after == lower.Length || !IsWordChar(lower[after]);
                if (leftOk && rightOk)
                {
                    lower = lower.Substring(0, idx) + replacement + lower.Substring(after);
                }
                else
                {
                    // Skip this hit, look for next occurrence
                    var rest = lower.IndexOf(phrase, idx + 1, StringComparison.Ordinal);
                    if (rest < 0) break;
                    // Move forward by replacing this single occurrence with itself padded — simpler: break and rely on next iteration
                    break;
                }
            }
        }
        return lower;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Maps common cross-platform/cross-framework UI terms to WinUI 3 control names.
    /// Used by SearchEngine to expand queries before BM25 scoring so agents
    /// familiar with HTML/React/WPF/iOS terminology can still find the right controls.
    /// All keys and values are lowercase. Lookup is case-insensitive.
    /// </summary>
    public static readonly Dictionary<string, string[]> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // ─── Tables / lists ───
        ["datagrid"]        = ["listview", "table", "rows", "columns"],
        ["table"]           = ["listview", "datagrid", "rows", "columns"],
        ["spreadsheet"]     = ["listview", "datagrid", "rows"],

        // ─── Dialogs / popups ───
        ["modal"]           = ["contentdialog", "dialog"],
        ["popup"]           = ["flyout", "dialog", "teachingtip"],
        ["alert"]           = ["contentdialog", "infobar"],
        ["confirm"]         = ["contentdialog"],
        ["prompt"]          = ["contentdialog"],
        ["actionsheet"]     = ["contentdialog", "menuflyout"],
        ["snackbar"]        = ["infobar", "teachingtip"],
        ["toast"]           = ["appnotification", "infobar"],
        ["banner"]          = ["infobar"],
        ["tooltip"]         = ["tooltipservice", "teachingtip"],

        // ─── Navigation ───
        ["sidebar"]         = ["navigationview", "splitview", "navigation"],
        ["navbar"]          = ["navigationview", "menubar"],
        ["drawer"]          = ["navigationview", "splitview"],
        ["hamburger"]       = ["navigationview", "splitview"],
        ["breadcrumbs"]     = ["breadcrumbbar"],
        ["crumbs"]          = ["breadcrumbbar"],

        // ─── Tabs ───
        ["tabs"]            = ["tabview", "pivot", "selectorbar"],
        ["tabbar"]          = ["selectorbar", "segmented"],

        // ─── Inputs ───
        ["dropdown"]        = ["combobox", "menuflyout", "dropdownbutton"],
        ["select"]          = ["combobox"],
        ["picker"]          = ["combobox", "calendarpicker", "datepicker", "timepicker"],
        ["stepper"]         = ["numberbox"],
        ["radiogroup"]      = ["radiobuttons"],
        ["switch"]          = ["toggleswitch"],
        ["chip"]            = ["tokenizingtextbox", "token"],
        ["chips"]           = ["tokenizingtextbox", "token"],
        ["pill"]            = ["tokenizingtextbox", "segmented"],
        ["range"]           = ["rangeselector", "slider"],
        ["search"]          = ["autosuggestbox", "textbox"],
        ["searchbox"]       = ["autosuggestbox"],
        ["autocomplete"]    = ["autosuggestbox"],
        ["typeahead"]       = ["autosuggestbox"],
        ["mention"]         = ["richsuggestbox"],
        ["hashtag"]         = ["richsuggestbox"],

        // ─── Text ───
        ["textarea"]        = ["textbox", "richeditbox"],
        ["multiline"]       = ["textbox", "richeditbox"],
        ["editor"]          = ["richeditbox", "textbox"],
        ["wysiwyg"]         = ["richeditbox"],
        ["password"]        = ["passwordbox"],
        ["label"]           = ["textblock"],
        ["heading"]         = ["textblock"],
        ["link"]            = ["hyperlinkbutton", "hyperlink"],

        // ─── Layout ───
        ["flex"]            = ["stackpanel", "wrappanel", "grid"],
        ["flexbox"]         = ["stackpanel", "wrappanel"],
        ["card"]            = ["settingscard", "border", "expander"],
        ["container"]       = ["border", "grid", "stackpanel"],
        ["divider"]         = ["appbarseparator", "menubarseparator", "rectangle"],
        ["splitter"]        = ["gridsplitter", "splitview"],
        ["resizable"]       = ["gridsplitter", "contentsizer"],
        ["accordion"]       = ["expander", "settingsexpander"],
        ["collapse"]        = ["expander", "treeview"],
        ["dock"]            = ["dockpanel"],
        ["wrap"]            = ["wrappanel", "wraplayout"],
        ["masonry"]         = ["staggeredpanel", "staggeredlayout"],

        // ─── Scrolling / virtualization ───
        ["scrollview"]      = ["scrollviewer"],
        ["virtualized"]     = ["listview", "itemsrepeater", "gridview"],
        ["lazy"]            = ["listview", "itemsrepeater"],
        ["pulltorefresh"]   = ["pulltorefresh"],
        ["infinite"]        = ["incrementalloadingcollection"],

        // ─── Tree / hierarchy ───
        ["tree"]            = ["treeview", "headeredtreeview"],
        ["folder"]          = ["treeview"],
        ["hierarchy"]       = ["treeview"],
        ["outline"]         = ["treeview"],

        // ─── Progress / loading ───
        ["loading"]         = ["progressring", "progressbar"],
        ["spinner"]         = ["progressring"],
        ["loader"]          = ["progressring", "progressbar"],

        // ─── Commands / buttons ───
        ["fab"]             = ["button"],
        ["iconbutton"]      = ["button", "appbarbutton"],
        ["toolbar"]         = ["commandbar", "appbarbutton"],
        ["ribbon"]          = ["tabbedcommandbar"],
        ["menu"]            = ["menubar", "menuflyout"],
        ["contextmenu"]     = ["menuflyout"],
        ["rightclick"]      = ["menuflyout"],

        // ─── Media ───
        ["video"]           = ["mediaplayerelement"],
        ["audio"]           = ["mediaplayerelement"],
        ["player"]          = ["mediaplayerelement"],
        ["webview"]         = ["webview2"],
        ["browser"]         = ["webview2"],
        ["iframe"]          = ["webview2"],
        ["crop"]            = ["imagecropper"],
        ["thumbnail"]       = ["image"],
        ["avatar"]          = ["personpicture"],
        ["profile"]         = ["personpicture"],

        // ─── Date / time / color ───
        ["calendar"]        = ["calendarview", "calendardatepicker"],
        ["datepicker"]      = ["datepicker", "calendardatepicker"],
        ["clock"]           = ["timepicker"],
        ["palette"]         = ["colorpicker"],

        // ─── Settings / forms ───
        ["settings"]        = ["settingscard", "settingsexpander"],
        ["form"]            = ["settingscard", "stackpanel"],
        ["preferences"]     = ["settingscard", "settingsexpander"],
        ["options"]         = ["settingscard", "combobox", "radiobuttons"],

        // ─── System / shell ───
        ["taskbar"]         = ["jumplist", "appbadge"],
        ["recent"]          = ["jumplist"],
        ["tray"]            = ["systemtray"],
        ["systemtray"]      = ["systemtray"],
        ["share"]           = ["sharecontract"],
        ["openfile"]        = ["filepicker"],
        ["savefile"]        = ["filepicker"],
        ["dragdrop"]        = ["drag", "drop"],

        // ─── Abbreviations ───
        ["btn"]             = ["button"],
        ["txt"]             = ["textbox", "textblock"],
        ["img"]             = ["image"],
        ["nav"]             = ["navigationview"],
        ["lv"]              = ["listview"],
        ["gv"]              = ["gridview"],

        // ─── Plurals → singular alias to actual control ───
        ["buttons"]         = ["button"],
        ["lists"]           = ["listview"],
        ["dialogs"]         = ["contentdialog"],
    };

    /// <summary>
    /// Expand a tokenized query by appending synonyms for each known term.
    /// Returns the original tokens plus any synonym tokens.
    /// </summary>
    public static string[] Expand(string[] queryWords)
    {
        var result = new List<string>(queryWords);
        var seen = new HashSet<string>(queryWords, StringComparer.OrdinalIgnoreCase);
        foreach (var w in queryWords)
        {
            if (Map.TryGetValue(w, out var syns))
            {
                foreach (var s in syns)
                {
                    if (seen.Add(s)) result.Add(s);
                }
            }
        }
        return result.ToArray();
    }
}
