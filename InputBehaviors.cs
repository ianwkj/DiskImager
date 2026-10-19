using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DiskImager;

/// <summary>Keyboard rules for text boxes that only accept a time (HH:mm) or a whole number.</summary>
public static class InputBehaviors
{
    // ---------------------------------------------------------------- Time (HH:mm)

    /// <summary>
    /// Digits only, typed in order: 1 → 15 → 15:0 → 15:00. Impossible times can't be entered
    /// (no hour 25, no minute 7x); a first digit of 3–9 means 03–09. Backspace removes the last digit.
    /// Leaving the box completes a partial time: 9 → 09:00, 15 → 15:00, 153 → 15:30.
    /// </summary>
    public static void AttachTime(TextBox box)
    {
        box.MaxLength = 5;
        box.PreviewTextInput += (_, e) =>
        {
            e.Handled = true;
            string digits = AllSelected(box) ? "" : Digits(box.Text);
            foreach (char c in e.Text) digits = AddTimeDigit(digits, c);
            SetTime(box, digits);
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space) e.Handled = true;
            else if (e.Key is Key.Back or Key.Delete)
            {
                e.Handled = true;
                string digits = AllSelected(box) ? "" : Digits(box.Text);
                if (digits.Length > 0) digits = digits[..^1];
                SetTime(box, digits);
            }
        };
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            e.CancelCommand();
            if (e.DataObject.GetData(DataFormats.UnicodeText) is not string pasted) return;
            string digits = "";
            foreach (char c in pasted) digits = AddTimeDigit(digits, c);
            SetTime(box, digits);
        });
        box.GotKeyboardFocus += (_, _) => box.SelectAll();
        box.LostKeyboardFocus += (_, _) => box.Text = CompleteTime(box.Text);
    }

    /// <summary>Adds a typed character to the time digits if it keeps the time valid.</summary>
    public static string AddTimeDigit(string digits, char c)
    {
        if (!char.IsAsciiDigit(c) || digits.Length >= 4) return digits;
        int d = c - '0';
        switch (digits.Length)
        {
            case 0: return d > 2 ? "0" + c : c.ToString();            // 9 → 09
            case 1: return digits[0] == '2' && d > 3 ? digits : digits + c; // hours 00–23
            case 2: return d > 5 ? digits : digits + c;               // minutes 00–59
            default: return digits + c;
        }
    }

    public static string FormatTime(string digits) =>
        digits.Length <= 2 ? digits : digits[..2] + ":" + digits[2..];

    /// <summary>Fills in a partial time: "9" → "09:00", "15" → "15:00", "15:3" → "15:30".</summary>
    public static string CompleteTime(string text)
    {
        string digits = Digits(text);
        if (digits.Length == 0) return "";
        if (digits.Length == 1) digits = "0" + digits;
        return FormatTime(digits.PadRight(4, '0'));
    }

    private static void SetTime(TextBox box, string digits)
    {
        box.Text = FormatTime(digits);
        box.CaretIndex = box.Text.Length;
    }

    // ---------------------------------------------------------------- Whole numbers

    /// <summary>Digits only (typed or pasted), at most <paramref name="maxDigits"/>.</summary>
    public static void AttachNumber(TextBox box, int maxDigits)
    {
        box.MaxLength = maxDigits;
        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
        box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Space) e.Handled = true; };
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is not string pasted || !pasted.Trim().All(char.IsAsciiDigit))
                e.CancelCommand();
        });
        box.GotKeyboardFocus += (_, _) => box.SelectAll();
    }

    // ----------------------------------------------------------------

    private static string Digits(string s) => new(s.Where(char.IsAsciiDigit).ToArray());

    private static bool AllSelected(TextBox box) => box.Text.Length > 0 && box.SelectionLength == box.Text.Length;
}
