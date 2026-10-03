-- A column of each SQL Server type, with a row of values (1) and a row of nulls (2); numbers to cross join, and codes to join by.
CREATE TYPE code FROM varchar(10) NULL;
GO
CREATE TABLE kinds (
   id int CONSTRAINT pk_kinds PRIMARY KEY,
   b bit,
   u8 tinyint,
   i16 smallint,
   i32 int,
   i64 bigint,
   dec decimal(12,4),
   mo money,
   smo smallmoney,
   f32 real,
   f64 float,
   ch char(3),
   vc varchar(10),
   vmax varchar(max),
   nch nchar(3),
   nvc nvarchar(10),
   nmax nvarchar(max),
   bin binary(4),
   vbin varbinary(10),
   uid uniqueidentifier,
   d date,
   t time(3),
   dt datetime,
   dt2 datetime2(3),
   sdt smalldatetime,
   dto datetimeoffset,
   x xml,
   v sql_variant,
   h hierarchyid,
   cd code,
   rv rowversion
);

INSERT INTO kinds (id, b, u8, i16, i32, i64, dec, mo, smo, f32, f64, ch, vc, vmax, nch, nvc, nmax, bin, vbin, uid, d, t, dt, dt2, sdt, dto, x, v, h, cd) VALUES
   (1, 1, 200, -12, 123456, 9007199254740993, 1234.5678, 12.34, 5.5, 1.5, 2.25, 'ch', 'varchar', 'long', N'nc', N'ñandú', N'Ünïcödé',
    0xDEADBEEF, 0x0102, '2F1C0000-0000-4000-8000-000000000002', '2026-03-01', '13:45:30.25', '2026-03-01 13:45:30.123', '2026-03-01 13:45:30.123',
    '2026-03-01 13:45', '2026-03-01 13:45:30 +02:00', '<a>1</a>', CAST(42 AS int), '/1/2/', 'A-1');
INSERT INTO kinds (id) VALUES (2);

CREATE TABLE numbers (n int CONSTRAINT pk_numbers PRIMARY KEY);
INSERT INTO numbers SELECT TOP (3000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM sys.all_columns a CROSS JOIN sys.all_columns b;

CREATE TABLE codes (code varchar(10) CONSTRAINT pk_codes PRIMARY KEY, n int NOT NULL);
INSERT INTO codes SELECT 'C' + RIGHT('0000' + CAST(n AS varchar(5)), 5), n FROM numbers;
