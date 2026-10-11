using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BloomHarbor
{
    /// <summary>
    /// The game's user-facing copy, by key, for one locale at a time.
    ///
    /// Pure C# on purpose: the offline suites load the same shipped locale files from disk and
    /// exercise the same lookup the player gets, so a missing key or a translation with the wrong
    /// placeholders fails a console suite rather than a screenshot review. Nothing here touches
    /// UnityEngine; <c>LocaleService</c> is the thin shim that hands it the TextAssets.
    ///
    /// Three properties matter more than features here, because a translation is data that arrives
    /// after the code is written and is often wrong:
    ///   * a lookup never returns null or empty, so a layout can never silently collapse;
    ///   * a malformed translation never throws, it degrades to English and then to the template;
    ///   * every failure is recorded in <see cref="Missing"/> and <see cref="Malformed"/> so a test
    ///     can assert on it instead of a human noticing.
    /// </summary>
    public static class Strings
    {
        public const string DefaultLocale = "en";

        /// <summary>
        /// The pseudolocale. It is not a language: it lengthens and accents every English string so
        /// a layout can be proved against the long end of real translation growth before any
        /// translator is paid. German and Portuguese routinely run 30-40% longer than English.
        /// </summary>
        public const string PseudoLocale = "qps";

        static readonly Dictionary<string, Dictionary<string, string>> tables =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        static readonly SortedSet<string> missing = new SortedSet<string>(StringComparer.Ordinal);
        static readonly SortedSet<string> malformed = new SortedSet<string>(StringComparer.Ordinal);
        static string locale = DefaultLocale;

        /// <summary>The locale lookups resolve against. Always a loaded locale or the default.</summary>
        public static string Locale { get { return locale; } }

        /// <summary>Keys a lookup could not resolve in any table. A shipped build should never add one.</summary>
        public static IEnumerable<string> Missing { get { return missing; } }

        /// <summary>Keys whose translation could not take the arguments the caller passed.</summary>
        public static IEnumerable<string> Malformed { get { return malformed; } }

        public static IEnumerable<string> LoadedLocales { get { return tables.Keys; } }

        public static void Reset()
        {
            tables.Clear(); missing.Clear(); malformed.Clear(); locale = DefaultLocale;
            ApplyNumberRule(locale);
        }

        // --- Whole numbers ------------------------------------------------------------------------

        /// <summary>
        /// How a language groups the digits of a whole number: the separator, and the digit count from
        /// which grouping starts. Keyed by language, with a regional row where a region writes its
        /// numbers differently from the language's default; anything not listed groups like English.
        ///
        /// Deliberately not the device's <see cref="CultureInfo"/>: the language on screen is the
        /// game's locale, which can differ from the device region, and a player whose digits followed
        /// the device while the words followed the game read "13,540" under a Spanish sentence, where
        /// a comma is the decimal mark (design review UX-12). It is also data the offline suites can
        /// assert exactly, which a platform's culture tables are not. The device's region is used only
        /// when its language is the one on screen (<see cref="SetLocale"/>).
        ///
        /// Spanish is CLDR's rule for <c>es</c>: a full stop, and no separator below five digits
        /// (minimumGroupingDigits 2), so "1200" and "13.540" — what a phone in Spain shows. Mexico, the
        /// United States, Latin America as a whole (419), Central America and the Spanish Caribbean write
        /// "13,540" with a full stop for the decimal, so "13.540" there reads as thirteen and a half:
        /// those regions keep the English rule while reading the same Spanish catalogue.
        /// A new locale that does not group with "," from four digits adds its row here.
        /// </summary>
        static readonly Dictionary<string, KeyValuePair<char, int>> numberRules =
            new Dictionary<string, KeyValuePair<char, int>>(StringComparer.OrdinalIgnoreCase)
            {
                { "es", new KeyValuePair<char, int>('.', 5) },
                { "es-MX", EnglishGrouping }, { "es-US", EnglishGrouping }, { "es-419", EnglishGrouping },
                { "es-GT", EnglishGrouping }, { "es-HN", EnglishGrouping }, { "es-NI", EnglishGrouping },
                { "es-PA", EnglishGrouping }, { "es-SV", EnglishGrouping }, { "es-PR", EnglishGrouping },
                { "es-DO", EnglishGrouping },
            };

        static KeyValuePair<char, int> EnglishGrouping { get { return new KeyValuePair<char, int>(',', 4); } }

        static char groupSeparator = ',';
        static int groupingFrom = 4;

        /// <summary>The digit-group separator of the active locale ("," in English, "." in Spanish).</summary>
        public static char GroupSeparator { get { return groupSeparator; } }

        /// <summary>The fewest digits a whole number has before it is grouped (4 in English, 5 in Spanish).</summary>
        public static int GroupingFrom { get { return groupingFrom; } }

        static string BaseLanguage(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            int dash = code.IndexOf('-');
            return dash > 0 ? code.Substring(0, dash) : code;
        }

        /// <summary>
        /// Chooses the grouping for the locale on screen. <paramref name="requested"/> is the device's
        /// code ("es-MX"); its region decides only when its language is the one being shown, so an
        /// unshipped language that fell back to English groups as English does.
        /// </summary>
        static void ApplyNumberRule(string resolved, string requested = null)
        {
            KeyValuePair<char, int> rule;
            string shown = resolved ?? DefaultLocale;
            bool regional = !string.IsNullOrEmpty(requested) &&
                string.Equals(BaseLanguage(requested), BaseLanguage(shown), StringComparison.OrdinalIgnoreCase);
            // A device code can carry a script or a second subtag ("es-419", "zh-Hant-TW"); the regional
            // row is tried whole and then as language-region.
            string languageRegion = null;
            if (regional)
            {
                int second = requested.IndexOf('-', requested.IndexOf('-') + 1);
                if (second > 0) languageRegion = requested.Substring(0, second);
            }
            if (!(regional && numberRules.TryGetValue(requested, out rule)) &&
                !(languageRegion != null && numberRules.TryGetValue(languageRegion, out rule)) &&
                !numberRules.TryGetValue(shown, out rule) &&
                !numberRules.TryGetValue(BaseLanguage(shown), out rule))
                rule = EnglishGrouping;
            groupSeparator = rule.Key; groupingFrom = rule.Value;
        }

        /// <summary>
        /// A whole number as the active locale writes it: "13,540" in English, "13.540" and "1200" in
        /// Spanish. The one formatter for every count, score, price in coins or gems and balance the
        /// game draws; <see cref="Format"/> applies it to integral arguments, and a count-up's
        /// "{0:N0}" uses the same rule (<c>BloomTween.LayOutCount</c>).
        /// </summary>
        public static string Number(long value)
        {
            bool negative = value < 0;
            // Unsigned so long.MinValue still has a magnitude.
            ulong magnitude = negative ? (ulong)(-(value + 1)) + 1UL : (ulong)value;
            string digits = magnitude.ToString(CultureInfo.InvariantCulture);
            if (digits.Length < groupingFrom)
                return negative ? "-" + digits : digits;
            var builder = new StringBuilder(digits.Length + digits.Length / 3 + 1);
            if (negative) builder.Append('-');
            for (int i = 0; i < digits.Length; i++)
            {
                if (i > 0 && (digits.Length - i) % 3 == 0) builder.Append(groupSeparator);
                builder.Append(digits[i]);
            }
            return builder.ToString();
        }

        /// <summary>
        /// The first whole number in drawn <paramref name="text"/> that is not written the way <see cref="Number"/>
        /// writes it in the active locale, or null when every one is. Three shapes count: a run of digits long
        /// enough to group but left bare ("13540" anywhere, "1200" in English); a number grouped with the other
        /// language's mark ("13,540" under Spanish, "13.540" under English); and a group below the locale's
        /// threshold ("1.200" in Spanish). A price is the store's to format, so a number beside a currency sign
        /// is left alone, as is a decimal ("2.99", "0,99"), a clock ("12:34") and a digit fused with letters
        /// ("L18", "x3"). The PageAudit lane runs this over every drawn line (design uplift S1 #9, UX-12); the
        /// localization suite proves it on known-bad lines.
        /// </summary>
        public static string MisgroupedNumber(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int n = text.Length, i = 0;
            while (i < n)
            {
                if (!char.IsDigit(text[i])) { i++; continue; }
                int start = i, end = i;
                // A run of digit groups joined by single '.' or ',' marks.
                while (end < n && (char.IsDigit(text[end]) ||
                       ((text[end] == '.' || text[end] == ',') && end + 1 < n && char.IsDigit(text[end + 1]) && end > start)))
                    end++;
                i = end;
                string token = text.Substring(start, end - start);
                char before = start > 0 ? text[start - 1] : ' ', after = end < n ? text[end] : ' ';
                if (char.IsLetter(before) || char.IsLetter(after) || before == ':' || after == ':') continue;
                if (NearCurrency(text, start, end)) continue;
                int digits = 0; char mark = '\0'; bool mixed = false;
                foreach (char c in token)
                {
                    if (char.IsDigit(c)) { digits++; continue; }
                    if (mark != '\0' && mark != c) mixed = true;
                    mark = c;
                }
                if (mark == '\0')
                {
                    if (digits >= groupingFrom) return token;
                    continue;
                }
                if (mixed) continue; // "1,234.50": a price or a decimal, never a count this game draws
                string[] groups = token.Split(mark);
                bool grouped = groups[0].Length >= 1 && groups[0].Length <= 3;
                for (int g = 1; g < groups.Length && grouped; g++) grouped = groups[g].Length == 3;
                if (!grouped) continue; // "2.99", "0,99", "1,5": a decimal
                if (mark != groupSeparator || digits < groupingFrom) return token;
            }
            return null;
        }

        static bool NearCurrency(string text, int start, int end)
        {
            const string signs = "$€£¥₹₩₽¢₺₫₱";
            for (int k = start - 1; k >= 0 && k >= start - 3; k--)
            {
                if (signs.IndexOf(text[k]) >= 0) return true;
                if (!char.IsWhiteSpace(text[k]) && !char.IsLetter(text[k])) break;
            }
            for (int k = end; k < text.Length && k <= end + 2; k++)
            {
                if (signs.IndexOf(text[k]) >= 0) return true;
                if (!char.IsWhiteSpace(text[k])) break;
            }
            return false;
        }

        /// <summary>A copy of the arguments with every integral one written by <see cref="Number"/>,
        /// or the arguments themselves when none is integral.</summary>
        static object[] Localized(object[] args)
        {
            object[] copy = null;
            for (int i = 0; i < args.Length; i++)
            {
                object arg = args[i];
                long whole;
                if (arg is int) whole = (int)arg;
                else if (arg is long) whole = (long)arg;
                else if (arg is short) whole = (short)arg;
                else if (arg is uint) whole = (uint)arg;
                else if (arg is ushort) whole = (ushort)arg;
                else continue;
                if (copy == null) copy = (object[])args.Clone();
                copy[i] = Number(whole);
            }
            return copy ?? args;
        }

        // --- Line length (design review UX-09) --------------------------------------------------------

        /// <summary>
        /// The most words a line of UI copy carries (design uplift S1 #9, UX-09: "a copy pass with a
        /// 6-word cap"). A player reads a line at a glance or skips it; a paragraph is skipped. The
        /// localization suite holds every catalogue line to it, and the PageAudit lane every drawn line.
        /// </summary>
        public const int LineWordCap = 6;

        /// <summary>
        /// The cap for a locale. Spanish carries one more: it spends a word on articles and prepositions
        /// English leaves out ("la tienda", "de la marea"), and a translation squeezed to English's count
        /// reads as a telegram. Every other language takes <see cref="LineWordCap"/>.
        /// </summary>
        public static int LineWordCapFor(string localeCode)
        {
            return string.Equals(BaseLanguage(localeCode), "es", StringComparison.OrdinalIgnoreCase) ? LineWordCap + 1 : LineWordCap;
        }

        /// <summary>
        /// The words in one line of copy, as the cap counts them: a whitespace-separated token that holds a
        /// letter. Numerals, prices, percentages and symbols ("13,540", "$0.99", "100 %", "+5", "·") are read
        /// at a glance and do not count. In a catalogue template a bare placeholder ("{0}", "{0}.") may be a
        /// name and counts as a word; one fused with a sign, a slash or brackets ("+{0}", "{0}/{1}", "×{2}",
        /// "({4})") is a number and does not.
        /// </summary>
        public static int Words(string line)
        {
            if (string.IsNullOrEmpty(line)) return 0;
            int words = 0, i = 0, n = line.Length;
            while (i < n)
            {
                while (i < n && char.IsWhiteSpace(line[i])) i++;
                if (i >= n) break;
                bool letter = false, placeholder = false, numeric = false;
                while (i < n && !char.IsWhiteSpace(line[i]))
                {
                    char c = line[i];
                    if (c == '{' && i + 1 < n && char.IsDigit(line[i + 1]))
                    {
                        int close = line.IndexOf('}', i);
                        if (close > i) { placeholder = true; i = close + 1; continue; }
                    }
                    if (char.IsLetter(c)) letter = true;
                    else if (c != '.' && c != ',' && c != ':' && c != ';' && c != '!' && c != '?' && c != '¡' && c != '¿' &&
                             c != '…' && c != '"' && c != '\'' && c != '«' && c != '»' && c != '’') numeric = true;
                    i++;
                }
                if (letter || (placeholder && !numeric)) words++;
            }
            return words;
        }

        /// <summary>
        /// Parses one locale's flat key/value document and registers it. Throws on a malformed
        /// document rather than shipping half a locale: a truncated file is a build problem, and
        /// the offline suite is where it should surface.
        /// </summary>
        public static void Load(string localeCode, string json)
        {
            if (string.IsNullOrEmpty(localeCode)) throw new ArgumentException("A locale needs a code.", "localeCode");
            tables[localeCode] = ParseFlatObject(json, localeCode);
        }

        /// <summary>
        /// Derives the pseudolocale from a loaded source locale. Generated rather than authored so
        /// it can never drift from the keys it is meant to stress.
        /// </summary>
        public static void LoadPseudo(string sourceLocale = DefaultLocale)
        {
            Dictionary<string, string> source;
            if (!tables.TryGetValue(sourceLocale, out source))
                throw new InvalidOperationException("Cannot derive the pseudolocale from unloaded locale " + sourceLocale + ".");
            var table = new Dictionary<string, string>(source.Count, StringComparer.Ordinal);
            foreach (var entry in source) table[entry.Key] = Pseudo(entry.Value);
            tables[PseudoLocale] = table;
        }

        public static bool Has(string localeCode)
        {
            return localeCode != null && tables.ContainsKey(localeCode);
        }

        /// <summary>
        /// Selects a locale. An unknown code falls back to the default rather than failing: a device
        /// reporting a language the game does not ship is ordinary, not an error.
        /// </summary>
        public static void SetLocale(string localeCode)
        {
            locale = Resolve(localeCode) ?? DefaultLocale;
            ApplyNumberRule(locale, localeCode);
        }

        /// <summary>
        /// The best loaded match for a device code: the exact code, then its base language
        /// ("pt-BR" to "pt"), then any loaded regional form of that language ("pt" to "pt-BR"),
        /// then null. Case-insensitive, because platforms disagree about "pt-br" and "pt-BR".
        /// </summary>
        public static string Resolve(string localeCode)
        {
            if (string.IsNullOrEmpty(localeCode)) return null;
            foreach (var key in tables.Keys)
                if (string.Equals(key, localeCode, StringComparison.OrdinalIgnoreCase)) return key;
            int dash = localeCode.IndexOf('-');
            string bare = dash > 0 ? localeCode.Substring(0, dash) : localeCode;
            foreach (var key in tables.Keys)
                if (string.Equals(key, bare, StringComparison.OrdinalIgnoreCase)) return key;
            foreach (var key in tables.Keys)
            {
                int keyDash = key.IndexOf('-');
                string keyBare = keyDash > 0 ? key.Substring(0, keyDash) : key;
                if (string.Equals(keyBare, bare, StringComparison.OrdinalIgnoreCase)) return key;
            }
            return null;
        }

        /// <summary>
        /// Names a catalogue key away from its <see cref="Get"/> call, and does nothing else.
        ///
        /// The catalogue audit finds keys by reading call sites, which is what keeps a translated
        /// string from being paid for forever after its screen is deleted. That works as long as
        /// every key is a literal at the point it is asked for. A surface that picks its key from an
        /// enum breaks the scan, so it declares the literals through this instead, and the audit
        /// keeps working without being weakened to "any string that looks like a key".
        /// </summary>
        public static string Key(string key) { return key; }

        /// <summary>
        /// The copy for a key in the current locale, falling back to the default locale and finally
        /// to a bracketed key. Never null, never empty: an empty label is indistinguishable from a
        /// broken layout, and a bracketed key is obvious in a capture.
        /// </summary>
        public static string Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return "⟦?⟧";
            string value;
            Dictionary<string, string> table;
            if (tables.TryGetValue(locale, out table) && table.TryGetValue(key, out value) && value.Length != 0)
                return value;
            if (!string.Equals(locale, DefaultLocale, StringComparison.OrdinalIgnoreCase) &&
                tables.TryGetValue(DefaultLocale, out table) && table.TryGetValue(key, out value) && value.Length != 0)
                return value;
            missing.Add(key);
            return "⟦" + key + "⟧";
        }

        /// <summary>
        /// The copy for a key with its arguments substituted. A translation that carries the wrong
        /// placeholders degrades to the default locale's template and then to the raw template; it
        /// never throws, because a player should lose formatting, not the screen.
        /// </summary>
        public static string Format(string key, params object[] args)
        {
            string template = Get(key);
            if (args == null || args.Length == 0) return template;
            // A whole number reaches the screen in the locale's grouping however the caller passed it:
            // an int handed straight in used to print "1200" in English beside "1,250" from the
            // pre-formatted call sites, and both in English grouping under Spanish copy.
            args = Localized(args);
            string formatted;
            if (TryFormat(template, args, out formatted)) return formatted;
            malformed.Add(key);
            Dictionary<string, string> table; string fallback;
            if (tables.TryGetValue(DefaultLocale, out table) && table.TryGetValue(key, out fallback) &&
                TryFormat(fallback, args, out formatted)) return formatted;
            return template;
        }

        static bool TryFormat(string template, object[] args, out string formatted)
        {
            try { formatted = string.Format(CultureInfo.CurrentCulture, template, args); return true; }
            catch (FormatException) { formatted = null; return false; }
            catch (ArgumentNullException) { formatted = null; return false; }
        }

        public static IEnumerable<string> Keys(string localeCode)
        {
            Dictionary<string, string> table;
            return tables.TryGetValue(localeCode, out table) ? (IEnumerable<string>)table.Keys : new string[0];
        }

        public static bool TryGetRaw(string localeCode, string key, out string value)
        {
            value = null;
            Dictionary<string, string> table;
            return tables.TryGetValue(localeCode, out table) && table.TryGetValue(key, out value);
        }

        /// <summary>
        /// The set of <c>{0}</c>-style indices a template consumes. The suite compares these across
        /// locales: a translation that drops or invents an index is the single most common way a
        /// localized build breaks, and it is fully detectable before shipping.
        /// </summary>
        public static SortedSet<int> Placeholders(string template)
        {
            var found = new SortedSet<int>();
            if (string.IsNullOrEmpty(template)) return found;
            for (int i = 0; i < template.Length - 1; i++)
            {
                if (template[i] != '{') continue;
                if (template[i + 1] == '{') { i++; continue; }
                int end = i + 1, value = 0, digits = 0;
                while (end < template.Length && template[end] >= '0' && template[end] <= '9')
                { value = value * 10 + (template[end] - '0'); end++; digits++; }
                if (digits == 0) continue;
                // A format or alignment suffix ("{0:N0}", "{0,6}") still consumes index 0.
                while (end < template.Length && template[end] != '}') end++;
                if (end < template.Length) { found.Add(value); i = end; }
            }
            return found;
        }

        /// <summary>
        /// Accents and lengthens a string while leaving placeholders and markup intact, so a
        /// pseudolocalized run stresses layout without breaking substitution.
        /// </summary>
        public static string Pseudo(string source)
        {
            if (string.IsNullOrEmpty(source)) return source;
            var builder = new StringBuilder(source.Length + 8);
            builder.Append('⟬');
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                if (c == '{')
                {
                    // Copy the whole placeholder through untouched.
                    int end = i;
                    while (end < source.Length && source[end] != '}') end++;
                    if (end < source.Length) { builder.Append(source, i, end - i + 1); i = end; continue; }
                }
                builder.Append(Accent(c));
                // Widen vowels to simulate the growth of a longer language without becoming unreadable.
                if ("aeiouAEIOU".IndexOf(c) >= 0) builder.Append(Accent(c));
            }
            builder.Append('⟭');
            return builder.ToString();
        }

        static char Accent(char c)
        {
            switch (c)
            {
                case 'a': return 'á'; case 'e': return 'é'; case 'i': return 'í';
                case 'o': return 'ó'; case 'u': return 'ú'; case 'n': return 'ñ';
                case 'c': return 'ç'; case 's': return 'š'; case 'y': return 'ý';
                case 'A': return 'Á'; case 'E': return 'É'; case 'I': return 'Í';
                case 'O': return 'Ó'; case 'U': return 'Ú'; case 'N': return 'Ñ';
                case 'C': return 'Ç'; case 'S': return 'Š'; case 'Y': return 'Ý';
                default: return c;
            }
        }

        // --- Document parsing -------------------------------------------------------------------

        /// <summary>
        /// A flat {"key": "value"} document. Hand-rolled rather than taken from a serializer so the
        /// offline suites and the player parse locale data through identical code, and so a
        /// malformed file names its own offset.
        /// </summary>
        static Dictionary<string, string> ParseFlatObject(string json, string localeCode)
        {
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            if (json == null) throw new FormatException("Locale " + localeCode + " has no document.");
            int i = SkipWhitespace(json, 0);
            if (i >= json.Length || json[i] != '{')
                throw new FormatException("Locale " + localeCode + " must be a single object.");
            i = SkipWhitespace(json, i + 1);
            if (i < json.Length && json[i] == '}') return table;
            while (i < json.Length)
            {
                i = SkipWhitespace(json, i);
                string key = ReadString(json, ref i, localeCode);
                i = SkipWhitespace(json, i);
                if (i >= json.Length || json[i] != ':')
                    throw new FormatException("Locale " + localeCode + " is missing ':' after key '" + key + "'.");
                i = SkipWhitespace(json, i + 1);
                string value = ReadString(json, ref i, localeCode);
                if (table.ContainsKey(key))
                    throw new FormatException("Locale " + localeCode + " repeats key '" + key + "'.");
                table[key] = value;
                i = SkipWhitespace(json, i);
                if (i < json.Length && json[i] == ',') { i++; continue; }
                if (i < json.Length && json[i] == '}') return table;
                throw new FormatException("Locale " + localeCode + " has a stray character after key '" + key + "'.");
            }
            throw new FormatException("Locale " + localeCode + " ended before its object closed.");
        }

        static int SkipWhitespace(string json, int i)
        {
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++;
            return i;
        }

        static string ReadString(string json, ref int i, string localeCode)
        {
            if (i >= json.Length || json[i] != '"')
                throw new FormatException("Locale " + localeCode + " expected a quoted string at offset " + i + ".");
            i++;
            var builder = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '"') return builder.ToString();
                if (c != '\\') { builder.Append(c); continue; }
                if (i >= json.Length) break;
                char escape = json[i++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (i + 4 > json.Length)
                            throw new FormatException("Locale " + localeCode + " has a truncated \\u escape.");
                        builder.Append((char)int.Parse(json.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default:
                        throw new FormatException("Locale " + localeCode + " has an unsupported escape '\\" + escape + "'.");
                }
            }
            throw new FormatException("Locale " + localeCode + " has an unterminated string.");
        }
    }
}
