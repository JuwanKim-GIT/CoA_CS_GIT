using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;

namespace QCS
{
    internal static class TestResultDateInput
    {
        internal static bool TryNormalize(string? input, out string formatted)
        {
            formatted = "";
            string s = (input ?? "").Trim();
            if (s.Length == 0) return true;
            string[] patterns = { "yyMMdd", "yyyyMMdd", "yy/MM/dd", "yyyy/MM/dd", "yy-MM-dd", "yyyy-MM-dd", "yy.MM.dd", "yyyy.MM.dd" };
            if (!DateTime.TryParseExact(s, patterns, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return false;
            formatted = d.ToString("yy/MM/dd", CultureInfo.InvariantCulture);
            return true;
        }
        internal static bool NormalizeDatePicker(DatePicker picker)
        {
            string s = picker.Text.Trim();
            if (s.Length == 0) { picker.SelectedDate = null; return true; }
            if (!TryNormalize(s, out var normalized)) return false;
            if (!DateTime.TryParseExact(normalized, "yy/MM/dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return false;
            picker.SelectedDate = date;
            picker.Text = normalized;
            return true;
        }
        internal static void Attach(DatePicker picker)
        {
            picker.LostKeyboardFocus += (s,e) => {
                if (!NormalizeDatePicker(picker))
                    System.Windows.MessageBox.Show("날짜 형식이 올바르지 않습니다. 예: 250101", "날짜 입력", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            };
            picker.KeyDown += (s,e) => { if(e.Key == Key.Enter && NormalizeDatePicker(picker)) picker.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); };
        }
    }
}
