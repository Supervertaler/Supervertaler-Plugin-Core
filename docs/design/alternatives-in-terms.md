# Alternatives in terms

**Status: draft, for the owner of core to review.** Written by the memoQ session
on 2026-09-19 under the rule that the non-owner may write a core change with the
owner's prior agreement, which was given. Measurements are the Trados side's,
taken from the live termbase. It belongs beside the cross-product matching note.

Supervertaler for Trados and Supervertaler for memoQ read and write **the same
termbase**. This note says how a term that has more than one acceptable form is
represented, because the answer had been decided in one place, assumed in
another, and guessed at in a third.

---

## 1. Why this exists

A term with alternatives arrives written as one string with a slash in it:
`inrichting / werkwijze`, `device / method`. Three different pieces of code each
decided separately what that meant, and each decided differently. The result was
terms that match almost nothing, alternatives that vanish into a note, and rows
that look like terminology and are not.

It keeps recurring because a slash in a cell is genuinely ambiguous, and because
nothing was written down.

## 2. The rule

> **One term is one source and one target. Neither ever contains alternatives.**
> **Alternatives live in the synonyms table, never in a string.**

That is the whole of it. The clauses below say what follows.

This is not new. The prompt generator has always told the model that a locked
target is the single binding rendering, and that a source needing different
renderings in different collocations gets a row per collocation with the
collocation named. The rule already existed and was already right. What was
missing is that nothing enforced it and nothing else knew about it.

## 3. Where alternatives do live

`termbase_synonyms` carries a `language` column saying whether each synonym is a
**source**-side or a **target**-side variant. That distinction is load-bearing:

- **Source synonyms are matched.** A term with two spellings genuinely has two
  things to look for.
- **Target synonyms are stored and displayed, never matched on.** A target-side
  variant is not an alternative source to search for.

**A target synonym is information for the translator, and never reaches the
model. The locked target stays the single binding rendering.**

The obvious objection to that last clause is worth answering, because a reader
will raise it. A model given only one rendering may force it where it does not
fit: a termbase correctly saying *applications* is *aanvragen* produced
"Mashup-aanvragen", which is nonsense. But the prompt already tells the model
that approved terms are a strong steer it may override when an entry is clearly
wrong for the sentence, while forbidden terms are absolute. The model therefore
already has permission to deviate with reason, and does not need a menu to do
it. Offering one would buy nothing and cost the consistency terminology exists
to provide. The asymmetry between a strong steer and an absolute ban is what
makes a single binding target safe.

## 4. What a slash means, and where it may be read

`" / "` with spaces is the separator. `and/or`, `km/h` and `24/7` are single
terms and stay single terms; the spaces are what make this safe.

**Only code that reads prose written by a person or a model may interpret a
slash** — today that is `PromptGlossaryExtractor`, reading a locked-terms table.
Everything downstream receives terms already resolved and must treat a slash as
an ordinary character.

A slash reaching a term row is therefore an error at a boundary, not a notation
to support.

## 5. Resolving a slash at the boundary

Four shapes, and only the first three can be resolved without a human.

| the row | what to do |
|---|---|
| both sides listed, counts match | pair them positionally |
| several sources, one target | each source renders that way |
| one source, several renderings | the first is binding, the rest recorded in the note |
| both sides listed, counts differ | no pairing is possible; collapse the target as above, then give each source the collapsed target |

**Collapse the target before pairing, never after.** Collapsing first leaves the
source holding a slash and the target holding one word, so the counts no longer
match anywhere downstream: one term is stored whose source is the literal
`a / b`, and the second rendering survives only inside a note.

**Several sources against one target cannot be split automatically in existing
data.** This is the case that looks mechanical and is not. In a prompt table the
row means each source renders that way, because the generator wrote it. In a
termbase the same shape usually means somebody declined to decide, and splitting
it stores a wrong term rather than a narrow one — `werkwijze -> device` where
the intended target was `method`. Wrong is worse than narrow. A human decides.

## 6. A term with a slash is narrow, not dead

Worth stating because it invites a bulk delete. A source containing `" / "`
contains a space, so it is a multi-word term, matched by finding the text inside
the segment with boundary checks rather than by looking up a word. It matches a
segment writing the same construction — a claims preamble saying *"de inrichting
/ werkwijze volgens conclusie 1"* fires it — and nothing else. Not both halves
present but apart, not either half alone, not the two words adjacent without the
slash.

Measured independently in both products on 2026-09-19, same result for the same
underlying reason rather than by coincidence.

## 7. The data this was written against

The live termbase, 2026-09-19: 37,359 terms.

| | rows |
|---|---|
| source contains `" / "` | 50 |
| target contains `" / "` | 25 |
| both sides | 11 |
| **distinct rows affected** | **64** |
| synonyms containing `" / "` | **0** |

Classified:

| | rows |
|---|---|
| both sides, counts match — safely pairable | 11 |
| several sources, one target — needs a human | 39 |
| one source, several renderings — needs a human | 14 |
| counts differ | 0 |

So a cleanup is **11 automatic and 53 needing a pass**, not 64 mechanical fixes.

The zero in the synonyms row is the important number. Nothing has ever put a
slash into a synonym, so this note codifies what every writer has already done
rather than imposing something new. And every one of the 53 is a decision
somebody declined to make at entry time — which is the argument for the rule,
because the rule is what stops the next 53 accruing.

## 8. Changing this

The rule and the synonym semantics are cross-product contracts. Change either in
one product only in the same session as the other, the way the termbase
full-text index triggers and the matching fold were agreed.

---

## Appendix: two things learned while writing this

Both earned during a single afternoon, and both about how the defects above were
found rather than about terminology.

**Fixing an obvious surface creates a blind spot beside it.** Three instances in
one day: a release freeze written against the wrong mechanism, a field assumed
missing that was already there, and a fix that named three branches and left the
fourth combination unexamined. *Adjacent* means the thing the fix quietly
changed the conditions for, not the thing next to it in the file.

The first two were caught by the author's own tests. The third was caught by a
reviewer — necessarily, because a test only covers the case its author thought
of, and the blind spot is by definition the case they did not. Review and tests
catch different classes of thing and are not substitutes.

**Assert what the thing is, not how many there are.** Twice in that afternoon the
shape of the assertion mattered more than having one. A count passed a pairing
that produced the right number of terms with the wrong targets. An existence
check passed a fix that had silently stopped generating all but one of the
forbidden terms it should have.
