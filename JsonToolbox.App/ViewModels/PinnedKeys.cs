using CommunityToolkit.Mvvm.ComponentModel;

namespace JsonToolbox.App.ViewModels;

/// <summary>
/// The property names the user has pinned, shared by the whole tree.
/// </summary>
/// <remarks>
/// <para>
/// A pin is on a name, not on one node. Pinning <c>Name</c> somewhere inside the four hundredth
/// record is a statement about records in general — show me this field without making me open
/// anything — so it applies to every container that has a property by that name.
/// </para>
/// <para>
/// One shared instance rather than a copy per row, because a change has to reach every row at
/// once: the values are collected by the scan that lists a container's children, so changing
/// the set means re-reading the containers that are currently open.
/// </para>
/// </remarks>
public sealed partial class PinnedKeys : ObservableObject
{
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    /// <summary>Raised after the set changes, so open containers can be read again.</summary>
    public event EventHandler? Changed;

    public IReadOnlySet<string> Keys => _keys;

    public bool IsEmpty => _keys.Count == 0;

    public bool Contains(string key) => _keys.Contains(key);

    public void Toggle(string key)
    {
        if (!_keys.Remove(key))
        {
            _keys.Add(key);
        }

        Notify();
    }

    public void Clear()
    {
        if (_keys.Count == 0)
        {
            return;
        }

        _keys.Clear();
        Notify();
    }

    /// <summary>The pinned names, for the bar that lets them be taken off again.</summary>
    public IReadOnlyList<string> Ordered => [.. _keys.Order(StringComparer.Ordinal)];

    private void Notify()
    {
        OnPropertyChanged(nameof(Keys));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Ordered));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
