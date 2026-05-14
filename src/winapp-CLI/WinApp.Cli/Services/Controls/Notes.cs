// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

internal static class Notes
{
    private static readonly Dictionary<string, string[]> KnownPitfalls = new()
    {
        // ─── Tabs / segmented / pivot family — when to use which ───
        ["TabView"] = [
            "TabCloseRequested provides the Tab via args.Tab — do NOT construct TabViewTabCloseRequestedEventArgs manually.",
            "Use IsClosable on individual TabViewItem to control which tabs can be closed.",
            "Always set VerticalAlignment=\"Stretch\" on TabView so its content fills the available space.",
            "TabViewItem.Content can be ANY UIElement (TextBox, Grid, UserControl) — do NOT use Frame unless you need page navigation.",
            "Use TabView for browser-style document tabs (closable, draggable, dynamic). Use Pivot for static section navigation. Use Segmented (Toolkit) for compact mode/view toggles."
        ],
        ["Pivot"] = [
            "Use Pivot for static section navigation in a single page (Mail folders, Settings categories). Use TabView for closable/draggable document tabs.",
            "Pivot has limited customization vs SelectorBar; for modern Windows 11 designs prefer NavigationView + Frame or SelectorBar."
        ],
        ["SelectorBar"] = [
            "Modern Windows 11 alternative to Pivot — flat horizontal selector for switching views.",
            "Choose SelectorBar over Pivot for new code; choose TabView for closable tabs; choose NavigationView for full sidebar nav."
        ],
        ["Segmented"] = [
            "Compact toggle group for mutually exclusive view modes (e.g., Day/Week/Month). Use Segmented (Toolkit) instead of TabView/Pivot when you only need to switch between 2-5 short options without page-level routing."
        ],
        ["TabbedCommandBar"] = [
            "Office Ribbon-style command bar with multiple tabs, each containing AppBarButton groups.",
            "Use TabbedCommandBar for productivity apps with many commands organized by category. Use plain CommandBar for simpler toolbars."
        ],

        // ─── Tree / lists / collection display ───
        ["TreeView"] = [
            "NEVER use custom .NET record/class types as TreeViewNode.Content with x:Bind DataTemplate — WinRT cannot roundtrip custom managed types through IInspectable.",
            "For data-bound trees, use ItemsSource with TreeViewItem in DataTemplate that binds its own ItemsSource to Children.",
            "TreeViewItemTemplateSelector does NOT exist in WinUI 3 — invented by older training data. Use a single DataTemplate with x:Bind, or use ItemContainerStyleSelector + ItemTemplate combination instead.",
            "TreeViewItem has NO .Icon property. Put icons (Image / FontIcon / SymbolIcon) inside the DataTemplate as the first child of a horizontal StackPanel.",
            "Border has NO .Cursor property in WinUI 3. To change cursor on hover, override ProtectedCursor on a custom UserControl, or set ProtectedCursor on the parent control inheriting from Control.",
            "For a file-explorer style sidebar: bind ItemsSource to a hierarchical ObservableCollection<FolderNode>, where each FolderNode has a Children property of the same type. Use HierarchicalDataTemplate equivalent: a DataTemplate whose root binds ItemsSource={x:Bind Children}."
        ],
        ["ListView"] = [
            "Avoid wrapping ListView in ScrollViewer — it breaks virtualization.",
            "Use x:Bind in DataTemplates for better performance vs Binding.",
            "WinUI 3 has no built-in DataGrid. For table/spreadsheet UIs, use ListView with a Grid-based ItemTemplate to create columns. Add column headers with a separate Grid above the ListView. Use GridSplitter from CommunityToolkit for resizable columns.",
            "For sortable columns, handle column header Click events and re-sort the ObservableCollection or use CollectionViewSource.",
            "Use ListView for VERTICAL lists of items. Use GridView for image/card grids. Use ItemsView for the modern collection control with built-in flexible layouts. Use ItemsRepeater for custom layouts without selection support."
        ],
        ["GridView"] = [
            "Use GridView for image-heavy grids (photo browser, media library). Use ListView for text-list rows. Use ItemsView for modern flexible layouts.",
            "GridView lacks built-in column resize — for spreadsheet-style use ListView + Grid ItemTemplate + GridSplitter."
        ],
        ["ItemsView"] = [
            "Modern WinUI 3 collection control (replaces ListView/GridView for new code). Supports flexible Layout (LinedFlowLayout, StackLayout, UniformGridLayout).",
            "Choose ItemsView for new code; ListView/GridView remain for backward compatibility and richer per-item interactions like swipe-to-delete."
        ],
        ["ItemsRepeater"] = [
            "ItemsRepeater is a layout primitive — NO selection, NO scrolling on its own. Wrap in ScrollViewer.",
            "Use ItemsRepeater when you need fully custom layout and don't need ListView's interactions."
        ],

        // ─── Dialogs / popups family ───
        ["ContentDialog"] = [
            "Always set XamlRoot = Content.XamlRoot before ShowAsync() in WinUI 3 desktop apps.",
            "For save/discard/cancel flows, always provide PrimaryButtonText, SecondaryButtonText AND CloseButtonText.",
            "Use ContentDialog for modal blocking decisions. Use Flyout for inline contextual UI. Use TeachingTip for non-blocking guidance/onboarding. Use InfoBar for persistent status messages."
        ],
        ["Flyout"] = [
            "Flyout auto-dismisses on outside click. Use ShowMode='Standard' for explicit dismiss.",
            "Flyout attaches to a control via FlyoutBase.AttachedFlyout or Button.Flyout. Use ContentDialog for centered modal, MenuFlyout for menus."
        ],
        ["MenuFlyout"] = [
            "Use MenuFlyout for context menus (right-click) and dropdown menus. Items are MenuFlyoutItem / ToggleMenuFlyoutItem / MenuFlyoutSubItem."
        ],
        ["TeachingTip"] = [
            "TeachingTip must have a Target set to appear near a control, or it shows as a banner.",
            "Use TeachingTip for non-blocking onboarding hints. Use ContentDialog for modal decisions. Use InfoBar for persistent status."
        ],
        ["InfoBar"] = [
            "InfoBar auto-closes if IsOpen is bound and the user clicks the close button. Ensure two-way binding on IsOpen.",
            "Use InfoBar for persistent inline status (Severity = Informational/Success/Warning/Error). Use TeachingTip for transient targeted hints. Use AppNotification for system-wide toast notifications."
        ],

        // ─── Color pickers — Gallery vs Toolkit ───
        ["ColorPicker"] = [
            "WinUI 3 ships a built-in <ColorPicker> in Microsoft.UI.Xaml.Controls — no NuGet needed.",
            "CommunityToolkit's <controls:ColorPicker> ADDS: ShowAccentColors (Windows accent palette), IsColorPaletteVisible (preset swatches), IsColorSpectrumVisible toggle.",
            "Choose Toolkit version if you need swatches or accent colors; otherwise WinUI native is sufficient and dependency-free."
        ],
        ["ColorPickerButton"] = [
            "CommunityToolkit-only control — wraps ColorPicker in a Button with a flyout. Use for compact color selection in toolbars."
        ],

        // ─── Navigation ───
        ["NavigationView"] = [
            "Use NavigationView.MenuItems for static nav, or MenuItemsSource for data-bound.",
            "Handle SelectionChanged, not ItemInvoked, for reliable navigation.",
            "Use NavigationView for app-level sidebar navigation (Mail, Settings). Use SplitView as a low-level pane primitive when you need full control. Use TabView for documents."
        ],
        ["SplitView"] = [
            "SplitView is a low-level pane control — you build the navigation list yourself. Use NavigationView for built-in nav UX.",
            "Set DisplayMode='Inline' for always-visible pane, 'CompactInline' for icon strip, 'Overlay' for hamburger flyout."
        ],

        // ─── Numeric input ───
        ["NumberBox"] = [
            "NumberBox.Value is double — use Math.Round for integer-only scenarios.",
            "Set SpinButtonPlacementMode='Inline' to show +/- buttons.",
            "Use NumberBox for precise numeric entry with validation. Use Slider for visual range selection. Use RangeSelector (Toolkit) for min/max range.",
            "Set ValidationMode='InvalidInputOverwritten' to auto-correct invalid input."
        ],
        ["Slider"] = [
            "Use Slider for single-value range selection with visual feedback. Use NumberBox for precise typed input. Use RangeSelector (Toolkit) for two-handle min/max."
        ],
        ["RangeSelector"] = [
            "CommunityToolkit two-handle slider for min/max range selection. Use over Slider when both bounds are user-configurable."
        ],

        // ─── Text input family ───
        ["AutoSuggestBox"] = [
            "Handle TextChanged with reason == UserInput to filter suggestions. Don't filter on SuggestionChosen.",
            "Use AutoSuggestBox for search-as-you-type. Use TokenizingTextBox (Toolkit) for tag/chip input. Use RichSuggestBox (Toolkit) for @mention/#hashtag inline tokens."
        ],
        ["RichEditBox"] = [
            "Use Document.GetText/SetText with TextGetOptions/TextSetOptions for content access.",
            "Use RichEditBox for formatted text (bold/italic/lists). Use TextBox for plain multiline text."
        ],
        ["RichSuggestBox"] = [
            "CommunityToolkit control combining AutoSuggestBox + RichEditBox — produces inline tokens for @mentions, #hashtags, etc.",
            "Use Prefixes property to define trigger characters (e.g., '@#'). Tokens are tracked in Tokens collection."
        ],
        ["TokenizingTextBox"] = [
            "CommunityToolkit control for chip/tag entry — items become removable pills.",
            "Use over a plain TextBox when input is a list of distinct items (recipient lists, tag clouds)."
        ],
        ["ComboBox"] = [
            "Bind SelectedItem or SelectedIndex, not SelectedValue, unless you also set SelectedValuePath.",
            "Use ComboBox for dropdown selection from a closed list. Use AutoSuggestBox for search/filter UX. Use MenuFlyout for non-data action menus."
        ],

        // ─── Settings card family — when to use which ───
        ["SettingsCard"] = [
            "Use SettingsCard for individual settings, SettingsExpander for grouped settings with sub-items.",
            "This is the standard Windows 11 Settings page pattern — do not build settings UI with plain StackPanel + ToggleSwitch.",
            "Set IsClickEnabled=True to turn the card into a button (e.g., for navigation to a detail page)."
        ],
        ["SettingsExpander"] = [
            "Use SettingsExpander to group multiple SettingsCards under a single collapsible header. The Items collection holds child SettingsCards.",
            "For a flat list of settings, use plain SettingsCard. For 2-3 logically related settings, use SettingsExpander."
        ],

        // ─── Sizers / splitters ───
        ["GridSplitter"] = [
            "Place GridSplitter in an Auto-width column between two content columns.",
            "Use ResizeBehavior='BasedOnAlignment' for the typical between-columns case. Use ResizeDirection='Auto' to let it infer."
        ],
        ["ContentSizer"] = [
            "Use ContentSizer for single-axis resize of a sibling element. Use GridSplitter for column/row resize within a Grid."
        ],

        // ─── Layout panels ───
        ["DockPanel"] = [
            "Set LastChildFill=True to make the last child fill remaining space.",
            "Use Dock attached property on children: Top/Bottom/Left/Right."
        ],
        ["WrapPanel"] = [
            "WrapPanel arranges children in a row/column then wraps to next line. Use for tag clouds, button bars.",
            "Use WrapLayout (Toolkit) when you need it inside an ItemsRepeater for virtualized scenarios."
        ],
        ["UniformGrid"] = [
            "All cells are equal-sized. Specify Rows or Columns; the other dimension auto-calculates.",
            "Use UniformGrid for symmetric grids (calendar, matrix). Use Grid for explicit row/column sizing."
        ],
        ["StaggeredPanel"] = [
            "Pinterest-style masonry layout — variable-height columns. Use StaggeredLayout (Toolkit) inside ItemsRepeater for virtualized version."
        ],

        // ─── Other commonly-confused controls ───
        ["BreadcrumbBar"] = ["BreadcrumbBar has no Items property — only ItemsSource."],
        ["Border"] = [
            "Border has NO .Cursor property in WinUI 3 (a common WPF holdover). To change the cursor on hover, override ProtectedCursor on a custom UserControl that hosts the Border, or set Cursor on the nearest parent FrameworkElement that inherits from Control.",
            "For a click-able border, wrap with Button (Style=\"{StaticResource TransparentButtonStyle}\") instead of catching pointer events on Border directly."
        ],
        ["WebView2"] = [
            "Always call EnsureCoreWebView2Async() before using CoreWebView2 properties.",
            "NavigateToString(html) loads inline HTML content without needing a URL."
        ],
        ["Expander"] = ["Expander does not support IsExpanded two-way binding by default — use x:Bind Mode=TwoWay."],
        ["MenuBar"] = ["MenuBar must be in the title bar area or at the top of the window for standard layout."],
        ["CommandBar"] = [
            "Use PrimaryCommands for always-visible actions, SecondaryCommands for overflow.",
            "Use CommandBar for app-wide toolbars. Use TabbedCommandBar (Toolkit) for ribbon-style multi-tab commands."
        ],
        ["AdvancedCollectionView"] = [
            "Wrap your ObservableCollection with AdvancedCollectionView for sorting, filtering and grouping without modifying the source.",
            "Call RefreshFilter() after changing the Filter predicate."
        ],
    };

    public static string[] GetNotes(string controlName)
    {
        return KnownPitfalls.TryGetValue(controlName, out var notes) ? notes : [];
    }
}
