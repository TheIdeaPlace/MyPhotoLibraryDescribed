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

    public InfoWindow(FileRow row, Settings settings, Action saveSettings, bool describeNow, DescriptionStore? store = null)
    {
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
                DescriptionBox.Text = "";
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
        Closed += (_, _) => _closing.Cancel();
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

    private void UpdateDescribeButton() => DescribeButton.Content = HasSaved ? "Describe _Again" : "_Describe Image";

    private async Task DescribeAsync()
    {
        if (_readiness is not { Usable: true })
        {
            Announcer.Announce(AIStatus.Text.Length > 0 ? AIStatus.Text : "Still checking for Windows' image description.");
            return;
        }
        if (_describing) { Announcer.Announce("Still describing"); return; }
        _describing = true;
        var kind = KindBox.SelectedValue as string ?? WindowsAI.Kinds[0].Id;
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
            if (text.Length > 0)
            {
                _store?.Put(_item, kind, text);
                _row.Description = text;
                UpdateDescribeButton();
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
