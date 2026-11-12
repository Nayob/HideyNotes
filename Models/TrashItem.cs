using System;
using HideyNotes.Localization;

namespace HideyNotes.Models
{
    /// <summary>A note deleted from history; kept in the trash for 30 days.</summary>
    public sealed class TrashItem
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public DateTime DeletedAt { get; set; }
        public string FilePath { get; set; } = string.Empty;

        public string DeletedLabel => DeletedAt.ToString("dd.MM.yyyy HH:mm");

        public string Preview
        {
            get
            {
                var flat = Text.Replace("\r", " ").Replace("\n", " ").Trim();
                return flat.Length > 120 ? flat[..120] + "…" : flat;
            }
        }

        public int DaysLeft => Math.Max(0, 30 - (int)(DateTime.Now - DeletedAt).TotalDays);

        public string ExpiryLabel => Loc.F("note.expiry", DaysLeft);

        public bool IsExpiringSoon => DaysLeft <= 3;
    }
}
