# Pollyanna

A self-hosted gift exchange for a family, for any occasion: names go into a hat,
each participant draws one recipient, and nobody, the operator included, can
see who drew whom.

## People and roles

**Person**:
Someone who has signed in to Pollyanna at least once through the identity provider.
_Avoid_: User, account, member

**Operator**:
Whoever runs a Pollyanna deployment: its server, database, draw key and mail relay. Not a role inside the app.
_Avoid_: Admin, site admin, host

**Organizer**:
A person who manages one event. Organizing gives no extra view of the draw.
_Avoid_: Admin, owner, event admin

**Participant**:
A person taking part in one event: they write notes, draw a recipient and receive a gift.
_Avoid_: Member, player, attendee

**Giver**:
The participant on the drawing side of an assignment: the one getting a gift for their recipient.
User-facing text says "your giver".
_Avoid_: Santa, Secret Santa, sender

**Recipient**:
The participant on the receiving side of an assignment: the one who gets the gift.
_Avoid_: Giftee, receiver, target

**Former participant**:
How a person who deleted their account appears in the events they stay in.
_Avoid_: Deleted user, ghost, tombstone (that's the stored row, not the domain term)

## Events

**Event**:
One exchange, e.g. "Family gifts 2026", with its own participants, rules, draw and status.
_Avoid_: Exchange, game, group, round

**Exchange date**:
The day the gifts are given. Purely informational: dates never change an event's status.
_Avoid_: Deadline, due date

**Status**:
Where an event is in its lifecycle: draft, open, drawn, revealed or archived.
_Avoid_: State, phase, stage

**Previous event**:
The earlier event this one is linked to, so the no-repeat rule can look back through it.
_Avoid_: Parent event, last year

**Invitation**:
A pending offer for an email address with no person yet to join an event; it becomes a participant at their first sign-in.
_Avoid_: Invite (as a noun), pending participant

**Notes**:
What a participant tells everyone in the event: likes, sizes, no-gos. Readable by every participant.
_Avoid_: Wishlist, stocking notes, profile

**Done shopping**:
A participant's own flag that their gift is bought, shown only to organizers.
_Avoid_: Finished, complete

**Leaving**:
A participant taking themselves out of an event before it is revealed.
_Avoid_: Declining, quitting, unsubscribing

**Request to leave**:
A leaver's pending wish to leave a drawn event when the patch can't apply; it waits for a redraw.
_Avoid_: Leave request, pending leave

## The draw

**Draw**:
Matching every participant to one recipient under the draw rules, at random, in one go.
_Avoid_: Shuffle, match, pick

**Draw rules**:
The constraints every draw and patch must satisfy: nobody draws themselves, their household, an exclusion, or a recent recipient.
_Avoid_: Constraints (in user-facing text), restrictions

**Household**:
A named group of participants within one event who never draw each other.
_Avoid_: Family, group, team

**Exclusion**:
A symmetric rule that two participants in one event never draw each other.
_Avoid_: Block, ban, restriction

**No-repeat rule**:
Nobody draws a recipient they had in the last N events along the previous-event chain.
_Avoid_: History rule, repeat avoidance

**Assignment**:
One giver's recipient in one event, stored sealed so it names no giver.
_Avoid_: Pairing (for a single one), match, pick

**Pairings**:
All of an event's assignments taken together, as in "the pairing list" shown after the reveal.
_Avoid_: Matches, results, the draw (the draw is the act, not its outcome)

**Sealed**:
Stored so the giver can't be recovered from the database alone; it takes the draw key as well.
_Avoid_: Encrypted, hidden, secret

**Draw key**:
The operator-held secret that, together with the database, is the only way to link an assignment to its giver.
_Avoid_: Encryption key, master key, secret

**Slip**:
A participant's own view of their assignment, revealed behind "Tap to reveal".
_Avoid_: Card, ticket, result

**Predictability warning**:
The manage page's warning before a draw that the rules leave few enough outcomes for pairings to be guessed.
_Avoid_: Weak draw warning

**Redraw**:
Throwing away every assignment and its thread and returning the event to open, so it can be drawn again.
_Avoid_: Reset, re-roll, reshuffle

**Patch**:
Changing a single giver's assignment so a dropout or late joiner fits a drawn event without a redraw.
_Avoid_: Fix, swap, adjustment

**Dropout**:
A participant who leaves a drawn event, whether they left or an organizer removed them.
_Avoid_: Leaver (only for one who left themselves), removed participant

**Late joiner**:
A person added to an event after it is drawn.
_Avoid_: Newcomer, latecomer, addition

**Reveal**:
The irreversible, optional step after which every participant and organizer can see the pairings.
_Avoid_: Unmask, disclose, publish

## Question box

**Question box**:
The feature that lets a giver ask their recipient questions without saying who is asking.
_Avoid_: Chat, messaging, inbox, DMs

**Thread**:
The conversation that belongs to one assignment, between its giver and its recipient.
_Avoid_: Conversation (except in user-facing text), chat, channel

**Message**:
One entry in a thread, sent by the giver or the recipient (a role, never a person).
_Avoid_: Post, comment, note (Notes are something else), email

**Question**:
A message from the giver. Only a question can start a thread.
_Avoid_: Ask, query

**Reply**:
A message from the recipient.
_Avoid_: Answer, response

**Closed thread**:
A thread the recipient has shut, taking no messages from either side until they reopen it.
_Avoid_: Blocked, muted, locked

## Notifications

**Notification**:
An email telling one person, or one invited address, that something in an event needs them.
_Avoid_: Alert, message (Messages belong to the question box), mail, push

**Notification kind**:
Which of the fixed set of things a notification is about, e.g. "Event is open" or "A question for you".
_Avoid_: Type, template, category

**Mail relay**:
The SMTP server the operator configures for sending notifications. Without one, there are no notifications.
_Avoid_: Mail server, email provider, SMTP

**Outbox**:
The notifications waiting to be sent. Nothing records them once they are sent or dropped.
_Avoid_: Queue, mail log, sent items

**Email switch**:
A person's single "Email me about Pollyanna events" setting, on by default.
_Avoid_: Subscription, notification preferences, unsubscribe
