using System.Windows.Input;

namespace PhotoFinder.Views;

/// <summary>The app's commands and their shortcuts. Shortcuts that WPF can't express as gestures
/// (Space, Enter, Backspace on their own) are handled by the list and gallery directly.</summary>
public static class AppCommands
{
    private static RoutedUICommand Make(string text, string name, params KeyGesture[] gestures) =>
        new(text, name, typeof(AppCommands), new InputGestureCollection(gestures));

    public static readonly RoutedUICommand OpenItem = Make("_Open", nameof(OpenItem), new KeyGesture(Key.O, ModifierKeys.Control));
    public static readonly RoutedUICommand Preview = Make("_Preview", nameof(Preview), new KeyGesture(Key.Y, ModifierKeys.Control));
    public static readonly RoutedUICommand GetInfo = Make("Get _Info", nameof(GetInfo),
        new KeyGesture(Key.I, ModifierKeys.Control), new KeyGesture(Key.Enter, ModifierKeys.Alt));
    public static readonly RoutedUICommand ShowInExplorer = Make("Sho_w in File Explorer", nameof(ShowInExplorer),
        new KeyGesture(Key.R, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand Export = Make("_Export…", nameof(Export), new KeyGesture(Key.E, ModifierKeys.Control));
    public static readonly RoutedUICommand AddFolder = Make("_Add Folder to Library…", nameof(AddFolder));
    public static readonly RoutedUICommand RemoveFolder = Make("_Remove Folder from Library", nameof(RemoveFolder));
    public static readonly RoutedUICommand Refresh = Make("Re_fresh", nameof(Refresh), new KeyGesture(Key.F5));
    public static readonly RoutedUICommand Exit = Make("E_xit", nameof(Exit), new KeyGesture(Key.F4, ModifierKeys.Alt));
    /// <summary>Speaks the status bar from anywhere, as Kelly's other apps do.</summary>
    public static readonly RoutedUICommand ReadStatusBar = Make("Read Status _Bar", nameof(ReadStatusBar), new KeyGesture(Key.F9, ModifierKeys.Control));
    public static readonly RoutedUICommand GoToStatusBar = Make("Go to Status Ba_r", nameof(GoToStatusBar),
        new KeyGesture(Key.F9, ModifierKeys.Control | ModifierKeys.Shift));

    public static readonly RoutedUICommand Find = Make("_Find", nameof(Find), new KeyGesture(Key.F, ModifierKeys.Control), new KeyGesture(Key.F3));

    public static readonly RoutedUICommand AsList = Make("as _List", nameof(AsList), new KeyGesture(Key.D2, ModifierKeys.Control));
    public static readonly RoutedUICommand AsGallery = Make("as _Gallery", nameof(AsGallery), new KeyGesture(Key.D4, ModifierKeys.Control));
    public static readonly RoutedUICommand ChooseColumns = Make("_Columns…", nameof(ChooseColumns));
    /// <summary>The Description panel. The Mac's is Option-Command-I; Ctrl+Alt is AltGr on many keyboards, so not here.</summary>
    public static readonly RoutedUICommand ShowDescription = Make("Show _Description", nameof(ShowDescription),
        new KeyGesture(Key.I, ModifierKeys.Control | ModifierKeys.Shift));

    public static readonly RoutedUICommand EnclosingFolder = Make("_Enclosing Folder", nameof(EnclosingFolder),
        new KeyGesture(Key.Up, ModifierKeys.Alt));
    public static readonly RoutedUICommand OpenSelected = Make("_Open Selected Item", nameof(OpenSelected),
        new KeyGesture(Key.Down, ModifierKeys.Alt));
    public static readonly RoutedUICommand AllPhotos = Make("_All Photos", nameof(AllPhotos),
        new KeyGesture(Key.A, ModifierKeys.Control | ModifierKeys.Shift));
    /// <summary>The next or previous photo, leaving focus where it is (#119). Alt+arrows, since Ctrl+arrows move by word in
    /// a text box and these work from the description box. The Mac's are Command-Right and Command-Left.</summary>
    public static readonly RoutedUICommand NextPhoto = Make("Ne_xt Photo", nameof(NextPhoto), new KeyGesture(Key.Right, ModifierKeys.Alt));
    public static readonly RoutedUICommand PreviousPhoto = Make("Pre_vious Photo", nameof(PreviousPhoto), new KeyGesture(Key.Left, ModifierKeys.Alt));
    /// <summary>To the Description panel's text box, showing the panel if it's hidden; from there, back to the list or
    /// gallery (#119). The Mac's is Shift-Command-D.</summary>
    public static readonly RoutedUICommand GoToDescription = Make("Go to _Description or Back to Photos", nameof(GoToDescription),
        new KeyGesture(Key.D, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand NextPane = Make("Next Pane", nameof(NextPane), new KeyGesture(Key.F6));
    public static readonly RoutedUICommand PreviousPane = Make("Previous Pane", nameof(PreviousPane), new KeyGesture(Key.F6, ModifierKeys.Shift));

    public static readonly RoutedUICommand ToggleFavorite = Make("Toggle _Favorite", nameof(ToggleFavorite),
        new KeyGesture(Key.F, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand EditCaption = Make("Edit Cap_tion…", nameof(EditCaption),
        new KeyGesture(Key.F2), new KeyGesture(Key.E, ModifierKeys.Control | ModifierKeys.Shift));
    /// <summary>Describes the photo you're on and speaks it, in the list, gallery or Preview. The Mac's is Command-D.</summary>
    public static readonly RoutedUICommand DescribePhoto = Make("_Describe Photo", nameof(DescribePhoto),
        new KeyGesture(Key.D, ModifierKeys.Control));
    /// <summary>The selected photos' saved descriptions become their captions, asking before replacing one.</summary>
    public static readonly RoutedUICommand UseDescriptionAsCaption = Make("_Use Description as Caption", nameof(UseDescriptionAsCaption));
    /// <summary>Describes the photo you're on afresh, replacing its saved description, and speaks it. The Mac's is
    /// Command-R (#119).</summary>
    public static readonly RoutedUICommand Redescribe = Make("Redescri_be", nameof(Redescribe), new KeyGesture(Key.R, ModifierKeys.Control));
    public static readonly RoutedUICommand DescribeInBackground = Make("Describe Photos in the Bac_kground", nameof(DescribeInBackground));
    public static readonly RoutedUICommand StopBackground = Make("_Stop Background Describing", nameof(StopBackground));
    public static readonly RoutedUICommand AutoDescribe = Make("Describe Photos _Automatically", nameof(AutoDescribe),
        new KeyGesture(Key.U, ModifierKeys.Control | ModifierKeys.Shift));

    /// <summary>The number of stars is the command parameter. No shortcuts: Ctrl+digit belongs to the views.</summary>
    public static readonly RoutedUICommand Rate = Make("_Rating", nameof(Rate));

    public static readonly RoutedUICommand CheckForUpdates = Make("Check for _Updates…", nameof(CheckForUpdates));
    public static readonly RoutedUICommand ReportBug = Make("_Report a Bug…", nameof(ReportBug));
    public static readonly RoutedUICommand CheckSystem = Make("Check _System…", nameof(CheckSystem));
    /// <summary>Describes a sample picture shipped with the app, to show describing works end to end (#95).</summary>
    public static readonly RoutedUICommand CheckAIDescription = Make("Check AI _Description…", nameof(CheckAIDescription));
    /// <summary>The diagnostic log's folder, in File Explorer, for attaching to a bug report.</summary>
    public static readonly RoutedUICommand OpenLogFolder = Make("Open _Log Folder", nameof(OpenLogFolder));
    /// <summary>The user guide and privacy policy, on GitHub, as on the Mac (#105).</summary>
    public static readonly RoutedUICommand UserGuide = Make($"{AppInfo.Name} User _Guide", nameof(UserGuide));
    public static readonly RoutedUICommand PrivacyPolicy = Make("_Privacy Policy", nameof(PrivacyPolicy));
    public static readonly RoutedUICommand About = Make($"_About {AppInfo.Name}", nameof(About));
}
