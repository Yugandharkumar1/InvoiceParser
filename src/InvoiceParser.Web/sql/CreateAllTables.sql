-- ============================================================
-- InvoiceParser — Full Database Setup Script
-- Run sections A and B against the correct databases.
--
-- DATABASE A (AppDb)    : Invoice, Charge, Usage, Inventory,
--                         VendorParsingRules, InvoiceFeedback, LineFeedback
-- DATABASE B (IPathDb)  : customer, carrier  (reference data)
-- ============================================================


-- ============================================================
-- ██  DATABASE A  ─  AppDb  (CodePulseDb / local app DB)
-- ============================================================

-- ------------------------------------------------------------
-- 1. t_invoice
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 't_invoice')
BEGIN
    CREATE TABLE t_invoice (
        t_invoice_id    INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
        customer_id     INT             NULL,
        carrier_id      INT             NULL,
        carrier_cd      NVARCHAR(50)    NULL,
        carrier_name    NVARCHAR(200)   NULL,
        carrier_account NVARCHAR(100)   NULL,
        invoice_number  NVARCHAR(100)   NULL,
        invoice_date    DATETIME        NULL,
        invoice_st_dtm  DATETIME        NULL,
        invoice_end_dtm DATETIME        NULL,
        invoice_due_dtm DATETIME        NULL,
        beg_bal         DECIMAL(18,2)   NULL,
        payment         DECIMAL(18,2)   NULL,
        prev_adj        DECIMAL(18,2)   NULL,
        curr_adj        DECIMAL(18,2)   NULL,
        curr_chg        DECIMAL(18,2)   NULL,
        curr_tax        DECIMAL(18,2)   NULL,
        end_bal         DECIMAL(18,2)   NULL,
        is_summary      BIT             NOT NULL DEFAULT 0,
        email_sent      BIT             NOT NULL DEFAULT 0,
        is_active       BIT             NOT NULL DEFAULT 1,
        add_usr         NVARCHAR(100)   NOT NULL DEFAULT 'InvoiceParser',
        add_dtm         DATETIME        NOT NULL DEFAULT GETDATE(),
        pdf_text        NVARCHAR(MAX)   NULL
    );
    PRINT 'Created table: t_invoice';
END
ELSE
    PRINT 'Table already exists: t_invoice';
GO

-- ------------------------------------------------------------
-- 2. t_charge
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 't_charge')
BEGIN
    CREATE TABLE t_charge (
        t_charge_id     INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
        t_invoice_id    INT             NOT NULL,
        t_usoc_map_id   INT             NOT NULL DEFAULT 0,
        charge_desc     NVARCHAR(500)   NULL,
        amount          DECIMAL(18,2)   NULL,
        line            NVARCHAR(200)   NULL,
        location        NVARCHAR(200)   NULL,
        account_number  NVARCHAR(100)   NULL,
        carrier_id      INT             NULL,

        CONSTRAINT FK_t_charge_t_invoice
            FOREIGN KEY (t_invoice_id)
            REFERENCES t_invoice (t_invoice_id)
            ON DELETE CASCADE
    );
    PRINT 'Created table: t_charge';
END
ELSE
    PRINT 'Table already exists: t_charge';
GO

-- ------------------------------------------------------------
-- 3. t_usage
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 't_usage')
BEGIN
    CREATE TABLE t_usage (
        t_usage_id      INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
        t_invoice_id    INT             NOT NULL,
        line_number     NVARCHAR(20)    NULL,
        t_usoc_map_id   INT             NULL,
        usoc_name       NVARCHAR(200)   NULL,
        usage_limit     NVARCHAR(50)    NULL,
        usage           NVARCHAR(50)    NULL,
        charge          NVARCHAR(50)    NULL,
        usagetype       NVARCHAR(50)    NULL,
        add_dtm         DATETIME        NOT NULL DEFAULT GETDATE(),
        account_id      INT             NULL,
        carrier_id      INT             NULL,

        CONSTRAINT FK_t_usage_t_invoice
            FOREIGN KEY (t_invoice_id)
            REFERENCES t_invoice (t_invoice_id)
    );
    PRINT 'Created table: t_usage';
END
ELSE
    PRINT 'Table already exists: t_usage';
GO

-- ------------------------------------------------------------
-- 4. t_inventory
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 't_inventory')
BEGIN
    CREATE TABLE t_inventory (
        t_inventory_id  INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
        t_invoice_id    INT             NOT NULL,
        customer_id     INT             NOT NULL DEFAULT 0,
        reference_number NVARCHAR(255)  NOT NULL DEFAULT '',
        service_type    NVARCHAR(255)   NULL,
        inventory_name  NVARCHAR(255)   NULL,
        employee_name   NVARCHAR(255)   NULL,
        location_name   NVARCHAR(255)   NULL,

        CONSTRAINT FK_t_inventory_t_invoice
            FOREIGN KEY (t_invoice_id)
            REFERENCES t_invoice (t_invoice_id)
    );
    PRINT 'Created table: t_inventory';
END
ELSE
    PRINT 'Table already exists: t_inventory';
GO

-- ------------------------------------------------------------
-- 5. VendorParsingRules
--    NOTE: Rules are now stored as JSON files in RuleDefinitions/.
--          This table is kept for backward compatibility only.
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'VendorParsingRules')
BEGIN
    CREATE TABLE VendorParsingRules (
        Id                  INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
        CarrierId           INT             NOT NULL DEFAULT 0,
        FieldName           NVARCHAR(100)   NOT NULL DEFAULT '',
        RegexPattern        NVARCHAR(500)   NOT NULL DEFAULT '',
        FieldType           NVARCHAR(20)    NOT NULL DEFAULT 'string',
        TargetTable         NVARCHAR(50)    NOT NULL DEFAULT 't_invoice',
        Section             NVARCHAR(50)    NULL,
        SortOrder           INT             NOT NULL DEFAULT 0,
        IsActive            BIT             NOT NULL DEFAULT 1,
        SuccessCount        INT             NOT NULL DEFAULT 0,
        FailCount           INT             NOT NULL DEFAULT 0,
        ConditionType       NVARCHAR(30)    NOT NULL
            CONSTRAINT DF_VendorParsingRules_ConditionType  DEFAULT 'regex_match',
        ConditionSource     NVARCHAR(30)    NOT NULL
            CONSTRAINT DF_VendorParsingRules_ConditionSource DEFAULT 'line_text',
        TransformationsJson NVARCHAR(MAX)   NULL
    );
    PRINT 'Created table: VendorParsingRules';
END
ELSE
BEGIN
    PRINT 'Table already exists: VendorParsingRules — checking for missing columns...';

    -- ConditionType (added in migration AddRuleConditionColumns.sql)
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('VendorParsingRules') AND name = 'ConditionType')
    BEGIN
        ALTER TABLE VendorParsingRules ADD ConditionType NVARCHAR(30) NOT NULL
            CONSTRAINT DF_VendorParsingRules_ConditionType DEFAULT 'regex_match';
        PRINT '  + Added column: ConditionType';
    END

    -- ConditionSource
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('VendorParsingRules') AND name = 'ConditionSource')
    BEGIN
        ALTER TABLE VendorParsingRules ADD ConditionSource NVARCHAR(30) NOT NULL
            CONSTRAINT DF_VendorParsingRules_ConditionSource DEFAULT 'line_text';
        PRINT '  + Added column: ConditionSource';
    END

    -- TransformationsJson
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('VendorParsingRules') AND name = 'TransformationsJson')
    BEGIN
        ALTER TABLE VendorParsingRules ADD TransformationsJson NVARCHAR(MAX) NULL;
        PRINT '  + Added column: TransformationsJson';
    END
END
GO

-- ------------------------------------------------------------
-- 6. InvoiceFeedback
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'InvoiceFeedback')
BEGIN
    CREATE TABLE InvoiceFeedback (
        Id                  INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
        CarrierId           INT             NOT NULL DEFAULT 0,
        FeedbackText        NVARCHAR(MAX)   NOT NULL DEFAULT '',
        PdfText             NVARCHAR(MAX)   NULL,
        OriginalFieldsJson  NVARCHAR(MAX)   NULL,
        ConfirmedFieldsJson NVARCHAR(MAX)   NULL,
        OriginalChargesJson NVARCHAR(MAX)   NULL,
        CreatedAt           DATETIME        NOT NULL DEFAULT GETUTCDATE(),
        IsProcessed         BIT             NOT NULL DEFAULT 0
    );
    PRINT 'Created table: InvoiceFeedback';
END
ELSE
    PRINT 'Table already exists: InvoiceFeedback';
GO

-- ------------------------------------------------------------
-- 7. LineFeedback
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'LineFeedback')
BEGIN
    CREATE TABLE LineFeedback (
        Id              INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
        RawText         NVARCHAR(2000)  NOT NULL DEFAULT '',
        NormalizedText  NVARCHAR(2000)  NOT NULL DEFAULT '',
        PredictedLabel  NVARCHAR(50)    NOT NULL DEFAULT '',
        CorrectedLabel  NVARCHAR(50)    NOT NULL DEFAULT '',
        CreatedAt       DATETIME        NOT NULL DEFAULT GETUTCDATE()
    );
    PRINT 'Created table: LineFeedback';
END
ELSE
    PRINT 'Table already exists: LineFeedback';
GO


-- ============================================================
-- ██  DATABASE B  ─  IPathDb  (datamart / iPath reference DB)
--     Run this block against your iPath / datamart database.
--     Skip if these tables already exist there.
-- ============================================================

-- ------------------------------------------------------------
-- 8. carrier
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'carrier')
BEGIN
    CREATE TABLE carrier (
        carrier_id      INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
        carrier_cd      NVARCHAR(10)    NOT NULL DEFAULT '',
        carrier_desc    NVARCHAR(255)   NOT NULL DEFAULT ''
    );
    PRINT 'Created table: carrier';
END
ELSE
    PRINT 'Table already exists: carrier';
GO

-- ------------------------------------------------------------
-- 9. customer
-- ------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'customer')
BEGIN
    CREATE TABLE customer (
        customer_id     INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
        customer_name   NVARCHAR(255)   NOT NULL DEFAULT ''
    );
    PRINT 'Created table: customer';
END
ELSE
    PRINT 'Table already exists: customer';
GO


-- ============================================================
-- Verification — run to confirm all tables exist in AppDb
-- ============================================================
SELECT
    t.name          AS TableName,
    SUM(p.rows)     AS RowCount
FROM sys.tables t
JOIN sys.partitions p ON t.object_id = p.object_id AND p.index_id IN (0,1)
WHERE t.name IN (
    't_invoice', 't_charge', 't_usage', 't_inventory',
    'VendorParsingRules', 'InvoiceFeedback', 'LineFeedback'
)
GROUP BY t.name
ORDER BY t.name;
GO
