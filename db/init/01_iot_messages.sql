CREATE EXTENSION IF NOT EXISTS timescaledb;

CREATE TABLE IF NOT EXISTS iot_messages (
    enqueued_time TIMESTAMPTZ NOT NULL,
    iothub_creation_time TIMESTAMPTZ NULL,
    connection_device_id TEXT NULL,
    body JSONB NULL,
    properties JSONB NULL,
    system_properties JSONB NULL,
    inserted_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

SELECT create_hypertable('iot_messages', 'enqueued_time', if_not_exists => TRUE, migrate_data => TRUE);

CREATE INDEX IF NOT EXISTS ix_iot_messages_device_time ON iot_messages (connection_device_id, enqueued_time DESC);
CREATE INDEX IF NOT EXISTS ix_iot_messages_enqueued_time ON iot_messages (enqueued_time DESC);
