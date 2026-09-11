using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Supervertaler.Core
{
    /// <summary>
    /// Finds parentheticals in Dutch source text that may be an English gloss of
    /// the words in front of them (issue #113).
    ///
    /// <para>Dutch technical writers routinely gloss a Dutch term with its English
    /// equivalent in brackets. In the Dutch that does real work - it tells a Dutch
    /// reader what the thing is called in English. Translate the head term and the
    /// gloss collapses onto it, so the English says the same thing twice.</para>
    ///
    /// <para><b>This class only nominates candidates. It never decides.</b> Whether
    /// a bracket duplicates the translation of its head term is a question about a
    /// translation that does not exist yet, so it is put to the model, one candidate
    /// at a time, and the answer is written into the generated prompt as a lookup
    /// rather than left as a judgement to be re-made on every segment.</para>
    ///
    /// <para><b>Deliberately permissive.</b> A candidate that turns out not to be a
    /// gloss costs one cheap model call and comes back KEEP. A gloss that never
    /// becomes a candidate is invisible. The filters below therefore only remove
    /// what is certainly not an English gloss - shapes (reference signs, acronyms,
    /// units, product codes) and spans carrying an everyday Dutch word.</para>
    ///
    /// <para><b>Dutch source only.</b> The discriminator is a list of Dutch function
    /// words; on any other source language it would be meaningless, so
    /// <see cref="FindCandidates"/> returns nothing rather than guessing.</para>
    /// </summary>
    public static class GlossDetector
    {
        /// <summary>A parenthetical that might be an English gloss.</summary>
        public class Candidate
        {
            /// <summary>1-based position of the segment in the document.</summary>
            public int SegmentNumber;
            /// <summary>The segment's full source text, for the model to read.</summary>
            public string SegmentText;
            /// <summary>The bracketed text exactly as written, brackets included.</summary>
            public string Parenthetical;
            /// <summary>The bracket's contents after stripping any "of" and quotes.</summary>
            public string GlossText;
        }

        /// <summary>
        /// Everyday Dutch words. One of these inside a bracket means the bracket is
        /// Dutch prose - an ordinary aside, not an English gloss. Function words and
        /// the handful of nouns that introduce one ("het materiaal"); not technical
        /// vocabulary, which is often spelled alike in both languages and would
        /// reject real glosses.
        /// </summary>
        private static readonly HashSet<string> DutchWords = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "de", "het", "een", "en", "maar", "want", "dus", "als", "dan", "ook",
            "zoals", "bijvoorbeeld", "bijv", "onder", "andere", "meer", "zeer",
            "niet", "geen", "wel", "deze", "dit", "die", "dat", "welke", "waarbij",
            "waarin", "waarvan", "hierbij", "hierin", "daarbij", "daarin",
            "met", "zonder", "voor", "naar", "door", "over", "tussen", "tegen",
            "bij", "uit", "tot", "vanaf", "volgens", "aan", "om", "per",
            "zijn", "is", "was", "wordt", "worden", "werd", "heeft", "hebben",
            "kan", "kunnen", "kunnen", "moet", "moeten", "zal", "zullen",
            "ten", "te", "der", "des", "aldus", "eveneens", "tevens", "alsook",
            "alleen", "vaste", "vast", "beide", "elk", "elke", "enkele", "enige",
            "materiaal", "stof", "middel", "deel", "vorm", "soort"
        };

        /// <summary>
        /// Words that introduce a gloss rather than being part of it. Stripped from
        /// the front before the Dutch check, because "of" is itself a Dutch word and
        /// would otherwise reject the clearest gloss form there is:
        /// <c>(of "methylene diphenyl diisocyanate")</c>.
        /// </summary>
        private static readonly string[] GlossIntroducers =
            { "of", "ofwel", "oftewel", "d.w.z.", "dwz", "i.e.", "resp.", "resp" };

        /// <summary>
        /// Above this many candidates in one document the register is not worth
        /// building: it would mean the filters are not discriminating, and it would
        /// put that many model calls in front of a prompt generation. A real
        /// 880-segment patent produced four out of 103 parentheticals.
        ///
        /// <para>The cap is applied by the CALLER, not here, so that abandoning the
        /// register is logged rather than silent. An earlier version returned an
        /// empty list when the cap was exceeded, which reads exactly like "this
        /// document has no glosses" - it took a breadth run to notice that 47
        /// candidates and 0 candidates produced the same answer.</para>
        /// </summary>
        public const int MaxCandidates = 40;

        private static readonly Regex Parentheticals =
            new Regex(@"\(([^()]{1,200})\)", RegexOptions.Compiled);

        // A reference sign, a percentage, a measurement: digits with only
        // punctuation, units or a percent sign around them. "(24)", "(100% w/w)".
        private static readonly Regex NumericOrUnit = new Regex(
            @"^[\d\s.,;:/×x%°+\-–—]*(?:[a-zA-Z]{1,4}(?:/[a-zA-Z]{1,4})?)?[\d\s.,;:/×x%°+\-–—]*$",
            RegexOptions.Compiled);

        // An acronym or abbreviation alone: "(APP)", "(VOCs)", "(ATH)".
        private static readonly Regex AcronymOnly =
            new Regex(@"^[A-Z][A-Za-z0-9]{0,7}s?$", RegexOptions.Compiled);

        // A product or model code: an uppercase run with digits and punctuation.
        // "(COSMO PU-221.900)".
        private static readonly Regex ProductCode =
            new Regex(@"^[A-Z0-9][A-Z0-9\s.\-/]*\d[A-Z0-9\s.\-/]*$", RegexOptions.Compiled);

        /// <summary>
        /// Nominates every parenthetical that could be an English gloss.
        /// <paramref name="sourceSegments"/> is the document's source text in order;
        /// the returned <see cref="Candidate.SegmentNumber"/> is 1-based into it.
        ///
        /// Returns an empty list for a non-Dutch source. Does NOT apply
        /// <see cref="MaxCandidates"/> - see that constant.
        /// </summary>
        public static List<Candidate> FindCandidates(IList<string> sourceSegments, string sourceLanguage)
        {
            var found = new List<Candidate>();
            if (sourceSegments == null || sourceSegments.Count == 0) return found;
            if (!IsDutch(sourceLanguage)) return found;

            for (int i = 0; i < sourceSegments.Count; i++)
            {
                var text = sourceSegments[i];
                if (string.IsNullOrWhiteSpace(text) || text.IndexOf('(') < 0) continue;

                foreach (Match m in Parentheticals.Matches(text))
                {
                    var inner = m.Groups[1].Value.Trim();
                    var gloss = StripIntroducerAndQuotes(inner);
                    if (!CouldBeEnglishGloss(gloss)) continue;

                    found.Add(new Candidate
                    {
                        SegmentNumber = i + 1,
                        SegmentText = text,
                        Parenthetical = m.Value,
                        GlossText = gloss
                    });
                }
            }

            return found;
        }

        private static bool IsDutch(string language)
        {
            if (string.IsNullOrWhiteSpace(language)) return false;
            var l = language.Trim();
            return l.StartsWith("nl", StringComparison.OrdinalIgnoreCase)
                || l.StartsWith("dut", StringComparison.OrdinalIgnoreCase)
                || l.IndexOf("Dutch", StringComparison.OrdinalIgnoreCase) >= 0
                || l.IndexOf("Nederlands", StringComparison.OrdinalIgnoreCase) >= 0
                || l.IndexOf("Flemish", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Removes a leading gloss introducer and any surrounding quotes, straight
        /// or curly, so the language check sees the gloss itself.
        /// </summary>
        private static string StripIntroducerAndQuotes(string inner)
        {
            var s = (inner ?? "").Trim();

            foreach (var intro in GlossIntroducers)
            {
                if (s.Length > intro.Length
                    && s.StartsWith(intro, StringComparison.OrdinalIgnoreCase)
                    && !char.IsLetter(s[intro.Length]))
                {
                    s = s.Substring(intro.Length).Trim();
                    break;
                }
            }

            s = s.Trim('"', '“', '”', '‘', '’', '\'', ' ');
            return s.Trim();
        }

        /// <summary>
        /// Whether a bracket's contents could be an English gloss: not a shape we
        /// recognise as something else, and carrying no everyday Dutch word.
        /// </summary>
        private static bool CouldBeEnglishGloss(string gloss)
        {
            if (string.IsNullOrWhiteSpace(gloss)) return false;
            if (gloss.Length < 3) return false;

            // Shapes that are never a gloss.
            if (NumericOrUnit.IsMatch(gloss)) return false;
            if (AcronymOnly.IsMatch(gloss)) return false;
            if (ProductCode.IsMatch(gloss)) return false;

            // Inline-formatting placeholders: a bracketed formula, not prose.
            // On one real patent this alone was twenty of forty-seven false
            // nominations - "(Cu2<t1>+</t1> of Cu<t2>+</t2>)" and its siblings.
            if (gloss.IndexOf('<') >= 0 || gloss.IndexOf('>') >= 0) return false;

            // A gloss is a term, not a clause. Six words is generous for one.
            var words = gloss.Split(new[] { ' ', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                             .Select(w => w.Trim('.', ',', ';', ':', '"', '\'', '(', ')'))
                             .Where(w => w.Length > 0)
                             .ToList();
            if (words.Count > 6) return false;

            // Two words at least. A single word is where the Dutch-versus-English
            // question is hardest and the answer matters least: on a real patent
            // every one-word nomination was Dutch - "(zouten)", "(chelaat)",
            // "(2-hydroxyethylmethacrylaat)" - and a one-word English gloss that is
            // missed merely survives into the translation, which is the cheap
            // direction to be wrong in.
            if (words.Count < 2) return false;

            // A digit inside a word means a formula, a model number or a product
            // code: "(Organosorb 10-AA)", "(1,2,3-trihydroxybenzeen)".
            if (words.Any(w => w.Any(char.IsDigit))) return false;

            // A slash inside a word means a unit or a ratio: "(mol/kg hars)".
            if (words.Any(w => w.IndexOf('/') >= 0)) return false;

            // The discriminator: an everyday Dutch word means Dutch prose.
            if (words.Any(w => DutchWords.Contains(w))) return false;

            // Must be words, not only symbols.
            return words.All(w => w.Any(char.IsLetter))
                && words.Any(w => w.Length > 2);
        }
    }
}
