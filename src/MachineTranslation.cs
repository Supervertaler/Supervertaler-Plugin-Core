using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Supervertaler.Core
{
    /// <summary>Why an engine request did not give translations.</summary>
    public enum MtFailureKind
    {
        /// <summary>The key was refused, or is missing.</summary>
        BadKey,
        /// <summary>The key's character allowance is used up.</summary>
        QuotaUsedUp,
        /// <summary>The engine does not translate this language pair.</summary>
        UnsupportedLanguage,
        /// <summary>Too many requests, still after the retries.</summary>
        RateLimited,
        /// <summary>A server error, still after the retry.</summary>
        ServerError,
        /// <summary>No answer: no connection, or the request timed out.</summary>
        Network,
        /// <summary>Any other refusal; the engine's own words say why.</summary>
        Refused,
        /// <summary>A reply that could not be read, or one with a different number of translations than was sent.</summary>
        Malformed,
    }

    /// <summary>One failed request: what kind, the engine's own words, and the sentence for the user.</summary>
    public sealed class MtFailure
    {
        public MtFailureKind Kind { get; internal set; }

        /// <summary>The HTTP status, or 0 when there was no reply.</summary>
        public int HttpStatus { get; internal set; }

        /// <summary>What the engine said, never holding the key; for the log.</summary>
        public string EngineMessage { get; internal set; }

        /// <summary>The sentence for the user.</summary>
        public string Message { get; internal set; }

        /// <summary>
        /// A bad key, a used-up allowance or an unsupported pair is the same for every
        /// request that would follow, so the run stops rather than send each one to be
        /// refused in turn.
        /// </summary>
        public bool StopsRun => Kind == MtFailureKind.BadKey || Kind == MtFailureKind.QuotaUsedUp
                             || Kind == MtFailureKind.UnsupportedLanguage;
    }

    /// <summary>What to translate, with which engine.</summary>
    public sealed class MtJob
    {
        /// <summary><see cref="MachineTranslation.DeepL"/>, <see cref="MachineTranslation.Google"/> or <see cref="MachineTranslation.Microsoft"/>.</summary>
        public string Engine { get; set; }
        public string ApiKey { get; set; }

        /// <summary>Microsoft only: the resource's region; empty for a Global resource.</summary>
        public string Region { get; set; }

        /// <summary>As the CAT tool names them, BCP 47: "en-GB", "nl-NL", "zh-Hant-TW".</summary>
        public string SourceLanguage { get; set; }
        public string TargetLanguage { get; set; }

        /// <summary>One entry per segment, inline tags as &lt;t1&gt;…&lt;/t1&gt; and &lt;t2/&gt;.</summary>
        public IList<string> Texts { get; set; }

        /// <summary>
        /// DeepL only: the context for the request carrying segments
        /// [start, start + count), or null for none. DeepL reads it and does not
        /// charge for it (docs/design/mt-engines.md, decision 2).
        /// </summary>
        public Func<int, int, string> ContextFor { get; set; }

        /// <summary>Where the user enters keys, for the bad-key sentence: "AI Settings".</summary>
        public string SettingsPlace { get; set; }
    }

    /// <summary>What a run gave.</summary>
    public sealed class MtOutcome
    {
        /// <summary>One entry per segment, index-aligned with <see cref="MtJob.Texts"/>; null where none came back.</summary>
        public string[] Translations { get; internal set; }

        /// <summary>Characters charged: as the engine reported them, or by its published rule (<see cref="BilledComputed"/>).</summary>
        public int BilledCharacters { get; internal set; }

        /// <summary>True when <see cref="BilledCharacters"/> was counted by the engine's published rule, not reported by it (Google).</summary>
        public bool BilledComputed { get; internal set; }

        /// <summary><see cref="BilledCharacters"/> at the list price, US dollars.</summary>
        public decimal Cost { get; internal set; }

        /// <summary>The language codes the engine was actually sent, for the report.</summary>
        public string SourceCodeSent { get; internal set; }
        public string TargetCodeSent { get; internal set; }

        /// <summary>The failure that stopped the run, or the last one; null when every request succeeded.</summary>
        public MtFailure Failure { get; internal set; }

        /// <summary>True when the run stopped before sending every segment.</summary>
        public bool Stopped { get; internal set; }

        public bool Cancelled { get; internal set; }

        /// <summary>HTTP requests made, retries included.</summary>
        public int Requests { get; internal set; }
    }

    /// <summary>
    /// Classic machine-translation engines - DeepL, Google Cloud Translation (Basic,
    /// v2) and Microsoft Translator (v3.0) - for SuperBench, and for Batch Translate
    /// should that follow (docs/design/mt-engines.md in the Trados repository). HTTP
    /// and JSON only, nothing of Trados or memoQ, so either plugin can use it.
    ///
    /// <para>One static class: three engines that differ in the shape of a request
    /// and a reply, chosen by a switch on the engine id. A run is split into
    /// requests (<see cref="Chunks"/>); each is retried when the engine is busy or
    /// briefly down, and the run stops at the first bad key, used-up allowance or
    /// unsupported pair, which every later request would meet too. Nothing here
    /// ever returns an engine's error text as a translation.</para>
    ///
    /// <para>Keys come from the caller, which reads them through
    /// <see cref="ApiKeyStore"/> under the ids below - the ids Sidekick already
    /// stores its machine-translation keys under. They travel in headers, never in
    /// a URL, and are taken out of anything an engine says before it is kept.</para>
    /// </summary>
    public static class MachineTranslation
    {
        public const string DeepL = "deepl";
        public const string Google = "google";
        public const string Microsoft = "microsoft";

        public static readonly IReadOnlyList<string> Engines = new[] { DeepL, Google, Microsoft };

        /// <summary>True for an engine id; never for an AI provider's.</summary>
        public static bool IsEngine(string id) =>
            id != null && Engines.Contains(id.Trim().ToLowerInvariant());

        public static string DisplayName(string id)
        {
            switch ((id ?? "").Trim().ToLowerInvariant())
            {
                case DeepL: return "DeepL";
                case Google: return "Google Translate";
                case Microsoft: return "Microsoft Translator";
                default: return id;
            }
        }

        /// <summary>
        /// The model the engine is asked for, for the usage log: DeepL's best model
        /// where the pair has one (never an error where it has not); the others' standard NMT.
        /// </summary>
        public static string ModelName(string engine) => engine == DeepL ? DeepLModel : "nmt";

        private const string DeepLModel = "prefer_quality_optimized";

        // ─── Price ─────────────────────────────────────────────────────────────────

        /// <summary>US dollars per million characters at list price (<see cref="PricingTable.MtPerMillion"/>).</summary>
        public static decimal ListPricePerMillion(string engine) => PricingTable.MtPerMillion(engine);

        public static decimal Cost(string engine, int characters) =>
            characters <= 0 ? 0m : characters * ListPricePerMillion(engine) / 1_000_000m;

        /// <summary>
        /// The characters a text costs as sent: its code points, markup included -
        /// Google's published rule, and Microsoft's. DeepL charges the text without
        /// its tags (on 1 Nov 2026 it billed 607 characters for 812 sent, 607 being
        /// the escaped text alone). Used for the estimate before a run, and for
        /// Google's count after one.
        /// </summary>
        public static int CharactersAsSent(string engine, string text)
        {
            var sent = MtMarkup.ToEngine(text, NotationOf(engine), out _);
            return CodePoints(engine == DeepL ? MtMarkup.PlainText(sent) : sent);
        }

        private static int CodePoints(string s)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                n++;
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            }
            return n;
        }

        // ─── Language codes ────────────────────────────────────────────────────────

        // Regions where Spanish is the Latin American kind, for DeepL's ES-419.
        private static readonly HashSet<string> LatinAmerica = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "419", "AR", "BO", "CL", "CO", "CR", "CU", "DO", "EC", "GT", "HN", "MX", "NI", "PA", "PE", "PR", "PY", "SV", "US", "UY", "VE",
        };

        /// <summary>The source-language code an engine wants for a BCP 47 tag.</summary>
        public static string SourceCode(string engine, string language)
        {
            var (lang, script, region) = Parts(language);
            switch (engine)
            {
                case DeepL:
                    // DeepL's sources carry no variant.
                    return (IsNorwegian(lang) ? "nb" : lang).ToUpperInvariant();
                default:
                    return TargetCode(engine, language);
            }
        }

        /// <summary>
        /// The target-language code an engine wants for a BCP 47 tag
        /// (docs/design/mt-engines.md §3.2). Variants are checked against each
        /// engine's own language list, recorded 1 Nov 2026; an unknown language goes
        /// as its base code and the engine's refusal says so.
        /// </summary>
        public static string TargetCode(string engine, string language)
        {
            var (lang, script, region) = Parts(language);
            bool traditional = script == "hant" || region == "TW" || region == "HK" || region == "MO";
            switch (engine)
            {
                case DeepL:
                    if (lang == "en") return region == "US" ? "EN-US" : "EN-GB";
                    if (lang == "pt") return region == "BR" ? "PT-BR" : "PT-PT";
                    if (lang == "zh") return traditional ? "ZH-HANT" : "ZH-HANS";
                    if (lang == "es") return region != null && LatinAmerica.Contains(region) ? "ES-419" : "ES";
                    if (lang == "fr" && region == "CA") return "FR-CA";
                    // Swiss German writes "ss" for "ß"; Liechtenstein does the same.
                    if (lang == "de" && (region == "CH" || region == "LI")) return "DE-CH";
                    if (IsNorwegian(lang)) return "NB";
                    return lang.ToUpperInvariant();

                case Google:
                    if (lang == "zh") return traditional ? "zh-TW" : "zh-CN";
                    if (lang == "pt") return region == null || region == "BR" ? "pt" : "pt-PT";
                    if (lang == "fr" && region == "CA") return "fr-CA";
                    if (IsNorwegian(lang)) return "no";
                    return lang;   // Filipino: core's table already says "tl", as Google does

                case Microsoft:
                    if (lang == "tl") return "fil";   // core's table turns "fil" into "tl"; Microsoft lists "fil"
                    if (lang == "zh") return traditional ? "zh-Hant" : "zh-Hans";
                    if (lang == "pt") return region == null || region == "BR" ? "pt" : "pt-PT";
                    if (lang == "fr" && region == "CA") return "fr-CA";
                    if (lang == "es" && region == "MX") return "es-MX";
                    if (IsNorwegian(lang)) return "nb";
                    if (lang == "sr") return script == "latn" ? "sr-Latn" : "sr-Cyrl";
                    return lang;

                default:
                    return lang;
            }
        }

        private static bool IsNorwegian(string lang) => lang == "nb" || lang == "nn" || lang == "no";

        /// <summary>"zh-Hant-TW" → ("zh", "hant", "TW"); "es-419" → ("es", null, "419").</summary>
        private static (string lang, string script, string region) Parts(string language)
        {
            var bits = (language ?? "").Trim().Replace('_', '-').Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries);
            var lang = bits.Length > 0 ? LanguageCodes.Normalise(bits[0]) : "";
            string script = null, region = null;
            for (int i = 1; i < bits.Length; i++)
            {
                if (bits[i].Length == 4 && script == null) script = bits[i].ToLowerInvariant();
                else if ((bits[i].Length == 2 || bits[i].Length == 3) && region == null) region = bits[i].ToUpperInvariant();
            }
            return (lang, script, region);
        }

        private static string LanguageName(string language)
        {
            try { return CultureInfo.GetCultureInfo(language).EnglishName; }
            catch { return string.IsNullOrWhiteSpace(language) ? "(unknown)" : language; }
        }

        // ─── Requests ──────────────────────────────────────────────────────────────

        internal static MtMarkup.Notation NotationOf(string engine) =>
            engine == DeepL ? MtMarkup.Notation.Xml : MtMarkup.Notation.Html;

        /// <summary>
        /// The run split into requests: at most 50 segments each - 10 for DeepL with
        /// context, since every request carries its own - and no more characters
        /// than the engine takes in one (DeepL "128 KiB" a request; Google
        /// "recommends" 5,000 code points; Microsoft 50,000 characters). A segment
        /// longer than that goes on its own, and the engine says if it is too long.
        /// </summary>
        public static List<(int Start, int Count)> Chunks(IList<string> sentTexts, string engine, bool withContext)
        {
            int maxCount = engine == DeepL && withContext ? 10 : 50;
            int maxChars = engine == DeepL ? 30_000 : engine == Google ? 5_000 : 45_000;
            var chunks = new List<(int, int)>();
            int start = 0, count = 0, chars = 0;
            for (int i = 0; i < sentTexts.Count; i++)
            {
                int len = (sentTexts[i] ?? "").Length;
                if (count > 0 && (count == maxCount || chars + len > maxChars))
                {
                    chunks.Add((start, count));
                    start = i; count = 0; chars = 0;
                }
                count++;
                chars += len;
            }
            if (count > 0) chunks.Add((start, count));
            return chunks;
        }

        /// <summary>
        /// DeepL's context for a request: the request's own segments and their
        /// neighbours on both sides, tags left out, up to <paramref name="maxChars"/>.
        /// DeepL translates each text of a request on its own; the context is how
        /// each one sees the sentences around it. Not charged.
        /// </summary>
        public static string ContextAround(IList<string> sources, int start, int count, int maxChars = 5_000)
        {
            if (sources == null || sources.Count == 0) return null;
            var picked = new SortedDictionary<int, string>();
            int used = 0;
            bool Take(int i)
            {
                if (i < 0 || i >= sources.Count) return false;
                var t = MtMarkup.PlainText(sources[i]).Trim();
                if (t.Length == 0) return true;
                if (used + t.Length + 1 > maxChars) return false;
                picked[i] = t;
                used += t.Length + 1;
                return true;
            }
            for (int i = start; i < start + count && i < sources.Count; i++)
                if (!Take(i)) break;
            for (int d = 1; ; d++)
            {
                bool before = start - d >= 0, after = start + count - 1 + d < sources.Count;
                if (!before && !after) break;
                bool took = false;
                if (before) took |= Take(start - d);
                if (after) took |= Take(start + count - 1 + d);
                if (!took) break;
            }
            return picked.Count == 0 ? null : string.Join("\n", picked.Values);
        }

        // ─── The run ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Translates every segment of <paramref name="job"/>, request by request.
        /// <paramref name="chunkDone"/> is called as each request's translations come
        /// back - (first segment, its translations; null where none) - so a caller can
        /// write them at once. A request that fails is skipped and the run goes on,
        /// unless the failure stops the run (<see cref="MtFailure.StopsRun"/>) or three
        /// requests in a row have failed. Never throws for an engine's refusal or for
        /// cancellation: the outcome says what happened, with what was charged so far.
        /// </summary>
        public static async Task<MtOutcome> TranslateAsync(MtJob job, Action<int, string[]> chunkDone, CancellationToken ct)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            var engine = (job.Engine ?? "").Trim().ToLowerInvariant();
            if (!IsEngine(engine)) throw new ArgumentException("Not a machine-translation engine: " + job.Engine, nameof(job));

            var texts = job.Texts ?? new string[0];
            var outcome = new MtOutcome
            {
                Translations = new string[texts.Count],
                SourceCodeSent = SourceCode(engine, job.SourceLanguage),
                TargetCodeSent = TargetCode(engine, job.TargetLanguage),
                BilledComputed = engine == Google,
            };
            if (texts.Count == 0) return outcome;

            var place = string.IsNullOrWhiteSpace(job.SettingsPlace) ? "the settings" : job.SettingsPlace.Trim();
            if (string.IsNullOrWhiteSpace(job.ApiKey))
            {
                outcome.Failure = new MtFailure
                {
                    Kind = MtFailureKind.BadKey,
                    Message = "No API key set for " + DisplayName(engine) + ". Add one in " + place + ".",
                };
                outcome.Stopped = true;
                return outcome;
            }

            var notation = NotationOf(engine);
            var sent = texts.Select(t => MtMarkup.ToEngine(t, notation, out _)).ToArray();
            var request = new Request
            {
                Engine = engine,
                Key = job.ApiKey.Trim(),
                Region = (job.Region ?? "").Trim(),
                Source = outcome.SourceCodeSent,
                Target = outcome.TargetCodeSent,
                SourceName = LanguageName(job.SourceLanguage),
                TargetName = LanguageName(job.TargetLanguage),
                Place = place,
            };

            int failedInARow = 0;
            foreach (var (start, count) in Chunks(sent, engine, engine == DeepL && job.ContextFor != null))
            {
                if (ct.IsCancellationRequested) { outcome.Cancelled = true; break; }
                request.Texts = new ArraySegment<string>(sent, start, count);
                request.Context = engine == DeepL ? job.ContextFor?.Invoke(start, count) : null;

                Reply reply;
                try
                {
                    reply = await SendWithRetriesAsync(request, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    outcome.Cancelled = true;
                    break;
                }
                outcome.Requests += reply.Attempts;

                if (reply.Failure != null)
                {
                    outcome.Failure = reply.Failure;
                    if (reply.Failure.StopsRun || ++failedInARow >= 3) { outcome.Stopped = true; break; }
                    continue;
                }

                failedInARow = 0;
                outcome.BilledCharacters += reply.Billed;
                var chunk = new string[count];
                for (int i = 0; i < count; i++)
                {
                    var back = MtMarkup.FromEngine(reply.Texts[i], notation, texts[start + i]);
                    chunk[i] = string.IsNullOrWhiteSpace(back) ? null : back;
                    outcome.Translations[start + i] = chunk[i];
                }
                chunkDone?.Invoke(start, chunk);
            }

            if (outcome.Stopped && outcome.Failure != null && !outcome.Failure.StopsRun)
                outcome.Failure.Message += " Stopped after three requests in a row failed.";
            outcome.Cost = Cost(engine, outcome.BilledCharacters);
            return outcome;
        }

        /// <summary>
        /// One check of a key, for a settings window's Test button: null when the
        /// engine accepts it, else why not. DeepL is asked for its usage (free, and
        /// <paramref name="info"/> then says how much of the allowance is used); the
        /// others translate one word, "Test", which costs four characters.
        /// </summary>
        public static async Task<MtFailure> CheckKeyAsync(string engine, string key, string region, string settingsPlace,
            CancellationToken ct, Action<string> info = null)
        {
            engine = (engine ?? "").Trim().ToLowerInvariant();
            if (engine == DeepL && !string.IsNullOrWhiteSpace(key))
            {
                var r = new Request { Engine = DeepL, Key = key.Trim(), Place = settingsPlace ?? "the settings", Usage = true };
                var reply = await SendOnceAsync(r, ct).ConfigureAwait(false);
                if (reply.Failure == null) info?.Invoke(reply.UsageLine);
                return reply.Failure;
            }
            var outcome = await TranslateAsync(new MtJob
            {
                Engine = engine, ApiKey = key, Region = region, SourceLanguage = "en", TargetLanguage = "nl",
                Texts = new[] { "Test" }, SettingsPlace = settingsPlace,
            }, null, ct).ConfigureAwait(false);
            if (outcome.Failure == null) info?.Invoke(DisplayName(engine) + " accepted the key.");
            return outcome.Failure;
        }

        // ─── HTTP ──────────────────────────────────────────────────────────────────

        // Shared for the life of the process, like LlmClient's: the system proxy
        // applies, timeouts are per request. Tests swap it for a fake handler.
        internal static HttpClient Http = new HttpClient(new HttpClientHandler()) { Timeout = Timeout.InfiniteTimeSpan };

        internal static TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

        /// <summary>The first wait before a retry; doubled for each further one. Tests shorten it.</summary>
        internal static TimeSpan RetryBase = TimeSpan.FromSeconds(2);

        private sealed class Request
        {
            public string Engine, Key, Region, Source, Target, SourceName, TargetName, Place, Context;
            public IList<string> Texts;
            public bool Usage;   // DeepL: ask for the usage, not a translation
        }

        private sealed class Reply
        {
            public string[] Texts;
            public int Billed;
            public MtFailure Failure;
            public TimeSpan? RetryAfter;
            public int Attempts;
            public string UsageLine;
        }

        private static async Task<Reply> SendWithRetriesAsync(Request request, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                var reply = await SendOnceAsync(request, ct).ConfigureAwait(false);
                reply.Attempts = attempt + 1;
                if (reply.Failure == null) return reply;

                var kind = reply.Failure.Kind;
                int retries = kind == MtFailureKind.RateLimited ? 3
                            : kind == MtFailureKind.ServerError || kind == MtFailureKind.Network ? 1 : 0;
                if (attempt >= retries) return reply;

                var wait = reply.RetryAfter
                        ?? (kind == MtFailureKind.RateLimited ? TimeSpan.FromTicks(RetryBase.Ticks << attempt) : RetryBase);
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }

        private static async Task<Reply> SendOnceAsync(Request r, CancellationToken ct)
        {
            using (var message = Build(r))
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(RequestTimeout);
                HttpResponseMessage response;
                string body;
                try
                {
                    response = await Http.SendAsync(message, timeout.Token).ConfigureAwait(false);
                    body = response.Content == null ? "" : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return Failed(r, MtFailureKind.Network, 0, null,
                        DisplayName(r.Engine) + " did not answer within " + (int)RequestTimeout.TotalSeconds + " seconds.");
                }
                catch (HttpRequestException ex)
                {
                    var why = Scrub(ex.GetBaseException().Message, r.Key);
                    return Failed(r, MtFailureKind.Network, 0, why, DisplayName(r.Engine) + " could not be reached: " + why);
                }

                using (response)
                {
                    body = Scrub(body, r.Key);
                    var status = (int)response.StatusCode;
                    if (!response.IsSuccessStatusCode)
                    {
                        var failed = Classify(r, status, body);
                        failed.RetryAfter = RetryAfterOf(response);
                        return failed;
                    }
                    return Parse(r, response, body);
                }
            }
        }

        private static HttpRequestMessage Build(Request r)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            HttpRequestMessage m;
            switch (r.Engine)
            {
                case DeepL:
                {
                    var host = r.Key.EndsWith(":fx", StringComparison.Ordinal) ? "https://api-free.deepl.com" : "https://api.deepl.com";
                    if (r.Usage)
                    {
                        m = new HttpRequestMessage(HttpMethod.Get, host + "/v2/usage");
                    }
                    else
                    {
                        var body = new Dictionary<string, object>
                        {
                            ["text"] = r.Texts.ToArray(),
                            ["source_lang"] = r.Source,
                            ["target_lang"] = r.Target,
                            ["tag_handling"] = "xml",
                            // v1 is DeepL's default "for now"; named, so the behaviour
                            // cannot change under us when DeepL switches (§3.1).
                            ["tag_handling_version"] = "v2",
                            ["split_sentences"] = "nonewlines",
                            ["preserve_formatting"] = true,
                            ["model_type"] = DeepLModel,
                            ["show_billed_characters"] = true,
                        };
                        if (!string.IsNullOrWhiteSpace(r.Context)) body["context"] = r.Context;
                        m = new HttpRequestMessage(HttpMethod.Post, host + "/v2/translate")
                        {
                            Content = new StringContent(json.Serialize(body), Encoding.UTF8, "application/json"),
                        };
                    }
                    m.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + r.Key);
                    return m;
                }
                case Google:
                {
                    var body = new Dictionary<string, object>
                    {
                        ["q"] = r.Texts.ToArray(),
                        ["source"] = r.Source,
                        ["target"] = r.Target,
                        ["format"] = "html",
                    };
                    m = new HttpRequestMessage(HttpMethod.Post, "https://translation.googleapis.com/language/translate/v2")
                    {
                        Content = new StringContent(json.Serialize(body), Encoding.UTF8, "application/json"),
                    };
                    // In a header rather than ?key=, so no URL - and no exception
                    // quoting one - ever carries it.
                    m.Headers.TryAddWithoutValidation("x-goog-api-key", r.Key);
                    return m;
                }
                default:
                {
                    var url = "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0"
                            + "&from=" + Uri.EscapeDataString(r.Source) + "&to=" + Uri.EscapeDataString(r.Target)
                            + "&textType=html";
                    var body = r.Texts.Select(t => new Dictionary<string, object> { ["Text"] = t }).ToArray();
                    m = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(json.Serialize(body), Encoding.UTF8, "application/json"),
                    };
                    m.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", r.Key);
                    if (!string.IsNullOrEmpty(r.Region)) m.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Region", r.Region);
                    return m;
                }
            }
        }

        private static Reply Parse(Request r, HttpResponseMessage response, string body)
        {
            object root;
            try { root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(body); }
            catch { return Unreadable(r, body); }

            try
            {
                if (r.Usage)
                {
                    var u = (IDictionary<string, object>)root;
                    long used = Convert.ToInt64(u["character_count"]), limit = Convert.ToInt64(u["character_limit"]);
                    return new Reply
                    {
                        UsageLine = "DeepL accepted the key: " + used.ToString("N0", CultureInfo.InvariantCulture) + " of "
                                  + limit.ToString("N0", CultureInfo.InvariantCulture) + " characters used this period.",
                    };
                }

                var texts = new List<string>();
                int billed = 0;
                switch (r.Engine)
                {
                    case DeepL:
                        foreach (IDictionary<string, object> t in (IEnumerable)((IDictionary<string, object>)root)["translations"])
                        {
                            texts.Add(t["text"] as string);
                            if (t.TryGetValue("billed_characters", out var b) && b != null) billed += Convert.ToInt32(b);
                        }
                        break;
                    case Google:
                        var data = (IDictionary<string, object>)((IDictionary<string, object>)root)["data"];
                        foreach (IDictionary<string, object> t in (IEnumerable)data["translations"])
                            texts.Add(t["translatedText"] as string);
                        billed = r.Texts.Sum(CodePoints);   // Google's rule: every character sent, markup included
                        break;
                    default:
                        foreach (IDictionary<string, object> item in (IEnumerable)root)
                        {
                            var list = (object[])item["translations"];
                            texts.Add(list.Length > 0 ? ((IDictionary<string, object>)list[0])["text"] as string : null);
                        }
                        billed = response.Headers.TryGetValues("X-metered-usage", out var metered)
                                 && int.TryParse(metered.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
                            ? m
                            : r.Texts.Sum(CodePoints);
                        break;
                }

                if (texts.Count != r.Texts.Count)
                    return Failed(r, MtFailureKind.Malformed, (int)response.StatusCode, Clip(body),
                        DisplayName(r.Engine) + " sent back " + texts.Count + " translations for " + r.Texts.Count
                        + " segments; none of them was used.");
                return new Reply { Texts = texts.ToArray(), Billed = billed };
            }
            catch
            {
                return Unreadable(r, body);
            }
        }

        private static Reply Unreadable(Request r, string body) =>
            Failed(r, MtFailureKind.Malformed, 200, Clip(body), DisplayName(r.Engine) + " sent a reply Supervertaler could not read.");

        /// <summary>A refused request, by each engine's documented codes (docs/design/mt-engines.md §5).</summary>
        private static Reply Classify(Request r, int status, string body)
        {
            var name = DisplayName(r.Engine);
            string said = null;
            long code = 0;
            var reasons = new List<string>();
            try
            {
                var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(body) as IDictionary<string, object>;
                if (root != null)
                {
                    if (root.TryGetValue("error", out var e) && e is IDictionary<string, object> err)
                    {
                        said = err.TryGetValue("message", out var msg) ? msg as string : null;
                        if (err.TryGetValue("code", out var c) && c != null) long.TryParse(Convert.ToString(c, CultureInfo.InvariantCulture), out code);
                        if (err.TryGetValue("status", out var st) && st is string s) reasons.Add(s);
                        CollectReasons(err, reasons);
                    }
                    else if (root.TryGetValue("message", out var msg))
                    {
                        said = msg as string;
                    }
                }
            }
            catch { /* not JSON: the status decides */ }
            said = said ?? Clip(body);

            bool Has(string reason) => reasons.Any(x => string.Equals(x, reason, StringComparison.OrdinalIgnoreCase));
            bool Says(string words) => said != null && said.IndexOf(words, StringComparison.OrdinalIgnoreCase) >= 0;

            MtFailureKind kind;
            switch (r.Engine)
            {
                case DeepL:
                    if (status == 403 || status == 401) kind = MtFailureKind.BadKey;
                    else if (status == 456) kind = MtFailureKind.QuotaUsedUp;
                    else if (status == 429 || status == 529) kind = MtFailureKind.RateLimited;
                    else if (status == 400 && Says("not supported") && Says("lang")) kind = MtFailureKind.UnsupportedLanguage;
                    else if (status >= 500) kind = MtFailureKind.ServerError;
                    else kind = MtFailureKind.Refused;
                    break;

                case Google:
                    if (Has("API_KEY_INVALID") || Has("API_KEY_EXPIRED") || Has("SERVICE_DISABLED") || Has("accessNotConfigured")
                        || Has("API_KEY_SERVICE_BLOCKED") || Has("API_KEY_HTTP_REFERRER_BLOCKED") || Has("API_KEY_IP_ADDRESS_BLOCKED")
                        || Has("PERMISSION_DENIED") || status == 401)
                        kind = MtFailureKind.BadKey;
                    else if (Has("dailyLimitExceeded") || Has("quotaExceeded") || Says("Daily Limit Exceeded"))
                        kind = MtFailureKind.QuotaUsedUp;
                    else if (status == 429 || Has("rateLimitExceeded") || Has("userRateLimitExceeded") || Has("RATE_LIMIT_EXCEEDED")
                             || Has("RESOURCE_EXHAUSTED") || Says("Rate Limit Exceeded"))
                        kind = MtFailureKind.RateLimited;
                    else if (status == 400 && (Has("target") || Has("source") || Says("language pair")))
                        kind = MtFailureKind.UnsupportedLanguage;
                    else if (status == 403) kind = MtFailureKind.BadKey;
                    else if (status >= 500) kind = MtFailureKind.ServerError;
                    else kind = MtFailureKind.Refused;
                    break;

                default:
                    if (status == 401 || (code >= 401000 && code < 402000)) kind = MtFailureKind.BadKey;
                    else if (code == 403001) kind = MtFailureKind.QuotaUsedUp;
                    else if (status == 429 || (code >= 429000 && code < 430000)) kind = MtFailureKind.RateLimited;
                    else if (code == 400019 || code == 400023 || code == 400035 || code == 400036) kind = MtFailureKind.UnsupportedLanguage;
                    else if (status == 403) kind = MtFailureKind.BadKey;
                    else if (status >= 500) kind = MtFailureKind.ServerError;
                    else kind = MtFailureKind.Refused;
                    break;
            }

            string message;
            switch (kind)
            {
                case MtFailureKind.BadKey:
                    message = name + " refused the key. Check it in " + r.Place + "."
                            + (r.Engine == DeepL ? " A key from DeepL's free plan ends in :fx."
                             : r.Engine == Google ? " The key's Google Cloud project needs the Cloud Translation API turned on."
                             : " For a resource that is not Global, its region must be set too.");
                    break;
                case MtFailureKind.QuotaUsedUp:
                    message = name + ": the character allowance for this key is used up.";
                    break;
                case MtFailureKind.UnsupportedLanguage:
                    message = name + " does not translate " + r.SourceName + " into " + r.TargetName + ".";
                    break;
                case MtFailureKind.RateLimited:
                    message = name + " turned the request away as one too many, four times running. Try again in a few minutes.";
                    break;
                case MtFailureKind.ServerError:
                    message = name + " did not answer (server error " + status + ").";
                    break;
                default:
                    message = name + " refused the request (" + status + "): " + said;
                    break;
            }
            return Failed(r, kind, status, said, message);
        }

        /// <summary>Google's reasons and field names, wherever its error puts them.</summary>
        private static void CollectReasons(IDictionary<string, object> err, List<string> reasons)
        {
            foreach (var listName in new[] { "errors", "details" })
            {
                if (!err.TryGetValue(listName, out var list) || !(list is IEnumerable items)) continue;
                foreach (var item in items)
                {
                    if (!(item is IDictionary<string, object> d)) continue;
                    if (d.TryGetValue("reason", out var reason) && reason is string rs) reasons.Add(rs);
                    if (d.TryGetValue("fieldViolations", out var fv) && fv is IEnumerable violations)
                        foreach (var v in violations)
                            if (v is IDictionary<string, object> vd && vd.TryGetValue("field", out var field) && field is string fs)
                                reasons.Add(fs);
                }
            }
        }

        private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
        {
            var delta = response.Headers.RetryAfter?.Delta;
            if (delta == null) return null;
            // Never longer than a minute: a run waits for it with the dialog open.
            return delta.Value > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : delta.Value;
        }

        private static Reply Failed(Request r, MtFailureKind kind, int status, string said, string message) => new Reply
        {
            Failure = new MtFailure { Kind = kind, HttpStatus = status, EngineMessage = said, Message = message },
        };

        private static string Scrub(string text, string key) =>
            string.IsNullOrEmpty(text) || string.IsNullOrEmpty(key) ? text : text.Replace(key, "***");

        private static string Clip(string s) =>
            string.IsNullOrEmpty(s) ? s : s.Length <= 300 ? s.Trim() : s.Substring(0, 300).Trim() + "…";
    }
}
