using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WpfApp.Extensions;

public static class ObservableCollectionExtensions
{
    public static int FindIndex<T>(this ObservableCollection<T> collection, Predicate<T> p)
    {
        for (var i = 0; i < collection.Count; i++)
        {
            if (collection[i] is { } item1 && p.Invoke(item1)) return i;
        }
        return -1;
    }
}
