-- A column of each PostgreSQL type, with a row of values (1) and a row of nulls (2); numbers to cross join, and codes to join by.
CREATE TYPE mood AS ENUM ('sad', 'ok', 'happy');
CREATE DOMAIN positive AS integer CHECK (VALUE > 0);
CREATE DOMAIN code AS varchar(10);

CREATE TABLE kinds (
   id integer PRIMARY KEY,
   b boolean,
   i16 smallint,
   i32 integer,
   i64 bigint,
   dec numeric(12,4),
   num numeric,
   f32 real,
   f64 double precision,
   txt text,
   vc varchar(10),
   ch char(3),
   js json,
   jsb jsonb,
   bin bytea,
   uid uuid,
   d date,
   t time,
   ts timestamp(3),
   tstz timestamptz,
   iv interval,
   mo money,
   en mood,
   pos positive,
   cd code,
   arr integer[],
   ip inet,
   x xml
);

INSERT INTO kinds VALUES
   (1, true, -12, 123456, 9007199254740993, 1234.5678, 0.1, 1.5, 2.25, 'text', 'varchar', 'ch', '{"a": 1}', '{"b": [1, 2]}',
    '\xdeadbeef', '2f1c0000-0000-4000-8000-000000000002', '2026-03-01', '13:45:30.25', '2026-03-01 13:45:30.123',
    '2026-03-01 13:45:30+02', '1 day 02:03:04', 12.34, 'happy', 7, 'A-1', '{1,2,3}', '192.168.0.1', '<a>1</a>'),
   (2, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
    NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);

CREATE TABLE numbers (n integer PRIMARY KEY);
INSERT INTO numbers SELECT generate_series(1, 3000);

CREATE TABLE codes (code varchar(10) PRIMARY KEY, n integer NOT NULL);
INSERT INTO codes SELECT 'C' || lpad(n::text, 5, '0'), n FROM numbers;
ANALYZE;
