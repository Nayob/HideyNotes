using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HideyNotes.Localization;
using HideyNotes.Models;
using HideyNotes.Services;
using Forms = System.Windows.Forms;

namespace HideyNotes.Windows
{
    public partial class PanelWindow : Window
    {
        private IntPtr _hwnd;
        private HwndSource? _source;
        private int _broadcastMessage;
        private bool _suppressTextChanged;
        private bool _loading = true;
        private int _charCount;
        private int _wordCount;
        private string _saveStateKey = "panel.status.saved";
        private string? _saveStateArg;

        private Note? _dragNote;
        private Point _dragStart;
        private bool _dragging;
        private HistoryWindow? _historyWindow;
        private bool _historyClickThrough;
        private bool _suppressHistoryRefocus;
        private bool _panelClosing;

        private List<int> _matches = new();
        private int _searchIndex = -1;

        private readonly List<string> _fontChoices = LoadInstalledFonts();

        private static List<string> LoadInstalledFonts()
        {
            var names = new List<string>();

            try
            {
                foreach (var family in Fonts.SystemFontFamilies)
                {
                    if (!string.IsNullOrWhiteSpace(family.Source)) names.Add(family.Source);
                }
            }
            catch (Exception ex)
            {
                Log.Error("font enumeration failed", ex);
            }

            names.Sort(StringComparer.CurrentCultureIgnoreCase);
            return names;
        }

        private static readonly (string Hex, string NameKey)[] Swatches =
        {
            ("", "panel.textColor.theme"),
            ("#FFEAEBEF", "panel.textColor.light"),
            ("#FFC8CBD2", "panel.textColor.gray"),
            ("#FF191B1F", "panel.textColor.dark"),
            ("#FF5E9DFF", "panel.textColor.blue"),
            ("#FF48B36B", "panel.textColor.green"),
            ("#FFE8A33D", "panel.textColor.orange"),
            ("#FFE05C5C", "panel.textColor.red"),
            ("#FF9B6BE0", "panel.textColor.violet")
        };

        private NotesStore Store => App.Current.Notes;

        private bool SearchOpen => SearchBar.Visibility == Visibility.Visible;

        public PanelWindow()
        {
            InitializeComponent();
            DataContext = Store;
            SetVersionLabel();
            FontBox.ItemsSource = _fontChoices;

            SourceInitialized += OnSourceInitialized;
            Activated += (_, _) =>
            {
                if (IsHistoryOpen) _historyClickThrough = true;

                App.Current.Controller?.NotifyPanelActivated();

                // No tray: the taskbar button is the way back to the panel.
                if (!TaskbarFallback) return;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var controller = App.Current.Controller;
                    if (controller is null || controller.IsOpen || controller.IsAnimating) return;
                    controller.ShowPanel();
                }), DispatcherPriority.Background);
            };
            Deactivated += (_, _) => App.Current.Controller?.NotifyPanelDeactivated();
            PreviewKeyDown += OnPreviewKeyDown;
            Closing += OnWindowClosing;
            Loaded += (_, _) => ScrollActiveTabIntoView();

            Editor.TextChanged += OnEditorTextChanged;
            DataObject.AddPastingHandler(Editor, OnEditorPasting);

            App.Current.Theme.Changed += OnThemeChanged;
            Store.ActiveChanged += OnActiveChanged;
            Store.Saved += OnNoteSaved;

            ApplyTypography();
            ShowActiveNote();
            LoadSettingsIntoUi();
            ScopeHere.IsChecked = true;

            _loading = false;
        }

        public bool IsSettingsOpen => SettingsLayer.Visibility == Visibility.Visible;

        public bool IsHistoryOpen => _historyWindow is { IsLoaded: true };

        /// <summary>When the tray is unavailable the panel acts as a regular taskbar window and closing it exits.</summary>
        public bool TaskbarFallback { get; set; }

        // ---------- window plumbing ----------

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);

            WindowVisuals.SetRoundedCorners(_hwnd);

            if (!TaskbarFallback) WindowVisuals.MakeToolWindow(_hwnd);

            ApplyBackdrop();

            _broadcastMessage = Native.RegisterWindowMessage("HIDEYNOTES_Activate");
            App.Current.Controller?.AttachPanelHandle(_hwnd);
        }

        /// <summary>
        /// Shows the version next to the title. The tooltip carries the full product version,
        /// including the build's commit hash, which is what a bug report needs.
        /// </summary>
        private void SetVersionLabel()
        {
            var version = typeof(PanelWindow).Assembly.GetName().Version;
            VersionText.Text = version is null ? string.Empty : $"v{version.Major}.{version.Minor}.{version.Build}";

            try
            {
                var product = System.Diagnostics.FileVersionInfo
                    .GetVersionInfo(Environment.ProcessPath ?? string.Empty).ProductVersion;

                if (!string.IsNullOrWhiteSpace(product)) VersionText.ToolTip = product;
            }
            catch
            {
                // the label is cosmetic: a missing tooltip is not worth a log entry
            }
        }

        private void ApplyBackdrop()
        {
            var settings = App.Current.Settings;
            var theme = App.Current.Theme;

            var mica = settings.UseMica && WindowVisuals.TryEnableBackdrop(_hwnd, theme.IsDark);
            theme.MicaEnabled = mica;
            theme.Apply();
            Log.Info($"backdrop: mica={mica}");
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyService.Id)
            {
                App.Current.Controller?.Toggle();
                handled = true;
            }
            else if (_broadcastMessage != 0 && msg == _broadcastMessage)
            {
                App.Current.Controller?.ShowPanel();
                handled = true;
            }
            else if (msg == Native.WM_MOUSEACTIVATE)
            {
                // A click on the panel while the history dialog sits on top: the dialog closes
                // on deactivation, and focus has to come back here afterwards.
                if (IsHistoryOpen) _historyClickThrough = true;
            }

            return IntPtr.Zero;
        }

        // ---------- typography ----------

        private void ApplyTypography()
        {
            var settings = App.Current.Settings;

            var family = new FontFamily(
                string.IsNullOrWhiteSpace(settings.FontFamily) ? "Segoe UI" : settings.FontFamily);

            Editor.FontFamily = family;
            Editor.FontSize = settings.FontSize;
            Editor.Document.FontFamily = family;
            Editor.Document.FontSize = settings.FontSize;
            Editor.Document.LineHeight = Math.Max(1, settings.FontSize * settings.LineHeight);
            Editor.Document.PagePadding = new Thickness(settings.EditorPadding);
            Editor.Foreground = ResolveTextBrush(settings);
        }

        private Brush ResolveTextBrush(AppSettings settings)
        {
            var hex = App.Current.Theme.IsDark ? settings.TextColorDark : settings.TextColorLight;

            if (!string.IsNullOrWhiteSpace(hex))
            {
                try
                {
                    var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                    brush.Freeze();
                    return brush;
                }
                catch { }
            }

            var fallback = new SolidColorBrush(App.Current.Theme.TextColor);
            fallback.Freeze();
            return fallback;
        }

        private void OnThemeChanged()
        {
            WindowVisuals.SetDarkMode(_hwnd, App.Current.Theme.IsDark);
            ApplyTypography();

            // The text colour is stored per theme, so the picker follows the switch.
            SyncHexBox();
            BuildSwatches();
        }

        private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _panelClosing = true;

            if (!TaskbarFallback) return;

            // In fallback mode closing the taskbar button is the way out of the app.
            Dispatcher.BeginInvoke(new Action(() => App.Current.ExitApp()), DispatcherPriority.Background);
        }

        // ---------- app ----------

        /// <summary>Opens the panel right away: without a tray there is nothing else to click.</summary>
        public void ShowOnStartup()
        {
            TaskbarFallback = true;
            ShowInTaskbar = true;
            Loaded += (_, _) => App.Current.Controller?.ShowPanel();
        }

        // ---------- notes ----------

        private void OnActiveChanged()
        {
            ShowActiveNote();
            ScrollActiveTabIntoView();
        }

        private void ShowActiveNote()
        {
            var note = Store.Active;
            if (note is null) return;

            _suppressTextChanged = true;
            Editor.Document.Blocks.Clear();
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            if (note.Text.Length > 0) paragraph.Inlines.Add(new Run(note.Text));
            Editor.Document.Blocks.Add(paragraph);
            _suppressTextChanged = false;

            UpdateCounters(note.Text);
            SetSaveState("panel.status.saved");
            UpdateStatus();

            if (SearchOpen && !string.IsNullOrEmpty(SearchBox.Text)) RunSearch(selectFirst: true);
        }

        private void ScrollActiveTabIntoView()
        {
            var note = Store.Active;
            if (note is null) return;

            if (TabsItems.ItemContainerGenerator.ContainerFromItem(note) is FrameworkElement container)
            {
                container.BringIntoView();
            }
        }

        private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressTextChanged || _loading) return;

            var note = Store.Active;
            if (note is null) return;

            var text = GetText();
            Store.QueueText(note, text);
            UpdateCounters(text);
            SetSaveState("panel.status.modified");
            UpdateStatus();

            if (SearchOpen && !string.IsNullOrEmpty(SearchBox.Text)) RunSearch(selectFirst: false);
        }

        private void OnNoteSaved(DateTime at)
        {
            SetSaveState("panel.status.savedAt", at.ToString("HH:mm:ss"));
            UpdateStatus();
        }

        public void CommitDraft()
        {
            if (SearchOpen) PersistLastSearch();
            Store.Flush();
        }

        private string GetText()
        {
            var range = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd);
            var text = range.Text ?? string.Empty;
            if (text.EndsWith("\r\n", StringComparison.Ordinal)) text = text[..^2];
            return text;
        }

        private void OnEditorPasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText)) return;

            var text = e.DataObject.GetData(DataFormats.UnicodeText) as string ?? string.Empty;
            e.CancelCommand();
            Editor.Selection.Text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private void UpdateCounters(string text)
        {
            _charCount = text.Length;
            _wordCount = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private void UpdateStatus()
        {
            StatusText.Text = $"{_charCount} {Loc.Instance.Plural("panel.status.chars", _charCount)} · " +
                              $"{_wordCount} {Loc.Instance.Plural("panel.status.words", _wordCount)} · " +
                              $"{(_saveStateArg is null ? Loc.T(_saveStateKey) : Loc.F(_saveStateKey, _saveStateArg))}";
        }

        /// <summary>Remembers how the state reads, so it can be re-rendered when the language changes.</summary>
        private void SetSaveState(string key, string? argument = null)
        {
            _saveStateKey = key;
            _saveStateArg = argument;
            UpdateStatus();
        }

        // ---------- search ----------

        /// <summary>True while a shortcut is opening the search bar: the scope change is not a user action then.</summary>
        private bool _openingSearch;

        /// <summary>Opens the search bar; the scope is chosen by the shortcut that opened it.</summary>
        public void OpenSearch(bool allTabs = false)
        {
            SearchBar.Visibility = Visibility.Visible;

            _openingSearch = true;
            try
            {
                if (allTabs) ScopeAll.IsChecked = true;
                else ScopeHere.IsChecked = true;
            }
            finally
            {
                _openingSearch = false;
            }

            var settings = App.Current.Settings;
            if (string.IsNullOrEmpty(SearchBox.Text) && settings.RememberSearch && !string.IsNullOrEmpty(settings.LastSearch))
            {
                SearchBox.Text = settings.LastSearch;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            }), DispatcherPriority.Input);

            RunSearch(selectFirst: true);
        }

        public void OpenSearchWith(string query)
        {
            SearchBar.Visibility = Visibility.Visible;
            if (ScopeHere.IsChecked != true && ScopeAll.IsChecked != true) ScopeHere.IsChecked = true;

            SearchBox.Text = query;
            Dispatcher.BeginInvoke(new Action(() => SearchBox.Focus()), DispatcherPriority.Input);
        }

        public void CloseSearch()
        {
            if (!SearchOpen) return;

            PersistLastSearch();

            SearchBar.Visibility = Visibility.Collapsed;
            SearchBox.Text = string.Empty;
            ClearHighlights();
            _matches.Clear();
            _searchIndex = -1;
            SearchInfo.Text = string.Empty;
            ApplyFilter(string.Empty);
            Editor.Focus();
        }

        private void PersistLastSearch()
        {
            var settings = App.Current.Settings;
            if (!settings.RememberSearch) return;

            var query = SearchBox.Text?.Trim() ?? string.Empty;
            if (query.Length == 0) return;

            settings.LastSearch = query;
            SettingsStore.Save(settings);
        }

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            RunSearch(selectFirst: true);
        }

        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    MoveMatch((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    CloseSearch();
                    e.Handled = true;
                    break;
            }
        }

        private void OnScopeChecked(object sender, RoutedEventArgs e)
        {
            if (_loading || _openingSearch) return;
            RunSearch(selectFirst: true);
        }

        private void OnNextMatch(object sender, RoutedEventArgs e) => MoveMatch(1);

        private void OnPrevMatch(object sender, RoutedEventArgs e) => MoveMatch(-1);

        private void OnCloseSearchClick(object sender, RoutedEventArgs e) => CloseSearch();

        private void RunSearch(bool selectFirst)
        {
            var query = SearchBox.Text ?? string.Empty;

            if (query.Length == 0)
            {
                _matches = new List<int>();
                _searchIndex = -1;
                SearchInfo.Text = string.Empty;
                ClearHighlights();
                ApplyFilter(string.Empty);
                return;
            }

            _matches = FindMatches(GetText(), query);
            _searchIndex = _matches.Count == 0 ? -1 : 0;
            UpdateSearchInfo();

            ApplyFilter(ScopeAll.IsChecked == true ? query : string.Empty);

            RenderHighlights();
            if (selectFirst && _matches.Count > 0) ScrollToMatch(0);

            Log.Info($"search '{query}' scope={(ScopeAll.IsChecked == true ? "all" : "here")} matches={_matches.Count}");
        }

        private void MoveMatch(int delta)
        {
            if (_matches.Count == 0) return;

            var index = _searchIndex < 0 ? 0 : _searchIndex + delta;
            if (index < 0) index = _matches.Count - 1;
            if (index >= _matches.Count) index = 0;

            _searchIndex = index;
            RenderHighlights();
            ScrollToMatch(index);
            UpdateSearchInfo();
        }

        private void UpdateSearchInfo()
        {
            if (string.IsNullOrEmpty(SearchBox.Text))
            {
                SearchInfo.Text = string.Empty;
                return;
            }

            SearchInfo.Text = _matches.Count == 0 ? Loc.T("panel.search.none") : $"{_searchIndex + 1}/{_matches.Count}";
        }

        private void ScrollToMatch(int index)
        {
            if (index < 0 || index >= _matches.Count) return;

            var start = GetPointerAt(Editor.Document, _matches[index]);
            (start?.Paragraph as FrameworkContentElement)?.BringIntoView();
        }

        /// <summary>Marks every match and makes the current one stand out.</summary>
        private void RenderHighlights()
        {
            if (!SearchOpen) return;

            _suppressTextChanged = true;
            try
            {
                var all = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd);
                all.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Transparent);

                var limit = Math.Min(_matches.Count, 300);
                for (var i = 0; i < limit; i++)
                {
                    var range = GetRangeAt(i);
                    if (range is null) continue;

                    range.ApplyPropertyValue(TextElement.BackgroundProperty,
                        i == _searchIndex ? CurrentMatchBrush : MatchBrush);
                }
            }
            catch (Exception ex)
            {
                Log.Error("search highlight failed", ex);
            }
            finally
            {
                _suppressTextChanged = false;
            }
        }

        private void ClearHighlights()
        {
            _suppressTextChanged = true;
            try
            {
                var all = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd);
                all.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Transparent);
            }
            catch (Exception ex)
            {
                Log.Error("search highlight clear failed", ex);
            }
            finally
            {
                _suppressTextChanged = false;
            }
        }

        private TextRange? GetRangeAt(int index)
        {
            if (index < 0 || index >= _matches.Count) return null;

            var start = GetPointerAt(Editor.Document, _matches[index]);
            if (start is null) return null;

            var end = start.GetPositionAtOffset(SearchBox.Text.Length, LogicalDirection.Forward);
            if (end is null) return null;

            return new TextRange(start, end);
        }

        private static readonly Brush MatchBrush = MakeBrush(Color.FromArgb(0x3D, 0x5E, 0x9D, 0xFF));
        private static readonly Brush CurrentMatchBrush = MakeBrush(Color.FromArgb(0xB4, 0x5E, 0x9D, 0xFF));

        private static Brush MakeBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private void ApplyFilter(string query)
        {
            foreach (var note in Store.Tabs)
            {
                if (string.IsNullOrEmpty(query))
                {
                    note.IsTabVisible = true;
                    note.MatchCount = 0;
                    continue;
                }

                var count = FindMatches(note.Text, query).Count;
                note.MatchCount = count;
                note.IsTabVisible = count > 0;
            }
        }

        private static List<int> FindMatches(string text, string query)
        {
            var result = new List<int>();
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(text)) return result;

            var index = 0;
            while (index <= text.Length - query.Length)
            {
                var found = text.IndexOf(query, index, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;

                result.Add(found);
                index = found + Math.Max(1, query.Length);
            }

            return result;
        }

        /// <summary>Maps a character index of the document text to a TextPointer.</summary>
        private static TextPointer? GetPointerAt(FlowDocument document, int charIndex)
        {
            var pointer = document.ContentStart;
            var count = 0;

            while (pointer is not null && pointer.CompareTo(document.ContentEnd) < 0)
            {
                var context = pointer.GetPointerContext(LogicalDirection.Forward);

                if (context == TextPointerContext.Text)
                {
                    var run = pointer.GetTextInRun(LogicalDirection.Forward);
                    if (charIndex < count + run.Length) return pointer.GetPositionAtOffset(charIndex - count);
                    count += run.Length;
                    pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
                    continue;
                }

                if (context == TextPointerContext.ElementEnd && pointer.GetAdjacentElement(LogicalDirection.Forward) is Paragraph)
                {
                    count += 2; // paragraph break counts as "\r\n" in TextRange.Text
                }

                pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
            }

            return null;
        }

        // ---------- focus ----------

        public void FocusEditor()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsVisible) return;
                Editor.Focus();
                Keyboard.Focus(Editor);
            }), DispatcherPriority.Input);
        }

        // ---------- tabs ----------

        private void OnNewTabClick(object sender, RoutedEventArgs e) => Store.CreateNote();

        private void OnTabCloseClick(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement { DataContext: Note note }) Store.Close(note);
        }

        private void OnTabMouseMiddle(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            if (sender is FrameworkElement { DataContext: Note note })
            {
                Store.Close(note);
                e.Handled = true;
            }
        }

        private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: Note note } element) return;

            Store.SetActive(note);

            if (e.ClickCount == 2)
            {
                BeginRename(note);
                e.Handled = true;
                return;
            }

            _dragNote = note;
            _dragStart = e.GetPosition(this);
            _dragging = false;
            element.CaptureMouse();
        }

        private void OnTabMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragNote is null || e.LeftButton != MouseButtonState.Pressed) return;

            if (!_dragging)
            {
                var delta = e.GetPosition(this) - _dragStart;
                if (Math.Abs(delta.X) < 6) return;
                _dragging = true;
            }

            var index = IndexAt(e.GetPosition(TabsItems));
            if (index >= 0 && index != Store.Tabs.IndexOf(_dragNote)) Store.Move(_dragNote, index);
        }

        private void OnTabMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element) element.ReleaseMouseCapture();
            _dragNote = null;
            _dragging = false;
        }

        private int IndexAt(Point point)
        {
            for (var i = 0; i < TabsItems.Items.Count; i++)
            {
                if (TabsItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container) continue;
                if (container.ActualWidth <= 0) continue;

                var bounds = container.TransformToAncestor(TabsItems)
                    .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));

                if (bounds.Contains(point)) return i;
            }

            return -1;
        }

        private void OnTabScrollWheel(object sender, MouseWheelEventArgs e)
        {
            TabScroll.ScrollToHorizontalOffset(TabScroll.HorizontalOffset - e.Delta / 2.0);
            e.Handled = true;
        }

        // ---------- rename ----------

        private void BeginRename(Note? note)
        {
            if (note is null) return;

            note.IsEditing = true;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                var box = FindTabTextBox(note);
                if (box is null)
                {
                    note.IsEditing = false;
                    return;
                }

                box.Text = note.Title;
                box.Focus();
                box.SelectAll();
            }), DispatcherPriority.Input);
        }

        private TextBox? FindTabTextBox(Note note)
        {
            if (TabsItems.ItemContainerGenerator.ContainerFromItem(note) is not DependencyObject container) return null;
            return FindVisualChildByName<TextBox>(container, "TabEdit");
        }

        private static T? FindVisualChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed && typed.Name == name) return typed;

                var nested = FindVisualChildByName<T>(child, name);
                if (nested is not null) return nested;
            }

            return null;
        }

        private void OnTabEditKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box || box.DataContext is not Note note) return;

            switch (e.Key)
            {
                case Key.Enter:
                    CommitRename(note, box);
                    Editor.Focus();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    note.IsEditing = false;
                    Editor.Focus();
                    e.Handled = true;
                    break;
            }
        }

        private void OnTabEditLostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box && box.DataContext is Note note) CommitRename(note, box);
        }

        private void CommitRename(Note note, TextBox box)
        {
            if (!note.IsEditing) return;

            note.IsEditing = false;
            Store.Rename(note, box.Text);
        }

        // ---------- shortcuts ----------

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (alt && key is >= Key.D1 and <= Key.D9)
            {
                // Some utilities grab plain Ctrl+1..9 system-wide; Alt (or Ctrl+Alt) + digit always works.
                Store.ActivateIndex(key - Key.D1);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (SearchOpen)
                {
                    CloseSearch();
                    e.Handled = true;
                    return;
                }

                if (Keyboard.FocusedElement is TextBox) return; // inline tab rename handles it
                if (IsSettingsOpen) CloseSettings();
                else App.Current.Controller?.HidePanel();
                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == Key.F)
            {
                // Ctrl+F searches the current note, Ctrl+Shift+F searches every tab. With the bar
                // already open the other combination only switches the scope; the same one closes.
                var allTabs = shift;

                if (!SearchOpen) OpenSearch(allTabs);
                else if ((ScopeAll.IsChecked == true) == allTabs) CloseSearch();
                else if (allTabs) ScopeAll.IsChecked = true;
                else ScopeHere.IsChecked = true;

                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == Key.P)
            {
                // Same button as the gear: the second press closes the settings, like the hotkey.
                if (IsSettingsOpen) CloseSettings();
                else OpenSettings();

                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == Key.Q)
            {
                App.Current.ExitApp();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F1)
            {
                OpenHelpNote();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F3)
            {
                MoveMatch(shift ? -1 : 1);
                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == Key.S)
            {
                SaveActiveNoteAs();
                e.Handled = true;
                return;
            }

            if (ctrl && shift && e.Key == Key.T)
            {
                Store.RestoreLastClosed();
                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == Key.T)
            {
                Store.CreateNote();
                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == Key.W)
            {
                if (shift) Store.CloseAndForget(Store.Active);
                else Store.Close(Store.Active);
                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == Key.Tab)
            {
                Store.CycleActive(shift ? -1 : 1);
                e.Handled = true;
                return;
            }

            if (ctrl && e.Key is >= Key.D1 and <= Key.D9)
            {
                Store.ActivateIndex(e.Key - Key.D1);
                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == Key.H)
            {
                OpenHistory();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F2)
            {
                BeginRename(Store.Active);
                e.Handled = true;
            }
        }

        // ---------- help ----------

        private static string HelpTitle => Loc.T("note.help");

        private static string HelpText => Loc.T("help.text");

        private void OnHelpClick(object sender, RoutedEventArgs e) => OpenHelpNote();

        public void OpenHelpNote()
        {
            var note = Store.Tabs.Concat(Store.History).FirstOrDefault(n => n.IsHelp);

            if (note is null)
            {
                note = Store.CreateNote(title: HelpTitle, text: HelpText, isHelp: true);
            }
            else
            {
                if (!note.IsOpen) Store.Restore(note);
                if (note.Title != HelpTitle) Store.Rename(note, HelpTitle);
            }

            note.IsHelp = true;

            // Keep the built-in page in sync with the current version and language.
            if (note.Text != HelpText)
            {
                note.Text = HelpText;
                Store.QueueText(note, HelpText);
                Store.Flush();
            }

            Store.SetActive(note);
            ShowActiveNote();
            Editor.Focus();
        }

        // ---------- language ----------

        private sealed class LanguageChoice
        {
            public string Code { get; init; } = "";
            public string Name { get; init; } = "";

            public override string ToString() => Name;
        }

        private bool _loadingLanguages;

        private void LoadLanguageChoices()
        {
            var current = App.Current.Settings.Language ?? string.Empty;

            var choices = new List<LanguageChoice>
            {
                new() { Code = "", Name = Loc.T("panel.language.system") }
            };

            foreach (var language in Loc.Instance.Languages)
            {
                choices.Add(new LanguageChoice { Code = language.Code, Name = language.Name });
            }

            _loadingLanguages = true;
            LanguageBox.ItemsSource = choices;
            LanguageBox.SelectedItem = choices.FirstOrDefault(c => string.Equals(c.Code, current, StringComparison.OrdinalIgnoreCase))
                                      ?? choices[0];
            _loadingLanguages = false;
        }

        private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _loadingLanguages) return;
            if (LanguageBox.SelectedItem is not LanguageChoice choice) return;

            var settings = App.Current.Settings;
            if (string.Equals(settings.Language, choice.Code, StringComparison.OrdinalIgnoreCase)) return;

            settings.Language = choice.Code;
            SettingsStore.Save(settings);
            Loc.Instance.SetLanguage(choice.Code);
            ApplyLanguage();
        }

        /// <summary>Re-renders everything that is produced in code rather than by a XAML binding.</summary>
        public void ApplyLanguage()
        {
            HotkeyLabel.Text = Loc.F("panel.hotkey", App.Current.Settings.Hotkey);
            TrayHint.Text = App.Current.TrayAvailable ? Loc.T("panel.trayHint") : Loc.T("panel.trayUnavailable");
            PinButton.ToolTip = Loc.T(PinButton.IsChecked == true ? "panel.pin.on" : "panel.pin");

            LoadMonitorChoices();
            LoadLanguageChoices();
            UpdateStatus();
            HexHint.Text = Loc.T("panel.textColor.invalid");

            _historyWindow?.ApplyLanguage();
            UpdateHelpNote();

            App.Current.RefreshTrayMenu();
            Log.Info($"language: {Loc.Instance.Code}");
        }

        /// <summary>Keeps the built-in help tab in the current language.</summary>
        private void UpdateHelpNote()
        {
            var help = Store.Tabs.Concat(Store.History).FirstOrDefault(n => n.IsHelp);
            if (help is null) return;

            if (help.Title != HelpTitle) Store.Rename(help, HelpTitle);

            if (help.Text == HelpText) return;

            help.Text = HelpText;
            Store.QueueText(help, HelpText);
            Store.Flush();

            if (ReferenceEquals(Store.Active, help)) ShowActiveNote();
        }

        // ---------- history ----------

        private void OnHistoryClick(object sender, RoutedEventArgs e) => OpenHistory();

        public void OpenHistory()
        {
            if (_historyWindow is { IsLoaded: true })
            {
                _historyWindow.KeepOpen();
                _historyWindow.Activate();
                return;
            }

            _historyWindow = new HistoryWindow { Owner = this };
            _historyWindow.Closed += (_, _) =>
            {
                _historyWindow = null;

                var clicked = _historyClickThrough;
                _historyClickThrough = false;

                if (_suppressHistoryRefocus || _panelClosing) return;

                // Back into the note either when the click was meant for the panel, or when the
                // dialog was closed from inside while our own process still holds the focus.
                var foreground = Native.GetForegroundWindow();
                Native.GetWindowThreadProcessId(foreground, out var processId);
                if (!clicked && processId != (uint)Environment.ProcessId) return;

                ReturnFocusToPanel();
            };
            _historyWindow.Show();
        }

        public void CloseHistory()
        {
            if (_historyWindow is not { IsLoaded: true }) return;

            // Called from the slide controller: no focus juggling, the panel is going away.
            _suppressHistoryRefocus = true;
            _historyWindow.Close();
            _suppressHistoryRefocus = false;
        }

        /// <summary>Hands the keyboard back to the editor after the dialog closed.</summary>
        private void ReturnFocusToPanel()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var controller = App.Current.Controller;
                if (!IsVisible || _panelClosing || controller is null || !controller.IsOpen) return;

                controller.NotifyPanelActivated();
                ForceForeground();

                // The activation often only settles after the click has been fully processed.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!IsVisible || _panelClosing) return;
                    var foreground = Native.GetForegroundWindow();
                    if (foreground == _hwnd) return;

                    Native.GetWindowThreadProcessId(foreground, out var processId);
                    if (processId != (uint)Environment.ProcessId) return;

                    ForceForeground();
                }), DispatcherPriority.ApplicationIdle);
            }), DispatcherPriority.Background);
        }

        private void ForceForeground()
        {
            if (_hwnd == IntPtr.Zero) return;

            var foreground = Native.GetForegroundWindow();
            if (foreground != _hwnd)
            {
                var ownThread = Native.GetCurrentThreadId();
                var otherThread = Native.GetWindowThreadProcessId(foreground, out _);
                var attached = otherThread != ownThread && Native.AttachThreadInput(ownThread, otherThread, true);

                Native.SetForegroundWindow(_hwnd);

                if (attached) Native.AttachThreadInput(ownThread, otherThread, false);
            }

            Activate();
            FocusEditor();
        }

        // ---------- settings flyout ----------

        public void OpenSettings()
        {
            LoadSettingsIntoUi();
            SettingsScroll.MaxHeight = Math.Max(200, ActualHeight - 120);
            SettingsLayer.Visibility = Visibility.Visible;
            SettingsButton.Foreground = (Brush)FindResource("AccentBrush");
        }

        public void CloseSettings()
        {
            if (!IsSettingsOpen) return;
            SettingsLayer.Visibility = Visibility.Collapsed;
            SettingsButton.ClearValue(ForegroundProperty);
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            if (IsSettingsOpen) CloseSettings();
            else OpenSettings();
        }

        private void OnHideClick(object sender, RoutedEventArgs e) => App.Current.Controller?.HidePanel();

        private void OnExitClick(object sender, RoutedEventArgs e) => App.Current.ExitApp();

        private void OnScrimClick(object sender, MouseButtonEventArgs e) => CloseSettings();

        private void OnCardClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

        private void LoadSettingsIntoUi()
        {
            var settings = App.Current.Settings;

            DelayNow.IsChecked = settings.HideDelayMs == 0;
            Delay300.IsChecked = settings.HideDelayMs == 300;
            Delay1000.IsChecked = settings.HideDelayMs == 1000;
            Delay3000.IsChecked = settings.HideDelayMs >= 2000;

            // The pin lives for one session only, so its state comes from the controller rather
            // than from the settings file.
            PinButton.IsChecked = App.Current.Controller?.IsPinned == true;
            PinButton.ToolTip = Loc.T(PinButton.IsChecked == true ? "panel.pin.on" : "panel.pin");

            var instant = string.Equals(settings.Transition, "instant", StringComparison.OrdinalIgnoreCase);
            TransitionSlide.IsChecked = !instant;
            TransitionInstant.IsChecked = instant;

            ThemeSystem.IsChecked = settings.Theme == "system";
            ThemeLight.IsChecked = settings.Theme == "light";
            ThemeDark.IsChecked = settings.Theme == "dark";

            HotkeyOn.IsChecked = settings.HotkeyEnabled;
            HotkeyOff.IsChecked = !settings.HotkeyEnabled;
            HotkeyLabel.Text = Loc.F("panel.hotkey", settings.Hotkey);

            RememberSearchOn.IsChecked = settings.RememberSearch;
            RememberSearchOff.IsChecked = !settings.RememberSearch;

            AutoStartOn.IsChecked = settings.AutoStart;
            AutoStartOff.IsChecked = !settings.AutoStart;

            LoadMonitorChoices();
            LoadLanguageChoices();
            if (!_fontChoices.Contains(settings.FontFamily)) _fontChoices.Add(settings.FontFamily);
            FontBox.SelectedItem = settings.FontFamily;
            UpdateTypographyUi();

            if (Environment.GetEnvironmentVariable("HIDEYNOTES_DEV_TAB") == "behavior")
            {
                // Dev aid: opens the settings on the behavior page for screenshots and checks.
                TabBehavior.IsChecked = true;
                TextPanel.Visibility = Visibility.Collapsed;
                BehaviorPanel.Visibility = Visibility.Visible;
            }
            else if (TabText.IsChecked != true && TabBehavior.IsChecked != true)
            {
                TabText.IsChecked = true;
            }

            TrayHint.Text = App.Current.TrayAvailable
                ? Loc.T("panel.trayHint")
                : Loc.T("panel.trayUnavailable");
        }

        private void OnDelayChecked(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not RadioButton { Tag: string tag }) return;
            if (!int.TryParse(tag, out var delay)) return;

            App.Current.Settings.HideDelayMs = delay;
            SettingsStore.Save(App.Current.Settings);
        }

        /// <summary>
        /// Pins the panel for this session: it keeps showing when it loses focus. The header button
        /// only changes its fill, the decision itself is made by the controller.
        /// </summary>
        private void OnPinToggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            App.Current.Controller?.SetPinned(PinButton.IsChecked == true);
            PinButton.ToolTip = Loc.T(PinButton.IsChecked == true ? "panel.pin.on" : "panel.pin");
        }

        private void OnTransitionChecked(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not RadioButton { Tag: string tag }) return;

            App.Current.Settings.Transition = tag == "instant" ? "instant" : "slide";
            SettingsStore.Save(App.Current.Settings);
        }

        private void OnThemeChecked(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not RadioButton { Tag: string tag }) return;

            App.Current.Settings.Theme = ThemeService.Normalize(tag);
            App.Current.Theme.SetMode(tag);
            SettingsStore.Save(App.Current.Settings);
        }

        private void OnHotkeyChecked(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not RadioButton { Tag: string tag }) return;

            App.Current.Settings.HotkeyEnabled = tag == "true";
            App.Current.Controller?.ApplyHotkey();
            SettingsStore.Save(App.Current.Settings);
        }

        // ---------- monitors ----------

        /// <summary>One entry of the monitor picker.</summary>
        private sealed class MonitorChoice
        {
            public string Device { get; init; } = "";
            public string Label { get; init; } = "";

            // The combo's selection box can fall back to plain text rendering.
            public override string ToString() => Label;
        }

        private bool _loadingMonitors;

        private void LoadMonitorChoices()
        {
            var settings = App.Current.Settings;
            var choices = new List<MonitorChoice>
            {
                new() { Device = "", Label = Loc.T("panel.monitor.auto") }
            };

            var screens = Forms.Screen.AllScreens;
            for (var i = 0; i < screens.Length; i++)
            {
                var screen = screens[i];
                var bounds = screen.Bounds;

                // "\\.\DISPLAY2" -> "Экран 2": the number Windows shows in the display settings.
                var number = i + 1;
                var digits = new string(screen.DeviceName.Where(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var parsed) && parsed > 0) number = parsed;

                choices.Add(new MonitorChoice
                {
                    Device = screen.DeviceName,
                    Label = Loc.F("panel.monitor.screen", number, bounds.Width, bounds.Height) +
                        (screen.Primary ? Loc.T("panel.monitor.primary") : "")
                });
            }

            _loadingMonitors = true;
            MonitorBox.ItemsSource = choices;
            MonitorBox.SelectedItem = choices.FirstOrDefault(c => string.Equals(c.Device, settings.MonitorDevice, StringComparison.OrdinalIgnoreCase))
                                      ?? choices[0];
            _loadingMonitors = false;

            MonitorHint.Text = screens.Length > 1
                ? Loc.T("panel.monitor.hintMulti")
                : Loc.T("panel.monitor.hintSingle");
        }

        private void OnMonitorChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _loadingMonitors) return;
            if (MonitorBox.SelectedItem is not MonitorChoice choice) return;

            var settings = App.Current.Settings;
            if (string.Equals(settings.MonitorDevice, choice.Device, StringComparison.OrdinalIgnoreCase)) return;

            settings.MonitorDevice = choice.Device;
            SettingsStore.Save(settings);
            App.Current.Controller?.RefreshLayout();
        }

        private void OnAutoStartChecked(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not RadioButton { Tag: string tag }) return;

            var settings = App.Current.Settings;
            settings.AutoStart = tag == "true";

            if (settings.AutoStart)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) AutoStart.Apply(exe);
            }
            else
            {
                AutoStart.Remove();
            }

            SettingsStore.Save(settings);
        }

        private void OnRememberSearchChecked(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not RadioButton { Tag: string tag }) return;

            var settings = App.Current.Settings;
            settings.RememberSearch = tag == "true";
            if (!settings.RememberSearch) settings.LastSearch = string.Empty;
            SettingsStore.Save(settings);
        }

        // ---------- typography settings ----------

        private void OnSettingsTabChecked(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not RadioButton { Tag: string tag }) return;

            var isText = tag != "behavior";
            TextPanel.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
            BehaviorPanel.Visibility = isText ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateTypographyUi()
        {
            var settings = App.Current.Settings;
            var culture = Loc.Instance.Culture;
            FontSizeValue.Text = settings.FontSize.ToString("0.#", culture);
            LineHeightValue.Text = settings.LineHeight.ToString("0.0", culture);
            PaddingValue.Text = settings.EditorPadding.ToString("0", culture);
            BuildSwatches();
            SyncHexBox();
        }

        // ---------- custom text colour ----------

        private string CurrentTextHex => App.Current.Theme.IsDark
            ? App.Current.Settings.TextColorDark
            : App.Current.Settings.TextColorLight;

        private void SyncHexBox()
        {
            HexBox.Text = CurrentTextHex ?? string.Empty;
            HexPlaceholder.Visibility = HexBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            HexHint.Visibility = Visibility.Collapsed;
        }

        private void SetTextHex(string hex)
        {
            var settings = App.Current.Settings;
            if (App.Current.Theme.IsDark) settings.TextColorDark = hex;
            else settings.TextColorLight = hex;

            ApplyTypography();
            BuildSwatches();
            SyncHexBox();
            SettingsStore.Save(settings);
        }

        private void OnHexKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    CommitHex();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    SyncHexBox();
                    e.Handled = true;
                    break;
            }
        }

        private void OnHexCommit(object sender, RoutedEventArgs e) => CommitHex();

        /// <summary>Accepts #RGB, #ARGB, #RRGGBB and #AARRGGBB; an empty box means "as in the theme".</summary>
        private void CommitHex()
        {
            var text = (HexBox.Text ?? string.Empty).Trim();

            if (text.Length == 0)
            {
                if (!string.IsNullOrEmpty(CurrentTextHex)) SetTextHex(string.Empty);
                return;
            }

            if (!text.StartsWith('#')) text = "#" + text;

            if (!TryParseHex(text, out var color))
            {
                HexHint.Text = Loc.T("panel.textColor.invalid");
                HexHint.Visibility = Visibility.Visible;
                return;
            }

            SetTextHex(FormatHex(color));
        }

        private static bool TryParseHex(string text, out Color color)
        {
            color = Colors.Black;

            try
            {
                if (ColorConverter.ConvertFromString(text) is not Color parsed) return false;
                color = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string FormatHex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

        private void OnPickColorClick(object sender, RoutedEventArgs e)
        {
            var current = TryParseHex(CurrentTextHex ?? string.Empty, out var stored)
                ? stored
                : App.Current.Theme.TextColor;

            using var dialog = new Forms.ColorDialog
            {
                FullOpen = true,
                AnyColor = true,
                Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B)
            };

            if (dialog.ShowDialog() != Forms.DialogResult.OK) return;

            var picked = dialog.Color;
            SetTextHex(FormatHex(Color.FromArgb(255, picked.R, picked.G, picked.B)));
        }

        private void OnFontChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (FontBox.SelectedItem is not string family) return;

            App.Current.Settings.FontFamily = family;
            ApplyTypography();
            SettingsStore.Save(App.Current.Settings);
        }

        private void OnFontSizeMinus(object sender, RoutedEventArgs e) => ChangeFontSize(-1);

        private void OnFontSizePlus(object sender, RoutedEventArgs e) => ChangeFontSize(1);

        private void ChangeFontSize(double delta)
        {
            var settings = App.Current.Settings;
            settings.FontSize = Math.Clamp(Math.Round(settings.FontSize + delta, 1), 10, 40);
            ApplyTypography();
            UpdateTypographyUi();
            SettingsStore.Save(settings);
        }

        private void OnLineHeightMinus(object sender, RoutedEventArgs e) => ChangeLineHeight(-0.1);

        private void OnLineHeightPlus(object sender, RoutedEventArgs e) => ChangeLineHeight(0.1);

        private void ChangeLineHeight(double delta)
        {
            var settings = App.Current.Settings;
            settings.LineHeight = Math.Clamp(Math.Round(settings.LineHeight + delta, 2), 0.8, 3.0);
            ApplyTypography();
            UpdateTypographyUi();
            SettingsStore.Save(settings);
        }

        private void OnPaddingMinus(object sender, RoutedEventArgs e) => ChangePadding(-4);

        private void OnPaddingPlus(object sender, RoutedEventArgs e) => ChangePadding(4);

        private void ChangePadding(double delta)
        {
            var settings = App.Current.Settings;
            settings.EditorPadding = Math.Clamp(settings.EditorPadding + delta, 0, 64);
            ApplyTypography();
            UpdateTypographyUi();
            SettingsStore.Save(settings);
        }

        private void BuildSwatches()
        {
            SwatchPanel.Children.Clear();

            var settings = App.Current.Settings;
            var current = App.Current.Theme.IsDark ? settings.TextColorDark : settings.TextColorLight;

            var accent = (Brush)FindResource("AccentBrush");
            var border = (Brush)FindResource("SegmentBorderBrush");

            foreach (var (hex, nameKey) in Swatches)
            {
                var isSelected = string.Equals(current ?? string.Empty, hex, StringComparison.OrdinalIgnoreCase);

                var swatch = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(13),
                    BorderThickness = new Thickness(isSelected ? 2 : 1),
                    BorderBrush = isSelected ? accent : border,
                    Background = string.IsNullOrEmpty(hex) ? Brushes.Transparent : ParseBrush(hex),
                    Margin = new Thickness(0, 0, 6, 6)
                };

                if (string.IsNullOrEmpty(hex))
                {
                    swatch.Child = new TextBlock
                    {
                        Text = "А",
                        FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }

                var button = new Button
                {
                    Style = (Style)FindResource("SwatchButton"),
                    Content = swatch,
                    Tag = hex
                };
                button.ToolTip = Loc.T(nameKey);
                button.Click += OnSwatchClick;
                SwatchPanel.Children.Add(button);
            }
        }

        private void OnSwatchClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string hex }) return;

            var settings = App.Current.Settings;
            if (App.Current.Theme.IsDark) settings.TextColorDark = hex;
            else settings.TextColorLight = hex;

            ApplyTypography();
            BuildSwatches();
            SyncHexBox();
            SettingsStore.Save(settings);
        }

        private static Brush ParseBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        // ---------- export ----------

        private void OnSaveClick(object sender, RoutedEventArgs e) => SaveActiveNoteAs();

        private void SaveActiveNoteAs()
        {
            var note = Store.Active;
            if (note is null) return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Loc.T("app.saveDialogTitle"),
                Filter = Loc.T("app.saveDialogFilter"),
                DefaultExt = ".txt",
                AddExtension = true,
                FileName = MakeFileName(note.Title)
            };

            if (dialog.ShowDialog(this) != true) return;

            try
            {
                File.WriteAllText(dialog.FileName, GetText(), new System.Text.UTF8Encoding(true));
                note.SavedPath = dialog.FileName;
                Store.MarkSaved(note);

                SetSaveState("panel.status.savedTo", Path.GetFileName(dialog.FileName));
                UpdateStatus();
            }
            catch (Exception ex)
            {
                Log.Error("save as failed", ex);
                MessageBox.Show(this,
                    Loc.T("app.saveFailed") + Environment.NewLine + ex.Message,
                    "HideyNotes", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string MakeFileName(string title)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string((title ?? string.Empty).Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
            if (safe.Length == 0) safe = Loc.T("note.default");
            if (safe.Length > 60) safe = safe[..60];
            return safe + ".txt";
        }
    }
}
