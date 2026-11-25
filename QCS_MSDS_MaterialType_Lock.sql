-- Run on qcs_msds.db. Protect existing records against classification updates.
CREATE TRIGGER IF NOT EXISTS qcs_msds_material_type_immutable
BEFORE UPDATE OF msds_material_type ON msds_mstr
WHEN NEW.msds_material_type IS NOT OLD.msds_material_type
BEGIN
    SELECT RAISE(ABORT, 'MSDS material type cannot be changed');
END;
