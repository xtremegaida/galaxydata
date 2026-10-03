-- The shop fixture (SQLite). Kept in step with shop.duckdb.sql.
CREATE TABLE customers (
   id INTEGER PRIMARY KEY,
   name TEXT NOT NULL,
   city TEXT,
   credit_limit DECIMAL(10,2),
   created_at DATETIME DEFAULT CURRENT_TIMESTAMP
);
CREATE UNIQUE INDEX ux_customers_name ON customers(name);

CREATE TABLE addresses (
   id INTEGER PRIMARY KEY,
   customer_id INTEGER NOT NULL REFERENCES customers(id),
   line1 TEXT NOT NULL,
   city TEXT
);

CREATE TABLE orders (
   id INTEGER PRIMARY KEY,
   customer_id INTEGER NOT NULL REFERENCES customers(id),
   ship_address_id INTEGER REFERENCES addresses(id),
   bill_address_id INTEGER REFERENCES addresses(id),
   status TEXT NOT NULL DEFAULT 'open',
   total DECIMAL(10,2) NOT NULL,
   order_date DATE NOT NULL,
   placed_at DATETIMEOFFSET
);
CREATE INDEX ix_orders_status ON orders(status);
CREATE INDEX ix_orders_open ON orders(order_date) WHERE status = 'open';

CREATE TABLE order_lines (
   order_id INTEGER NOT NULL REFERENCES orders(id) ON DELETE CASCADE,
   line_no INTEGER NOT NULL,
   product_code VARCHAR(20) NOT NULL,
   qty INTEGER NOT NULL,
   price DECIMAL(10,2) NOT NULL,
   PRIMARY KEY (order_id, line_no)
);

CREATE TABLE employees (
   id INTEGER PRIMARY KEY,
   name TEXT NOT NULL,
   manager_id INTEGER REFERENCES employees(id)
);

CREATE TABLE audit_log (
   at DATETIME,
   message TEXT
);

CREATE VIEW open_orders AS SELECT id, customer_id, total FROM orders WHERE status = 'open';

CREATE TRIGGER trg_orders_updated AFTER UPDATE ON orders
BEGIN
   INSERT INTO audit_log(at, message) VALUES (CURRENT_TIMESTAMP, 'order ' || NEW.id || ' updated');
END;

INSERT INTO customers (id, name, city, credit_limit) VALUES
   (1, 'Acme Ltd', 'Cape Town', 5000.00),
   (2, 'Beta Corp', 'Johannesburg', 1000.00),
   (3, 'Gamma Inc', NULL, NULL);
INSERT INTO addresses (id, customer_id, line1, city) VALUES
   (1, 1, '1 Main Rd', 'Cape Town'),
   (2, 2, '9 High St', 'Johannesburg');
INSERT INTO orders (id, customer_id, ship_address_id, bill_address_id, status, total, order_date, placed_at) VALUES
   (1001, 1, 1, 1, 'open', 250.00, '2026-01-05', '2026-01-05 10:30:00+02:00'),
   (1002, 1, 1, NULL, 'shipped', 99.50, '2026-01-09', '2026-01-09 23:15:00-05:00'),
   (1003, 2, 2, 2, 'open', 12.25, '2026-02-01', '2026-01-05 11:00:00+05:00'),
   (1004, 3, NULL, NULL, 'cancelled', 0.00, '2026-02-14', NULL);
INSERT INTO order_lines (order_id, line_no, product_code, qty, price) VALUES
   (1001, 1, 'P-100', 2, 100.00),
   (1001, 2, 'P-200', 1, 50.00),
   (1002, 1, 'P-100', 1, 99.50),
   (1003, 1, 'P-300', 5, 2.45);
INSERT INTO employees (id, name, manager_id) VALUES (1, 'Ann', NULL), (2, 'Ben', 1), (3, 'Cal', 1);
