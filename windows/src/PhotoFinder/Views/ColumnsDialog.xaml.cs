using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoFinder.Models;
using PhotoFinder.Services;

namespace PhotoFinder.Views;

/// <summary>
/// View, Choose Columns: which columns the list shows and in what order, which is also the order each photo is
/// read in. The same design as The Chat Place's and QuickMail's: the columns not shown, the ones shown in the
/// order they're read, Add, Remove, Move Up, Move Down, Move to Top, Move to Bottom and Reset to Default, and a
/// preview of the selected photo's line. OK keeps the choice; Cancel or Escape leaves it.
///
/// Keys as well as buttons: Enter in Available adds, Delete or Backspace in Shown removes, and Alt+Up, Alt+Down,
/// Alt+Home and Alt+End in Shown move. Every action is said, and focus stays on the column it moved, so arrowing
/// on reads its new neighbours.
/// </summary>
public partial class ColumnsDialog : Window
{
    private readonly List<string> _shown;
    private readonly FileRow? _sample;
    /// <summary>What Shown displays. Moves are made on it in place, so the list isn't reset under the screen reader.</summary>
    private readonly ObservableCollection<string> _shownTitles = [];

    public ColumnsDialog(IEnumerable<string> shown, FileRow? sample)
    {
        _shown = Columns.Clean(shown);
        _sample = sample;
        InitializeComponent();
        // Focus opens in the lists, past the explanation, so the lists carry it as their help text.
        System.Windows.Automation.AutomationProperties.SetHelpText(ShownList, Explanation.Text);
        System.Windows.Automation.AutomationProperties.SetHelpText(AvailableList, Explanation.Text);
        // Alt+S and Alt+V focus the list itself, which WPF doesn't pass on to an item: put it on the selected one.
        // Afterwards, not during: focus also lands on the list while it's rebuilding, and laying it out
        // then crashes WPF.
        ShownList.GotKeyboardFocus += (_, e) => { if (e.NewFocus == ShownList) Later(() => { if (ShownList.IsKeyboardFocused) FocusShown(); }); };
        AvailableList.GotKeyboardFocus += (_, e) => { if (e.NewFocus == AvailableList) Later(() => { if (AvailableList.IsKeyboardFocused) FocusAvailable(); }); };
        Fill(shownIndex: 0, availableIndex: 0);
        Loaded += (_, _) => FocusShown();
    }

    /// <summary>The shown column ids, in the order they're read.</summary>
    public IReadOnlyList<string> ShownColumns => _shown;

    private static string ColumnTitle(string id) => Columns.Find(id)!.Title;

    private List<string> NotShown() => Columns.All.Select(c => c.Id).Where(id => !_shown.Contains(id)).ToList();

    private string Position(int index) => $"{index + 1} of {_shown.Count}";

    private static void Say(string text) => Announcer.Announce(text, interrupt: true);

    /// <summary>Both lists again, each keeping its place (or going to the given row), and the preview.</summary>
    private void Fill(int? shownIndex = null, int? availableIndex = null)
    {
        var shownAt = shownIndex ?? ShownList.SelectedIndex;
        var availableAt = availableIndex ?? AvailableList.SelectedIndex;
        _shownTitles.Clear();
        foreach (var id in _shown) _shownTitles.Add(ColumnTitle(id));
        ShownList.ItemsSource ??= _shownTitles;
        ShownList.SelectedIndex = Math.Clamp(shownAt, 0, _shown.Count - 1);
        var rest = NotShown();
        // An empty list reads as nothing at all to a screen reader.
        AvailableList.ItemsSource = rest.Count > 0 ? rest.Select(ColumnTitle).ToList() : ["Every column is shown."];
        AvailableList.SelectedIndex = Math.Clamp(availableAt, 0, Math.Max(rest.Count, 1) - 1);
        ShowPreview();
    }

    private void ShowPreview() =>
        PreviewBox.Text = _sample is null ? "There's no photo here to preview." : _sample.SpokenWith(_shown);

    private void Later(Action action) => Dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Input);

    /// <summary>Keyboard focus on the selected row of Shown, so a screen reader reads where the column is now.</summary>
    private void FocusShown()
    {
        ShownList.UpdateLayout();
        if (ShownList.ItemContainerGenerator.ContainerFromIndex(ShownList.SelectedIndex) is ListBoxItem item) item.Focus();
        else ShownList.Focus();
    }

    private void FocusAvailable()
    {
        AvailableList.UpdateLayout();
        if (AvailableList.ItemContainerGenerator.ContainerFromIndex(AvailableList.SelectedIndex) is ListBoxItem item) item.Focus();
        else AvailableList.Focus();
    }

    private int? SelectedShown() => ShownList.SelectedIndex is var i && i >= 0 && i < _shown.Count ? i : null;

    // MARK: Actions (internal so tests can drive them)

    /// <summary>Adds the selected available column at the end; stays in Available, on the next one, to add more
    /// (or goes to Shown, on the column just added, when that was the last).</summary>
    internal void AddColumn()
    {
        var rest = NotShown();
        var index = AvailableList.SelectedIndex;
        if (rest.Count == 0) { Say("Every column is shown already."); return; }
        if (index < 0 || index >= rest.Count) { Say("No column selected."); return; }
        var id = rest[index];
        _shown.Add(id);
        Fill(shownIndex: _shown.Count - 1, availableIndex: index);
        Say($"{ColumnTitle(id)} added, {Position(_shown.Count - 1)}.");
        if (rest.Count > 1) FocusAvailable(); else FocusShown();
    }

    internal void RemoveColumn()
    {
        if (SelectedShown() is not { } index) { Say("No column selected."); return; }
        var id = _shown[index];
        if (id == Columns.Name)
        {
            FocusShown();
            Say("Name is always shown. It can be moved.");
            return;
        }
        _shown.RemoveAt(index);
        Fill(shownIndex: index);
        FocusShown();
        Say($"{ColumnTitle(id)} removed. {(_shown.Count == 1 ? "1 column" : $"{_shown.Count} columns")} shown.");
    }

    internal void Move(int step)
    {
        if (SelectedShown() is not { } index) { Say("No column selected."); return; }
        MoveTo(index, index + step, step < 0 ? "up" : "down", step < 0 ? "top" : "bottom");
    }

    internal void MoveToEnd(bool top)
    {
        if (SelectedShown() is not { } index) { Say("No column selected."); return; }
        var where = top ? "top" : "bottom";
        MoveTo(index, top ? 0 : _shown.Count - 1, $"to {where}", where);
    }

    private void MoveTo(int index, int target, string how, string end)
    {
        var id = _shown[index];
        if (index == target || target < 0 || target >= _shown.Count)
        {
            FocusShown();
            Say($"{ColumnTitle(id)} is already at the {end}.");
            return;
        }
        _shown.RemoveAt(index);
        _shown.Insert(target, id);
        // Just that row, not the whole list; Available hasn't changed at all.
        _shownTitles.Move(index, target);
        ShownList.SelectedIndex = target;
        ShowPreview();
        FocusShown();
        Say($"{ColumnTitle(id)} moved {how}, {Position(target)}.");
    }

    internal void ResetColumns()
    {
        _shown.Clear();
        _shown.AddRange(Columns.DefaultOrder);
        Fill(shownIndex: 0, availableIndex: 0);
        FocusShown();
        Say($"Columns reset to the default: {_shown.Count} shown, Name first.");
    }

    // MARK: Events

    private void Add_Click(object sender, RoutedEventArgs e) => AddColumn();
    private void Remove_Click(object sender, RoutedEventArgs e) => RemoveColumn();
    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);
    private void MoveTop_Click(object sender, RoutedEventArgs e) => MoveToEnd(top: true);
    private void MoveBottom_Click(object sender, RoutedEventArgs e) => MoveToEnd(top: false);
    private void Reset_Click(object sender, RoutedEventArgs e) => ResetColumns();
    private void AvailableList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AddColumn();
    private void ShownList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => RemoveColumn();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var inAvailable = AvailableList.IsKeyboardFocusWithin;
        var inShown = ShownList.IsKeyboardFocusWithin;
        if (inAvailable && Keyboard.Modifiers == ModifierKeys.None && key == Key.Enter)
        {
            AddColumn();   // Enter in Available adds, rather than pressing OK
            e.Handled = true;
        }
        else if (inShown && Keyboard.Modifiers == ModifierKeys.None && key is Key.Delete or Key.Back)
        {
            RemoveColumn();
            e.Handled = true;
        }
        else if (inShown && Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down or Key.Home or Key.End)
        {
            if (key == Key.Up) Move(-1);
            else if (key == Key.Down) Move(1);
            else MoveToEnd(top: key == Key.Home);
            e.Handled = true;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
