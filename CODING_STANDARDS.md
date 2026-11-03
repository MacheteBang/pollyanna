# Coding standards

Reviewers check every diff against these rules. They are judgement calls; mechanical rules belong in the build or CI instead. Where a rule comes from the spec, the section is cited, and the spec wins if the two ever disagree. Tests follow `docs/agents/testing.md`.

## Secrecy (SPEC §5.2, §8, docs/adr/0001)

- Only the giver's own lookup maps a giver tag back to a person, and only in memory. No page, form, URL, log line, email or HTML attribute carries an assignment or message ID.
- Logs carry no email addresses, no event, assignment or message IDs, and no message bodies. An error is logged by its kind and type, never by an exception message that could quote data back.
- A notification is never addressed to a giver because of their own assignment. Givers get only emails that go to every participant at once.
- Anything new a participant can see (a page, a log line, an email) gets a case in `PairingSecrecyTests` or `NotificationSecrecyTests`.

## Web pages (SPEC §7, §8, §12)

- Refusals are `NotFound()`, so a refusal doesn't reveal that an event exists. The page checks the role; `EventManager` checks statuses and the event's own rules.
- Every post carries a CSRF token, and every management action checks that the user is an organizer of that event.
- After a post, redirect with a one-shot notice in TempData, shown by `_Notice`. Only the details forms re-render with what was typed.
- No inline styles or scripts (strict CSP). CSS goes in `wwwroot/css/site.css`, in the block for its feature. Script goes in `wwwroot/js/site.js`, and anything essential keeps a no-script path, as the reveal card does with its `data-fold` form.
- Visual choices follow §12: only the palette's colours, Courier Prime, and slips and stamps rather than cards and pills.

## Domain code

- Domain logic is a scoped DI service (`EventManager` and its partials, `Outbox`, `EmailSwitch`), not a static helper that takes `PollyannaDb`. Pure algorithms with no database, such as `Draw`, are the exception.
- Actions return an `Outcome` (ok plus a message) rather than throwing for expected refusals.
- Changing an event's status is a status-conditioned `ExecuteUpdateAsync`, so two organizers can't both claim the same step.
- Notifications are added inside the same transaction as the change that causes them, through `Outbox`.
- Time comes from the injected `TimeProvider`, never `DateTime.UtcNow`. Stored times are UTC `DateTime`.
- Ids are random GUIDs.

## Data

- Entities and their enums live in `Pollyanna.Data` (`Data/Entities.cs`). Enums are stored by name.
- Every model change ships with its migration (CI checks this). Add one with the command in CONTRIBUTING.md.
- A query that limits rows (`Take`, `First`) has an `OrderBy`.

## Naming

- Use the GLOSSARY.md terms in code as in prose, and avoid the synonyms it lists. For example, `Outbox` methods are `Add…`, not `Queue…`.
