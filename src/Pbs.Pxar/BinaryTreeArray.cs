using System.Numerics;

namespace Pbs.Pxar;

/// <summary>
/// Permutes a hash-sorted array into the self-balancing binary-search-tree layout pxar uses for
/// goodbye tables (item at index i has its smaller child at 2i+1 and larger at 2i+2). Reimplements
/// upstream <c>pxar/src/binary_tree_array.rs</c> exactly so lookups by the official decoder work.
/// </summary>
public static class BinaryTreeArray
{
    /// <summary>
    /// For <paramref name="n"/> sorted items, invoke <paramref name="copy"/>(sourceIndex, destIndex)
    /// to place each sorted item into its BST array position.
    /// </summary>
    public static void Copy(int n, Action<int, int> copy)
    {
        if (n == 0) return;
        int e = 63 - BitOperations.LeadingZeroCount((ulong)n); // (64 - leading_zeros - 1)
        CopyInner(copy, n, 0, e, 0);
    }

    private static void CopyInner(Action<int, int> copy, int n, int o, int e, int i)
    {
        int p = 1 << e;
        int t = p + (p >> 1) - 1;
        int m = n > t ? p - 1 : p - 1 - (t - n);
        copy(o + m, i);
        if (m > 0)
            CopyInner(copy, m, o, e - 1, i * 2 + 1);
        if (m + 1 < n)
            CopyInner(copy, n - m - 1, o + m + 1, e - 1, i * 2 + 2);
    }
}
