using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// The finder for restated passages on other kinds of document, and other
    /// languages, than the patent it was first measured on
    /// (docs/design/restated-passages.md). It has no idea what kind of document
    /// it is reading, and these tests are there to keep it so: a contract, a
    /// manual, software strings, and languages written with and without spaces.
    /// All invented.
    /// </summary>
    [Tests]
    internal static class RestatedDocumentTypesTests
    {
        private static List<RestatedTarget> Targets(params string[] texts) =>
            texts.Select((t, i) => new RestatedTarget { Label = "segment " + (i + 1), Text = t, HasNote = false }).ToList();

        private static string[] Lines(string summary) =>
            summary == null ? new string[0] : summary.Split(new[] { Environment.NewLine }, StringSplitOptions.None);

        // ---- a contract, English into German --------------------------------------

        private static readonly string[] Contract =
        {
            "This Agreement is made between Acme Ltd and the Supplier.",
            "\"Confidential Information\" means any information disclosed by one party to the other party in connection with this Agreement, whether in writing or orally.",
            "The Supplier shall deliver the Goods within thirty days.",
            "The Receiving Party shall not use any information disclosed by one party to the other party in connection with this Agreement for any other purpose.",
        };

        public static void Contract_ADefinitionRestatedInALaterClause_IsFound()
        {
            var found = RestatedPassages.Find(Contract);

            Assert.Equal(1, found.Pairs.Count, "the definition and the clause that restates it");
            Assert.Equal(1, found.Pairs[0].A, "the definition");
            Assert.Equal(3, found.Pairs[0].B, "the later clause");
            Assert.Equal(15, found.Pairs[0].PassageWords, "any information ... this Agreement");
        }

        public static void Contract_TheSameGerman_GivesNoLine_DifferentGerman_GivesOne()
        {
            const string definition = "„Vertrauliche Informationen“ sind alle Informationen, die eine Partei der anderen Partei im Zusammenhang mit diesem Vertrag offenlegt, sei es schriftlich oder mündlich.";
            var same = Targets(
                "Dieser Vertrag wird zwischen Acme Ltd und dem Lieferanten geschlossen.", definition,
                "Der Lieferant liefert die Waren innerhalb von dreißig Tagen.",
                "Die empfangende Partei darf alle Informationen, die eine Partei der anderen Partei im Zusammenhang mit diesem Vertrag offenlegt, nicht für andere Zwecke verwenden.");
            Assert.True(RestatedPassages.Report(Contract, same).Summary() == null, "worded the same: nothing to say");

            var different = Targets(same[0].Text, definition, same[2].Text,
                "Die empfangende Partei darf keine Informationen, die von einer Partei an die andere in Verbindung mit dieser Vereinbarung weitergegeben wurden, für andere Zwecke verwenden.");
            var lines = Lines(RestatedPassages.Report(Contract, different).Summary());
            Assert.Equal(2, lines.Length, "the count and the case");
            Assert.True(lines[1].StartsWith("Translated differently: segment 2 and segment 4 "), "names the two clauses: " + lines[1]);
        }

        // ---- a manual, English into French ------------------------------------------

        private const string Warning = "Disconnect the appliance from the mains supply before removing the cover.";
        private const string WarningFr = "Débranchez l'appareil du secteur avant de retirer le capot.";

        private static readonly string[] Manual =
        {
            "Chapter 2: Cleaning", Warning, "Wipe the housing with a damp cloth.",
            "Chapter 5: Replacing the lamp", Warning, "Unscrew the lamp anticlockwise.",
            "Chapter 7: Replacing the filter",
            "Warning: disconnect the appliance from the mains supply before removing the cover or the filter.",
        };

        public static void Manual_AWarningRepeatedAcrossChapters_IsFound_WholeOrInsideALongerSentence()
        {
            var found = RestatedPassages.Find(Manual);
            Assert.Equal(3, found.Pairs.Count, "three segments carry the warning: three pairs");
            Assert.True(found.Pairs.All(p => p.PassageWords == 11), "over the warning's 11 words");
        }

        public static void Manual_OneChapterWordedDifferently_IsReportedAgainstTheOthers()
        {
            var targets = Targets(
                "Chapitre 2 : Nettoyage", WarningFr, "Essuyez le boîtier avec un chiffon humide.",
                "Chapitre 5 : Remplacement de la lampe", WarningFr, "Dévissez la lampe dans le sens antihoraire.",
                "Chapitre 7 : Remplacement du filtre",
                "Attention : coupez l'alimentation électrique de l'appareil avant d'enlever le couvercle ou le filtre.");

            var report = RestatedPassages.Report(Manual, targets);
            Assert.Equal(2, report.Different.Count, "chapter 7 against chapter 2 and against chapter 5; 2 and 5 agree");
            Assert.True(report.Different.All(f => f.LabelB == "segment 8"), "both name the odd one out");

            targets[7].Text = "Attention : débranchez l'appareil du secteur avant de retirer le capot ou le filtre.";
            Assert.True(RestatedPassages.Report(Manual, targets).Summary() == null, "worded the same in all three: nothing to say");
        }

        public static void Manual_AWarningInMoreSegmentsThanTheCap_IsLeftAlone_AKnownLimit()
        {
            // The same warning inside ten different sentences. Past
            // MaxSegmentsPerPhrase it counts as boilerplate: no pairs, so a
            // different wording in one of them is NOT reported. (Ten identical
            // segments are consistent repetitions' business, not this check's.)
            var manual = Enumerable.Range(1, 10)
                .Select(n => "Step " + n + " of chapter " + (n + 20) + ": disconnect the appliance from the mains supply before removing the cover.")
                .ToList();
            Assert.Equal(0, RestatedPassages.Find(manual).Pairs.Count, "ten segments is over the cap of " + RestatedPassages.MaxSegmentsPerPhrase);

            Assert.Equal(15, RestatedPassages.Find(manual.Take(6).ToList()).Pairs.Count, "six is not: every pair of the six");
        }

        // ---- software strings, English into Spanish ----------------------------------

        public static void SoftwareStrings_ShortOnes_PairNothing_HoweverAlike()
        {
            // 400 strings of two to six words that share most of their words.
            // Under eight words in a row there is no pair: no noise, and no help
            // either - identical strings are consistent repetitions' business.
            var strings = new List<string>();
            foreach (var noun in new[] { "file", "folder", "project", "template", "report", "user", "group", "filter", "layout", "profile" })
                foreach (var verb in new[] { "Open", "Save", "Delete", "Rename", "Copy", "Move", "Export", "Import", "Print", "Share" })
                {
                    strings.Add(verb + " " + noun);
                    strings.Add(verb + " the selected " + noun);
                    strings.Add("Could not " + verb.ToLowerInvariant() + " the " + noun);
                    strings.Add("Do you want to " + verb.ToLowerInvariant() + " this " + noun + "?");
                }

            var clock = Stopwatch.StartNew();
            var found = RestatedPassages.Find(strings);
            clock.Stop();
            Assert.Equal(400, strings.Count, "400 strings");
            Assert.Equal(0, found.Pairs.Count, "no pair among short strings");
            Assert.True(clock.ElapsedMilliseconds < 1000, "and no time spent: " + clock.ElapsedMilliseconds + " ms");

            var targets = strings.Select((s, i) => new RestatedTarget { Label = "segment " + (i + 1), Text = "[" + s + "]", HasNote = false }).ToList();
            Assert.True(RestatedPassages.Report(strings, targets).Summary() == null, "so no lines");
        }

        private static readonly string[] Messages =
        {
            "The file could not be saved because the disk is full or write-protected.",
            "Settings",
            "The project could not be saved because the disk is full or write-protected.",
        };

        public static void SoftwareStrings_TwoMessagesThatDifferInOneWord_AreNearTwins()
        {
            var found = RestatedPassages.Find(Messages);
            Assert.Equal(1, found.Pairs.Count, "the two messages");
            Assert.True(found.Pairs[0].NearTwin, "the same sentence but for one word");
            Assert.Equal(1, found.Pairs[0].TwinDifferences, "they differ in one place");
        }

        public static void SoftwareStrings_ADifferenceWhereTheSourcesDiffer_IsNotReported_WhereverTheOtherLanguagePutsIt()
        {
            // English has the changed word second; Spanish has it fifth.
            var targets = Targets(
                "No se pudo guardar el archivo porque el disco está lleno o protegido contra escritura.",
                "Configuración",
                "No se pudo guardar el proyecto porque el disco está lleno o protegido contra escritura.");
            Assert.True(RestatedPassages.Report(Messages, targets).Summary() == null, "archivo / proyecto is the sources' difference");
        }

        public static void SoftwareStrings_ASecondDifference_IsReported()
        {
            var targets = Targets(
                "No se pudo guardar el archivo porque el disco está lleno o protegido contra escritura.",
                "Configuración",
                "No se ha podido guardar el proyecto porque el disco está lleno o protegido contra escritura.");
            var report = RestatedPassages.Report(Messages, targets);
            Assert.Equal(1, report.Different.Count, "pudo / ha podido is not the sources' difference");
        }

        public static void ADocumentWithNoNotesAtAll_GetsNoNoteLines()
        {
            // The [[TC: ...]] marker is a convention of some prompts. Without it
            // near-twins are still compared, and nothing is said about notes.
            var targets = Targets(
                "No se pudo guardar el archivo porque el disco está lleno o protegido contra escritura.",
                "Configuración",
                "No se pudo guardar el proyecto porque el disco está lleno o protegido contra escritura.");
            var report = RestatedPassages.Report(Messages, targets);
            Assert.Equal(1, report.Compared, "compared");
            Assert.Equal(0, report.OneNote.Count, "no notes anywhere: no note lines");

            foreach (var t in targets) t.HasNote = null;   // a host that does not read notes at all
            Assert.Equal(0, RestatedPassages.Report(Messages, targets).OneNote.Count, "nor when the host cannot tell");
        }

        // ---- other languages ------------------------------------------------------------

        public static void Russian_Greek_AndTurkish_PairLikeAnyLanguageWithSpaces()
        {
            // Cyrillic, with the passage in capitals in the second segment.
            var russian = RestatedPassages.Find(new[]
            {
                "Согласно договору, поставщик обязан доставить товар на склад покупателя в течение тридцати дней.",
                "Введение",
                "Стороны подтверждают, что ПОСТАВЩИК ОБЯЗАН ДОСТАВИТЬ ТОВАР НА СКЛАД ПОКУПАТЕЛЯ В ТЕЧЕНИЕ тридцати дней после оплаты.",
            });
            Assert.Equal(1, russian.Pairs.Count, "Russian, whatever the case");
            Assert.Equal(11, russian.Pairs[0].PassageWords, "поставщик ... дней");

            var greek = RestatedPassages.Find(new[]
            {
                "Ο προμηθευτής οφείλει να παραδώσει τα εμπορεύματα στην αποθήκη του αγοραστή εντός τριάντα ημερών.",
                "Τα μέρη συμφωνούν ότι ο προμηθευτής οφείλει να παραδώσει τα εμπορεύματα στην αποθήκη του αγοραστή χωρίς καθυστέρηση.",
            });
            Assert.Equal(1, greek.Pairs.Count, "Greek");

            var turkish = RestatedPassages.Find(new[]
            {
                "Cihazın kapağını açmadan önce fişi prizden çekin ve cihazın tamamen soğumasını bekleyin.",
                "Dikkat: cihazın kapağını açmadan önce fişi prizden çekin ve cihazın tamamen soğumasını bekleyin, aksi halde yanabilirsiniz.",
            });
            Assert.Equal(1, turkish.Pairs.Count, "Turkish");
        }

        public static void French_AnApostropheStaysInsideItsWord()
        {
            var found = RestatedPassages.Find(new[]
            {
                "Débranchez l'appareil de l'alimentation avant d'ouvrir le capot de l'unité centrale.",
                "Il faut que vous débranchez l'appareil de l'alimentation avant d'ouvrir le capot de l'unité centrale chaque fois.",
            });
            Assert.Equal(1, found.Pairs.Count, "French");
            Assert.Equal(11, found.Pairs[0].PassageWords, "l'appareil, l'alimentation, d'ouvrir and l'unité are one word each");
        }

        public static void ChineseAndJapaneseSources_PairNothing_NotSupportedYet()
        {
            // Written without spaces, a whole clause is one "word": there is never
            // a run of eight. Nothing is found, and nothing is reported.
            const string passage = "供应商应在三十天内将货物交付至买方仓库并承担全部运输费用";
            var chinese = new[] { "根据本合同，" + passage + "。", "引言", "双方确认，" + passage + "，逾期应支付违约金。" };
            Assert.Equal(0, RestatedPassages.Find(chinese).Pairs.Count, "Chinese: no pairs");
            Assert.True(RestatedPassages.Report(chinese, Targets("a", "b", "c")).Summary() == null, "and no lines");

            var japanese = new[] { "カバーを取り外す前に、必ず電源プラグをコンセントから抜いてください。", "警告：カバーを取り外す前に、必ず電源プラグをコンセントから抜いてください。" };
            Assert.Equal(0, RestatedPassages.Find(japanese).Pairs.Count, "Japanese: no pairs");

            Assert.True(RestatedPassages.WrittenWithoutSpaces(chinese[0]), "Chinese is recognised");
            Assert.True(RestatedPassages.WrittenWithoutSpaces(japanese[0]), "Japanese is recognised");
            Assert.True(RestatedPassages.WrittenWithoutSpaces("ถอดปลั๊กออกจากเต้ารับก่อนถอดฝาครอบ"), "Thai is recognised");
            Assert.True(!RestatedPassages.WrittenWithoutSpaces("커버를 분리하기 전에 전원 플러그를 뽑으십시오."), "Korean is written with spaces");
            Assert.True(!RestatedPassages.WrittenWithoutSpaces("Supplied by 東芝 under this Agreement."), "a name quoted in another script does not count");
            Assert.True(!RestatedPassages.WrittenWithoutSpaces(null) && !RestatedPassages.WrittenWithoutSpaces("12 %"), "nothing to judge");
        }

        public static void ATranslationIntoChineseOrJapanese_IsSkipped_NeverCalledDifferent()
        {
            // The English sources pair. The Chinese translations cannot be compared
            // word by word, so the pair is skipped - even where they do differ.
            var sources = new[]
            {
                "The supplier shall deliver the goods to the buyer's warehouse within thirty days.",
                "The parties confirm that the supplier shall deliver the goods to the buyer's warehouse within thirty days of payment.",
            };
            Assert.Equal(1, RestatedPassages.Find(sources).Pairs.Count, "the English sources pair");

            var same = RestatedPassages.Report(sources, Targets("供应商应在三十天内将货物交付至买方仓库。", "双方确认，供应商应在付款后三十天内将货物交付至买方仓库。"));
            Assert.Equal(1, same.Skipped, "skipped");
            Assert.True(same.Summary() == null, "no lines");

            var different = RestatedPassages.Report(sources, Targets("供应商应在三十天内将货物交付至买方仓库。", "双方确认，卖方须于付款后三十日内把商品送到买方的库房。"));
            Assert.True(different.Summary() == null, "no lines for a difference either: not supported yet");
        }
        // ---- rules that came from the first real measurement (14 Nov 2026) ---------------
        // Invented text in the shape of what was found; the job's own text is nowhere.

        private static string ValueList(string opening, double from, int steps) =>
            opening + " " + string.Join(", preferably at least ",
                Enumerable.Range(0, steps).Select(i => (from + i * 0.1).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " mm")) + ".";

        public static void TwoDifferentListsOfValues_AreNotAPair_ThoughTheyShareLongRuns()
        {
            // "... at least 0.7 mm, preferably at least 0.8 mm, preferably at least
            // 0.9 mm ..." stands in both, forty words in a row. It is the formula
            // of a list, not a restated passage, and the two translations cannot be
            // lined up. Half the lines on the first real document were of this kind.
            var lists = new[]
            {
                ValueList("The wall of the housing has a thickness of at least", 0.5, 12),
                "The housing is made of steel.",
                ValueList("The lid supplied by Acme has a thickness of at least", 0.7, 14),
            };
            Assert.Equal(0, RestatedPassages.Find(lists).Pairs.Count, "two different lists of values");
        }

        public static void TheSameListOfValuesTwice_IsStillANearTwin_AndIsCompared()
        {
            var lists = new[]
            {
                ValueList("The wall of the housing has a thickness of at least", 0.5, 12),
                "The housing is made of steel.",
                ValueList("The wall of the cover has a thickness of at least", 0.5, 12),
            };
            var found = RestatedPassages.Find(lists);
            Assert.Equal(1, found.Pairs.Count, "the same list for another part");
            Assert.True(found.Pairs[0].NearTwin, "a near-twin");

            var german = ValueList("Die Wand des Gehäuses hat eine Dicke von mindestens", 0.5, 12).Replace("preferably at least", "vorzugsweise mindestens");
            var same = Targets(german, "Das Gehäuse besteht aus Stahl.", german.Replace("des Gehäuses", "des Deckels"));
            Assert.True(RestatedPassages.Report(lists, same).Summary() == null, "Gehäuses / Deckels is the sources' difference");

            var different = Targets(german, "Das Gehäuse besteht aus Stahl.",
                german.Replace("des Gehäuses", "des Deckels").Replace("hat eine Dicke von", "weist eine Stärke von"));
            Assert.Equal(1, RestatedPassages.Report(lists, different).Different.Count, "Dicke / Stärke is not");
        }

        public static void ARunOfNumbersAndAUnit_IsNotAPassage()
        {
            // Eight words in a row, one of them a word: "10 mm 12 mm 14 mm 16 mm".
            var found = RestatedPassages.Find(new[]
            {
                "The bracket is supplied in the sizes 10 mm 12 mm 14 mm 16 mm and can be ordered from Acme in steel or brass at any time.",
                "Please note that our price list no longer shows 10 mm 12 mm 14 mm 16 mm separately because all four cost the same.",
            });
            Assert.Equal(0, found.Pairs.Count, "numbers and a unit are not a restated passage");
        }

        private static readonly string[] Measuring =
        {
            "De hoogte wordt gemeten in een loodrechte richting op de grondplaat tussen het vrije uiteinde van de beugel en de klem.",
            "Het huis is van staal.",
            "Het is vereist dat de afstand volgens een loodrechte richting op de grondplaat tussen het vrije uiteinde van de beugel en de klem maandelijks wordt gecontroleerd.",
        };

        public static void TheSameWordsInAnotherOrderAtTheStartOfAPassage_IsADifference()
        {
            // "in a perpendicular direction [to the base plate ...]" against "in a
            // direction perpendicular [to the base plate ...]": the shared wording
            // starts after it, so nothing inside the passage is broken. A known
            // case on the first real document was missed this way.
            var reordered = Targets(
                "The height is measured in a perpendicular direction to the base plate between the free end of the bracket and the clamp.",
                "The housing is made of steel.",
                "It is required that the distance in a direction perpendicular to the base plate between the free end of the bracket and the clamp is checked monthly.");
            Assert.Equal(1, RestatedPassages.Report(Measuring, reordered).Different.Count, "perpendicular direction / direction perpendicular");

            var same = Targets(reordered[0].Text, reordered[1].Text,
                "It is required that the distance in a perpendicular direction to the base plate between the free end of the bracket and the clamp is checked monthly.");
            Assert.True(RestatedPassages.Report(Measuring, same).Summary() == null, "worded the same: nothing to say");
        }

        public static void DifferentWordsBeforeAPassage_AreNotADifference()
        {
            // What comes before the passage is another sentence in each segment,
            // and is translated as such.
            var targets = Targets(
                "The height is measured in a perpendicular direction to the base plate between the free end of the bracket and the clamp.",
                "The housing is made of steel.",
                "The distance along a perpendicular direction to the base plate between the free end of the bracket and the clamp must be checked monthly.");
            Assert.True(RestatedPassages.Report(Measuring, targets).Summary() == null, "nothing to say");
        }
    }
}
