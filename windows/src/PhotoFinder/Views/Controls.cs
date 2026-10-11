using System.Collections;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace PhotoFinder.Views;

/// <summary>A ListView that can replace its whole selection in one step, which ListBox keeps protected.
/// Selecting thousands of rows one at a time raises an event for each.</summary>
public sealed class FileListView : ListView
{
    public void SelectOnly(IList items)
    {
        if (items.Count == 0) UnselectAll();
        else SetSelectedItems(items);
    }
}

/// <summary>The gallery's picture: one focusable element that screen readers know is an image and
/// read by the name it's given (the same name the list uses).</summary>
public sealed class GalleryImage : ContentControl
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
    private readonly TextBlock _message = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 14,
        TextWrapping = TextWrapping.Wrap,
    };

    public GalleryImage()
    {
        _message.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.GrayTextBrushKey);
        var grid = new Grid();
        grid.Children.Add(_image);
        grid.Children.Add(_message);
        Content = grid;
        IsTabStop = true;
    }

    public void Show(ImageSource? source, string message = "")
    {
        _image.Source = source;
        _message.Text = source is null ? message : "";
        // The message isn't a separate element, so screen readers get it as the image's status.
        AutomationProperties.SetItemStatus(this, _message.Text);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GalleryImagePeer(this);

    private sealed class GalleryImagePeer(GalleryImage owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => "Image";
        protected override bool IsContentElementCore() => true;
        protected override bool IsControlElementCore() => true;
        // Its picture and message are part of it, not separate things to visit.
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
