using System.Security.Cryptography;
using System.Text;

namespace Tysl.Inspection.Desktop.Infrastructure.OpenPlatform;

internal static class RsaDecryptor
{
    public static bool TryGetCipherEncoding(string? cipherText, out RsaCipherEncoding encoding)
    {
        var normalized = cipherText?.Trim() ?? string.Empty;
        if (TryHex(normalized, out _))
        {
            encoding = RsaCipherEncoding.Hex;
            return true;
        }

        try
        {
            _ = Convert.FromBase64String(normalized);
            encoding = RsaCipherEncoding.Base64;
            return true;
        }
        catch (FormatException)
        {
            encoding = RsaCipherEncoding.Unknown;
            return false;
        }
    }

    public static RsaDecryptResult Decrypt(string cipherText, string privateKey)
    {
        if (string.IsNullOrWhiteSpace(cipherText))
        {
            return new RsaDecryptResult(string.Empty, RsaCipherEncoding.Unknown, RsaPrivateKeyFormat.Unknown, RsaDecryptMode.Unknown);
        }

        var (cipherBytes, cipherEncoding) = DecodeCipherBytes(cipherText);
        using var rsaLease = ImportPrivateKey(privateKey);
        var (plainBytes, decryptMode) = DecryptCipherBytes(rsaLease.Rsa, cipherBytes);
        return new RsaDecryptResult(
            Encoding.UTF8.GetString(plainBytes),
            cipherEncoding,
            rsaLease.PrivateKeyFormat,
            decryptMode);
    }

    private static (byte[] Bytes, RsaCipherEncoding Encoding) DecodeCipherBytes(string cipherText)
    {
        var normalized = cipherText.Trim();
        if (TryHex(normalized, out var hexBytes))
        {
            return (hexBytes, RsaCipherEncoding.Hex);
        }

        try
        {
            return (Convert.FromBase64String(normalized), RsaCipherEncoding.Base64);
        }
        catch (FormatException exception)
        {
            throw new RsaDecryptException(
                RsaDecryptStage.InputEncoding,
                "RTSP data 输入编码识别：无法识别输入编码。",
                exception);
        }
    }

    private static RsaLease ImportPrivateKey(string privateKey)
    {
        if (string.IsNullOrWhiteSpace(privateKey))
        {
            throw new RsaDecryptException(
                RsaDecryptStage.PrivateKeyImport,
                "RSA 私钥导入失败：RSA 私钥为空。");
        }

        var keyText = privateKey
            .Replace("-----BEGIN PRIVATE KEY-----", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-----END PRIVATE KEY-----", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-----BEGIN RSA PRIVATE KEY-----", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-----END RSA PRIVATE KEY-----", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();

        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(keyText);
        }
        catch (FormatException exception)
        {
            throw new RsaDecryptException(
                RsaDecryptStage.PrivateKeyImport,
                "RSA 私钥导入失败：私钥内容不是合法 Base64。",
                exception);
        }

        var rsa = RSA.Create();
        try
        {
            try
            {
                rsa.ImportPkcs8PrivateKey(keyBytes, out _);
                return new RsaLease(rsa, RsaPrivateKeyFormat.Pkcs8);
            }
            catch (CryptographicException)
            {
                rsa.ImportRSAPrivateKey(keyBytes, out _);
                return new RsaLease(rsa, RsaPrivateKeyFormat.Pkcs1);
            }
        }
        catch (Exception exception)
        {
            rsa.Dispose();
            throw new RsaDecryptException(
                RsaDecryptStage.PrivateKeyImport,
                "RSA 私钥导入失败。",
                exception);
        }
    }

    private static (byte[] Bytes, RsaDecryptMode Mode) DecryptCipherBytes(RSA rsa, byte[] cipherBytes)
    {
        if (cipherBytes.Length == 0)
        {
            return (Array.Empty<byte>(), RsaDecryptMode.Unknown);
        }

        var blockSize = rsa.KeySize / 8;
        if (blockSize <= 0)
        {
            throw new RsaDecryptException(
                RsaDecryptStage.Decrypt,
                "RSA 分段/单段解密失败：RSA 密钥长度无效。");
        }

        if (cipherBytes.Length % blockSize != 0)
        {
            throw new RsaDecryptException(
                RsaDecryptStage.Decrypt,
                $"RSA 分段/单段解密失败：密文长度 {cipherBytes.Length} 不是块长 {blockSize} 的整数倍。");
        }

        try
        {
            if (cipherBytes.Length == blockSize)
            {
                return (rsa.Decrypt(cipherBytes, RSAEncryptionPadding.Pkcs1), RsaDecryptMode.SingleBlock);
            }

            using var plainStream = new MemoryStream(cipherBytes.Length);
            for (var offset = 0; offset < cipherBytes.Length; offset += blockSize)
            {
                var plainBlock = rsa.Decrypt(cipherBytes.AsSpan(offset, blockSize), RSAEncryptionPadding.Pkcs1);
                plainStream.Write(plainBlock, 0, plainBlock.Length);
            }

            return (plainStream.ToArray(), RsaDecryptMode.Chunked);
        }
        catch (CryptographicException exception)
        {
            throw new RsaDecryptException(
                RsaDecryptStage.Decrypt,
                "RSA 分段/单段解密失败。",
                exception);
        }
    }

    private static bool TryHex(string? source, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        var normalized = source?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length % 2 != 0)
        {
            return false;
        }

        for (var index = 0; index < normalized.Length; index++)
        {
            if (!Uri.IsHexDigit(normalized[index]))
            {
                return false;
            }
        }

        bytes = Convert.FromHexString(normalized);
        return true;
    }

    private sealed record RsaLease(RSA Rsa, RsaPrivateKeyFormat PrivateKeyFormat) : IDisposable
    {
        public void Dispose()
        {
            Rsa.Dispose();
        }
    }
}

internal sealed record RsaDecryptResult(
    string PlainText,
    RsaCipherEncoding CipherEncoding,
    RsaPrivateKeyFormat PrivateKeyFormat,
    RsaDecryptMode DecryptMode);

internal enum RsaCipherEncoding
{
    Unknown = 0,
    Hex = 1,
    Base64 = 2
}

internal enum RsaPrivateKeyFormat
{
    Unknown = 0,
    Pkcs8 = 1,
    Pkcs1 = 2
}

internal enum RsaDecryptMode
{
    Unknown = 0,
    SingleBlock = 1,
    Chunked = 2
}

internal enum RsaDecryptStage
{
    InputEncoding = 0,
    PrivateKeyImport = 1,
    Decrypt = 2
}

internal sealed class RsaDecryptException : Exception
{
    public RsaDecryptException(RsaDecryptStage stage, string stageSummary, Exception? innerException = null)
        : base(stageSummary, innerException)
    {
        Stage = stage;
        StageSummary = stageSummary;
    }

    public RsaDecryptStage Stage { get; }

    public string StageSummary { get; }
}
