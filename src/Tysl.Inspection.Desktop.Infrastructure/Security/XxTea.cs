using System.Text;

namespace Tysl.Inspection.Desktop.Infrastructure.Security;

internal static class XxTea
{
    public static string EncryptToHex(string plainText, string key)
    {
        var data = ToUInt32Array(Encoding.UTF8.GetBytes(plainText), includeLength: true);
        var keyData = ToUInt32Array(Encoding.UTF8.GetBytes(key), includeLength: false);
        if (keyData.Length < 4)
        {
            Array.Resize(ref keyData, 4);
        }

        var result = Encrypt(data, keyData);
        return Convert.ToHexString(ToByteArray(result, includeLength: false));
    }

    private static uint[] Encrypt(uint[] values, uint[] key)
    {
        var length = values.Length;
        if (length < 2)
        {
            return values;
        }

        const uint delta = 0x9E3779B9;
        var rounds = (uint)(6 + 52 / length);
        var sum = 0u;
        var last = values[length - 1];

        while (rounds-- > 0)
        {
            sum += delta;
            var e = (sum >> 2) & 3;

            for (var index = 0; index < length - 1; index++)
            {
                var next = values[index + 1];
                values[index] += Mix(sum, next, last, index, e, key);
                last = values[index];
            }

            var first = values[0];
            values[length - 1] += Mix(sum, first, last, length - 1, e, key);
            last = values[length - 1];
        }

        return values;
    }

    private static uint Mix(uint sum, uint next, uint last, int index, uint e, uint[] key)
    {
        return ((last >> 5) ^ (next << 2)) + ((next >> 3) ^ (last << 4))
            ^ ((sum ^ next) + (key[(index & 3) ^ e] ^ last));
    }

    private static uint[] ToUInt32Array(byte[] data, bool includeLength)
    {
        var length = (data.Length + 3) / 4;
        var result = includeLength ? new uint[length + 1] : new uint[length];
        if (includeLength)
        {
            result[length] = (uint)data.Length;
        }

        for (var index = 0; index < data.Length; index++)
        {
            result[index >> 2] |= (uint)data[index] << ((index & 3) << 3);
        }

        return result;
    }

    private static byte[] ToByteArray(uint[] data, bool includeLength)
    {
        var length = data.Length << 2;
        if (includeLength)
        {
            var actualLength = (int)data[^1];
            if (actualLength < 0 || actualLength > length)
            {
                return Array.Empty<byte>();
            }

            length = actualLength;
        }

        var result = new byte[length];
        for (var index = 0; index < length; index++)
        {
            result[index] = (byte)(data[index >> 2] >> ((index & 3) << 3));
        }

        return result;
    }
}
