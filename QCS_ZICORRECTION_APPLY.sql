-- Run on qcs_common.db. Existing settings are preserved on repeat execution.
BEGIN IMMEDIATE;
INSERT OR IGNORE INTO zx_code_mstr (zx_code_fldname,zx_code_value,zx_code_cmmt,zx_code_desc1) VALUES ('ZICORRECTION_APPLY','FG','YES','제품 AI 교정 적용');
INSERT OR IGNORE INTO zx_code_mstr (zx_code_fldname,zx_code_value,zx_code_cmmt,zx_code_desc1) VALUES ('ZICORRECTION_APPLY','RM','NO','원료 AI 교정 적용');
COMMIT;
