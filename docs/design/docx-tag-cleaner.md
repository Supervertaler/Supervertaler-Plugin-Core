# DOCX tag cleaner (after TransTools' Tag Cleaner)

Behaviour spec for a pre-import Word cleaner in **Supervertaler for Trados** and
**Supervertaler for memoQ**, built once in Plugin Core. Workbench may get a port
later (see the end).

Written 2026-10-27 from the TransTools for Word 2.22 source.

## Source and licence

- The Document Cleaner lives in the VBA project of
  `C:\Users\mbeijer\AppData\Roaming\Microsoft\Word\STARTUP\TransTools.dotm`
  (modules `frmDocCleaner`, `modDocCleaner`, `modNormalizeFont`, `modColors`).
  It is **not** in the .NET `TransToolsPlus.dll` (TransTools+ 2.4), which has
  none of the cleaner's strings.
- TransTools for Word is **GPL v3 or later** (header in every module). Plugin
  Core and both plugins use the Source Available Licence, so **do not copy
  code**: implement from this description. Everything below describes behaviour
  in my own words.
- The source was read with `oletools` (`olevba`), not decompiled.

## Where it goes

- **Core:** a UI-free `DocxTagCleaner` in `core/src`, alongside `DocxStructure`
  and `DocxImages`. It takes a .docx path and the chosen options, writes a
  cleaned copy and returns counts. Nothing in it references `Sdl.*`, `MemoQ.*` or
  WinForms (Core's rule).
- **memoQ:** run it from the standalone Supervertaler for memoQ editor, which
  doesn't need memoQ open. It writes `<name> (cleaned).docx` next to the
  original, and the translator imports that file into memoQ.
- **Trados:** hook into Studio's own file conversion, so it runs as the file is
  added to a project, with no Supervertaler window and no open project needed.
  See "Trados: cleaning during file conversion" below.
- **No new DLL.** Read and write the package with `System.IO.Compression` and
  `System.Xml`, as `DocxStructure` does. Don't use `DocumentFormat.OpenXml`:
  Trados references it, but memoQ doesn't, and Core can't depend on it.
- **Write through `AtomicFile.Write(..., replaceExisting: false)`.** Never
  overwrite the original, or an earlier cleaned copy, without asking.
- **Clean before import, never afterwards.** Trados maps paragraph units to
  `DocxStructure` paragraphs by the character offset of each `<w:p` in
  `document.xml`, and cleaning changes those offsets. That works if the cleaned
  file is the one Trados imports, because Studio and `DocxStructure` then read
  the same file. It breaks if `DocxStructure` reads the original while Studio
  holds the cleaned copy, or the other way round. Check which file the memoQ
  structure reader (#7) opens.

## Trados: cleaning during file conversion

Found with REA on 2026-10-27, by static inspection of Studio 2024 (Studio18).
Studio 2022 (Studio17) and 2026 (Studio19) have the same types and the same
`PreTweak.Docx` key. A prototype then confirmed it in all three versions, outside
the Studio UI: see "Prototype results" below.

**The mechanism (observed in the DLLs).** Studio's file-type framework lets
code rewrite a native file before Studio parses it:

- `Sdl.FileTypeSupport.Framework.NativeApi.IFilePreTweaker` has one method,
  `TweakFilePreParsing(...)`, and an `Enabled` switch from `IFileTweaker`.
- Any file extractor accepts extra tweakers through
  `IFileExtractor.AddFileTweaker(...)`.
- Studio's base class `AbstractFilePreTweaker`
  (`Sdl.FileTypeSupport.Framework.Core.Utilities.NativeApi`) does the
  plumbing. It leaves `OriginalFilePath` alone, generates a temp path, sets it
  as `InputFilePath`, and registers it as a dependency file with a
  `FileJanitor` that deletes it afterwards. Then it calls `Tweak(...)`, which a
  subclass overrides to write the new file.

**Studio already does this for Word.** Studio's own .doc filter is built
exactly this way:

- `DocFilterComponentBuilder` wraps Studio's `DocxComponentBuilder`.
- Its `BuildFileExtractor` takes the Word extractor and adds a `DocPreTweaker`.
- `DocPreTweaker.Tweak` converts the .doc (with Aspose) into a .docx at
  `InputFilePath`. It then tags that dependency file
  `PreTweak.Docx.DependencyFileId`.
- Studio's Word filter (`Sdl.FileTypeSupport.Filters.MicrosoftOffice.Word_2`)
  contains that same key.

So the Word filter is already built to parse a .docx that a pre-tweaker has
substituted.

**Attaching to the existing Word file type (observed).** A plugin can extend a
file type rather than replace it:

- Mark a class `[FileTypeComponentBuilderExtension(...)]` with
  `OriginalFileType = "WordprocessingML v. 2"` (the stock Word 2007+ file type)
  and implement `IFileTypeComponentBuilderAdapter`.
- When Studio builds its file-type list (`PocoFilterManager.LoadFileTypeCache`),
  it finds such extensions. `AddPSExtensionToCache` then hands the adapter the
  original builder through `Original`, and stores it in the file-type
  dictionary by its file-type ID.

**The shape of it (design):**

- `SupervertalerWordAdapter` delegates every `Build*` call to `Original`, and
  also the extra interfaces Studio's Word builder has:
  `IDefaultFileTypeSettingsProvider`, `IFileTypeSettingsConverterComponentBuilder`,
  `IQuickInsertsBuilder` and `IFileTypeDefinitionAware`.
- In `BuildFileExtractor` it also adds a `TagCleanerPreTweaker`, a subclass of
  `AbstractFilePreTweaker` whose `Tweak` runs Core's `DocxTagCleaner` from
  `OriginalFilePath` to `InputFilePath`. It then names that dependency file
  `PreTweak.Docx.DependencyFileId` (see the prototype results).
- `Enabled` follows a Supervertaler setting, **off by default**. Once installed,
  this runs on every .docx added to every project.
- Options come from Supervertaler's own settings file. The tweaker runs on
  Studio's conversion thread, so it must not show any UI.
- **If the cleaner fails, copy the original to `InputFilePath` unchanged** and
  report a warning through `MessageReporter`. The base class turns an exception
  into a failed conversion, and a cleaner bug must never stop a file from
  being added.

### Prototype results (2026-10-27)

A throwaway spike plugin and a headless harness live in the Trados repo's
gitignored `.dev\pretweak-spike\` (see its `run.sh`). The tweak was trivial:
remove every `w:proofErr` (which moves later paragraph offsets) and add a
`SvPretweaked` document variable as a marker.

The harness loads each Studio's own file-type manager outside Studio. It puts the
spike's manifest beside Studio's in a shadow `PlugIns` folder, then converts a
test .docx to sdlxliff and generates the target back. Same registration code as
Studio (`LoadFileTypeCache` → `AddPSExtensionToCache`, seen in the stack traces).
Studio 2022, 2024 and 2026 gave identical results:

| Question | Result |
|---|---|
| 1. Replace or add? | **Replaces.** Still one `WordprocessingML v. 2` definition (51/61 file types, as without the spike); its builder is the adapter, wrapping `DocxComponentBuilder`. |
| 2. Which copy is used? | **The cleaned one**, for both the embedded original and the generated target. |
| 3. Offsets for `StructureContextResolver`? | **Match.** Studio's `StartsAt` values are the cleaned file's offsets, and the first `<internal-file>` is the cleaned file. No change needed in the plugin. |
| 4. Settings, verifiers, preview? | Settings pages (all five), verifiers, preview sets and quick tags are identical. **Preview components are not forwarded**; see below. |
| 5. Studio 2022, 2024, 2026? | **All three.** One source; built against each Studio's own DLLs (x86 for 2022/2024, x64 for 2026). |
| Failure path | A tweak that throws half-way still converts, from the **unchanged original**. Temp copies are deleted on both paths. |

Two findings change the design:

- **Name the temp copy `PreTweak.Docx.DependencyFileId`**, as Studio's
  `DocPreTweaker` does, after writing it. On the failure path too. Without
  that, the Word filter adds its own `Docx.DependencyFileId` and the sdlxliff
  embeds the document **twice** (10 KB instead of 7 KB on the test file; for a
  30 MB manual, an extra 40 MB of base64 in every sdlxliff). With it, there's one
  copy, and no dangling temp path in `sdl:ref-files`.
- **Preview components can't be forwarded.** Studio's Word builder also
  implements `IFileTypePreviewComponentsAware`. That interface lives in
  `Sdl.FileTypeSupport.Filters.PreviewCommon`, which isn't on Studio's public
  list (`pluginconfig.xml`), so a plugin can't implement it. In Studio 2024 the
  editor's preview still works without it (checked below). In Studio 2026 the
  old preview hooks are marked obsolete ("preview UI was moved inside Studio").

Also set `RequireValidEncoding = false` on the tweaker: a .docx is binary.

**Checked in the real Studio 2024 and Studio 2026 (2026-10-27)**, with the spike
installed as an ordinary third-party `.sdlplugin`. Both gave the same results:

- **Studio accepts the package** with no error. Its log shows Studio itself
  constructing the adapter around `DocxComponentBuilder` (`SDLTradosStudio.exe`,
  32-bit in 2024 and 64-bit in 2026).
- **One "Microsoft Word 2007-2019"** in File → Options → File Types, identifier
  `WordprocessingML v. 2`. All its settings pages open as usual.
- **Open Document** ran the tweak inside Studio's own project conversion. The
  project's sdlxliff embeds one copy, the cleaned one. `StartsAt` matches its
  offsets, so Supervertaler's list markers line up. The project's source copy is
  the untouched original, and the temp copy was deleted.
- **Preview works.** The HTML preview rendered the document, list numbering
  included, even though the adapter doesn't forward `IFileTypePreviewComponentsAware`.
- **Save Target As** produced a .docx built from the cleaned copy, which opens
  cleanly in Word.

Preview works in 2026 too, although Studio 2026 rebuilt the preview inside
Studio. Not checked in the Studio 2022 UI; there, the headless run covers it.

`bash install.sh <17|18|19>` and `bash install.sh <ver> --remove` in the spike
folder install and remove it, with Studio closed.

## Why Workbench's Supercleaner failed

Workbench had a TransTools-inspired cleaner, `modules/supercleaner.py`, removed
in v1.9.386 (2026-04-24) as unsafe. Every mistake below is easy to repeat in C#:

| Option | Supercleaner did | TransTools does |
|---|---|---|
| Set default text spacing | Set paragraph **line spacing** to 1.0 | Resets **character** spacing, scale, position, kerning |
| Black → Automatic | Removed **every** explicit colour (red text lost) | Changes only black (and theme black) to automatic |
| Remove manual hyphens | Also deleted real hyphens before whitespace ("pre- and post-war" → "preand post-war") | Removes only optional hyphens |
| Replace symbols | Changed NBSP to space and … to ... (content change) | Converts Symbol/Wingdings private-use characters to real Unicode |
| Character styles | Dropped the style, so its formatting was lost (bold from *Strong* vanished) | Converts the style's effect to direct formatting, then drops the style |
| Normalise size/colour/font | Forced the **most common** value on every run, no tolerance | Uses the paragraph's **first** run, with tolerances, and skips symbol fonts |
| Merge runs | Never | This is the core step. It is what actually removes tags |
| Scope | Body paragraphs only | Every story: body, tables, headers, footers, footnotes, endnotes, text boxes |
| Errors | `except Exception: pass` everywhere | Per-run, but one bad run doesn't stop the paragraph |

Lesson: the per-option property strips are the easy part. The value is in the
run-merging step, and in getting each option's exact scope right.

## The options (Tag Cleaner tab)

DOCX element names are WordprocessingML (`w:` namespace).

### 1. Set default text spacing
For every run, set character spacing to 0, scale to 100%, position to 0 and
kerning to 0. DOCX: remove `w:spacing`, `w:w`, `w:position`, `w:kern` from
`w:rPr`. Not paragraph spacing.

### 2. Remove text shading
Clear shading on characters **and** on paragraphs: texture none, foreground
and background automatic. DOCX: remove `w:shd` from `w:rPr` and from `w:pPr`.
Highlighting is a separate option.

### 3. Remove manual hyphens
Delete optional hyphens only (Word's `^-`). DOCX: remove `w:softHyphen`
elements, and U+00AD in `w:t` text. Never touch U+002D or non-breaking hyphens
(`w:noBreakHyphen`).

### 4. Change font colour from Black to Automatic
- Only runs whose colour is pure black become automatic. Also theme "Text 1"
  when the document theme resolves it to 000000, and theme "Text 1, lighter 5%"
  when it resolves to 0D0D0D.
- TransTools runs this only when the Windows window-text colour is black.
  Supervertaler can skip that check.
- DOCX: `w:color w:val="000000"` (or the theme cases above) → `w:val="auto"`
  with theme attributes removed. Every other colour is left alone.

### 5. Remove excessive bookmarks
Three things are deleted, never the cleaner's own `_TT_*` bookmarks:
- **Always:** `OLE_LINK*` bookmarks (clipboard artefacts).
- **TOC bookmarks sub-option:** all `_Toc*` bookmarks, plus any bookmark that a
  HYPERLINK or PAGEREF field inside a table of contents points to.
- **Unreferenced sub-option:** every bookmark, hidden ones included, that no
  field references. TransTools counts HYPERLINK, PAGEREF, REF, NOTEREF,
  GOTOBUTTON, ASK, SET and BARCODE fields.
- DOCX needs more than TransTools does: references come from complex-field
  `w:instrText` (which can be split across runs), `w:fldSimple/@w:instr`, **and**
  `w:hyperlink/@w:anchor`. Delete the matching `w:bookmarkStart` and
  `w:bookmarkEnd` by `w:id`, across all parts.

### 6. Remove text highlighting
Remove `w:highlight` from every run. (Off by default in TransTools.)

### 7. Remove character styles, leave direct formatting only
For each run with a character style (`w:rStyle`), work out what the style
contributes (follow its `basedOn` chain) and write those properties as direct
formatting, then remove `w:rStyle`. Runs inside hyperlinks are skipped, so the
*Hyperlink* style stays.

### 8. Normalise font face / size / colour in each paragraph (three options)
These apply during run merging (below). The **base run** is the first run in
the paragraph that is not in a hyperlink, not a symbol font and, unless option 7
is on, has no character style.
- **Font face:** other runs take the base font, except symbol fonts.
- **Font size:** a dropdown sets the rule. Either always use the base size, or
  use it only if the run's size differs by at most 1, 2 or 3 pt (bigger
  differences are kept).
- **Font colour:** a run takes the base colour only if the two are similar,
  using the "redmean" RGB distance (threshold 200). Two different standard
  colours (black, the theme palette) never count as similar.
- With an option off, the difference is kept. TransTools then reports how many
  more tags turning it on would remove.

### 9. Accept tracked changes
Accept all revisions and turn tracking off. In DOCX this is real work: remove
`w:del` and `w:moveFrom`, unwrap `w:ins` and `w:moveTo`, drop the `*PrChange`
elements, handle deleted paragraph marks, remove `w:trackRevisions`. Leave it
for a later version unless it's really needed: both CAT tools can already
accept changes on import.

### 10. Replace symbol-font characters with Unicode
Runs in Symbol, Wingdings, Webdings and the like hold private-use codes that
CAT tools show as tags. Each one is mapped to its real Unicode character (a
table in `modSymbolReplacement`), and the run takes the font of the nearest
non-symbol run. DOCX also stores these as `w:sym w:font=… w:char=…`.

### 11. Superscript digits → Unicode
Superscripted digits become ⁰ ¹ ² ³ ⁴ ⁵ ⁶ ⁷ ⁸ ⁹, and the superscript formatting is
removed. A sub-option limits this to 2 and 3 (for m², m³).

### Always on: run merging (the actual tag reduction)
For every paragraph in every story:
1. **If every run already has the same formatting** (and there's no mixed style
   or hyperlink), TransTools rewrites the paragraph's formatting as one uniform
   block. DOCX: merge all runs into one.
2. **Otherwise**, compare each run with the base run. A run made only of
   spaces, no-break spaces or line breaks (not tabs, which might be form-field
   separators) **takes the formatting of the run before it**, or the run after
   if it comes first. So a space between two bold words doesn't create its own
   tag. Then reset the paragraph to the base formatting and re-apply only the
   real differences.
3. Runs inside hyperlinks are never touched.

DOCX equivalent: within one parent (`w:p`, `w:hyperlink`, `w:sdtContent`, …),
merge neighbouring `w:r` whose `w:rPr` are identical after the options above
are applied. Compare the **whole** `w:rPr`, complex-script parts included
(`w:szCs`, `w:bCs`, `w:rFonts/@w:cs`, `@w:eastAsia`). Only merge runs whose
content is text-like (`w:t`, `w:tab`, `w:br`, `w:noBreakHyphen`). Never merge
across `w:fldChar`, `w:instrText`, bookmarks, comment ranges, `w:sym`, drawings
or footnote references.

Before this step, TransTools cuts and pastes the whole body over itself (a
trick taken from the OmegaT DOCX how-to). My guess is that this mainly strips
revision-session IDs. The DOCX equivalent would be removing `w:rsid*`
attributes, `w:proofErr` and `w:lastRenderedPageBreak`. Not verified.

### Check for unsupported formatting
If any text uses Word 2010 Text Effects (glow, reflection, 3-D, …), TransTools
skips the run-merging step, because Word's reset would corrupt them. At XML
level they're just `w14:*` children of `w:rPr` and survive merging as long as
they're compared and copied like any other property.

## Implementation notes (design choices, not TransTools behaviour)

- Parts: `document.xml`, every `header*.xml` and `footer*.xml`, `footnotes.xml`,
  `endnotes.xml`, `comments.xml`. Text boxes (`w:txbxContent`) are inside these.
  `mc:AlternateContent` often holds the same text box twice (Choice and
  Fallback). Clean both the same way.
- Copy every other package entry byte for byte. Rewrite only the parts that
  changed.
- Show the before/after run count, the only number the translator cares about.
- An error on one paragraph should leave that paragraph untouched and be
  reported, not swallowed (Supercleaner's silent `except: pass`).
- Bookmarks: build one set of referenced names by parsing the field
  instructions, then look names up in it. TransTools tests every field against
  every bookmark, which is O(fields × bookmarks): fine at dev size, slow on a
  long report with thousands of TOC entries.
- Run it off the UI thread in both plugins. A large document blocking the
  Trados or memoQ UI thread looks like a hang.
- Scale target: a 500-page OCR'd PDF conversion, around 200,000 runs and
  10,000 bookmarks, in seconds. Test with real ABBYY/Acrobat output, not a
  hand-made sample.
- Tests go in `core/tests` with .docx fixtures. Failure cases: a field
  instruction split across runs; a bookmark spanning paragraphs; hyperlinks;
  text boxes in AlternateContent; `w:sym`; right-to-left and CJK text; comment
  ranges; tracked changes left in place. Also import a cleaned file into Trados
  and memoQ and check the tag count really drops.

## Workbench, later

Workbench is Python, so it can't share this code. It can follow the same spec
with lxml. Don't use python-docx's `paragraph.runs`: it skips hyperlink runs and
text boxes, which is one reason Supercleaner only covered the body.
