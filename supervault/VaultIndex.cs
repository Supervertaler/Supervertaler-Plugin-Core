using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Directory = Lucene.Net.Store.Directory;

namespace Supervertaler.Core.SuperVault
{
    /// <summary>A stored unit as the index returns it, oriented to the lookup's direction.</summary>
    public sealed class VaultCandidate
    {
        public string Id;          // the Workbench database row id
        public string SourceRaw;   // text in the lookup's source language, tag codes included
        public string TargetRaw;
        public string Variant;     // the language variants it was stored under, e.g. "nl-BE>en-US"
        public bool Exact;         // found by its source text's hash: the normalised source is identical
    }

    /// <summary>
    /// One built generation of the SuperVault index, opened read-only. Thread-safe: any number of lookups at once.
    ///
    /// One Lucene document per translation unit. Each side is indexed under its base language ("nl", "en"), so every
    /// variant searches alike and either side can be the query side: one unit serves nl-BE>en-US and en-GB>nl-NL.
    /// Per side: <c>w_xx</c> (its words, for candidates), <c>h_xx</c> (hash of its normalised text, for exact
    /// matches) and <c>r_xx</c> (the stored text, tag codes included). <c>lp</c> is the base pair ("en|nl"), so a
    /// lookup never returns a unit from another pair that happens to share a language.
    ///
    /// The texts are stored in the index itself, so a lookup never touches the Workbench database: lookups keep
    /// working while Workbench has it open or locked, and the index is the only thing read at lookup time.
    /// </summary>
    public sealed class VaultIndex : IDisposable
    {
        internal const LuceneVersion V = LuceneVersion.LUCENE_48;
        /// <summary>Bumped whenever the document layout changes; an index of another format is rebuilt, not read.</summary>
        public const int Format = 2;

        // Candidate selection, measured on the real 1.55M-unit Workbench TM and a 9.3M copy (supervault-fuzzy-speed.md):
        // the 8 rarest words find 99 of 100 best matches in 14 ms (1.55M) / 22 ms (9.3M).
        const int RareWords = 8;
        const double CommonShare = 0.01;   // a word in more than 1% of units is too common to pick candidates with
        internal const int CandidateCount = 200;
        // Of those, only the best this many (by Lucene's rare-word score, after the length test) are read from disk:
        // reading a stored unit costs about 0.35 ms (decompression), searching for all 200 about 10 ms in all.
        internal const int LoadCount = 60;

        readonly DirectoryReader _reader;
        readonly IndexSearcher _searcher;
        public string Folder { get; }
        public int Count => _reader.NumDocs;

        VaultIndex(string folder, DirectoryReader reader)
        {
            Folder = folder;
            _reader = reader;
            _searcher = new IndexSearcher(reader);
        }

        /// <summary>Opens a built generation. Throws if it is missing or damaged (the caller reports and rebuilds).</summary>
        public static VaultIndex Open(string folder)
        {
            Directory dir = FSDirectory.Open(folder);
            try { return new VaultIndex(folder, DirectoryReader.Open(dir)); }
            catch { dir.Dispose(); throw; }
        }

        /// <summary>Takes a reference for the length of one lookup. False once the index has been closed.</summary>
        internal bool TryAcquire() => _reader.TryIncRef();
        internal void Release() => _reader.DecRef();

        /// <summary>Drops the opener's reference; the files close once the last lookup in progress has finished.</summary>
        public void Dispose()
        {
            try { _reader.DecRef(); } catch (ObjectDisposedException) { }
        }

        static readonly ISet<string> NoFields = new HashSet<string>();

        /// <summary>
        /// Candidates for a document segment whose plain text is <paramref name="plain"/>: exact matches first (by
        /// hash), then fuzzy candidates found by the segment's rarest words. Scoring is the caller's (<see cref="VaultLookup"/>).
        /// </summary>
        public List<VaultCandidate> Candidates(string plain, string sourceLang, string targetLang, int maxExact, int minScore = 0)
        {
            var result = new List<VaultCandidate>();
            if (string.IsNullOrEmpty(plain) || !TryAcquire()) return result;
            try
            {
                var pair = PairKey(sourceLang, targetLang);
                var seen = new HashSet<int>();
                var exact = new BooleanQuery
                {
                    { new TermQuery(new Term("h_" + sourceLang, VaultMarkup.Hash(plain))), Occur.MUST },
                    { new TermQuery(new Term("lp", pair)), Occur.MUST },
                };
                foreach (var sd in _searcher.Search(exact, Math.Max(1, maxExact)).ScoreDocs)
                    if (seen.Add(sd.Doc)) result.Add(Load(sd.Doc, sourceLang, targetLang, true));

                var words = VaultDiff.Words(plain).Distinct().ToList();
                var chosen = RarestWords(words, "w_" + sourceLang);
                if (chosen.Count == 0) return result;
                var any = new BooleanQuery();
                foreach (var w in chosen) any.Add(new TermQuery(new Term("w_" + sourceLang, w)), Occur.SHOULD);
                var q = new BooleanQuery { { any, Occur.MUST }, { new TermQuery(new Term("lp", pair)), Occur.MUST } };
                // A unit of m words can't score more than min(n,m)/max(n,m) against a segment of n words: skip those that
                // can't reach the minimum without reading them. The word counts are doc values, read without decompression.
                var lengths = WordCounts("n_" + sourceLang);
                int n = Math.Max(1, VaultDiff.Words(plain).Count);
                int loaded = 0;
                foreach (var sd in _searcher.Search(q, CandidateCount).ScoreDocs)
                {
                    if (loaded >= LoadCount) break;
                    if (seen.Contains(sd.Doc)) continue;
                    if (lengths != null && minScore > 0)
                    {
                        long m = Math.Max(1, lengths.Get(sd.Doc));
                        if (100.0 * Math.Min(n, m) / Math.Max(n, m) + 10 < minScore) continue;   // 10 points of slack: n counts distinct words
                    }
                    seen.Add(sd.Doc);
                    result.Add(Load(sd.Doc, sourceLang, targetLang, false));
                    loaded++;
                }
                return result;
            }
            finally { Release(); }
        }

        /// <summary>Concordance: units whose <paramref name="searchLang"/> side contains the words of <paramref name="plain"/>.</summary>
        public List<VaultCandidate> Concordance(string plain, string searchLang, string otherLang, int max)
        {
            var result = new List<VaultCandidate>();
            if (string.IsNullOrEmpty(plain) || !TryAcquire()) return result;
            try
            {
                var words = VaultDiff.Words(plain).Distinct().ToList();
                var chosen = RarestWords(words, "w_" + searchLang);
                if (chosen.Count == 0) return result;
                var any = new BooleanQuery();
                foreach (var w in chosen) any.Add(new TermQuery(new Term("w_" + searchLang, w)), Occur.SHOULD);
                var q = new BooleanQuery { { any, Occur.MUST }, { new TermQuery(new Term("lp", PairKey(searchLang, otherLang))), Occur.MUST } };
                foreach (var sd in _searcher.Search(q, Math.Max(max * 4, 50)).ScoreDocs)
                    result.Add(Load(sd.Doc, searchLang, otherLang, false));
                return result;
            }
            finally { Release(); }
        }

        /// <summary>
        /// Word counts per unit. Fetched per lookup, not kept: a doc-values reader holds a file position, so one instance
        /// must not be shared between threads (Lucene caches one per thread underneath, so this is cheap).
        /// </summary>
        NumericDocValues WordCounts(string field) => MultiDocValues.GetNumericValues(_reader, field);

        HashSet<string> _pairs;

        /// <summary>The base language pairs the index holds, e.g. {"en|nl", "de|nl"}. Read once: a generation never changes.</summary>
        public HashSet<string> Pairs()
        {
            var cached = _pairs;
            if (cached != null) return cached;
            var pairs = new HashSet<string>(StringComparer.Ordinal);
            if (!TryAcquire()) return pairs;
            try
            {
                var terms = MultiFields.GetTerms(_reader, "lp");
                if (terms == null) return pairs;
                var e = terms.GetEnumerator();
                while (e.MoveNext()) pairs.Add(e.Term.Utf8ToString());
                return _pairs = pairs;
            }
            finally { Release(); }
        }

        List<string> RarestWords(List<string> words, string field)
        {
            var withCounts = words.Select(w => (w, df: _reader.DocFreq(new Term(field, w)))).Where(x => x.df > 0).OrderBy(x => x.df).ToList();
            if (withCounts.Count == 0) return new List<string>();
            double limit = Math.Max(1, _reader.NumDocs * CommonShare);
            var chosen = withCounts.Where(x => x.df <= limit).Take(RareWords).Select(x => x.w).ToList();
            // Every word is common ("Figure 1", "and the"): fall back to the least common few.
            return chosen.Count > 0 ? chosen : withCounts.Take(3).Select(x => x.w).ToList();
        }

        VaultCandidate Load(int doc, string sourceLang, string targetLang, bool exact)
        {
            var d = _searcher.Doc(doc);
            return new VaultCandidate { Id = d.Get("id"), SourceRaw = d.Get("r_" + sourceLang), TargetRaw = d.Get("r_" + targetLang), Variant = d.Get("v"), Exact = exact };
        }

        public static string PairKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;

        // ---- writing (used by VaultBuilder only) ----

        static readonly FieldType WordsField = MakeWordsField();
        static FieldType MakeWordsField()
        {
            var ft = new FieldType { IsIndexed = true, IsTokenized = true, IsStored = false, IndexOptions = IndexOptions.DOCS_ONLY, OmitNorms = true };
            ft.Freeze();
            return ft;
        }

        internal static Document MakeDocument(string id, string langA, string rawA, string plainA, string langB, string rawB, string plainB, string variant)
        {
            return new Document
            {
                new StringField("id", id, Field.Store.YES),
                new StringField("lp", PairKey(langA, langB), Field.Store.NO),
                new Field("w_" + langA, new WordStream(VaultDiff.Words(plainA)), WordsField),
                new Field("w_" + langB, new WordStream(VaultDiff.Words(plainB)), WordsField),
                new NumericDocValuesField("n_" + langA, VaultDiff.Words(plainA).Count),
                new NumericDocValuesField("n_" + langB, VaultDiff.Words(plainB).Count),
                new StringField("h_" + langA, VaultMarkup.Hash(plainA), Field.Store.NO),
                new StringField("h_" + langB, VaultMarkup.Hash(plainB), Field.Store.NO),
                new StoredField("r_" + langA, rawA),
                new StoredField("r_" + langB, rawB),
                new StoredField("v", variant ?? ""),
            };
        }

        /// <summary>Feeds already-split words to Lucene, so the index and the lookups split text the same way (VaultDiff.Words).</summary>
        sealed class WordStream : TokenStream
        {
            readonly List<string> _words;
            readonly ICharTermAttribute _term;
            int _i;
            public WordStream(List<string> words) { _words = words; _term = AddAttribute<ICharTermAttribute>(); }
            public override bool IncrementToken()
            {
                ClearAttributes();
                if (_i >= _words.Count) return false;
                var w = _words[_i++];
                if (w.Length > 255) w = w.Substring(0, 255);
                _term.SetEmpty().Append(w);
                return true;
            }
            public override void Reset() { base.Reset(); _i = 0; }
        }

        /// <summary>IndexWriter wants an analyzer; every text field is pre-split (WordStream), so this one is never asked for words.</summary>
        internal sealed class NoAnalyzer : Analyzer
        {
            protected override TokenStreamComponents CreateComponents(string fieldName, TextReader reader) =>
                throw new NotSupportedException("SuperVault indexes pre-split words only.");
        }
    }
}
