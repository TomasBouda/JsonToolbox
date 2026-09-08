using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace JsonToolbox.App.ViewModels;

/// <summary>
/// An observable collection that can take a batch of items and report it as one change.
/// </summary>
/// <remarks>
/// <see cref="ObservableCollection{T}"/> raises one notification per item, and every
/// notification makes the bound control invalidate its layout. Filling a tree with a few
/// thousand rows that way costs seconds of frozen UI, while the same rows added as a single
/// change cost milliseconds. Avalonia handles multi-item change notifications, so the batch
/// does not have to be flattened into a reset.
/// </remarks>
public sealed class ObservableRangeCollection<T> : ObservableCollection<T>
{
    /// <summary>Inserts a batch at <paramref name="index"/> as one change.</summary>
    public void InsertRange(int index, IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        List<T> batch = [.. items];
        if (batch.Count == 0)
        {
            return;
        }

        for (int i = 0; i < batch.Count; i++)
        {
            Items.Insert(index + i, batch[i]);
        }

        OnPropertyChanged(new(nameof(Count)));
        OnPropertyChanged(new("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, batch, index));
    }

    /// <summary>Removes <paramref name="count"/> items from <paramref name="index"/> as one change.</summary>
    public void RemoveRange(int index, int count)
    {
        if (count <= 0)
        {
            return;
        }

        List<T> removed = [.. Items.Skip(index).Take(count)];
        for (int i = 0; i < removed.Count; i++)
        {
            Items.RemoveAt(index);
        }

        OnPropertyChanged(new(nameof(Count)));
        OnPropertyChanged(new("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
    }

    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        List<T> batch = [.. items];
        if (batch.Count == 0)
        {
            return;
        }

        int index = Count;

        // Items are put into the backing list directly so that the base class does not raise
        // a notification for each one; the single notification is raised afterwards.
        foreach (T item in batch)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new(nameof(Count)));
        OnPropertyChanged(new("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, batch, index));
    }
}
