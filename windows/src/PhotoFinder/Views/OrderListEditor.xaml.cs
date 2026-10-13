using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoFinder.Services;

namespace PhotoFinder.Views;

/// <summary>
/// What one tab of View > Labels chooses from, and the words it uses (#38): the list's columns, or what the gallery says.
/// </summary>
/// <param name="Noun">"column" or "label", for what's said.</param>
/// <param name="Required">An item that can't be removed (Name, for the list), or null.</param>
/// <param name="ResetText">Said after Reset to Default.</param>
/// <param name="Preview">How a choice reads for the sample photo.</param>
/// <param name="Clean">Makes a choice usable: known items only, each once, the default when nothing's left.</param>
public sealed record OrderChoice(
    IReadOnlyList<string> All, Func<string, string> Title, IReadOnlyList<string> Default, Func<IEnumerable<string>, List<string>> Clean,
    string Noun, string? Required, string ResetText,
    string Explanation, string AvailableTitle, string ShownTitle, string EveryShown, string PreviewTitle,
    Func<IReadOnlyList<string>, string> Preview);

/// <summary>
/// One tab of View > Labels: the items not shown, the ones shown in the order they're read, Add, Remove, Move Up,
/// Move Down, Move to Top, Move to Bottom and Reset to Default, and a preview of the selected photo. The same design as
/// The Chat Place's and QuickMail's, and the Mac's.
///
/// Keys as well as buttons: Enter in Available adds, Delete or Backspace in Shown removes, and Alt+Up, Alt+Down,
/// Alt+Home and Alt+End in Shown move. Every action is said, and focus stays on the item it moved, so arrowing on reads
/// its new neighbours. The last item shown can't be removed, so the photo always has something to read.
/// </summary>
public partial class OrderListEditor : UserControl
{
    private OrderChoice _choice = null!;
    private readonly List<string> _shown = [];
    /// <summary>What Shown displays. Moves are made on it in place, so the list isn't reset under the screen reader.</summary>
    private readonly ObservableCollection<string> _shownTitles = [];

    public OrderListEditor()
    {
        InitializeComponent();
        // Alt+S and Alt+V focus the list itself, which WPF doesn't pass on to an item: put it on the selected one.
        // Afterwards, not during: focus also lands on the list while it's rebuilding, and laying it out then crashes WPF.
        ShownList.GotKeyboardFocus += (_, e) => { if (e.NewFocus == ShownList) Later(() => { if (ShownList.IsKeyboardFocused) FocusShown(); }); };
        AvailableList.GotKeyboardFocus += (_, e) => { if (e.NewFocus == AvailableList) Later(() => { if (AvailableList.IsKeyboardFocused) FocusAvailable(); }); };
    }

    public void Set(OrderChoice choice, IEnumerable<string> shown)
    {
        _choice = choice;
        _shown.Clear();
        _shown.AddRange(choice.Clean(shown));
        Explanation.Text = choice.Explanation;
        AvailableLabel.Content = $"A_vailable {choice.Noun}s:";
        ShownLabel.Content = $"_Shown {choice.Noun}s, in the order they're read:";
        PreviewLabel.Content = $"_{choice.PreviewTitle}:";
        AutomationProperties.SetName(AvailableList, choice.AvailableTitle);
        AutomationProperties.SetName(ShownList, choice.ShownTitle);
        AutomationProperties.SetName(PreviewBox, choice.PreviewTitle);
        // Not the lists' help text: JAWS read it again with every item arrowed to. LabelsDialog says it once instead.
        Fill(shownIndex: 0, availableIndex: 0);
    }

    /// <summary>The shown ids, in the order they're read.</summary>
    public IReadOnlyList<string> Shown => _shown;

    private List<string> NotShown() => _choice.All.Where(id => !_shown.Contains(id)).ToList();

    private string Position(int index) => $"{index + 1} of {_shown.Count}";

    private static void Say(string text) => Announcer.Announce(text, interrupt: true);

    /// <summary>Both lists again, each keeping its place (or going to the given row), and the preview.</summary>
    private void Fill(int? shownIndex = null, int? availableIndex = null)
    {
        var shownAt = shownIndex ?? ShownList.SelectedIndex;
        var availableAt = availableIndex ?? AvailableList.SelectedIndex;
        _shownTitles.Clear();
        foreach (var id in _shown) _shownTitles.Add(_choice.Title(id));
        ShownList.ItemsSource ??= _shownTitles;
        ShownList.SelectedIndex = Math.Clamp(shownAt, 0, _shown.Count - 1);
        var rest = NotShown();
        // An empty list reads as nothing at all to a screen reader.
        AvailableList.ItemsSource = rest.Count > 0 ? rest.Select(_choice.Title).ToList() : [_choice.EveryShown];
        AvailableList.SelectedIndex = Math.Clamp(availableAt, 0, Math.Max(rest.Count, 1) - 1);
        ShowPreview();
    }

    private void ShowPreview() => PreviewBox.Text = _choice.Preview(_shown);

    private void Later(Action action) => Dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Input);

    /// <summary>Keyboard focus on the selected row of Shown, so a screen reader reads where the item is now.</summary>
    internal void FocusShown()
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

    /// <summary>Adds the selected available item at the end; stays in Available, on the next one, to add more (or goes
    /// to Shown, on the item just added, when that was the last).</summary>
    internal void Add()
    {
        var rest = NotShown();
        var index = AvailableList.SelectedIndex;
        if (rest.Count == 0) { Say($"Every {_choice.Noun} is shown already."); return; }
        if (index < 0 || index >= rest.Count) { Say($"No {_choice.Noun} selected."); return; }
        var id = rest[index];
        _shown.Add(id);
        Fill(shownIndex: _shown.Count - 1, availableIndex: index);
        Say($"{_choice.Title(id)} added, {Position(_shown.Count - 1)}.");
        if (rest.Count > 1) FocusAvailable(); else FocusShown();
    }

    internal void Remove()
    {
        if (SelectedShown() is not { } index) { Say($"No {_choice.Noun} selected."); return; }
        var id = _shown[index];
        if (id == _choice.Required)
        {
            FocusShown();
            Say($"{_choice.Title(id)} is always shown. It can be moved.");
            return;
        }
        if (_shown.Count == 1)
        {
            FocusShown();
            Say($"{_choice.Title(id)} is the only {_choice.Noun} shown. Add another before removing it.");
            return;
        }
        _shown.RemoveAt(index);
        Fill(shownIndex: index);
        FocusShown();
        Say($"{_choice.Title(id)} removed. {(_shown.Count == 1 ? $"1 {_choice.Noun}" : $"{_shown.Count} {_choice.Noun}s")} shown.");
    }

    internal void Move(int step)
    {
        if (SelectedShown() is not { } index) { Say($"No {_choice.Noun} selected."); return; }
        MoveTo(index, index + step, step < 0 ? "up" : "down", step < 0 ? "top" : "bottom");
    }

    internal void MoveToEnd(bool top)
    {
        if (SelectedShown() is not { } index) { Say($"No {_choice.Noun} selected."); return; }
        var where = top ? "top" : "bottom";
        MoveTo(index, top ? 0 : _shown.Count - 1, $"to {where}", where);
    }

    private void MoveTo(int index, int target, string how, string end)
    {
        var id = _shown[index];
        if (index == target || target < 0 || target >= _shown.Count)
        {
            FocusShown();
            Say($"{_choice.Title(id)} is already at the {end}.");
            return;
        }
        _shown.RemoveAt(index);
        _shown.Insert(target, id);
        // Just that row, not the whole list; Available hasn't changed at all.
        _shownTitles.Move(index, target);
        ShownList.SelectedIndex = target;
        ShowPreview();
        FocusShown();
        Say($"{_choice.Title(id)} moved {how}, {Position(target)}.");
    }

    internal void Reset()
    {
        _shown.Clear();
        _shown.AddRange(_choice.Default);
        Fill(shownIndex: 0, availableIndex: 0);
        FocusShown();
        Say(_choice.ResetText);
    }

    // MARK: Events

    private void Add_Click(object sender, RoutedEventArgs e) => Add();
    private void Remove_Click(object sender, RoutedEventArgs e) => Remove();
    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);
    private void MoveTop_Click(object sender, RoutedEventArgs e) => MoveToEnd(top: true);
    private void MoveBottom_Click(object sender, RoutedEventArgs e) => MoveToEnd(top: false);
    private void Reset_Click(object sender, RoutedEventArgs e) => Reset();
    private void AvailableList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Add();
    private void ShownList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Remove();

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // Home and End on a button would otherwise reach the tabs, which move to the first or last tab.
        if (e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase && Keyboard.Modifiers == ModifierKeys.None && key is Key.Home or Key.End)
        {
            e.Handled = true;
            return;
        }
        var inAvailable = AvailableList.IsKeyboardFocusWithin;
        var inShown = ShownList.IsKeyboardFocusWithin;
        if (inAvailable && Keyboard.Modifiers == ModifierKeys.None && key == Key.Enter)
        {
            Add();   // Enter in Available adds, rather than pressing OK
            e.Handled = true;
        }
        else if (inShown && Keyboard.Modifiers == ModifierKeys.None && key is Key.Delete or Key.Back)
        {
            Remove();
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
}
