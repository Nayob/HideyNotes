using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HideyNotes.Models
{
    public sealed class Note : INotifyPropertyChanged
    {
        private string _title = "Заметка";
        private string _origin = OriginNone;
        private bool _isActive;
        private bool _isEditing;
        private bool _isTabVisible = true;
        private int _matchCount;

        /// <summary>A fresh note: closing it sends it to the trash.</summary>
        public const string OriginNone = "none";

        /// <summary>Opened from the library: closing it puts it back into the library.</summary>
        public const string OriginLibrary = "library";

        /// <summary>Restored from the trash: closing it puts it back into the trash.</summary>
        public const string OriginTrash = "trash";

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public int Order { get; set; }
        public DateTime Created { get; set; } = DateTime.Now;
        public DateTime Modified { get; set; } = DateTime.Now;
        public bool IsOpen { get; set; } = true;

        /// <summary>Built-in help tab: never filed anywhere, recreated on demand.</summary>
        public bool IsHelp { get; set; }
        public string? SavedPath { get; set; }
        public DateTime? SavedAt { get; set; }

        /// <summary>When the note was put into the library; null for notes that never were.</summary>
        public DateTime? LibraryAt { get; set; }

        /// <summary>Where the note came from: none, library or trash. Decides what closing does.</summary>
        public string Origin
        {
            get => _origin;
            set
            {
                if (_origin == value) return;
                _origin = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsLibrary));
            }
        }

        /// <summary>The tab shows a small book icon, and closing returns the note to the library.</summary>
        public bool IsLibrary => _origin == OriginLibrary;

        /// <summary>Note body, kept in memory for open tabs.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>First line shown in the library and trash lists.</summary>
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
