using System;
using System.Collections.Generic;
using System.Text;

namespace Supervertaler.Core
{
    /// <summary>
    /// One source segment of the open document, with the number the CAT tool
    /// shows for it and, in a document made up of several files, the file it
    /// is in.
    ///
    /// <para>The prompts used to number the document 1..N straight through.
    /// Trados Studio numbers the segments of each file of a merged document
    /// from 1, so with a figures file of 7 segments ahead of a text file of
    /// 444, the text file's segment 32 went out as 39 - and every segment
    /// AutoPrompt cited sent the translator to the wrong row, by the size of
    /// the files before it. A segment now carries its own number, and the
    /// prompt cites what the translator sees.</para>
    /// </summary>
    public sealed class DocumentSegment
    {
        public DocumentSegment(string text, string number, string file = null)
        {
            Text = text ?? "";
            Number = number ?? "";
            File = string.IsNullOrEmpty(file) ? null : file;
        }

        /// <summary>The source text, exactly as the model receives it.</summary>
        public string Text { get; }

        /// <summary>The number the CAT tool shows for the segment: "32", or
        /// "331 a" for a segment split in Trados Studio.</summary>
        public string Number { get; }

        /// <summary>The name of the file the segment is in, or null in a
        /// one-file document, or where the host could not tell.</summary>
        public string File { get; }

        /// <summary>Texts numbered 1..N, one file: all a caller that knows
        /// nothing more can say, and what the document block always assumed.</summary>
        public static List<DocumentSegment> Sequential(IList<string> texts)
        {
            var list = new List<DocumentSegment>(texts?.Count ?? 0);
            if (texts == null) return list;
            for (int i = 0; i < texts.Count; i++)
                list.Add(new DocumentSegment(texts[i], (i + 1).ToString()));
            return list;
        }
    }

    /// <summary>
    /// Writes a document's segments into a prompt, numbered as the CAT tool
    /// numbers them. One-file documents come out exactly as they always have;
    /// a document of several files gets a heading where each file starts.
    /// </summary>
    public static class DocumentContent
    {
        /// <summary>The files the segments name, each once, in document order.
        /// Fewer than two means a one-file document as far as numbering goes.</summary>
        public static List<string> Files(IList<DocumentSegment> segments)
        {
            var files = new List<string>();
            if (segments == null) return files;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in segments)
                if (s?.File != null && seen.Add(s.File)) files.Add(s.File);
            return files;
        }

        /// <summary>
        /// Appends the segments as "number. text" lines. With
        /// <paramref name="maxSegments"/> above zero and more segments than that,
        /// only the first 80% and the last 20% of it are written, with a line
        /// saying how many were left out. <paramref name="skipBlank"/> leaves out
        /// segments with no text, which costs nothing when every line carries
        /// its number.
        ///
        /// <para>In a document of several files the list opens with a sentence
        /// saying how it is numbered, and each file starts with a heading
        /// naming it - written again, marked "continued", where the list
        /// resumes in mid-file after the omitted segments.</para>
        /// </summary>
        /// <param name="documentFiles">The whole document's files, from
        /// <see cref="Files"/>, when <paramref name="segments"/> are only the first
        /// part of it: that part may lie in one file, and it is still one file of
        /// several. By default, the files the segments name.</param>
        public static void AppendNumbered(StringBuilder sb, IList<DocumentSegment> segments,
            int maxSegments = 0, bool skipBlank = false, IList<string> documentFiles = null)
        {
            if (sb == null || segments == null || segments.Count == 0) return;

            var files = documentFiles != null ? new List<string>(documentFiles) : Files(segments);
            if (files.Count > 1)
            {
                sb.Append("This document is made up of ").Append(files.Count)
                  .AppendLine(" files. The CAT tool numbers the segments of each file separately,");
                sb.AppendLine("starting again at 1, and so does this list: a segment is identified by its file and its number.");
                sb.AppendLine();
            }

            // The file whose heading was written last. Null before the first.
            string headed = null;

            if (maxSegments <= 0 || segments.Count <= maxSegments)
            {
                for (int i = 0; i < segments.Count; i++)
                    AppendLine(sb, segments[i], files, ref headed, skipBlank, null);
                return;
            }

            int firstCount = (int)(maxSegments * 0.8);
            int lastCount = maxSegments - firstCount;
            int omitted = segments.Count - maxSegments;

            for (int i = 0; i < firstCount; i++)
                AppendLine(sb, segments[i], files, ref headed, skipBlank, null);

            sb.AppendLine();
            sb.Append("[... ").Append(omitted).AppendLine(" segments omitted ...]");
            sb.AppendLine();

            // Files run one after another, so the only file the tail can resume
            // in is the one the head ended in.
            var resumedIn = headed;
            headed = null;
            for (int i = segments.Count - lastCount; i < segments.Count; i++)
                AppendLine(sb, segments[i], files, ref headed, skipBlank, resumedIn);
        }

        private static void AppendLine(StringBuilder sb, DocumentSegment s, List<string> files,
            ref string headed, bool skipBlank, string resumedIn)
        {
            if (s == null || (skipBlank && string.IsNullOrWhiteSpace(s.Text))) return;

            // A segment without a file stays under the heading before it.
            if (files.Count > 1 && s.File != null && s.File != headed)
            {
                sb.Append("## File ").Append(files.IndexOf(s.File) + 1).Append(" of ").Append(files.Count)
                  .Append(": ").Append(s.File);
                if (s.File == resumedIn) sb.Append(" (continued)");
                sb.AppendLine();
                headed = s.File;
            }

            sb.Append(s.Number).Append(". ").AppendLine(s.Text);
        }
    }
}
