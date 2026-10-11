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
            if (e.NewValue is ICommand command) command.CanExecuteChanged += _canExecuteChanged ??= (_, _) => UpdateAvailability();
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
