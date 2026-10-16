using Markdig;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GeminiBar;

public sealed partial class MainWindow : Window
{
    private const int WmClipboardUpdate = 0x031D;
    private const double ExpandedWidth = 468;
    private const double ExpandedHeight = 690;
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    private AppSettings _settings;
    private string _apiKey = "";
    private readonly List<ChatMessage> _messages = new();
    private Grid _root = null!;
    private Button _compactButton = null!;
    private Border _expandedPanel = null!;
    private Grid _chatHost = null!;
    private TextBox _input = null!;
    private Button _sendButton = null!;
    private Button _stopButton = null!;
    private Button _liveButton = null!;
    private TextBlock _status = null!;
    private TextBlock _imageLabel = null!;
    private Border _imageStrip = null!;
    private WebView2? _browser;
    private byte[]? _pendingImage;
    private string _lastClipboardHash = "";
    private bool _expanded;
    private bool _busy;
    private CancellationTokenSource? _requestCts;
    private HwndSource? _hwndSource;
    private IntPtr _hwnd;

    private ClientWebSocket? _liveSocket;
    private CancellationTokenSource? _liveCts;
    private Channel<string>? _liveOutgoing;
    private WaveIn? _liveInput;
    private WaveOut? _liveOutput;
    private BufferedWaveProvider? _livePlayback;
    private Task? _liveSendTask;
    private Task? _liveReceiveTask;
    private bool _liveActive;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load(out _apiKey);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        FontFamily = new FontFamily("Segoe UI");

        Content = BuildUi();
        ApplyOpacity();

        SourceInitialized += (_, _) => RegisterClipboardListener();
        Loaded += async (_, _) =>
        {
            await InitializeChatBrowserAsync();
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                SetExpanded(true);
                Dispatcher.BeginInvoke(new Action(OpenSettings));
            }
        };
        Closing += (_, _) =>
        {
            if (_hwndSource is not null)
                _hwndSource.RemoveHook(ClipboardWndProc);
            if (_hwnd != IntPtr.Zero)
                RemoveClipboardFormatListener(_hwnd);
            _requestCts?.Cancel();
            _ = StopLiveAsync();
        };

        SetExpanded(false);
    }

    private UIElement BuildUi()
    {
        _root = new Grid();

        _compactButton = MakeButton("G", ToggleExpanded, "#202124", "#202124", Brushes.White);
        _compactButton.FontSize = 22;
        _compactButton.FontWeight = FontWeights.SemiBold;
        _compactButton.Width = 56;
        _compactButton.Height = 56;
        _compactButton.HorizontalAlignment = HorizontalAlignment.Center;
        _compactButton.VerticalAlignment = VerticalAlignment.Center;
        _compactButton.ToolTip = "Open GeminiBar";
        _compactButton.Margin = new Thickness(3);
        _root.Children.Add(_compactButton);

        _expandedPanel = BuildExpandedPanel();
        _expandedPanel.Visibility = Visibility.Collapsed;
        _root.Children.Add(_expandedPanel);
        return _root;
    }

    private Border BuildExpandedPanel()
    {
        var outer = new Border
        {
            Background = Brush("#F8F9FC"),
            BorderBrush = Brush("#E4E7EE"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(10),
            SnapsToDevicePixels = true
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.Child = layout;

        var header = new Grid { Margin = new Thickness(2, 1, 2, 9) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var identity = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(new TextBlock
        {
            Text = "GeminiBar",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#202124")
        });
        identity.Children.Add(new TextBlock
        {
            Text = "Study assistant",
            FontSize = 11,
            Foreground = Brush("#7B8190"),
            Margin = new Thickness(0, 1, 0, 0)
        });
        header.Children.Add(identity);

        var headerButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        headerButtons.Children.Add(MakeButton("Settings", OpenSettings, "#FFFFFF", "#E1E5EC", Brush("#343844"), new Thickness(7, 7, 7, 7)));
        headerButtons.Children.Add(MakeButton("New session", ClearSession, "#FFFFFF", "#E1E5EC", Brush("#343844"), new Thickness(7, 7, 7, 7)));
        headerButtons.Children.Add(MakeButton("—", () => SetExpanded(false), "#FFFFFF", "#E1E5EC", Brush("#343844"), new Thickness(8, 7, 8, 7)));
        headerButtons.Children.Add(MakeButton("×", Close, "#FFFFFF", "#E1E5EC", Brush("#343844"), new Thickness(9, 7, 9, 7)));
        Grid.SetColumn(headerButtons, 1);
        header.Children.Add(headerButtons);
        identity.MouseLeftButtonDown += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        };
        Grid.SetRow(header, 0);
        layout.Children.Add(header);

        var actions = new Grid { Margin = new Thickness(1, 0, 1, 8) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.Children.Add(MakeButton("Use clipboard image", () =>
        {
            if (!TryCaptureClipboardImage(false, true))
                MessageBox.Show(this, "No image was found on the clipboard. Press Win + Shift + S, select the question, then try again.", "GeminiBar", MessageBoxButton.OK, MessageBoxImage.Information);
        }, "#FFFFFF", "#E1E5EC", Brush("#343844"), new Thickness(8, 7, 8, 7)));

        _liveButton = MakeButton("Live", ToggleLiveAsync, "#202124", "#202124", Brushes.White, new Thickness(12, 7, 12, 7));
        Grid.SetColumn(_liveButton, 1);
        actions.Children.Add(_liveButton);

        _status = new TextBlock
        {
            Text = "Ready",
            FontSize = 11,
            Foreground = Brush("#7B8190"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 0, 4, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(_status, 2);
        actions.Children.Add(_status);
        Grid.SetRow(actions, 1);
        layout.Children.Add(actions);

        _imageStrip = new Border
        {
            Background = Brush("#EEF1F7"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(1, 0, 1, 8),
            Visibility = Visibility.Collapsed
        };
        var imageStripContent = new DockPanel();
        var removeImage = MakeButton("Remove", () =>
        {
            _pendingImage = null;
            _lastClipboardHash = "";
            UpdateImageStrip();
        }, "#FFFFFF", "#DDE2EA", Brush("#454B58"), new Thickness(7, 4, 7, 4));
        DockPanel.SetDock(removeImage, Dock.Right);
        imageStripContent.Children.Add(removeImage);
        _imageLabel = new TextBlock
        {
            Text = "Screenshot attached",
            FontSize = 12,
            Foreground = Brush("#454B58"),
            VerticalAlignment = VerticalAlignment.Center
        };
        imageStripContent.Children.Add(_imageLabel);
        _imageStrip.Child = imageStripContent;
        Grid.SetRow(_imageStrip, 2);
        layout.Children.Add(_imageStrip);

        _chatHost = new Grid { Background = Brushes.White };
        _chatHost.Children.Add(new TextBlock
        {
            Text = "Loading chat…",
            Foreground = Brush("#7B8190"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 13
        });
        var chatFrame = new Border
        {
            Background = Brushes.White,
            BorderBrush = Brush("#E8EBF1"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            ClipToBounds = true,
            Child = _chatHost,
            Margin = new Thickness(1, 0, 1, 8)
        };
        Grid.SetRow(chatFrame, 3);
        layout.Children.Add(chatFrame);

        var composer = new Grid { Margin = new Thickness(1, 0, 1, 0) };
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _input = new TextBox
        {
            MinHeight = 48,
            MaxHeight = 116,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 13,
            Padding = new Thickness(11, 9, 11, 9),
            Background = Brushes.White,
            Foreground = Brush("#202124"),
            BorderBrush = Brush("#DDE2EA"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 7, 0),
            ToolTip = "Type your question. Enter sends; Shift + Enter adds a new line."
        };
        _input.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                e.Handled = true;
                await SendCurrentMessageAsync();
            }
        };
        composer.Children.Add(_input);

        var sendStack = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Bottom };
        _sendButton = MakeButton("Send", async () => await SendCurrentMessageAsync(), "#202124", "#202124", Brushes.White, new Thickness(15, 9, 15, 9));
        _stopButton = MakeButton("Stop", () => _requestCts?.Cancel(), "#FFFFFF", "#DDE2EA", Brush("#343844"), new Thickness(15, 8, 15, 8));
        _stopButton.Visibility = Visibility.Collapsed;
        sendStack.Children.Add(_sendButton);
        sendStack.Children.Add(_stopButton);
        Grid.SetColumn(sendStack, 1);
        composer.Children.Add(sendStack);
        Grid.SetRow(composer, 4);
        layout.Children.Add(composer);

        var footnote = new TextBlock
        {
            Text = "Screenshots and questions are sent to Google only when you submit them.",
            FontSize = 10,
            Foreground = Brush("#8A90A0"),
            Margin = new Thickness(4, 7, 4, 0),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(footnote, 5);
        layout.Children.Add(footnote);

        return outer;
    }

    private static Button MakeButton(string text, Action action, string background, string border, Brush foreground, Thickness? padding = null)
    {
        var button = new Button
        {
            Content = text,
            Background = Brush(background),
            BorderBrush = Brush(border),
            BorderThickness = new Thickness(1),
            Foreground = foreground,
            Padding = padding ?? new Thickness(10, 7, 10, 7),
            Margin = new Thickness(2, 0, 2, 0),
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Cursor = Cursors.Hand,
            Focusable = true,
            Template = RoundedButtonTemplate()
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static ControlTemplate RoundedButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.MarginProperty, new Thickness(1));
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, 0.82));
        template.Triggers.Add(hover);
        return template;
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private void ToggleExpanded() => SetExpanded(!_expanded);

    private void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        var work = SystemParameters.WorkArea;
        if (expanded)
        {
            Width = ExpandedWidth;
            Height = ExpandedHeight;
            Left = Math.Max(work.Left + 6, work.Right - ExpandedWidth - 16);
            Top = Math.Max(work.Top + 16, work.Top + 82);
            _compactButton.Visibility = Visibility.Collapsed;
            _expandedPanel.Visibility = Visibility.Visible;
        }
        else
        {
            Width = 64;
            Height = 64;
            Left = Math.Max(work.Left + 4, work.Right - Width - 20);
            Top = Math.Max(work.Top + 10, work.Top + 85);
            _expandedPanel.Visibility = Visibility.Collapsed;
            _compactButton.Visibility = Visibility.Visible;
        }

        ApplyOpacity();
        if (expanded && IsLoaded)
        {
            Activate();
            _input?.Focus();
        }
    }

    private void ApplyOpacity() => Opacity = Math.Clamp(_settings.Opacity, 0.35, 1.0);

    private async Task InitializeChatBrowserAsync()
    {
        try
        {
            _browser = new WebView2();
            _chatHost.Children.Clear();
            _chatHost.Children.Add(_browser);
            await _browser.EnsureCoreWebView2Async();
            _browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _browser.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase))
                    e.Cancel = true;
            };
            RefreshChatHtml();
        }
        catch
        {
            _browser = null;
            _chatHost.Children.Clear();
            _chatHost.Children.Add(new TextBlock
            {
                Text = "The Microsoft Edge WebView2 Runtime is required to render the chat and equations. Install the Evergreen Runtime, then reopen GeminiBar.",
                Foreground = Brush("#6B7280"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(18),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            SetStatus("WebView2 runtime required");
        }
    }

    private void RefreshChatHtml()
    {
        if (_browser?.CoreWebView2 is null)
            return;

        try { _browser.NavigateToString(BuildChatHtml()); }
        catch { SetStatus("Unable to refresh chat view"); }
    }

    private string BuildChatHtml()
    {
        var sb = new StringBuilder();
        sb.Append("""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline' https://cdn.jsdelivr.net; script-src 'unsafe-inline' https://cdn.jsdelivr.net; font-src data: https://cdn.jsdelivr.net;">
<style>
*{box-sizing:border-box}body{margin:0;padding:14px;background:#fff;color:#252833;font-family:Segoe UI,Arial,sans-serif;font-size:13px;line-height:1.55}
#chat{display:flex;flex-direction:column;gap:13px}.bubble{max-width:94%;padding:11px 13px;border-radius:14px;overflow-wrap:anywhere}
.user{align-self:flex-end;background:#EEF1F7;border:1px solid #E3E7EF}.assistant{align-self:flex-start;background:#FAFBFD;border:1px solid #EDF0F5}.error{align-self:flex-start;background:#FFF1F0;border:1px solid #FAD7D4;color:#8B2822}
.kind{font-size:10px;text-transform:uppercase;letter-spacing:.06em;color:#858B98;margin-bottom:4px;font-weight:600}
p{margin:5px 0 8px}p:last-child{margin-bottom:0}h1,h2,h3{line-height:1.3;margin:10px 0 6px;font-weight:650}h1{font-size:18px}h2{font-size:16px}h3{font-size:14px}
pre{background:#F2F4F8;padding:10px;border-radius:8px;overflow-x:auto;font-size:12px}code{font-family:Consolas,monospace;background:#F1F3F7;border-radius:4px;padding:1px 3px}pre code{background:transparent;padding:0}
blockquote{border-left:3px solid #CED5E2;margin:8px 0;padding:3px 10px;color:#606777}table{border-collapse:collapse;width:100%;font-size:12px}td,th{border:1px solid #E1E5EC;padding:5px 7px;text-align:left}th{background:#F4F6F9}
img.attachment{display:block;max-width:100%;max-height:250px;object-fit:contain;margin:8px 0 0;border-radius:8px;border:1px solid #E2E6ED}
ul,ol{padding-left:21px;margin:5px 0}li{margin:3px 0}.empty{color:#9298A5;text-align:center;padding:60px 8px 24px;font-size:13px}
.MathJax{max-width:100%;overflow-x:auto;overflow-y:hidden}
</style>
<script>window.MathJax={tex:{inlineMath:[['\\(','\\)'],['$','$']],displayMath:[['\\[','\\]'],['$$','$$']],processEscapes:true},options:{skipHtmlTags:['script','noscript','style','textarea','pre','code']}};</script>
<script async src="https://cdn.jsdelivr.net/npm/mathjax@3/es5/tex-chtml.js"></script>
</head><body><main id="chat">
""");

        if (_messages.Count == 0)
        {
            sb.Append("<div class='empty'>Ask a question or press Win + Shift + S to capture a question from your PDF.</div>");
        }
        else
        {
            foreach (var message in _messages)
            {
                var cls = message.IsTransient ? "error" : (message.Role == "user" ? "user" : "assistant");
                sb.Append("<article class='bubble ").Append(cls).Append("'><div class='kind'>")
                    .Append(message.IsTransient ? "Notice" : (message.Role == "user" ? "You" : "Gemini"))
                    .Append("</div>");
                if (!string.IsNullOrWhiteSpace(message.Text))
                    sb.Append(Markdown.ToHtml(message.Text, MarkdownPipeline));
                if (message.ImagePng is { Length: > 0 } image)
                    sb.Append("<img class='attachment' alt='Captured question' src='data:image/png;base64,")
                        .Append(Convert.ToBase64String(image)).Append("'>");
                sb.Append("</article>");
            }
        }

        sb.Append("""
</main><script>
function typeset(){if(window.MathJax&&MathJax.typesetPromise){MathJax.typesetPromise().catch(function(){});}}
window.addEventListener('load',function(){typeset();window.scrollTo(0,document.body.scrollHeight);});
setTimeout(function(){typeset();window.scrollTo(0,document.body.scrollHeight);},700);
</script></body></html>
""");
        return sb.ToString();
    }

    private void OpenSettings()
    {
        var dialog = new SettingsWindow(_settings, _apiKey) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        _settings = dialog.ResultSettings;
        _apiKey = dialog.ResultApiKey;
        SettingsStore.Save(_settings, _apiKey);
        ApplyOpacity();
        SetStatus("Settings saved");
        RefreshChatHtml();
    }

    private async Task SendCurrentMessageAsync()
    {
        if (_busy)
            return;

        var text = _input.Text.Trim();
        if (text.Length == 0 && _pendingImage is null)
            return;

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            OpenSettings();
            if (string.IsNullOrWhiteSpace(_apiKey))
                return;
        }

        if (string.IsNullOrWhiteSpace(text) && _pendingImage is not null)
            text = "Please explain the question in this screenshot using my study instructions.";

        var image = _pendingImage;
        _pendingImage = null;
        UpdateImageStrip();
        _input.Clear();

        _messages.Add(new ChatMessage("user", text, image));
        RefreshChatHtml();
        SetBusy(true);
        _requestCts = new CancellationTokenSource();

        try
        {
            SetStatus("Thinking…");
            var response = await GeminiApiClient.GenerateAsync(_messages, _settings, _apiKey, _requestCts.Token);
            _messages.Add(new ChatMessage("assistant", response));
            RefreshChatHtml();
            SetStatus("Ready");
        }
        catch (OperationCanceledException)
        {
            _messages.Add(new ChatMessage("assistant", "Response stopped.", null, true));
            RefreshChatHtml();
            SetStatus("Stopped");
        }
        catch (Exception ex)
        {
            _messages.Add(new ChatMessage("assistant", ex.Message, null, true));
            RefreshChatHtml();
            SetStatus("Request failed");
        }
        finally
        {
            _requestCts.Dispose();
            _requestCts = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _sendButton.IsEnabled = !busy;
        _stopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _sendButton.Opacity = busy ? 0.5 : 1.0;
    }

    private void ClearSession()
    {
        _requestCts?.Cancel();
        if (_liveActive)
            _ = StopLiveAsync();

        _messages.Clear();
        _pendingImage = null;
        _lastClipboardHash = "";
        UpdateImageStrip();
        RefreshChatHtml();
        SetStatus("New session");
        _input.Clear();
    }

    private void SetStatus(string message)
    {
        if (_status is null)
            return;
        _status.Text = message;
    }

    private void UpdateImageStrip()
    {
        if (_imageStrip is null)
            return;
        _imageStrip.Visibility = _pendingImage is null ? Visibility.Collapsed : Visibility.Visible;
        _imageLabel.Text = _pendingImage is null ? "" : "Screenshot attached to next question";
    }

    private void RegisterClipboardListener()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(ClipboardWndProc);
        AddClipboardFormatListener(_hwnd);
    }

    private IntPtr ClipboardWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
            Dispatcher.BeginInvoke(new Action(() => TryCaptureClipboardImage(true, false)));
        return IntPtr.Zero;
    }

    private bool TryCaptureClipboardImage(bool expandWhenCaptured, bool force)
    {
        try
        {
            if (!Clipboard.ContainsImage())
                return false;

            var bitmap = Clipboard.GetImage();
            if (bitmap is null)
                return false;

            byte[] png;
            var scale = Math.Min(1.0, 1800.0 / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
            BitmapSource source = bitmap;
            if (scale < 1.0)
            {
                var transformed = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
                transformed.Freeze();
                source = transformed;
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                png = stream.ToArray();
            }

            var hash = Convert.ToHexString(SHA256.HashData(png));
            if (!force && string.Equals(hash, _lastClipboardHash, StringComparison.Ordinal))
                return false;

            _lastClipboardHash = hash;
            _pendingImage = png;
            UpdateImageStrip();
            SetStatus("Screenshot captured");
            if (expandWhenCaptured)
                SetExpanded(true);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private async void ToggleLiveAsync()
    {
        if (_liveActive)
        {
            await StopLiveAsync();
            return;
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            OpenSettings();
            if (string.IsNullOrWhiteSpace(_apiKey))
                return;
        }

        try
        {
            SetStatus("Connecting to Live…");
            _liveCts = new CancellationTokenSource();
            var token = _liveCts.Token;
            var socket = new ClientWebSocket();
            _liveSocket = socket;

            var key = Uri.EscapeDataString(_apiKey);
            var endpoint = new Uri("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key=" + key);
            await socket.ConnectAsync(endpoint, token);
            var setup = GeminiLiveClient.BuildSetup(_settings);
            var setupBytes = Encoding.UTF8.GetBytes(setup.ToJsonString());
            await socket.SendAsync(new ArraySegment<byte>(setupBytes), WebSocketMessageType.Text, true, token);
            await GeminiLiveClient.WaitForSetupCompleteAsync(socket, token);

            _liveOutgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(12)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
            _liveSendTask = Task.Run(() => GeminiLiveClient.SendLoopAsync(socket, _liveOutgoing.Reader, token));

            _livePlayback = new BufferedWaveProvider(new WaveFormat(24000, 16, 1))
            {
                DiscardOnBufferOverflow = true
            };
            _liveOutput = new WaveOut();
            _liveOutput.Init(_livePlayback);
            _liveOutput.Play();

            _liveInput = new WaveIn
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 100,
                NumberOfBuffers = 3
            };
            _liveInput.DataAvailable += (_, e) =>
            {
                if (_liveOutgoing is null || e.BytesRecorded <= 0)
                    return;
                var chunk = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);
                var packet = new JsonObject
                {
                    ["realtimeInput"] = new JsonObject
                    {
                        ["audio"] = new JsonObject
                        {
                            ["mimeType"] = "audio/pcm;rate=16000",
                            ["data"] = Convert.ToBase64String(chunk)
                        }
                    }
                };
                _liveOutgoing.Writer.TryWrite(packet.ToJsonString());
            };
            _liveInput.StartRecording();
            _liveActive = true;
            _liveButton.Content = "Stop Live";
            SetStatus("Live connected · listening");
            _liveReceiveTask = Task.Run(() => GeminiLiveClient.ReceiveLoopAsync(socket, _livePlayback, SetLiveStatus, token));
        }
        catch (Exception ex)
        {
            await StopLiveAsync();
            SetStatus("Live connection failed");
            MessageBox.Show(this, "Gemini Live could not start.\n\n" + ex.Message, "GeminiBar Live", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SetLiveStatus(string text)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_liveActive)
                SetStatus(text);
        }));
    }

    private async Task StopLiveAsync()
    {
        _liveActive = false;
        try { _liveInput?.StopRecording(); } catch { }
        try { _liveOutgoing?.Writer.TryComplete(); } catch { }
        try { _liveCts?.Cancel(); } catch { }
        try { _liveSocket?.Abort(); } catch { }
        try { _liveSocket?.Dispose(); } catch { }
        try { _liveInput?.Dispose(); } catch { }
        try { _liveOutput?.Stop(); } catch { }
        try { _liveOutput?.Dispose(); } catch { }
        try { _liveCts?.Dispose(); } catch { }
        _liveSocket = null;
        _liveInput = null;
        _liveOutput = null;
        _livePlayback = null;
        _liveCts = null;
        _liveOutgoing = null;
        _liveSendTask = null;
        _liveReceiveTask = null;

        if (_liveButton is not null)
            _liveButton.Content = "Live";
        if (_status is not null)
            SetStatus("Live stopped");
        await Task.CompletedTask;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}

internal sealed record ChatMessage(string Role, string Text, byte[]? ImagePng = null, bool IsTransient = false);

internal sealed class AppSettings
{
    public string TextModel { get; set; } = "gemini-3.8-flash";
    public string LiveModel { get; set; } = "gemini-3.8-live";
    public string ThinkingLevel { get; set; } = "medium";
    public double Opacity { get; set; } = 0.96;
    public string Instructions { get; set; } = "You are my study tutor. Give a useful hint first instead of immediately revealing the full solution. Explain one step at a time when asked, and only reveal the complete solution when I explicitly request it. Render mathematical expressions clearly and be accurate about anything unclear in a screenshot.";
}

internal static class SettingsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GeminiBar-local-key-v1");
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GeminiBar");

    public static AppSettings Load(out string apiKey)
    {
        apiKey = "";
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var settingsPath = Path.Combine(DirectoryPath, "settings.json");
            if (File.Exists(settingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath));
                if (loaded is not null)
                    CopyIntoDefaults(loaded);
                else
                    loaded = new AppSettings();
                var keyPath = Path.Combine(DirectoryPath, "api-key.dat");
                if (File.Exists(keyPath))
                {
                    try
                    {
                        var plain = ProtectedData.Unprotect(File.ReadAllBytes(keyPath), Entropy, DataProtectionScope.CurrentUser);
                        apiKey = Encoding.UTF8.GetString(plain);
                        CryptographicOperations.ZeroMemory(plain);
                    }
                    catch { apiKey = ""; }
                }
                return loaded ?? new AppSettings();
            }

            var newKeyPath = Path.Combine(DirectoryPath, "api-key.dat");
            if (File.Exists(newKeyPath))
            {
                try
                {
                    var plain = ProtectedData.Unprotect(File.ReadAllBytes(newKeyPath), Entropy, DataProtectionScope.CurrentUser);
                    apiKey = Encoding.UTF8.GetString(plain);
                    CryptographicOperations.ZeroMemory(plain);
                }
                catch { }
            }
        }
        catch { }
        return new AppSettings();
    }

    private static AppSettings CopyIntoDefaults(AppSettings s)
    {
        s.TextModel = string.IsNullOrWhiteSpace(s.TextModel) ? "gemini-3.8-flash" : s.TextModel.Trim();
        s.LiveModel = string.IsNullOrWhiteSpace(s.LiveModel) ? "gemini-3.8-live" : s.LiveModel.Trim();
        s.ThinkingLevel = new[] { "low", "medium", "high" }.Contains(s.ThinkingLevel, StringComparer.OrdinalIgnoreCase)
            ? s.ThinkingLevel.ToLowerInvariant() : "medium";
        s.Opacity = Math.Clamp(s.Opacity, 0.35, 1.0);
        s.Instructions ??= "";
        return s;
    }

    public static void Save(AppSettings settings, string apiKey)
    {
        Directory.CreateDirectory(DirectoryPath);
        var normalized = CopyIntoDefaults(settings);
        File.WriteAllText(Path.Combine(DirectoryPath, "settings.json"),
            JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true }));

        var keyPath = Path.Combine(DirectoryPath, "api-key.dat");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (File.Exists(keyPath))
                File.Delete(keyPath);
            return;
        }

        var plain = Encoding.UTF8.GetBytes(apiKey.Trim());
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(keyPath, encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}

internal static class GeminiApiClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public static string BuildGenerateUrl(string model, string apiKey)
    {
        var id = NormalizeModelId(model);
        return "https://generativelanguage.googleapis.com/v1beta/models/" +
               Uri.EscapeDataString(id) + ":generateContent?key=" + Uri.EscapeDataString(apiKey);
    }

    public static string NormalizeModelId(string model)
    {
        var id = (model ?? "").Trim();
        if (id.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            id = id.Substring("models/".Length);
        return id;
    }

    public static JsonObject BuildPayload(IReadOnlyList<ChatMessage> messages, AppSettings settings)
    {
        var contents = new JsonArray();
        foreach (var message in messages.Where(m => !m.IsTransient))
        {
            var parts = new JsonArray();
            if (!string.IsNullOrWhiteSpace(message.Text))
                parts.Add(new JsonObject { ["text"] = message.Text });

            if (message.ImagePng is { Length: > 0 } image)
            {
                parts.Add(new JsonObject
                {
                    ["inlineData"] = new JsonObject
                    {
                        ["mimeType"] = "image/png",
                        ["data"] = Convert.ToBase64String(image)
                    }
                });
            }

            if (parts.Count == 0)
                continue;

            contents.Add(new JsonObject
            {
                ["role"] = message.Role == "user" ? "user" : "model",
                ["parts"] = parts
            });
        }

        var payload = new JsonObject
        {
            ["contents"] = contents,
            ["generationConfig"] = new JsonObject
            {
                ["thinkingConfig"] = new JsonObject
                {
                    ["thinkingLevel"] = (settings.ThinkingLevel ?? "medium").ToUpperInvariant()
                }
            }
        };

        if (!string.IsNullOrWhiteSpace(settings.Instructions))
        {
            payload["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = settings.Instructions })
            };
        }

        return payload;
    }

    public static async Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, AppSettings settings, string apiKey, CancellationToken token)
    {
        var url = BuildGenerateUrl(settings.TextModel, apiKey);
        var payload = BuildPayload(messages, settings);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token);
        var body = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
        {
            var detail = body;
            try
            {
                using var errDoc = JsonDocument.Parse(body);
                if (errDoc.RootElement.TryGetProperty("error", out var error) &&
                    error.TryGetProperty("message", out var errorMessage))
                    detail = errorMessage.GetString() ?? body;
            }
            catch { }
            throw new InvalidOperationException("Gemini API returned HTTP " + (int)response.StatusCode + ": " + detail);
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
        {
            var block = "Gemini returned no answer.";
            if (doc.RootElement.TryGetProperty("promptFeedback", out var feedback))
                block += " " + feedback.ToString();
            throw new InvalidOperationException(block);
        }

        var output = new StringBuilder();
        var first = candidates[0];
        if (first.TryGetProperty("content", out var content) &&
            content.TryGetProperty("parts", out var responseParts))
        {
            foreach (var part in responseParts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var value))
                    output.Append(value.GetString());
            }
        }

        var answer = output.ToString().Trim();
        return answer.Length == 0 ? "The model returned no text. Try another model or simplify the question." : answer;
    }
}

internal static class GeminiLiveClient
{
    public static JsonObject BuildSetup(AppSettings settings)
    {
        var model = GeminiApiClient.NormalizeModelId(settings.LiveModel);
        if (!model.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            model = "models/" + model;

        return new JsonObject
        {
            ["setup"] = new JsonObject
            {
                ["model"] = model,
                ["generationConfig"] = new JsonObject
                {
                    ["responseModalities"] = new JsonArray(JsonValue.Create("AUDIO")),
                    ["speechConfig"] = new JsonObject
                    {
                        ["voiceConfig"] = new JsonObject
                        {
                            ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = "Kore" }
                        }
                    }
                },
                ["systemInstruction"] = new JsonObject
                {
                    ["parts"] = new JsonArray(new JsonObject
                    {
                        ["text"] = settings.Instructions ?? "Help me study and explain one step at a time."
                    })
                },
                ["inputAudioTranscription"] = new JsonObject(),
                ["outputAudioTranscription"] = new JsonObject()
            }
        };
    }

    public static async Task WaitForSetupCompleteAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (!token.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Gemini Live closed the connection before setup completed.");

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;

            using var doc = JsonDocument.Parse(message.ToArray());
            message.SetLength(0);
            var root = doc.RootElement;

            if (root.TryGetProperty("setupComplete", out _))
                return;

            if (root.TryGetProperty("error", out var error))
            {
                var detail = error.TryGetProperty("message", out var value) ? value.GetString() : error.ToString();
                throw new InvalidOperationException("Gemini Live setup failed: " + (detail ?? "unknown error"));
            }
        }

        throw new OperationCanceledException(token);
    }

    public static async Task SendLoopAsync(ClientWebSocket socket, ChannelReader<string> reader, CancellationToken token)
    {
        try
        {
            while (await reader.WaitToReadAsync(token))
            {
                while (reader.TryRead(out var text))
                {
                    if (socket.State != WebSocketState.Open)
                        return;
                    var bytes = Encoding.UTF8.GetBytes(text);
                    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
    }

    public static async Task ReceiveLoopAsync(ClientWebSocket socket, BufferedWaveProvider playback, Action<string> status, CancellationToken token)
    {
        var buffer = new byte[32 * 1024];
        using var message = new MemoryStream();

        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    status("Live connection closed");
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                var bytes = message.ToArray();
                message.SetLength(0);
                using var doc = JsonDocument.Parse(bytes);
                var root = doc.RootElement;

                if (root.TryGetProperty("setupComplete", out _))
                    status("Live connected · listening");

                if (root.TryGetProperty("error", out var error))
                {
                    var text = error.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : error.ToString();
                    status("Live error: " + (text ?? "unknown error"));
                }

                if (root.TryGetProperty("serverContent", out var serverContent))
                {
                    if (serverContent.TryGetProperty("inputTranscription", out var input) &&
                        input.TryGetProperty("text", out var inputText) && !string.IsNullOrWhiteSpace(inputText.GetString()))
                        status("Live · listening: " + Clip(inputText.GetString()!, 44));

                    if (serverContent.TryGetProperty("outputTranscription", out var output) &&
                        output.TryGetProperty("text", out var outputText) && !string.IsNullOrWhiteSpace(outputText.GetString()))
                        status("Live · speaking: " + Clip(outputText.GetString()!, 44));

                    if (serverContent.TryGetProperty("modelTurn", out var turn) &&
                        turn.TryGetProperty("parts", out var parts))
                    {
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (part.TryGetProperty("inlineData", out var inlineData) &&
                                inlineData.TryGetProperty("data", out var data))
                            {
                                try
                                {
                                    var audio = Convert.FromBase64String(data.GetString() ?? "");
                                    if (audio.Length > 0)
                                        playback.AddSamples(audio, 0, audio.Length);
                                }
                                catch (FormatException) { }
                            }
                        }
                    }

                    if (serverContent.TryGetProperty("turnComplete", out var complete) && complete.ValueKind == JsonValueKind.True)
                        status("Live connected · listening");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            status("Live disconnected: " + Clip(ex.Message, 70));
        }
        catch (ObjectDisposedException) { }
        catch (JsonException ex)
        {
            status("Live response parse error: " + Clip(ex.Message, 70));
        }
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text.Substring(0, length - 1) + "…";
}

internal static class SelfTest
{
    public static void Run()
    {
        var url = GeminiApiClient.BuildGenerateUrl("models/gemini-3.8-flash", "demo key");
        if (!url.Contains("/models/gemini-3.8-flash:generateContent?key=demo%20key", StringComparison.Ordinal))
            throw new InvalidOperationException("Model URL generation failed.");

        var messages = new List<ChatMessage>
        {
            new("user", "Explain this", new byte[] { 1, 2, 3 }),
            new("assistant", "Here is a hint."),
            new("assistant", "This temporary error should not be sent.", null, true)
        };
        var payload = GeminiApiClient.BuildPayload(messages, new AppSettings { ThinkingLevel = "low" });
        var contents = payload["contents"]!.AsArray();
        if (contents.Count != 2)
            throw new InvalidOperationException("Conversation context filtering failed.");
        if (contents[0]!["role"]!.GetValue<string>() != "user")
            throw new InvalidOperationException("User role serialization failed.");
        if (contents[0]!["parts"]!.AsArray().Count != 2)
            throw new InvalidOperationException("Screenshot attachment serialization failed.");
        if (payload["generationConfig"]!["thinkingConfig"]!["thinkingLevel"]!.GetValue<string>() != "LOW")
            throw new InvalidOperationException("Thinking level serialization failed.");
        if (payload["systemInstruction"] is null)
            throw new InvalidOperationException("System instruction serialization failed.");

        var live = GeminiLiveClient.BuildSetup(new AppSettings());
        if (!live["setup"]!["model"]!.GetValue<string>().Contains("gemini-3.8-live", StringComparison.Ordinal))
            throw new InvalidOperationException("Live model setup failed.");
    }
}
