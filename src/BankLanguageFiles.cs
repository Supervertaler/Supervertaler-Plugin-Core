using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Supervertaler.Core
{
    /// <summary>
    /// Memory bank files written for one target language: <c>style.nl-BE.md</c>,
    /// <c>style.fr-FR.md</c>, <c>style.nl.md</c>. They are sent only with work into
    /// that language, so a bank can hold house conventions for several languages
    /// without every request carrying all of them (asked for by an agency that keeps
    /// conventions for nl-BE, fr-FR, en-GB and en-US).
    ///
    /// <para><b>Which files.</b> A root <c>*.md</c> whose name ends in a language tag
    /// before <c>.md</c>, after a name: <c>name.xx.md</c> or <c>name.xx-YY.md</c>,
    /// with <c>xx</c> a two-letter ISO 639-1 code and an optional script
    /// (<c>Latn</c>) and region (<c>BE</c>, <c>419</c>); <c>_</c> is accepted for
    /// <c>-</c>. Only two-letter codes: three-letter ones collide with ordinary words
    /// (<c>may</c>, <c>per</c>, <c>cat</c> are all ISO 639-2), and a bank file called
    /// <c>notes.may.md</c> must not vanish from French jobs. Codes ISO withdrew
    /// (<c>in</c>, <c>iw</c>) are not tags either. Everything else is a general file
    /// and is sent as before.</para>
    ///
    /// <para><b>Which language.</b> A file for a language (<c>style.nl.md</c>) goes
    /// with every variant of it: nl-NL, nl-BE, plain nl. A file for a variant
    /// (<c>style.nl-BE.md</c>) goes only with that variant. A job into nl-BE gets
    /// both, and the prompt says the variant's file wins where they disagree. A job
    /// whose target is only "Dutch", with no variant, gets <c>style.nl.md</c> but no
    /// variant file, since it cannot be told which variant is meant.</para>
    ///
    /// <para><b>No target language known</b> (the chat with no document open, an MCP
    /// tool called without one): every language file is sent, each under its own
    /// name, as before this existed. Nothing is hidden on a guess.</para>
    ///
    /// <para>The target arrives however the product spells it: <c>nl-BE</c>,
    /// <c>nl_BE</c>, memoQ's <c>dut-BE</c>, Studio's display name
    /// <c>Dutch (Belgium)</c>, or just <c>Dutch</c>. A name nothing recognises counts
    /// as unknown.</para>
    /// </summary>
    public static class BankLanguageFiles
    {
        public enum Fit
        {
            /// <summary>Not a language file: sent as before.</summary>
            General,
            /// <summary>A file for the target's language (style.nl.md for nl-BE).</summary>
            Language,
            /// <summary>A file for the target's exact variant (style.nl-BE.md for nl-BE).</summary>
            Variant,
            /// <summary>A file for another language or variant: not sent.</summary>
            Other,
            /// <summary>A language file, but the target is not known: sent.</summary>
            TargetUnknown
        }

        // name . xx [-Script] [-RR|-999] . md   (the name part may itself contain dots)
        private static readonly Regex TagPattern = new Regex(
            @"^(?<stem>.+)\.(?<lang>[A-Za-z]{2})(?:[-_](?<script>[A-Za-z]{4}))?(?:[-_](?<region>[A-Za-z]{2}|\d{3}))?\.md$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex CodePattern = new Regex(
            @"^[A-Za-z]{2,3}(?:[-_][A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant);

        /// <summary>
        /// The language tag a bank file is written for, in canonical form
        /// (<c>nl-BE</c>, <c>sr-Latn-RS</c>, <c>nl</c>), or null for a general file.
        /// </summary>
        public static string TagOf(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            var m = TagPattern.Match(fileName);
            if (!m.Success) return null;
            if (m.Groups["stem"].Value.Trim('.').Length == 0) return null;

            var lang = m.Groups["lang"].Value.ToLowerInvariant();
            // A live two-letter code that is its own canonical form: "in" (now "id")
            // and the like are not accepted, nor anything the table does not know.
            if (!LanguageCodes.IsKnown(lang) || LanguageCodes.Normalise(lang) != lang) return null;

            return Join(lang, m.Groups["script"].Value, m.Groups["region"].Value);
        }

        /// <summary>
        /// The target language in canonical form (<c>nl-BE</c>, or <c>nl</c> when no
        /// variant is given), or null when it is empty or not recognised.
        /// </summary>
        public static string ResolveTarget(string target)
        {
            var value = (target ?? "").Trim();
            if (value.Length == 0) return null;

            // A code: nl-BE, nl_BE, dut-BE, nl, eng.
            if (CodePattern.IsMatch(value) && LanguageCodes.IsKnown(value))
            {
                var parts = value.Split('-', '_');
                var lang = LanguageCodes.Normalise(parts[0]);
                string script = "", region = "";
                for (int i = 1; i < parts.Length; i++)
                {
                    if (parts[i].Length == 4 && script.Length == 0 && region.Length == 0) script = parts[i];
                    else if (region.Length == 0) region = parts[i];
                }
                return Join(lang, script, region);
            }

            // A display name: "Dutch (Belgium)", as Trados Studio passes it.
            string name;
            if (DisplayNames.Value.TryGetValue(value, out name)) return name;

            // A bare language name: "Dutch", "Flemish".
            if (LanguageCodes.IsKnown(value)) return LanguageCodes.Normalise(value);

            return null;
        }

        /// <summary>How a bank file relates to the target language. <paramref name="target"/> as passed by the product.</summary>
        public static Fit Classify(string fileName, string target)
        {
            var tag = TagOf(fileName);
            if (tag == null) return Fit.General;

            var want = ResolveTarget(target);
            if (want == null) return Fit.TargetUnknown;

            var fileLang = LangOf(tag);
            if (!string.Equals(fileLang, LangOf(want), StringComparison.OrdinalIgnoreCase)) return Fit.Other;
            if (tag.Length == fileLang.Length) return Fit.Language;            // style.nl.md
            return string.Equals(tag, want, StringComparison.OrdinalIgnoreCase) ? Fit.Variant : Fit.Other;
        }

        /// <summary>
        /// Order of sending: general files, then language files, then variant files,
        /// each by name. The prompt says a later, more specific file wins where they
        /// disagree, and the order shows the same thing.
        /// </summary>
        public static int Rank(Fit fit)
        {
            switch (fit)
            {
                case Fit.General: return 0;
                case Fit.Language: return 1;
                case Fit.TargetUnknown: return 1;
                case Fit.Variant: return 2;
                default: return 3;
            }
        }

        private static string LangOf(string tag)
        {
            var cut = tag.IndexOf('-');
            return cut < 0 ? tag : tag.Substring(0, cut);
        }

        private static string Join(string lang, string script, string region)
        {
            var s = lang.ToLowerInvariant();
            if (!string.IsNullOrEmpty(script))
                s += "-" + char.ToUpperInvariant(script[0]) + script.Substring(1).ToLowerInvariant();
            if (!string.IsNullOrEmpty(region))
                s += "-" + region.ToUpperInvariant();
            return s;
        }

        // English and local display names of every specific culture ("Dutch
        // (Belgium)" -> nl-BE), built once on first use: a few hundred cultures,
        // a few milliseconds.
        private static readonly Lazy<Dictionary<string, string>> DisplayNames =
            new Lazy<Dictionary<string, string>>(() =>
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var c in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
                    {
                        if (string.IsNullOrEmpty(c.Name)) continue;
                        var parts = c.Name.Split('-');
                        var lang = LanguageCodes.Normalise(parts[0]);
                        if (lang.Length != 2) continue;
                        string script = "", region = "";
                        for (int i = 1; i < parts.Length; i++)
                        {
                            if (parts[i].Length == 4 && script.Length == 0) script = parts[i];
                            else region = parts[i];
                        }
                        var tag = Join(lang, script, region);
                        foreach (var n in new[] { c.EnglishName, c.DisplayName, c.NativeName })
                            if (!string.IsNullOrWhiteSpace(n) && !map.ContainsKey(n)) map[n] = tag;
                    }
                }
                catch { /* no culture data: display names stay unknown */ }
                return map;
            });
    }
}
