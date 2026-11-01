using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Supervertaler.Core.Models;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// Machine-translation engines (docs/design/mt-engines.md in the Trados repo).
    /// Offline: a fake HTTP handler plays DeepL, Google and Microsoft, with the
    /// replies the real ones gave on 1 Nov 2026 where a failure's shape matters.
    /// Mostly the ways a run goes wrong - a bad key, an allowance used up mid-run,
    /// an unsupported pair, a busy or silent engine, a reply that cannot be read,
    /// cancellation - since the rule is that none of them ends with an engine's
    /// error text in a translation, a key in a message, or requests sent only to be
    /// refused. Fake keys only; no key file is touched.
    /// </summary>
    [Tests]
    internal static class MachineTranslationTests
    {
        private const string Key = "fake-PROJ-001-key";
        private const string FreeKey = "fake-PROJ-001-key:fx";
        private const string Place = "AI Settings";

        // ─── Tags ──────────────────────────────────────────────────────────────────

        private static readonly string[] Tagged =
        {
            "Press <t1>Start</t1> to begin.",
            "The <t1>first <t2>and</t2> second</t1> layers are bonded.",
            "See <t1>Figure 3</t1> for details.<t2/>",
            "<t1>Note:</t1> x < y & \"a\" > b, it's Acme's PROJ-001.",
            "A sensor<t1/> and a controller<t2/>.",
            "No tags at all.",
        };

        public static void Mt_Tags_XmlRoundTrip_GivesBackTheSegmentExactly()
        {
            foreach (var s in Tagged)
            {
                var sent = MtMarkup.ToEngine(s, MtMarkup.Notation.Xml, out var tagsSent);
                Assert.True(tagsSent, "tags sent: " + s);
                Assert.True(!sent.Contains(" < ") && !sent.Contains(" & "), "text escaped: " + sent);
                Assert.Equal(s, MtMarkup.FromEngine(sent, MtMarkup.Notation.Xml, s), "round trip");
            }
        }

        public static void Mt_Tags_HtmlRoundTrip_UsesSpansAndImages()
        {
            var sent = MtMarkup.ToEngine("See <t1>Figure 3</t1> for details.<t2/>", MtMarkup.Notation.Html, out _);
            Assert.Equal("See <span id=\"t1\">Figure 3</span> for details.<img id=\"t2\">", sent, "html notation");
            foreach (var s in Tagged)
            {
                var html = MtMarkup.ToEngine(s, MtMarkup.Notation.Html, out _);
                Assert.Equal(s, MtMarkup.FromEngine(html, MtMarkup.Notation.Html, s), "round trip");
            }
        }

        public static void Mt_Tags_NestedSpansCloseInTheRightOrder()
        {
            const string s = "The <t1>first <t2>and</t2> second</t1> layers.";
            const string reply = "De <span id=\"t1\">eerste <span id=\"t2\">en</span> tweede</span> lagen.";
            Assert.Equal("De <t1>eerste <t2>en</t2> tweede</t1> lagen.", MtMarkup.FromEngine(reply, MtMarkup.Notation.Html, s), "nesting");
        }

        public static void Mt_Tags_GoogleSpacingRepaired_FrenchSpacesKept()
        {
            const string s = "The housing (<t1>10</t1>) is fixed to <t2>PROJ-001</t2>.";
            const string google = "De behuizing ( <span id=\"t1\">10</span> ) is bevestigd aan <span id=\"t2\">PROJ-001</span> .";
            Assert.Equal("De behuizing (<t1>10</t1>) is bevestigd aan <t2>PROJ-001</t2>.",
                MtMarkup.FromEngine(google, MtMarkup.Notation.Html, s), "spaces Google adds");

            const string fr = "Voir <span id=\"t1\">figure 3</span> ; le <span id=\"t2\">capteur</span> !";
            Assert.Equal("Voir <t1>figure 3</t1> ; le <t2>capteur</t2> !",
                MtMarkup.FromEngine(fr, MtMarkup.Notation.Html, "See <t1>Figure 3</t1>; the <t2>sensor</t2>!"), "French spacing kept");
        }

        public static void Mt_Tags_StandaloneReturnedAsEmptyPair_BecomesOnePlaceholder()
        {
            Assert.Equal("Klik<t1/> Opslaan<t2/>.",
                MtMarkup.FromEngine("Klik<t1></t1> Opslaan<t2></t2>.", MtMarkup.Notation.Xml, "Click<t1/> Save<t2/>."), "xml");
            Assert.Equal("Zie<t2/>", MtMarkup.FromEngine("Zie<img id=\"t2\"/>", MtMarkup.Notation.Html, "See<t2/>"), "img with slash");
        }

        public static void Mt_Tags_MarkupTheEngineAdded_IsDropped_EntitiesDecoded()
        {
            var back = MtMarkup.FromEngine("<b>Druk</b> op<br> <span id=\"t1\">Start</span> &amp; it&#39;s &quot;x&quot; &lt; y",
                MtMarkup.Notation.Html, "Press <t1>Start</t1>");
            Assert.Equal("Druk op <t1>Start</t1> & it's \"x\" < y", back, "foreign markup gone, entities decoded");
            Assert.True(!back.Contains("<b>") && !back.Contains("<br"), "nothing of an engine's markup is left as text");
        }

        public static void Mt_Tags_BadlyNestedSource_IsSentWithoutTags()
        {
            foreach (var bad in new[] { "A <t1>b <t2>c</t1> d</t2>", "A <t1>open only", "A </t1> close only", "A<t1/> b<t1/>" })
            {
                Assert.True(!MtMarkup.WellFormed(bad), "not well formed: " + bad);
                var sent = MtMarkup.ToEngine(bad, MtMarkup.Notation.Xml, out var tagsSent);
                Assert.True(!tagsSent && !sent.Contains("<t"), "sent plain: " + bad);
            }
        }

        // ─── Language codes ────────────────────────────────────────────────────────

        public static void Mt_Codes_DeepL()
        {
            var expected = new Dictionary<string, string>
            {
                ["en-US"] = "EN-US", ["en-GB"] = "EN-GB", ["en-ZA"] = "EN-GB", ["en"] = "EN-GB",
                ["pt-BR"] = "PT-BR", ["pt-PT"] = "PT-PT", ["pt-AO"] = "PT-PT",
                ["zh-CN"] = "ZH-HANS", ["zh-Hans-SG"] = "ZH-HANS", ["zh-TW"] = "ZH-HANT", ["zh-Hant-HK"] = "ZH-HANT",
                ["es-ES"] = "ES", ["es-MX"] = "ES-419", ["es-419"] = "ES-419",
                ["nb-NO"] = "NB", ["nn-NO"] = "NB", ["no"] = "NB", ["nl-NL"] = "NL", ["nl-BE"] = "NL", ["de-CH"] = "DE",
            };
            foreach (var kv in expected)
                Assert.Equal(kv.Value, MachineTranslation.TargetCode(MachineTranslation.DeepL, kv.Key), "DeepL target for " + kv.Key);
            foreach (var src in new[] { "en-US", "pt-BR", "zh-TW", "es-419" })
                Assert.True(!MachineTranslation.SourceCode(MachineTranslation.DeepL, src).Contains("-"), "a DeepL source carries no variant: " + src);
        }

        public static void Mt_Codes_GoogleAndMicrosoft_AreInTheirOwnLists()
        {
            var cultures = new[]
            {
                "en-GB", "en-US", "nl-NL", "nl-BE", "de-DE", "fr-FR", "fr-CA", "es-ES", "es-MX", "it-IT", "pt-BR", "pt-PT",
                "zh-CN", "zh-TW", "zh-Hant-HK", "ja-JP", "ko-KR", "nb-NO", "nn-NO", "sv-SE", "da-DK", "fi-FI", "pl-PL",
                "cs-CZ", "ru-RU", "uk-UA", "tr-TR", "el-GR", "he-IL", "ar-SA", "sr-Latn-RS", "sr-Cyrl-RS", "fil-PH", "hu-HU",
            };
            var google = new HashSet<string>(GoogleList, StringComparer.Ordinal);
            var microsoft = new HashSet<string>(MicrosoftList, StringComparer.Ordinal);
            foreach (var c in cultures)
            {
                var g = MachineTranslation.TargetCode(MachineTranslation.Google, c);
                Assert.True(google.Contains(g), "Google's list has " + g + " (for " + c + ")");
                var m = MachineTranslation.TargetCode(MachineTranslation.Microsoft, c);
                Assert.True(microsoft.Contains(m), "Microsoft's list has " + m + " (for " + c + ")");
            }
            Assert.Equal("zh-TW", MachineTranslation.TargetCode(MachineTranslation.Google, "zh-Hant-HK"), "Google traditional");
            Assert.Equal("pt", MachineTranslation.TargetCode(MachineTranslation.Microsoft, "pt-BR"), "Microsoft Brazilian");
            Assert.Equal("sr-Latn", MachineTranslation.TargetCode(MachineTranslation.Microsoft, "sr-Latn-RS"), "Microsoft Serbian Latin");
        }

        // ─── Requests ──────────────────────────────────────────────────────────────

        public static void Mt_Chunks_FiftyEach_TenForDeepLWithContext_CharactersCapped()
        {
            var texts = Enumerable.Range(0, 120).Select(i => "Segment " + i).ToList();
            Assert.Equal("0+50,50+50,100+20", Show(MachineTranslation.Chunks(texts, MachineTranslation.Microsoft, false)), "by count");
            Assert.Equal(12, MachineTranslation.Chunks(texts, MachineTranslation.DeepL, true).Count, "DeepL with context: 10 each");
            Assert.Equal(3, MachineTranslation.Chunks(texts, MachineTranslation.DeepL, false).Count, "DeepL without context: 50 each");

            var big = Enumerable.Range(0, 4).Select(i => new string('x', 2_000)).ToList();
            Assert.Equal("0+2,2+2", Show(MachineTranslation.Chunks(big, MachineTranslation.Google, false)), "Google's 5,000 code points");
            var huge = new List<string> { "a", new string('x', 9_000), "b" };
            Assert.Equal("0+1,1+1,2+1", Show(MachineTranslation.Chunks(huge, MachineTranslation.Google, false)), "an over-long segment alone");
            Assert.Equal(0, MachineTranslation.Chunks(new string[0], MachineTranslation.Google, false).Count, "nothing to send");
        }

        public static void Mt_Context_TheChunkAndItsNeighbours_TagsLeftOut()
        {
            var src = Enumerable.Range(0, 10).Select(i => "S" + i + " <t1>x</t1>").ToList();
            var ctx = MachineTranslation.ContextAround(src, 4, 2, maxChars: 40);
            Assert.True(!ctx.Contains("<t"), "no tags");
            var lines = ctx.Split('\n');
            Assert.True(lines.Contains("S4 x") && lines.Contains("S5 x"), "the chunk itself");
            Assert.True(lines.Contains("S3 x") && lines.Contains("S6 x"), "a neighbour each side");
            Assert.True(ctx.Length <= 40, "within the limit: " + ctx.Length);
            Assert.True(lines.SequenceEqual(lines.OrderBy(l => int.Parse(l.Substring(1, 1)))), "in document order");
            Assert.True(MachineTranslation.ContextAround(new string[0], 0, 0) == null, "none");
        }

        public static void Mt_DeepL_RequestShape_ContextAndBilling()
        {
            using (var mt = new MtScene(Echo))
            {
                var texts = Enumerable.Range(0, 12).Select(i => "Press <t1>Start</t1> " + i).ToList();
                var calls = new List<int>();
                var outcome = Run(new MtJob
                {
                    Engine = MachineTranslation.DeepL, ApiKey = FreeKey, SourceLanguage = "en-GB", TargetLanguage = "nl-NL",
                    Texts = texts, SettingsPlace = Place, ContextFor = (s, c) => MachineTranslation.ContextAround(texts, s, c),
                }, (start, chunk) => calls.Add(start));

                Assert.Equal(2, mt.Requests.Count, "two requests of up to 10 (context on)");
                var r = mt.Requests[0];
                Assert.True(r.Url.StartsWith("https://api-free.deepl.com/v2/translate"), "a :fx key goes to the free host");
                Assert.Equal("DeepL-Auth-Key " + FreeKey, r.Header("Authorization"), "key in the header");
                Assert.True(!r.Url.Contains(FreeKey), "never in the URL");
                var body = r.Json;
                Assert.Equal("xml", body["tag_handling"] as string, "xml tags");
                Assert.Equal("v2", body["tag_handling_version"] as string, "tag handling named");
                Assert.Equal("NL", body["target_lang"] as string, "target");
                Assert.Equal("EN", body["source_lang"] as string, "source without variant");
                Assert.Equal(true, (bool)body["show_billed_characters"], "billed characters asked for");
                Assert.True((body["context"] as string ?? "").Contains("Press Start 0"), "context sent, without tags");
                Assert.Equal("0,10", string.Join(",", calls), "each chunk handed over as it came back");
                Assert.Equal(texts[3], outcome.Translations[3], "tags back in the plugins' notation");
                Assert.Equal(12 * 7, outcome.BilledCharacters, "DeepL's billed_characters summed");
                Assert.True(!outcome.BilledComputed && outcome.Failure == null && !outcome.Stopped, "clean run");
                Assert.Equal(MachineTranslation.Cost(MachineTranslation.DeepL, 84), outcome.Cost, "cost at list price");

                Run(new MtJob { Engine = MachineTranslation.DeepL, ApiKey = Key, SourceLanguage = "en", TargetLanguage = "de", Texts = new[] { "x" } }, null);
                Assert.True(mt.Requests.Last().Url.StartsWith("https://api.deepl.com/"), "a paid key goes to the paid host");
                Assert.True(!mt.Requests.Last().Json.ContainsKey("context"), "no context unless asked for");
            }
        }

        public static void Mt_Google_KeyInHeader_HtmlMode_BillingComputedFromWhatWasSent()
        {
            using (var mt = new MtScene(Echo))
            {
                var texts = new[] { "See <t1>Figure 3</t1>.", "It's 4 < 5 & so on." };
                var outcome = Run(new MtJob { Engine = MachineTranslation.Google, ApiKey = Key, SourceLanguage = "en-GB", TargetLanguage = "zh-TW", Texts = texts }, null);
                var r = mt.Requests.Single();
                Assert.Equal(Key, r.Header("x-goog-api-key"), "key in the header");
                Assert.True(!r.Url.Contains(Key) && !r.Url.Contains("key="), "never in the URL");
                Assert.Equal("html", r.Json["format"] as string, "html mode");
                Assert.Equal("zh-TW", r.Json["target"] as string, "target");
                var sent = ((object[])r.Json["q"]).Cast<string>().ToList();
                Assert.Equal("See <span id=\"t1\">Figure 3</span>.", sent[0], "spans");
                Assert.Equal(sent.Sum(s => s.Length), outcome.BilledCharacters, "every character sent, markup included");
                Assert.True(outcome.BilledComputed, "and it says so");
                Assert.Equal(texts[1], outcome.Translations[1], "escaped and decoded again");
            }
        }

        public static void Mt_Microsoft_RegionOnlyWhenSet_MeteredUsageRead()
        {
            using (var mt = new MtScene(Echo))
            {
                var o = Run(new MtJob { Engine = MachineTranslation.Microsoft, ApiKey = Key, SourceLanguage = "en", TargetLanguage = "sr-Latn-RS", Texts = new[] { "One", "Two" } }, null);
                var r = mt.Requests[0];
                Assert.Equal(Key, r.Header("Ocp-Apim-Subscription-Key"), "key header");
                Assert.True(r.Header("Ocp-Apim-Subscription-Region") == null, "a Global resource sends no region");
                Assert.True(r.Url.Contains("textType=html") && r.Url.Contains("to=sr-Latn"), "html, target");
                Assert.Equal(777, o.BilledCharacters, "X-metered-usage");
                Assert.True(!o.BilledComputed, "reported, not computed");

                Run(new MtJob { Engine = MachineTranslation.Microsoft, ApiKey = Key, Region = "westeurope", SourceLanguage = "en", TargetLanguage = "nl", Texts = new[] { "One" } }, null);
                Assert.Equal("westeurope", mt.Requests[1].Header("Ocp-Apim-Subscription-Region"), "a regional resource sends it");
            }
        }

        // ─── Failures ──────────────────────────────────────────────────────────────

        private const string GoogleBadKey = "{\"error\":{\"code\":400,\"message\":\"API key not valid. Please pass a valid API key.\",\"status\":\"INVALID_ARGUMENT\","
            + "\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\",\"reason\":\"API_KEY_INVALID\",\"domain\":\"googleapis.com\"}]}}";
        private const string GoogleUnsupported = "{\"error\":{\"code\":400,\"message\":\"Invalid Value\",\"errors\":[{\"message\":\"Invalid Value\",\"domain\":\"global\",\"reason\":\"invalid\"}],"
            + "\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.BadRequest\",\"fieldViolations\":[{\"field\":\"target\",\"description\":\"Target language: cy\"}]}]}}";
        private const string MicrosoftBadKey = "{\"error\":{\"code\":401001,\"message\":\"The request is not authorized because credentials are missing or invalid.\"}}";
        private const string DeepLBadKey = "{\"message\":\"Forbidden. You can find more info in our docs: https://developers.deepl.com/docs/getting-started/auth\"}";

        public static void Mt_BadKey_StopsAtTheFirstRequest_EveryEngine()
        {
            var cases = new[]
            {
                (MachineTranslation.DeepL, 403, DeepLBadKey, ":fx"),
                (MachineTranslation.Google, 400, GoogleBadKey, "Cloud Translation API"),
                (MachineTranslation.Microsoft, 401, MicrosoftBadKey, "region"),
            };
            foreach (var (engine, status, body, hint) in cases)
            {
                using (var mt = new MtScene((req, i, ct) => Reply(status, body)))
                {
                    var o = Run(Many(engine, 120), null);
                    Assert.Equal(1, mt.Requests.Count, engine + ": one request, not one per chunk");
                    Assert.Equal(MtFailureKind.BadKey, o.Failure.Kind, engine + ": bad key");
                    Assert.True(o.Stopped && o.Translations.All(t => t == null), engine + ": stopped, nothing translated");
                    Assert.True(o.Failure.Message.StartsWith(MachineTranslation.DisplayName(engine) + " refused the key. Check it in AI Settings."), engine + ": " + o.Failure.Message);
                    Assert.True(o.Failure.Message.Contains(hint), engine + " hint");
                }
            }
        }

        public static void Mt_MissingKey_SendsNothing()
        {
            using (var mt = new MtScene(Echo))
            {
                var o = Run(new MtJob { Engine = MachineTranslation.DeepL, ApiKey = " ", Texts = new[] { "x" }, SettingsPlace = Place, TargetLanguage = "nl" }, null);
                Assert.Equal(0, mt.Requests.Count, "no request");
                Assert.Equal("No API key set for DeepL. Add one in AI Settings.", o.Failure.Message, "message");
            }
        }

        public static void Mt_QuotaUsedUpMidRun_KeepsWhatCameBack_SendsNoMore()
        {
            using (var mt = new MtScene((req, i, ct) => i == 0 ? Echo(req, i, ct) : Task.FromResult(Reply(456, "{\"message\":\"Quota exceeded\"}"))))
            {
                var delivered = new List<int>();
                var o = Run(Many(MachineTranslation.DeepL, 150), (s, c) => delivered.Add(s));
                Assert.Equal(2, mt.Requests.Count, "the third chunk is never sent");
                Assert.Equal(MtFailureKind.QuotaUsedUp, o.Failure.Kind, "quota");
                Assert.Equal("DeepL: the character allowance for this key is used up.", o.Failure.Message, "message");
                Assert.Equal("0", string.Join(",", delivered), "the first chunk was delivered");
                Assert.Equal(50, o.Translations.Count(t => t != null), "and kept");
                Assert.True(o.BilledCharacters > 0 && o.Cost > 0, "what was charged is still counted");
            }
        }

        public static void Mt_UnsupportedPair_NamesBothLanguages()
        {
            using (var mt = new MtScene((req, i, ct) => Reply(400, GoogleUnsupported)))
            {
                var o = Run(new MtJob { Engine = MachineTranslation.Google, ApiKey = Key, SourceLanguage = "en-GB", TargetLanguage = "cy-GB", Texts = new[] { "x" } }, null);
                Assert.Equal(MtFailureKind.UnsupportedLanguage, o.Failure.Kind, "unsupported");
                Assert.Equal("Google Translate does not translate English (United Kingdom) into Welsh (United Kingdom).", o.Failure.Message, "message");
            }
            using (var mt = new MtScene((req, i, ct) => Reply(400, "{\"error\":{\"code\":400036,\"message\":\"The target language is not valid.\"}}")))
                Assert.Equal(MtFailureKind.UnsupportedLanguage, Run(Many(MachineTranslation.Microsoft, 3), null).Failure.Kind, "Microsoft 400036");
            using (var mt = new MtScene((req, i, ct) => Reply(400, "{\"message\":\"Value for 'target_lang' not supported.\"}")))
                Assert.Equal(MtFailureKind.UnsupportedLanguage, Run(Many(MachineTranslation.DeepL, 3), null).Failure.Kind, "DeepL");
        }

        public static void Mt_Busy_RetriedHonouringRetryAfter_ThenSucceeds()
        {
            using (var mt = new MtScene((req, i, ct) => i < 2 ? Task.FromResult(Reply(429, "{\"message\":\"Too many requests\"}", retryAfterSeconds: 0)) : Echo(req, i, ct)))
            {
                var o = Run(Many(MachineTranslation.DeepL, 3), null);
                Assert.Equal(3, mt.Requests.Count, "two refusals, then the answer");
                Assert.True(o.Failure == null && o.Translations.All(t => t != null), "translated");
                Assert.Equal(3, o.Requests, "requests counted, retries included");
            }
        }

        public static void Mt_BusyEveryTime_ChunkFails_RunGoesOn()
        {
            // Chunk 1: four refusals (one try, three retries). Chunk 2 answers.
            using (var mt = new MtScene((req, i, ct) => i < 4 ? Task.FromResult(Reply(429, "{\"error\":{\"code\":429001,\"message\":\"busy\"}}")) : Echo(req, i, ct)))
            {
                var o = Run(Many(MachineTranslation.Microsoft, 60), null);
                Assert.Equal(5, mt.Requests.Count, "1 + 3 retries, then the next chunk");
                Assert.Equal(MtFailureKind.RateLimited, o.Failure.Kind, "rate limited");
                Assert.True(!o.Stopped, "a busy engine does not stop the run");
                Assert.True(o.Translations.Take(50).All(t => t == null) && o.Translations.Skip(50).All(t => t != null), "only the busy chunk is empty");
            }
        }

        public static void Mt_ServerErrors_RetriedOnce_ThreeFailedChunksStopTheRun()
        {
            using (var mt = new MtScene((req, i, ct) => Reply(503, "<html>Service Unavailable</html>")))
            {
                var o = Run(Many(MachineTranslation.Google, 300), null);   // Google: 5,000 code points a request
                Assert.Equal(6, mt.Requests.Count, "three chunks, each tried twice");
                Assert.True(o.Stopped, "stopped after three failed chunks in a row");
                Assert.Equal(MtFailureKind.ServerError, o.Failure.Kind, "server error");
                Assert.True(o.Failure.Message.Contains("server error 503") && o.Failure.Message.Contains("three requests in a row"), o.Failure.Message);
            }
        }

        public static void Mt_Timeout_IsANetworkFailure_NotAHang()
        {
            using (var mt = new MtScene(async (req, i, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return Reply(200, "{}"); }))
            {
                MachineTranslation.RequestTimeout = TimeSpan.FromMilliseconds(150);
                var o = Run(Many(MachineTranslation.DeepL, 2), null);
                Assert.Equal(MtFailureKind.Network, o.Failure.Kind, "network");
                Assert.True(o.Failure.Message.Contains("did not answer within"), o.Failure.Message);
                Assert.Equal(2, mt.Requests.Count, "retried once");
            }
        }

        public static void Mt_UnreadableReplies_NoneOfItIsUsed()
        {
            var bodies = new[]
            {
                "not json at all",
                "{\"translations\":[{\"text\":\"one\"}]}",                    // one translation for two segments
                "{\"unexpected\":true}",
            };
            foreach (var body in bodies)
            {
                using (var mt = new MtScene((req, i, ct) => Reply(200, body)))
                {
                    var o = Run(Many(MachineTranslation.DeepL, 2), null);
                    Assert.Equal(MtFailureKind.Malformed, o.Failure.Kind, "malformed: " + body);
                    Assert.True(o.Translations.All(t => t == null), "nothing used: " + body);
                }
            }
        }

        public static void Mt_EmptyTranslation_IsNoTranslation()
        {
            using (var mt = new MtScene((req, i, ct) => Reply(200, "{\"translations\":[{\"text\":\"Een\"},{\"text\":\"  \"}]}")))
            {
                var o = Run(Many(MachineTranslation.DeepL, 2), null);
                Assert.True(o.Translations[0] == "Een" && o.Translations[1] == null, "blank is null");
            }
        }

        public static void Mt_NoErrorTextOrKeyEverReachesATranslationOrMessage()
        {
            // An engine that quotes the key back in its error.
            var echoKey = "{\"error\":{\"code\":400,\"message\":\"Bad key " + Key + " for this API\"}}";
            foreach (var engine in MachineTranslation.Engines)
            {
                foreach (var status in new[] { 400, 403, 429, 456, 500 })
                {
                    using (var mt = new MtScene((req, i, ct) => Reply(status, echoKey)))
                    {
                        var o = Run(Many(engine, 3), null);
                        Assert.True(o.Translations.All(t => t == null), engine + " " + status + ": no translation");
                        Assert.True(!(o.Failure.Message ?? "").Contains(Key) && !(o.Failure.EngineMessage ?? "").Contains(Key),
                            engine + " " + status + ": key scrubbed");
                    }
                }
            }
        }

        public static void Mt_CancelledDuringARequest_KeepsWhatCameBack_DoesNotThrow()
        {
            using (var cts = new CancellationTokenSource())
            using (var mt = new MtScene(async (req, i, ct) =>
            {
                if (i == 0) return await Echo(req, i, ct);
                cts.Cancel();
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return Reply(200, "{}");
            }))
            {
                var o = MachineTranslation.TranslateAsync(Many(MachineTranslation.Microsoft, 120), null, cts.Token).GetAwaiter().GetResult();
                Assert.True(o.Cancelled, "cancelled");
                Assert.Equal(50, o.Translations.Count(t => t != null), "the first chunk is kept");
                Assert.Equal(2, mt.Requests.Count, "nothing after the cancel");
                Assert.True(o.BilledCharacters > 0, "and its characters counted");
            }
        }

        public static void Mt_CancelledBetweenChunks_SendsNoMore()
        {
            using (var cts = new CancellationTokenSource())
            using (var mt = new MtScene(Echo))
            {
                var o = MachineTranslation.TranslateAsync(Many(MachineTranslation.DeepL, 120), (s, c) => cts.Cancel(), cts.Token).GetAwaiter().GetResult();
                Assert.Equal(1, mt.Requests.Count, "one request");
                Assert.True(o.Cancelled && !o.Stopped, "cancelled, not stopped");
            }
        }

        // ─── Prices, costs, names ──────────────────────────────────────────────────

        public static void Mt_Prices_PerCharacter_NeverTheAiCeiling()
        {
            foreach (var engine in MachineTranslation.Engines)
            {
                Assert.True(MachineTranslation.ListPricePerMillion(engine) > 0, engine + " has a price");
                Assert.Equal(1_000_000m * MachineTranslation.ListPricePerMillion(engine) / 1_000_000m,
                    MachineTranslation.Cost(engine, 1_000_000), engine + ": a million characters at list price");
                Assert.Equal(0m, TokenEstimator.CostCeiling(engine, MachineTranslation.ModelName(engine), 1_000_000, 0, 0, 1_000_000),
                    engine + ": never priced as tokens at the dearest AI rate");
            }
            Assert.Equal(0m, MachineTranslation.ListPricePerMillion(LlmModels.ProviderClaude), "an AI provider has no MT price");
            Assert.True(!MachineTranslation.IsEngine(LlmModels.ProviderGemini) && !LlmModels.AllProviderKeys.Any(MachineTranslation.IsEngine),
                "no AI provider is an engine, and no engine is an AI provider");
        }

        public static void Mt_PromptLogEntry_ShowsCharactersNotTokens()
        {
            using (new CultureScope())
            {
            var e = new PromptLogEntry { Provider = "deepl", Model = "prefer_quality_optimized", DisplayModel = "DeepL", Characters = 3104, EstimatedCost = 0.0854m, Duration = TimeSpan.FromSeconds(1.2) };
            Assert.Equal("DeepL • 3,104 characters • $0.09 • 1.2s", e.SummaryLine, "summary");
            Assert.True(e.ToFullText().Contains("Characters charged: 3,104"), "full text");
            }
        }

        public static void Mt_KeyMessages_NameTheEngine()
        {
            using (var s = new KeyScene())
            {
                Assert.Equal("No API key set for DeepL. Add one in AI Settings.", ApiKeyStore.MissingKeyMessage("deepl", Place), "DeepL");
                Assert.Equal("No API key set for Microsoft Translator. Add one in AI Settings.", ApiKeyStore.MissingKeyMessage("microsoft", Place), "Microsoft");
                Assert.True(ApiKeyStore.CheckShape("google", "AIzaFakeKeyForProj001") == null, "a Google key in the google box passes");
            }
        }

        // ─── SuperBench with an engine ────────────────────────────────────────────

        public static void Mt_SuperBench_ReportSaysWhatTheEnginesWereGiven()
        {
            using (new CultureScope())
            {
            var run = new SuperBench.Run { DocumentName = "Acme PROJ-001.docx", SourceLang = "English", TargetLang = "Dutch", Sources = { "Press <t1>Start</t1>." } };
            run.Contenders.Add(new SuperBench.Contender { Provider = "claude", Model = "claude-opus-5-5", DisplayModel = "Claude Opus 5.5", Translations = { "Druk op <t1>Start</t1>." }, InputTokens = 1200, OutputTokens = 40, Cost = 0.01m });
            run.Contenders.Add(new SuperBench.Contender { Provider = "deepl", Model = "prefer_quality_optimized", DisplayModel = "DeepL", Translations = { "Druk op <t1>Start</t1>." }, Characters = 3104, Cost = 0.0854m });
            run.Contenders.Add(new SuperBench.Contender { Provider = "google", Model = "nmt", DisplayModel = "Google Translate", Translations = { null }, Characters = 52, Error = "Google Translate refused the key." });
            SuperBench.AssignBlindLabels(run, new Random(1));
            var md = SuperBench.RenderMarkdown(run);
            Assert.True(md.Contains("- Machine translation: DeepL and Google Translate were given no prompt and no termbase. The judge marked them against both, like every candidate."), "who saw what");
            Assert.True(md.Contains("DeepL (machine translation) | $0.0854 | 3,104 characters |"), "DeepL row");
            Assert.True(md.Contains("52 characters (computed)"), "Google's count is computed");
            Assert.True(md.Contains("1,200 / 40"), "the AI row keeps tokens");
            Assert.True(md.Contains("at list price, in US dollars"), "list-price note");

            var aiOnly = new SuperBench.Run { Sources = { "x" } };
            aiOnly.Contenders.Add(new SuperBench.Contender { Provider = "claude", Model = "m", Translations = { "y" } });
            SuperBench.AssignBlindLabels(aiOnly);
            var plain = SuperBench.RenderMarkdown(aiOnly);
            Assert.True(!plain.Contains("Machine translation:") && !plain.Contains("list price"), "nothing about MT without an engine");
            }
        }

        public static void Mt_SuperBench_JudgeHeadingsNameTheProject_NotTheCandidates()
        {
            var run = new SuperBench.Run { Sources = { "x" } };
            run.Contenders.Add(new SuperBench.Contender { Provider = "deepl", Translations = { "y" } });
            SuperBench.AssignBlindLabels(run);
            var prompt = SuperBench.BuildJudgeUserPrompt(run, "Use British spelling.", "- valve → klep");
            Assert.True(prompt.Contains("# THE PROJECT'S TRANSLATION INSTRUCTIONS") && prompt.Contains("# THE PROJECT'S APPROVED TERMS"), "project headings");
            Assert.True(!prompt.Contains("CANDIDATES WERE GIVEN"), "nothing a machine-translation candidate was not given");
            Assert.True(!prompt.Contains("DeepL") && !prompt.Contains("deepl"), "the judge is never told which is the engine");
        }

        // ─── Helpers ───────────────────────────────────────────────────────────────

        private static MtOutcome Run(MtJob job, Action<int, string[]> chunkDone) =>
            MachineTranslation.TranslateAsync(job, chunkDone, CancellationToken.None).GetAwaiter().GetResult();

        private static MtJob Many(string engine, int n) => new MtJob
        {
            Engine = engine, ApiKey = Key, SourceLanguage = "en-GB", TargetLanguage = "nl-NL", SettingsPlace = Place,
            Texts = Enumerable.Range(0, n).Select(i => "Segment " + i + " of Acme PROJ-001, with <t1>a tag</t1> and some words.").ToList(),
        };

        private static string Show(List<(int Start, int Count)> chunks) => string.Join(",", chunks.Select(c => c.Start + "+" + c.Count));

        /// <summary>Each engine's success reply, with every text sent back as it came.</summary>
        private static Task<HttpResponseMessage> Echo(SeenRequest req, int index, CancellationToken ct)
        {
            var json = new JavaScriptSerializer();
            if (req.Url.Contains("deepl.com"))
            {
                var texts = ((object[])req.Json["text"]).Cast<string>();
                var reply = new { translations = texts.Select(t => new { text = t, billed_characters = 7, detected_source_language = "EN" }) };
                return Task.FromResult(Reply(200, json.Serialize(reply)));
            }
            if (req.Url.Contains("googleapis.com"))
            {
                var texts = ((object[])req.Json["q"]).Cast<string>();
                var reply = new { data = new { translations = texts.Select(t => new { translatedText = t }) } };
                return Task.FromResult(Reply(200, json.Serialize(reply)));
            }
            var items = ((object[])json.DeserializeObject(req.Body)).Cast<IDictionary<string, object>>();
            var ms = Reply(200, json.Serialize(items.Select(it => new { translations = new[] { new { text = it["Text"] as string, to = "nl" } } })));
            ms.Headers.TryAddWithoutValidation("X-metered-usage", "777");
            return Task.FromResult(ms);
        }

        private static HttpResponseMessage Reply(int status, string body, int? retryAfterSeconds = null)
        {
            var r = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (retryAfterSeconds.HasValue) r.Headers.TryAddWithoutValidation("Retry-After", retryAfterSeconds.Value.ToString());
            return r;
        }

        // Google's and Microsoft's language lists as they gave them on 1 Nov 2026.
        private static readonly string[] GoogleList =
        {
            "ab", "ace", "ach", "af", "sq", "alz", "am", "ar", "hy", "as", "awa", "ay", "az", "ban", "bm", "ba",
            "eu", "btx", "bts", "bbc", "be", "bem", "bn", "bew", "bho", "bik", "bs", "br", "bg", "bua", "yue", "ca",
            "ceb", "ny", "zh", "zh-TW", "cv", "co", "crh", "hr", "cs", "da", "dv", "din", "doi", "dov", "nl", "dz",
            "en", "eo", "et", "ee", "fj", "tl", "fi", "fr", "fr-CA", "fy", "ff", "gaa", "gl", "ka", "de", "el", "gn",
            "gu", "ht", "cnh", "ha", "haw", "iw", "hil", "hi", "hmn", "hu", "hrx", "is", "ig", "ilo", "id", "ga",
            "it", "ja", "jw", "kn", "pam", "kk", "km", "cgg", "rw", "ktu", "gom", "ko", "kri", "ku", "ckb", "ky",
            "lo", "ltg", "la", "lv", "lij", "li", "ln", "lt", "lmo", "lg", "luo", "lb", "mk", "mai", "mak", "mg",
            "ms", "ms-Arab", "ml", "mt", "mi", "mr", "chm", "mni-Mtei", "min", "lus", "mn", "my", "nr", "new", "ne",
            "no", "nus", "oc", "or", "om", "pag", "pap", "ps", "fa", "pl", "pt", "pt-PT", "pa", "pa-Arab", "qu",
            "rom", "ro", "rn", "ru", "sm", "sg", "sa", "gd", "nso", "sr", "st", "crs", "shn", "sn", "scn", "szl",
            "sd", "si", "sk", "sl", "so", "es", "su", "sw", "ss", "sv", "tg", "ta", "tt", "te", "tet", "th", "ti",
            "ts", "tn", "tr", "tk", "ak", "uk", "ur", "ug", "uz", "vi", "cy", "xh", "yi", "yo", "yua", "zu", "he",
            "jv", "zh-CN",
        };

        private static readonly string[] MicrosoftList =
        {
            "af", "am", "ar", "as", "az", "ba", "be", "bg", "bho", "bn", "bo", "brx", "bs", "ca", "cs", "cy", "da",
            "de", "doi", "dsb", "dv", "el", "en", "es", "es-MX", "et", "eu", "fa", "fi", "fil", "fj", "fo", "fr",
            "fr-CA", "ga", "gl", "gom", "gu", "ha", "he", "hi", "hne", "hr", "hsb", "ht", "hu", "hy", "id", "ig",
            "ikt", "is", "it", "iu", "iu-Latn", "ja", "ka", "kk", "km", "kmr", "kn", "ko", "ks", "ku", "ky", "lb",
            "ln", "lo", "lt", "lug", "lv", "lzh", "mai", "mg", "mi", "mk", "ml", "mn-Cyrl", "mn-Mong", "mni", "mr",
            "ms", "mt", "mww", "my", "nb", "ne", "nl", "nso", "nya", "or", "otq", "pa", "pl", "prs", "ps", "pt",
            "pt-PT", "ro", "ru", "run", "rw", "sd", "si", "sk", "sl", "sm", "sn", "so", "sq", "sr-Cyrl", "sr-Latn",
            "st", "sv", "sw", "ta", "te", "th", "ti", "tk", "tlh-Latn", "tlh-Piqd", "tn", "to", "tr", "tt", "ty",
            "ug", "uk", "ur", "uz", "vi", "xh", "yo", "yua", "yue", "zh-Hans", "zh-Hant", "zu",
        };
    }

    /// <summary>Numbers formatted the English way for the length of a test, whatever this PC's region.</summary>
    internal sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous = Thread.CurrentThread.CurrentCulture;
        public CultureScope() { Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US"); }
        public void Dispose() { Thread.CurrentThread.CurrentCulture = _previous; }
    }

    /// <summary>One request as the fake engine saw it.</summary>
    internal sealed class SeenRequest
    {
        public string Url, Body;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        public IDictionary<string, object> Json => (IDictionary<string, object>)new JavaScriptSerializer().DeserializeObject(Body);
    }

    /// <summary>
    /// The engines replaced by a fake for the length of a test: every request is
    /// recorded and answered by <c>respond(request, its index, token)</c>. Retries
    /// wait a millisecond instead of seconds. Puts everything back on Dispose.
    /// </summary>
    internal sealed class MtScene : IDisposable
    {
        public readonly List<SeenRequest> Requests = new List<SeenRequest>();
        private readonly HttpClient _previous = MachineTranslation.Http;
        private readonly TimeSpan _previousRetry = MachineTranslation.RetryBase, _previousTimeout = MachineTranslation.RequestTimeout;

        public MtScene(Func<SeenRequest, int, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            MachineTranslation.Http = new HttpClient(new Handler(this, respond)) { Timeout = Timeout.InfiniteTimeSpan };
            MachineTranslation.RetryBase = TimeSpan.FromMilliseconds(1);
            MachineTranslation.RequestTimeout = TimeSpan.FromSeconds(10);
        }

        public MtScene(Func<SeenRequest, int, CancellationToken, HttpResponseMessage> respond)
            : this((r, i, ct) => Task.FromResult(respond(r, i, ct))) { }

        public void Dispose()
        {
            MachineTranslation.Http = _previous;
            MachineTranslation.RetryBase = _previousRetry;
            MachineTranslation.RequestTimeout = _previousTimeout;
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly MtScene _scene;
            private readonly Func<SeenRequest, int, CancellationToken, Task<HttpResponseMessage>> _respond;

            public Handler(MtScene scene, Func<SeenRequest, int, CancellationToken, Task<HttpResponseMessage>> respond)
            {
                _scene = scene;
                _respond = respond;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var seen = new SeenRequest
                {
                    Url = request.RequestUri.ToString(),
                    Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(),
                };
                foreach (var h in request.Headers) seen.Headers[h.Key] = string.Join(",", h.Value);
                int index;
                lock (_scene.Requests) { _scene.Requests.Add(seen); index = _scene.Requests.Count - 1; }
                return await _respond(seen, index, ct);
            }
        }
    }
}
