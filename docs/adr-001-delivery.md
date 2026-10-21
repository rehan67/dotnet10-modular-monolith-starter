# ADR 001: Separate consumer commit

Accepted. Atomicity covers the order and outbox row within one database transaction. Delivery remains at least once. The inbox key and notification business effect commit together in the consumer transaction; the relay acknowledgement commits afterwards. This deliberately exposes the publish/ack gap without requiring a broker. Module schemas are logically owned, not separate deployable databases.

Do not replace the consumer call with a remote non-idempotent email API and claim exactly-once delivery. A real provider needs a durable idempotency contract or reconciliation.
