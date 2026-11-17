using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;
using HideyNotes.Localization;
using HideyNotes.Models;

namespace HideyNotes.Services
{
    /// <summary>
    /// Notes storage: one .txt per note plus a JSON index for metadata and tab order.
    /// Everything is written atomically; a broken index is rebuilt from the notes themselves.
    ///
    /// Life cycle: tabs are the working set, the library keeps notes for good, the trash keeps
    /// everything removed for 30 days. A note remembers where it came from, so closing it puts it
    /// back where it belongs instead of asking the user.
    /// </summary>
    public sealed class NotesStore
    {
        private const int PreviewLength = 140;
        private const int TrashRetentionDays = 30;
        private const int IndexVersion = 2;

        private readonly DispatcherTimer _saveTimer;
        private string? _pendingId;
        private string? _pendingText;

        public ObservableCollection<Note> Tabs { get; } = new();
        public List<Note> Library { get; } = new();
        public Note? Active { get; private set; }

        public event Action? TabsChanged;
        public event Action? ActiveChanged;
        public event Action<DateTime>? Saved;
        public event Action? LibraryChanged;
        public event Action? TrashChanged;

        public NotesStore(Dispatcher dispatcher)
        {
            _saveTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _saveTimer.Tick += (_, _) => Flush();
        }

        // ---------- load / save ----------

        public void Load()
        {
            PurgeTrash();

            var index = ReadIndex();
            var metas = index?.Notes ?? new List<NoteMeta>();
            var migrated = MigrateHistoryToTrash(metas);

            var known = new HashSet<string>(metas.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);

            // Adopt orphan files (index lost or a note written by hand): they become open tabs.
            foreach (var file in Directory.EnumerateFiles(AppPaths.NotesDir, "*.txt"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (known.Contains(id)) continue;

                metas.Add(new NoteMeta
                {
                    Id = id,
                    Title = TitleFromFile(file) ?? Loc.T("note.default"),
                    Order = metas.Count,
                    Created = File.GetLastWriteTime(file),
                    Modified = File.GetLastWriteTime(file),
                    Open = true
                });
            }

            foreach (var meta in metas.OrderBy(m => m.Order))
            {
                // A hand-written or broken index may miss the dates: fall back to the file time.
                if (meta.Modified == default)
                {
                    try
                    {
                        var path = NotePath(meta.Id);
                        if (File.Exists(path)) meta.Modified = File.GetLastWriteTime(path);
                    }
                    catch { }
                }

                if (meta.Created == default) meta.Created = meta.Modified;

                var note = new Note
                {
                    Id = meta.Id,
                    Title = string.IsNullOrWhiteSpace(meta.Title) ? Loc.T("note.default") : meta.Title,
                    Order = meta.Order,
                    Created = meta.Created,
                    Modified = meta.Modified,
                    IsOpen = meta.Open,
                    IsHelp = meta.IsHelp,
                    SavedPath = meta.SavedPath,
                    SavedAt = meta.SavedAt,
                    LibraryAt = meta.LibraryAt,
                    Origin = string.IsNullOrWhiteSpace(meta.Origin) ? Note.OriginNone : meta.Origin,
                    Text = ReadText(meta.Id)
                };

                if (note.IsOpen)
                {
                    Tabs.Add(note);
                }
                else
                {
                    note.Preview = MakePreview(note.Text);
                    Library.Insert(0, note);
                }
            }

            SortLibrary();

            if (Tabs.Count == 0) CreateNote(save: false);
            else
            {
                var activeId = index?.ActiveId;
                Active = Tabs.FirstOrDefault(n => n.Id == activeId) ?? Tabs[0];
                Active.IsActive = true;
            }

            if (migrated) LibraryChanged?.Invoke();
            SaveIndex();
        }

        /// <summary>
        /// Older versions kept closed notes in a history list that lived 30 days. That list is gone:
        /// such notes move to the trash with their original dates, so nothing is lost.
        /// </summary>
        private bool MigrateHistoryToTrash(List<NoteMeta> metas)
        {
            var changed = false;

            foreach (var meta in metas.Where(m => !m.Open && !m.Library).ToList())
            {
                metas.Remove(meta);
                changed = true;

                try
                {
                    var text = ReadText(meta.Id);
                    WriteTrashEntry(meta.Id, meta.Title, text, meta.HistoryAt ?? meta.Modified);
                    TryDelete(NotePath(meta.Id));
                }
                catch (Exception ex)
                {
                    Log.Error($"history migration failed: {meta.Id}", ex);
                }
            }

            return changed;
        }

        public void Flush()
        {
            _saveTimer.Stop();
            if (_pendingId is null || _pendingText is null) return;

            var id = _pendingId;
            var text = _pendingText;
            _pendingId = null;
            _pendingText = null;

            try
            {
                AppPaths.WriteAtomic(NotePath(id), text);
                var note = FindById(id);
                if (note is not null)
                {
                    note.Modified = DateTime.Now;
                    note.Text = text;
                }

                SaveIndex();
                Saved?.Invoke(DateTime.Now);
            }
            catch (Exception ex)
            {
                Log.Error("note save failed", ex);
            }
        }

        public void QueueText(Note note, string text)
        {
            note.Text = text;
            _pendingId = note.Id;
            _pendingText = text;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        // ---------- tabs ----------

        public Note CreateNote(bool save = true, string? title = null, string? text = null,
            bool isHelp = false, string origin = Note.OriginNone)
        {
            Flush();

            var note = new Note
            {
                Title = string.IsNullOrWhiteSpace(title) ? NextTitle() : title.Trim(),
                Order = Tabs.Count,
                IsOpen = true,
                IsHelp = isHelp,
                Origin = origin,
                Text = text ?? string.Empty
            };

            Tabs.Add(note);
            SetActiveInternal(note);

            if (save)
            {
                if (!string.IsNullOrEmpty(note.Text))
                {
                    try
                    {
                        AppPaths.WriteAtomic(NotePath(note.Id), note.Text);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("note create write failed", ex);
                    }
                }

                Persist();
                TabsChanged?.Invoke();
                ActiveChanged?.Invoke();
            }

            return note;
        }

        public Note? FindByTitle(string title) =>
            Tabs.Concat(Library).FirstOrDefault(n => string.Equals(n.Title, title, StringComparison.OrdinalIgnoreCase));

        public void SetActive(Note? note)
        {
            if (note is null || note == Active) return;
            Flush();
            SetActiveInternal(note);
            Persist();
            ActiveChanged?.Invoke();
        }

        /// <summary>
        /// Tabs the user can actually reach: while the search filters the strip, hidden tabs are
        /// skipped, so cycling and number shortcuts follow what is on screen.
        /// </summary>
        private List<Note> Navigable() => Tabs.Where(n => n.IsTabVisible).ToList();

        public void ActivateIndex(int index)
        {
            var tabs = Navigable();
            if (index < 0 || index >= tabs.Count) return;
            SetActive(tabs[index]);
        }

        public void CycleActive(int delta)
        {
            var tabs = Navigable();
            if (tabs.Count == 0) return;
            if (tabs.Count == 1)
            {
                SetActive(tabs[0]);
                return;
            }

            var index = Active is null ? -1 : tabs.IndexOf(Active);
            if (index < 0)
            {
                // The active note is filtered out: start from the first visible one.
                SetActive(tabs[delta >= 0 ? 0 : tabs.Count - 1]);
                return;
            }

            index = (index + delta + tabs.Count) % tabs.Count;
            SetActive(tabs[index]);
        }

        /// <summary>
        /// Closing never destroys anything: a library note goes back to the library, everything
        /// else goes to the trash for 30 days.
        /// </summary>
        public void Close(Note? note)
        {
            if (note is null) return;

            // The help tab is a built-in page, not user content: closing it just removes it.
            if (note.IsHelp)
            {
                CloseAndForget(note);
                return;
            }

            if (note.Origin == Note.OriginLibrary)
            {
                MoveToLibrary(note);
                return;
            }

            DiscardToTrash(note);
        }

        /// <summary>Removes an open note straight to the trash, whatever its origin was.</summary>
        public void DiscardToTrash(Note? note)
        {
            if (note is null || note.IsHelp) return;

            Flush();

            var index = Tabs.IndexOf(note);
            if (index < 0) return;

            WriteTrashEntry(note.Id, note.Title, note.Text, DateTime.Now);
            Tabs.Remove(note);
            note.IsOpen = false;
            note.IsActive = false;
            TryDelete(NotePath(note.Id));

            EnsureActiveAfterRemoval(index);
            Persist();
            TrashChanged?.Invoke();
            TabsChanged?.Invoke();
            ActiveChanged?.Invoke();
        }

        /// <summary>Files the note away for good: it leaves the tabs and sits in the library.</summary>
        public void MoveToLibrary(Note? note)
        {
            if (note is null || note.IsHelp) return;

            Flush();

            var index = Tabs.IndexOf(note);
            if (index < 0) return;

            Tabs.Remove(note);
            note.IsOpen = false;
            note.IsActive = false;
            note.Origin = Note.OriginLibrary;
            note.LibraryAt = DateTime.Now;
            note.Preview = MakePreview(note.Text);
            Library.Remove(note);
            Library.Insert(0, note);
            SortLibrary();

            EnsureActiveAfterRemoval(index);
            Persist();
            LibraryChanged?.Invoke();
            TabsChanged?.Invoke();
            ActiveChanged?.Invoke();
        }

        /// <summary>Opens a library note in the tabs; closing it will put it back.</summary>
        public void Restore(Note? note)
        {
            if (note is null) return;

            Library.Remove(note);
            note.IsOpen = true;
            note.Origin = Note.OriginLibrary;
            note.Order = Tabs.Count;
            note.Modified = DateTime.Now;
            Tabs.Add(note);

            SetActiveInternal(note);
            Persist();
            LibraryChanged?.Invoke();
            TabsChanged?.Invoke();
            ActiveChanged?.Invoke();
        }

        public void RestoreLastClosed()
        {
            var last = ReadTrash().FirstOrDefault();
            if (last is not null) RestoreFromTrash(last);
        }

        public void CloseAndForget(Note? note)
        {
            if (note is null) return;
            Flush();

            var index = Tabs.IndexOf(note);
            if (index < 0) return;
            Tabs.Remove(note);
            TryDelete(NotePath(note.Id));

            EnsureActiveAfterRemoval(index);
            Persist();
            TabsChanged?.Invoke();
            ActiveChanged?.Invoke();
        }

        public void CloseAllForget()
        {
            foreach (var note in Tabs.ToList())
            {
                TryDelete(NotePath(note.Id));
            }

            Tabs.Clear();
            CreateNote(save: false);
            Persist();
            TabsChanged?.Invoke();
            ActiveChanged?.Invoke();
        }

        private void EnsureActiveAfterRemoval(int removedIndex)
        {
            if (Tabs.Count == 0)
            {
                var fresh = new Note { Title = NextTitle(), Order = 0, IsOpen = true };
                Tabs.Add(fresh);
                SetActiveInternal(fresh);
                return;
            }

            SetActiveInternal(Tabs[Math.Min(removedIndex, Tabs.Count - 1)]);
        }

        private void SetActiveInternal(Note note)
        {
            foreach (var tab in Tabs) tab.IsActive = false;
            note.IsActive = true;
            Active = note;
        }

        // ---------- library ----------

        /// <summary>A library note moved to the trash keeps its text but loses its library place.</summary>
        public void DeleteToTrash(Note? note)
        {
            if (note is null) return;

            WriteTrashEntry(note.Id, note.Title, note.Text, DateTime.Now);
            TryDelete(NotePath(note.Id));
            Library.Remove(note);
            Persist();
            LibraryChanged?.Invoke();
            TrashChanged?.Invoke();
        }

        // ---------- trash ----------

        public List<TrashItem> ReadTrash()
        {
            var items = new List<TrashItem>();

            try
            {
                foreach (var file in Directory.EnumerateFiles(AppPaths.TrashDir, "*.json"))
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize<TrashEntry>(File.ReadAllText(file), JsonOptions);
                        if (entry is null) continue;

                        items.Add(new TrashItem
                        {
                            Id = entry.Id,
                            Title = entry.Title,
                            Text = entry.Text,
                            DeletedAt = entry.DeletedAt,
                            FilePath = file
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Error("trash entry read failed: " + file, ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("trash enumerate failed", ex);
            }

            return items.OrderByDescending(i => i.DeletedAt).ToList();
        }

        public void RestoreFromTrash(TrashItem item)
        {
            // Restored notes behave like fresh ones: closing them sends them back to the trash,
            // with a fresh 30-day countdown.
            CreateNote(title: item.Title, text: item.Text, origin: Note.OriginTrash);
            TryDelete(item.FilePath);
            TrashChanged?.Invoke();
        }

        public void DeleteTrashItem(TrashItem item)
        {
            TryDelete(item.FilePath);
            TrashChanged?.Invoke();
        }

        public void ClearTrash()
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(AppPaths.TrashDir)) TryDelete(file);
            }
            catch (Exception ex)
            {
                Log.Error("trash clear failed", ex);
            }

            TrashChanged?.Invoke();
        }

        private void WriteTrashEntry(string id, string title, string text, DateTime deletedAt)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new TrashEntry
                {
                    Id = id,
                    Title = title,
                    Text = text,
                    DeletedAt = deletedAt
                }, JsonOptions);

                var name = $"{deletedAt:yyyyMMdd-HHmmss}-{id}.json";
                AppPaths.WriteAtomic(Path.Combine(AppPaths.TrashDir, name), payload);
            }
            catch (Exception ex)
            {
                Log.Error("trash write failed", ex);
            }
        }

        private void PurgeTrash()
        {
            try
            {
                var limit = DateTime.Now.AddDays(-TrashRetentionDays);
                foreach (var file in Directory.EnumerateFiles(AppPaths.TrashDir))
                {
                    if (File.GetLastWriteTime(file) < limit) File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                Log.Error("trash purge failed", ex);
            }
        }

        // ---------- rename / order ----------

        public void MarkSaved(Note note)
        {
            note.SavedAt = DateTime.Now;
            note.Modified = DateTime.Now;
            Persist();
        }

        public void Rename(Note? note, string title)
        {
            if (note is null) return;

            var clean = title.Trim();
            if (clean.Length == 0) clean = Loc.T("note.default");
            if (clean.Length > 80) clean = clean[..80];

            note.Title = clean;
            note.Modified = DateTime.Now;
            Persist();
            TabsChanged?.Invoke();
            LibraryChanged?.Invoke();
        }

        public void Move(Note note, int targetIndex)
        {
            var current = Tabs.IndexOf(note);
            if (current < 0) return;

            targetIndex = Math.Clamp(targetIndex, 0, Tabs.Count - 1);
            if (current == targetIndex) return;

            Tabs.Move(current, targetIndex);
            for (var i = 0; i < Tabs.Count; i++) Tabs[i].Order = i;
            Persist();
        }

        // ---------- helpers ----------

        public string GetText(Note note) => note.Text;

        private void Persist() => SaveIndex();

        private void SaveIndex()
        {
            try
            {
                var notes = new List<NoteMeta>();

                foreach (var n in Tabs)
                {
                    notes.Add(ToMeta(n, open: true));
                }

                foreach (var n in Library)
                {
                    notes.Add(ToMeta(n, open: false));
                }

                var index = new IndexFile
                {
                    Version = IndexVersion,
                    ActiveId = Active?.Id,
                    Notes = notes
                };

                AppPaths.WriteAtomic(AppPaths.IndexFile, JsonSerializer.Serialize(index, JsonOptions));
            }
            catch (Exception ex)
            {
                Log.Error("index save failed", ex);
            }
        }

        private static NoteMeta ToMeta(Note n, bool open) => new()
        {
            Id = n.Id,
            Title = n.Title,
            Order = n.Order,
            Created = n.Created,
            Modified = n.Modified,
            Open = open,
            IsHelp = n.IsHelp,
            Library = !open,
            Origin = n.Origin,
            LibraryAt = n.LibraryAt,
            SavedPath = n.SavedPath,
            SavedAt = n.SavedAt
        };

        private IndexFile? ReadIndex()
        {
            try
            {
                if (!File.Exists(AppPaths.IndexFile)) return null;
                return JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(AppPaths.IndexFile), JsonOptions);
            }
            catch (Exception ex)
            {
                Log.Error("index read failed, rebuilding", ex);
                return null;
            }
        }

        private string ReadText(string id)
        {
            try
            {
                var path = NotePath(id);
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
            catch (Exception ex)
            {
                Log.Error($"note read failed: {id}", ex);
                return string.Empty;
            }
        }

        private static string? TitleFromFile(string path)
        {
            try
            {
                foreach (var line in File.ReadLines(path).Take(3))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length > 0) return trimmed.Length > 60 ? trimmed[..60] : trimmed;
                }
            }
            catch { }

            return null;
        }

        private static string MakePreview(string text)
        {
            var flat = string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                .Trim();

            return flat.Length > PreviewLength ? flat[..PreviewLength] + "…" : flat;
        }

        private string NextTitle()
        {
            // Titles may be numbered in either built-in language, so parse both prefixes.
            var prefixes = new[] { Loc.T("note.default") + " ", "Заметка " };
            var max = 0;

            foreach (var note in Tabs.Concat(Library))
            {
                var title = note.Title.Trim();
                foreach (var prefix in prefixes)
                {
                    if (!title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (int.TryParse(title[prefix.Length..], out var number)) max = Math.Max(max, number);
                }
            }

            return Loc.F("note.numbered", Math.Max(max + 1, Tabs.Count + 1));
        }

        private Note? FindById(string id) => Tabs.Concat(Library).FirstOrDefault(n => n.Id == id);

        private static string NotePath(string id) => Path.Combine(AppPaths.NotesDir, id + ".txt");

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Error("delete failed: " + path, ex);
            }
        }

        private void SortLibrary() =>
            Library.Sort((a, b) => (b.LibraryAt ?? b.Modified).CompareTo(a.LibraryAt ?? a.Modified));

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private sealed class IndexFile
        {
            public int Version { get; set; } = 1;
            public string? ActiveId { get; set; }
            public List<NoteMeta> Notes { get; set; } = new();
        }

        private sealed class NoteMeta
        {
            public string Id { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public int Order { get; set; }
            public DateTime Created { get; set; }
            public DateTime Modified { get; set; }
            public bool Open { get; set; }

            /// <summary>Version 2: the note sits in the library.</summary>
            public bool Library { get; set; }

            /// <summary>Version 2: none, library or trash.</summary>
            public string Origin { get; set; } = string.Empty;

            public DateTime? LibraryAt { get; set; }
            public bool IsHelp { get; set; }

            /// <summary>Version 1 kept closed notes in a history list; read only for the migration.</summary>
            public DateTime? HistoryAt { get; set; }

            public string? SavedPath { get; set; }
            public DateTime? SavedAt { get; set; }
        }

        private sealed class TrashEntry
        {
            public string Id { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Text { get; set; } = string.Empty;
            public DateTime DeletedAt { get; set; }
        }
    }
}
