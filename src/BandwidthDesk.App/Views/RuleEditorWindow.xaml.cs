using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BandwidthDesk.App.Services;
using BandwidthDesk.Core.Models;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace BandwidthDesk.App.Views;

public partial class RuleEditorWindow : Window
{
    private readonly BandwidthRule _draft;
    private readonly IReadOnlyList<BandwidthRule> _existingRules;
    private readonly RateUnit _defaultUnit;
    public BandwidthRule? Result { get; private set; }

    // Order is significant: indexes into Units[] are persisted in unit ComboBoxes,
    // and the index used as default in ApplyValueAndUnit() depends on RateUnit.
    private static readonly (string Label, long Multiplier, RateUnit Unit)[] Units =
    {
        ("KB/s", 1024L, RateUnit.KBps),
        ("MB/s", 1024L * 1024, RateUnit.MBps),
        ("B/s",  1L, RateUnit.Bps),
    };

    public RuleEditorWindow(BandwidthRule draft)
        : this(draft, Array.Empty<BandwidthRule>(), RateUnit.KBps)
    {
    }

    public RuleEditorWindow(BandwidthRule draft, IReadOnlyList<BandwidthRule> existingRules, RateUnit defaultUnit)
    {
        InitializeComponent();
        _draft = draft;
        _existingRules = existingRules;
        _defaultUnit = defaultUnit;

        NameBox.Text = draft.Name;
        EnabledBox.IsChecked = draft.Enabled;

        foreach (ComboBoxItem item in MatchKindBox.Items)
        {
            if ((string)item.Tag == draft.MatchKind.ToString())
            {
                MatchKindBox.SelectedItem = item;
                break;
            }
        }
        if (MatchKindBox.SelectedItem is null) MatchKindBox.SelectedIndex = 0;
        MatchValueBox.Text = draft.MatchValue;

        foreach (var (label, _, _) in Units)
        {
            DownloadUnitBox.Items.Add(label);
            UploadUnitBox.Items.Add(label);
        }
        ApplyValueAndUnit(DownloadValueBox, DownloadUnitBox, draft.DownloadBytesPerSecond);
        ApplyValueAndUnit(UploadValueBox, UploadUnitBox, draft.UploadBytesPerSecond);

        WindowChrome.ApplyTheme(this, ThemeManager.Current);
    }

    private void ApplyValueAndUnit(WpfTextBox valueBox, WpfComboBox unitBox, long bps)
    {
        if (bps <= 0)
        {
            valueBox.Text = "0";
            unitBox.SelectedIndex = IndexOfUnit(_defaultUnit);
            return;
        }
        // Pick the largest unit that yields a clean (>=1) value.
        for (int i = 1; i >= 0; i--)
        {
            long mult = Units[i].Multiplier;
            if (bps >= mult)
            {
                valueBox.Text = (bps / (double)mult).ToString("0.###", CultureInfo.InvariantCulture);
                unitBox.SelectedIndex = i;
                return;
            }
        }
        valueBox.Text = bps.ToString(CultureInfo.InvariantCulture);
        unitBox.SelectedIndex = 2;
    }

    private static int IndexOfUnit(RateUnit unit)
    {
        for (int i = 0; i < Units.Length; i++)
            if (Units[i].Unit == unit) return i;
        return 0;
    }

    private void MatchKindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MatchValueLabel is null) return;
        if (MatchKindBox.SelectedItem is ComboBoxItem item)
        {
            MatchValueLabel.Text = (string)item.Tag switch
            {
                "ExecutableName" => "Executable name (e.g. chrome.exe)",
                "ProcessId" => "Process id (e.g. 1234)",
                "ExecutablePath" => "Full executable path",
                _ => "Value",
            };
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        try
        {
            var kind = Enum.Parse<RuleMatchKind>((string)((ComboBoxItem)MatchKindBox.SelectedItem!).Tag);
            var value = MatchValueBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                ErrorText.Text = "Match value is required.";
                return;
            }
            if (kind == RuleMatchKind.ProcessId && !int.TryParse(value, out _))
            {
                ErrorText.Text = "Process id must be an integer.";
                return;
            }

            // Disallow duplicate rules (same MatchKind + MatchValue). A rule editing itself is fine.
            var dup = _existingRules.FirstOrDefault(r =>
                r.Id != _draft.Id &&
                r.MatchKind == kind &&
                string.Equals(r.MatchValue?.Trim(), value, MatchValueComparison(kind)));
            if (dup is not null)
            {
                ErrorText.Text = $"A rule already exists for this {DescribeKind(kind)} ('{dup.Name}'). Edit that rule instead.";
                return;
            }

            long down = ParseRate(DownloadValueBox.Text, DownloadUnitBox.SelectedIndex);
            long up = ParseRate(UploadValueBox.Text, UploadUnitBox.SelectedIndex);
            if (down < 0 || up < 0)
            {
                ErrorText.Text = "Limits must be non-negative numbers.";
                return;
            }

            _draft.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? value : NameBox.Text.Trim();
            _draft.MatchKind = kind;
            _draft.MatchValue = value;
            _draft.DownloadBytesPerSecond = down;
            _draft.UploadBytesPerSecond = up;
            _draft.Enabled = EnabledBox.IsChecked == true;
            _draft.UpdatedUtc = DateTime.UtcNow;

            Result = _draft;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private static StringComparison MatchValueComparison(RuleMatchKind kind) => kind switch
    {
        RuleMatchKind.ProcessId => StringComparison.Ordinal,
        _ => StringComparison.OrdinalIgnoreCase,
    };

    private static string DescribeKind(RuleMatchKind kind) => kind switch
    {
        RuleMatchKind.ExecutableName => "executable name",
        RuleMatchKind.ProcessId => "process id",
        RuleMatchKind.ExecutablePath => "executable path",
        _ => "value",
    };

    private static long ParseRate(string text, int unitIndex)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
                return -1;
        }
        if (double.IsNaN(v) || double.IsInfinity(v)) return -1;
        if (v < 0) return -1;
        if (v == 0) return 0;

        if (unitIndex < 0 || unitIndex >= Units.Length) unitIndex = 0;
        double bytesPerSecond = v * Units[unitIndex].Multiplier;
        if (double.IsInfinity(bytesPerSecond) || bytesPerSecond > long.MaxValue) return -1;
        return (long)Math.Round(bytesPerSecond);
    }

    // Numeric-only input: digits + a single decimal separator.
    private void NumericOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not WpfTextBox tb) { e.Handled = true; return; }
        e.Handled = !IsValidNumericInsertion(tb, e.Text);
    }

    private void NumericOnly_OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not WpfTextBox tb) { e.CancelCommand(); return; }
        if (!e.SourceDataObject.GetDataPresent(System.Windows.DataFormats.UnicodeText, true))
        {
            e.CancelCommand();
            return;
        }
        var text = (string)e.SourceDataObject.GetData(System.Windows.DataFormats.UnicodeText, true);
        if (!IsValidNumericInsertion(tb, text))
            e.CancelCommand();
    }

    private static bool IsValidNumericInsertion(WpfTextBox tb, string insertion)
    {
        if (string.IsNullOrEmpty(insertion)) return false;
        var current = tb.Text ?? string.Empty;
        int selStart = tb.SelectionStart;
        int selLen = tb.SelectionLength;
        var candidate = current.Remove(selStart, selLen).Insert(selStart, insertion);

        // Allow empty result through (caller will treat as 0), but everything inserted must be numeric.
        var sep = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        int dots = 0;
        foreach (var ch in candidate)
        {
            if (ch >= '0' && ch <= '9') continue;
            if (ch == '.' || ch == ',' || (sep.Length == 1 && ch == sep[0])) { dots++; continue; }
            return false;
        }
        return dots <= 1;
    }
}
