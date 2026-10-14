-- A small HyperWhirlpool warehouse, as its query service shows it: the public objects of two domains, web ({web})
-- and crm ({crm}), and the catalog ({catalog}) whose schema table holds the document that describes them
-- (hyperwhirlpool.schema.json, which the tests insert). A warehouse's objects are views over its own tables, here
-- they are tables, which read the same. Missing references are zero values, as the warehouse writes them: an event
-- without a CRM contact has crm_contact_id ''.

CREATE TABLE {crm}.contact (
   contact_id String,
   first_seen DateTime64(3, 'UTC'),
   last_seen DateTime64(3, 'UTC'),
   events UInt64,
   country String,
   lifecycle String
) ENGINE = MergeTree ORDER BY contact_id;

INSERT INTO {crm}.contact VALUES
   ('C1', '2025-11-02 09:00:00.000', '2026-02-10 12:00:00.000', 4, 'ZA', 'student'),
   ('C2', '2025-12-15 10:30:00.000', '2026-02-20 08:15:00.000', 2, 'KE', 'applicant'),
   ('C3', '2024-03-01 14:00:00.000', '2024-03-01 14:00:00.000', 1, 'ZA', 'alumnus');

CREATE TABLE {web}.device (
   device_id UUID,
   first_seen DateTime64(3, 'UTC'),
   last_seen DateTime64(3, 'UTC'),
   events UInt64,
   browser LowCardinality(String)
) ENGINE = MergeTree ORDER BY device_id;

INSERT INTO {web}.device VALUES
   ('00000000-0000-0000-0000-0000000000d1', '2026-01-05 08:00:00.000', '2026-02-03 17:00:00.000', 4, 'Firefox'),
   ('00000000-0000-0000-0000-0000000000d2', '2026-02-11 19:00:00.000', '2026-02-11 19:20:00.000', 2, 'Chrome');

CREATE TABLE {web}.session (
   session_id UUID,
   device_id UUID,
   first_seen DateTime64(3, 'UTC'),
   last_seen DateTime64(3, 'UTC'),
   events UInt64,
   page_views UInt64,
   duration_ms Nullable(Int64),
   traffic_type String
) ENGINE = MergeTree ORDER BY session_id;

INSERT INTO {web}.session VALUES
   ('00000000-0000-0000-0000-0000000000a1', '00000000-0000-0000-0000-0000000000d1', '2026-01-05 08:00:00.000', '2026-01-05 08:12:00.000', 3, 3, 720000, 'search'),
   ('00000000-0000-0000-0000-0000000000a2', '00000000-0000-0000-0000-0000000000d1', '2026-02-03 17:00:00.000', '2026-02-03 17:00:00.000', 1, 1, NULL, 'direct'),
   ('00000000-0000-0000-0000-0000000000a3', '00000000-0000-0000-0000-0000000000d2', '2026-02-11 19:00:00.000', '2026-02-11 19:20:00.000', 2, 2, 1200000, 'social');

-- Page views carry columns of their session (copied when the warehouse finalizes them). One refers to a session
-- the warehouse doesn't have, and carries nulls.
CREATE TABLE {web}.page_view (
   page_view_id UUID,
   session_id UUID,
   first_seen DateTime64(3, 'UTC'),
   last_seen DateTime64(3, 'UTC'),
   events UInt64,
   url String,
   session_traffic_type LowCardinality(Nullable(String)),
   session_page_views Nullable(UInt64)
) ENGINE = MergeTree ORDER BY page_view_id;

INSERT INTO {web}.page_view VALUES
   ('00000000-0000-0000-0000-0000000000f1', '00000000-0000-0000-0000-0000000000a1', '2026-01-05 08:00:00.000', '2026-01-05 08:04:00.000', 2, 'https://example.ac.za/', 'search', 3),
   ('00000000-0000-0000-0000-0000000000f2', '00000000-0000-0000-0000-0000000000a1', '2026-01-05 08:05:00.000', '2026-01-05 08:11:00.000', 1, 'https://example.ac.za/apply', 'search', 3),
   ('00000000-0000-0000-0000-0000000000f3', '00000000-0000-0000-0000-0000000000a1', '2026-01-05 08:12:00.000', '2026-01-05 08:12:00.000', 1, 'https://example.ac.za/fees', 'search', 3),
   ('00000000-0000-0000-0000-0000000000f4', '00000000-0000-0000-0000-0000000000a2', '2026-02-03 17:00:00.000', '2026-02-03 17:00:00.000', 1, 'https://example.ac.za/', 'direct', 1),
   ('00000000-0000-0000-0000-0000000000f5', '00000000-0000-0000-0000-0000000000a3', '2026-02-11 19:00:00.000', '2026-02-11 19:19:00.000', 3, 'https://example.ac.za/', 'social', 2),
   ('00000000-0000-0000-0000-0000000000f6', '00000000-0000-0000-0000-0000000000a3', '2026-02-11 19:20:00.000', '2026-02-11 19:20:00.000', 1, 'https://example.ac.za/apply', 'social', 2),
   ('00000000-0000-0000-0000-0000000000f7', '00000000-0000-0000-0000-0000000000a9', '2026-02-12 10:00:00.000', '2026-02-12 10:00:00.000', 1, 'https://example.ac.za/', NULL, NULL);

CREATE TABLE {web}.page_view_events (
   log_day Date,
   event_id UUID,
   ts DateTime64(3, 'UTC'),
   session_id UUID,
   device_id UUID,
   crm_contact_id String,
   ip String,
   url String,
   referrer Nullable(String),
   viewport_w Nullable(UInt16),
   _errors Array(String)
) ENGINE = MergeTree PARTITION BY log_day ORDER BY (session_id, ts);

INSERT INTO {web}.page_view_events VALUES
   ('2026-01-05', '00000000-0000-0000-0000-0000000000e1', '2026-01-05 08:00:00.000', '00000000-0000-0000-0000-0000000000a1', '00000000-0000-0000-0000-0000000000d1', 'C1', '192.0.2.1', 'https://example.ac.za/', 'https://search.example/', 1280, []),
   ('2026-01-05', '00000000-0000-0000-0000-0000000000e2', '2026-01-05 08:05:00.000', '00000000-0000-0000-0000-0000000000a1', '00000000-0000-0000-0000-0000000000d1', 'C1', '192.0.2.1', 'https://example.ac.za/apply', NULL, 1280, []),
   ('2026-01-05', '00000000-0000-0000-0000-0000000000e3', '2026-01-05 08:12:00.000', '00000000-0000-0000-0000-0000000000a1', '00000000-0000-0000-0000-0000000000d1', 'C1', '192.0.2.1', 'https://example.ac.za/fees', NULL, 1280, []),
   ('2026-02-03', '00000000-0000-0000-0000-0000000000e4', '2026-02-03 17:00:00.000', '00000000-0000-0000-0000-0000000000a2', '00000000-0000-0000-0000-0000000000d1', '', '192.0.2.1', 'https://example.ac.za/', NULL, NULL, []),
   ('2026-02-11', '00000000-0000-0000-0000-0000000000e5', '2026-02-11 19:00:00.000', '00000000-0000-0000-0000-0000000000a3', '00000000-0000-0000-0000-0000000000d2', 'C2', '198.51.100.7', 'https://example.ac.za/', 'https://social.example/', 390, []),
   ('2026-02-11', '00000000-0000-0000-0000-0000000000e6', '2026-02-11 19:20:00.000', '00000000-0000-0000-0000-0000000000a3', '00000000-0000-0000-0000-0000000000d2', '', '198.51.100.7', 'https://example.ac.za/apply', NULL, 390, ['viewport_w: out of range']);

CREATE TABLE {web}.link_device_contact (
   from_type String,
   from_id String,
   to_type String,
   to_id String,
   device_id Nullable(UUID),
   crm_contact_id String,
   first_seen DateTime64(3, 'UTC'),
   last_seen DateTime64(3, 'UTC'),
   evidence UInt64
) ENGINE = MergeTree ORDER BY (from_id, to_id);

INSERT INTO {web}.link_device_contact VALUES
   ('web.device', '00000000-0000-0000-0000-0000000000d1', 'crm.contact', 'C1', '00000000-0000-0000-0000-0000000000d1', 'C1', '2026-01-05 08:00:00.000', '2026-01-05 08:12:00.000', 3),
   ('web.device', '00000000-0000-0000-0000-0000000000d2', 'crm.contact', 'C2', '00000000-0000-0000-0000-0000000000d2', 'C2', '2026-02-11 19:00:00.000', '2026-02-11 19:00:00.000', 1);

CREATE TABLE {catalog}.schema (
   seq UInt32,
   hash String,
   published_at DateTime64(3, 'UTC'),
   document String
) ENGINE = MergeTree ORDER BY seq;

-- An earlier version, which a reader must not take for the latest.
INSERT INTO {catalog}.schema VALUES (11, '0000000000aa', '2026-10-01 08:00:00.000', '{"format": "hw-schema/0"}');
