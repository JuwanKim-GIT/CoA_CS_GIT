using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

// 🎯 [WPF 전용 매싱 강제 지정] 남은 28개 에러를 격파하기 위한 별칭 추가
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using DataGrid = System.Windows.Controls.DataGrid;       // Grid 컨트롤 모호성 해결
using Clipboard = System.Windows.Clipboard;             // 클립보드 기능 모호성 해결
using KeyEventArgs = System.Windows.Input.KeyEventArgs; // 2층 PreviewKeyDown 이벤트 모호성 해결
using Orientation = System.Windows.Controls.Orientation; // Orientation 레이아웃 모호성 해결

using Excel = NetOffice.ExcelApi;

namespace CoA_CS
{
    /// <summary>
    /// Interaction logic for CoARegistryView.xaml
    /// </summary>
    public partial class CoARegistryView : UserControl
    {
        // 입력용 그리드와 연동될 동적 컬렉션
        private ObservableCollection<PreviewItem> _inputList = new ObservableCollection<PreviewItem>();

        public CoARegistryView()
        {
            InitializeComponent();
            CheckAndInitStartNumber();
            DgInput.ItemsSource = _inputList;
        }

        /// <summary>
        /// 프로그램 최초 실행 시 zx_code_mstr에서 시작 번호를 조회하고, 없으면 유효한 값을 입력받아 생성하는 메서드
        /// </summary>
        private void CheckAndInitStartNumber()
        {
            // Online 모드일 경우 시작번호 팝업을 건너뛰고 바로 리턴 (301번대 사용)
            if (DatabaseManager.IsOnline)
                return;

            bool hasConfig = false;

            try
            {
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseManager.ActiveConnectionString))
                {
                    conn.Open();

                    // 1. 기존에 설정된 고유 시작 번호 필드가 있는지 조회 (Read)
                    string checkQuery = "SELECT COUNT(*) FROM zx_code_mstr WHERE zx_code_fldname = 'CoA_CS_Start_Num';";
                    using (var cmd = new Microsoft.Data.Sqlite.SqliteCommand(checkQuery, conn))
                    {
                        long count = (long)cmd.ExecuteScalar();
                        if (count > 0) hasConfig = true;
                    }

                    // 2. 설정값이 이미 존재한다면 예외 없이 즉시 통과! (아무것도 건드리지 않음)
                    if (hasConfig) return;

                    // 3. 설정값이 없으면 올바른 값을 입력받기 위한 입력창 가동
                    string userInput = "";
                    List<string> validInputs = new List<string> { "501", "601", "701", "801", "901" };

                    //while (true)
                    //{
                    //    // Microsoft.VisualBasic 참조를 활용한 순정 InputBox 호출
                    //    userInput = Microsoft.VisualBasic.Interaction.InputBox(
                    //        "오프라인 사용자 구분을 위한 고유 시작 번호를 입력해 주세요.\n\n[입력 가능 값]: 501, 601, 701, 801, 901",
                    //        "최초 실행 설정 (User Configuration)",
                    //        "501"
                    //    ).Trim();

                    //    // 💡 취소 버튼을 누르거나 빈 값을 넣었을 때
                    //    if (string.IsNullOrEmpty(userInput))
                    //    {
                    //        MessageBox.Show("초기 설정 없이는 CoA 발행 기능을 이용할 수 없습니다.\n조회 및 발행 기능이 비활성화됩니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);

                    //        // 🎯 그리드를 건드리지 않고, 핵심 기능 버튼만 정밀 타격해서 비활성화!
                    //        this.IsEnabled = false;
                    //        return;
                    //    }

                    //    // 입력값 유효성 검사 (5가지 중 하나라면 루프 탈출)
                    //    if (validInputs.Contains(userInput))
                    //    {
                    //        break;
                    //    }

                    //    MessageBox.Show("잘못된 입력입니다!\n시작 번호는 반드시 501, 601, 701, 801, 901 중 하나여야만 합니다.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                    //}




                    while (true)
                    {
                        string tempValue = "";
                        bool isCanceled = true;

                        //  중복과 불필요한 윈폼 찌꺼기 경로 코드를 완전히 걷어내고 정석으로 마감
                        Window inputWindow = new Window
                        {
                            Title = "최초 실행 설정 (User Configuration)",
                            Width = 400,
                            SizeToContent = SizeToContent.Height,
                            WindowStartupLocation = WindowStartupLocation.CenterScreen, // 한 줄만 깔끔하게 유지
                            ResizeMode = ResizeMode.NoResize,
                            Topmost = true // 쉼표나 별도 선언 없이 깔끔하게 true로 닫기
                        };
                        StackPanel sp = new StackPanel { Margin = new Thickness(20) }; // 마진을 넓혀 여백 확보
                        TextBlock txtMsg = new TextBlock
                        {
                            Text = "오프라인 사용자 구분을 위한 고유 시작 번호를 입력해 주세요.\n\n[입력 가능 값]: 501, 601, 701, 801, 901",
                            Margin = new Thickness(0, 0, 0, 12),
                            TextWrapping = TextWrapping.Wrap
                        };
                        TextBox txtInput = new TextBox { Height = 26, Text = "501", VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 15) };

                        // 🎯 버튼 구역 상단 여백 보정
                        Grid btnGrid = new Grid { Margin = new Thickness(0, 5, 0, 0) };
                        btnGrid.ColumnDefinitions.Add(new ColumnDefinition());
                        btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                        btnGrid.ColumnDefinitions.Add(new ColumnDefinition());

                        Button btnOk = new Button { Content = "확인", Height = 26 };
                        Button btnCancel = new Button { Content = "취소", Height = 26 };

                        // 🎯 [이벤트 핸들러] 확인/취소 시 플래그 제어 및 창 닫기
                        btnOk.Click += (s, e) => { isCanceled = false; tempValue = txtInput.Text.Trim(); inputWindow.Close(); };
                        btnCancel.Click += (s, e) => { inputWindow.Close(); };

                        Grid.SetColumn(btnOk, 0);
                        Grid.SetColumn(btnCancel, 2);
                        btnGrid.Children.Add(btnOk);
                        btnGrid.Children.Add(btnCancel);

                        sp.Children.Add(txtMsg);
                        sp.Children.Add(txtInput);
                        sp.Children.Add(btnGrid);
                        inputWindow.Content = sp;

                        // 모달 팝업으로 기동 (WPF 순정 엔진 작동)
                        inputWindow.ShowDialog();

                        // 🎯 사용자가 취소를 누르거나 입력을 비워둔 경우 방어벽
                        if (isCanceled || string.IsNullOrEmpty(tempValue))
                        {
                            MessageBox.Show("초기 설정 없이는 CoA 발행 기능을 이용할 수 없습니다.\n조회 및 발행 기능이 비활성화됩니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
                            this.IsEnabled = false;
                            return;
                        }

                        // 유효성 검사 성공 시 userInput 변수에 값을 채우고 루프 탈출
                        if (validInputs.Contains(tempValue))
                        {
                            userInput = tempValue;
                            break;
                        }

                        MessageBox.Show("잘못된 입력입니다!\n시작 번호는 반드시 501, 601, 701, 801, 901 중 하나여야만 합니다.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }






                    // 4. 검증 완료된 값을 zx_code_mstr 테이블에 최종 생성 (Write)
                    // 와니의 규칙: zx_code_value는 빈 값, zx_code_cmmt에 입력값 주입
                    string insertQuery = @"
                        INSERT INTO zx_code_mstr (zx_code_fldname, zx_code_value, zx_code_cmmt, zx_code_desc1, zx_code_desc2)
                        VALUES ('CoA_CS_Start_Num', '', @UserValue, '오프라인 유저 구분 시작번호', '');";

                    using (var insertCmd = new Microsoft.Data.Sqlite.SqliteCommand(insertQuery, conn))
                    {
                        insertCmd.Parameters.AddWithValue("@UserValue", userInput);
                        insertCmd.ExecuteNonQuery();
                    }

                    MessageBox.Show($"사용자 초기 설정이 완료되었습니다!\n지정된 시작 번호 대역: {userInput}", "성공", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                // UI 잠금 기능인 `this.IsEnabled = false;`를 완전히 제거하여 그리드 오작동을 원천 차단!
                MessageBox.Show($"초기 설정 검사 중 오류가 발생했습니다:\n{ex.Message}", "시스템 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// New 버튼 클릭 이벤트 핸들러 (마스터, 수량, 크로마스칸, 배치 및 날짜/만료일 통합 검증)
        /// </summary>
        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            if (_inputList.Count == 0)
            {
                MessageBox.Show("입력 그리드에 데이터가 존재하지 않습니다.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // [0단계] CoA No 생성 (TxtCoaNo.Text가 비어있을 때만 채번)
            if (string.IsNullOrEmpty(TxtCoaNo.Text))
            {
                string todayStr = DateTime.Now.ToString("yyyyMMdd");
                string startNum = string.Empty;

                // 1. CoA_CS_Start_Num 조회
                using (var conn0 = new SqliteConnection(DatabaseManager.ActiveConnectionString))
                {
                    conn0.Open();
                    string query0 = "SELECT zx_code_cmmt FROM zx_code_mstr WHERE zx_code_fldname = 'CoA_CS_Start_Num' LIMIT 1;";
                    using (var cmd0 = new SqliteCommand(query0, conn0))
                    {
                        var result0 = cmd0.ExecuteScalar();
                        if (result0 != null && result0 != DBNull.Value)
                            startNum = result0.ToString().Trim();
                    }
                }
                if (string.IsNullOrEmpty(startNum)) startNum = "501"; // 방어

                // [매일 초기화] 오늘 날짜의 coa_no 레코드가 없으면 Next_Num을 Start_Num으로 리셋
                string startDigit = startNum.Length > 0 ? startNum[0].ToString() : "5";
                string todayPrefix = $"CoA-{todayStr}-{startDigit}";
                using (var connReset = new SqliteConnection(DatabaseManager.ActiveConnectionString))
                {
                    connReset.Open();
                    string checkToday = "SELECT COUNT(*) FROM coa_mstr WHERE coa_no LIKE @Prefix || '%';";
                    using (var cmdCheck = new SqliteCommand(checkToday, connReset))
                    {
                        cmdCheck.Parameters.AddWithValue("@Prefix", todayPrefix);
                        long todayCount = (long)cmdCheck.ExecuteScalar();
                        if (todayCount == 0)
                        {
                            // 오늘 발행된 CoA가 없음 → Next_Num을 Start_Num으로 초기화
                            string resetNext = "UPDATE zx_code_mstr SET zx_code_cmmt = @StartVal WHERE zx_code_fldname = 'CoA_CS_Next_Num';";
                            using (var cmdReset = new SqliteCommand(resetNext, connReset))
                            {
                                cmdReset.Parameters.AddWithValue("@StartVal", startNum);
                                cmdReset.ExecuteNonQuery();
                            }
                        }
                    }
                }

                int coaSeq;
                using (var conn2 = new SqliteConnection(DatabaseManager.ActiveConnectionString))
                {
                    conn2.Open();

                    // 2. CoA_CS_Next_Num 조회 (항상 현재 값 사용, 없으면 Start_Num으로 fallback)
                    string selectNext = "SELECT zx_code_cmmt FROM zx_code_mstr WHERE zx_code_fldname = 'CoA_CS_Next_Num' LIMIT 1;";
                    using (var cmd2 = new SqliteCommand(selectNext, conn2))
                    {
                        var result2 = cmd2.ExecuteScalar();
                        if (result2 != null && result2 != DBNull.Value && int.TryParse(result2.ToString().Trim(), out int nextVal))
                            coaSeq = nextVal;
                        else
                            coaSeq = int.Parse(startNum); // Next_Num 없으면 Start_Num으로 fallback
                    }

                    // 3. Next_Num +1 갱신 (항상 UPDATE만, 신규 INSERT 금지)
                    string updateNext = "UPDATE zx_code_mstr SET zx_code_cmmt = @NextVal WHERE zx_code_fldname = 'CoA_CS_Next_Num';";
                    using (var cmd2 = new SqliteCommand(updateNext, conn2))
                    {
                        cmd2.Parameters.AddWithValue("@NextVal", (coaSeq + 1).ToString());
                        cmd2.ExecuteNonQuery();
                    }
                }

                // 4. TextBox에 표시
                string coaNoForDisplay = $"CoA-{todayStr}-{coaSeq:D3}";
                TxtCoaNo.Text = coaNoForDisplay;
            }

            // 하단 결과 그리드 리셋
            TblPreview.ItemsSource = null;
            TblDetail.ItemsSource = null;

            List<PreviewItem> previewList = new List<PreviewItem>();

            // 1. 입력받은 라인별로 루프를 돌린다.
            foreach (var inputItem in _inputList)
            {
                // 🎯 [수정] 기본값 초기화 구역
                string statusVal = "O";
                string msgVal = "Valid";
                string desc1Val = "";
                string desc2Val = "";
                string shelfVal = "0";
                string conv1Val = "0";
                string colorCodeVal = inputItem.ColorCode;
                string mfDateVal = inputItem.MfDate != null ? inputItem.MfDate.Trim() : "";
                string expDateVal = "";

                // [단계 1] QTY 유효성 검증 로직
                string rawQty = inputItem.Qty != null ? inputItem.Qty.Trim() : "0";
                if (string.IsNullOrEmpty(rawQty) || rawQty == "?" || !double.TryParse(rawQty, out double parsedQty) || parsedQty <= 0)
                {
                    statusVal = "X";
                    msgVal = "Wrong QTY";
                }

                // 앞자리 0을 제거한 청정 품목코드 생성
                string itemCode = inputItem.BaseItemCode.Trim();
                string cleanItemCode = itemCode.TrimStart('0');

                // [단계 2] pt2_mstr 대조 진행
                if (statusVal == "O" && !string.IsNullOrEmpty(cleanItemCode))
                {
                    using (var conn = new SqliteConnection(DatabaseManager.ActiveConnectionString))
                    {
                        conn.Open();
                        string query = "SELECT pt2_desc1, pt2_desc2, pt2_shelf, pt2_conv1, pt2_color_code FROM pt2_mstr WHERE LTRIM(pt2_part, '0') = @ItemCode LIMIT 1;";

                        using (var cmd = new SqliteCommand(query, conn))
                        {
                            cmd.Parameters.AddWithValue("@ItemCode", cleanItemCode);
                            using (var reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    desc1Val = reader["pt2_desc1"] != DBNull.Value ? reader["pt2_desc1"].ToString().Trim() : "";
                                    desc2Val = reader["pt2_desc2"] != DBNull.Value ? reader["pt2_desc2"].ToString().Trim() : "";
                                    shelfVal = reader["pt2_shelf"] != DBNull.Value ? reader["pt2_shelf"].ToString().Trim() : "0";
                                    conv1Val = reader["pt2_conv1"] != DBNull.Value ? reader["pt2_conv1"].ToString().Trim() : "0";
                                    string mstrColor = reader["pt2_color_code"] != DBNull.Value ? reader["pt2_color_code"].ToString().Trim() : "";

                                    if (string.IsNullOrEmpty(colorCodeVal) || colorCodeVal == "-")
                                    {
                                        colorCodeVal = mstrColor;
                                    }

                                    // pt2_shelf 유효성 검증 (Wrong Shelf) → statusVal은 'O' 유지
                                    bool isShelfOk = int.TryParse(shelfVal, out int shelfCheck) && shelfCheck > 0;
                                    if (!isShelfOk)
                                    {
                                        msgVal = "Wrong Shelf";
                                    }

                                    // pt2_conv1 유효성 검증 (Wrong QTY) → statusVal을 'X'로 처리
                                    bool isConv1Ok = double.TryParse(conv1Val, out double conv1Check) && conv1Check > 0;
                                    if (!isConv1Ok)
                                    {
                                        statusVal = "X";
                                        msgVal = "Wrong QTY";
                                    }
                                }
                                else
                                {
                                    statusVal = "X";
                                    msgVal = "Wrong Item";
                                }
                            }
                        }
                    }
                }
                else if (statusVal == "O" && string.IsNullOrEmpty(cleanItemCode))
                {
                    statusVal = "X";
                    msgVal = "Wrong Item";
                }

                // [단계 3] CHS(Chromascan) 코드가 존재할 때 zx_code_mstr 대조 및 품명 치환
                string chsCode = inputItem.ChsCode != null ? inputItem.ChsCode.Trim() : "";

                if (statusVal == "O" && !string.IsNullOrEmpty(chsCode) && chsCode != "-")
                {
                    bool isChsValid = false;

                    using (var conn = new SqliteConnection(DatabaseManager.ActiveConnectionString))
                    {
                        conn.Open();
                        string zxQuery = @"
                            SELECT zx_code_cmmt, zx_code_desc2 
                            FROM zx_code_mstr 
                            WHERE zx_code_fldname = 'CoA_ChromaScan' AND zx_code_value = @ChsCode 
                            LIMIT 1;";

                        using (var zxCmd = new SqliteCommand(zxQuery, conn))
                        {
                            zxCmd.Parameters.AddWithValue("@ChsCode", chsCode);
                            using (var zxReader = zxCmd.ExecuteReader())
                            {
                                if (zxReader.Read())
                                {
                                    isChsValid = true;

                                    try
                                    {
                                        string[] descParts = desc1Val.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                        if (descParts.Length >= 3)
                                        {
                                            string targetPart = descParts[2];
                                            string baseColorCode = targetPart.Split('/')[0];
                                            if (!string.IsNullOrEmpty(baseColorCode))
                                            {
                                                desc1Val = desc1Val.Replace(baseColorCode, chsCode);
                                            }
                                        }
                                    }
                                    catch { }

                                    string zxDesc2 = zxReader["zx_code_desc2"] != DBNull.Value ? zxReader["zx_code_desc2"].ToString().Trim() : "";
                                    if (!string.IsNullOrEmpty(zxDesc2))
                                    {
                                        desc2Val = zxDesc2;
                                    }

                                    string zxCmmt = zxReader["zx_code_cmmt"] != DBNull.Value ? zxReader["zx_code_cmmt"].ToString().Trim() : "";
                                    if ((string.IsNullOrEmpty(colorCodeVal) || colorCodeVal == "-") && !string.IsNullOrEmpty(zxCmmt))
                                    {
                                        colorCodeVal = zxCmmt;
                                    }
                                }
                            }
                        }
                    }

                    if (!isChsValid)
                    {
                        statusVal = "X";
                        msgVal = "Wrong CHS";
                    }
                }

                // [단계 4] qmir_det 대조 및 날짜/만료일 계산 처리 🌟
                string batchNo = inputItem.BatchNumber != null ? inputItem.BatchNumber.Trim() : "";

                if (statusVal == "O")
                {
                    bool isBatchValid = false;
                    string dbMfDate = "";
                    string rawQmirItem = "";

                    using (var conn = new SqliteConnection(DatabaseManager.ActiveConnectionString))
                    {
                        conn.Open();
                        // qmir_batch 번호로 데이터가 존재하는지 대조 및 필요한 컬럼 추출
                        string qmirQuery = "SELECT qmir_mf_date, qmir_part FROM qmir_det WHERE qmir_batch = @BatchNo LIMIT 1;";

                        using (var qmirCmd = new SqliteCommand(qmirQuery, conn))
                        {
                            qmirCmd.Parameters.AddWithValue("@BatchNo", batchNo);
                            using (var qmirReader = qmirCmd.ExecuteReader())
                            {
                                if (qmirReader.Read())
                                {
                                    isBatchValid = true;
                                    dbMfDate = qmirReader["qmir_mf_date"] != DBNull.Value ? qmirReader["qmir_mf_date"].ToString().Trim() : "";
                                    rawQmirItem = qmirReader["qmir_part"] != DBNull.Value ? qmirReader["qmir_part"].ToString().Trim() : "";
                                }
                            }
                        }
                    }

                    if (isBatchValid)
                    {
                        // 화면 입력 제조일자가 누락되었거나 대시(-)면 DB 제조일자로 자동 바인딩
                        if (string.IsNullOrEmpty(mfDateVal) || mfDateVal == "-")
                        {
                            mfDateVal = dbMfDate;
                        }

                        if (string.IsNullOrEmpty(mfDateVal) || mfDateVal == "-")
                        {
                            statusVal = "X";
                            msgVal = "Wrong Date";
                        }
                        else
                        {
                            // 날짜 포맷 유연하게 파싱 처리 (2026-07-06, 2026/07/06, 26-07-06, 20260706 등 대응)
                            string cleanDateStr = mfDateVal.Replace("/", "-").Replace(".", "-").Trim();
                            string[] formats = { "yyyy-MM-dd", "yy-MM-dd", "yyyyMMdd", "yyyy-MM-dd HH:mm:ss" };

                            // 날짜 문자열 공백 기준 첫 번째 파트만 추출 (시분초 잘라내기 방어)
                            if (cleanDateStr.Contains(" "))
                            {
                                cleanDateStr = cleanDateStr.Split(' ')[0];
                            }

                            if (DateTime.TryParseExact(cleanDateStr, formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime dtMf))
                            {
                                // 보존기한(Shelf)을 날짜 수치로 더해서 유효기간(EXP. Date) 역산 처리
                                if (int.TryParse(shelfVal, out int shelfDays) && shelfDays > 0)
                                {
                                    DateTime dtExp = dtMf.AddDays(shelfDays - 1); // 파index 가동 규칙 반영
                                    expDateVal = dtExp.ToString("yyyy-MM-dd");
                                    mfDateVal = dtMf.ToString("yyyy-MM-dd"); // 화면 출력도 깔끔하게 통일
                                }
                            }
                            else
                            {
                                statusVal = "X";
                                msgVal = "Wrong Date";
                            }
                        }

                        // [단계 4-2] 파이썬 엔진 품질 연동 스펙 검증 (Wrong FINI 시리즈 완벽 보정)
                        if (statusVal == "O")
                        {
                            // 1. 알파벳과 숫자만 남기고 특수문자 제거
                            string actualQmirItem = System.Text.RegularExpressions.Regex.Replace(rawQmirItem, @"[^a-zA-Z0-9]", "");
                            // 2. 왼쪽의 '0'을 완벽하게 제거
                            string cleanQmirItem = actualQmirItem.TrimStart('0');

                            bool isQmirPtValid = false;
                            string mfgPartVal = "";

                            if (!string.IsNullOrEmpty(cleanQmirItem))
                            {
                                using (var conn = new SqliteConnection(DatabaseManager.ActiveConnectionString))
                                {
                                    conn.Open();
                                    string finiQuery = "SELECT pt2_mfg_part FROM pt2_mstr WHERE LTRIM(pt2_part, '0') = @QmirItem LIMIT 1;";
                                    using (var finiCmd = new SqliteCommand(finiQuery, conn))
                                    {
                                        finiCmd.Parameters.AddWithValue("@QmirItem", cleanQmirItem);
                                        using (var finiReader = finiCmd.ExecuteReader())
                                        {
                                            if (finiReader.Read())
                                            {
                                                isQmirPtValid = true;
                                                mfgPartVal = finiReader["pt2_mfg_part"] != DBNull.Value ? finiReader["pt2_mfg_part"].ToString().Trim() : "";
                                            }
                                        }
                                    }
                                }
                            }

                            // 대소문자 무시 비교를 위해 ToUpper 처리
                            string currentCombinedDesc = $"{desc1Val} {desc2Val}".Trim().ToUpper();
                            string upperMfgPart = mfgPartVal.ToUpper();
                            string upperChsCode = chsCode.ToUpper();

                            // 💡 와니 말대로 statusVal은 "O"로 유지하면서 msgVal만 덮어쓰기 가동!
                            if (!isQmirPtValid)
                            {
                                msgVal = "Wrong FINI1";
                            }
                            else if (string.IsNullOrEmpty(mfgPartVal))
                            {
                                msgVal = "Wrong FINI3";
                            }
                            // 크로마스칸 코드가 비어있거나 대시(-)이고, 완제품 파트 6자리가 품명에 포함되지 않는 경우
                            else if ((string.IsNullOrEmpty(upperChsCode) || upperChsCode == "-") &&
                                     (upperMfgPart.Length < 6 || !currentCombinedDesc.Contains(upperMfgPart.Substring(0, 6))))
                            {
                                msgVal = "Wrong FINI2";
                            }
                        }
                    }
                    else
                    {
                        statusVal = "X";
                        msgVal = "Wrong Batch";
                    }
                }

                // [단계 5] 내용량(LTQTY) 계산 및 conv1 수치 검증
                string ltqtyVal = "0.00";
                if (statusVal == "O" || msgVal.StartsWith("Wrong FINI"))
                {
                    if (double.TryParse(conv1Val, out double floatConv) && floatConv > 0)
                    {
                        ltqtyVal = floatConv.ToString("F2");
                    }
                }

                // 최종 가공된 품명 1과 2 결합
                string finalDesc = $"{desc1Val} {desc2Val}".Trim();

                // 2층 프리뷰 그리드 모델에 바인딩 데이터 생성
                PreviewItem item = new PreviewItem
                {
                    Valid = statusVal,
                    ErrorMsg = msgVal,
                    BaseItemCode = inputItem.BaseItemCode,
                    Desc = finalDesc,
                    BatchNumber = inputItem.BatchNumber,
                    ChsCode = inputItem.ChsCode,
                    ColorCode = string.IsNullOrEmpty(colorCodeVal) ? "" : colorCodeVal,
                    Qty = inputItem.Qty,
                    LtQty = ltqtyVal,
                    MfDate = string.IsNullOrEmpty(mfDateVal) ? "" : mfDateVal,
                    Shelf = shelfVal,
                    ExpDate = expDateVal,
                    PjtNo = inputItem.PjtNo
                };

                previewList.Add(item);
            }

            // 하단 2층 프리뷰 그리드에 소스 적용하여 화면 갱신
            TblPreview.ItemsSource = previewList;
        }

        /// <summary>
        /// 2층 그리드에서 행을 선택하면 실행되는 감지기 (FullRow 매칭 버전)
        /// </summary>
        private void TblPreview_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 행 단위 선택 모드에서 현재 선택된 아이템을 안전하게 캐스팅
            if (TblPreview.SelectedItem is PreviewItem selectedItem)
            {
                string batchNo = selectedItem.BatchNumber != null
                    ? selectedItem.BatchNumber.Replace("\r", "").Replace("\n", "").Trim()
                    : "";

                if (string.IsNullOrEmpty(batchNo) || batchNo == "-")
                {
                    TblDetail.ItemsSource = null;
                    return;
                }

                List<DetailItem> detailList = new List<DetailItem>();

                try
                {
                    using (var conn = new SqliteConnection(DatabaseManager.ActiveConnectionString))
                    {
                        conn.Open();

                        string query = @"
                            SELECT qmir_no, qmir_charac, qmir_first_value, qmir_last_value, 
                                   qmir_uom, qmir_ltol, qmir_utol, qmir_mf_date, qmir_part 
                            FROM qmir_det 
                            WHERE TRIM(qmir_batch) = @BatchNo;";

                        using (var cmd = new SqliteCommand(query, conn))
                        {
                            cmd.Parameters.AddWithValue("@BatchNo", batchNo);
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    string FormatTo5(object val)
                                    {
                                        if (val == DBNull.Value || val == null) return "";
                                        string raw = val.ToString().Replace("?", "").Trim();
                                        if (double.TryParse(raw, out double d)) return d.ToString("F5");
                                        return raw;
                                    }

                                    DetailItem detail = new DetailItem
                                    {
                                        No = reader["qmir_no"] != DBNull.Value ? reader["qmir_no"].ToString().Trim() : "",
                                        Characteristic = reader["qmir_charac"] != DBNull.Value ? reader["qmir_charac"].ToString().Trim() : "",
                                        FirstValue = FormatTo5(reader["qmir_first_value"]),
                                        LastValue = FormatTo5(reader["qmir_last_value"]),
                                        Uom = reader["qmir_uom"] != DBNull.Value ? reader["qmir_uom"].ToString().Trim() : "",
                                        LowerLimit = FormatTo5(reader["qmir_ltol"]),
                                        UpperLimit = FormatTo5(reader["qmir_utol"]),
                                        MfDateDetail = reader["qmir_mf_date"] != DBNull.Value ? reader["qmir_mf_date"].ToString().Trim() : "",
                                        ItemCode = reader["qmir_part"] != DBNull.Value ? reader["qmir_part"].ToString().Trim() : ""
                                    };

                                    detailList.Add(detail);
                                }
                            }
                        }
                    }

                    TblDetail.ItemsSource = detailList;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"3층 상세 조회 에러: {ex.Message}");
                }
            }
        }





        // 🎯 Register 버튼 클릭 이벤트 핸들러 (1단계 유효성 검사 파트)
        private void BtnRegister_Click(object sender, RoutedEventArgs e)
        {
            // ===================================================================
            // [1단계] 화면 입력 데이터 유효성 최종 검사
            // ===================================================================

            // ① 상단 고객사명 입력 칸(TxtCust)이 비어있는지 검사
            string custName = TxtCust.Text.Trim();
            if (string.IsNullOrEmpty(custName))
            {
                MessageBox.Show("고객사명(Cust Name)은 필수 입력 항목입니다.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtCust.Focus();
                return; // 프로세스 즉시 중단 (방어벽 작동)
            }

            // ② 2층 프리뷰 그리드(TblPreview)에 조회된 행이 존재하는지 검사
            int rowCount = TblPreview.Items.Count;
            if (rowCount == 0)
            {
                MessageBox.Show("출력할 데이터가 없습니다. 먼저 New 조회를 수행하세요.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                return; // 프로세스 즉시 중단 (방어벽 작동)
            }

            // ③ 그리드 내부에 정상 데이터(Valid 상태가 'O')인 행이 단 하나라도 존재하는지 확인
            bool hasValidRow = false;

            for (int idx = 0; idx < rowCount; idx++)
            {
                var rowData = TblPreview.Items[idx];

                if (rowData != null)
                {
                    string statusVal = "";

                    // 와니의 순정 PreviewItem 클래스 모델 바인딩 구조에 맞춰 Valid 속성 추출
                    if (rowData is PreviewItem previewItem)
                    {
                        statusVal = previewItem.Valid?.ToString().Trim() ?? "";
                    }
                    else
                    {
                        // 예방 차원의 리플렉션 방어 코드
                        var prop = rowData.GetType().GetProperty("Valid") ?? rowData.GetType().GetProperty("valid");
                        statusVal = prop?.GetValue(rowData)?.ToString().Trim() ?? "";
                    }

                    // 정상 발행 가능한 라인이 하나라도 있다면 패스!
                    if (statusVal == "O")
                    {
                        hasValidRow = true;
                        break; // 하나라도 찾으면 루프 조기 탈출
                    }
                }
            }

            // 정상 데이터('O')가 하나도 없으면 발행을 원천 차단
            if (!hasValidRow)
            {
                MessageBox.Show("발행 가능한 정상 데이터(Status: O)가 존재하지 않습니다.\n에러 내역을 확인해 주세요.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                return; // 프로세스 즉시 중단 (방어벽 작동)
            }

            string coaMasterNo = TxtCoaNo.Text.Trim();
            if (string.IsNullOrEmpty(coaMasterNo))
            {
                MessageBox.Show("CoA 번호가 생성되지 않았습니다. 먼저 New 버튼을 눌러주세요.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // ❌ (알림창은 5단계 최종 완료 구역으로 이동하므로 여기서는 삭제!)


            // ===================================================================
            // [3단계] 성적서 순정 엑셀 템플릿 로드 및 시트 복사
            // ===================================================================
            string templateFile = "New_CoA_Form_Eng.xltm";
            if (RdoKo.IsChecked == true) templateFile = "New_CoA_Form_Kor.xltm";
            else if (RdoRu.IsChecked == true) templateFile = "New_CoA_form_Rus.xltm";
            else if (RdoJa.IsChecked == true) templateFile = "New_CoA_form_Jpn.xltm";

            // zx_code_mstr에서 CoA_CS_xltm_path 조회 (없으면 등록 요청)
            string xltmBaseDir = null;
            using (var connPath = new SqliteConnection(DatabaseManager.ActiveConnectionString))
            {
                connPath.Open();
                string queryPath = "SELECT zx_code_cmmt FROM zx_code_mstr WHERE zx_code_fldname = 'CoA_CS_xltm_path' LIMIT 1;";
                using (var cmdPath = new SqliteCommand(queryPath, connPath))
                {
                    var resultPath = cmdPath.ExecuteScalar();
                    if (resultPath != null && resultPath != DBNull.Value && !string.IsNullOrEmpty(resultPath.ToString().Trim()))
                        xltmBaseDir = resultPath.ToString().Trim();
                }
            }

            if (string.IsNullOrEmpty(xltmBaseDir))
            {
                MessageBox.Show("zx_code_mstr에 'CoA_CS_xltm_path'가 등록되지 않았습니다.\n관리자에게 문의하여 등록해 주세요.", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string templatePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, xltmBaseDir, templateFile).Replace("/", "\\");

            if (!System.IO.File.Exists(templatePath))
            {
                MessageBox.Show($"서식파일이 존재하지 않습니다:\n{templatePath}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string docsDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CoA_Docs");
            if (!System.IO.Directory.Exists(docsDir))
            {
                System.IO.Directory.CreateDirectory(docsDir);
            }

            string saveFilename = $"{coaMasterNo}.xlsx";
            string savePath = System.IO.Path.Combine(docsDir, saveFilename).Replace("/", "\\");

            Excel.Application excelApp = null;
            Excel.Workbook wb = null;

            try
            {
                excelApp = new Excel.Application();
                excelApp.Visible = false;
                excelApp.DisplayAlerts = false;

                wb = excelApp.Workbooks.Open(templatePath);
                Excel.Worksheet baseWs = (Excel.Worksheet)wb.Sheets[1];

                int validLineIdx = 0;

                for (int idx = 0; idx < rowCount; idx++)
                {
                    var rowData = TblPreview.Items[idx];
                    string statusVal = "";

                    if (rowData is PreviewItem previewItem)
                    {
                        statusVal = previewItem.Valid?.ToString().Trim() ?? "";
                    }
                    else
                    {
                        var prop = rowData.GetType().GetProperty("Valid") ?? rowData.GetType().GetProperty("valid");
                        statusVal = prop?.GetValue(rowData)?.ToString().Trim() ?? "";
                    }

                    if (statusVal != "O") continue;

                    validLineIdx++;
                    string sheetCoaId = $"{coaMasterNo}-{validLineIdx:D3}";

                    Excel.Worksheet ws = null;
                    if (validLineIdx == 1)
                    {
                        ws = baseWs;
                        ws.Name = sheetCoaId;
                    }
                    else
                    {
                        baseWs.Copy(wb.Sheets[validLineIdx]);
                        ws = (Excel.Worksheet)wb.Sheets[validLineIdx];
                        ws.Name = sheetCoaId;
                    }

                    // ===================================================================
                    // [4단계] 엑셀 템플릿 데이터 칼매핑 및 파일 저장
                    // ===================================================================
                    string rawItemCode = "";
                    string combinedDesc = "";
                    string batchStr = "";
                    string colorRaw = "";
                    string qtyStr = "0";
                    string ltqtyStr = "0";
                    string mfDateRaw = "";
                    string expDateRaw = "";
                    string pjtNoRaw = "";

                    if (rowData is PreviewItem pi)
                    {
                        rawItemCode = pi.BaseItemCode ?? "";
                        combinedDesc = pi.Desc ?? "";
                        batchStr = pi.BatchNumber ?? "";
                        colorRaw = pi.ColorCode ?? "";
                        qtyStr = pi.Qty ?? "0";
                        ltqtyStr = pi.LtQty ?? "0";
                        mfDateRaw = pi.MfDate ?? "";
                        expDateRaw = pi.ExpDate ?? "";
                        pjtNoRaw = pi.PjtNo ?? "";
                    }

                    // 날짜 포맷팅 로컬 헬퍼 (DD.MM.YYYY 스타일 변환)
                    string FormatToDotStyle(string dateText)
                    {
                        string clean = dateText.Replace("/", "-").Replace(".", "-").Trim();
                        string[] tokens = clean.Split('-');
                        if (tokens.Length == 3)
                        {
                            string year = tokens[0];
                            if (year.Length == 2) year = "20" + year;
                            return $"{tokens[2]}.{tokens[1]}.{year}";
                        }
                        return dateText;
                    }

                    string formattedMfDate = FormatToDotStyle(mfDateRaw);
                    string formattedExpDate = FormatToDotStyle(expDateRaw);

                    // 내용량 통합 수식 연산 문자열 세팅
                    string cleanLtQty = ltqtyStr.Replace("LT", "").Trim();
                    string volumeCombined = $"{cleanLtQty}LT X {qtyStr}EA";
                    try
                    {
                        double totalVol = Convert.ToDouble(cleanLtQty.Replace(",", "")) * Convert.ToDouble(qtyStr.Replace(",", ""));
                        volumeCombined = $"{cleanLtQty}LT X {qtyStr}EA = {totalVol:F2}LT";
                    }
                    catch { }

                    // ① 상단 마스터 고정 셀 주소 데이터 매핑 주입 (NetOffice 소괄호 문법 반영)
                    ((Excel.Range)ws.Cells[1, 3]).Value = sheetCoaId; // C1
                    ((Excel.Range)ws.Range("M9")).Value = custName;

                    if (!string.IsNullOrEmpty(TxtPjt.Text.Trim()))
                    {
                        ((Excel.Range)ws.Range("B12")).Value = "Project Name  :";
                        ((Excel.Range)ws.Range("D12")).Value = TxtPjt.Text.Trim();
                    }
                    else
                    {
                        ((Excel.Range)ws.Range("B12")).Value = "";
                        ((Excel.Range)ws.Range("D12")).Value = "";
                    }

                    if (!string.IsNullOrEmpty(pjtNoRaw))
                    {
                        ((Excel.Range)ws.Range("B13")).Value = "Project No       :";
                        ((Excel.Range)ws.Range("D13")).Value = pjtNoRaw;
                    }
                    else
                    {
                        ((Excel.Range)ws.Range("B13")).Value = "";
                        ((Excel.Range)ws.Range("D13")).Value = "";
                    }

                    ((Excel.Range)ws.Range("D5")).Value = rawItemCode;
                    ((Excel.Range)ws.Range("D6")).Value = combinedDesc;
                    ((Excel.Range)ws.Range("D8")).Value = batchStr;
                    ((Excel.Range)ws.Range("D9")).Value = formattedMfDate;
                    ((Excel.Range)ws.Range("D10")).Value = formattedExpDate;
                    ((Excel.Range)ws.Range("D11")).Value = volumeCombined;
                    ((Excel.Range)ws.Range("M8")).Value = colorRaw;

                    // ② 하단 품질 상세 스펙 주입 구역 (21행 기본 잔상 제거 및 SQLite 주입)
                    // 🎯 Range 추출 후 ClearContents() 메서드를 소괄호 체인으로 직접 가동
                    ((Excel.Range)ws.Range("A21:O32")).ClearContents();

                    int startRow = 21;
                    int excelRowOffset = 0;

                    using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseManager.ActiveConnectionString))
                    {
                        conn.Open();
                        string detailQuery = "SELECT qmir_charac, qmir_first_value, qmir_uom, qmir_ltol, qmir_utol FROM qmir_det WHERE qmir_batch = @BatchNo;";

                        using (var detCmd = new Microsoft.Data.Sqlite.SqliteCommand(detailQuery, conn))
                        {
                            detCmd.Parameters.AddWithValue("@BatchNo", batchStr);
                            using (var reader = detCmd.ExecuteReader())
                            {
                                // 🎯 [교정] ? 문자가 들어오면 엑셀에 블랭크("")로 치환하는 로컬 헬퍼 함수
                                string FilterQuestionMark(string text)
                                {
                                    if (string.IsNullOrEmpty(text)) return "";
                                    string clean = text.Trim();
                                    return clean == "?" ? "" : clean;
                                }

                                while (reader.Read())
                                {
                                    int currentExcelRow = startRow + excelRowOffset;

                                    string charTxt = reader["qmir_charac"]?.ToString() ?? "";
                                    // 🎯 아래 3개 변수에 필터 함수 적용!
                                    string firstTxt = FilterQuestionMark(reader["qmir_first_value"]?.ToString());
                                    string uomTxt = reader["qmir_uom"]?.ToString() ?? "";
                                    string lowerTxt = FilterQuestionMark(reader["qmir_ltol"]?.ToString());
                                    string upperTxt = FilterQuestionMark(reader["qmir_utol"]?.ToString());

                                    string methodTxt = "";
                                    string zxUomTxt = "";

                                    string methodQuery = @"
                                        SELECT zx_code_desc1, zx_code_desc2 
                                        FROM zx_code_mstr 
                                        WHERE zx_code_fldname = 'CoA_Method' AND UPPER(TRIM(zx_code_cmmt)) = @CharTarget LIMIT 1;";

                                    using (var mCmd = new Microsoft.Data.Sqlite.SqliteCommand(methodQuery, conn))
                                    {
                                        mCmd.Parameters.AddWithValue("@CharTarget", charTxt.Trim().ToUpper());
                                        using (var mReader = mCmd.ExecuteReader())
                                        {
                                            if (mReader.Read())
                                            {
                                                methodTxt = mReader["zx_code_desc1"]?.ToString() ?? "";
                                                zxUomTxt = mReader["zx_code_desc2"]?.ToString() ?? "";
                                            }
                                        }
                                    }

                                    string finalUom = !string.IsNullOrEmpty(zxUomTxt) ? zxUomTxt : uomTxt;

                                    // 정석 지정 셀 주소에 데이터 삽입
                                    ((Excel.Range)ws.Range($"A{currentExcelRow}")).Value = "";
                                    ((Excel.Range)ws.Range($"B{currentExcelRow}")).Value = charTxt;
                                    ((Excel.Range)ws.Range($"F{currentExcelRow}")).Value = methodTxt;
                                    ((Excel.Range)ws.Range($"J{currentExcelRow}")).Value = lowerTxt; // 👈 블랭크 처리됨
                                    ((Excel.Range)ws.Range($"K{currentExcelRow}")).Value = "";
                                    ((Excel.Range)ws.Range($"L{currentExcelRow}")).Value = upperTxt; // 👈 블랭크 처리됨
                                    ((Excel.Range)ws.Range($"M{currentExcelRow}")).Value = firstTxt; // 👈 블랭크 처리됨
                                    ((Excel.Range)ws.Range($"O{currentExcelRow}")).Value = finalUom;

                                    excelRowOffset++;
                                }
                            }
                        }
                    }
                }

                // ③ 껍데기 Basic 시트 삭제 및 최종 저장 후 자동 열기
                try
                {
                    excelApp.DisplayAlerts = false;
                    ((Excel.Worksheet)wb.Sheets["Basic"])?.Delete();
                }
                catch { }
                finally { excelApp.DisplayAlerts = true; }

                // 🎯 NetOffice 규칙에 맞춰 파일 포맷 번호(51 = xlsx) 지정 및 소괄호 마감
                wb.SaveAs(savePath, 51);
                wb.Close(false);
                wb = null;

                excelApp.Quit();
                excelApp = null;

                // 🎯 4단계 완료 시점에서 생성된 엑셀 파일 바로 화면에 열어주기!
                if (System.IO.File.Exists(savePath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(savePath) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                // 🎯 InnerException까지 싹 다 긁어와서 진짜 배후의 원인을 노출시킴
                string realError = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                MessageBox.Show($"4단계 엑셀 제어 및 매핑 중 오류가 발생했습니다:\n{realError}\n\n[상세 정보]: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                if (wb != null) { wb.Close(false); wb = null; }
                if (excelApp != null) { excelApp.Quit(); excelApp = null; }
            }


            // ===================================================================
            // [5단계] DB 최종 마스터/디테일 저장 및 화면 초기화 (실제 컬럼 스펙 반영)
            // ===================================================================

            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseManager.ActiveConnectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    try
                    {
                        // 🎯 [여기서부터] 첫 번째 행에서 프로젝트 번호를 미리 추출해서 변수 꼬임 방지
                        string masterPjtNo = "";
                        if (rowCount > 0)
                        {
                            dynamic firstRow = TblPreview.Items[0];
                            masterPjtNo = firstRow.PjtNo ?? "";
                        }

                        // ① coa_mstr 마스터 테이블 인서트
                        string syncFlag = DatabaseManager.IsOnline ? "" : "1";
                        string insertMstrQuery = @"
                                INSERT INTO coa_mstr (
                                    coa_no, coa_customer, coa_proj_name, coa_proj_no, 
                                    coa_reg_date, coa_reg_psn, coa_upd_date, coa_upd_psn, coa_char4
                                ) VALUES (
                                    @CoaNo, @CoaCustomer, @CoaProjName, @CoaProjNo, 
                                    @RegDate, 'System', @UpdDate, 'System', @SyncFlag
                                );";

                        using (var mstrCmd = new Microsoft.Data.Sqlite.SqliteCommand(insertMstrQuery, conn, tx))
                        {
                            mstrCmd.Parameters.AddWithValue("@CoaNo", coaMasterNo);
                            mstrCmd.Parameters.AddWithValue("@CoaCustomer", custName);
                            mstrCmd.Parameters.AddWithValue("@CoaProjName", TxtPjt.Text.Trim());
                            mstrCmd.Parameters.AddWithValue("@CoaProjNo", masterPjtNo);
                            mstrCmd.Parameters.AddWithValue("@RegDate", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                            mstrCmd.Parameters.AddWithValue("@UpdDate", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                            mstrCmd.Parameters.AddWithValue("@SyncFlag", syncFlag);
                            mstrCmd.ExecuteNonQuery();
                        }
                        // 🎯 [여기까지] 기존 라인에 대체해서 덮어쓰면 끝!

                        // ② coad_det 디테일 테이블 인서트 (이하 기존 코드 그대로 유지...)

                        // ② coad_det 디테일 테이블 인서트
                        int detLineSeq = 0;
                        for (int idx = 0; idx < rowCount; idx++)
                        {
                            var rowData = TblPreview.Items[idx];
                            dynamic dynamicRow = rowData;
                            string statusVal = dynamicRow.Valid?.ToString().Trim() ?? "";

                            if (statusVal != "O") continue;
                            detLineSeq++; // 1부터 시작하는 라인 순번 (coad_line)

                            string rawItemCode = dynamicRow.BaseItemCode ?? "";
                            string combinedDesc = dynamicRow.Desc ?? "";
                            string batchStr = dynamicRow.BatchNumber ?? "";
                            string colorRaw = dynamicRow.ColorCode ?? "";
                            string qtyStr = dynamicRow.Qty ?? "0";
                            string ltqtyStr = dynamicRow.LtQty ?? "0";
                            string mfDateRaw = dynamicRow.MfDate ?? "";
                            string expDateRaw = dynamicRow.ExpDate ?? "";

                            string insertDetQuery = @"
                                    INSERT INTO coad_det (
                                        coad_no, coad_line, coad_part, coad_desc1, 
                                        coad_batch, coad_color_code, coad_qty, coad_ltqty, 
                                        coad_mf_date, coad_exp_date, coad_char4
                                    ) VALUES (
                                        @CoadNo, @CoadLine, @CoadPart, @CoadDesc1, 
                                        @CoadBatch, @CoadColorCode, @CoadQty, @CoadLtQty, 
                                        @CoadMfDate, @CoadExpDate, @SyncFlag
                                    );";

                            using (var detCmd = new Microsoft.Data.Sqlite.SqliteCommand(insertDetQuery, conn, tx))
                            {
                                detCmd.Parameters.AddWithValue("@CoadNo", coaMasterNo); // 마스터키와 매싱
                                detCmd.Parameters.AddWithValue("@CoadLine", detLineSeq);
                                detCmd.Parameters.AddWithValue("@CoadPart", rawItemCode);
                                detCmd.Parameters.AddWithValue("@CoadDesc1", combinedDesc);
                                detCmd.Parameters.AddWithValue("@CoadBatch", batchStr);
                                detCmd.Parameters.AddWithValue("@CoadColorCode", colorRaw);
                                detCmd.Parameters.AddWithValue("@CoadQty", Convert.ToDouble(qtyStr.Replace(",", "")));
                                detCmd.Parameters.AddWithValue("@CoadLtQty", Convert.ToDouble(ltqtyStr.Replace("LT", "").Replace(",", "").Trim()));
                                detCmd.Parameters.AddWithValue("@CoadMfDate", mfDateRaw);
                                detCmd.Parameters.AddWithValue("@CoadExpDate", expDateRaw);
                                detCmd.Parameters.AddWithValue("@SyncFlag", syncFlag);
                                detCmd.ExecuteNonQuery();
                            }
                        }

                        tx.Commit(); // 최종 승인
                    }
                    catch (Exception dbEx)
                    {
                        tx.Rollback();
                        throw new Exception($"DB 저장 중 오류가 발생하여 롤백되었습니다: {dbEx.Message}");
                    }
                }
            }

            //// ③ 성적서 정상 발행 알림창 출력
            //MessageBox.Show($"성적서 : {coaMasterNo} 가 정상 발행되었습니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);


            // ④ [교정] 화면 UI 싹 비우고 리셋하기
            TxtCust.Text = "";
            TxtPjt.Text = "";
            TxtCoaNo.Text = "";

            // 🎯 와니 아이디어 반영! 1층 입력, 2층 프리뷰, 3층 상세까지 통째로 올킬 청소
            BtnClear_Click(null, null);
        }






        /// <summary>
        /// 🎯 [추가] Clear 버튼 클릭 시 상단 입력 그리드와 하단 프리뷰를 싹 초기화
        /// </summary>
        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            // 상단 입력 데이터 컬렉션 완전히 비우기
            _inputList.Clear();

            // 하단 결과 그리드들도 깨끗하게 초기화
            TblPreview.ItemsSource = null;
            TblDetail.ItemsSource = null;
            TxtCoaNo.Text = "";
        }

        /// <summary>
        /// 입력 그리드에서 Ctrl+V를 누를 때 단일 셀 복사/붙여넣기 및 행 단위 붙여넣기를 모두 지원하는 메서드
        /// </summary>
        private void DgInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true; // WPF 기본 붙여넣기 차단

                string clipboardText = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(clipboardText)) return;

                // 💡 클립보드 데이터가 탭이나 줄바꿈이 없는 완전한 '단일 텍스트'인 경우 단일 셀 수정 처리
                if (!clipboardText.Contains("\t") && !clipboardText.Contains("\n") && !clipboardText.Contains("\r"))
                {
                    if (DgInput.CurrentCell != null && DgInput.CurrentCell.Item is PreviewItem currentItem)
                    {
                        // 🎯 [수정] 현재 진행 중인 DataGrid의 편집 모드를 안전하게 커밋하여 종료
                        DgInput.CommitEdit(DataGridEditingUnit.Cell, true);
                        DgInput.CommitEdit(DataGridEditingUnit.Row, true);

                        string cleanValue = clipboardText.Trim();
                        string header = DgInput.CurrentCell.Column.Header.ToString();

                        switch (header)
                        {
                            case "품목코드": currentItem.BaseItemCode = cleanValue; break;
                            case "배치번호": currentItem.BatchNumber = cleanValue; break;
                            case "수량 (QTY)": currentItem.Qty = cleanValue; break;
                            case "CHS 코드": currentItem.ChsCode = cleanValue; break;
                            case "제조일자": currentItem.MfDate = cleanValue; break;
                            case "컬러코드": currentItem.ColorCode = cleanValue; break;
                            case "PJT 번호": currentItem.PjtNo = cleanValue; break;
                        }

                        // 🎯 [변경] 트랜잭션 충돌을 방지하기 위해 Items.Refresh() 대신 
                        // 소스 바인딩을 리프레시하거나 뷰를 강제 갱신하는 안전한 방법 가동
                        var selectedIndex = DgInput.SelectedIndex;
                        DgInput.ItemsSource = null;
                        DgInput.ItemsSource = _inputList;
                        DgInput.SelectedIndex = selectedIndex;

                        return; // 단일 셀 처리 끝났으므로 탈출!
                    }
                }

                // -------------------------------------------------------------
                // 이 아래는 기존 대량/행 단위 복사 붙여넣기 로직 그대로 유지
                // -------------------------------------------------------------
                string[] lines = clipboardText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

                int startRowIndex = 0;
                if (DgInput.CurrentCell != null && DgInput.CurrentCell.Item != null)
                {
                    startRowIndex = DgInput.Items.IndexOf(DgInput.CurrentCell.Item);
                    if (startRowIndex < 0) startRowIndex = 0;
                }

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    string[] cells = line.Split('\t');
                    if (cells.Length < 2) continue;

                    // 🎯 [교정] 빈 값("")을 하이픈("-")으로 강제 치환하던 독소 로직 해제
                    string baseItemCode = cells.Length > 0 ? cells[0].Trim() : "";
                    string batchNumber = cells.Length > 1 ? cells[1].Trim() : "";
                    string qty = cells.Length > 2 ? cells[2].Trim() : "0";

                    // 비어있으면 하이픈이 아니라 빈 값("")이 유지되도록 수정
                    string chsCode = cells.Length > 3 && !string.IsNullOrEmpty(cells[3]) ? cells[3].Trim() : "";
                    string mfDate = cells.Length > 4 && !string.IsNullOrEmpty(cells[4]) ? cells[4].Trim() : "";
                    string colorCode = cells.Length > 5 && !string.IsNullOrEmpty(cells[5]) ? cells[5].Trim() : "";
                    string pjtNo = cells.Length > 6 && !string.IsNullOrEmpty(cells[6]) ? cells[6].Trim() : "";

                    int targetRowIndex = startRowIndex + i;

                    if (targetRowIndex < _inputList.Count)
                    {
                        var existingItem = _inputList[targetRowIndex];
                        existingItem.BaseItemCode = baseItemCode;
                        existingItem.BatchNumber = batchNumber;
                        existingItem.Qty = qty;
                        existingItem.ChsCode = chsCode;
                        existingItem.MfDate = mfDate;
                        existingItem.ColorCode = colorCode;
                        existingItem.PjtNo = pjtNo;
                    }
                    else
                    {
                        PreviewItem newItem = new PreviewItem
                        {
                            BaseItemCode = baseItemCode,
                            BatchNumber = batchNumber,
                            Qty = qty,
                            ChsCode = chsCode,
                            MfDate = mfDate,
                            ColorCode = colorCode,
                            PjtNo = pjtNo
                        };
                        _inputList.Add(newItem);
                    }
                }

                DgInput.Items.Refresh();
            }
        }
    }

    /// <summary>
    /// 1층 프리뷰 그리드(TblPreview)의 각 열과 1:1로 매칭되는 데이터 모델 클래스 (바깥으로 분리)
    /// </summary>
    public class PreviewItem
    {
        public string Valid { get; set; } = "OK";
        public string ErrorMsg { get; set; } = "-";
        public string BaseItemCode { get; set; } = "";
        public string Desc { get; set; } = "-";
        public string BatchNumber { get; set; } = "";
        public string ChsCode { get; set; } = "-";
        public string ColorCode { get; set; } = "-";
        public string Qty { get; set; } = "0";
        public string LtQty { get; set; } = "-";
        public string MfDate { get; set; } = "-";
        public string Shelf { get; set; } = "-";
        public string ExpDate { get; set; } = "-";
        public string PjtNo { get; set; } = "-";
    }

    /// <summary>
    /// 3층 품질특성 상세 내역 그리드 데이터 모델 클래스
    /// </summary>
    public class DetailItem
    {
        public string No { get; set; } = "";
        public string Characteristic { get; set; } = "";
        public string FirstValue { get; set; } = "";
        public string LastValue { get; set; } = "";
        public string Uom { get; set; } = "";
        public string LowerLimit { get; set; } = "";
        public string UpperLimit { get; set; } = "";
        public string MfDateDetail { get; set; } = "";
        public string ItemCode { get; set; } = "";
    }
}