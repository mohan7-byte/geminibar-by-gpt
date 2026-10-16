using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GeminiBar;

internal sealed class SettingsWindow : Window
{
    private readonly PasswordBox _apiKey;
    private readonly TextBox _textModel;
    private readonly TextBox _liveModel;
    private readonly ComboBox _thinking;
    private readonly Slider _opacity;
    private readonly TextBlock _opacityValue;
    private readonly TextBox _instructions;

    public AppSettings ResultSettings { get; private set; } = new();
    public string ResultApiKey { get; private set; } = "";

    public SettingsWindow(AppSettings settings, string apiKey)
    {
        Title = "GeminiBar Settings";
        Width = 520;
        Height = 720;
        MinWidth = 480;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        Background = new SolidColorBrush(Color.FromRgb(248, 249, 252));
        FontFamily = new FontFamily("Segoe UI");
        ShowInTaskbar = false;
        Topmost = true;

        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(18) };
        Content = root;

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock
        {
            Text = "Settings",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(32, 33, 36))
        });
        header.Children.Add(new TextBlock
        {
            Text = "Your key is stored locally and encrypted for this Windows user.",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(112, 119, 132)),
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 15, 0, 0)
        };
        var cancel = CreateButton("Cancel", false);
        cancel.Click += (_, _) => { DialogResult = false; };
        var save = CreateButton("Save settings", true);
        save.Click += (_, _) => SaveSettings();
        footer.Children.Add(cancel);
        footer.Children.Add(save);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var fields = new StackPanel { Orientation = Orientation.Vertical };
        var scroller = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroller);

        fields.Children.Add(Label("Google Gemini API key"));
        _apiKey = new PasswordBox
        {
            Password = apiKey,
            PasswordChar = '●',
            Padding = new Thickness(10, 8, 10, 8),
            FontSize = 13,
            Margin = new Thickness(0, 4, 0, 10),
            Background = Brushes.White
        };
        fields.Children.Add(_apiKey);

        fields.Children.Add(Label("Text and image model ID"));
        _textModel = CreateTextBox(settings.TextModel, 1);
        fields.Children.Add(_textModel);
        fields.Children.Add(Hint("Example: gemini-3.8-flash. You can type a future model ID here."));

        fields.Children.Add(Label("Gemini Live model ID"));
        _liveModel = CreateTextBox(settings.LiveModel, 1);
        fields.Children.Add(_liveModel);
        fields.Children.Add(Hint("Example: gemini-3.8-live. Live models are separate from text models."));

        fields.Children.Add(Label("Thinking level"));
        _thinking = new ComboBox
        {
            ItemsSource = new[] { "low", "medium", "high" },
            SelectedItem = settings.ThinkingLevel,
            IsEditable = false,
            FontSize = 13,
            Padding = new Thickness(7),
            Margin = new Thickness(0, 4, 0, 10),
            Background = Brushes.White
        };
        fields.Children.Add(_thinking);

        var opacityRow = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
        opacityRow.Children.Add(Label("Floating window opacity"));
        _opacityValue = new TextBlock
        {
            Text = Math.Round(settings.Opacity * 100) + "%",
            Foreground = new SolidColorBrush(Color.FromRgb(80, 86, 99)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        DockPanel.SetDock(_opacityValue, Dock.Right);
        opacityRow.Children.Add(_opacityValue);
        fields.Children.Add(opacityRow);

        _opacity = new Slider
        {
            Minimum = 0.35,
            Maximum = 1.0,
            Value = Math.Clamp(settings.Opacity, 0.35, 1.0),
            TickFrequency = 0.05,
            IsSnapToTickEnabled = false,
            Margin = new Thickness(0, 2, 0, 12)
        };
        _opacity.ValueChanged += (_, _) => _opacityValue.Text = Math.Round(_opacity.Value * 100) + "%";
        fields.Children.Add(_opacity);

        fields.Children.Add(Label("Custom study instructions"));
        _instructions = new TextBox
        {
            Text = settings.Instructions,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 145,
            MaxHeight = 230,
            Padding = new Thickness(10),
            FontSize = 13,
            Margin = new Thickness(0, 4, 0, 10),
            Background = Brushes.White
        };
        fields.Children.Add(_instructions);
        fields.Children.Add(Hint("These instructions are sent with each text request and used to configure Live."));
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Color.FromRgb(60, 65, 77)),
        Margin = new Thickness(0, 9, 0, 0)
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = new SolidColorBrush(Color.FromRgb(130, 136, 149)),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, -4, 0, 7)
    };

    private static TextBox CreateTextBox(string text, int rows) => new()
    {
        Text = text,
        Padding = new Thickness(10, 8, 10, 8),
        FontSize = 13,
        Margin = new Thickness(0, 4, 0, 6),
        Background = Brushes.White,
        AcceptsReturn = rows > 1,
        TextWrapping = TextWrapping.Wrap
    };

    private static Button CreateButton(string text, bool primary)
    {
        return new Button
        {
            Content = text,
            Padding = new Thickness(14, 9, 14, 9),
            Margin = new Thickness(5, 0, 0, 0),
            FontSize = 12,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = primary ? new SolidColorBrush(Color.FromRgb(32, 33, 36)) : Brushes.White,
            Foreground = primary ? Brushes.White : new SolidColorBrush(Color.FromRgb(55, 60, 71)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(221, 226, 235)),
            BorderThickness = new Thickness(1)
        };
    }

    private void SaveSettings()
    {
        if (string.IsNullOrWhiteSpace(_textModel.Text))
        {
            MessageBox.Show(this, "Enter a text model ID.", "GeminiBar", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(_liveModel.Text))
        {
            MessageBox.Show(this, "Enter a Live model ID.", "GeminiBar", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(_apiKey.Password))
        {
            MessageBox.Show(this, "Enter your Gemini API key.", "GeminiBar", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ResultSettings = new AppSettings
        {
            TextModel = _textModel.Text.Trim(),
            LiveModel = _liveModel.Text.Trim(),
            ThinkingLevel = _thinking.SelectedItem?.ToString() ?? "medium",
            Opacity = _opacity.Value,
            Instructions = _instructions.Text.Trim()
        };
        ResultApiKey = _apiKey.Password.Trim();
        DialogResult = true;
    }
}
