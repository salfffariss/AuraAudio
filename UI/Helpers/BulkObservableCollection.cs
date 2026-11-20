using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AuraAudio.UI.Helpers;

/// <summary>
/// Turunan ObservableCollection berkinerja tinggi yang mendukung operasi batch ReplaceAll
/// dengan hanya menembakkan satu kali event NotifyCollectionChangedAction.Reset.
/// Mencegah ratusan layout-pass beruntun pada WPF VirtualizingStackPanel saat memuat atau mencari lagu.
/// </summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotifications;

    public BulkObservableCollection()
    {
    }

    public BulkObservableCollection(IEnumerable<T> collection) : base(collection)
    {
    }

    /// <summary>
    /// Mengganti seluruh isi koleksi dalam satu langkah atomik dengan satu notifikasi Reset ke UI.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        CheckReentrancy();
        _suppressNotifications = true;
        try
        {
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }
        }
        finally
        {
            _suppressNotifications = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_suppressNotifications) return;
        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_suppressNotifications) return;
        base.OnPropertyChanged(e);
    }
}
