using System;
using System.Collections.Generic;
using System.Text;
using Lucene.Net.Analysis.Snowball;
using Lucene.Net.Analysis.Util;
using Lucene.Net.Tartarus.Snowball;
using Lucene.Net.Tartarus.Snowball.Ext;
using Lucene.Net.Util;

namespace Supervertaler.Core.SuperVault
{
    /// <summary>
    /// The word rules EverTM scores with (<see cref="VaultWordRules"/>): the Snowball project's stemmers and stopword
    /// lists, as Lucene.Net.Analysis.Common ships them (Apache-2.0 and BSD; the same Lucene.Net version as the index).
    /// Studio has its own; these are the public ones closest to them. A language without them gets no rules: no stem
    /// matching and no stopword correction.
    /// </summary>
    public static class VaultLanguage
    {
        static readonly Dictionary<string, (string stopFile, Func<SnowballProgram> stemmer)> Known =
            new Dictionary<string, (string, Func<SnowballProgram>)>(StringComparer.Ordinal)
            {
                ["da"] = ("danish_stop.txt", () => new DanishStemmer()),
                ["nl"] = ("dutch_stop.txt", () => new DutchStemmer()),
                ["en"] = ("english_stop.txt", () => new EnglishStemmer()),
                ["fi"] = ("finnish_stop.txt", () => new FinnishStemmer()),
                ["fr"] = ("french_stop.txt", () => new FrenchStemmer()),
                ["de"] = ("german_stop.txt", () => new GermanStemmer()),
                ["hu"] = ("hungarian_stop.txt", () => new HungarianStemmer()),
                ["it"] = ("italian_stop.txt", () => new ItalianStemmer()),
                ["no"] = ("norwegian_stop.txt", () => new NorwegianStemmer()),
                ["nb"] = ("norwegian_stop.txt", () => new NorwegianStemmer()),
                ["nn"] = ("norwegian_stop.txt", () => new NorwegianStemmer()),
                ["pt"] = ("portuguese_stop.txt", () => new PortugueseStemmer()),
                ["ru"] = ("russian_stop.txt", () => new RussianStemmer()),
                ["es"] = ("spanish_stop.txt", () => new SpanishStemmer()),
                ["sv"] = ("swedish_stop.txt", () => new SwedishStemmer()),
            };

        // Stopword lists: read once per language, then only read from (safe from several threads).
        static readonly Dictionary<string, CharArraySet> Stops = new Dictionary<string, CharArraySet>(StringComparer.Ordinal);
        static readonly object Gate = new object();

        /// <summary>
        /// Rules for <paramref name="language"/> ("en", "nl-BE"...), or null when there are none. A new object each call:
        /// its stemmer keeps state, so it is for one lookup on one thread. Never throws: if the lists can't be read, there
        /// are no rules, and scores are at most a little lower.
        /// </summary>
        public static VaultWordRules Rules(string language)
        {
            var lang = VaultMarkup.BaseLanguage(language);
            if (!Known.TryGetValue(lang, out var k)) return null;
            try
            {
                CharArraySet stops;
                lock (Gate)
                {
                    if (!Stops.TryGetValue(k.stopFile, out stops))
                    {
                        using (var reader = IOUtils.GetDecodingReader(typeof(SnowballFilter), k.stopFile, Encoding.UTF8))
                            stops = CharArraySet.UnmodifiableSet(WordlistLoader.GetSnowballWordSet(reader, LuceneVersion.LUCENE_48));
                        Stops[k.stopFile] = stops;
                    }
                }
                var stemmer = k.stemmer();
                var seen = new Dictionary<string, string>(StringComparer.Ordinal);
                return new VaultWordRules
                {
                    Stem = w =>
                    {
                        if (string.IsNullOrEmpty(w)) return "";
                        if (seen.TryGetValue(w, out var s)) return s;
                        stemmer.SetCurrent(w);
                        stemmer.Stem();
                        s = stemmer.Current;
                        if (seen.Count < 10000) seen[w] = s;   // one lookup's words; bounded all the same
                        return s;
                    },
                    IsStopword = w => !string.IsNullOrEmpty(w) && stops.Contains(w),
                };
            }
            catch { return null; }
        }
    }
}
