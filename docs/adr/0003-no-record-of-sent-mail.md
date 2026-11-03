# Keep no record of sent mail

A notification lives in the outbox only until it is sent (or dropped after 24 hours of failures, or when the person turns emails off or deletes their account), then its row is deleted. Failures are logged by notification kind and SMTP error only, with no addresses and no event, assignment or message IDs. A sent-mail log would sit next to the plaintext `recipient_id` on threads and record who was emailed about what and when, which is the kind of timing record the sealed draw (ADR-0002) is built to avoid. Delivery problems are diagnosed at the mail relay, which is the operator's to inspect.

## Consequences

- The app can't answer "did Carol get the email?". Don't add a sent log, a "last emailed" column or per-address logging to answer it.
