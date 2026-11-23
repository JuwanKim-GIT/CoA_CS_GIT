using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Input;
using QCS.Analysis;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Microsoft.Data.Sqlite;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

// WPF와 Windows Forms 클래스 이름 충돌 방지
using UserControl = System.Windows.Controls.UserControl;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;


namespace QCS
{
    public partial class MsdsRegistrationView : UserControl
    {
        // 기존 AI 교정·학습 자동 적용 임시 중지 (재활성화 시 true)
        private static readonly bool EnableAiLearning = false;
        private sealed class PdfItem
        {
            public int Index { get; set; }
            public string FileName { get; set; } = "";
            public string GracoCode => MsdsDbRegistration.GracoCode(FileName);
            public string FileSize
            {
                get
                {
                    if (!File.Exists(FullPath)) return "";
                    long bytes = new FileInfo(FullPath).Length;
                    return bytes >= 1048576 ? $"{bytes / 1048576.0:F1} MB" : $"{bytes / 1024.0:F0} KB";
                }
            }
            public string FullPath { get; set; } = "";
            public string MaterialType { get; set; } = "";
            public string RegistrationKind { get; set; } = "확인중";
            public string Status { get; set; } = "대기";
            public MsdsAnalysisResult? Parsed { get; set; }
            public MsdsUnifiedMemory Unified { get; } = new();
            public DateTime? AnalysisStarted { get; set; }
            public DateTime? AnalysisFinished { get; set; }
            public string AnalysisModel { get; set; } = "";
            public string AnalysisStatus { get; set; } = "대기";
            public string AnalysisDisplay { get; set; } = "대기";
            public bool Dirty { get; set; }
            public List<MsdsLearningEntry> Applied { get; set; } = new();
            public Dictionary<string,MsdsLearningEntry> Pending { get; set; } = new();
            public Dictionary<string,MsdsLearningEntry> SavedLearning { get; set; } = new();
        }

        private sealed class IngredientEdit
        {
            public int Index { get; set; }
            public string Name { get; set; } = "";
            public string CasNumber { get; set; } = "";
            public string ConcentrationRaw { get; set; } = "";
            public string Twa { get; set; } = "";
            public string Stel { get; set; } = "";
            public string OralLd50 { get; set; } = "";
            public string DermalLd50 { get; set; } = "";
            public string InhalationLc50 { get; set; } = "";

            public string Minimum => FormatAmount(MsdsDbRegistration.Amounts(ConcentrationRaw).Item1);
            public string Maximum => FormatAmount(MsdsDbRegistration.Amounts(ConcentrationRaw).Item2);
            public string Calculated => FormatAmount(MsdsDbRegistration.Amounts(ConcentrationRaw).Item3);
            private static string FormatAmount(double? value) => value?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }

        private PdfItem? _displayedItem;
        private bool _loadingFields;
        private bool _revertingSelection;
        private readonly string[] _editableFieldNames =
        {
            "ProductNameBox", "SupplierNameBox", "GracoCodeBox", "RevisionDateBox",
            "RevisionNumberBox", "PhoneBox", "AddressBox", "UnNumberBox", "HazardCodesBox",
            "AutoIgnitionBox", "VaporPressureBox", "LelBox", "UelBox", "FlashPointBox",
            "CorrosivityBox", "PersonalProtectionBox", "MoelSubmissionBox", "MolecularFormulaBox", "PhysicalStateBox", "HazardousGoodsBox", "ToxicityBox", "ReactivityBox"
        };
        private System.Collections.Generic.Dictionary<string, string> CaptureFields()
        {
            var values = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var name in _editableFieldNames)
                if (FindName(name) is System.Windows.Controls.TextBox box) values[name] = box.Text;
            return values;
        }
        private System.Collections.Generic.List<IngredientEdit> CaptureIngredients() =>
            IngredientsGrid.Items.OfType<IngredientEdit>().Select(v => new IngredientEdit
            { Index = v.Index, Name = v.Name, CasNumber = v.CasNumber,
              ConcentrationRaw = v.ConcentrationRaw, Twa = v.Twa, Stel = v.Stel, OralLd50 = v.OralLd50, DermalLd50 = v.DermalLd50, InhalationLc50 = v.InhalationLc50 }).ToList();
        private void PublishStatus(string message)
        {
            if (System.Windows.Window.GetWindow(this) is MainWindow main)
                main.SetRunStatus(message);
        }

        private void SetStatus(PdfItem item, string value)
        {
            item.Status = value;
            FilesGrid.Items.Refresh();
        }
        // UI 상태만 갱신한다. 실제 저장·추가·삭제 조건과 처리 로직은 기존 메서드에서 검증한다.
        private void UpdateActionButtons()
        {
            if (DbSaveButton == null || AddIngredientButton == null || RemoveIngredientButton == null) return;
            if (ExportAnalysisButton != null) ExportAnalysisButton.IsEnabled = !_isAnalyzing && _files.Any(v => v.Parsed != null);
            DbSaveButton.IsEnabled = !_isAnalyzing && FilesGrid != null && FilesGrid.SelectedItems.Cast<PdfItem>().Any(v => v.Unified.Confirmed);
            bool canEditIngredients = !_isAnalyzing && _displayedItem?.Parsed != null;
            AddIngredientButton.IsEnabled = canEditIngredients;
            RemoveIngredientButton.IsEnabled = canEditIngredients && IngredientsGrid?.SelectedItem != null;
        }

        private static readonly string[] CasReferenceTypes = { "인체", "사고", "제한", "중점", "잔류", "배출량" };
        private System.Windows.Controls.TextBox[] ReferenceBoxes => new[]
        { HumanReferenceBox, AccidentReferenceBox, RestrictReferenceBox,
          PriorityReferenceBox, PersistReferenceBox, EmissionReferenceBox };

        private void ClearCasReferences()
        {
            foreach (var box in ReferenceBoxes) box.Clear();
        }

        private void LoadCasReferences()
        {
            ClearCasReferences();
            if (IngredientsGrid.SelectedItem is not IngredientEdit ingredient ||
                string.IsNullOrWhiteSpace(ingredient.CasNumber)) return;
            try
            {
                var found = ReadCasReferences(ingredient.CasNumber);
                for (int i = 0; i < CasReferenceTypes.Length; i++)
                    if (found.TryGetValue(CasReferenceTypes[i], out var entries))
                        ReferenceBoxes[i].Text = string.Join(Environment.NewLine, entries);
            }
            catch (Exception ex)
            {
                PublishStatus("CAS 참조정보 조회 실패: " + ex.Message);
            }
        }

        private static Dictionary<string, List<string>> ReadCasReferences(string casNumber)
        {
            if (string.IsNullOrWhiteSpace(casNumber)) return new Dictionary<string, List<string>>(StringComparer.Ordinal);
                var path = DatabaseManager.GetDbPath("reference");
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
                };
                using var conn = new SqliteConnection(builder.ToString());
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT casref_type, casref_value FROM casref_mstr
                    WHERE REPLACE(TRIM(casref_cas_no), ' ', '') = $cas
                    AND casref_type IN ('인체','사고','제한','중점','잔류','배출량')
                    ORDER BY casref_id";
                cmd.Parameters.AddWithValue("$cas", casNumber.Trim().Replace(" ", ""));
                var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string type = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    string value = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    if (!found.TryGetValue(type, out var entries)) found[type] = entries = new List<string>();
                    if (!entries.Contains(value)) entries.Add(value);
                }
            return found;
        }

        private void IngredientsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateActionButtons();
            LoadCasReferences();
        }

        private void SaveDisplayed()
        {
            if (_displayedItem == null || _isAnalyzing || _displayedItem.Parsed == null) return;
            IngredientsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
            IngredientsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
            try
            {
                _displayedItem.RegistrationKind = MsdsDbRegistration.GetRegistrationStatus(
                    _displayedItem.MaterialType, MsdsDbRegistration.GracoCode(_displayedItem.FileName),
                    RevisionNumberBox.Text, RevisionDateBox.Text);
                FilesGrid.Items.Refresh();
            }
            catch (Exception ex)
            {
                PublishStatus("등록 구분 확인 실패: " + ex.Message);
                return;
            }
            foreach (var field in CaptureFields()) _displayedItem.Unified.SaveBasic(field.Key, field.Value);
            _displayedItem.SavedLearning = new(_displayedItem.Pending);
            UpdateUnifiedIngredients(_displayedItem, CaptureIngredients());
            _displayedItem.Unified.Confirmed = true;
            _displayedItem.Dirty = false;
            SetStatus(_displayedItem, "확인완료");
            UpdateActionButtons();
            PublishStatus($"MSDS 검토 결과 저장 완료: {_displayedItem.FileName}");
        }
        private void SaveCurrent_Click(object sender, RoutedEventArgs e) => SaveDisplayed();

        private async void DbSave_Click(object sender, RoutedEventArgs e)
        {
            if (_isAnalyzing) return;
            // 현재 편집 중인 셀까지 반영한다.
            IngredientsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            IngredientsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            if (_displayedItem?.Dirty == true)
            {
                System.Windows.MessageBox.Show("현재 PDF의 수정사항을 먼저 [저장]해 주세요.", "DB 저장", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var targets = FilesGrid.SelectedItems.Cast<PdfItem>().Where(v => v.Unified.Confirmed).ToList();
            if (targets.Count == 0)
            {
                System.Windows.MessageBox.Show("선택한 PDF 중 DB에 저장할 확인완료 항목이 없습니다.", "DB 저장");
                return;
            }
            if (System.Windows.MessageBox.Show($"확인완료 {targets.Count}건을 DB에 저장하시겠습니까?", "DB 저장 확인",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                PublishStatus("MSDS DB 저장 취소");
                return;
            }
            DbSaveButton.IsEnabled = false;
            int success = 0, failed = 0, embeddingFailed = 0;
            try
            {
                foreach (var item in targets)
                {
                    var values = item.Unified.FinalBasics();
                    var ingredients = item.Unified.Ingredients.Select(v => new MsdsDbRegistration.Ingredient
                    {
                        Name = v.Name, CasNumber = v.CasNumber, ConcentrationRaw = v.Final(0),
                        Twa = v.Final(4), Stel = v.Final(5),
                        OralLd50 = v.Final(6), DermalLd50 = v.Final(7),
                        InhalationLc50 = v.Final(8), SortOrder = v.Order ?? 0,
                        Original = new MsdsDbRegistration.Ingredient { SortOrder = v.Order ?? 0, Name = v.Name, CasNumber = v.CasNumber,
                            ConcentrationRaw = v.Final(0), Twa = v.Final(4), Stel = v.Final(5),
                            OralLd50 = v.Final(6), DermalLd50 = v.Final(7), InhalationLc50 = v.Final(8) }
                    }).ToList();
                    var snapshot = new MsdsDbRegistration.Document
                    {
                        FilePath = item.FullPath, MaterialType = item.MaterialType, Values = values, OriginalValues = new Dictionary<string, string>(values, StringComparer.Ordinal), Ingredients = ingredients, Learning = item.SavedLearning.Values.ToList(), Applied = item.Applied.ToList()
                    };
                    var result = await Task.Run(() => MsdsDbRegistration.Save(snapshot));
                    if (result.Duplicate)
                    {
                        item.RegistrationKind = "중복";
                        SetStatus(item, "중복");
                        failed++;
                    }
                    else if (result.Success)
                    {
                        // Semantic vectors are generated after the MSDS transaction commits.
                        // An API failure must never roll back an already saved MSDS.
                        try
                        {
                            string embeddingKey = GetAiSetting("MSDS_API_KEY");
                            await MsdsOpenAiEmbeddingPersistence.SyncAsync(embeddingKey);
                        }
                        catch (Exception embeddingError)
                        {
                            embeddingFailed++;
                            PublishStatus("MSDS 저장 완료 / AI 의미 임베딩 미완료: " + embeddingError.Message);
                        }
                        if (ReferenceEquals(_displayedItem, item)) _displayedItem = null;
                        _files.Remove(item);
                        success++;
                    }
                    else
                    {
                        SetStatus(item, result.Stage == "PDF" ? "PDF복사실패" : "DB저장실패");
                        failed++;
                        System.Windows.MessageBox.Show($"{item.FileName}\n{result.Message}", "DB 저장 오류",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            finally
            {
                FilesGrid.Items.Refresh();
                UpdateCount();
                UpdateActionButtons();
            }
            PublishStatus($"MSDS DB 저장 결과: 성공 {success}건 / 실패 {failed}건" +
                (embeddingFailed > 0 ? $" / AI 의미 임베딩 실패 {embeddingFailed}건 (다음 저장 시 재시도)" : ""));
            System.Windows.MessageBox.Show($"DB 저장 완료: 성공 {success}건 / 실패 {failed}건", "DB 저장 결과");
        }

        private void AddIngredient_Click(object sender, RoutedEventArgs e)
        {
            if (_displayedItem?.Parsed == null || _isAnalyzing) return;
            IngredientsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            IngredientsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var rows = CaptureIngredients();
            rows.Add(new IngredientEdit { Index = rows.Count + 1 });
            IngredientsGrid.ItemsSource = rows;
            IngredientsGrid.SelectedItem = rows[^1];
            IngredientsGrid.ScrollIntoView(rows[^1]);
            MarkDirty();
            UpdateActionButtons();
        }

        private void RemoveIngredient_Click(object sender, RoutedEventArgs e)
        {
            if (_displayedItem?.Parsed == null || _isAnalyzing) return;
            if (IngredientsGrid.SelectedItem is not IngredientEdit selected) return;
            IngredientsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            IngredientsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var rows = CaptureIngredients();
            int index = IngredientsGrid.Items.OfType<IngredientEdit>().ToList().IndexOf(selected);
            if (index < 0) return;
            rows.RemoveAt(index);
            for (int i = 0; i < rows.Count; i++) rows[i].Index = i + 1;
            IngredientsGrid.ItemsSource = rows;
            MarkDirty();
            UpdateActionButtons();
        }

        private void EditableField_TextChanged(object sender, TextChangedEventArgs e) => MarkDirty();
        private void IngredientsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e) =>
            Dispatcher.BeginInvoke(new Action(MarkDirty), System.Windows.Threading.DispatcherPriority.Background);
        private void MarkDirty()
        {
            if (_loadingFields || _isAnalyzing || _displayedItem?.Parsed == null) return;
            _displayedItem.Dirty = true;
            if (_displayedItem.Unified.Confirmed)
            {
                _displayedItem.Unified.Confirmed = false;
                SetStatus(_displayedItem, _displayedItem.AnalysisDisplay);
            }
            UpdateActionButtons();
        }
        private bool ConfirmLeave()
        {
            if (_displayedItem?.Dirty != true)
            {
                // 분석 결과를 수정하지 않았다면 파일 이동 시 자동 확인완료 처리한다.
                if (_displayedItem?.Parsed != null && !_displayedItem.Unified.Confirmed && !_isAnalyzing)
                    SaveDisplayed();
                return true;
            }
            var result = System.Windows.MessageBox.Show(
                "현재 파일의 변경사항을 저장하시겠습니까?\n예: 저장 / 아니요: 저장 안 함 / 취소: 이동 취소",
                "변경사항 저장 확인", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel) return false;
            if (result == MessageBoxResult.Yes) SaveDisplayed();
            else
            {
                _displayedItem.Pending = new(_displayedItem.SavedLearning);
                _displayedItem.Dirty = false;
                _displayedItem.Unified.Confirmed = _displayedItem.Unified.Confirmed;
                SetStatus(_displayedItem, _displayedItem.Unified.Confirmed ? "확인완료" : _displayedItem.AnalysisDisplay);
            }
            return true;
        }
        private void ClearWork(PdfItem item)
        {
            item.Applied.Clear(); item.Pending.Clear(); item.SavedLearning.Clear();
            item.Unified.Clear();
            item.Parsed = null;
            item.AnalysisStarted = null; item.AnalysisFinished = null; item.AnalysisModel = "";
            item.Dirty = false;
            item.AnalysisStatus = "대기";
            item.AnalysisDisplay = "대기";
            item.Status = "대기";
        }
        // 분석 직후 AI 원본을 통합 테이블에 보관한다. 별도의 원본 사본은 만들지 않는다.
        private static void InitializeUnifiedMemory(PdfItem item, MsdsAnalysisResult result)
        {
            BuildUnifiedMemory(item, result);
        }
        private static Dictionary<string, string> BuildOriginalAiSnapshot(PdfItem item)
        {
            var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (ui, field, _) in UnifiedFields)
                if (item.Unified.Basic(ui) is { } row) snapshot[field] = row.ValueAi;
            foreach (var row in item.Unified.Ingredients)
            {
                if (string.IsNullOrWhiteSpace(row.CasNumber) ||
                    item.Unified.Ingredients.Count(v => v.CasNumber.Trim() == row.CasNumber.Trim()) != 1) continue;
                var original = new IngredientEdit
                {
                    Name = row.Name, CasNumber = row.CasNumber, ConcentrationRaw = row.Ai(0),
                    Twa = row.Ai(4), Stel = row.Ai(5), OralLd50 = row.Ai(6),
                    DermalLd50 = row.Ai(7), InhalationLc50 = row.Ai(8)
                };
                foreach (var property in MsdsIngredientLearningKey.Fields)
                    snapshot[MsdsIngredientLearningKey.Key(row.CasNumber, property)] =
                        MsdsIngredientLearningKey.Get(original, property) ?? "";
            }
            return snapshot;
        }
        private static List<IngredientEdit> UnifiedIngredientEdits(PdfItem item) =>
            item.Unified.Ingredients.Select(v => new IngredientEdit
            {
                Index = v.Order ?? 0, Name = v.Name, CasNumber = v.CasNumber,
                ConcentrationRaw = v.Final(0), Twa = v.Final(4),
                Stel = v.Final(5), OralLd50 = v.Final(6),
                DermalLd50 = v.Final(7), InhalationLc50 = v.Final(8)
            }).ToList();
        private static readonly (string Ui, string Field, string Name)[] UnifiedFields =
        {
            ("GracoCodeBox", "msds_sds_code", "Graco Code"),
            ("RevisionDateBox", "msds_revision_date", "개정일"),
            ("RevisionNumberBox", "msds_revision_number", "개정번호"),
            ("ProductNameBox", "msds_product_name", "원료(제품)명"),
            ("SupplierNameBox", "msds_supplier_name", "공급업체명"),
            ("PhoneBox", "msds_supplier_phone", "공급업체 전화"),
            ("AddressBox", "msds_supplier_address", "공급업체 주소"),
            ("MoelSubmissionBox", "msds_moel_submission_no", "노동부 제출번호"),
            ("HazardCodesBox", "msds_hazard_codes", "유해·위험 문구"),
            ("CorrosivityBox", "msds_corrosive_flag", "피부 부식성/자극성"),
            ("PersonalProtectionBox", "msds_ppe", "개인보호구"),
            ("ReactivityBox", "msds_reactivity", "반응성"),
            ("LelBox", "msds_lel", "폭발하한계 (LEL)"),
            ("UelBox", "msds_uel", "폭발상한계 (UEL)"),
            ("FlashPointBox", "msds_flash_point", "인화점"),
            ("AutoIgnitionBox", "msds_autoignition", "자연발화점"),
            ("VaporPressureBox", "msds_vapor_pressure", "증기압"),
            ("UnNumberBox", "msds_un_number", "UN 번호"),
            ("MolecularFormulaBox", "msds_molecular_formula", "분자식"),
            ("PhysicalStateBox", "msds_physical_state", "물리적 상태"),
            ("HazardousGoodsBox", "msds_hazardous_goods_class", "위험물 분류"),
            ("ToxicityBox", "msds_toxicity", "독성치")
        };

        private static void BuildUnifiedMemory(PdfItem item, MsdsAnalysisResult result)
        {
            item.Unified.Clear();
            var fields = result.Fields.GroupBy(f => f.FieldKey)
                .ToDictionary(g => g.Key, g => g.First().Value ?? "", StringComparer.Ordinal);
            foreach (var (ui, key, name) in UnifiedFields)
                item.Unified.Rows.Add(new MsdsUnifiedMemory.Row
                {
                    MsdsId = item.GracoCode, Category = "기본정보", FieldCode = ui,
                    FieldName = name, ValueAi = ui == "GracoCodeBox" ? item.GracoCode :
                        fields.TryGetValue(key, out var value) ? value : "",
                    SourcePage = result.Fields.FirstOrDefault(f => f.FieldKey == key)?.SourcePage
                });
            item.Unified.Rows.Add(new MsdsUnifiedMemory.Row
            {
                MsdsId = item.GracoCode, Category = "기본정보", FieldCode = "MaterialTypeDisplay",
                FieldName = "원료/제품 구분", ValueAi = item.MaterialType
            });
            item.Unified.Rows.Add(new MsdsUnifiedMemory.Row
            {
                MsdsId = item.GracoCode, Category = "기본정보", FieldCode = "FileNameBox",
                FieldName = "파일명", ValueAi = item.FileName
            });
            foreach (var (ingredient, index) in result.Ingredients.Select((v, i) => (v, i)))
            {
                var raw = ingredient.ConcentrationRaw ?? "";
                var amounts = MsdsDbRegistration.Amounts(raw);
                string Format(double? number) => number?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "";
                var values = new[] { raw, Format(amounts.Item1), Format(amounts.Item2), Format(amounts.Item3),
                    ingredient.Twa ?? "", ingredient.Stel ?? "", ingredient.OralLd50 ?? "",
                    ingredient.DermalLd50 ?? "", ingredient.InhalationLc50 ?? "" };
                for (int i = 0; i < MsdsUnifiedMemory.IngredientCodes.Length; i++)
                    item.Unified.Rows.Add(new MsdsUnifiedMemory.Row
                    {
                        MsdsId = item.GracoCode, Category = "구성성분", Order = index + 1,
                        CasNumber = ingredient.CasNumber ?? "", IngredientName = ingredient.Name ?? "",
                        FieldCode = MsdsUnifiedMemory.IngredientCodes[i],
                        FieldName = MsdsUnifiedMemory.IngredientNames[i], ValueAi = values[i],
                        SourcePage = ingredient.SourcePage
                    });
            }
        }

        private static void UpdateUnifiedIngredients(PdfItem item, IEnumerable<IngredientEdit> ingredients)
        {
            var saved = ingredients.ToList();
            var previous = item.Unified.Rows.Where(r => r.Category == "구성성분").ToList();
            var used = new HashSet<int>();
            var next = new List<MsdsUnifiedMemory.Row>();
            foreach (var v in saved)
            {
                // Match by original row order first, then CAS/name. Preserve AI originals.
                var match = previous.FirstOrDefault(r => !used.Contains(r.Order ?? 0) && r.Order == v.Index &&
                    r.CasNumber == v.CasNumber && r.IngredientName == v.Name)
                    ?? previous.FirstOrDefault(r => !used.Contains(r.Order ?? 0) &&
                        r.CasNumber == v.CasNumber && r.IngredientName == v.Name);
                var oldOrder = match?.Order ?? 0;
                if (match != null) used.Add(oldOrder);
                var values = new[] { v.ConcentrationRaw, v.Minimum, v.Maximum, v.Calculated,
                    v.Twa, v.Stel, v.OralLd50, v.DermalLd50, v.InhalationLc50 };
                for (int i = 0; i < values.Length; i++)
                {
                    var code = MsdsUnifiedMemory.IngredientCodes[i];
                    var row = match == null ? null : previous.FirstOrDefault(r => r.Order == oldOrder && r.FieldCode == code);
                    if (row == null)
                        row = new MsdsUnifiedMemory.Row { MsdsId = item.GracoCode, Category = "구성성분",
                            FieldCode = code, FieldName = MsdsUnifiedMemory.IngredientNames[i] };
                    row.Order = v.Index;
                    row.CasNumber = v.CasNumber;
                    row.IngredientName = v.Name;
                    var baseline = row.AiApplied && row.ValueCorrection != null ? row.ValueCorrection : row.ValueAi;
                    row.ValueMod = values[i] == baseline ? null : values[i];
                    next.Add(row);
                }
            }
            item.Unified.Rows.RemoveAll(r => r.Category == "구성성분");
            item.Unified.Rows.AddRange(next);
        }

        private void ApplySameDocumentLearning(PdfItem item, MsdsAnalysisResult result)
        {
            try { item.Applied=MsdsSameDocumentLearning.Apply(item.FullPath,result); var count=item.Applied.Count; LogList.Items.Insert(0,$"{DateTime.Now:HH:mm:ss}  {item.FileName}: 동일 PDF 학습 {count}필드 반영"); }
            catch(Exception ex) { LogList.Items.Insert(0,$"{DateTime.Now:HH:mm:ss}  {item.FileName}: 학습 조회 실패 (기존 분석 유지) - {ex.Message}"); }
        }
        private async Task ApplySimilarDocumentLearningAsync(PdfItem item, MsdsAnalysisResult result, CancellationToken ct)
        {
            try
            {
                var entries = await MsdsSimilarDocumentLearning.ApplyAsync(item.FullPath, item.MaterialType, result, item.Applied, ct, message => LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {item.FileName}: S03 {message}"));
                item.Applied.AddRange(entries);
                var ingredientEntries = await MsdsSimilarDocumentLearning.ApplyIngredientAsync(item.FullPath,
                    item.MaterialType, result, item.Applied, ct, message => LogList.Items.Insert(0,
                    $"{DateTime.Now:HH:mm:ss}  {item.FileName}: {message}"));
                item.Applied.AddRange(ingredientEntries);
                LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {item.FileName}: S01~S06 유사 PDF 학습 {entries.Count + ingredientEntries.Count}필드 반영 (검증 필요)");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {item.FileName}: S03 학습 실패 (기존 분석 유지) - {ex.Message}"); }
        }
        private async Task ApplySemanticCorrectionsAsync(PdfItem item, MsdsAnalysisResult result, string apiKey, CancellationToken ct)
        {
            var diagnostics = new List<string>();
            void Trace(string message)
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {message}";
                diagnostics.Add(line);
                LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {item.FileName}: {message}");
            }
            Trace($"AI 교정 진단 시작 | 파일={item.FileName} | 성분={result.Ingredients.Count}건");
            if (!CanApplyAiLearning())
            {
                Trace("교정 단계 건너뜀 | 사유=현재 사용자에게 Admin 권한 없음");
                SaveAiCorrectionDiagnostic(item, diagnostics);
                return;
            }
            try
            {
                var proposals = await MsdsSemanticCorrectionApplication.ProposeAsync(apiKey, result, ct,
                    message => Trace(message));
                foreach (var proposal in proposals)
                {
                    var row = item.Unified.IngredientField(proposal.Order, proposal.Property switch
                    {
                        "Twa" => "twa", "Stel" => "stel", "OralLd50" => "oral_ld50",
                        "DermalLd50" => "dermal_ld50", "InhalationLc50" => "inhalation_lc50", _ => ""
                    });
                    if (row == null || row.CasNumber != proposal.Cas || row.ValueAi.Trim() != proposal.Before)
                    {
                        Trace($"교정 반영 제외 CAS={proposal.Cas} 항목={proposal.Property}: 결과 행 매핑 불일치");
                        continue;
                    }
                    Trace($"교정 반영 성공 CAS={proposal.Cas} 항목={proposal.Property} | 학습번호={proposal.CorrectionId} | 유사도={proposal.Similarity:F3} | 이전값={proposal.Before} | 교정값={proposal.After}");
                    row.ValueCorrection = proposal.After;
                    row.AiApplied = true;
                    row.LearningNo = proposal.CorrectionId;
                    item.Applied.Add(new MsdsLearningEntry
                    {
                        Id = proposal.CorrectionId, Key = MsdsIngredientLearningKey.Key(proposal.Cas, proposal.Property),
                        Before = proposal.Before, After = proposal.After,
                        Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(item.FullPath))),
                        At = DateTimeOffset.Now.ToString("O"), Type = "SIMILAR", Subtype = "SEMANTIC"
                    });
                }
                Trace($"OpenAI 의미 검색 교정 {item.Applied.Count(x => x.Subtype == "SEMANTIC")}건 (저장 전 검증 필요)");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Trace($"의미 검색 교정 미적용 | {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Trace("AI 교정 진단 종료");
                SaveAiCorrectionDiagnostic(item, diagnostics);
            }
        }

        private void SaveAiCorrectionDiagnostic(PdfItem item, List<string> diagnostics)
        {
            try
            {
                var outputFolder = System.IO.Path.Combine(AppContext.BaseDirectory, "Output");
                Directory.CreateDirectory(outputFolder);
                var path = System.IO.Path.Combine(outputFolder,
                    $"QCS_AI_Correction_Diagnostic_{DateTime.Now:yyyyMMdd_HHmmss_fffffff}.txt");
                File.WriteAllLines(path, diagnostics, new System.Text.UTF8Encoding(false));
                PublishStatus("AI 교정 진단 로그 저장: " + System.IO.Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {item.FileName}: AI 교정 진단 로그 저장 실패 - {ex.Message}");
            }
        }

        private void SetAnalysisResult(PdfItem item, MsdsAnalysisResult result)
        {
            item.Parsed = result;
            item.AnalysisFinished = DateTime.Now;
            item.AnalysisStatus = "분석완료";
            // 신뢰도 산식은 추후 검증: 현재는 실제 계산하지 않은 수치를 표시하지 않는다.
            item.AnalysisDisplay = "분석완료";
            item.Status = item.AnalysisDisplay;
        }

        private readonly ObservableCollection<PdfItem> _files = new();
        private readonly MsdsPdfTextExtractor _textExtractor = new();
        private readonly MsdsLocalFieldParser _fieldParser = new();
        private bool _isAnalyzing;
        private CancellationTokenSource? _analysisCts;

        private sealed class AiModelOption
        {
            public string Value { get; set; } = "";
            public string Description { get; set; } = "";
        }

        private void LoadAiModels()
        {
            using var conn = new SqliteConnection(
                DatabaseManager.GetConnectionString("common"));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT zx_code_value, zx_code_desc1 FROM zx_code_mstr
                                WHERE zx_code_fldname = 'API_model'
                                  AND COALESCE(zx_code_value, '') <> ''
                                ORDER BY zx_code_value";
            var options = new List<AiModelOption>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var value = reader.IsDBNull(0) ? "" : reader.GetString(0).Trim();
                    if (value.Length == 0) continue;
                    var desc = reader.IsDBNull(1) ? value : reader.GetString(1).Trim();
                    options.Add(new AiModelOption { Value = value, Description = desc.Length == 0 ? value : desc });
                }
            }
            if (options.Count == 0)
                throw new InvalidOperationException("API_model 공통코드가 등록되어 있지 않습니다.");
            AiModelComboBox.ItemsSource = options;
            var defaultModel = GetAiSetting("MSDS_AI_Model", required: false);
            if (string.IsNullOrWhiteSpace(defaultModel)) defaultModel = "gpt-4.1-mini";
            AiModelComboBox.SelectedValue = options.Any(o => o.Value == defaultModel)
                ? defaultModel : options[0].Value;
        }

        private string GetSelectedAiModel()
        {
            if (AiModelComboBox.SelectedValue is string value && !string.IsNullOrWhiteSpace(value))
                return value;
            throw new InvalidOperationException("AI 모델을 선택해 주세요.");
        }

        // 선택된 콤보박스에만 분석 방식과 모델명을 함께 표시한다.
        // 드롭다운 목록과 SelectedValue(실제 분석에 전달하는 모델 값)는 변경하지 않는다.
        private void UpdateAnalysisModeDisplay(string? rule = null)
        {
            if (AnalysisModeDisplay == null) return;
            try
            {
                rule ??= GetMsdsAnalysisRule();
                string mode = rule switch
                {
                    "1" => "AI Only",
                    "2" => "C# Only",
                    "3" => "C# + AI",
                    _ => "설정 확인"
                };
                string model = rule == "2" ? "AI 미사용"
                    : (AiModelComboBox.SelectedItem as AiModelOption)?.Description ?? "모델 미선택";
                AnalysisModeDisplay.Text = $"{mode} | {model}";
                AnalysisModeDisplay.ToolTip = rule switch
                {
                    "1" => "PDF 원본을 AI에 전달하여 분석",
                    "2" => "C# 텍스트 추출 및 JSON 규칙으로 분석",
                    "3" => "C#으로 추출한 텍스트를 AI로 분석",
                    _ => "분석 방식 설정을 확인해 주세요."
                };
            }
            catch
            {
                AnalysisModeDisplay.Text = "분석 방식 확인 필요";
                AnalysisModeDisplay.ToolTip = "MSDS_Analysis_rule 설정을 확인해 주세요.";
            }
        }

        private void RefreshAiModelAvailability()
        {
            try
            {
                string rule = GetMsdsAnalysisRule();
                AiModelComboBox.IsEnabled = !_isAnalyzing && rule != "2";
                UpdateAnalysisModeDisplay(rule);
            }
            catch
            {
                AiModelComboBox.IsEnabled = false;
                UpdateAnalysisModeDisplay();
            }
        }

        public MsdsRegistrationView()
        {
            InitializeComponent();
            OpenLogButton.IsEnabled = LogList.Items.Count > 0;
            ((System.Collections.Specialized.INotifyCollectionChanged)LogList.Items).CollectionChanged += (_,__) => OpenLogButton.IsEnabled = LogList.Items.Count > 0;

            FilesGrid.ItemsSource = _files;
            AiModelComboBox.SelectionChanged += (_, _) => UpdateAnalysisModeDisplay();
            try { LoadAiModels(); }
            catch (Exception ex)
            {
                AiModelComboBox.IsEnabled = false;
                LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  AI 모델 목록 조회 오류: {ex.Message}");
            }
            UpdateCount();
            UpdateActionButtons();
            SizeChanged += (_, _) => { UpdateAdaptiveScale(); UpdateResponsiveLayout(); };
            Loaded += (_, _) => { UpdateAdaptiveScale(); UpdateResponsiveLayout(); UpdateMaterialTypeDisplay(); UpdateLogHeight(); UpdateNavigation(); RefreshAiModelAvailability(); };
            SizeChanged += (_, _) => UpdateLogHeight();
        }

        // Session-only panel visibility and RM/FG choice. No automatic restoration.
        private bool _restoringMaterialType;
        private void MaterialType_Checked(object sender, RoutedEventArgs e)
        {
            if (_restoringMaterialType || MaterialTypeRaw == null || MaterialTypeProduct == null) return;
            if (_displayedItem == null) { UpdateMaterialTypeDisplay(); return; }
            var selected = MaterialTypeRaw.IsChecked == true ? "RM" : "FG";
            if (_displayedItem.MaterialType == selected) { UpdateMaterialTypeDisplay(); return; }
            _displayedItem.MaterialType = selected;
            UpdateMaterialTypeDisplay();
            _displayedItem.Unified.Confirmed = false; // Reconfirm before DB save after classification change.
            SetStatus(_displayedItem, "구분변경");
            FilesGrid.Items.Refresh();
            UpdateActionButtons();
        }
        private void UpdateMaterialTypeDisplay()
        {
            if (MaterialTypeDisplay == null || MaterialTypeRaw == null) return;
            string type = _displayedItem?.MaterialType ?? (MaterialTypeRaw.IsChecked == true ? "RM" : "FG");
            MaterialTypeDisplay.Text = type == "RM" ? "원료(RM)" : "제품(FG)";
        }
        private bool? _filesOverride;
        private bool? _previewOverride;

        private void ToggleFiles_Click(object sender, RoutedEventArgs e)
        {
            _filesOverride = FilesPanel.Visibility != Visibility.Visible;
            UpdateResponsiveLayout();
            PublishStatus(FilesPanel.Visibility == Visibility.Visible ? "PDF 등록 영역 펼침" : "PDF 등록 영역 접음");
        }

        private void TogglePreview_Click(object sender, RoutedEventArgs e)
        {
            _previewOverride = PreviewPanel.Visibility != Visibility.Visible;
            UpdateResponsiveLayout();
            PublishStatus(PreviewPanel.Visibility == Visibility.Visible ? "PDF 원문 표시" : "PDF 원문 감춤");
        }

        private bool _logExpanded = true;
        private bool _layoutUpdating;
        private int _lastLayoutMode = -1;
        private bool _lastFilesVisible;
        private bool _lastPreviewVisible;

        private void OpenLog_Click(object sender, RoutedEventArgs e)
        {
            if (LogList.Items.Count == 0) { PublishStatus("열 수 있는 분석 로그가 없습니다."); return; }
            try
            {
                var folder = System.IO.Path.Combine(AppContext.BaseDirectory, "Output");
                Directory.CreateDirectory(folder);
                var path = System.IO.Path.Combine(folder, "QCS_MSDS_Log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fffffff") + ".txt");
                var text = string.Join(Environment.NewLine, LogList.Items.Cast<object>().Select(x=>x?.ToString() ?? ""));
                try { var secret = GetAiSetting("MSDS_API_KEY"); if (!string.IsNullOrEmpty(secret)) text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal); } catch { /* No configured key available. */ }
                System.IO.File.WriteAllText(path, text, new System.Text.UTF8Encoding(true));
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
                PublishStatus("분석 로그 저장 및 열기 완료: " + System.IO.Path.GetFileName(path));
            }
            catch (Exception ex) { PublishStatus("분석 로그 열기 실패: " + ex.Message); }
        }

        private void ToggleLog_Click(object sender, RoutedEventArgs e)
        {
            _logExpanded = !_logExpanded;
            UpdateLogHeight();
            LogList.Visibility = _logExpanded ? Visibility.Visible : Visibility.Collapsed;
            ToggleLogButton.Content = _logExpanded ? "로그 접기 ▲" : "로그 펼치기 ▼";
            PublishStatus(_logExpanded ? "분석 로그 펼침" : "분석 로그 접음");
        }

        private void UpdateLogHeight()
        {
            if (LogRow == null || FilesPanel == null) return;
            // 목록 최소 높이를 확보하고 나머지 가용 공간 안에서 로그를 확장한다.
            double available = Math.Max(0, FilesPanel.ActualHeight - 11 * 2 - 43 - 36 - 32 - 38 - 34 - 35 - 110);
            LogRow.Height = new GridLength(_logExpanded ? Math.Min(270, available) : 0);
        }

        private void UpdateNavigation()
        {
            if (FilesGrid == null || FirstFileButton == null) return;
            int i = FilesGrid.SelectedItem is PdfItem item ? _files.IndexOf(item) : -1;
            CurrentFileText.Text = i < 0 ? "-----" : MsdsDbRegistration.GracoCode(_files[i].FileName);
            CurrentFileText.ToolTip = i < 0 ? "선택 파일 없음" : _files[i].FileName;
            FirstFileButton.IsEnabled = PreviousFileButton.IsEnabled = i > 0;
            NextFileButton.IsEnabled = LastFileButton.IsEnabled = i >= 0 && i < _files.Count - 1;
        }
        private void SelectFile(int index)
        {
            if (index < 0 || index >= _files.Count) return;
            FilesGrid.SelectedItem = _files[index];
            FilesGrid.ScrollIntoView(_files[index]);
            UpdateNavigation();
        }
        private void FirstFile_Click(object sender, RoutedEventArgs e) => SelectFile(0);
        private void PreviousFile_Click(object sender, RoutedEventArgs e) => SelectFile(FilesGrid.SelectedIndex - 1);
        private void NextFile_Click(object sender, RoutedEventArgs e) => SelectFile(FilesGrid.SelectedIndex + 1);
        private void LastFile_Click(object sender, RoutedEventArgs e) => SelectFile(_files.Count - 1);
        private void FileSearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            string term = FileSearchBox.Text.Trim();
            if (term.Length == 0 || _files.Count == 0) return;
            int start = FilesGrid.SelectedIndex;
            for (int offset = 1; offset <= _files.Count; offset++)
            {
                int index = (start + offset + _files.Count) % _files.Count;
                if (_files[index].FileName.Contains(term, StringComparison.OrdinalIgnoreCase))
                { SelectFile(index); FileSearchBox.Dispatcher.BeginInvoke(new Action(() => FileSearchBox.Focus()), System.Windows.Threading.DispatcherPriority.Input); return; }
            }
        }
        private bool CanApplyAiLearning()
        {
            var user = (Window.GetWindow(this) as MainWindow)?.GetCurrentUser() ?? StartupUserService.FindCurrentUser();
            return user?.UserLevel == QcsAuthorizationService.LevelAdmin;
        }

        private bool CanUseAiCorrection()
        {
            var user = (Window.GetWindow(this) as MainWindow)?.GetCurrentUser() ?? StartupUserService.FindCurrentUser();
            return user?.UserLevel == QcsAuthorizationService.LevelAdmin
                && QcsAuthorizationService.CanAccessProgram(user, "MsdsRegistrationView.xaml.cs")
                && QcsAuthorizationService.CanAccessProgram(user, QcsAuthorizationService.AiCorrectionProgramId);
        }

        private void SetAnalysisUi(bool analyzing)
        {
            _isAnalyzing = analyzing;
            FilesGrid.IsEnabled = true;
            FileSearchBox.IsEnabled = true;
            // 화면 내 데이터 수정 및 파일 추가/삭제를 모두 잠근다. 로그와 진행률은 유지한다.
            StartAnalysisButton.Visibility = analyzing ? Visibility.Collapsed : Visibility.Visible;
            StopAnalysisButton.Visibility = analyzing ? Visibility.Visible : Visibility.Collapsed;
            StopAnalysisButton.IsHitTestVisible = true;
            foreach (var button in FindVisualChildren<System.Windows.Controls.Button>(FilesPanel))
                if (!ReferenceEquals(button, StopAnalysisButton) && !ReferenceEquals(button, ToggleLogButton))
                    button.IsEnabled = !analyzing;
            BottomClearButton.IsEnabled = !analyzing;
            RailAddButton.IsEnabled = !analyzing;
            MaterialTypeProduct.IsEnabled = !analyzing;
            MaterialTypeRaw.IsEnabled = !analyzing;
            foreach (var check in FindVisualChildren<System.Windows.Controls.CheckBox>(this)) check.IsEnabled = !analyzing;
            foreach (var box in FindVisualChildren<System.Windows.Controls.TextBox>(this))
                if (!ReferenceEquals(box, FileSearchBox) && !ReferenceEquals(box, FileNameBox) && !ReferenceEquals(box, GracoCodeBox))
                    box.IsReadOnly = analyzing;
            SaveCurrentButton.IsEnabled = !analyzing && _displayedItem?.Parsed != null;
            AiCorrectionButton.IsEnabled = !analyzing && _displayedItem?.Parsed != null && CanUseAiCorrection();
            RefreshAiModelAvailability();
            UpdateCount();
            UpdateNavigation();
        }
        private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T found) yield return found;
                foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
            }
        }
        private void StopAnalysis_Click(object sender, RoutedEventArgs e)
        {
            StopAnalysisButton.IsEnabled = false;
            _analysisCts?.Cancel();
            LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  분석 중지 요청");
            PublishStatus("MSDS 분석 중지 요청");
        }
        // Scale the MSDS registration workspace, not the user-selected panel visibility.
        // Keep a readable minimum; scrollbars remain available for narrow layouts.
        private void UpdateAdaptiveScale()
        {
            if (RegistrationRoot == null || ActualWidth <= 0 || ActualHeight <= 0) return;
            double scale = Math.Min(1.0, Math.Min(ActualWidth / 1550.0, ActualHeight / 850.0));
            scale = Math.Max(0.70, scale);
            if (Math.Abs(_currentUiScale - scale) < 0.015) return;
            _currentUiScale = scale;
            RegistrationRoot.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
        }
        private double _currentUiScale = 1.0;

        private void UpdateResponsiveLayout()
        {
            if (_layoutUpdating || WorkspaceGrid == null || FilesPanel == null ||
                FilesRail == null || PreviewPanel == null) return;

            // ActualWidth는 레이아웃 결과에 따라 달라질 수 있으므로
            // 최상위 컨테이너의 가용 폭을 기준으로 모드를 결정한다.
            double width = ActualWidth - 16;
            if (width <= 0) return;
            int mode = width >= 1450 ? 2 : width >= 900 ? 1 : 0;
            bool showFiles = _filesOverride ?? true;
            bool showPreview = _previewOverride ?? true;

            // 창 크기 변경 이벤트 중 반복해서 Star/Pixel 폭을 덮어쓰지 않는다.
            // 패널 표시 상태 또는 화면 구간이 바뀔 때만 재배치한다.
            if (_lastLayoutMode == mode && _lastFilesVisible == showFiles &&
                _lastPreviewVisible == showPreview) return;

            _layoutUpdating = true;
            try
            {
                _lastLayoutMode = mode;
                _lastFilesVisible = showFiles;
                _lastPreviewVisible = showPreview;
                FilesPanel.Visibility = showFiles ? Visibility.Visible : Visibility.Collapsed;
                FilesRail.Visibility = showFiles ? Visibility.Collapsed : Visibility.Visible;
                LeftSplitter.Visibility = showFiles ? Visibility.Visible : Visibility.Collapsed;
                PreviewPanel.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
                RightSplitter.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;

                var columns = WorkspaceGrid.ColumnDefinitions;
                // PDF 등록 영역은 고정 상한 폭을 사용하여 최대화 시 화면을 독점하지 않는다.
                columns[0].Width = new GridLength(showFiles ? (mode == 2 ? 340 : 300) : 44);
                columns[1].Width = new GridLength(showFiles ? 6 : 5);
                columns[3].Width = new GridLength(showPreview ? 6 : 0);
                columns[2].Width = new GridLength(showPreview ? (mode == 2 ? 42 : 60) : 1, GridUnitType.Star);
                columns[4].Width = showPreview
                    ? new GridLength(mode == 2 ? 32 : 40, GridUnitType.Star)
                    : new GridLength(0);

                ToggleFilesButton.Content = showFiles ? "◀  PDF 등록 접기" : "☰  PDF 등록 펼치기";
                TogglePreviewButton.Content = showPreview ? "▣  원문 감추기" : "▣  원문 보기";
            }
            finally { _layoutUpdating = false; }
        }

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "MSDS PDF 파일 선택",
                Filter = "PDF 파일 (*.pdf)|*.pdf",
                Multiselect = true
            };
            if (dialog.ShowDialog() == true) AddPaths(dialog.FileNames);
        }

        private void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "MSDS PDF 폴더 선택" };
            if (dialog.ShowDialog() != true) return;
            try { AddPaths(Directory.EnumerateFiles(dialog.FolderName, "*", SearchOption.TopDirectoryOnly)
                .Where(p => string.Equals(Path.GetExtension(p), ".pdf", StringComparison.OrdinalIgnoreCase))); }
            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "폴더 읽기 오류"); }
        }

        private void AddPaths(System.Collections.Generic.IEnumerable<string> paths)
        {
            var materialType = MaterialTypeRaw.IsChecked == true ? "RM" : "FG";
            int added = 0, skipped = 0;
            foreach (var path in paths)
            {
                if (!File.Exists(path)) continue;
                var fullPath = Path.GetFullPath(path);
                if (_files.Any(x => string.Equals(x.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    continue;
                }
                string name = Path.GetFileName(path);
                string gracoCode = MsdsDbRegistration.GracoCode(path);
                string kind;
                try { kind = MsdsDbRegistration.GetRegistrationStatus(materialType, gracoCode); }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show("MSDS 등록 상태 조회에 실패했습니다.\n" + ex.Message,
                        "DB 조회 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _files.Add(new PdfItem { Index = _files.Count + 1, FileName = name,
                    FullPath = fullPath, MaterialType = materialType, RegistrationKind = kind });
                added++;
            }
            FilesGrid.Items.Refresh();
            LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  PDF {added}건 추가, 목록 중복 {skipped}건 제외");
            UpdateCount();
            PublishStatus($"PDF 파일 {added}건 추가, 목록 중복 {skipped}건 제외");
            if (FilesGrid.SelectedItem == null && _files.Count > 0) FilesGrid.SelectedIndex = 0;
        }

        private async void FilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_revertingSelection) return;
            var incoming = FilesGrid.SelectedItem as PdfItem;
            if (!ReferenceEquals(_displayedItem, incoming) && !ConfirmLeave())
            {
                _revertingSelection = true;
                FilesGrid.SelectedItem = _displayedItem;
                _revertingSelection = false;
                UpdateNavigation();
                return;
            }
            UpdateNavigation();
            UpdateActionButtons();
            if (incoming is not PdfItem item) { _displayedItem = null; ResetPreview(); return; }
            _displayedItem = item;
            _restoringMaterialType = true;
            try
            {
                MaterialTypeRaw.IsChecked = item.MaterialType == "RM";
                MaterialTypeProduct.IsChecked = item.MaterialType != "RM";
            }
            finally { _restoringMaterialType = false; }
            UpdateMaterialTypeDisplay();
            UpdateActionButtons();
            FileNameBox.Text = item.FileName;
            ShowExtractedFields(item);
            try
            {
                // WebView2는 비동기로 초기화된다. 초기화 완료 후 선택된 PDF를 연다.
                await PdfBrowser.EnsureCoreWebView2Async();
                // 초기화 도중 사용자가 다른 파일을 선택하거나 목록을 비울 수 있다.
                if (!ReferenceEquals(FilesGrid.SelectedItem, item)) return;
                PdfBrowser.CoreWebView2.Navigate(new Uri(Path.GetFullPath(item.FullPath)).AbsoluteUri);
            }
            catch (Exception ex)
            {
                LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  미리보기 실패: {ex.Message}");
            }
        }

        private void RemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_isAnalyzing) { PublishStatus("분석 중에는 파일을 제거할 수 없습니다."); return; }
            var selected = FilesGrid.SelectedItems.Cast<PdfItem>().ToList();
            if (selected.Count == 0) { PublishStatus("제거할 PDF를 선택해 주세요."); return; }
            if (selected.Any(item => item.Status != "대기") && System.Windows.MessageBox.Show(
                $"선택한 {selected.Count}건의 PDF와 작업 내용을 목록에서 제거하시겠습니까?", "파일 제거 확인",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _displayedItem = null;
            foreach (var item in selected) _files.Remove(item);
            for (int i = 0; i < _files.Count; i++) _files[i].Index = i + 1;
            FilesGrid.Items.Refresh();
            UpdateCount();
            if (_files.Count == 0) ResetPreview();
            else FilesGrid.SelectedIndex = Math.Min(selected[0].Index - 1, _files.Count - 1);
            PublishStatus($"PDF {selected.Count}건을 목록에서 제거했습니다.");
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (_files.Any(f => f.Status != "대기") && System.Windows.MessageBox.Show(
                "목록과 지금까지의 작업 내용을 모두 삭제하시겠습니까?", "목록 초기화",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _displayedItem = null;
            _files.Clear();
            UpdateCount();
            ResetPreview();
            LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  목록 초기화");
            PublishStatus("PDF 목록 초기화 완료");
        }

        private void ResetPreview()
        {
            _loadingFields = true;
            FileNameBox.Clear();
            ProductNameBox.Clear();
            SupplierNameBox.Clear();
            IngredientsGrid.ItemsSource = null;
            ClearCasReferences();
            ClearAdditionalFields();
            _loadingFields = false;
            SaveCurrentButton.IsEnabled = false;
            AiCorrectionButton.IsEnabled = false;
            UpdateActionButtons();
            if (PdfBrowser.CoreWebView2 != null)
                PdfBrowser.CoreWebView2.Navigate("about:blank");
        }

        private static string GetMsdsAnalysisRule()
        {
            using var conn = new SqliteConnection(
                DatabaseManager.GetConnectionString("common"));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT zx_code_cmmt
                                FROM zx_code_mstr
                                WHERE zx_code_fldname = @fieldName
                                  AND COALESCE(zx_code_value, '') = ''";
            cmd.Parameters.AddWithValue("@fieldName", "MSDS_Analysis_rule");
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("MSDS_Analysis_rule 설정 레코드가 없습니다.");
            string rule = reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString()?.Trim() ?? "";
            if (reader.Read())
                throw new InvalidOperationException("MSDS_Analysis_rule 설정 레코드가 중복되었습니다.");
            if (rule != "1" && rule != "2" && rule != "3")
                throw new InvalidOperationException($"Comment 값이 올바르지 않습니다: '{rule}' (1, 2, 3만 허용)");
            return rule;
        }

        // 공통코드의 Comment 컬럼에서 AI 설정값을 조회한다.
        private static string GetAiSetting(string fieldName, bool required = true)
        {
            using var conn = new SqliteConnection(
                DatabaseManager.GetConnectionString("common"));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT zx_code_cmmt FROM zx_code_mstr
                                WHERE zx_code_fldname = @fieldName
                                  AND COALESCE(zx_code_value, '') = ''";
            cmd.Parameters.AddWithValue("@fieldName", fieldName);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                if (!required) return "";
                throw new InvalidOperationException(fieldName + " 설정 레코드가 없습니다.");
            }
            string value = reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString()?.Trim() ?? "";
            if (reader.Read()) throw new InvalidOperationException(fieldName + " 설정이 중복되었습니다.");
            if (required && string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(fieldName + "의 Comment 값이 비어 있습니다.");
            return value;
        }

        private bool PrepareNewAnalysis()
        {
            if (_files.Any(f => f.Status != "대기") && System.Windows.MessageBox.Show(
                "완료되지 않은 항목이 존재합니다.\n지금까지의 작업을 무시하고 처음부터 다시 시도하시겠습니까?",
                "재분석 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return false;
            foreach (var item in _files) ClearWork(item);
            if (FilesGrid.SelectedItem is PdfItem selected) ShowExtractedFields(selected);
            FilesGrid.Items.Refresh();
            return true;
        }
        private void FinishCancelledAnalysis()
        {
            if (_analysisCts?.IsCancellationRequested != true) return;
            foreach (var item in _files)
            {
                item.AnalysisStatus = "중지됨";
                item.Status = "중지됨";
            }
            FilesGrid.Items.Refresh();
        }

        // 등록된 PDF 전체에 대해 로컬 텍스트 추출 테스트를 실행한다.
        // 이 단계에서는 AI 분석, 필드 추출, DB 저장을 수행하지 않는다.
        private async void StartAnalysis_Click(object sender, RoutedEventArgs e)
        {
            if (_isAnalyzing || _files.Count == 0) return;
            PublishStatus($"MSDS 분석 시작: {_files.Count}건");
            // DB 설정에 따라 분석 모듈을 선택한다.
            string analysisRule;
            try { analysisRule = GetMsdsAnalysisRule(); }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("MSDS 분석 설정 조회 오류: " + ex.Message, "분석 중단");
                return;
            }

            RefreshAiModelAvailability();
            // 1: AI Only - PDF 원본을 AI에 전달하고 기존 검토 화면에 표시한다.
            if (analysisRule == "1")
            {
                string apiKey, model;
                try
                {
                    apiKey = GetAiSetting("MSDS_API_KEY");
                    model = GetSelectedAiModel();
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show("AI 설정 조회 오류: " + ex.Message, "분석 중단");
                    return;
                }

                if (!PrepareNewAnalysis()) return;
                _analysisCts = new CancellationTokenSource();
                SetAnalysisUi(true);
                var aiTargets = _files.ToArray();
                int aiProcessed = 0;
                AnalysisProgressBar.Value = 0;
                AnalysisProgressText.Text = $"0 / {aiTargets.Length} (0%)";
                LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  AI PDF 분석 시작 ({aiTargets.Length}건, {model})");
                try
                {
                    var analyzer = new MsdsAiPdfAnalyzer(apiKey, model);
                    foreach (var item in aiTargets)
                    {
                        if (_analysisCts!.IsCancellationRequested) break;
                        item.Status = "분석 중";
                        item.AnalysisStarted = DateTime.Now; item.AnalysisModel = model;
                        FilesGrid.Items.Refresh();
                        try
                        {
                            var aiResult = await analyzer.AnalyzeAsync(item.FullPath, _analysisCts!.Token);
                            InitializeUnifiedMemory(item, aiResult);
                            if (EnableAiLearning && CanApplyAiLearning())
                            {
                                ApplySameDocumentLearning(item, aiResult);
                                await ApplySimilarDocumentLearningAsync(item, aiResult, _analysisCts!.Token);
                            }
                            await ApplySemanticCorrectionsAsync(item, aiResult, apiKey, _analysisCts!.Token);
                            MsdsAiResultFiles.Save(item.FullPath, aiResult);
                            SetAnalysisResult(item, aiResult);
                            if (ReferenceEquals(FilesGrid.SelectedItem, item)) ShowExtractedFields(item);
                            LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {item.FileName}: AI 추출 {aiResult.Fields.Count}필드, 성분 {aiResult.Ingredients.Count}건 (검증 필요)");
                        }
                        catch (OperationCanceledException) when (_analysisCts?.IsCancellationRequested == true)
                        {
                            item.Status = "중지됨";
                            break;
                        }
                        catch (Exception ex)
                        {
                            item.Status = "분석완료";
                            LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {item.FileName}: AI 분석 오류 - {ex.Message}");
                        }
                        aiProcessed++;
                        int percent = (int)Math.Round(aiProcessed * 100.0 / aiTargets.Length);
                        AnalysisProgressBar.Value = percent;
                        AnalysisProgressText.Text = $"{aiProcessed} / {aiTargets.Length} ({percent}%)";
                        FilesGrid.Items.Refresh();
                    }
                    LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  AI PDF 분석 종료");
                    PublishStatus(_analysisCts?.IsCancellationRequested == true ? "MSDS 분석 중지됨" : "MSDS 분석 처리 종료");
                }
                finally
                {
                    FinishCancelledAnalysis();
                    SetAnalysisUi(false);
                    _analysisCts?.Dispose();
                    _analysisCts = null;
                    StopAnalysisButton.IsEnabled = true;
                }
                return;
            }

            // 3: C#에서 PDF 전체 텍스트를 추출한 뒤 AI에 텍스트만 전달한다.
            if (analysisRule == "3")
            {
                string apiKey, model;
                try
                {
                    apiKey = GetAiSetting("MSDS_API_KEY");
                    model = GetSelectedAiModel();
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show("AI 설정 조회 오류: " + ex.Message, "분석 중단");
                    return;
                }

                if (!PrepareNewAnalysis()) return;
                _analysisCts = new CancellationTokenSource();
                SetAnalysisUi(true);
                var targetsHybrid = _files.ToArray();
                int processedHybrid = 0;
                AnalysisProgressBar.Value = 0;
                AnalysisProgressText.Text = $"0 / {targetsHybrid.Length} (0%)";
                LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  C# + AI 전체 텍스트 분석 시작 ({targetsHybrid.Length}건, {model})");
                try
                {
                    var analyzer = new MsdsAiPdfAnalyzer(apiKey, model);
                    foreach (var item in targetsHybrid)
                    {
                        if (_analysisCts!.IsCancellationRequested) break;
                        item.Status = "분석 중";
                        item.AnalysisStarted = DateTime.Now; item.AnalysisModel = model;
                        FilesGrid.Items.Refresh();
                        try
                        {
                            var extracted = await _textExtractor.ExtractAsync(item.FullPath, _analysisCts.Token);
                            if (!extracted.HasExtractableText)
                                throw new InvalidOperationException("PDF에 추출 가능한 텍스트가 없습니다. (OCR 미지원)");
                            LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {item.FileName}: {extracted.PageCount}페이지, 텍스트 {extracted.FullText.Length:N0}자 → AI 전달");
                            var aiResult = await analyzer.AnalyzeTextAsync(item.FullPath, extracted.FullText, _analysisCts.Token);
                            InitializeUnifiedMemory(item, aiResult);
                            if (EnableAiLearning && CanApplyAiLearning())
                            {
                                ApplySameDocumentLearning(item, aiResult);
                                await ApplySimilarDocumentLearningAsync(item, aiResult, _analysisCts!.Token);
                            }
                            await ApplySemanticCorrectionsAsync(item, aiResult, apiKey, _analysisCts!.Token);
                            MsdsAiResultFiles.Save(item.FullPath, aiResult);
                            SetAnalysisResult(item, aiResult);
                            if (ReferenceEquals(FilesGrid.SelectedItem, item)) ShowExtractedFields(item);
                            LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {item.FileName}: 하이브리드 추출 {aiResult.Fields.Count}필드, 성분 {aiResult.Ingredients.Count}건 (검증 필요)");
                        }
                        catch (OperationCanceledException) when (_analysisCts?.IsCancellationRequested == true)
                        {
                            item.Status = "중지됨";
                            break;
                        }
                        catch (Exception ex)
                        {
                            item.Status = "분석오류";
                            LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {item.FileName}: 하이브리드 분석 오류 - {ex.Message}");
                        }
                        processedHybrid++;
                        int percent = (int)Math.Round(processedHybrid * 100.0 / targetsHybrid.Length);
                        AnalysisProgressBar.Value = percent;
                        AnalysisProgressText.Text = $"{processedHybrid} / {targetsHybrid.Length} ({percent}%)";
                        FilesGrid.Items.Refresh();
                    }
                    LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  C# + AI 전체 텍스트 분석 종료");
                    PublishStatus(_analysisCts?.IsCancellationRequested == true ? "MSDS 분석 중지됨" : "MSDS 분석 처리 종료");
                }
                finally
                {
                    FinishCancelledAnalysis();
                    SetAnalysisUi(false);
                    _analysisCts?.Dispose();
                    _analysisCts = null;
                    StopAnalysisButton.IsEnabled = true;
                }
                return;
            }

            // 2: 기존 C# 분석 경로를 변경하지 않는다.
            // JSON 오류 시 기존 분석 결과를 변경하지 않는다.
            MsdsRuleSet rules;
            try { rules = MsdsRuleLoader.Load(); }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("파싱 규칙 오류: " + ex.Message, "JSON 규칙 확인");
                return;
            }
            if (!PrepareNewAnalysis()) return;
            _analysisCts = new CancellationTokenSource();
            SetAnalysisUi(true);
            var targets = _files.ToArray();
            int processed = 0;
            AnalysisProgressBar.Value = 0;
            AnalysisProgressText.Text = $"0 / {targets.Length} (0%)";
            LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  PDF 텍스트 추출 시작 ({targets.Length}건)");
            try
            {
                foreach (var item in targets)
                {
                    if (_analysisCts!.IsCancellationRequested) break;
                    item.Status = "분석 중";
                        item.AnalysisStarted = DateTime.Now; item.AnalysisModel = "C# Local";
                    FilesGrid.Items.Refresh();
                    try
                    {
                        var result = await _textExtractor.ExtractAsync(item.FullPath, _analysisCts!.Token);
                        var parsed = _fieldParser.Parse(result, rules);
                        MsdsTempFiles.Save(item.FullPath, result, parsed);
                        SetAnalysisResult(item, parsed);
                        if (ReferenceEquals(FilesGrid.SelectedItem, item)) ShowExtractedFields(item);
                        if (result.HasExtractableText)
                            LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {item.FileName}: 후보 필드 {parsed.Fields.Count(f => f.Value != null)}건, CAS {parsed.Ingredients.Count}건 (원문 확인 필요)");
                        string detail = result.HasExtractableText
                            ? $"{result.PageCount}페이지, 텍스트 {result.FullText.Length:N0}자"
                            : $"{result.PageCount}페이지, 텍스트 없음 (수동 입력 대상)";
                        LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {item.FileName}: {detail}");
                    }
                    catch (OperationCanceledException) when (_analysisCts?.IsCancellationRequested == true)
                    {
                        item.Status = "중지됨";
                        break;
                    }
                    catch (Exception ex)
                    {
                        item.Status = "분석완료";
                        LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {item.FileName}: 오류 - {ex.Message}");
                    }
                    processed++;
                    int percent = (int)Math.Round(processed * 100.0 / targets.Length);
                    AnalysisProgressBar.Value = percent;
                    AnalysisProgressText.Text = $"{processed} / {targets.Length} ({percent}%)";
                    FilesGrid.Items.Refresh();
                }
                LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  PDF 텍스트 추출 종료");
                PublishStatus(_analysisCts?.IsCancellationRequested == true ? "MSDS 분석 중지됨" : "MSDS 분석 처리 종료");
            }
            finally
            {
                FinishCancelledAnalysis();
                SetAnalysisUi(false);
                _analysisCts?.Dispose();
                _analysisCts = null;
                StopAnalysisButton.IsEnabled = true;
            }
        }

        // 파일별 추출 결과를 표시한다. DB 저장이나 자동 검증은 수행하지 않는다.
        private void ClearAdditionalFields()
        {
            foreach (var box in new[] { GracoCodeBox, RevisionDateBox, RevisionNumberBox,
                PhoneBox, AddressBox, UnNumberBox, HazardCodesBox, AutoIgnitionBox,
                VaporPressureBox, LelBox, UelBox, FlashPointBox,
                CorrosivityBox, PersonalProtectionBox, MolecularFormulaBox, PhysicalStateBox, HazardousGoodsBox, ReactivityBox }) box.Clear();
        }

        private static string ExtractMoelNumber(PdfItem item)
        {
            return item.Parsed?.Fields.FirstOrDefault(f => f.FieldKey == "msds_moel_submission_no")?.Value ?? "";
        }

        private async void AiCorrection_Click(object sender, RoutedEventArgs e)
        {
            if (_isAnalyzing || _displayedItem?.Parsed == null) return;
            if (!CanUseAiCorrection())
            {
                PublishStatus("AI 교정 접근 제한: 프로그램 사용권한이 없습니다.");
                System.Windows.MessageBox.Show("AI 교정 기능의 사용권한이 없습니다.", "권한 안내", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var item = _displayedItem;
            try
            {
                var pdfText = await _textExtractor.ExtractAsync(item.FullPath);
                if (!ReferenceEquals(_displayedItem, item)) return;
                var values = CaptureFields();
                var rows = CaptureIngredients();
                foreach (var row in rows.Where(r => rows.Count(x => x.CasNumber.Trim() == r.CasNumber.Trim()) == 1))
                    foreach (var field in MsdsIngredientLearningKey.Fields)
                    {
                        var key = MsdsIngredientLearningKey.Key(row.CasNumber, field);
                        if (MsdsIngredientLearningKey.Parse(key, out _, out _)) values[key] = MsdsIngredientLearningKey.Get(row, field) ?? "";
                    }
                var window = new MsdsAiCorrectionWindow(item.FileName, item.FullPath,
                    pdfText, item.Parsed, values, values.Keys, BuildOriginalAiSnapshot(item));
                window.Owner = System.Windows.Window.GetWindow(this);
                if (window.ShowDialog() != true) return;
                _loadingFields = true;
                try
                {
                    foreach (var entry in window.CorrectedValues)
                        if (FindName(entry.Key) is System.Windows.Controls.TextBox box)
                            box.Text = entry.Value;
                        else if (MsdsIngredientLearningKey.Parse(entry.Key, out var cas, out var field))
                        {
                            var matches = rows.Where(r => r.CasNumber.Trim() == cas).ToList();
                            if (matches.Count == 1) MsdsIngredientLearningKey.Set(matches[0], field, entry.Value);
                        }
                    IngredientsGrid.ItemsSource = rows;
                }
                finally { _loadingFields = false; }
                foreach (var key in window.CorrectedValues.Keys) item.Pending.Remove(key);
                foreach (var entry in window.PendingLearning) item.Pending[entry.Key]=entry;
                MarkDirty();
                PublishStatus($"AI 교정값 {window.CorrectedValues.Count}개 항목 적용: {item.FileName} (DB 저장 전)");
            }
            catch (Exception ex)
            {
                PublishStatus("AI 교정 실행 실패: " + ex.Message);
                System.Windows.MessageBox.Show("AI 교정 화면을 열 수 없습니다.\n" + ex.Message,
                    "AI 교정", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowExtractedFields(PdfItem item)
        {
            _loadingFields = true;
            string Get(string key) => item.Parsed?.Fields.FirstOrDefault(f => f.FieldKey == key)?.Value ?? "";
            ProductNameBox.Text = Get("msds_product_name");
            SupplierNameBox.Text = Get("msds_supplier_name");
            GracoCodeBox.Text = MsdsDbRegistration.GracoCode(item.FileName);
            RevisionDateBox.Text = Get("msds_revision_date");
            RevisionNumberBox.Text = Get("msds_revision_number");
            MoelSubmissionBox.Text = ExtractMoelNumber(item);
            PhoneBox.Text = Get("msds_supplier_phone");
            AddressBox.Text = Get("msds_supplier_address");
            UnNumberBox.Text = Get("msds_un_number");
            HazardCodesBox.Text = Get("msds_hazard_codes");
            AutoIgnitionBox.Text = Get("msds_autoignition");
            VaporPressureBox.Text = Get("msds_vapor_pressure");
            LelBox.Text = Get("msds_lel");
            UelBox.Text = Get("msds_uel");
            FlashPointBox.Text = Get("msds_flash_point");
            CorrosivityBox.Text = Get("msds_corrosive_flag");
            PersonalProtectionBox.Text = Get("msds_ppe");
            MolecularFormulaBox.Text = Get("msds_molecular_formula");
            PhysicalStateBox.Text = Get("msds_physical_state");
            HazardousGoodsBox.Text = Get("msds_hazardous_goods_class");
            ToxicityBox.Text = Get("msds_toxicity");
            ReactivityBox.Text = Get("msds_reactivity");

            if (item.Unified.DisplayInitialized)
                foreach (var entry in item.Unified.FinalBasics())
                    if (FindName(entry.Key) is System.Windows.Controls.TextBox box) box.Text = entry.Value;
            if (item.Parsed != null)
            {
                try
                {
                    item.RegistrationKind = MsdsDbRegistration.GetRegistrationStatus(item.MaterialType,
                        MsdsDbRegistration.GracoCode(item.FileName), RevisionNumberBox.Text, RevisionDateBox.Text);
                    FilesGrid.Items.Refresh();
                }
                catch (Exception ex)
                {
                    item.RegistrationKind = "확인필요";
                    PublishStatus("등록 구분 확인 실패: " + ex.Message);
                }
            }
            IngredientsGrid.ItemsSource = UnifiedIngredientEdits(item);
            // 최초 표시에서 계산·추출한 UI 값도 AI 원본 행에 기록한다.
            if (!item.Unified.DisplayInitialized)
            {
                foreach (var field in CaptureFields())
                {
                    var row = item.Unified.Basic(field.Key);
                    if (row != null) row.ValueAi = field.Value;
                }
                item.Unified.DisplayInitialized = true;
            }
            _loadingFields = false;
            LoadCasReferences();
            SaveCurrentButton.IsEnabled = !_isAnalyzing && item.Parsed != null;
            AiCorrectionButton.IsEnabled = !_isAnalyzing && item.Parsed != null && CanUseAiCorrection();
            UpdateActionButtons();
        }

        private static string FriendlyFieldName(string key) => key switch
        {
            "ProductNameBox" or "msds_product_name" => "제품명",
            "SupplierNameBox" or "msds_supplier_name" => "공급업체",
            "GracoCodeBox" => "Graco Code(검토값)",
            "RevisionDateBox" or "msds_revision_date" => "개정일",
            "RevisionNumberBox" or "msds_revision_number" => "개정번호",
            "PhoneBox" or "msds_supplier_phone" => "공급업체 전화번호",
            "AddressBox" or "msds_supplier_address" => "공급업체 주소",
            "UnNumberBox" or "msds_un_number" => "UN 번호",
            "HazardCodesBox" or "msds_hazard_codes" => "유해·위험 문구",
            "AutoIgnitionBox" or "msds_autoignition" => "자연발화온도",
            "VaporPressureBox" or "msds_vapor_pressure" => "증기압",
            "LelBox" or "msds_lel" => "폭발하한(LEL)",
            "UelBox" or "msds_uel" => "폭발상한(UEL)",
            "FlashPointBox" or "msds_flash_point" => "인화점",
            "CorrosivityBox" or "msds_corrosive_flag" => "부식성·자극성",
            "PersonalProtectionBox" or "msds_ppe" => "개인보호구",
            "MoelSubmissionBox" or "msds_moel_submission_no" => "노동부 제출번호",
            "MolecularFormulaBox" or "msds_molecular_formula" => "분자식",
            "PhysicalStateBox" or "msds_physical_state" => "물리적 상태",
            "HazardousGoodsBox" or "msds_hazardous_goods_class" => "위험물 분류",
            "ToxicityBox" or "msds_toxicity" => "독성치",
            "ReactivityBox" or "msds_reactivity" => "반응성",
            _ => key
        };

        private static string[] ParseFriendlyLog(string raw)
        {
            var time = "";
            var message = raw.Trim();
            if (message.Length >= 8 && TimeSpan.TryParse(message.Substring(0, 8), out _))
            { time = message.Substring(0, 8); message = message.Substring(8).Trim(); }
            var file = "전체 작업";
            var separator = message.IndexOf(": ", StringComparison.Ordinal);
            if (separator > 0 && message.Substring(0, separator).Contains(".pdf", StringComparison.OrdinalIgnoreCase))
            { file = message.Substring(0, separator); message = message.Substring(separator + 2).Trim(); }
            string stage = "작업 기록", result = "안내";
            if (message.Contains("학습", StringComparison.OrdinalIgnoreCase)) stage = "교정학습";
            else if (message.Contains("AI", StringComparison.OrdinalIgnoreCase) || message.Contains("하이브리드")) stage = "AI 분석";
            else if (message.Contains("텍스트") || message.Contains("페이지")) stage = "PDF 읽기";
            else if (message.Contains("성분") || message.Contains("CAS")) stage = "구성성분 분석";
            else if (message.Contains("PDF") || message.Contains("목록")) stage = "파일 관리";
            if (message.Contains("실패") || message.Contains("오류")) result = "실패";
            else if (message.Contains("중지") || message.Contains("취소")) result = "중지/취소";
            else if (message.Contains("반영") || message.Contains("적용")) result = "적용";
            else if (message.Contains("종료") || message.Contains("완료") || message.Contains("추출") || message.Contains("전달")) result = "성공";
            else if (message.Contains("시작")) result = "시작";
            else if (message.Contains("제외") || message.Contains("건너")) result = "건너뜀";
            message = message.Replace("(검증 필요)", "(원문 확인 필요)").Replace("S03 학습", "유사 문서(S03) 교정학습")
                .Replace("S01~S06 유사 PDF 학습", "유사 문서 교정학습").Replace("AI 추출", "AI 분석으로 추출한 항목")
                .Replace("하이브리드 추출", "PDF 텍스트 및 AI 분석으로 추출한 항목");
            return new[] { time, file, stage, result, message };
        }

        private void ExportAnalysis_Click(object sender, RoutedEventArgs e)
        {
            if (_isAnalyzing || !_files.Any(v => v.Parsed != null)) return;
            if (!IngredientsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true) ||
                !IngredientsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true))
            { PublishStatus("편집 중인 성분값을 확인해 주세요."); return; }
            try
            {
                var outputFolder = System.IO.Path.Combine(AppContext.BaseDirectory, "Output");
                Directory.CreateDirectory(outputFolder);
                var xlsxPath = System.IO.Path.Combine(outputFolder,
                    $"QCS_MSDS_Analysis_{DateTime.Now:yyyyMMdd_HHmmss_fffffff}.xlsx");
                // Export the same 16-column rows used in memory; no separate report data store.
                var analyzed = _files.Where(v => v.Parsed != null).ToList();
                var excelRows = new List<string[]>
                {
                    new[] { "MSDS ID", "구분", "구성성분 순번", "CAS No.", "성분명", "항목코드", "항목명",
                        "AI 최초", "AI 교정", "사용자 수정", "최종", "AI교정학습No", "AI적용",
                        "검증상태", "원문페이지", "원문근거" }
                };
                foreach (var item in analyzed)
                {
                    // Reflect unsaved edits on the active screen without changing the confirmed state.
                    if (ReferenceEquals(item, _displayedItem))
                    {
                        foreach (var field in CaptureFields()) item.Unified.SaveBasic(field.Key, field.Value);
                        UpdateUnifiedIngredients(item, CaptureIngredients());
                    }
                    foreach (var row in item.Unified.Rows)
                        excelRows.Add(new[]
                        {
                            row.MsdsId, row.Category, row.Order?.ToString() ?? "", row.CasNumber,
                            row.IngredientName, row.FieldCode, row.FieldName, row.ValueAi,
                            row.ValueCorrection ?? "", row.ValueMod ?? "", row.FinalValue,
                            row.LearningNo, row.AiApplied ? "Y" : "N", row.Verification,
                            row.SourcePage?.ToString() ?? "", row.SourceEvidence
                        });
                }
                var logRows = new List<string[]> { new[] { "기록시간", "MSDS 파일명", "처리 단계", "처리 결과", "상세 내용" } };
                foreach (var item in analyzed)
                {
                    var duration = item.AnalysisStarted.HasValue && item.AnalysisFinished.HasValue
                        ? (item.AnalysisFinished.Value - item.AnalysisStarted.Value).TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                        : "";
                    logRows.Add(new[] { item.AnalysisFinished?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                        item.FileName, "AI 분석 요약", item.AnalysisStatus,
                        $"모델: {item.AnalysisModel}; 시작: {item.AnalysisStarted:yyyy-MM-dd HH:mm:ss}; 종료: {item.AnalysisFinished:yyyy-MM-dd HH:mm:ss}; 소요시간: {duration}초; 구성성분: {item.Parsed?.Ingredients.Count ?? 0}건; 교정학습 적용: {item.Applied.Count}건" });
                }
                foreach (var entry in LogList.Items.Cast<object>().Reverse())
                    logRows.Add(ParseFriendlyLog(entry?.ToString() ?? ""));

                // 원문 로그/필드에 API 키가 섞인 경우 Excel에도 기록하지 않는다.
                string secretKey = "";
                try { secretKey = GetAiSetting("MSDS_API_KEY") ?? ""; } catch { }
                if (!string.IsNullOrEmpty(secretKey))
                {
                    foreach (var row in excelRows.Concat(logRows))
                        for (int i = 0; i < row.Length; i++) row[i] = row[i].Replace(secretKey, "[REDACTED]", StringComparison.Ordinal);
                }
                // Export exactly the same sanitized rows to JSON, preserving column order and blank values.
                // Excel output remains unchanged; JSON is an additional companion file.
                MsdsAnalysisExcelWriter.Save(xlsxPath, excelRows, logRows);
                var jsonPath = System.IO.Path.ChangeExtension(xlsxPath, ".json");
                string? jsonWarning = null;
                try
                {
                    var payload = new
                    {
                        formatVersion = 1,
                        exportedAt = DateTimeOffset.Now.ToString("O"),
                        analysisColumns = excelRows[0],
                        analysisRows = excelRows.Skip(1).ToArray(),
                        logColumns = logRows[0],
                        logRows = logRows.Skip(1).ToArray()
                    };
                    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                    System.IO.File.WriteAllText(jsonPath, json, new System.Text.UTF8Encoding(false));
                }
                catch (Exception jsonEx)
                {
                    jsonWarning = jsonEx.Message;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(xlsxPath) { UseShellExecute = true });
                if (jsonWarning == null)
                    PublishStatus("분석결과 Excel + JSON 저장 완료: " + System.IO.Path.GetFileName(xlsxPath));
                else
                    PublishStatus("Excel 저장 완료, JSON 저장 실패: " + jsonWarning);
            }
            catch (Exception ex)
            {
                PublishStatus("분석결과 출력 실패: " + ex.Message);
                System.Windows.MessageBox.Show(ex.Message, "분석결과 출력", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateCount()
        {
            FileCountText.Text = $"등록된 파일 {_files.Count}건";
            StartAnalysisButton.IsEnabled = _files.Count > 0 && !_isAnalyzing;
            UpdateNavigation();
            UpdateActionButtons();
        }
    }
}
