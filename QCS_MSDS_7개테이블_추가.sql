-- QCS MSDS 관련 테이블 7개 추가
-- 기존 CoA Register 테이블 및 zx_code_mstr 유지
-- 실행 전 qcs.db 백업 필수

PRAGMA foreign_keys = ON;
BEGIN TRANSACTION;

-- 1. MSDS 기본정보 (36개 컬럼)
CREATE TABLE IF NOT EXISTS msds_mstr (
    msds_msds_id INTEGER PRIMARY KEY,
    msds_file_name TEXT,
    msds_file_path TEXT,
    msds_file_hash TEXT,
    msds_graco_code TEXT,
    msds_product_name TEXT,
    msds_supplier_name TEXT,
    msds_supplier_phone TEXT,
    msds_supplier_address TEXT,
    msds_revision_date TEXT,
    msds_revision_no TEXT,
    msds_is_latest INTEGER,
    msds_h_codes TEXT,
    msds_corrosive_flag TEXT,
    msds_ppe TEXT,
    msds_lel TEXT,
    msds_uel TEXT,
    msds_flash_point TEXT,
    msds_autoignition_temp TEXT,
    msds_vapor_pressure TEXT,
    msds_reactivity TEXT,
    msds_toxicity TEXT,
    msds_un_number TEXT,
    msds_registered_at TEXT,
    msds_updated_at TEXT,
    msds_is_deleted INTEGER,
    msds__num1 REAL,
    msds__num2 REAL,
    msds__num3 REAL,
    msds__num4 REAL,
    msds__char1 TEXT,
    msds__char2 TEXT,
    msds__char3 TEXT,
    msds__char4 TEXT,
    msds__memo1 TEXT,
    msds__memo2 TEXT
);

-- 2. MSDS 구성성분 (22개 컬럼)
CREATE TABLE IF NOT EXISTS msds_comp_dtl (
    msds_comp_msds_id INTEGER PRIMARY KEY,
    msds_comp_parent_msds_id INTEGER NOT NULL,
    msds_comp_name TEXT,
    msds_comp_cas_no TEXT,
    msds_comp_content_raw TEXT,
    msds_comp_content_min REAL,
    msds_comp_content_max REAL,
    msds_comp_content_calculated REAL,
    msds_comp_molecular_formula TEXT,
    msds_comp_twa TEXT,
    msds_comp_stel TEXT,
    msds_comp_sort_order INTEGER,
    msds_comp__num1 REAL,
    msds_comp__num2 REAL,
    msds_comp__num3 REAL,
    msds_comp__num4 REAL,
    msds_comp__char1 TEXT,
    msds_comp__char2 TEXT,
    msds_comp__char3 TEXT,
    msds_comp__char4 TEXT,
    msds_comp__memo1 TEXT,
    msds_comp__memo2 TEXT,
    FOREIGN KEY (msds_comp_parent_msds_id)
        REFERENCES msds_mstr(msds_msds_id) ON DELETE RESTRICT
);

-- 3. MSDS 운영정보 (18개 컬럼)
CREATE TABLE IF NOT EXISTS msds_oper_dtl (
    msds_oper_msds_id INTEGER PRIMARY KEY,
    msds_oper_parent_msds_id INTEGER NOT NULL,
    msds_oper_usage_status TEXT,
    msds_oper_location TEXT,
    msds_oper_stock_kg REAL,
    msds_oper_receipt_type TEXT,
    msds_oper_business_type TEXT,
    msds_oper_updated_at TEXT,
    msds_oper__num1 REAL,
    msds_oper__num2 REAL,
    msds_oper__num3 REAL,
    msds_oper__num4 REAL,
    msds_oper__char1 TEXT,
    msds_oper__char2 TEXT,
    msds_oper__char3 TEXT,
    msds_oper__char4 TEXT,
    msds_oper__memo1 TEXT,
    msds_oper__memo2 TEXT,
    FOREIGN KEY (msds_oper_parent_msds_id)
        REFERENCES msds_mstr(msds_msds_id) ON DELETE RESTRICT,
    UNIQUE (msds_oper_parent_msds_id)
);

-- 4. CAS 참조정보 (22개 컬럼)
CREATE TABLE IF NOT EXISTS cas_ref_mstr (
    cas_ref_cas_id INTEGER PRIMARY KEY,
    cas_ref_cas_no TEXT NOT NULL,
    cas_ref_toxic_substance TEXT,
    cas_ref_accident_substance TEXT,
    cas_ref_restricted_prohibited TEXT,
    cas_ref_priority_regulated TEXT,
    cas_ref_persistent_pollutant TEXT,
    cas_ref_emission_survey TEXT,
    cas_ref_controlled_substance TEXT,
    cas_ref_special_control TEXT,
    cas_ref_workplace_measure TEXT,
    cas_ref_updated_at TEXT,
    cas_ref__num1 REAL,
    cas_ref__num2 REAL,
    cas_ref__num3 REAL,
    cas_ref__num4 REAL,
    cas_ref__char1 TEXT,
    cas_ref__char2 TEXT,
    cas_ref__char3 TEXT,
    cas_ref__char4 TEXT,
    cas_ref__memo1 TEXT,
    cas_ref__memo2 TEXT,
    UNIQUE (cas_ref_cas_no)
);

-- 5. 규제 검토 결과 (27개 컬럼)
CREATE TABLE IF NOT EXISTS reg_result_dtl (
    reg_result_result_id INTEGER PRIMARY KEY,
    reg_result_msds_id INTEGER NOT NULL,
    reg_result_comp_id INTEGER NOT NULL,
    reg_result_priority_match TEXT,
    reg_result_toxic_substance TEXT,
    reg_result_accident_substance TEXT,
    reg_result_restricted_prohibited TEXT,
    reg_result_priority_regulated TEXT,
    reg_result_persistent_pollutant TEXT,
    reg_result_emission_survey TEXT,
    reg_result_hazardous_chemical TEXT,
    reg_result_controlled_substance TEXT,
    reg_result_special_control TEXT,
    reg_result_workplace_measure TEXT,
    reg_result_hazardous_goods_class TEXT,
    reg_result_judgment_status TEXT,
    reg_result_judged_at TEXT,
    reg_result__num1 REAL,
    reg_result__num2 REAL,
    reg_result__num3 REAL,
    reg_result__num4 REAL,
    reg_result__char1 TEXT,
    reg_result__char2 TEXT,
    reg_result__char3 TEXT,
    reg_result__char4 TEXT,
    reg_result__memo1 TEXT,
    reg_result__memo2 TEXT,
    FOREIGN KEY (reg_result_msds_id)
        REFERENCES msds_mstr(msds_msds_id) ON DELETE RESTRICT,
    FOREIGN KEY (reg_result_comp_id)
        REFERENCES msds_comp_dtl(msds_comp_msds_id) ON DELETE RESTRICT
);

-- 6. PDF 추출 및 검증 결과 (20개 컬럼)
CREATE TABLE IF NOT EXISTS extract_result_dtl (
    extract_result_result_id INTEGER PRIMARY KEY,
    extract_result_msds_id INTEGER NOT NULL,
    extract_result_comp_id INTEGER,
    extract_result_field_name TEXT NOT NULL,
    extract_result_original_value TEXT,
    extract_result_edited_value TEXT,
    extract_result_confidence REAL,
    extract_result_validation_status TEXT,
    extract_result_source_page INTEGER,
    extract_result_updated_at TEXT,
    extract_result__num1 REAL,
    extract_result__num2 REAL,
    extract_result__num3 REAL,
    extract_result__num4 REAL,
    extract_result__char1 TEXT,
    extract_result__char2 TEXT,
    extract_result__char3 TEXT,
    extract_result__char4 TEXT,
    extract_result__memo1 TEXT,
    extract_result__memo2 TEXT,
    FOREIGN KEY (extract_result_msds_id)
        REFERENCES msds_mstr(msds_msds_id) ON DELETE RESTRICT,
    FOREIGN KEY (extract_result_comp_id)
        REFERENCES msds_comp_dtl(msds_comp_msds_id) ON DELETE RESTRICT
);

-- 7. 변경 이력 (14개 컬럼)
CREATE TABLE IF NOT EXISTS audit_log_hist (
    audit_log_log_id INTEGER PRIMARY KEY,
    audit_log_msds_id INTEGER,
    audit_log_product_name TEXT,
    audit_log_content TEXT,
    audit_log__num1 REAL,
    audit_log__num2 REAL,
    audit_log__num3 REAL,
    audit_log__num4 REAL,
    audit_log__char1 TEXT,
    audit_log__char2 TEXT,
    audit_log__char3 TEXT,
    audit_log__char4 TEXT,
    audit_log__memo1 TEXT,
    audit_log__memo2 TEXT
);

-- 인덱스 11개
CREATE INDEX IF NOT EXISTS idx_msds_graco_code
    ON msds_mstr(msds_graco_code);

CREATE INDEX IF NOT EXISTS idx_msds_product_name
    ON msds_mstr(msds_product_name);

CREATE INDEX IF NOT EXISTS idx_msds_file_hash
    ON msds_mstr(msds_file_hash);

CREATE INDEX IF NOT EXISTS idx_comp_parent
    ON msds_comp_dtl(msds_comp_parent_msds_id);

CREATE INDEX IF NOT EXISTS idx_comp_cas
    ON msds_comp_dtl(msds_comp_cas_no);

CREATE INDEX IF NOT EXISTS idx_reg_msds
    ON reg_result_dtl(reg_result_msds_id);

CREATE INDEX IF NOT EXISTS idx_reg_comp
    ON reg_result_dtl(reg_result_comp_id);

CREATE INDEX IF NOT EXISTS idx_extract_msds
    ON extract_result_dtl(extract_result_msds_id);

CREATE INDEX IF NOT EXISTS idx_extract_comp
    ON extract_result_dtl(extract_result_comp_id);

CREATE INDEX IF NOT EXISTS idx_audit_msds
    ON audit_log_hist(audit_log_msds_id);

CREATE INDEX IF NOT EXISTS idx_audit_product
    ON audit_log_hist(audit_log_product_name);

COMMIT;