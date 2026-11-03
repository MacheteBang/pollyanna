# Never email a giver about their own assignment

No notification is ever addressed to a giver because of something in their own assignment: a reply, the recipient closing or reopening the thread, a patch changing their recipient, or their recipient editing notes. Givers get only the notifications sent to every participant at once (event open, names drawn, event revealed); everything else reaches them in-app. The operator runs the mail relay and can read the database, where threads carry `recipient_id` in plaintext and message times to the hour, so one email to Bob in the hour Carol's thread changed would name Bob as Carol's giver without the draw key. Even a mail log alone, such as at a third-party relay, would pair people off from the timing of question and reply emails.

## Considered Options

- **Ban only thread activity** (replies, closing, reopening), and list other giver-directed notifications case by case. Rejected: every new kind would reopen the same argument, and "your recipient has changed" leaks in exactly the same way.
- **Delay or batch giver emails** (e.g. a daily digest) to blur the timing. Rejected: whether a digest is sent at all, and to whom, still says that something happened in that person's assignment.

## Consequences

- Emails to recipients are fine ("A question for you"): the database already says whose thread it is, and the email names no giver. Question emails are still held to the next full hour (ADR-0004).
- A new notification kind that goes to one giver, or to some givers but not all, contradicts this ADR.
