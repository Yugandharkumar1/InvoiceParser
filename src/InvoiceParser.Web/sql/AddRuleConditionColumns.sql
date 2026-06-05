-- ============================================================
-- Migration: Add configurable condition & transform columns
--            to VendorParsingRules table
-- Run once against the application database (CodePulseDb / datamart)
-- ============================================================

-- 1. ConditionType — how the condition is evaluated
--    Default: 'regex_match' preserves all existing rule behaviour.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('VendorParsingRules')
      AND name = 'ConditionType'
)
BEGIN
    ALTER TABLE VendorParsingRules
    ADD ConditionType NVARCHAR(30) NOT NULL
        CONSTRAINT DF_VendorParsingRules_ConditionType DEFAULT 'regex_match';

    PRINT 'Added column: ConditionType';
END
ELSE
    PRINT 'Column already exists: ConditionType';

-- 2. ConditionSource — what is being tested
--    Default: 'line_text' preserves all existing rule behaviour.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('VendorParsingRules')
      AND name = 'ConditionSource'
)
BEGIN
    ALTER TABLE VendorParsingRules
    ADD ConditionSource NVARCHAR(30) NOT NULL
        CONSTRAINT DF_VendorParsingRules_ConditionSource DEFAULT 'line_text';

    PRINT 'Added column: ConditionSource';
END
ELSE
    PRINT 'Column already exists: ConditionSource';

-- 3. TransformationsJson — ordered list of transform steps (JSON array)
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('VendorParsingRules')
      AND name = 'TransformationsJson'
)
BEGIN
    ALTER TABLE VendorParsingRules
    ADD TransformationsJson NVARCHAR(MAX) NULL;

    PRINT 'Added column: TransformationsJson';
END
ELSE
    PRINT 'Column already exists: TransformationsJson';

GO

-- ============================================================
-- Verify
-- ============================================================
SELECT
    name,
    max_length,
    is_nullable,
    object_definition(default_object_id) AS default_value
FROM sys.columns
WHERE object_id = OBJECT_ID('VendorParsingRules')
  AND name IN ('ConditionType', 'ConditionSource', 'TransformationsJson')
ORDER BY name;
