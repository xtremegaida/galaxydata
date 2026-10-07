-- A column of each ClickHouse type, with a row of values (1) and a row of nulls (2), where the type may be null
-- (arrays, maps, tuples and JSON can't be: theirs are empty), numbers to cross join, and codes to join by. The tests
-- split the script at each semicolon, so only the ends of statements have one.
CREATE TABLE kinds (
   id Int32,
   b Nullable(Bool),
   i8 Nullable(Int8),
   i16 Nullable(Int16),
   i32 Nullable(Int32),
   i64 Nullable(Int64),
   u8 Nullable(UInt8),
   u16 Nullable(UInt16),
   u32 Nullable(UInt32),
   u64 Nullable(UInt64),
   i128 Nullable(Int128),
   dec Nullable(Decimal(12, 4)),
   dec38 Nullable(Decimal(38, 10)),
   f32 Nullable(Float32),
   f64 Nullable(Float64),
   txt Nullable(String),
   fs Nullable(FixedString(3)),
   lc LowCardinality(Nullable(String)),
   en Nullable(Enum8('sad' = 1, 'ok' = 2, 'happy' = 3)),
   uid Nullable(UUID),
   d Nullable(Date),
   d32 Nullable(Date32),
   dt Nullable(DateTime),
   dt64 Nullable(DateTime64(3)),
   dtz Nullable(DateTime64(3, 'Africa/Johannesburg')),
   ip4 Nullable(IPv4),
   ip6 Nullable(IPv6),
   arr Array(Int32),
   mp Map(String, Int32),
   tup Tuple(Int32, String),
   js JSON
) ENGINE = MergeTree ORDER BY id;

INSERT INTO kinds VALUES
   (1, true, -8, -12, 123456, 9007199254740993, 200, 60000, 4000000000, 18446744073709551615, 170141183460469231731687303715884105727,
    1234.5678, 0.1, 1.5, 2.25, 'text', 'abc', 'low', 'happy', '2f1c0000-0000-4000-8000-000000000002', '2026-03-01', '2026-03-01',
    '2026-03-01 13:45:30', '2026-03-01 13:45:30.123', '2026-03-01 13:45:30.123', '192.168.0.1', '2001:db8::1', [1, 2, 3], {'a': 1},
    (1, 'x'), '{"a": 1}'),
   (2, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
    NULL, NULL, NULL, NULL, NULL, [], {}, (0, ''), '{}');

CREATE TABLE numbers (n Int32) ENGINE = MergeTree ORDER BY n;
INSERT INTO numbers SELECT toInt32(number + 1) FROM system.numbers LIMIT 3000;

CREATE TABLE codes (code String, n Int32) ENGINE = MergeTree ORDER BY code;
INSERT INTO codes SELECT concat('C', leftPad(toString(n), 5, '0')), n FROM numbers;
