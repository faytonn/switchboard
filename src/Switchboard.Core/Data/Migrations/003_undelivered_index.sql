-- 003_undelivered_index: find the messages still waiting for one session, quickly.
-- A partial index holds only the rows matching its WHERE clause. Once delivered, a message
-- leaves the index, so it stays tiny however long the history gets.
create index messages_undelivered_idx on messages (to_session, id) where delivered_at is null;
