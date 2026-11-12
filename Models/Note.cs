using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using HideyNotes.Localization;

namespace HideyNotes.Models
{
    public sealed class Note : INotifyPropertyChanged
    {
        private string _title = "Заметка";
        private bool _isActive;
        private bool _isEditing;
        private bool _isTabVisible = true;
        private int _matchCount;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public int Order { get; set; }
        public DateTime Created { get; set; } = DateTime.Now;
        public DateTime Modified { get; set; } = DateTime.Now;
        public bool IsOpen { get; set; } = true;

        /// <summary>Built-in help tab: never goes to history, recreated on demand.</summary>
        public bool IsHelp { get; set; }
        public string? SavedPath { get; set; }
        public DateTime? SavedAt { get; set; }

        /// <summary>When the tab was moved to history; null for open notes.</summary>
        public DateTime? HistoryAt { get; set; }

        public DateTime HistorySince => HistoryAt ?? Modified;

        public bool IsExpiringSoon => !IsOpen && DateTime.Now >= HistorySince.AddDays(27);

        public int ExpiryDaysLeft => Math.Max(0, 30 - (int)(DateTime.Now - HistorySince).TotalDays);

        public string ExpiryLabel => IsOpen ? string.Empty : Loc.F("note.expiry", ExpiryDaysLeft);

        /// <summary>Note body, kept in memory for open tabs.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>First line shown in the history window.</summary>
        public string Preview { get; set; } = string.Empty;

        public string Title
        {
            get => _title;
            set
            {
                if (_title == value) return;
                _title = value;
                OnPropertyChanged();
            }
        }

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value) return;
                _isActive = value;
                OnPropertyChanged();
            }
        }

        public bool IsEditing
        {
            get => _isEditing;
            set
            {
                if (_isEditing == value) return;
                _isEditing = value;
                OnPropertyChanged();
            }
        }

        public string ModifiedLabel => Modified.ToString("dd.MM.yyyy HH:mm");

        /// <summary>Tab is visible in the strip (search across notes may hide it).</summary>
        public bool IsTabVisible
        {
            get => _isTabVisible;
            set
            {
                if (_isTabVisible == value) return;
                _isTabVisible = value;
                OnPropertyChanged();
            }
        }

        /// <summary>How many search matches this note has (shown as a tab badge).</summary>
        public int MatchCount
        {
            get => _matchCount;
            set
            {
                if (_matchCount == value) return;
                _matchCount = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
