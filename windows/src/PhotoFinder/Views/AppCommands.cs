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

    public static readonly RoutedUICommand Find = Make("_Find", nameof(Find), new KeyGesture(Key.F, ModifierKeys.Control), new KeyGesture(Key.F3));

    public static readonly RoutedUICommand AsList = Make("as _List", nameof(AsList), new KeyGesture(Key.D2, ModifierKeys.Control));
    public static readonly RoutedUICommand AsGallery = Make("as _Gallery", nameof(AsGallery), new KeyGesture(Key.D4, ModifierKeys.Control));
    public static readonly RoutedUICommand ChooseColumns = Make("_Columns…", nameof(ChooseColumns));

    public static readonly RoutedUICommand EnclosingFolder = Make("_Enclosing Folder", nameof(EnclosingFolder),
        new KeyGesture(Key.Up, ModifierKeys.Alt));
    public static readonly RoutedUICommand OpenSelected = Make("_Open Selected Item", nameof(OpenSelected),
        new KeyGesture(Key.Down, ModifierKeys.Alt));
    public static readonly RoutedUICommand AllPhotos = Make("_All Photos", nameof(AllPhotos),
        new KeyGesture(Key.A, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand NextPane = Make("Next Pane", nameof(NextPane), new KeyGesture(Key.F6));
    public static readonly RoutedUICommand PreviousPane = Make("Previous Pane", nameof(PreviousPane), new KeyGesture(Key.F6, ModifierKeys.Shift));

    public static readonly RoutedUICommand ToggleFavorite = Make("Toggle _Favorite", nameof(ToggleFavorite),
        new KeyGesture(Key.F, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand EditCaption = Make("Edit Cap_tion…", nameof(EditCaption),
        new KeyGesture(Key.F2), new KeyGesture(Key.E, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand DescribeImage = Make("_Describe Image…", nameof(DescribeImage),
        new KeyGesture(Key.D, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand DescribeInBackground = Make("Describe Added Folders in the Bac_kground", nameof(DescribeInBackground));
    public static readonly RoutedUICommand StopBackground = Make("_Stop Background Describing", nameof(StopBackground));
    public static readonly RoutedUICommand AutoDescribe = Make("Describe Photos _Automatically", nameof(AutoDescribe),
        new KeyGesture(Key.U, ModifierKeys.Control | ModifierKeys.Shift));

    /// <summary>The number of stars is the command parameter. No shortcuts: Ctrl+digit belongs to the views.</summary>
    public static readonly RoutedUICommand Rate = Make("_Rating", nameof(Rate));

    public static readonly RoutedUICommand About = Make("_About Photo Finder", nameof(About));
}
