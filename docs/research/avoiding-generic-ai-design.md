# How to avoid generic AI-built design

Research for [#51](https://github.com/MacheteBang/pollyanna/issues/51), part of the map
[#50](https://github.com/MacheteBang/pollyanna/issues/50) ("A new visual identity").
Researched November 26, 2026. This file lives on a throwaway branch and is never merged.

## The answer in brief

- A design reads as generic AI output when it is made of defaults: choices that would appear
  for any subject. No single pattern proves anything. The signal is several defaults at once,
  on a page whose look says nothing about its subject.
- There are now at least two generations of tells. The first is the familiar purple-gradient
  SaaS kit. The second is the "tasteful" default that shows up once the first is avoided: a
  cream page, a thin serif headline with one italic word, a terracotta accent, pill buttons.
  **A brief that says "modern but warm" points straight at the second one**, so it is the
  main risk for Pollyanna.
- The practices that vendors and designers agree on: start from the subject's own world, fix
  a small token system before drawing, spend boldness in one place, use real content, compare
  each plan against what a generic prompt would have produced, and review rendered screens.
- Generated imagery escapes the generic look when a person owns the visual grammar (line,
  palette, shapes, what is left out), feeds the generator the project's own references, and
  redraws the result as clean vector art instead of shipping the raw output.

## Sources and how far to trust them

Read and used below:

| Key | Source | Kind |
|-----|--------|------|
| A1 | Anthropic, ["Improving frontend design through Skills"](https://claude.com/blog/improving-frontend-design-through-skills), November 12, 2025 (Rajasekaran, Wei, Bricken) | Vendor guidance, primary |
| A2 | Anthropic, [`frontend-design` skill](https://github.com/anthropics/skills/blob/main/skills/frontend-design/SKILL.md), text as of November 26, 2026 | Vendor guidance, primary |
| O1 | OpenAI, ["Designing delightful frontends with GPT-5.4"](https://developers.openai.com/blog/designing-delightful-frontends-with-gpt-5-4) | Vendor guidance, primary |
| O2 | OpenAI, ["Frontend prompt instructions"](https://developers.openai.com/api/docs/guides/frontend-prompt) | Vendor guidance, primary |
| K | Adrian Krebs, ["Scoring Show HN submissions for AI design patterns"](https://adriankrebs.ch/blog/design-slop/), April 20, 2026 | A developer's measurement of 1,590 live pages; pattern list compiled with designers |
| I | Paul Bakaus, [Impeccable README](https://github.com/pbakaus/impeccable), as of November 26, 2026 | Design tool for coding agents with a 62-rule detector |
| D | Tanya Donska, ["AI Makes Bad Design Look Good Enough"](https://builtin.com/articles/ai-design-slop-era), Built In, November 25, 2026 | Designer's essay |
| C | Stuart Crawford, ["Custom Illustrations: The Last Moat in an AI-Generated World"](https://inkbotdesign.com/custom-illustrations/), Inkbot Design, March 16, 2026 | Studio blog with a commercial interest in custom illustration |
| W | Wikipedia, ["Signs of AI writing"](https://en.wikipedia.org/wiki/Wikipedia:Signs_of_AI_writing) (first part of the page read) | Community field guide maintained by editors |
| R | Recraft, ["How to create a custom style"](https://www.recraft.ai/docs/recraft-studio/styles/custom-styles/how-to-create-a-custom-style.md) | Vendor documentation, primary |

Limits of this research:

- A2 and the Impeccable README were read as raw text. The other pages were read through a
  fetch tool that summarizes, so quotations from them are short and should be checked against
  the page before being reused anywhere that matters.
- The vendor guides describe what their own models over-produce. That makes them the best
  available primary source on the tells, but the lists are observations, not studies.
- Krebs's numbers come from deterministic CSS and DOM checks with a self-reported 5 to 10%
  false-positive rate, and the post's own counts disagree (it says 16 patterns and lists 18).
  Use it for the pattern list, not the percentages.
- Working designers are thinly represented. Two Creative Bloq features
  ([Vanessa Porter, "Everything looks the same. Now what?"](https://www.creativebloq.com/ai/everything-looks-the-same-now-what),
  June 6, 2026, and [Tom May on 2026 graphic design trends](https://www.creativebloq.com/design/graphic-design/texture-warmth-and-tactile-rebellion-the-big-graphic-design-trends-for-2026),
  December 23, 2025) could be reached only as headlines and standfirsts, and two UX Collective
  essays returned errors. Nothing below relies on them.
- The popular story that the purple default traces to Tailwind UI's indigo buttons rests on a
  joking 2025 post by Tailwind's creator, known here only through secondary reports. It is
  left out as unverified.
- No source studied illustration tells systematically. The imagery sections lean on one
  studio article (C) and on vendor image rules (O1, O2).

## Why generated design converges

Anthropic names the cause "distributional convergence": models predict from statistical
patterns in training data, safe choices that offend no one dominate that data, and an
undirected model "samples from this high-probability center" (A1). OpenAI says the same in
its own words: "When prompts are underspecified, models often fall back to high-frequency
patterns from the training data," giving output that is "plausible and functional" but drifts
toward "generic structure, weak visual hierarchy" (O1).

Two consequences matter for a design session:

- **The defaults move.** A1 notes that a model told to avoid Inter converges on another
  favorite such as Space Grotesk. A2's list of current clusters opens with the cream, serif
  and terracotta look, not the purple one. Banning a tell does not produce a choice; it
  produces the next default.
- **Competence is not the test.** Donska's point is that generated work passes review because
  each piece is fine: "AI points at nothing in particular and says, 'This is professional.'"
  (D). Krebs reaches a similar verdict on the pages he measured: "just uninspired" (K). The
  question to ask of a screen is whether it could belong to any other product.

## The tells

A tell here means a default that shows up regardless of subject. A2 is explicit that all of
these are "legitimate for some briefs"; the problem is using one without a reason.

### Whole-look clusters

A2 lists the looks that generated design currently clusters around:

1. A warm cream background (near `#F4F1EA`), a high-contrast serif display face and a
   terracotta or warm-clay accent (near `#D97757`).
2. A near-black background with one acid-green or vermilion accent.
3. A broadsheet layout: hairline rules, zero border radius, dense newspaper columns.
4. The SaaS card kit: identical rounded cards, one radius on everything, the same soft gray
   shadow under each, gradient washes as decoration.
5. Template chrome that appears whatever the subject (detailed under type, layout and copy
   below).

The Impeccable README shows the first cluster as its "without" example and calls it "the
tasteful default: cream page, thin serif, italic accent, pill buttons" (I). OpenAI's guide
tells its model to limit "beige/cream/sand/tan" and "brown/orange/espresso" palettes in the
same sentence as purple gradients (O2).

### Color

- Purple or indigo gradients on white, and purple-to-blue gradients generally (A1, O1, I, K).
- One-note palettes: a page "dominated by variations of a single hue family" (O2).
- Cream, sand and tan grounds with a terracotta or coral accent (A2, O2).
- Permanent dark mode with medium-gray body text and barely passing contrast (K); "dark-mode
  bias" (O1).
- Gradients on everything, large colored glows, colored box shadows (K); gradient orbs and
  bokeh blobs as backgrounds (O2).
- Tinted near-black (`#0B0B0B`, `#111`) standing in for black (A2).
- Gray text on colored backgrounds (I).

### Type

- Inter, Roboto, Arial or the system stack for everything (A1, A2, O1, I, K).
- The replacement favorites: Space Grotesk, Instrument Serif, Geist (K, A1).
- One accent word in a headline set in italic serif, bold or a second color (A2, K, I).
- Tracked-out all-caps labels and "eyebrow" lines above every heading (A2, K).
- A monospace face for small data labels (A2).
- Type as a neutral carrier, with no decision visible in scale, weight, width or spacing (A2).

### Layout and structure

- A centered hero in a generic sans with a badge above the headline (K).
- A hero made of a big number, a small label, supporting stats and a gradient accent (A2);
  stat banner rows (K).
- Identical feature cards, each with an icon on top (K); content "chopped into identical
  rounded cards" (A2); cards nested in cards (I, O2).
- Colored borders on the top or left edge of cards (K).
- Numbered "01 / 02 / 03" markers on content that is not a sequence (A2, K).
- Pill buttons (I) and glassmorphism (K).
- Floating badges, chips and callout boxes laid over hero imagery (O1).
- Fade-and-slide-up entrances on every section and hover transitions on every card (A2).

### Iconography

- A rounded-square icon tile above every heading (I).
- Emoji used as icons in navigation and sidebars (K).
- An icon on every feature whether or not the icon says anything (K's "identical feature
  cards, each with an icon on top" is the measured form of this).
- An arrow appended to link and button text (A2).

### Illustration and imagery

- Decoration in place of a subject: "Decorative gradients and abstract backgrounds do not
  count as the main visual idea" (O1). Fake 3D objects, orbs and blobs (O1, O2).
- "Dark, blurred, cropped, stock-like, or purely atmospheric media" (O2).
- Flat vector spot illustrations dropped into rounded cards (I, describing its "without"
  example).
- A finish that is polished and interchangeable. Crawford's test is whether a competitor
  could produce a near-identical image from a short prompt (C).
- Text, logos or interface frames baked into a generated image (O1).

### Copy

- Template punctuation: meta strings joined with middle dots ("A · B · C") and labels built
  as "WORD — fragment" with a spaced em dash (A2).
- Negative parallelism ("not just X, but Y"), the rule of three, and inflated vocabulary such
  as "crucial," "pivotal," "showcase" and "testament" (W). W warns that no sign is proof by
  itself.
- Copy that sells or describes the interface instead of naming the action. O2 forbids visible
  text that explains the app's features; A2 asks for names "by what users will understand,"
  calls to action that say exactly what happens, and one name for one action through a flow.
- Mood in place of direction in empty and error states. A2: "Errors don't apologize," and
  "An empty screen is an invitation to act."
- Volume. O1: "If deleting 30 percent of the copy improves the page, keep deleting."

## Practices that produce a distinctive identity

1. **Start from the subject's world.** "The subject's industry, subject matter, materials,
   and vernacular are where distinctive visual choices come from" (A2). Open with "the most
   characteristic thing in the subject's world." OpenAI's test for the result: "If the first
   viewport could belong to another brand after removing the nav, the branding is too weak"
   (O1).
2. **Fix a compact token system before drawing.** Four to six named colors, the typefaces and
   their roles, a layout concept, and a few principles that say what makes this design unlike
   others (A2). O1 gives the same advice ("Define your design system and constraints
   upfront") and adds limits: two typefaces at most, one accent color by default.
3. **Give visual references.** A mood board or screenshots act as "visual guardrails" (O1).
   The references should come from the subject, not from other apps.
4. **Compare the plan with the generic answer.** A2: work through a similar prompt and see
   whether it lands in the same place; if a part of the plan "reads like the generic default
   you would produce for any similar page," revise it and say why.
5. **Spend boldness in one place.** "Let one element be the memorable thing, keep everything
   around it quiet and disciplined" (A2). One orchestrated motion moment beats effects
   scattered everywhere.
6. **Make structure carry information.** Borders, numbers, labels and dividers should encode
   something true about the content (A2).
7. **Let type carry the personality.** One family, or two that are clearly distinct, chosen
   for this brief, with a deliberate scale, weights and spacing (A2).
8. **Design with real content and treat copy as design.** Supply real copy and product
   context so the model does not invent placeholder patterns (O1); write in the user's words
   (A2).
9. **Critique the rendered result, then remove something.** Review screenshots, not code, and
   take one accessory off (A2). A deterministic detector such as Impeccable's catches the
   mechanical tells (I); whether the design has a point of view remains a human call (D).
10. **Keep a record of what has been tried.** A2 notes that human designers remember and try
    something new, and that a model needs notes to do the same.

## Using generated imagery without the generic look

- **Own the grammar first.** Write down line weight, corner treatment, palette, texture and
  the "don'ts" before generating anything (C). The grammar is what makes a family of marks.
- **Use the project's own references.** Recraft's custom styles take up to ten reference
  images (five on its older models), offer vector output and a palette setting, and warn that
  low-quality references reduce fidelity (R). Hand sketches of the bow and the girl make
  references that belong to the project.
- **Treat output as a draft.** Crawford recommends generating for ideation and having a person
  produce the final asset as clean SVG (C). The current girl mark was made this way
  (generated, then cleaned up and recolored), and the same pattern can be repeated under a
  stricter grammar.
- **Show the real subject.** Images should "reveal the actual product, place, object, state
  ... or person" (O2). An illustration should show something from a gift exchange, not an
  atmosphere.
- **Keep words and interface out of images** (O1).
- **Draw one family.** Every mark shares a finish, stroke logic and palette, so the set reads
  as drawn together (C).
- **Design for the small size.** Detail is art-directed per size (C); the bow has to survive
  at 16 pixels.

## What this means for Pollyanna

These are inferences from the sources above, read against SPEC.md §12 and the map.

- **The brief is the risk.** "Modern, warm, family and friendship" is the kind of description
  that yields cream, a soft serif and a terracotta accent. §12 already bans "a cream + serif +
  coral look." The new identity has to keep that ban while the paper metaphor is open to
  change, and the paper metaphor is what currently supplies the subject-specific character.
- **The kept motifs are the strongest asset.** A faceless girl seen from behind with bows in
  her braids, and a reveal that is an act of untying, are not where a generic prompt lands.
  They should lead, with the interface kept quiet around them.
- **Open-licensed fonts overlap with the overused list.** Inter, Roboto, Space Grotesk,
  Instrument Serif and Geist are all open-licensed and all named as tells. The licensing
  constraint narrows the field toward the defaults, so the type choice needs a stated reason
  tied to the subject.
- **Some current §12 devices now sit on tell lists**: all-caps labels (the stamps) and a
  monospace face. They were chosen for a reason under the paper metaphor. If one survives,
  its reason has to survive with it.

## Checklist for a design session

Apply to each direction and each mark before showing it.

**Direction**

1. Swap test: cover the wordmark and the two motifs. Could this screen belong to a recipe
   app, a wedding site or a note-taking tool? If yes, the identity is not carrying it.
2. Source test: name the thing from the world of gift exchanges that each major choice
   (ground color, accent, typeface, shape language) comes from. "It feels warm" is not a
   source.
3. Cluster test: does the direction match one of the five clusters above, in particular cream
   plus serif plus terracotta or coral? If so, it needs a reason a stranger would accept, or
   it goes.
4. One bold thing: say in one sentence what the memorable element is. Everything else is
   quieter than it. The reveal is the natural candidate.
5. Tokens first: four to six named colors with roles, at most two typefaces with roles and
   one shape rule, written down before the mockup is drawn. Contrast checked at WCAG AA.
6. Type: the family is not on the overused list unless there is a subject-specific reason,
   and no headline accents one word in italic or color.
7. Structure: no label, number, border, badge or icon that does not encode something true.
   No eyebrow labels, no numbered markers on non-sequences, no colored card edges, no card
   grid where a list would do.
8. Motion: one orchestrated moment (the untying), responses to the user's own actions, and
   nothing else.

**Marks (bow, girl, wordmark, favicon)**

9. Grammar written before drawing: stroke weights, corner treatment, palette, what is always
   left out (the face), how a bow is constructed. Both marks follow it.
10. Prompt test: could a short prompt reproduce this mark closely? If so, it needs a decision
    in it that a prompt would not make (a specific construction, proportion or omission).
11. Hand-finished vector: if generated, redrawn or cleaned to a small, readable SVG in the
    project's palette, with no gradients, glows or soft shading left over from the generator.
12. Small sizes designed, not scaled: checked at 16 and 32 pixels.
13. Provenance recorded: tool, who holds the rights, and the license of anything adapted.

**Copy and labels**

14. Labels name the action in the participant's words; one action keeps one name from button
    to confirmation.
15. No middle-dot meta strings, no "WORD — fragment" labels, no "not just X, but Y," no
    triads for rhythm, no filler adjectives.
16. Empty and error states say what to do next, without apology or mood.
17. Cut pass: remove copy until removing more would lose information.

**Before sign-off**

18. Look at the rendered screens at phone width, not at the code or the token list, and
    remove one thing.
19. Note in the map what was tried and rejected, so the next session does not rediscover it.
