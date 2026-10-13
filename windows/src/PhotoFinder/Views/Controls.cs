using System.Collections;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PhotoFinder.Views;

/// <summary>
/// A menu item that the arrow keys still reach when its command can't run, as in native Windows menus. WPF skips
/// disabled menu items entirely; this one stays enabled to WPF but looks unavailable, screen readers hear it as
/// unavailable (its UIA IsEnabled is false), and choosing it does nothing.
/// </summary>
public class AppMenuItem : MenuItem
{
    private static readonly DependencyPropertyKey IsAvailablePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsAvailable), typeof(bool), typeof(AppMenuItem),
        new PropertyMetadata(true, (d, e) => ((AppMenuItem)d).AvailabilityChanged((bool)e.OldValue, (bool)e.NewValue)));

    public static readonly DependencyProperty IsAvailableProperty = IsAvailablePropertyKey.DependencyProperty;

    /// <summary>Whether its command can run now. Checked when its menu opens and whenever WPF re-checks commands.</summary>
    public bool IsAvailable => (bool)GetValue(IsAvailableProperty);

    public AppMenuItem()
    {
        // A context menu opening (or a submenu shown) is checked as it appears.
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) UpdateAvailability(); };
    }

    /// <summary>Kept here because commands hold their CanExecuteChanged handlers weakly.</summary>
    private EventHandler? _canExecuteChanged;

    /// <summary>Always enabled to WPF, so the arrow keys stop on it; MenuItem would otherwise disable it with its command.</summary>
    protected override bool IsEnabledCore => true;

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == CommandProperty)
        {
            if (e.OldValue is ICommand old && _canExecuteChanged is not null) old.CanExecuteChanged -= _canExecuteChanged;
            // Only while it's showing: WPF re-checks commands after every key press, and MenuItem itself skips closed
            // menus for the same reason. Opening its menu checks it again.
            if (e.NewValue is ICommand command) command.CanExecuteChanged += _canExecuteChanged ??= (_, _) => { if (IsVisible) UpdateAvailability(); };
            UpdateAvailability();
        }
        else if (e.Property == CommandParameterProperty || e.Property == CommandTargetProperty)
        {
            UpdateAvailability();
        }
    }

    public void UpdateAvailability()
    {
        var available = Command switch
        {
            null => true,
            RoutedCommand routed => routed.CanExecute(CommandParameter, CommandTarget ?? this),
            var command => command.CanExecute(CommandParameter),
        };
        SetValue(IsAvailablePropertyKey, available);
    }

    protected override void OnSubmenuOpened(RoutedEventArgs e)
    {
        foreach (var child in Items.OfType<AppMenuItem>()) child.UpdateAvailability();
        base.OnSubmenuOpened(e);
    }

    protected override void OnClick()
    {
        if (IsAvailable) base.OnClick();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new AppMenuItemPeer(this);

    private void AvailabilityChanged(bool was, bool now)
    {
        if (now) ClearValue(ForegroundProperty);
        else SetResourceReference(ForegroundProperty, SystemColors.GrayTextBrushKey);
        UIElementAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(AutomationElementIdentifiers.IsEnabledProperty, was, now);
    }

    private sealed class AppMenuItemPeer(AppMenuItem owner) : MenuItemAutomationPeer(owner)
    {
        // Also what makes UIA's Invoke refuse it.
        protected override bool IsEnabledCore() => owner.IsAvailable;
    }
}

/// <summary>A picture screen readers skip, because what it shows is said in words beside it: the Description panel's
/// preview (#119), whose description is right below.</summary>
public sealed class SilentImage : Image
{
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}

/// <summary>Shows an element only when its text isn't empty, so status bar sections with nothing to say are skipped.</summary>
public sealed class TextVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

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

/// <summary>The gallery's picture: one focusable element that screen readers know is an image, named with the gallery's
/// labels (#38). The name is read when focus arrives or a screen reader asks (Insert+Tab), but a change isn't raised on
/// every move: moving says the same words already, so they'd be read twice.</summary>
public sealed class GalleryImage : ContentControl
{
    private string _spokenName = "";

    /// <summary>What the picture is read as.</summary>
    public string SpokenName
    {
        get => _spokenName;
        set
        {
            _spokenName = value;
            // WPF raises a name change after the next layout when the name differs from what its peer last read
            // (AutomationPeer.UpdateSubtree). Reading it now leaves nothing to differ, so no change is raised and the
            // move's announcement is all that's said; a screen reader asking still gets the new name.
            UIElementAutomationPeer.FromElement(this)?.GetName();
        }
    }

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
        protected override string GetNameCore() => owner.SpokenName;
        protected override string GetClassNameCore() => "Image";
        protected override bool IsContentElementCore() => true;
        protected override bool IsControlElementCore() => true;
        // Its picture and message are part of it, not separate things to visit.
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
