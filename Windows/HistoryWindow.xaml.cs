using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HideyNotes.Localization;
using HideyNotes.Models;
using HideyNotes.Services;

namespace HideyNotes.Windows
{
    public partial class HistoryWindow : Window
    {
        private NotesStore Store => App.Current.Notes;

        private bool _everActivated;
        private bool _closing;
        private bool _keepOpen;
        private bool _loading = true;
        private bool _trashMode;

        public HistoryWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Closed += OnClosed;
            Closing += (_, _) => _closing = true;
            Activated += (_, _) => _everActivated = true;
            Deactivated += OnDeactivated;

            ModeHistory.IsChecked = true;
            _loading = false;

            UpdateMode();
            Refresh();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Store.HistoryChanged += Refresh;
            Store.TrashChanged += Refresh;
            Store.PurgeHistory();
            Refresh();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            Log.Info($"history: closed (fg={Native.Describe(Native.GetForegroundWindow())})");
            Store.HistoryChanged -= Refresh;
            Store.TrashChanged -= Refresh;
        }

        /// <summary>Clicking anywhere outside closes the dialog, just like the panel hides on blur.</summary>
        private void OnDeactivated(object? sender, EventArgs e)
        {
            if (!_everActivated || _closing || !IsLoaded) return;

            // Closing right here would destroy the window while Windows is still handing the
            // focus to the next window (the panel), and that window would be left without it.
            // Let the click finish first: the panel is activated by then.
            Dispatcher.BeginInvoke(new Action(CloseFromDeactivate), DispatcherPriority.Background);
        }

        private void CloseFromDeactivate()
        {
            if (_keepOpen)
            {
                _keepOpen = false;
                return;
            }

            if (_closing || !IsLoaded) return;
            Close();
        }

        /// <summary>Called when the dialog is asked to come back before the deferred close ran.</summary>
        public void KeepOpen() => _keepOpen = true;

        private void OnModeChecked(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not RadioButton { Tag: string tag }) return;

            _trashMode = tag == "trash";
            UpdateMode();
            Refresh();
        }

        private void UpdateMode()
        {
            HistoryList.Visibility = _trashMode ? Visibility.Collapsed : Visibility.Visible;
            TrashList.Visibility = _trashMode ? Visibility.Visible : Visibility.Collapsed;
            HistoryActions.Visibility = _trashMode ? Visibility.Collapsed : Visibility.Visible;
            TrashActions.Visibility = _trashMode ? Visibility.Visible : Visibility.Collapsed;

            HintText.Text = _trashMode ? Loc.T("hist.hintTrash") : Loc.T("hist.hintClosed");
            ClearButton.Content = _trashMode ? Loc.T("hist.clearTrash") : Loc.T("hist.clearHistory");
        }

        /// <summary>Re-renders the texts that are produced in code after the language changed.</summary>
        public void ApplyLanguage()
        {
            UpdateMode();
            Refresh();
        }

        private void Refresh()
        {
            var query = Query;

            if (_trashMode)
            {
                var all = Store.ReadTrash();
                var items = Filter(all, t => t.Title, t => t.Text);
                TrashList.ItemsSource = items;
                EmptyText.Text = query.Length == 0 ? Loc.T("hist.emptyTrash") : Loc.T("hist.emptyNotFound");
                EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                SearchInfo.Text = query.Length == 0 ? string.Empty : Loc.F("hist.found", items.Count, all.Count);
            }
            else
            {
                var all = Store.History.ToList();
                var items = Filter(all, n => n.Title, n => n.Text);
                HistoryList.ItemsSource = items;
                EmptyText.Text = query.Length == 0 ? Loc.T("hist.emptyClosed") : Loc.T("hist.emptyNotFound");
                EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                SearchInfo.Text = query.Length == 0 ? string.Empty : Loc.F("hist.found", items.Count, all.Count);
            }

            UpdateButtons();
        }

        private string Query => SearchBox.Text?.Trim() ?? string.Empty;

        private bool Searching => Query.Length > 0;

        /// <summary>Matches the title or the text of an entry, case-insensitive.</summary>
        private List<T> Filter<T>(List<T> source, Func<T, string> title, Func<T, string> text)
        {
            var query = Query;
            if (query.Length == 0) return source;

            return source
                .Where(item => title(item).Contains(query, StringComparison.OrdinalIgnoreCase) ||
                               text(item).Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // ---------- search ----------

        public void OpenSearchField()
        {
            if (SearchBar.Visibility == Visibility.Visible) return;

            SearchBar.Visibility = Visibility.Visible;
            SearchButton.Foreground = (Brush)FindResource("AccentBrush");
            Dispatcher.BeginInvoke(new Action(() => SearchBox.Focus()), DispatcherPriority.Input);
        }

        public void CloseSearchField()
        {
            if (SearchBar.Visibility != Visibility.Visible) return;

            SearchBar.Visibility = Visibility.Collapsed;
            SearchButton.ClearValue(ForegroundProperty);
            SearchInfo.Text = string.Empty;
            SearchBox.Text = string.Empty;
            Refresh();
            HistoryList.Focus();
        }

        private void ToggleSearchField()
        {
            if (SearchBar.Visibility == Visibility.Visible) CloseSearchField();
            else OpenSearchField();
        }

        private void OnSearchClick(object sender, RoutedEventArgs e) => ToggleSearchField();

        private void OnClearSearchClick(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = string.Empty;
            SearchBox.Focus();
        }

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (!IsLoaded) return;
            Refresh();
        }

        private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseSearchField();
                e.Handled = true;
            }
        }

        private void UpdateButtons()
        {
            if (_trashMode)
            {
                var has = TrashList.SelectedItem is TrashItem;
                TrashRestoreButton.IsEnabled = has;
                TrashDeleteButton.IsEnabled = has;
                TrashRestoreButton.Opacity = has ? 1 : 0.45;
                TrashDeleteButton.Opacity = has ? 1 : 0.45;

                // Clearing is a whole-list action: do not offer it while the list is filtered.
                var canClear = TrashList.Items.Count > 0 && !Searching;
                ClearButton.IsEnabled = canClear;
                ClearButton.Opacity = canClear ? 1 : 0.45;
                ClearButton.ToolTip = Searching ? Loc.T("hist.clearDisabled") : null;
                return;
            }

            var hasHistory = HistoryList.SelectedItem is Note;
            RestoreButton.IsEnabled = hasHistory;
            DeleteButton.IsEnabled = hasHistory;
            RestoreButton.Opacity = hasHistory ? 1 : 0.45;
            DeleteButton.Opacity = hasHistory ? 1 : 0.45;

            var canClearHistory = HistoryList.Items.Count > 0 && !Searching;
            ClearButton.IsEnabled = canClearHistory;
            ClearButton.Opacity = canClearHistory ? 1 : 0.45;
            ClearButton.ToolTip = Searching ? Loc.T("hist.clearDisabled") : null;
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_trashMode && TrashList.SelectedItem is not null) HistoryList.SelectedItem = null;
            if (!_trashMode && HistoryList.SelectedItem is not null) TrashList.SelectedItem = null;

            UpdateButtons();
        }

        private void OnRestoreClick(object sender, RoutedEventArgs e)
        {
            if (HistoryList.SelectedItem is not Note note) return;
            Store.Restore(note);
            UpdateButtons();
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (HistoryList.SelectedItem is not Note note) return;
            Store.DeleteToTrash(note);
            UpdateButtons();
        }

        private void OnTrashRestoreClick(object sender, RoutedEventArgs e)
        {
            if (TrashList.SelectedItem is not TrashItem item) return;
            Store.RestoreFromTrash(item);
            UpdateButtons();
        }

        private void OnTrashDeleteClick(object sender, RoutedEventArgs e)
        {
            if (TrashList.SelectedItem is not TrashItem item) return;
            Store.DeleteTrashItem(item);
            UpdateButtons();
        }

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            if (_trashMode) Store.ClearTrash();
            else Store.ClearHistory();

            UpdateButtons();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

        private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (ctrl && e.Key == Key.F)
            {
                ToggleSearchField();
                e.Handled = true;
                return;
            }

            if (ctrl && e.Key == Key.H)
            {
                Close();
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.Escape:
                    // Esc closes the search first, the dialog itself on the second press.
                    if (SearchBar.Visibility == Visibility.Visible)
                    {
                        CloseSearchField();
                    }
                    else
                    {
                        Close();
                    }

                    e.Handled = true;
                    break;

                case Key.Delete:
                    if (_trashMode && TrashList.SelectedItem is TrashItem trashItem)
                    {
                        Store.DeleteTrashItem(trashItem);
                        e.Handled = true;
                    }
                    else if (!_trashMode && HistoryList.SelectedItem is Note note)
                    {
                        Store.DeleteToTrash(note);
                        e.Handled = true;
                    }
                    break;

                case Key.Enter:
                    if (_trashMode && TrashList.SelectedItem is TrashItem restoreTrash)
                    {
                        Store.RestoreFromTrash(restoreTrash);
                        e.Handled = true;
                    }
                    else if (!_trashMode && HistoryList.SelectedItem is Note restoreNote)
                    {
                        Store.Restore(restoreNote);
                        e.Handled = true;
                    }
                    break;
            }
        }
    }
}
