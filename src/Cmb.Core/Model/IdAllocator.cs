namespace Cmb.Core.Model;

public sealed class IdAllocator
{
    private int _next;

    public IdAllocator(int startFrom = 1)
    {
        if (startFrom <= 0) throw new ArgumentOutOfRangeException(nameof(startFrom));
        _next = startFrom;
    }

    public int Next() => _next++;

    public int Peek => _next;
}
