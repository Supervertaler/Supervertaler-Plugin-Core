using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// The DOCUMENT CONTENT block and AutoPrompt's excerpt number segments as the
    /// CAT tool does. Trados Studio numbers each file of a merged document from 1;
    /// the block numbered 1..N straight through, so in a 7-segment figures file
    /// followed by a 444-segment text file, the text file's segment 32 went out as
    /// 39, and every segment AutoPrompt cited was 7 too high. A one-file document
    /// must come out exactly as it always did.
    /// </summary>
    [Tests]
    internal static class DocumentNumberingTests
    {
        private const string Figures = "Acme - Figures only.docx.sdlxliff";
        private const string Text = "Acme - Text only.docx.sdlxliff";

        /// <summary>The block as it was built before segments carried numbers -
        /// the loop copied from TranslationPrompt as it stood at core bce25bf.</summary>
        private static string LegacyBlock(List<string> documentSegments, int maxDocumentSegments)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("# DOCUMENT CONTENT");
            sb.AppendLine("The following is the source document content. Analyze it to determine the document type");
            sb.AppendLine("(legal, medical, technical, marketing, financial, patent, scientific, etc.) and use that");
            sb.AppendLine("assessment to inform your translation style, terminology choices, and register.");
            sb.AppendLine();
            var max = maxDocumentSegments > 0 ? maxDocumentSegments : 500;
            if (documentSegments.Count <= max)
            {
                for (int i = 0; i < documentSegments.Count; i++)
                    sb.Append(i + 1).Append(". ").AppendLine(documentSegments[i]);
            }
            else
            {
                int firstCount = (int)(max * 0.8);
                int lastCount = max - firstCount;
                int omitted = documentSegments.Count - max;
                for (int i = 0; i < firstCount; i++)
                    sb.Append(i + 1).Append(". ").AppendLine(documentSegments[i]);
                sb.AppendLine();
                sb.Append("[... ").Append(omitted).AppendLine(" segments omitted ...]");
                sb.AppendLine();
                int startLast = documentSegments.Count - lastCount;
                for (int i = startLast; i < documentSegments.Count; i++)
                    sb.Append(i + 1).Append(". ").AppendLine(documentSegments[i]);
            }
            return sb.ToString();
        }

        private static string Legacy(List<string> texts, int max) =>
            (TranslationPrompt.BuildSystemPrompt("Dutch", "English") + LegacyBlock(texts, max)).TrimEnd();

        private static List<string> Texts(string prefix, int n) =>
            Enumerable.Range(1, n).Select(i => prefix + " sentence " + i + ".").ToList();

        /// <summary>A file's segments, numbered from 1 as Studio numbers them.</summary>
        private static IEnumerable<DocumentSegment> File(string name, string prefix, int n) =>
            Enumerable.Range(1, n).Select(i => new DocumentSegment(prefix + " sentence " + i + ".", i.ToString(), name));

        private static List<DocumentSegment> TwoFiles() =>
            File(Figures, "Figure", 7).Concat(File(Text, "Text", 444)).ToList();

        // ─── One file: exactly as before ─────────────────────────────

        public static void OneFile_ListOfStrings_IsByteIdenticalToBefore()
        {
            foreach (var (n, max) in new[] { (1, 500), (3, 500), (500, 500), (501, 500), (2000, 500), (10, 5), (7, 0), (451, 50) })
            {
                var texts = Texts("Zin", n);
                texts[0] = null;   // a segment the host had no text for
                var now = TranslationPrompt.BuildSystemPrompt("Dutch", "English",
                    documentSegments: texts, maxDocumentSegments: max);
                Assert.Equal(Legacy(texts, max), now, $"{n} segments, cap {max}");
            }
        }

        public static void OneFile_WithStudioNumbers_IsByteIdenticalToBefore()
        {
            // Studio's own numbers, 1..N, with the file named or not: a one-file
            // document is not headed, so nothing changes.
            foreach (var (n, max) in new[] { (12, 500), (600, 500) })
            {
                var texts = Texts("Zin", n);
                foreach (var file in new[] { null, Text })
                {
                    var numbered = texts.Select((t, i) => new DocumentSegment(t, (i + 1).ToString(), file)).ToList();
                    var now = TranslationPrompt.BuildSystemPrompt("Dutch", "English",
                        maxDocumentSegments: max, numberedSegments: numbered);
                    Assert.Equal(Legacy(texts, max), now, $"{n} segments, file {file ?? "unnamed"}");
                }
            }
        }

        public static void OneFile_ASplitSegment_KeepsStudiosNumber()
        {
            var numbered = new List<DocumentSegment>
            {
                new DocumentSegment("Eerste.", "1"),
                new DocumentSegment("Tweede, deel a.", "2 a"),
                new DocumentSegment("Tweede, deel b.", "2 b"),
                new DocumentSegment("Derde.", "3"),
            };
            var now = TranslationPrompt.BuildSystemPrompt("Dutch", "English", numberedSegments: numbered);
            Assert.True(now.EndsWith("\r\n1. Eerste.\r\n2 a. Tweede, deel a.\r\n2 b. Tweede, deel b.\r\n3. Derde."),
                "Studio's numbers, split segment included: " + Tail(now));
        }

        // ─── Several files: numbered per file, headed ────────────────

        public static void TwoFiles_EachSegmentKeepsItsOwnFilesNumber()
        {
            var block = TranslationPrompt.BuildSystemPrompt("Dutch", "English",
                maxDocumentSegments: 0, numberedSegments: TwoFiles());

            Assert.True(block.Contains("This document is made up of 2 files."), "says how it is numbered");
            Assert.True(block.Contains("\r\n## File 1 of 2: " + Figures + "\r\n1. Figure sentence 1.\r\n"), "file 1 headed, from 1");
            Assert.True(block.Contains("\r\n7. Figure sentence 7.\r\n## File 2 of 2: " + Text + "\r\n1. Text sentence 1.\r\n"),
                "file 2 headed where it starts, and starts again at 1");
            Assert.True(block.Contains("\r\n32. Text sentence 32.\r\n"), "the text file's segment 32 is 32");
            Assert.True(!block.Contains("39. Text sentence 32."), "not 39, the old continuous number");
            Assert.True(block.EndsWith("\r\n444. Text sentence 444."), "ends on the last segment of the text file");
            Assert.Equal(2, Count(block, "\r\n## File "), "one heading per file");
        }

        public static void ThreeFiles_TruncatedInMidFile_ResumesUnderAContinuedHeading()
        {
            var segs = File("A.docx.sdlxliff", "A", 5)
                .Concat(File("B.docx.sdlxliff", "B", 300))
                .Concat(File("C.docx.sdlxliff", "C", 20)).ToList();

            // Cap 100: the first 80 (A 1-5, B 1-75) and the last 20 (C 1-20).
            var block = TranslationPrompt.BuildSystemPrompt("Dutch", "English",
                maxDocumentSegments: 100, numberedSegments: segs);
            Assert.True(block.Contains("made up of 3 files"), "three files");
            Assert.True(block.Contains("\r\n75. B sentence 75.\r\n\r\n[... 225 segments omitted ...]\r\n\r\n## File 3 of 3: C.docx.sdlxliff\r\n1. C sentence 1.\r\n"),
                "omission, then file C headed afresh: " + Around(block, "omitted"));
            Assert.True(!block.Contains("(continued)"), "C was never headed before, so it is not 'continued'");

            // Cap 50: the first 40 (A 1-5, B 1-35) and the last 10 (C 11-20).
            block = TranslationPrompt.BuildSystemPrompt("Dutch", "English",
                maxDocumentSegments: 50, numberedSegments: segs);
            Assert.True(block.Contains("\r\n## File 3 of 3: C.docx.sdlxliff\r\n11. C sentence 11.\r\n"), "C's own numbers after the gap");

            // Cap 50 with C small enough that the tail starts inside B.
            segs = File("A.docx.sdlxliff", "A", 5).Concat(File("B.docx.sdlxliff", "B", 300))
                .Concat(File("C.docx.sdlxliff", "C", 4)).ToList();
            block = TranslationPrompt.BuildSystemPrompt("Dutch", "English",
                maxDocumentSegments: 50, numberedSegments: segs);
            Assert.True(block.Contains("[... 259 segments omitted ...]\r\n\r\n## File 2 of 3: B.docx.sdlxliff (continued)\r\n295. B sentence 295.\r\n"),
                "resumes in B under a continued heading: " + Around(block, "omitted"));
            Assert.True(block.EndsWith("\r\n## File 3 of 3: C.docx.sdlxliff\r\n1. C sentence 1.\r\n2. C sentence 2.\r\n3. C sentence 3.\r\n4. C sentence 4."),
                "C headed and numbered from 1: " + Tail(block));
        }

        public static void ASegmentWithNoFile_StaysUnderTheHeadingBeforeIt()
        {
            var segs = new List<DocumentSegment>
            {
                new DocumentSegment("Een.", "1", "A"),
                new DocumentSegment("Twee.", "2", null),
                new DocumentSegment("Drie.", "1", "B"),
            };
            var sb = new StringBuilder();
            DocumentContent.AppendNumbered(sb, segs);
            var text = sb.ToString();
            Assert.True(text.Contains("## File 1 of 2: A\r\n1. Een.\r\n2. Twee.\r\n## File 2 of 2: B\r\n1. Drie.\r\n"), text);
        }

        public static void TheFirstPartOfADocument_InOneFile_IsStillHeadedAsOneFileOfSeveral()
        {
            // get_prompt_context capped at 5 segments of a 7 + 444 document: the part
            // shown lies in the figures file alone, and is still file 1 of 2.
            var all = TwoFiles();
            var sb = new StringBuilder();
            DocumentContent.AppendNumbered(sb, all.Take(5).ToList(), skipBlank: true,
                documentFiles: DocumentContent.Files(all));
            var text = sb.ToString();
            Assert.True(text.StartsWith("This document is made up of 2 files."), "says how it is numbered: " + text);
            Assert.True(text.Contains("\r\n## File 1 of 2: " + Figures + "\r\n1. Figure sentence 1.\r\n"), "headed as file 1 of 2: " + text);
            Assert.True(text.EndsWith("\r\n5. Figure sentence 5.\r\n"), "the five segments asked for: " + text);
        }

        public static void BothListsAtOnce_IsRefused()
        {
            try
            {
                TranslationPrompt.BuildSystemPrompt("Dutch", "English",
                    documentSegments: new List<string> { "x" },
                    numberedSegments: new List<DocumentSegment> { new DocumentSegment("x", "1") });
            }
            catch (ArgumentException) { return; }
            throw new AssertFailed("passing the document twice was accepted");
        }

        // ─── AutoPrompt's excerpt ─────────────────────────────────────

        private static PromptGenerationContext Ctx() => new PromptGenerationContext
        {
            SourceLang = "Dutch",
            TargetLang = "English",
            DetectedDomain = "patent",
            SegmentCount = 451,
        };

        public static void MetaPrompt_OneFile_IsByteIdenticalToBefore()
        {
            var texts = Texts("Zin", 40);
            texts[3] = "";   // a blank segment, which the excerpt leaves out
            var before = Ctx(); before.SourceSegments = texts;
            var now = Ctx();
            now.NumberedSegments = texts.Select((t, i) => new DocumentSegment(t, (i + 1).ToString(), Text)).ToList();

            Assert.Equal(PromptGenerator.BuildMetaPrompt(before), PromptGenerator.BuildMetaPrompt(now), "one file, numbered or not");
            Assert.True(!PromptGenerator.BuildMetaPrompt(now).Contains("SEGMENT NUMBERS"), "no citation rule for one file");
        }

        public static void MetaPrompt_WithoutNumbers_IsAsBefore()
        {
            // memoQ sets SourceSegments only: the excerpt is the plain lines, blanks left out.
            var ctx = Ctx();
            ctx.SourceSegments = new List<string> { "Een.", "  ", "Twee." };
            var meta = PromptGenerator.BuildMetaPrompt(ctx);
            Assert.True(meta.Contains("=== PROJECT CONTEXT (document content) ===\r\nEen.\r\nTwee.\r\n\r\n=== PROMPT GENERATION INSTRUCTIONS"),
                "plain excerpt");
            Assert.True(!meta.Contains("SEGMENT NUMBERS"), "no citation rule");
        }

        public static void MetaPrompt_TwoFiles_NumbersPerFile_AndAsksForFileAndQuote()
        {
            var ctx = Ctx();
            var segs = TwoFiles();
            segs[7 + 9] = new DocumentSegment("   ", "10", Text);   // blank: left out, numbers unchanged
            ctx.NumberedSegments = segs;
            var meta = PromptGenerator.BuildMetaPrompt(ctx);

            Assert.True(meta.Contains("SEGMENT NUMBERS: this document is made up of 2 files"), "the citation rule");
            Assert.True(meta.Contains("identify it by its file name and its number"), "file and number");
            Assert.True(meta.Contains("quote a few words of its source text beside"), "and a quote");
            Assert.True(meta.Contains("=== PROJECT CONTEXT (document content) ===\r\nThis document is made up of 2 files."), "excerpt opens with how it is numbered");
            Assert.True(meta.Contains("\r\n## File 2 of 2: " + Text + "\r\n1. Text sentence 1.\r\n"), "file 2 headed, from 1");
            Assert.True(meta.Contains("\r\n9. Text sentence 9.\r\n11. Text sentence 11.\r\n"), "blank segment 10 left out, 11 still 11");
            Assert.True(meta.Contains("\r\n32. Text sentence 32.\r\n"), "segment 32 is 32");
            Assert.True(!meta.Contains("39. Text sentence 32."), "not 39");
            Assert.True(meta.Contains("\r\n444. Text sentence 444.\r\n\r\n=== PROMPT GENERATION INSTRUCTIONS"), "whole document, untruncated");
        }

        // ─── helpers ─────────────────────────────────────────────────

        private static int Count(string s, string what)
        {
            int n = 0;
            for (int i = s.IndexOf(what, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + 1, StringComparison.Ordinal)) n++;
            return n;
        }

        private static string Tail(string s) => s.Length <= 300 ? s : s.Substring(s.Length - 300);

        private static string Around(string s, string what)
        {
            var i = s.IndexOf(what, StringComparison.Ordinal);
            if (i < 0) return "(" + what + " not found)";
            var from = Math.Max(0, i - 150);
            return s.Substring(from, Math.Min(s.Length - from, 300));
        }
    }
}
