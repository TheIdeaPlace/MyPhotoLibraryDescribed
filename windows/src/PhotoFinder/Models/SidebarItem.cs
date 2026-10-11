using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PhotoFinder.Models;

/// <summary>Where the rows in the file list come from.</summary>
public enum SourceKind
{
    /// <summary>A sidebar section (Library, Dates, Folders): lists its children.</summary>
    Section,
    AllPhotos,
    Favorites,
    Videos,
    Screenshots,
    ScreenRecordings,
    RecentlyAdded,
    Panoramas,
    Raw,
    Animated,
    Year,
    Month,
    Folder,
}

/// <summary>A place in the sidebar. Items with children behave like folders: selecting one lists its
/// children, and a disk folder also lists the photos directly inside it.</summary>
public sealed class SidebarItem : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;

    public required string Id { get; init; }
    public required string Title { get; init; }
    public required SourceKind Kind { get; init; }
    public int Count { get; init; }
    public IReadOnlyList<SidebarItem> Children { get; init; } = [];
    public string? ParentId { get; init; }
    /// <summary>Shown in the Kind column when this item appears as a row, e.g. "Folder" or "Year".</summary>
    public required string ContainerKind { get; init; }
    public int Year { get; init; }
    public int Month { get; init; }
    /// <summary>The disk folder, for <see cref="SourceKind.Folder"/>.</summary>
    public string? FolderPath { get; init; }

    public bool HasChildren => Children.Count > 0;

    public string CountText => Count == 1 ? "1 item" : $"{Count:N0} items";

    /// <summary>What a screen reader hears for the sidebar item.</summary>
    public string AccessibleName => Kind == SourceKind.Section ? Title : $"{Title}, {CountText}";

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    /// <summary>Same place, same title, same count and the same children: nothing a person would see changed.</summary>
    public bool SameAs(SidebarItem other) =>
        Id == other.Id && Title == other.Title && Count == other.Count && Children.Count == other.Children.Count
        && Children.Zip(other.Children).All(p => p.First.SameAs(p.Second));

    public override string ToString() => AccessibleName;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
