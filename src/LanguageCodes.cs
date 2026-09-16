using System;
using System.Collections.Generic;

namespace Supervertaler.Core
{
    /// <summary>
    /// One place that knows a language is the same language however it was
    /// spelled.
    ///
    /// <para><b>Why this exists.</b> The three products and the tools they live
    /// in do not agree on how to name a language. memoQ hands a plugin
    /// <c>dut-NL</c> and <c>eng-GB</c>; Trados Studio works in .NET culture names
    /// like <c>en-GB</c> and <c>nl-NL</c>; the shared termbase database holds
    /// <c>nl</c>, <c>en</c>, <c>de</c>, and occasionally <c>en-GB</c>. Any
    /// question of the form "is this termbase in the language this job needs?"
    /// has to normalise both sides before it can be answered at all.</para>
    ///
    /// <para><b>The bug that earned it.</b> A termbase is stored one way round.
    /// Of the 84 in one real database, 61 are nl-to-en and 19 are en-to-nl, and
    /// the 19 are just as useful on a Dutch-to-English job read backwards. Until
    /// Supervertaler for memoQ learned to turn them, those 19 were silently dead:
    /// it matched the source column against the source segment, so they were
    /// asked to find English words in Dutch text and answered almost nothing. The
    /// single thing that did match was "water", which is spelled the same in both
    /// languages and so appears in both columns - which is exactly why it looked
    /// like a working feature. Getting that comparison right requires this table,
    /// and having it in two places is how the two products would come to disagree
    /// about it.</para>
    ///
    /// <para><b>An unknown code is left alone, never guessed.</b> The obvious
    /// shortcut - take the first two letters of a three-letter code - is wrong
    /// far more often than it looks: <c>tur</c> is <c>tr</c> and not <c>tu</c>,
    /// <c>spa</c> is <c>es</c>, <c>ger</c> is <c>de</c>. Worse, it can invent a
    /// match rather than merely miss one: <c>swe</c> truncates to <c>sw</c>,
    /// which is Swahili. So anything not in the table below comes back as it went
    /// in, and a comparison against it simply fails - which leaves the caller
    /// doing whatever it does when it cannot tell, rather than acting on a wrong
    /// answer.</para>
    ///
    /// <para>The table is the full ISO 639-1 set with both ISO 639-2 forms - the
    /// bibliographic codes CAT tools favour (<c>ger</c>, <c>dut</c>, <c>fre</c>)
    /// and the terminological ones (<c>deu</c>, <c>nld</c>, <c>fra</c>) - plus
    /// English names, because some hosts pass those instead, and the handful of
    /// superseded codes still in the wild.</para>
    /// </summary>
    public static class LanguageCodes
    {
        // iso639-1 | iso639-2/B | iso639-2/T | English name(s)
        private static readonly string[] Table =
        {
            "aa|aar|aar|Afar",
            "ab|abk|abk|Abkhazian",
            "ae|ave|ave|Avestan",
            "af|afr|afr|Afrikaans",
            "ak|aka|aka|Akan",
            "am|amh|amh|Amharic",
            "an|arg|arg|Aragonese",
            "ar|ara|ara|Arabic",
            "as|asm|asm|Assamese",
            "av|ava|ava|Avaric",
            "ay|aym|aym|Aymara",
            "az|aze|aze|Azerbaijani",
            "ba|bak|bak|Bashkir",
            "be|bel|bel|Belarusian",
            "bg|bul|bul|Bulgarian",
            "bi|bis|bis|Bislama",
            "bm|bam|bam|Bambara",
            "bn|ben|ben|Bengali",
            "bo|tib|bod|Tibetan",
            "br|bre|bre|Breton",
            "bs|bos|bos|Bosnian",
            "ca|cat|cat|Catalan,Valencian",
            "ce|che|che|Chechen",
            "ch|cha|cha|Chamorro",
            "co|cos|cos|Corsican",
            "cr|cre|cre|Cree",
            "cs|cze|ces|Czech",
            "cu|chu|chu|Church Slavic",
            "cv|chv|chv|Chuvash",
            "cy|wel|cym|Welsh",
            "da|dan|dan|Danish",
            "de|ger|deu|German",
            "dv|div|div|Divehi,Dhivehi,Maldivian",
            "dz|dzo|dzo|Dzongkha",
            "ee|ewe|ewe|Ewe",
            "el|gre|ell|Greek",
            "en|eng|eng|English",
            "eo|epo|epo|Esperanto",
            "es|spa|spa|Spanish,Castilian",
            "et|est|est|Estonian",
            "eu|baq|eus|Basque",
            "fa|per|fas|Persian,Farsi",
            "ff|ful|ful|Fulah",
            "fi|fin|fin|Finnish",
            "fj|fij|fij|Fijian",
            "fo|fao|fao|Faroese",
            "fr|fre|fra|French",
            "fy|fry|fry|Western Frisian,Frisian",
            "ga|gle|gle|Irish",
            "gd|gla|gla|Scottish Gaelic,Gaelic",
            "gl|glg|glg|Galician",
            "gn|grn|grn|Guarani",
            "gu|guj|guj|Gujarati",
            "gv|glv|glv|Manx",
            "ha|hau|hau|Hausa",
            "he|heb|heb|Hebrew",
            "hi|hin|hin|Hindi",
            "ho|hmo|hmo|Hiri Motu",
            "hr|hrv|hrv|Croatian",
            "ht|hat|hat|Haitian,Haitian Creole",
            "hu|hun|hun|Hungarian",
            "hy|arm|hye|Armenian",
            "hz|her|her|Herero",
            "ia|ina|ina|Interlingua",
            "id|ind|ind|Indonesian",
            "ie|ile|ile|Interlingue,Occidental",
            "ig|ibo|ibo|Igbo",
            "ii|iii|iii|Sichuan Yi,Nuosu",
            "ik|ipk|ipk|Inupiaq",
            "io|ido|ido|Ido",
            "is|ice|isl|Icelandic",
            "it|ita|ita|Italian",
            "iu|iku|iku|Inuktitut",
            "ja|jpn|jpn|Japanese",
            "jv|jav|jav|Javanese",
            "ka|geo|kat|Georgian",
            "kg|kon|kon|Kongo",
            "ki|kik|kik|Kikuyu,Gikuyu",
            "kj|kua|kua|Kuanyama,Kwanyama",
            "kk|kaz|kaz|Kazakh",
            "kl|kal|kal|Kalaallisut,Greenlandic",
            "km|khm|khm|Central Khmer,Khmer",
            "kn|kan|kan|Kannada",
            "ko|kor|kor|Korean",
            "kr|kau|kau|Kanuri",
            "ks|kas|kas|Kashmiri",
            "ku|kur|kur|Kurdish",
            "kv|kom|kom|Komi",
            "kw|cor|cor|Cornish",
            "ky|kir|kir|Kirghiz,Kyrgyz",
            "la|lat|lat|Latin",
            "lb|ltz|ltz|Luxembourgish,Letzeburgesch",
            "lg|lug|lug|Ganda",
            "li|lim|lim|Limburgan,Limburgish",
            "ln|lin|lin|Lingala",
            "lo|lao|lao|Lao",
            "lt|lit|lit|Lithuanian",
            "lu|lub|lub|Luba-Katanga",
            "lv|lav|lav|Latvian",
            "mg|mlg|mlg|Malagasy",
            "mh|mah|mah|Marshallese",
            "mi|mao|mri|Maori",
            "mk|mac|mkd|Macedonian",
            "ml|mal|mal|Malayalam",
            "mn|mon|mon|Mongolian",
            "mr|mar|mar|Marathi",
            "ms|may|msa|Malay",
            "mt|mlt|mlt|Maltese",
            "my|bur|mya|Burmese",
            "na|nau|nau|Nauru",
            "nb|nob|nob|Norwegian Bokmal,Norwegian Bokmål",
            "nd|nde|nde|North Ndebele",
            "ne|nep|nep|Nepali",
            "ng|ndo|ndo|Ndonga",
            "nl|dut|nld|Dutch,Flemish",
            "nn|nno|nno|Norwegian Nynorsk",
            "no|nor|nor|Norwegian",
            "nr|nbl|nbl|South Ndebele",
            "nv|nav|nav|Navajo,Navaho",
            "ny|nya|nya|Chichewa,Chewa,Nyanja",
            "oc|oci|oci|Occitan",
            "oj|oji|oji|Ojibwa",
            "om|orm|orm|Oromo",
            "or|ori|ori|Oriya,Odia",
            "os|oss|oss|Ossetian,Ossetic",
            "pa|pan|pan|Panjabi,Punjabi",
            "pi|pli|pli|Pali",
            "pl|pol|pol|Polish",
            "ps|pus|pus|Pushto,Pashto",
            "pt|por|por|Portuguese",
            "qu|que|que|Quechua",
            "rm|roh|roh|Romansh",
            "rn|run|run|Rundi",
            "ro|rum|ron|Romanian,Moldavian,Moldovan",
            "ru|rus|rus|Russian",
            "rw|kin|kin|Kinyarwanda",
            "sa|san|san|Sanskrit",
            "sc|srd|srd|Sardinian",
            "sd|snd|snd|Sindhi",
            "se|sme|sme|Northern Sami",
            "sg|sag|sag|Sango",
            "si|sin|sin|Sinhala,Sinhalese",
            "sk|slo|slk|Slovak",
            "sl|slv|slv|Slovenian,Slovene",
            "sm|smo|smo|Samoan",
            "sn|sna|sna|Shona",
            "so|som|som|Somali",
            "sq|alb|sqi|Albanian",
            "sr|srp|srp|Serbian",
            "ss|ssw|ssw|Swati",
            "st|sot|sot|Southern Sotho",
            "su|sun|sun|Sundanese",
            "sv|swe|swe|Swedish",
            "sw|swa|swa|Swahili",
            "ta|tam|tam|Tamil",
            "te|tel|tel|Telugu",
            "tg|tgk|tgk|Tajik",
            "th|tha|tha|Thai",
            "ti|tir|tir|Tigrinya",
            "tk|tuk|tuk|Turkmen",
            "tl|tgl|tgl|Tagalog",
            "tn|tsn|tsn|Tswana",
            "to|ton|ton|Tonga",
            "tr|tur|tur|Turkish",
            "ts|tso|tso|Tsonga",
            "tt|tat|tat|Tatar",
            "tw|twi|twi|Twi",
            "ty|tah|tah|Tahitian",
            "ug|uig|uig|Uighur,Uyghur",
            "uk|ukr|ukr|Ukrainian",
            "ur|urd|urd|Urdu",
            "uz|uzb|uzb|Uzbek",
            "ve|ven|ven|Venda",
            "vi|vie|vie|Vietnamese",
            "vo|vol|vol|Volapuk,Volapük",
            "wa|wln|wln|Walloon",
            "wo|wol|wol|Wolof",
            "xh|xho|xho|Xhosa",
            "yi|yid|yid|Yiddish",
            "yo|yor|yor|Yoruba",
            "za|zha|zha|Zhuang,Chuang",
            "zh|chi|zho|Chinese,Mandarin",
            "zu|zul|zul|Zulu"
        };

        /// <summary>
        /// Codes that are no longer correct but are still handed to us by real
        /// software. ISO reassigned the first three in 1989 and plenty of systems
        /// never noticed; <c>sh</c> outlived the country it was named for.
        /// </summary>
        private static readonly string[] Superseded =
        {
            "iw|he",    // Hebrew, before 1989
            "in|id",    // Indonesian, before 1989
            "ji|yi",    // Yiddish, before 1989
            "mo|ro",    // Moldavian, merged into Romanian
            "sh|sr",    // Serbo-Croatian; Serbian is the closest surviving code
            "tl|tl",    // Tagalog: kept explicitly, since "fil" is a near-synonym
            "fil|tl",   // Filipino, which CAT tools and ISO disagree about
            "iw-IL|he"
        };

        private static readonly Dictionary<string, string> _toTwoLetter = Build();

        private static Dictionary<string, string> Build()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in Table)
            {
                var parts = row.Split('|');
                var two = parts[0];

                map[two] = two;
                map[parts[1]] = two;
                map[parts[2]] = two;

                foreach (var name in parts[3].Split(','))
                {
                    var trimmed = name.Trim();
                    if (trimmed.Length > 0) map[trimmed] = two;
                }
            }

            foreach (var row in Superseded)
            {
                var parts = row.Split('|');
                map[parts[0]] = parts[1];
            }

            return map;
        }

        /// <summary>
        /// The two-letter code for a language, given any of the spellings above -
        /// with any region or script subtag removed, so <c>en-GB</c>,
        /// <c>eng-GB</c> and <c>English</c> all come back as <c>en</c>.
        ///
        /// <para>A code this table does not know comes back trimmed and
        /// lower-cased but otherwise untouched. It is never guessed at: see the
        /// class remarks for why truncating a three-letter code is worse than
        /// doing nothing.</para>
        /// </summary>
        public static string Normalise(string code)
        {
            var value = (code ?? string.Empty).Trim();
            if (value.Length == 0) return string.Empty;

            string mapped;

            // Whole string first: a name like "Norwegian Bokmal" has a space in
            // it, and "zh-Hant" may one day be worth distinguishing, so the exact
            // spelling gets its chance before anything is cut off it.
            if (_toTwoLetter.TryGetValue(value, out mapped)) return mapped;

            var cut = value.IndexOfAny(new[] { '-', '_' });
            if (cut > 0)
            {
                var stem = value.Substring(0, cut);
                if (_toTwoLetter.TryGetValue(stem, out mapped)) return mapped;
                return stem.ToLowerInvariant();
            }

            return value.ToLowerInvariant();
        }

        /// <summary>
        /// Whether two spellings mean the same language. Two codes this table
        /// does not know are the same only if they are written the same way.
        /// </summary>
        public static bool AreSame(string a, string b)
        {
            var left = Normalise(a);
            var right = Normalise(b);

            return left.Length > 0
                && right.Length > 0
                && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether this is a language the table recognises. Worth asking before
        /// reporting that two languages differ, since "I do not know" and "they
        /// are different" deserve different words in front of a user.
        /// </summary>
        public static bool IsKnown(string code)
        {
            var value = (code ?? string.Empty).Trim();
            if (value.Length == 0) return false;
            if (_toTwoLetter.ContainsKey(value)) return true;

            var cut = value.IndexOfAny(new[] { '-', '_' });
            return cut > 0 && _toTwoLetter.ContainsKey(value.Substring(0, cut));
        }
    }
}
