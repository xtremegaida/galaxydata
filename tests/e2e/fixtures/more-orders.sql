-- More rows than the shop fixture has, for pages: 150 customers after its 3 (ids 101 to 250), and 246 orders after its
-- 4, so the orders take three pages of 100 (122 of them over 500), and order 2007's customer (230) is on the second
-- page of customers, for the reference picker to open at.
WITH RECURSIVE n(i) AS (SELECT 101 UNION ALL SELECT i + 1 FROM n WHERE i < 250)
INSERT INTO customers (id, name) SELECT i, 'Customer ' || i FROM n;

WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 246)
INSERT INTO orders (id, customer_id, status, total, order_date)
SELECT
   2000 + i,
   CASE WHEN i = 7 THEN 230 ELSE 1 + i % 3 END,
   CASE i % 3 WHEN 0 THEN 'open' WHEN 1 THEN 'shipped' ELSE 'cancelled' END,
   10 + (i * 37) % 991 + (i % 4) * 0.25,
   date('2026-03-01', '+' || i || ' days')
FROM n;
