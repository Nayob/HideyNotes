using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HideyNotes.Services;

namespace HideyNotes.Localization
{
    /// <summary>A language the user can pick in the settings.</summary>
    public sealed class LanguageInfo
    {
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public bool BuiltIn { get; init; }
        public bool FromFile { get; init; }
    }

    /// <summary>
    /// Interface strings. Russian and English are compiled into the executable; files dropped into
    /// the "lang" folder add languages or override the built-in ones. A broken file is skipped, so
    /// the interface always has text.
    /// XAML picks the strings up as {DynamicResource Loc.some.key}.
    /// </summary>
    public sealed class Loc
    {
        public const string FallbackCode = "ru";
        public const string EnglishCode = "en";

        private sealed class Pack
        {
            public string Name { get; init; } = "";
            public bool BuiltIn { get; init; }
            public bool FromFile { get; init; }
            public Dictionary<string, string> Strings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class LanguageFile
        {
            public string? Code { get; set; }
            public string? Name { get; set; }
            public Dictionary<string, string>? Strings { get; set; }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static readonly string[] EmbeddedResources =
        {
            "HideyNotes.Localization.Strings.ru.json",
            "HideyNotes.Localization.Strings.en.json"
        };

        private readonly Dictionary<string, Pack> _packs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _effective = new(StringComparer.OrdinalIgnoreCase);

        // English first, Russian last: a partially translated file only shows its own gaps.
        private static readonly string[] FallbackOrder = { EnglishCode, FallbackCode };
        private readonly List<Dictionary<string, string>> _fallbackLayers = new();
        private List<LanguageInfo> _languages = new();

        public static Loc Instance { get; } = new Loc();

        /// <summary>Code of the language in use right now.</summary>
        public string Code { get; private set; } = FallbackCode;

        public IReadOnlyList<LanguageInfo> Languages => _languages;

        /// <summary>Culture used for numbers and dates of this language.</summary>
        public CultureInfo Culture { get; private set; } = CultureInfo.CurrentCulture;

        /// <summary>Raised when the language changes (tray menu, code-built texts).</summary>
        public event Action? Changed;

        // ---------- lookup ----------

        public string this[string key] => Get(key);

        public static string T(string key) => Instance.Get(key);

        public static string F(string key, params object?[] args) => Instance.Format(key, args);

        public string Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            if (_effective.TryGetValue(key, out var value)) return value;

            foreach (var layer in _fallbackLayers)
            {
                if (layer.TryGetValue(key, out var fallback)) return fallback;
            }

            return key;
        }

        public string Format(string key, params object?[] args)
        {
            var text = Get(key);
            if (args.Length == 0) return text;

            try
            {
                return string.Format(text, args);
            }
            catch (FormatException)
            {
                return text;
            }
        }

        /// <summary>Picks the form of a count word, Russian and English rules.</summary>
        public string Plural(string keyBase, int count)
        {
            foreach (var suffix in PluralSuffixes(count))
            {
                var key = $"{keyBase}.{suffix}";
                if (_effective.TryGetValue(key, out var value)) return value;

                foreach (var layer in _fallbackLayers)
                {
                    if (layer.TryGetValue(key, out var fallback)) return fallback;
                }
            }

            return Get($"{keyBase}.many");
        }

        private IEnumerable<string> PluralSuffixes(int count)
        {
            if (string.Equals(Code, FallbackCode, StringComparison.OrdinalIgnoreCase))
            {
                var mod100 = count % 100;
                if (mod100 is >= 11 and <= 14)
                {
                    yield return "many";
                }
                else
                {
                    var mod10 = count % 10;
                    if (mod10 == 1) yield return "one";
                    else if (mod10 is >= 2 and <= 4) yield return "few";
                    else yield return "many";
                }

                yield return "many";
                yield break;
            }

            yield return count == 1 ? "one" : "many";
            yield return "many";
        }

        // ---------- setup ----------

        /// <summary>Loads the dictionaries and activates the requested language ("" = system).</summary>
        public void Initialize(string languageCode)
        {
            Reload();
            SetLanguage(languageCode);
        }

        /// <summary>Re-reads the "lang" folder without touching the current selection.</summary>
        public void Reload()
        {
            _packs.Clear();
            LoadEmbedded();
            LoadFiles();
            RebuildLanguages();
        }

        public void SetLanguage(string languageCode)
        {
            var code = Resolve(languageCode);

            _effective.Clear();
            if (_packs.TryGetValue(code, out var pack))
            {
                foreach (var pair in pack.Strings) _effective[pair.Key] = pair.Value;
            }

            _fallbackLayers.Clear();
            foreach (var fallbackCode in FallbackOrder)
            {
                if (string.Equals(fallbackCode, code, StringComparison.OrdinalIgnoreCase)) continue;
                if (_packs.TryGetValue(fallbackCode, out var fallbackPack)) _fallbackLayers.Add(fallbackPack.Strings);
            }

            Code = code;
            Culture = SafeCulture(code);
            PublishResources();
            Changed?.Invoke();
        }

        private static CultureInfo SafeCulture(string code)
        {
            try
            {
                return CultureInfo.GetCultureInfo(code);
            }
            catch
            {
                return CultureInfo.CurrentCulture;
            }
        }

        private readonly HashSet<string> _resourceKeys = new();

        /// <summary>
        /// Puts the strings into the application resources so every {DynamicResource Loc.key}
        /// in XAML picks up the new language at once.
        /// </summary>
        private void PublishResources()
        {
            var resources = System.Windows.Application.Current?.Resources;
            if (resources is null) return;

            foreach (var key in _resourceKeys) resources.Remove(key);
            _resourceKeys.Clear();

            void Publish(Dictionary<string, string> source)
            {
                foreach (var pair in source)
                {
                    var key = "Loc." + pair.Key;
                    if (!_resourceKeys.Add(key)) continue; // the first source wins

                    resources[key] = pair.Value;
                }
            }

            Publish(_effective);
            foreach (var layer in _fallbackLayers) Publish(layer);
        }

        private string Resolve(string requested)
        {
            if (!string.IsNullOrWhiteSpace(requested) && _packs.ContainsKey(requested)) return requested;

            var system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (!string.IsNullOrWhiteSpace(system) && _packs.ContainsKey(system)) return system;

            if (_packs.ContainsKey(FallbackCode)) return FallbackCode;
            return _packs.Keys.FirstOrDefault() ?? FallbackCode;
        }

        private void LoadEmbedded()
        {
            var assembly = Assembly.GetExecutingAssembly();

            foreach (var resource in EmbeddedResources)
            {
                try
                {
                    using var stream = assembly.GetManifestResourceStream(resource);
                    if (stream is null)
                    {
                        Log.Error($"language resource missing: {resource}");
                        continue;
                    }

                    using var reader = new StreamReader(stream);
                    var code = ResourceCode(resource);
                    var pack = Parse(reader.ReadToEnd(), code, builtIn: true);
                    if (pack is null)
                    {
                        Log.Error($"language resource is broken: {resource}");
                        continue;
                    }

                    _packs[code] = pack;
                }
                catch (Exception ex)
                {
                    Log.Error($"language resource {resource} failed", ex);
                }
            }
        }

        private static string ResourceCode(string resource)
        {
            // ...Strings.ru.json -> ru
            var name = resource[(resource.LastIndexOf("Strings.", StringComparison.Ordinal) + "Strings.".Length)..];
            return name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
        }

        /// <summary>Extra languages: any *.json in the "lang" folder of the data directory.</summary>
        private void LoadFiles()
        {
            string folder;
            try
            {
                folder = AppPaths.LangDir;
                if (!Directory.Exists(folder)) return;
            }
            catch (Exception ex)
            {
                Log.Error("lang folder unavailable", ex);
                return;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(folder, "*.json");
            }
            catch (Exception ex)
            {
                Log.Error("lang folder listing failed", ex);
                return;
            }

            foreach (var file in files)
            {
                try
                {
                    var fileCode = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    var document = JsonSerializer.Deserialize<LanguageFile>(File.ReadAllText(file), JsonOptions);

                    if (document?.Strings is null || document.Strings.Count == 0)
                    {
                        // Never let a broken file leave the interface empty: keep the built-in one.
                        Log.Error($"lang file {Path.GetFileName(file)} is broken, ignored");
                        continue;
                    }

                    var strings = Clean(document.Strings);
                    if (strings.Count == 0)
                    {
                        Log.Error($"lang file {Path.GetFileName(file)} has no usable strings, ignored");
                        continue;
                    }

                    var code = string.IsNullOrWhiteSpace(document.Code) ? fileCode : document.Code.Trim().ToLowerInvariant();
                    var name = string.IsNullOrWhiteSpace(document.Name) ? code : document.Name.Trim();

                    if (_packs.TryGetValue(code, out var existing) && existing.BuiltIn)
                    {
                        // A file may fix or extend a built-in language: merge, the file wins.
                        var merged = new Dictionary<string, string>(existing.Strings, StringComparer.OrdinalIgnoreCase);
                        foreach (var pair in strings) merged[pair.Key] = pair.Value;

                        _packs[code] = new Pack
                        {
                            Name = name.Length > 0 ? name : existing.Name,
                            BuiltIn = true,
                            FromFile = true,
                            Strings = merged
                        };
                        Log.Info($"lang file {Path.GetFileName(file)} overrides built-in {code}");
                    }
                    else
                    {
                        _packs[code] = new Pack { Name = name, FromFile = true, Strings = strings };
                        Log.Info($"lang file {Path.GetFileName(file)} adds {code} ({strings.Count} strings)");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"lang file {Path.GetFileName(file)} failed", ex);
                }
            }
        }

        private static Dictionary<string, string> Clean(Dictionary<string, string> source)
        {
            var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in source)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                strings[pair.Key] = pair.Value ?? string.Empty;
            }

            return strings;
        }

        private static Pack? Parse(string json, string fallbackCode, bool builtIn)
        {
            try
            {
                var file = JsonSerializer.Deserialize<LanguageFile>(json, JsonOptions);
                if (file?.Strings is null || file.Strings.Count == 0) return null;

                var strings = Clean(file.Strings);
                if (strings.Count == 0) return null;

                var name = string.IsNullOrWhiteSpace(file.Name) ? fallbackCode : file.Name.Trim();
                return new Pack { Name = name, BuiltIn = builtIn, Strings = strings };
            }
            catch
            {
                return null;
            }
        }

        private void RebuildLanguages()
        {
            _languages = _packs
                .Select(pair => new LanguageInfo
                {
                    Code = pair.Key,
                    Name = pair.Value.Name,
                    BuiltIn = pair.Value.BuiltIn,
                    FromFile = pair.Value.FromFile
                })
                .OrderBy(info => info.Code == FallbackCode ? 0 : info.Code == EnglishCode ? 1 : 2)
                .ThenBy(info => info.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }
}
