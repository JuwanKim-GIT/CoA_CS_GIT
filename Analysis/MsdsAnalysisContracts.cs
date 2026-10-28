using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace QCS.Analysis
{
    // zx_code_mstr에 저장할 코드값. 실제 테이블 컬럼 매핑은 DB 스키마 확인 후 구현.
    public enum MsdsEngineMode { Local, AiDirect, AiHybrid }
    public enum MsdsFieldStatus { Extracted, NotFound, NeedsReview, ManuallyEntered }

    public sealed class MsdsExtractedField
    {
        public string FieldKey { get; set; } = ""; // 예: msds_product_name
        public string? Value { get; set; }
        public MsdsFieldStatus Status { get; set; } = MsdsFieldStatus.NotFound;
        public double? Confidence { get; set; } // 0~1, 계산 가능한 경우에만 사용
        public int? SourcePage { get; set; }
        public string? SourceText { get; set; }
    }

    public sealed class MsdsIngredient
    {
        public string? Name { get; set; }
        public string? CasNumber { get; set; }
        public string? ConcentrationRaw { get; set; }
        public int? SourcePage { get; set; }
        public string? SourceText { get; set; }
    }

    public sealed class MsdsAnalysisResult
    {
        public string FilePath { get; set; } = "";
        public MsdsEngineMode Engine { get; set; }
        public List<MsdsExtractedField> Fields { get; } = new();
        public List<MsdsIngredient> Ingredients { get; } = new();
        public List<string> Warnings { get; } = new();
        public bool RequiresManualEntry { get; set; }
    }

    public interface IMsdsAnalysisEngine
    {
        MsdsEngineMode Mode { get; }
        Task<MsdsAnalysisResult> AnalyzeAsync(string pdfPath, CancellationToken cancellationToken = default);
    }
}
