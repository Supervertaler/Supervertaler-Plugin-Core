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

        // Metadata (index format 3; null or empty in a format-2 index and for units just confirmed).
        public List<(string Field, string Value)> Fields = new List<(string, string)>();
        public DateTime? Created, Changed;   // UTC; null: not known
        public string CreatedBy, ChangedBy;
        public DateTime? Added;              // when it entered EverTM
        /// <summary>Other translations of the same source that newest-wins hid (distinct target texts).</summary>
        public int Older;

        // For choosing the newest; read from doc values, never from the stored text.
        internal long Date, UnitId, SourceHash, TargetHash;
        internal bool VariantMatch;
    }

    /// <summary>A unit's metadata as the store gives it to the index (<see cref="VaultStore.IndexColumns"/>).</summary>
    internal sealed class VaultIndexMeta
    {
        public long? Created, Changed;       // seconds since 1970, UTC
        public string CreatedBy, ChangedBy, Added;
        public List<(string Field, string Value)> Fields = new List<(string, string)>();

        /// <summary>The date "newest" is decided by: changed, else created; 0 when the unit has none, so undated units are older than any dated one.</summary>
        public long Date => Changed ?? Created ?? 0;
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
    ///
    /// Format 3 adds, per unit, what choosing the newest translation needs, as doc values (a few bytes per unit, read
    /// without decompressing anything): <c>d</c> (its date, 0 if none), <c>u</c> (its store id: imported later = higher),
    /// <c>t_xx</c> (a 64-bit hash of each side's normalised text) and <c>c_xx</c> (each side's language variant,
    /// "en-gb"). Its metadata for display is one stored field, <c>m</c>, read only for the units shown.
    ///
    /// One translation per source, newest wins (design note section 3): of the units whose source side is the same
    /// text (one exact-match group), a lookup offers only the translation of the best unit: the project's exact target
    /// variant first, then the newest date, then the one imported last. Hiding happens here, at lookup, so nothing is
    /// deleted and both directions keep their own newest.
    /// </summary>
    public sealed class VaultIndex : IDisposable
    {
        internal const LuceneVersion V = LuceneVersion.LUCENE_48;
        /// <summary>Bumped whenever the document layout changes; an index of another format is rebuilt, not read.</summary>
        public const int Format = 3;
        /// <summary>An index of this format is still searched (without newest-wins or metadata) until the automatic rebuild replaces it.</summary>
        public const int OldestReadable = 2;

        /// <summary>Exact-match groups larger than this are cut there (the newest of the first this-many units wins). Measured groups: up to 474 in 871k units.</summary>
        internal const int MaxGroup = 20000;

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

        /// <summary>Commit data key: the last store change (<c>changes.seq</c>) this index has applied.</summary>
        internal const string SeqKey = "store_seq";

        /// <summary>The last store change this index has applied; 0 for an index built before learning existed.</summary>
        public long StoreSeq { get; }
        /// <summary>False for an index built by stage 1, which records no store change.</summary>
        public bool HasStoreSeq { get; }

        VaultIndex(string folder, DirectoryReader reader)
        {
            Folder = folder;
            _reader = reader;
            _searcher = new IndexSearcher(reader);
            StoreSeq = ReadSeq(reader.IndexCommit.UserData, out bool has);
            HasStoreSeq = has;
        }

        internal static long ReadSeq(IDictionary<string, string> data, out bool has)
        {
            has = data != null && data.TryGetValue(SeqKey, out var v) && long.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);
            return has ? long.Parse(data[SeqKey], System.Globalization.CultureInfo.InvariantCulture) : 0;
        }

        /// <summary>Opens a built generation. Throws if it is missing or damaged (the caller reports and rebuilds).</summary>
        public static VaultIndex Open(string folder)
        {
            Directory dir = FSDirectory.Open(folder);
            try { return new VaultIndex(folder, DirectoryReader.Open(dir)); }
            catch { dir.Dispose(); throw; }
        }

        /// <summary>
        /// The same generation with the units committed since this one was opened (learned or imported a piece at a
        /// time), or null if nothing changed. Cheap: Lucene reads the commit point and reopens only the new parts;
        /// segments both share are shared, not read again. Throws if the generation can't be read.
        /// </summary>
        internal VaultIndex Reopen()
        {
            if (!TryAcquire()) return null;
            try
            {
                var newer = DirectoryReader.OpenIfChanged(_reader);
                return newer == null ? null : new VaultIndex(Folder, newer);
            }
            finally { Release(); }
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
        /// <param name="targetCulture">The project's target language with its variant ("en-GB"): units in that exact variant win first. Null or a bare language: no preference.</param>
        public List<VaultCandidate> Candidates(string plain, string sourceLang, string targetLang, int maxExact, int minScore = 0, string targetCulture = null)
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
                var dv = Values.For(this, sourceLang, targetLang, targetCulture);
                if (dv == null)   // a format-2 index: every translation, as before
                {
                    foreach (var sd in _searcher.Search(exact, Math.Max(1, maxExact)).ScoreDocs)
                        if (seen.Add(sd.Doc)) result.Add(Load(sd.Doc, sourceLang, targetLang, true, null));
                }
                else
                {
                    // The whole group, from doc values only; then the stored text of the winning translation's units
                    // (formatting variants of it: Search picks the one whose tags fit the document).
                    var docs = new AllDocs(MaxGroup);
                    _searcher.Search(exact, docs);   // no scores and no top-N queue: a group of thousands costs microseconds each
                    var group = docs.Docs.Select(dv.Read).ToList();
                    foreach (var g in group) seen.Add(g.doc);
                    if (group.Count > 0)
                    {
                        var best = group.OrderByDescending(g => g.c, Ranking).First().c;
                        int older = group.Select(g => g.c.TargetHash).Distinct().Count() - 1;
                        foreach (var g in group.Where(g => g.c.TargetHash == best.TargetHash).OrderByDescending(g => g.c, Ranking).Take(Math.Max(1, maxExact)))
                        {
                            var c = Load(g.doc, sourceLang, targetLang, true, g.c);
                            c.Older = older;
                            result.Add(c);
                        }
                    }
                }

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
                var fuzzy = new List<VaultCandidate>();
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
                    fuzzy.Add(Load(sd.Doc, sourceLang, targetLang, false, dv?.Read(sd.Doc).c));
                    loaded++;
                }
                result.AddRange(dv == null ? fuzzy : NewestPerSource(fuzzy));
                return result;
            }
            finally { Release(); }
        }

        /// <summary>Concordance: units whose <paramref name="searchLang"/> side contains the words of <paramref name="plain"/>.</summary>
        public List<VaultCandidate> Concordance(string plain, string searchLang, string otherLang, int max, string otherCulture = null)
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
                var dv = Values.For(this, searchLang, otherLang, otherCulture);
                foreach (var sd in _searcher.Search(q, Math.Max(max * 4, 50)).ScoreDocs)
                    result.Add(Load(sd.Doc, searchLang, otherLang, false, dv?.Read(sd.Doc).c));
                return dv == null ? result : NewestPerSource(result);
            }
            finally { Release(); }
        }

        /// <summary>
        /// Word counts per unit. Fetched per lookup, not kept: a doc-values reader holds a file position, so one instance
        /// must not be shared between threads (Lucene caches one per thread underneath, so this is cheap).
        /// </summary>
        NumericDocValues WordCounts(string field) => MultiDocValues.GetNumericValues(_reader, field);

        HashSet<string> _pairs;

        /// <summary>The base language pairs the index holds, e.g. {"en|nl", "de|nl"}. Read once per reader: new units come with a new <see cref="VaultIndex"/> (<see cref="Reopen"/>).</summary>
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

        VaultCandidate Load(int doc, string sourceLang, string targetLang, bool exact, VaultCandidate ranked)
        {
            var d = _searcher.Doc(doc);
            var c = ranked ?? new VaultCandidate();
            c.Id = d.Get("id"); c.SourceRaw = d.Get("r_" + sourceLang); c.TargetRaw = d.Get("r_" + targetLang); c.Variant = d.Get("v"); c.Exact = exact;
            var m = d.Get("m");
            if (m != null)
                foreach (var (f, v) in VaultFields.Unpack(m))
                {
                    if (f == "@created") c.Created = Time(v);
                    else if (f == "@changed") c.Changed = Time(v);
                    else if (f == "@createdBy") c.CreatedBy = v;
                    else if (f == "@changedBy") c.ChangedBy = v;
                    else if (f == "@added") c.Added = DateTime.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : (DateTime?)null;
                    else c.Fields.Add((f, v));
                }
            return c;
        }

        static DateTime? Time(string seconds) =>
            long.TryParse(seconds, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var s) ? VaultFields.FromSeconds(s) : (DateTime?)null;

        /// <summary>Best first: the exact target variant, then the newest date, then the one imported last (higher store id).</summary>
        static readonly IComparer<VaultCandidate> Ranking = Comparer<VaultCandidate>.Create((x, y) =>
        {
            int c = x.VariantMatch.CompareTo(y.VariantMatch);
            if (c == 0) c = x.Date.CompareTo(y.Date);
            return c != 0 ? c : x.UnitId.CompareTo(y.UnitId);
        });

        /// <summary>
        /// Among candidates already loaded (fuzzy, concordance): per source text, only the best unit's translation (its
        /// formatting variants kept); the others count as older translations. Order otherwise kept.
        /// </summary>
        internal static List<VaultCandidate> NewestPerSource(List<VaultCandidate> list)
        {
            var winner = new Dictionary<long, VaultCandidate>();
            foreach (var c in list)
                if (!winner.TryGetValue(c.SourceHash, out var w) || Ranking.Compare(c, w) > 0) winner[c.SourceHash] = c;
            var older = list.GroupBy(c => c.SourceHash).ToDictionary(g => g.Key, g => g.Select(c => c.TargetHash).Distinct().Count() - 1);
            var kept = new List<VaultCandidate>();
            foreach (var c in list)
                if (c.TargetHash == winner[c.SourceHash].TargetHash) { c.Older = Math.Max(c.Older, older[c.SourceHash]); kept.Add(c); }
            return kept;
        }

        /// <summary>Collects matching documents (top-level ids), up to a limit, in index order.</summary>
        sealed class AllDocs : ICollector
        {
            readonly int _max;
            int _base;
            public readonly List<int> Docs = new List<int>();
            public AllDocs(int max) { _max = max; }
            public void SetScorer(Scorer scorer) { }
            public void Collect(int doc) { if (Docs.Count < _max) Docs.Add(_base + doc); }
            public void SetNextReader(AtomicReaderContext context) { _base = context.DocBase; }
            public bool AcceptsDocsOutOfOrder => true;
        }

        /// <summary>The doc values one lookup reads to rank units. Fetched per lookup, like <see cref="WordCounts"/>: not shareable between threads.</summary>
        sealed class Values
        {
            NumericDocValues _date, _id, _src, _trg;
            SortedDocValues _variant;
            string _culture;
            readonly BytesRef _bytes = new BytesRef();

            /// <summary>Null for an index without them (format 2).</summary>
            public static Values For(VaultIndex index, string sourceLang, string targetLang, string targetCulture)
            {
                var v = new Values
                {
                    _date = MultiDocValues.GetNumericValues(index._reader, "d"),
                    _id = MultiDocValues.GetNumericValues(index._reader, "u"),
                    _src = MultiDocValues.GetNumericValues(index._reader, "t_" + sourceLang),
                    _trg = MultiDocValues.GetNumericValues(index._reader, "t_" + targetLang),
                };
                if (v._date == null || v._id == null || v._src == null || v._trg == null) return null;
                var culture = NormalCulture(targetCulture);
                if (culture != null && culture != VaultMarkup.BaseLanguage(culture))   // a variant was asked for
                {
                    v._variant = MultiDocValues.GetSortedValues(index._reader, "c_" + targetLang);
                    v._culture = culture;
                }
                return v;
            }

            public (int doc, VaultCandidate c) Read(int doc)
            {
                var c = new VaultCandidate { Date = _date.Get(doc), UnitId = _id.Get(doc), SourceHash = _src.Get(doc), TargetHash = _trg.Get(doc) };
                if (_variant != null)
                {
                    _variant.Get(doc, _bytes);
                    c.VariantMatch = _bytes.Length > 0 && _bytes.Utf8ToString() == _culture;
                }
                return (doc, c);
            }
        }

        /// <summary>"en_GB" -> "en-gb"; null for none.</summary>
        internal static string NormalCulture(string culture)
        {
            var c = culture?.Trim().Replace('_', '-').ToLowerInvariant();
            return string.IsNullOrEmpty(c) ? null : c;
        }

        /// <summary>64 bits of the MD5 of a normalised text (<see cref="VaultMarkup.Hash"/>): which units have the same text.</summary>
        internal static long Hash64(string md5Hex) => unchecked((long)ulong.Parse(md5Hex.Substring(0, 16), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));

        public static string PairKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;

        // ---- writing (VaultLibrary: full builds and piece-at-a-time updates) ----

        static readonly FieldType WordsField = MakeWordsField();
        static FieldType MakeWordsField()
        {
            var ft = new FieldType { IsIndexed = true, IsTokenized = true, IsStored = false, IndexOptions = IndexOptions.DOCS_ONLY, OmitNorms = true };
            ft.Freeze();
            return ft;
        }

        internal static Document MakeDocument(string id, string langA, string rawA, string plainA, string langB, string rawB, string plainB, string variant, VaultIndexMeta meta = null)
        {
            string hashA = VaultMarkup.Hash(plainA), hashB = VaultMarkup.Hash(plainB);
            var doc = MakeTextDocument(id, langA, rawA, plainA, hashA, langB, rawB, plainB, hashB, variant);
            meta = meta ?? new VaultIndexMeta();
            doc.Add(new NumericDocValuesField("d", meta.Date));
            doc.Add(new NumericDocValuesField("u", long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0));
            doc.Add(new NumericDocValuesField("t_" + langA, Hash64(hashA)));
            doc.Add(new NumericDocValuesField("t_" + langB, Hash64(hashB)));
            var (ca, cb) = SideCultures(langA, langB, variant);
            doc.Add(new SortedDocValuesField("c_" + langA, new BytesRef(ca ?? "")));
            doc.Add(new SortedDocValuesField("c_" + langB, new BytesRef(cb ?? "")));
            var shown = new List<(string, string)>(meta.Fields);
            if (meta.Created != null) shown.Add(("@created", meta.Created.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (meta.Changed != null) shown.Add(("@changed", meta.Changed.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (meta.CreatedBy != null) shown.Add(("@createdBy", meta.CreatedBy));
            if (meta.ChangedBy != null) shown.Add(("@changedBy", meta.ChangedBy));
            if (meta.Added != null) shown.Add(("@added", meta.Added));
            if (shown.Count > 0) doc.Add(new StoredField("m", VaultFields.Pack(shown)));
            return doc;
        }

        /// <summary>The variant of each side ("nl-be", "en-us") from the unit's variant pair, whichever way round it was stored; null if unknown.</summary>
        internal static (string a, string b) SideCultures(string langA, string langB, string variant)
        {
            int gt = variant?.IndexOf('>') ?? -1;
            if (gt < 0) return (null, null);
            string x = NormalCulture(variant.Substring(0, gt)), y = NormalCulture(variant.Substring(gt + 1));
            if (x == null || y == null) return (null, null);
            if (VaultMarkup.BaseLanguage(x) == langA && VaultMarkup.BaseLanguage(y) == langB) return (x, y);
            if (VaultMarkup.BaseLanguage(x) == langB && VaultMarkup.BaseLanguage(y) == langA) return (y, x);
            return (null, null);
        }

        static Document MakeTextDocument(string id, string langA, string rawA, string plainA, string hashA, string langB, string rawB, string plainB, string hashB, string variant)
        {
            return new Document
            {
                new StringField("id", id, Field.Store.YES),
                new StringField("lp", PairKey(langA, langB), Field.Store.NO),
                new Field("w_" + langA, new WordStream(VaultDiff.Words(plainA)), WordsField),
                new Field("w_" + langB, new WordStream(VaultDiff.Words(plainB)), WordsField),
                new NumericDocValuesField("n_" + langA, VaultDiff.Words(plainA).Count),
                new NumericDocValuesField("n_" + langB, VaultDiff.Words(plainB).Count),
                new StringField("h_" + langA, hashA, Field.Store.NO),
                new StringField("h_" + langB, hashB, Field.Store.NO),
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
                throw new NotSupportedException("EverTM indexes pre-split words only.");
        }
    }
}
