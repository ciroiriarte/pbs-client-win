using System.Buffers.Binary;

namespace Pbs.Pxar;

/// <summary>
/// SipHash-2-4 keyed hash, matching Rust's <c>siphasher</c> crate (<c>SipHasher24::new_with_keys</c>
/// then a single <c>write</c> + <c>finish</c>). pxar uses it to hash directory-entry names for the
/// goodbye lookup table, so the output must be byte-identical to the upstream implementation.
/// </summary>
public static class SipHash24
{
    public static ulong Compute(ReadOnlySpan<byte> data, ulong k0, ulong k1)
    {
        ulong v0 = k0 ^ 0x736f6d6570736575UL;
        ulong v1 = k1 ^ 0x646f72616e646f6dUL;
        ulong v2 = k0 ^ 0x6c7967656e657261UL;
        ulong v3 = k1 ^ 0x7465646279746573UL;

        int len = data.Length;
        int end = len - (len % 8);
        for (int i = 0; i < end; i += 8)
        {
            ulong m = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i, 8));
            v3 ^= m;
            SipRound(ref v0, ref v1, ref v2, ref v3);
            SipRound(ref v0, ref v1, ref v2, ref v3);
            v0 ^= m;
        }

        // Final block: remaining <8 bytes, with the total length in the top byte.
        ulong b = (ulong)len << 56;
        for (int i = 0; i < len - end; i++)
            b |= (ulong)data[end + i] << (8 * i);

        v3 ^= b;
        SipRound(ref v0, ref v1, ref v2, ref v3);
        SipRound(ref v0, ref v1, ref v2, ref v3);
        v0 ^= b;

        v2 ^= 0xff;
        SipRound(ref v0, ref v1, ref v2, ref v3);
        SipRound(ref v0, ref v1, ref v2, ref v3);
        SipRound(ref v0, ref v1, ref v2, ref v3);
        SipRound(ref v0, ref v1, ref v2, ref v3);

        return v0 ^ v1 ^ v2 ^ v3;
    }

    private static void SipRound(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3)
    {
        v0 += v1; v1 = System.Numerics.BitOperations.RotateLeft(v1, 13); v1 ^= v0; v0 = System.Numerics.BitOperations.RotateLeft(v0, 32);
        v2 += v3; v3 = System.Numerics.BitOperations.RotateLeft(v3, 16); v3 ^= v2;
        v0 += v3; v3 = System.Numerics.BitOperations.RotateLeft(v3, 21); v3 ^= v0;
        v2 += v1; v1 = System.Numerics.BitOperations.RotateLeft(v1, 17); v1 ^= v2; v2 = System.Numerics.BitOperations.RotateLeft(v2, 32);
    }
}
