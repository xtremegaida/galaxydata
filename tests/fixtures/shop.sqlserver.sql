-- The shop fixture (SQL Server). Kept in step with shop.sqlite.sql and shop.duckdb.sql. Batches are separated by GO.
CREATE TABLE customers (
   id int IDENTITY(1, 1) CONSTRAINT pk_customers PRIMARY KEY,
   name nvarchar(100) NOT NULL,
   city nvarchar(40),
   credit_limit decimal(10,2),
   created_at datetime2 CONSTRAINT df_customers_created DEFAULT SYSUTCDATETIME()
);
CREATE UNIQUE INDEX ux_customers_name ON customers(name);
EXEC sp_addextendedproperty 'MS_Description', 'People and companies who order', 'SCHEMA', 'dbo', 'TABLE', 'customers';
EXEC sp_addextendedproperty 'MS_Description', 'In rand', 'SCHEMA', 'dbo', 'TABLE', 'customers', 'COLUMN', 'credit_limit';

CREATE TABLE addresses (
   id int CONSTRAINT pk_addresses PRIMARY KEY,
   customer_id int NOT NULL CONSTRAINT fk_addresses_customer REFERENCES customers(id),
   line1 nvarchar(100) NOT NULL,
   city nvarchar(40)
);

CREATE TABLE orders (
   id bigint CONSTRAINT pk_orders PRIMARY KEY,
   customer_id int NOT NULL CONSTRAINT fk_orders_customer REFERENCES customers(id),
   ship_address_id int CONSTRAINT fk_orders_ship REFERENCES addresses(id),
   bill_address_id int CONSTRAINT fk_orders_bill REFERENCES addresses(id),
   status varchar(20) NOT NULL CONSTRAINT df_orders_status DEFAULT 'open',
   total decimal(10,2) NOT NULL,
   order_date date NOT NULL,
   placed_at datetimeoffset,
   version rowversion
);
CREATE INDEX ix_orders_status ON orders(status);
CREATE INDEX ix_orders_open ON orders(order_date) WHERE status = 'open';

CREATE TABLE order_lines (
   order_id bigint NOT NULL CONSTRAINT fk_order_lines_order REFERENCES orders(id) ON DELETE CASCADE,
   line_no int NOT NULL,
   product_code varchar(20) NOT NULL,
   qty int NOT NULL,
   price decimal(10,2) NOT NULL,
   amount AS (qty * price),
   CONSTRAINT pk_order_lines PRIMARY KEY (order_id, line_no)
);

CREATE TABLE employees (
   id int CONSTRAINT pk_employees PRIMARY KEY,
   name nvarchar(100) NOT NULL,
   manager_id int
);

CREATE TABLE audit_log (
   at datetime2,
   message nvarchar(max)
);
GO
CREATE SCHEMA crm;
GO
CREATE TABLE crm.contacts (
   id uniqueidentifier CONSTRAINT pk_contacts PRIMARY KEY,
   customer_ref int,
   email varchar(200) NOT NULL CONSTRAINT uq_contacts_email UNIQUE,
   [Display Name] nvarchar(100)
);
GO
CREATE VIEW open_orders AS SELECT id, customer_id, total FROM orders WHERE status = 'open';
GO
CREATE TRIGGER trg_orders_updated ON orders AFTER UPDATE AS
   INSERT INTO audit_log(at, message) SELECT SYSUTCDATETIME(), CONCAT('order ', id, ' updated') FROM inserted;
GO
SET IDENTITY_INSERT customers ON;
INSERT INTO customers (id, name, city, credit_limit) VALUES
   (1, 'Acme Ltd', 'Cape Town', 5000.00),
   (2, 'Beta Corp', 'Johannesburg', 1000.00),
   (3, 'Gamma Inc', NULL, NULL);
SET IDENTITY_INSERT customers OFF;
INSERT INTO addresses (id, customer_id, line1, city) VALUES
   (1, 1, '1 Main Rd', 'Cape Town'),
   (2, 2, '9 High St', 'Johannesburg');
INSERT INTO orders (id, customer_id, ship_address_id, bill_address_id, status, total, order_date, placed_at) VALUES
   (1001, 1, 1, 1, 'open', 250.00, '2026-01-05', '2026-01-05 10:30:00 +02:00'),
   (1002, 1, 1, NULL, 'shipped', 99.50, '2026-01-09', '2026-01-09 23:15:00 -05:00'),
   (1003, 2, 2, 2, 'open', 12.25, '2026-02-01', '2026-01-05 11:00:00 +05:00'),
   (1004, 3, NULL, NULL, 'cancelled', 0.00, '2026-02-14', NULL);
INSERT INTO order_lines (order_id, line_no, product_code, qty, price) VALUES
   (1001, 1, 'P-100', 2, 100.00),
   (1001, 2, 'P-200', 1, 50.00),
   (1002, 1, 'P-100', 1, 99.50),
   (1003, 1, 'P-300', 5, 2.45);
INSERT INTO employees (id, name, manager_id) VALUES (1, 'Ann', NULL), (2, 'Ben', 1), (3, 'Cal', 1);
INSERT INTO crm.contacts VALUES ('2f1c0000-0000-4000-8000-000000000001', 1, 'ann@acme.test', 'Ann at Acme');
-- A foreign key added WITH NOCHECK isn't trusted: SQL Server doesn't vouch for the rows it had.
ALTER TABLE employees WITH NOCHECK ADD CONSTRAINT fk_employees_manager FOREIGN KEY (manager_id) REFERENCES employees(id);
