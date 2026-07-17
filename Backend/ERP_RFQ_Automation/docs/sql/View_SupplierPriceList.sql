-- View_SupplierPriceList
-- Latest purchase price per (product, supplier), fed by SupplierPurchaseHistory.
-- Mapped read-only by EF (ViewSupplierPriceList / ToView("View_SupplierPriceList")).
-- The SQL Server original was not ported by the Postgres migration; this recreates it.
-- Applied to Neon on 2026-07-17. Re-run (idempotent) on any new environment.
--
-- Column names/casing must match the EF mapping exactly:
--   ProductId, ProductName, PartNo, SupplierId, SupplierName,
--   LatestPrice, Currency, LastPurchasedDate
-- PartNo is non-nullable in the EF model, hence the COALESCE.

CREATE OR REPLACE VIEW "View_SupplierPriceList" AS
SELECT DISTINCT ON (sph."ProductId", sph."SupplierId")
    sph."ProductId"       AS "ProductId",
    p."ProductName"       AS "ProductName",
    COALESCE(p."PartNo", '') AS "PartNo",
    sph."SupplierId"      AS "SupplierId",
    s."Name"              AS "SupplierName",
    sph."UnitPrice"       AS "LatestPrice",
    sph."Currency"        AS "Currency",
    sph."PurchaseDate"    AS "LastPurchasedDate"
FROM "SupplierPurchaseHistory" sph
JOIN "Products"  p ON p."ID" = sph."ProductId"
JOIN "Suppliers" s ON s."ID" = sph."SupplierId"
ORDER BY sph."ProductId", sph."SupplierId", sph."PurchaseDate" DESC;
