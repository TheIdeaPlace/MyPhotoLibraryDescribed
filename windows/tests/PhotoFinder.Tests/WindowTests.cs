using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PhotoFinder.Models;
using PhotoFinder.Services;
using PhotoFinder.ViewModels;
using PhotoFinder.Views;

namespace PhotoFinder.Tests;

/// <summary>The real windows, shown without activating them, so they run on any machine without
/// taking focus from whatever the person is doing.</summary>
[Collection(SharedState.Name)]
public sealed class WindowTests : IDisposable
{
    private readonly TestLibrary _lib = new();

    public void Dispose() => _lib.Dispose();

    /// <summary>Lets the dispatcher run until <paramref name="done"/> or the timeout.</summary>
    private static async Task<bool> WaitFor(Func<bool> done, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            if (DateTime.UtcNow > until) return false;
            await Dispatcher.Yield(DispatcherPriority.Background);
            await Task.Delay(50);
        }
        return true;
    }

    private static FileRow RowFor(string path, MediaMetadata? metadata = null)
    {
        var info = new FileInfo(path);
        return FileRow.ForItem(new MediaItem(path, info.Length, info.CreationTime, info.LastWriteTime) { Metadata = metadata });
    }

    [WpfFact]
    public async Task InfoWindow_ListsGeneralAndDetails_EachReadAsLabelAndValue()
    {
        var path = _lib.AddJpeg("a.jpg", new TestLibrary.JpegOptions { CameraMake = "Canon", CameraModel = "EOS R6" });
        var row = RowFor(path, new MediaMetadata { Width = 64, Height = 48, Caption = "Hello", Rating = 5 });
        var window = new InfoWindow(row, new Settings(), () => { }, describeNow: false) { ShowActivated = false };
        window.Show();
        try
        {
            var general = ((IEnumerable<InfoLine>)window.GeneralList.ItemsSource).ToList();
            Assert.Contains(new InfoLine("Caption", "Hello"), general);
            Assert.Contains(new InfoLine("Favorite", "Yes"), general);
            Assert.Contains(new InfoLine("Dimensions", "64 × 48 pixels"), general);
            Assert.True(await WaitFor(() => window.DetailsList.Visibility == Visibility.Visible));
            var details = ((IEnumerable<InfoLine>)window.DetailsList.ItemsSource).ToList();
            Assert.Contains(new InfoLine("Camera", "Canon EOS R6"), details);

            window.GeneralList.UpdateLayout();
            var first = (ListBoxItem)window.GeneralList.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.Equal("Name: a.jpg", AutomationProperties.GetName(first));

            // The description types read as their names, not as the objects behind them.
            window.KindBox.IsDropDownOpen = true;
            window.KindBox.UpdateLayout();
            var kinds = window.KindBox.Items.Cast<object>()
                .Select(i => (ComboBoxItem)window.KindBox.ItemContainerGenerator.ContainerFromItem(i))
                .Select(c => System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(c).GetName()).ToList();
            window.KindBox.IsDropDownOpen = false;
            Assert.Equal(["Accessible", "Detailed", "Brief", "Diagram"], kinds);
            Assert.Equal("Accessible", window.KindBox.SelectedItem?.ToString());
        }
        finally
        {
            window.Close();
        }
    }

    [WpfFact]
    public async Task InfoWindow_ForAVideo_HasNoImageContents()
    {
        var path = _lib.AddFile("clip.mp4", "not really a video");
        var window = new InfoWindow(RowFor(path), new Settings(), () => { }, describeNow: false) { ShowActivated = false };
        window.Show();
        try
        {
            Assert.Equal(Visibility.Collapsed, window.ContentsSection.Visibility);
            Assert.True(await WaitFor(() => window.DetailsStatus.Text != "Reading details…"));
        }
        finally
        {
            window.Close();
        }
    }

    [WpfFact]
    public async Task InfoWindow_ReadText_ShowsTheTextAndAnnouncesIt()
    {
        Assert.SkipUnless(TextRecognition.IsAvailable, "No OCR language is installed on this machine.");
        var announced = new List<string>();
        Announcer.Observer = announced.Add;
        var path = _lib.AddJpeg("sign.jpg", new TestLibrary.JpegOptions { Text = "OPEN DAILY" });
        var window = new InfoWindow(RowFor(path), new Settings(), () => { }, describeNow: false) { ShowActivated = false };
        window.Show();
        try
        {
            window.ReadTextButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(await WaitFor(() => window.OcrBox.Visibility == Visibility.Visible, 30));
            Assert.Contains("OPEN DAILY", window.OcrBox.Text);
            Assert.Contains(announced, a => a.Contains("OPEN DAILY"));
        }
        finally
        {
            window.Close();
            Announcer.Observer = null;
        }
    }

    [WpfFact]
    public void InfoWindow_DescribeButton_SaysDescribeAgain_WhenThisTypeHasASavedDescription()
    {
        var path = _lib.AddJpeg("a.jpg");
        var row = RowFor(path);
        var store = new DescriptionStore(_lib.PathOf(@"data\Descriptions.json"));
        store.Put(row.Item!, "brief", "A short one.");
        var window = new InfoWindow(row, new Settings { DescriptionKind = "accessible" }, () => { }, describeNow: false, store) { ShowActivated = false };
        window.Show();
        try
        {
            Assert.Equal("_Describe Image", window.DescribeButton.Content);
            window.KindBox.SelectedValue = "brief";
            Assert.Equal("Describe A_gain", window.DescribeButton.Content);
            Assert.Equal("A short one.", window.DescriptionBox.Text);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Describe Image, end to end through the window, the helper and Windows' model. Runs only where
    /// the helper is installed and the model is ready.</summary>
    [WpfFact]
    public async Task InfoWindow_DescribeNow_DescribesThePhotoAndRemembersTheKind()
    {
        Assert.SkipUnless(WindowsAI.IsInstalled, "The Photo Finder AI helper isn't installed here.");
        var readiness = await WindowsAI.CheckAsync();
        Assert.SkipUnless(readiness.State == "Ready", $"Windows' model isn't ready here: {readiness.State}");

        var announced = new List<string>();
        Announcer.Observer = announced.Add;
        var path = _lib.AddJpeg("sign.jpg", new TestLibrary.JpegOptions { Text = "STOP" });
        var settings = new Settings { DescriptionKind = "brief" };
        var saved = 0;
        var window = new InfoWindow(RowFor(path), settings, () => saved++, describeNow: true) { ShowActivated = false };
        window.Show();
        try
        {
            Assert.Equal("brief", window.KindBox.SelectedValue);
            Assert.True(await WaitFor(() => window.DescriptionBox.Visibility == Visibility.Visible || window.AIStatus.Text.Contains("couldn't"), 120),
                $"No description; status: {window.AIStatus.Text}");
            Assert.False(string.IsNullOrWhiteSpace(window.DescriptionBox.Text), window.AIStatus.Text);
            Assert.Contains(window.DescriptionBox.Text, announced);
            Assert.True(saved > 0);
        }
        finally
        {
            window.Close();
            Announcer.Observer = null;
        }
    }

    [WpfFact]
    public async Task ThumbnailCache_DropsRequestsNoLongerWanted()
    {
        var cache = new ThumbnailCache();
        var calls = 0;
        cache.Request(@"C:\no\such\file.jpg", _ => calls++, stillWanted: () => false);
        cache.Request(@"C:\no\such\other.jpg", _ => calls++, stillWanted: () => true);
        Assert.True(await WaitFor(() => calls == 1));
        await Task.Delay(300);
        Assert.Equal(1, calls);
    }

    [WpfFact]
    public void GalleryImage_SaysItsMessageAsItsStatus()
    {
        var image = new GalleryImage();
        image.Show(null, "This picture can't be shown.");
        Assert.Equal("This picture can't be shown.", AutomationProperties.GetItemStatus(image));
        image.Show(new System.Windows.Media.Imaging.WriteableBitmap(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null));
        Assert.Equal("", AutomationProperties.GetItemStatus(image));
    }

    [WpfFact]
    public void CaptionDialog_StartsWithTheCaption_AndWarnsWhenChangingSeveral()
    {
        var one = new CaptionDialog(1, "Old caption") { ShowActivated = false };
        Assert.Equal("Edit Caption", one.Title);
        Assert.Equal("Old caption", one.Caption);
        Assert.Equal(Visibility.Collapsed, one.ManyNote.Visibility);

        var many = new CaptionDialog(3, "") { ShowActivated = false };
        Assert.Equal("Edit Caption for 3 Items", many.Title);
        Assert.Equal(Visibility.Visible, many.ManyNote.Visibility);
        Assert.Equal("This replaces the caption on all 3 items.", AutomationProperties.GetHelpText(many.CaptionBox));
    }

    [WpfFact]
    public async Task PreviewWindow_MovesThroughItems_AndStopsAtTheEnds()
    {
        var rows = new[] { RowFor(_lib.AddJpeg("a.jpg")), RowFor(_lib.AddJpeg("b.jpg")) };
        var preview = new PreviewWindow(rows, 1) { ShowActivated = false };
        preview.Show();
        try
        {
            Assert.Equal("b.jpg - Preview", preview.Title);
            Assert.Equal("2 of 2", preview.PositionText.Text);
            Assert.False(preview.NextButton.IsEnabled);
            preview.PreviousButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal("a.jpg - Preview", preview.Title);
            Assert.Equal(rows[0], preview.CurrentRow);
            Assert.StartsWith("a.jpg, Photo", AutomationProperties.GetName(preview.Picture));
            Assert.True(await WaitFor(() => preview.Picture.Content is Grid g && g.Children.OfType<Image>().Single().Source is not null));
        }
        finally
        {
            preview.Close();
        }
    }

    /// <summary>Every element Tab visits, in order, from WPF's own Tab logic. WPF keeps it internal, and real
    /// key presses would need the window in front, taking focus from whatever the person is doing.</summary>
    private static List<DependencyObject> TabOrder(Window window)
    {
        var navigation = typeof(FrameworkElement).Assembly.GetType("System.Windows.Input.KeyboardNavigation")!;
        var current = navigation.GetProperty("Current", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        var getNextTab = navigation.GetMethod("GetNextTab", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            [typeof(DependencyObject), typeof(DependencyObject), typeof(bool)])!;
        // What Tab does (KeyboardNavigation.Navigate): the next tab stop after the focused element, within that
        // element's own group (a container with its own TabNavigation, like a list), not the whole window.
        var groupParent = navigation.GetMethod("GetGroupParent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            [typeof(DependencyObject), typeof(bool)])!;
        // Which navigation is happening (Tab, Ctrl+Tab or arrows) is state Navigate sets first; groups are found by it.
        navigation.GetField("_navigationProperty", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(current, KeyboardNavigation.TabNavigationProperty);
        var order = new List<DependencyObject>();
        DependencyObject? e = null;
        for (var i = 0; i < 200; i++)
        {
            // includeCurrent: true, as Navigate does, so a focused group with tab stops inside is entered.
            var container = e is null ? window : (DependencyObject)groupParent.Invoke(current, [e, true])!;
            e = (DependencyObject?)getNextTab.Invoke(current, [e, container, false]);
            if (e is null || order.Contains(e)) break;
            order.Add(e);
        }
        return order;
    }

    [WpfFact]
    public async Task MainWindow_ShowsTheLibrary_WithNamedListAndRows()
    {
        _lib.AddJpeg(@"Trip\a.jpg", new TestLibrary.JpegOptions { Title = "Beach", DateTaken = new DateTime(2022, 6, 1) });
        _lib.AddJpeg(@"Trip\b.jpg", new TestLibrary.JpegOptions { DateTaken = new DateTime(2022, 6, 2) });
        var model = new LibraryModel(new Settings(), _lib.PathOf(@"data\Settings.json"), new MetadataCache(_lib.PathOf(@"data\m.json")),
            () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1));
        var window = new MainWindow(model) { ShowActivated = false };
        window.Show();
        try
        {
            Assert.True(await WaitFor(() => model.Rows.Count == 2 && model.DetailProgress is null && model.Rows.Any(r => r.Caption == "Beach")));
            Assert.Equal("All Photos - Photo Finder - Read-Only", window.Title);
            Assert.Equal("All Photos", AutomationProperties.GetName(window.FileList));
            Assert.Equal(2, window.FileList.Items.Count);

            window.FileList.UpdateLayout();
            var first = (ListViewItem)window.FileList.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.StartsWith("b.jpg, ", AutomationProperties.GetName(first));

            // The menu bar isn't a Tab stop: Alt and F10 reach it, as in every Windows app.
            Assert.Equal(KeyboardNavigationMode.None, KeyboardNavigation.GetTabNavigation(window.MainMenu));
            Assert.Equal(KeyboardNavigationMode.None, KeyboardNavigation.GetControlTabNavigation(window.MainMenu));
            var tabOrder = TabOrder(window);
            Assert.NotEmpty(tabOrder);
            Assert.DoesNotContain(tabOrder, e => e is MenuItem || e is Menu);
            Assert.Contains(window.SearchBox, tabOrder);
            // The list is one Tab stop, not one per row.
            Assert.Single(tabOrder.OfType<ListViewItem>());

            // ...and the arrow keys still move between its menus. WPF's arrow navigation looks for tab stops,
            // which is what broke Right Arrow when the menus themselves were marked as not tab stops.
            var menus = window.MainMenu.Items.OfType<MenuItem>().ToList();
            for (var i = 0; i + 1 < menus.Count; i++)
                Assert.Same(menus[i + 1], menus[i].PredictFocus(FocusNavigationDirection.Right));

            // Turning a column on rebuilds the list's columns and what screen readers hear.
            var before = window.ListColumns.Columns.Count;
            model.SetColumnVisible(Columns.Find("keywords")!, true);
            Assert.Equal(before + 1, window.ListColumns.Columns.Count);

            // Gallery shows the selected row with the list's name.
            model.SelectedRows = [model.Rows[1]];
            AppCommands.AsGallery.Execute(null, window);
            Assert.True(await WaitFor(() => AutomationProperties.GetName(window.GalleryImage).StartsWith("a.jpg")));
        }
        finally
        {
            window.Close();
        }
    }

    [WpfFact]
    public async Task Gallery_NamesThePhotoOnly_FooterIsTabbable_AndTheStatusBarIsTheLastPane()
    {
        _lib.AddJpeg(@"Trip\a.jpg", new TestLibrary.JpegOptions { Title = "Beach", DateTaken = new DateTime(2022, 6, 1) });
        var announced = new List<string>();
        Announcer.Observer = announced.Add;
        var model = new LibraryModel(new Settings(), _lib.PathOf(@"data\Settings.json"), new MetadataCache(_lib.PathOf(@"data\m.json")),
            () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1));
        var window = new MainWindow(model) { ShowActivated = false };
        window.Show();
        try
        {
            Assert.True(await WaitFor(() => model.Rows.Count == 1 && model.DetailProgress is null && model.Rows[0].Caption == "Beach"));
            model.SelectedRows = [model.Rows[0]];
            AppCommands.AsGallery.Execute(null, window);
            // Just the name, so arrowing says which photo and the description follows it.
            Assert.True(await WaitFor(() => AutomationProperties.GetName(window.GalleryImage) == "a.jpg"));
            window.UpdateLayout();

            // Tab from the photo goes through the footer in reading order: name, details, caption (no description
            // yet), then Previous, the position and Next.
            var tabOrder = TabOrder(window);
            var fromPhoto = tabOrder.SkipWhile(e => e != window.GalleryImage).Skip(1).Take(6).ToList();
            Assert.Equal<DependencyObject>([window.GalleryName, window.GalleryDetails, window.GalleryCaption,
                window.PreviousButton, window.GalleryPositionText, window.NextButton], fromPhoto);
            Assert.Equal("1 of 1", window.GalleryPositionText.Text);
            // The status bar is reached with F6, not Tab, as in QuickMail.
            Assert.DoesNotContain(tabOrder, e => e is TextBlock);

            // Only sections with something to say are stops.
            Assert.Equal([window.StatusBarText], window.StatusSections());
            model.BackgroundProgress = "Describing photos in the background, 1 of 2";
            window.UpdateLayout();
            Assert.Equal([window.StatusBarText, window.BackgroundText], window.StatusSections());
            Assert.Equal(model.StatusText, window.StatusBarText.Text);
            SetActivity(model, "Exporting…");
            window.UpdateLayout();
            Assert.Equal([window.StatusBarText, window.ActivityText, window.BackgroundText], window.StatusSections());
            Assert.Equal("Exporting…", window.ActivityText.Text);

            // F6 from the gallery goes to the Description panel, Search, then the status bar, then round to the sidebar.
            Assert.True(window.GalleryImage.Focus());
            AppCommands.NextPane.Execute(null, window);
            Assert.True(window.DescriptionPanel.IsKeyboardFocusWithin);
            Assert.True(window.DescriptionPanel.DescriptionBox.IsKeyboardFocused);
            AppCommands.NextPane.Execute(null, window);
            Assert.True(window.SearchBox.IsKeyboardFocusWithin);
            AppCommands.NextPane.Execute(null, window);
            Assert.True(window.StatusBarText.IsKeyboardFocused);
            Assert.Contains(announced, a => a.StartsWith("Status bar: "));

            // Right and Left wrap through the sections.
            window.StatusBar_PreviewKeyDown(window.MainStatusBar, Press(System.Windows.Input.Key.Right));
            Assert.True(window.ActivityText.IsKeyboardFocused);
            window.StatusBar_PreviewKeyDown(window.MainStatusBar, Press(System.Windows.Input.Key.Right));
            Assert.True(window.BackgroundText.IsKeyboardFocused);
            window.StatusBar_PreviewKeyDown(window.MainStatusBar, Press(System.Windows.Input.Key.Right));
            Assert.True(window.StatusBarText.IsKeyboardFocused);
            window.StatusBar_PreviewKeyDown(window.MainStatusBar, Press(System.Windows.Input.Key.Left));
            Assert.True(window.BackgroundText.IsKeyboardFocused);
            window.StatusBar_PreviewKeyDown(window.MainStatusBar, Press(System.Windows.Input.Key.Left));
            Assert.True(window.ActivityText.IsKeyboardFocused);

            // The section you're on going away (the export finished) leaves you in the status bar.
            SetActivity(model, null);
            Assert.True(await WaitFor(() => window.StatusBarText.IsKeyboardFocused));
            model.BackgroundProgress = null;

            AppCommands.NextPane.Execute(null, window);
            Assert.True(window.Sidebar.IsKeyboardFocusWithin);
            AppCommands.PreviousPane.Execute(null, window);
            Assert.True(window.MainStatusBar.IsKeyboardFocusWithin);

            // Escape goes back to the photo.
            window.StatusBar_PreviewKeyDown(window.MainStatusBar, Press(System.Windows.Input.Key.Escape));
            Assert.True(window.GalleryImage.IsKeyboardFocused);
        }
        finally
        {
            Announcer.Observer = null;
            window.Close();
        }
    }

    private static void SetActivity(LibraryModel model, string? activity) =>
        typeof(LibraryModel).GetProperty(nameof(LibraryModel.Activity))!.SetValue(model, activity);

    private static IEnumerable<MenuItem> AllMenuItems(ItemsControl parent) =>
        parent.Items.OfType<MenuItem>().SelectMany(m => AllMenuItems(m).Prepend(m));

    [WpfFact]
    public async Task Menus_KeepUnavailableItemsReachable_SidebarDeleteRemovesOnlyLibraryFolders_AndDescriptionTypeIsAChoice()
    {
        _lib.AddJpeg(@"Trip\a.jpg");
        var model = new LibraryModel(new Settings(), _lib.PathOf(@"data\Settings.json"), new MetadataCache(_lib.PathOf(@"data\m.json")),
            () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1));
        var window = new MainWindow(model) { ShowActivated = false };
        window.Show();
        try
        {
            Assert.True(await WaitFor(() => model.Rows.Count == 1 && model.DetailProgress is null));

            // Every menu item stays enabled to WPF, so the arrows reach it, and says whether it can be used.
            var items = AllMenuItems(window.MainMenu).ToList();
            Assert.All(items, m => Assert.IsType<AppMenuItem>(m));
            var remove = (AppMenuItem)items.Single(m => m.Command == AppCommands.RemoveFolder);
            remove.UpdateAvailability();
            Assert.True(remove.IsEnabled);
            Assert.True(remove.Focusable);
            Assert.False(remove.IsAvailable);
            var peer = (System.Windows.Automation.Peers.AutomationPeer)System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(remove);
            Assert.False(peer.IsEnabled());
            Assert.Equal("Only folders in the library can be removed.", window.SidebarRemovalProblem());
            // Down Arrow from Add Folder lands on it rather than skipping to Refresh, as a disabled WPF item would.
            var file = window.MainMenu.Items.OfType<MenuItem>().First();
            file.IsSubmenuOpen = true;
            Assert.True(await WaitFor(() => remove.IsVisible));
            var add = items.Single(m => m.Command == AppCommands.AddFolder);
            Assert.Same(remove, add.PredictFocus(FocusNavigationDirection.Down));
            file.IsSubmenuOpen = false;

            // On a library folder, it can be removed; a folder inside it can't on its own.
            model.Navigate(model.Sections[2].Children[0].Id);
            remove.UpdateAvailability();
            Assert.True(remove.IsAvailable);
            Assert.True(peer.IsEnabled());
            Assert.Null(window.SidebarRemovalProblem());
            model.Navigate(SidebarIds.Folder(_lib.PathOf("Trip")));
            Assert.StartsWith("This folder is part of ", window.SidebarRemovalProblem());

            // Photo > Description Type: the four kinds, and choosing one is saved.
            var kinds = items.Single(m => m.Name == "KindMenu").Items.OfType<AppMenuItem>().ToList();
            Assert.Equal(["Accessible", "Detailed", "Brief", "Diagram"], kinds.Select(k => ((string)k.Header).Replace("_", "")));
            window.SetDescriptionKind("brief", "Brief");
            Assert.Equal("brief", Settings.Load(_lib.PathOf(@"data\Settings.json")).DescriptionKind);

            Assert.Contains(items, m => m.Command == AppCommands.DescribePhoto);

            // The Help menu in the order both apps use (#33).
            var help = window.MainMenu.Items.OfType<MenuItem>().Last();
            Assert.Equal([AppCommands.CheckForUpdates, AppCommands.ReportBug, AppCommands.CheckSystem, null, AppCommands.About],
                help.Items.Cast<object>().Select(i => (i as MenuItem)?.Command));
            Assert.Equal($"_About {AppInfo.Name}", AppCommands.About.Text);

            // No two items in one menu share an access key, so Alt+letter always chooses.
            foreach (var menu in items.Where(m => m.Items.Count > 0).Cast<ItemsControl>().Append(window.MainMenu).Append(window.FileList.ContextMenu))
            {
                var keys = menu.Items.OfType<MenuItem>().Select(m => m.Header as string).OfType<string>()
                    .Where(h => h.Contains('_')).Select(h => char.ToLowerInvariant(h[h.IndexOf('_') + 1])).ToList();
                Assert.True(keys.Count == keys.Distinct().Count(), $"Repeated access key in {(menu as HeaderedItemsControl)?.Header ?? menu.Name}: {string.Join(",", keys)}");
            }
            Assert.All(window.FileList.ContextMenu.Items.OfType<MenuItem>(), m => Assert.IsType<AppMenuItem>(m));

            // Describe Photo on a folder row is available, so it can say why it can't describe a folder.
            model.Navigate(model.Sections[2].Children[0].Id);
            model.SelectedRows = [model.Rows.Single(r => r.Name == "Trip")];
            Assert.True(AppCommands.DescribePhoto.CanExecute(null, window));

            // Ctrl+Alt+Shift+D is AltGr+Shift+D: while typing in the search box, the box gets it, not Describe Again.
            Assert.True(AppCommands.DescribeAgain.CanExecute(null, window));
            Assert.True(window.SearchBox.Focus());
            Assert.False(AppCommands.DescribeAgain.CanExecute(null, window));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A key press as WPF would deliver it to the focused element.</summary>
    private static KeyEventArgs Press(Key key) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual((System.Windows.Media.Visual)Keyboard.FocusedElement)!, 0, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
}
