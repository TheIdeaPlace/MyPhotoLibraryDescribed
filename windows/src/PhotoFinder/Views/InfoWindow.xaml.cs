using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoFinder.Models;
using PhotoFinder.Services;

namespace PhotoFinder.Views;

/// <summary>A field as a screen reader hears it: "Camera: Canon EOS R6".</summary>
public sealed record InfoLine(string Label, string Value)
{
    public string Spoken => $"{Label}: {Value}";

    /// <summary>What screen readers fall back to for an item without its own name.</summary>
    public override string ToString() => Spoken;
}

/// <summary>Get Info: the date, location, camera and exposure details, and Image Contents, which describes
/// the photo with Windows' on-device AI and reads any text in it.</summary>
public partial class InfoWindow : Window
{
    private readonly MediaItem _item;
    private readonly Settings _settings;
    private readonly Action _saveSettings;
    private readonly bool _describeNow;
    private AIReadiness? _readiness;
    // Busy flags rather than disabling the buttons: a disabled button that has focus drops it to the window.
    private bool _describing;
    private bool _reading;
    private readonly CancellationTokenSource _closing = new();

    private readonly DescriptionStore? _store;
    private readonly FileRow _row;

    private readonly Func<FileRow, Window, string, Task>? _useAsCaption;

    public InfoWindow(FileRow row, Settings settings, Action saveSettings, bool describeNow, DescriptionStore? store = null,
        Func<FileRow, Window, string, Task>? useAsCaption = null)
    {
        _useAsCaption = useAsCaption;
        _item = row.Item ?? throw new ArgumentException("Get Info needs a photo or video.", nameof(row));
        _settings = settings;
        _saveSettings = saveSettings;
        _describeNow = describeNow;
        _store = store;
        _row = row;
        InitializeComponent();
        Title = $"{_item.Name} Info";
        NameHeading.Text = _item.Name;
        GeneralList.ItemsSource = GeneralFields(row).Select(f => new InfoLine(f.Label, f.Value)).ToList();
        ContentsSection.Visibility = _item.IsVideo ? Visibility.Collapsed : Visibility.Visible;
        KindBox.ItemsSource = WindowsAI.Kinds.Select(k => new KindChoice(k.Id, k.Title)).ToList();
        // Each type has its own description; show the one for the type chosen, if it's been made.
        KindBox.SelectionChanged += (_, _) =>
        {
            if (KindBox.SelectedValue is string kind && _store?.Get(_item, kind) is { } text) ShowDescription(text);
            else
            {
                DescriptionLabel.Visibility = Visibility.Collapsed;
                DescriptionBox.Visibility = Visibility.Collapsed;
                UseAsCaptionButton.Visibility = Visibility.Collapsed;
                DescriptionBox.Text = "";
                StartConversation("");
            }
            UpdateDescribeButton();
        };
        KindBox.SelectedValue = WindowsAI.Kinds.Any(k => k.Id == settings.DescriptionKind) ? settings.DescriptionKind : WindowsAI.Kinds[0].Id;
        // A description already made (automatically, or here before) shows at once.
        if (store?.Get(_item, (string)KindBox.SelectedValue) is { } saved) ShowDescription(saved);
        Loaded += async (_, _) =>
        {
            if (GeneralList.Items.Count > 0) GeneralList.SelectedIndex = 0;
            if (!describeNow) FocusFirstField();
            await Task.WhenAll(LoadDetailsAsync(), CheckAIAsync());
        };
        _row.PropertyChanged += Row_PropertyChanged;
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _row.PropertyChanged -= Row_PropertyChanged;
        };
    }

    /// <summary>A description type in the combo box. Screen readers name each choice from ToString, which on a
    /// record is "KindChoice { Id = ..., Title = ... }" unless it's overridden.</summary>
    private sealed record KindChoice(string Id, string Title)
    {
        public override string ToString() => Title;
    }

    private void FocusFirstField()
    {
        GeneralList.UpdateLayout();
        if (GeneralList.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem first) first.Focus();
        else GeneralList.Focus();
    }

    internal static List<InfoField> GeneralFields(FileRow row)
    {
        var item = row.Item!;
        var m = item.Metadata ?? MediaMetadata.Empty;
        var fields = new List<InfoField>
        {
            new("Name", item.Name),
            new("Kind", row.Kind),
        };
        if (m.DateTaken is { } taken) fields.Add(new("Date Taken", taken.ToString("F", CultureInfo.CurrentCulture)));
        fields.Add(new("Modified", item.Modified.ToString("F", CultureInfo.CurrentCulture)));
        if (m.Width > 0) fields.Add(new("Dimensions", $"{m.Width} × {m.Height} pixels"));
        if (item.IsVideo && m.DurationSeconds > 0) fields.Add(new("Duration", FileRow.FormatDuration(m.DurationSeconds)));
        fields.Add(new("Favorite", item.IsFavorite ? "Yes" : "No"));
        fields.Add(new("Caption", m.Caption.Length > 0 ? m.Caption : "None"));
        if (m.AltText.Length > 0) fields.Add(new("Alt Text in the File", m.AltText));
        if (m.Keywords.Length > 0) fields.Add(new("Keywords", m.Keywords));
        if (m.Rating > 0) fields.Add(new("Rating", FileRow.StarsText(m.Rating)));
        if (m.Latitude is { } lat && m.Longitude is { } lon)
            fields.Add(new("Coordinates", string.Format(CultureInfo.CurrentCulture, "{0:0.00000}, {1:0.00000}", lat, lon)));
        fields.Add(new("File Type", MediaTypes.FileTypeDescription(item.Path)));
        fields.Add(new("Size", FileRow.FormatSize(item.Size)));
        fields.Add(new("Folder", item.Folder));
        if (item.IsOnlineOnly) fields.Add(new("Availability", "Online-only: stored in the cloud, downloaded when opened"));
        return fields;
    }

    private async Task LoadDetailsAsync()
    {
        try
        {
            var path = _item.Path;
            var details = await Task.Run(() => MetadataReader.ReadDetailsAsync(path));
            if (details.Count == 0)
            {
                DetailsStatus.Text = "No additional details available.";
                return;
            }
            DetailsList.ItemsSource = details.Select(f => new InfoLine(f.Label, f.Value)).ToList();
            DetailsList.Visibility = Visibility.Visible;
            DetailsStatus.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            DetailsStatus.Text = $"The details couldn't be read: {ex.Message}";
        }
    }

    // MARK: Describe Image

    private async Task CheckAIAsync()
    {
        if (_item.IsVideo) return;
        AIStatus.Text = "Checking for Windows' image description…";
        try
        {
            _readiness = await WindowsAI.CheckAsync(_closing.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!_readiness.Usable)
        {
            AIStatus.Text = _readiness.Message ?? "Windows' image description isn't available on this PC.";
            DescribePanel.Visibility = Visibility.Collapsed;
            if (_describeNow)
            {
                FocusFirstField();
                Announcer.Announce(AIStatus.Text);
            }
            return;
        }
        AIStatus.Text = "";
        AIStatus.Visibility = Visibility.Collapsed;
        if (_describeNow)
        {
            DescribeButton.Focus();
            // Already described (automatically, or before): say it rather than describe it again.
            if (_store?.Get(_item, (string)KindBox.SelectedValue) is { } saved)
                Announcer.Announce(saved, interrupt: true);
            else
                await DescribeAsync();
        }
    }

    private async void Describe_Click(object sender, RoutedEventArgs e) => await DescribeAsync();

    /// <summary>"Describe Image" when this type has no saved description, "Describe Again" when it has one, which
    /// describing replaces, as on the Mac.</summary>
    private bool HasSaved => KindBox.SelectedValue is string kind && _store?.Get(_item, kind) is not null;

    private void UpdateDescribeButton() => DescribeButton.Content = HasSaved ? "Describe A_gain" : "_Describe Image";

    /// <summary>After describing: renaming the focused button now would have screen readers say its new name over the
    /// description, so it's renamed when focus leaves it.</summary>
    private void UpdateDescribeButtonLater()
    {
        if (!DescribeButton.IsKeyboardFocused)
        {
            UpdateDescribeButton();
            return;
        }
        void Renamed(object sender, KeyboardFocusChangedEventArgs e)
        {
            DescribeButton.LostKeyboardFocus -= Renamed;
            UpdateDescribeButton();
        }
        DescribeButton.LostKeyboardFocus += Renamed;
    }

    private async Task DescribeAsync()
    {
        if (_readiness is not { Usable: true })
        {
            Announcer.Announce(AIStatus.Text.Length > 0 ? AIStatus.Text : "Still checking for Windows' image description.");
            return;
        }
        if (_describing) { Announcer.Announce("Still describing"); return; }
        var kind = KindBox.SelectedValue as string ?? WindowsAI.Kinds[0].Id;
        // A description the person corrected is replaced only if they agree, as with Describe Again.
        if (_store?.IsEdited(_item, kind) == true && MessageBox.Show(this, "Replace the description you edited?", "Describe Again",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _describing = true;
        _settings.DescriptionKind = kind;
        _saveSettings();
        AIStatus.Visibility = Visibility.Visible;
        try
        {
            if (_readiness?.State == "NotReady")
            {
                AIStatus.Text = "Getting Windows' image description model ready. The first time, this can take a few minutes.";
                Announcer.Announce(AIStatus.Text);
                _readiness = await WindowsAI.PrepareAsync(_closing.Token);
                if (_readiness.State != "Ready")
                {
                    AIStatus.Text = _readiness.Message ?? "Windows couldn't get its image description model ready. Try again in a few minutes.";
                    Announcer.Announce(AIStatus.Text);
                    return;
                }
            }
            var again = HasSaved;
            AIStatus.Text = again ? "Describing again…" : "Describing…";
            Announcer.Announce(again ? "Describing again" : "Describing");
            var path = _item.Path;
            var text = await App.WindowsAI.DescribeAsync(async size =>
            {
                using var bitmap = await ImageDecoding.DecodeAsync(path, size);
                return await ImageDecoding.ToJpegAsync(bitmap);
            }, kind, _closing.Token);
            AIStatus.Visibility = Visibility.Collapsed;
            DescriptionLabel.Visibility = Visibility.Visible;
            DescriptionBox.Visibility = Visibility.Visible;
            DescriptionBox.Text = text.Length > 0 ? text : "Windows returned an empty description.";
            // Nothing new to use: the old description stays saved, but the box doesn't show it.
            if (text.Length == 0) UseAsCaptionButton.Visibility = Visibility.Collapsed;
            if (text.Length > 0)
            {
                UseAsCaptionButton.Visibility = _useAsCaption is null ? Visibility.Collapsed : Visibility.Visible;
                StartConversation(text);
                _store?.Put(_item, kind, text);
                _row.Description = text;
                UpdateDescribeButtonLater();
            }
            // Spoken whole, with focus left where it is, as the Mac app does: moving focus would cut the
            // speech off. The description box is next in Tab order for reading it line by line.
            Announcer.Announce(DescriptionBox.Text, interrupt: true);
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
        catch (DescriptionException ex)
        {
            AIStatus.Text = ex.Message;
            Announcer.Announce(ex.Message, interrupt: true);
        }
        catch (Exception ex)
        {
            AIStatus.Text = $"Windows couldn't describe this photo: {ex.Message}";
            Announcer.Announce(AIStatus.Text, interrupt: true);
        }
        finally
        {
            _describing = false;
        }
    }

    private void ShowDescription(string text)
    {
        DescriptionLabel.Visibility = Visibility.Visible;
        DescriptionBox.Visibility = Visibility.Visible;
        DescriptionBox.Text = text;
        UseAsCaptionButton.Visibility = _useAsCaption is null ? Visibility.Collapsed : Visibility.Visible;
        StartConversation(text);
    }

    // MARK: Follow-up questions (#24)

    /// <summary>Whether the question box is offered: <see cref="AppInfo.FollowUpQuestions"/>, replaceable in tests.</summary>
    internal static bool FollowUpQuestionsShown { get; set; } = AppInfo.FollowUpQuestions;

    /// <summary>Asks Windows' text model; replaceable in tests.</summary>
    internal static Func<string, string, string, IReadOnlyList<(string Question, string Answer)>, CancellationToken, Task<string>> AskQuestion { get; set; } =
        (description, text, question, history, cancel) => App.WindowsAI.AskAsync(description, text, question, history, cancel);

    /// <summary>The description the questions are about, and what's been asked so far. A new description starts over.</summary>
    private string _conversationAbout = "";
    private readonly List<(string Question, string Answer)> _exchanges = [];
    private bool _asking;

    private void StartConversation(string description)
    {
        if (description == _conversationAbout) return;
        _conversationAbout = description;
        _exchanges.Clear();
        AnswersBox.Text = "";
        AnswersLabel.Visibility = Visibility.Collapsed;
        AnswersBox.Visibility = Visibility.Collapsed;
        AskStatus.Visibility = Visibility.Collapsed;
        AskPanel.Visibility = description.Length > 0 && FollowUpQuestionsShown ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Ask_Click(object sender, RoutedEventArgs e) => await AskAsync();

    private async void QuestionBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        await AskAsync();
    }

    /// <summary>Asks the question in the box about the description showing. The answer is spoken whole and added to the
    /// answers, and the box is cleared for the next question, with focus left there.</summary>
    internal async Task AskAsync()
    {
        var question = QuestionBox.Text.Trim();
        if (question.Length == 0)
        {
            Announcer.Announce("Type a question first");
            return;
        }
        if (_asking)
        {
            Announcer.Announce("Still answering");
            return;
        }
        var description = _conversationAbout;
        var text = OcrBox.Visibility == Visibility.Visible && OcrBox.Text != "No text found" ? OcrBox.Text : "";
        _asking = true;
        AskStatus.Visibility = Visibility.Visible;
        AskStatus.Text = "Asking Windows…";
        Announcer.Announce("Asking");
        try
        {
            var answer = await AskQuestion(description, text, question, _exchanges.ToList(), _closing.Token);
            if (description != _conversationAbout) return;  // described again meanwhile: about a different description
            if (answer.Length == 0) answer = "Windows returned an empty answer.";
            else _exchanges.Add((question, answer));
            AskStatus.Visibility = Visibility.Collapsed;
            AnswersLabel.Visibility = Visibility.Visible;
            AnswersBox.Visibility = Visibility.Visible;
            AnswersBox.AppendText((AnswersBox.Text.Length > 0 ? Environment.NewLine + Environment.NewLine : "") + $"You asked: {question}{Environment.NewLine}Answer: {answer}");
            AnswersBox.ScrollToEnd();
            QuestionBox.Clear();
            Announcer.Announce(answer, interrupt: true);
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
        catch (DescriptionException ex)
        {
            AskStatus.Text = ex.Message;
            Announcer.Announce(ex.Message, interrupt: true);
        }
        catch (Exception ex)
        {
            AskStatus.Text = $"Windows couldn't answer: {ex.Message}";
            Announcer.Announce(AskStatus.Text, interrupt: true);
        }
        finally
        {
            _asking = false;
        }
    }

    /// <summary>Use as Caption: the saved description of the chosen type becomes the photo's caption, the same as
    /// Photo > Use Description as Caption, which uses the type chosen here.</summary>
    private async void UseAsCaption_Click(object sender, RoutedEventArgs e)
    {
        if (_useAsCaption is null) return;
        // The type chosen here, without changing what's used elsewhere.
        await _useAsCaption(_row, this, KindBox.SelectedValue as string ?? _settings.DescriptionKind);
    }

    /// <summary>The General list again after the row changed (a caption written here, or from a description), keeping
    /// the line you were on, so its Caption isn't out of date.</summary>
    private void Row_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("" or nameof(FileRow.Caption))) return;
        var index = GeneralList.SelectedIndex;
        var hadFocus = GeneralList.IsKeyboardFocusWithin;
        GeneralList.ItemsSource = GeneralFields(_row).Select(f => new InfoLine(f.Label, f.Value)).ToList();
        if (index < 0) return;
        GeneralList.SelectedIndex = Math.Min(index, GeneralList.Items.Count - 1);
        if (hadFocus && GeneralList.ItemContainerGenerator.ContainerFromIndex(GeneralList.SelectedIndex) is ListBoxItem item) item.Focus();
    }

    // MARK: Read text

    private async void ReadText_Click(object sender, RoutedEventArgs e)
    {
        if (_reading) { Announcer.Announce("Still reading"); return; }
        _reading = true;
        OcrStatus.Visibility = Visibility.Visible;
        OcrStatus.Text = "Reading text…";
        try
        {
            var lines = await TextRecognition.ReadLinesAsync(_item.Path);
            OcrStatus.Visibility = Visibility.Collapsed;
            OcrLabel.Visibility = Visibility.Visible;
            OcrBox.Visibility = Visibility.Visible;
            OcrBox.Text = lines.Count == 0 ? "No text found" : string.Join(Environment.NewLine, lines);
            Announcer.Announce(lines.Count == 0 ? "No text found" : $"Text: {string.Join(" ", lines)}", interrupt: true);
        }
        catch (Exception ex)
        {
            OcrStatus.Text = $"The text couldn't be read: {ex.Message}";
            Announcer.Announce(OcrStatus.Text, interrupt: true);
        }
        finally
        {
            _reading = false;
        }
    }

    private void FieldList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && sender is ListBox { SelectedItem: InfoLine line })
        {
            try
            {
                Clipboard.SetText(line.Value);
                Announcer.Announce($"Copied {line.Label}");
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // The clipboard was busy; pressing again works.
            }
            e.Handled = true;
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
