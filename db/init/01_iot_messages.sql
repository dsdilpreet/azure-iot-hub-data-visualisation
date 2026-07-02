CREATE EXTENSION IF NOT EXISTS timescaledb;

CREATE TABLE IF NOT EXISTS iot_messages (
    message_time TIMESTAMPTZ NOT NULL,
    enqueued_time TIMESTAMPTZ NULL,
    device_id TEXT NULL,
    blob_name TEXT NULL,
    blob_time TIMESTAMPTZ NULL,
    body JSONB NOT NULL,
    properties JSONB NULL,
    system_properties JSONB NULL,
    inserted_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

SELECT create_hypertable('iot_messages', 'message_time', if_not_exists => TRUE, migrate_data => TRUE);

CREATE INDEX IF NOT EXISTS ix_iot_messages_device_time ON iot_messages (device_id, message_time DESC);
CREATE INDEX IF NOT EXISTS ix_iot_messages_enqueued_time ON iot_messages (enqueued_time DESC);
