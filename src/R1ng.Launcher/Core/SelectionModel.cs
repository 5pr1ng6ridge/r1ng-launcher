namespace R1ng.Launcher.Core;

/// <summary>
/// Keyboard selection state for the result list. Kept free of WPF types so navigation rules
/// (wrap-around, paging, clamping) can be unit-tested.
/// </summary>
public sealed class SelectionModel
{
    public int Count { get; private set; }

    public int Index { get; private set; } = -1;

    public bool HasSelection => Index >= 0 && Index < Count;

    /// <summary>Resets to the first row when <paramref name="count"/> is positive.</summary>
    public void Reset(int count, int preferredIndex = 0)
    {
        Count = Math.Max(0, count);
        Index = Count == 0 ? -1 : Math.Clamp(preferredIndex, 0, Count - 1);
    }

    /// <summary>Moves by <paramref name="delta"/> rows, wrapping around the list.</summary>
    public bool Move(int delta)
    {
        if (Count == 0)
        {
            Index = -1;
            return false;
        }

        var next = Index < 0 ? 0 : Index + delta;
        var wrapped = ((next % Count) + Count) % Count;
        var changed = wrapped != Index;
        Index = wrapped;
        return changed;
    }

    /// <summary>Moves by whole pages, clamped to the list bounds (no wrap for paging).</summary>
    public bool MovePage(int direction, int pageSize)
    {
        if (Count == 0)
        {
            Index = -1;
            return false;
        }

        var size = Math.Max(1, pageSize);
        var next = Math.Clamp((Index < 0 ? 0 : Index) + (direction * size), 0, Count - 1);
        var changed = next != Index;
        Index = next;
        return changed;
    }

    public bool MoveToStart()
    {
        if (Count == 0)
            return false;

        var changed = Index != 0;
        Index = 0;
        return changed;
    }

    public bool MoveToEnd()
    {
        if (Count == 0)
            return false;

        var target = Count - 1;
        var changed = Index != target;
        Index = target;
        return changed;
    }
}
