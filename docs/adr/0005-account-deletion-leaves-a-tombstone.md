# Deleting an account leaves an empty person row

When a person deletes their account, their profile (name, email, picture, provider link) and notes are deleted, but the person row stays with only its `id`. Sealed giver tags are computed from that `id` and can't be rewritten without the plaintext pairings, and revealed pairings, kept threads and the no-repeat rule all need it to keep working. They appear as "Former participant". Signing in again creates a new person with no link to the old history.

## Considered Options

- **Deleting the row and everything that references it.** Rejected: it would cut people out of other participants' revealed and archived history and break the no-repeat rule for the people left.
- **Keeping the row and its profile, only blocking sign-in.** Rejected: it isn't a deletion.

## Consequences

- Code must treat a person with no name, email or provider link as normal, not as corrupt data.
- Re-linking by verified email (§3) can't reach a deleted person, since their email is gone.
