-- The shop fixture (ClickHouse). Kept in step with shop.sqlite.sql and shop.postgres.sql. ClickHouse has no keys,
-- foreign keys, unique indexes or triggers: the tests declare the keys and relations in an overlay. Tables are
-- MergeTree, sorted by what the others' primary keys are. There is no crm schema: in ClickHouse it would be a
-- database of the server's, not the test's. Times with an offset are written in UTC. The tests split the script at
-- each semicolon, so only the ends of statements have one.
CREATE TABLE customers (
   id Int32,
   name String,
   city Nullable(String),
   credit_limit Nullable(Decimal(10, 2)) COMMENT 'In rand',
   created_at Nullable(DateTime64(6)) DEFAULT now64(6)
) ENGINE = MergeTree ORDER BY id COMMENT 'People and companies who order';

CREATE TABLE addresses (
   id Int32,
   customer_id Int32,
   line1 String,
   city Nullable(String)
) ENGINE = MergeTree ORDER BY id;

CREATE TABLE orders (
   id Int64,
   customer_id Int32,
   ship_address_id Nullable(Int32),
   bill_address_id Nullable(Int32),
   status LowCardinality(String) DEFAULT 'open',
   total Decimal(10, 2),
   order_date Date,
   placed_at Nullable(DateTime64(6, 'UTC')),
   tags Array(String)
) ENGINE = MergeTree ORDER BY id;

CREATE TABLE order_lines (
   order_id Int64,
   line_no Int32,
   product_code String,
   qty Int32,
   price Decimal(10, 2),
   amount Decimal(12, 2) MATERIALIZED qty * price
) ENGINE = MergeTree ORDER BY (order_id, line_no);

CREATE TABLE employees (
   id Int32,
   name String,
   manager_id Nullable(Int32)
) ENGINE = MergeTree ORDER BY id;

CREATE TABLE audit_log (
   at Nullable(DateTime64(6)),
   message Nullable(String)
) ENGINE = MergeTree ORDER BY tuple();

CREATE VIEW open_orders AS SELECT id, customer_id, total FROM orders WHERE status = 'open';

INSERT INTO customers (id, name, city, credit_limit) VALUES
   (1, 'Acme Ltd', 'Cape Town', 5000.00),
   (2, 'Beta Corp', 'Johannesburg', 1000.00),
   (3, 'Gamma Inc', NULL, NULL);
INSERT INTO addresses (id, customer_id, line1, city) VALUES
   (1, 1, '1 Main Rd', 'Cape Town'),
   (2, 2, '9 High St', 'Johannesburg');
INSERT INTO orders (id, customer_id, ship_address_id, bill_address_id, status, total, order_date, placed_at) VALUES
   (1001, 1, 1, 1, 'open', 250.00, '2026-01-05', '2026-01-05 08:30:00'),
   (1002, 1, 1, NULL, 'shipped', 99.50, '2026-01-09', '2026-01-10 04:15:00'),
   (1003, 2, 2, 2, 'open', 12.25, '2026-02-01', '2026-01-05 06:00:00'),
   (1004, 3, NULL, NULL, 'cancelled', 0.00, '2026-02-14', NULL);
INSERT INTO order_lines (order_id, line_no, product_code, qty, price) VALUES
   (1001, 1, 'P-100', 2, 100.00),
   (1001, 2, 'P-200', 1, 50.00),
   (1002, 1, 'P-100', 1, 99.50),
   (1003, 1, 'P-300', 5, 2.45);
INSERT INTO employees (id, name, manager_id) VALUES (1, 'Ann', NULL), (2, 'Ben', 1), (3, 'Cal', 1);
