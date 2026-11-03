# Every participant has their own identity-provider account

Pollyanna has no proxy participants: everyone who takes part, children included, signs in with their own account at the family's identity provider, and the draw won't run while an invitation is pending. Each assignment is sealed to a Pollyanna person ID (ADR-0002), each slip is shown only to the person who signs in as that giver, and the question box assumes the giver and recipient are each one person. A parent drawing for a child would see the child's recipient, and a parent receiving questions for a child would read them. Giving children accounts is a job for the identity provider, which the family already runs.

## Considered Options

- **Proxy participants managed by another person.** Rejected: it needs a second kind of participant everywhere a person ID is used (sealing, notes, threads, notifications), and makes the proxy holder see more than one slip.
