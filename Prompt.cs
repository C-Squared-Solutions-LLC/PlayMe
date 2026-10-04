using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PlayMe;

/// <summary>
/// A one-line text prompt. The player panel never takes focus, so it can't
/// host a text box itself - this is an ordinary window that can.
/// </summary>
public static class Prompt
{
    public static string? Ask(string title, string hint, string initial, double left, double top)
    {
        var box = new TextBox
        {
            Text = initial,
            FontSize = 13,
            Padding = new Thickness(6, 4, 6, 4),
            Background = new SolidColorBrush(Color.FromRgb(0x23, 0x25, 0x30)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF7)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF7)),
        };

        var label = new TextBlock
        {
            Text = hint,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9D, 0xA2, 0xB4)),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6),
            TextWrapping = TextWrapping.Wrap,
        };

        var ok = new Button { Content = "Open", Width = 76, Height = 26, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 76, Height = 26, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(label);
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = left,
            Top = top,
            Background = new SolidColorBrush(Color.FromRgb(0x19, 0x1A, 0x21)),
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Content = panel,
        };

        string? result = null;
        ok.Click += (_, _) => { result = box.Text; window.Close(); };
        cancel.Click += (_, _) => window.Close();
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
            window.Activate();
        };
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) window.Close();
        };
        window.ShowDialog();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
