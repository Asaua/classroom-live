using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace ClassroomLive.Extension
{
    internal sealed class LocaleOption
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public string Direction { get; set; }
        public Dictionary<string, string> Values { get; set; }
    }

    internal static class ExtensionLocalization
    {
        private static readonly List<LocaleOption> options = new List<LocaleOption>();
        private static LocaleOption current;

        internal static IReadOnlyList<LocaleOption> Options { get { return options; } }
        internal static string Code { get { return current == null ? "ko" : current.Code; } }
        internal static bool HasSavedLanguage { get { return Find(ReadSavedLanguage()) != null; } }

        internal static void Initialize()
        {
            options.Clear();
            var extensionFolder = Path.GetDirectoryName(typeof(ExtensionLocalization).Assembly.Location) ?? "";
            var directory = Path.Combine(extensionFolder, "Host", "wwwroot", "locales");
            if (Directory.Exists(directory))
            {
                foreach (var path in Directory.GetFiles(directory, "*.json"))
                {
                    var locale = Read(path);
                    if (locale != null) options.Add(locale);
                }
            }

            if (options.Count == 0)
            {
                options.Add(new LocaleOption
                {
                    Code = "en", Name = "English", Direction = "ltr",
                    Values = new Dictionary<string, string>()
                });
            }

            var saved = ReadSavedLanguage();
            var system = CultureInfo.CurrentUICulture.Name;
            var neutralSystem = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            Apply(Find(saved) != null ? saved : Find(system) != null ? system :
                Find(neutralSystem) != null ? neutralSystem : Find("en") != null ? "en" : options[0].Code);
        }

        internal static bool Apply(string code)
        {
            var locale = Find(code);
            if (locale == null) return false;
            current = locale;
            return true;
        }

        internal static void Save(string code)
        {
            if (!Apply(code)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath, "{\"language\":\"" + Code + "\"}", new UTF8Encoding(false));
            }
            catch { }
        }

        internal static string T(string key, params object[] values)
        {
            string text;
            if (current == null || !current.Values.TryGetValue(key, out text)) text = key;
            for (var index = 0; index + 1 < values.Length; index += 2)
                text = text.Replace("{" + values[index] + "}", Convert.ToString(values[index + 1], CultureInfo.CurrentCulture));
            return text;
        }

        private static LocaleOption Find(string code) => options.FirstOrDefault(option =>
            string.Equals(option.Code, code, StringComparison.OrdinalIgnoreCase));

        private static LocaleOption Read(string path)
        {
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(Dictionary<string, string>),
                    new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
                using (var stream = File.OpenRead(path))
                {
                    var values = (Dictionary<string, string>)serializer.ReadObject(stream);
                    string code, name, direction;
                    if (!values.TryGetValue("$code", out code) || !values.TryGetValue("$name", out name) ||
                        !values.TryGetValue("$direction", out direction)) return null;
                    return new LocaleOption { Code = code, Name = name, Direction = direction, Values = values };
                }
            }
            catch { return null; }
        }

        private static string SettingsPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClassroomLive", "settings.json");

        private static string ReadSavedLanguage()
        {
            try
            {
                var match = Regex.Match(File.ReadAllText(SettingsPath), "\\\"language\\\"\\s*:\\s*\\\"([a-zA-Z0-9-]+)\\\"");
                return match.Success ? match.Groups[1].Value : null;
            }
            catch { return null; }
        }
    }
}
