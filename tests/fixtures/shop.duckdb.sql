-- The shop fixture (DuckDB). Kept in step with shop.sqlite.sql; DuckDB has no triggers.
CREATE SEQUENCE customers_id_seq START 100;
CREATE TABLE customers (
   id INTEGER PRIMARY KEY DEFAULT nextval('customers_id_seq'),
   name VARCHAR NOT NULL UNIQUE,
   city VARCHAR,
   credit_limit DECIMAL(10,2),
   created_at TIMESTAMP DEFAULT current_timestamp
);

CREATE TABLE addresses (
   id INTEGER PRIMARY KEY,
   customer_id INTEGER NOT NULL REFERENCES customers(id),
   line1 VARCHAR NOT NULL,
   city VARCHAR
);

CREATE TABLE orders (
   id BIGINT PRIMARY KEY,
   customer_id INTEGER NOT NULL REFERENCES customers(id),
   ship_address_id INTEGER REFERENCES addresses(id),
   bill_address_id INTEGER REFERENCES addresses(id),
   status VARCHAR NOT NULL DEFAULT 'open',
   total DECIMAL(10,2) NOT NULL,
   order_date DATE NOT NULL,
   placed_at TIMESTAMPTZ,
   tags VARCHAR[]
);
CREATE INDEX ix_orders_status ON orders(status);

CREATE TABLE order_lines (
   order_id BIGINT NOT NULL REFERENCES orders(id),
   line_no INTEGER NOT NULL,
   product_code VARCHAR NOT NULL,
   qty INTEGER NOT NULL,
   price DECIMAL(10,2) NOT NULL,
   PRIMARY KEY (order_id, line_no)
);

CREATE TABLE employees (
   id INTEGER PRIMARY KEY,
   name VARCHAR NOT NULL,
   manager_id INTEGER
);

CREATE TABLE audit_log (
   "at" TIMESTAMP,
   message VARCHAR
);

CREATE SCHEMA crm;
CREATE TABLE crm.contacts (
   id UUID PRIMARY KEY,
   customer_ref INTEGER,
   email VARCHAR NOT NULL,
   "Display Name" VARCHAR
);

CREATE VIEW open_orders AS SELECT id, customer_id, total FROM orders WHERE status = 'open';

INSERT INTO customers (id, name, city, credit_limit) VALUES
   (1, 'Acme Ltd', 'Cape Town', 5000.00),
   (2, 'Beta Corp', 'Johannesburg', 1000.00),
   (3, 'Gamma Inc', NULL, NULL);
INSERT INTO addresses (id, customer_id, line1, city) VALUES
   (1, 1, '1 Main Rd', 'Cape Town'),
   (2, 2, '9 High St', 'Johannesburg');
INSERT INTO orders (id, customer_id, ship_address_id, bill_address_id, status, total, order_date) VALUES
   (1001, 1, 1, 1, 'open', 250.00, DATE '2026-01-05'),
   (1002, 1, 1, NULL, 'shipped', 99.50, DATE '2026-01-09'),
   (1003, 2, 2, 2, 'open', 12.25, DATE '2026-02-01'),
   (1004, 3, NULL, NULL, 'cancelled', 0.00, DATE '2026-02-14');
INSERT INTO order_lines (order_id, line_no, product_code, qty, price) VALUES
   (1001, 1, 'P-100', 2, 100.00),
   (1001, 2, 'P-200', 1, 50.00),
   (1002, 1, 'P-100', 1, 99.50),
   (1003, 1, 'P-300', 5, 2.45);
INSERT INTO employees (id, name, manager_id) VALUES (1, 'Ann', NULL), (2, 'Ben', 1), (3, 'Cal', 1);
INSERT INTO crm.contacts VALUES ('2f1c0000-0000-4000-8000-000000000001', 1, 'ann@acme.test', 'Ann at Acme');
