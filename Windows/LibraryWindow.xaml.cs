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
    /// <summary>
    /// Library and trash in one window: the library keeps notes for good, the trash keeps
    /// everything removed from the tabs for 30 days. Deleting for good is only possible here.
    /// </summary>
    public partial class LibraryWindow : Window
    {
        private NotesStore Store => App.Current.Notes;

        private bool _everActivated;
        private bool _closing;
        private bool _keepOpen;
        private bool _loading = true;
        private bool _trashMode;
        private bool _modal;

        public LibraryWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Closed += OnClosed;
            Closing += (_, _) => _closing = true;
            Activated += (_, _) => _everActivated = true;
            Deactivated += OnDeactivated;

            ModeLibrary.IsChecked = true;
            _loading = false;

            UpdateMode();
            Refresh();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Store.LibraryChanged += Refresh;
            Store.TrashChanged += Refresh;
            Refresh();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            Log.Info($"library: closed (fg={Native.Describe(Native.GetForegroundWindow())})");
            Store.LibraryChanged -= Refresh;
            Store.TrashChanged -= Refresh;
        }

        /// <summary>Clicking anywhere outside closes the dialog, just like the panel hides on blur.</summary>
        private void OnDeactivated(object? sender, EventArgs e)
        {
            if (!_everActivated || _closing || !IsLoaded) return;

            // A confirmation box deactivates us too: closing the window under it would be a bug.
            if (_modal) return;

            // Closing right here would destroy the window while Windows is still handing the
            // focus to the next window (the panel), and that window would be left without it.
            // Let the click finish first: the panel is activated by then.
            Dispatcher.BeginInvoke(new Action(CloseFromDeactivate), DispatcherPriority.Background);
        }

        private void CloseFromDeactivate()
        {
            if (_modal) return;

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
            LibraryList.Visibility = _trashMode ? Visibility.Collapsed : Visibility.Visible;
            TrashList.Visibility = _trashMode ? Visibility.Visible : Visibility.Collapsed;
            LibraryActions.Visibility = _trashMode ? Visibility.Collapsed : Visibility.Visible;
            TrashActions.Visibility = _trashMode ? Visibility.Visible : Visibility.Collapsed;

            // The library is not something you clear: only the trash has that button.
            ClearButton.Visibility = _trashMode ? Visibility.Visible : Visibility.Collapsed;

            HintText.Text = _trashMode ? Loc.T("hist.hintTrash") : Loc.T("hist.hintLibrary");
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
                var all = Store.Library.ToList();
                var items = Filter(all, n => n.Title, n => n.Text);
                LibraryList.ItemsSource = items;
                EmptyText.Text = query.Length == 0 ? Loc.T("hist.emptyLibrary") : Loc.T("hist.emptyNotFound");
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
            LibraryList.Focus();
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

            var hasLibrary = LibraryList.SelectedItem is Note;
            OpenButton.IsEnabled = hasLibrary;
            LibraryTrashButton.IsEnabled = hasLibrary;
            OpenButton.Opacity = hasLibrary ? 1 : 0.45;
            LibraryTrashButton.Opacity = hasLibrary ? 1 : 0.45;
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_trashMode && TrashList.SelectedItem is not null) LibraryList.SelectedItem = null;
            if (!_trashMode && LibraryList.SelectedItem is not null) TrashList.SelectedItem = null;

            UpdateButtons();
        }

        private void OnOpenClick(object sender, RoutedEventArgs e)
        {
            if (LibraryList.SelectedItem is not Note note) return;
            Store.Restore(note);
            UpdateButtons();
        }

        private void OnLibraryTrashClick(object sender, RoutedEventArgs e)
        {
            if (LibraryList.SelectedItem is not Note note) return;
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
            if (!Confirm(Loc.T("hist.confirmDelete"), Loc.T("hist.confirmDeleteTitle"))) return;

            Store.DeleteTrashItem(item);
            UpdateButtons();
        }

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            if (!_trashMode) return;
            if (!Confirm(Loc.T("hist.confirmClear"), Loc.T("hist.confirmDeleteTitle"))) return;

            Store.ClearTrash();
            UpdateButtons();
        }

        /// <summary>Only destruction asks for confirmation; everything else is undoable.</summary>
        private bool Confirm(string message, string title)
        {
            _modal = true;
            try
            {
                return MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No) == MessageBoxResult.Yes;
            }
            finally
            {
                _modal = false;
            }
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

            if (e.Key == Key.Escape)
            {
                if (SearchBar.Visibility == Visibility.Visible) CloseSearchField();
                else Close();

                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.Delete:
                    if (_trashMode && TrashList.SelectedItem is TrashItem trashItem)
                    {
                        if (!Confirm(Loc.T("hist.confirmDelete"), Loc.T("hist.confirmDeleteTitle"))) return;
                        Store.DeleteTrashItem(trashItem);
                        e.Handled = true;
                    }
                    else if (!_trashMode && LibraryList.SelectedItem is Note note)
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
                    else if (!_trashMode && LibraryList.SelectedItem is Note restoreNote)
                    {
                        Store.Restore(restoreNote);
                        e.Handled = true;
                    }
                    break;
            }
        }
    }
}
