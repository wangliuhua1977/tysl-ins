# Open Platform RSA 解密成功基线（来自远程仓库真实代码）

## 文档目的

本文档整理 **天翼视联 Open Platform 在项目中已经落地并接入实际调用链的 RSA 解密实现**，用于新项目 `tysl-ins` 参考复用。

建议保存到：

```text
C:\tysl-ins\docs\open-platform\open-platform-rsa-decrypt-success-baseline.md
```

---

## 代码来源说明

本基线不是根据接口文档重新推演生成，而是来自远程 GitHub 仓库中已经存在的真实源码与调用链。

优先采用的仓库与文件：

### 仓库一：`wangliuhua1977/ty_sight`

#### 1. RSA 解密类

```text
TylinkMonitorDemo.Infrastructure/Crypto/RsaDecryptor.cs
```

#### 2. 响应解密调用链

```text
TylinkMonitorDemo.Infrastructure/Clients/ApiClient.cs
```

#### 3. 业务侧调用样板

```text
TylinkMonitorDemo.Services/StreamService.cs
```

### 备用参考：`wangliuhua1977/ty_sl`

#### 1. RSA 解密类

```text
TylinkInspection.Infrastructure/OpenPlatform/RsaCipher.cs
```

#### 2. 响应解密器

```text
TylinkInspection.Infrastructure/OpenPlatform/PlaceholderOpenPlatformResponseDecryptor.cs
```

#### 3. OpenPlatform 客户端

```text
TylinkInspection.Infrastructure/OpenPlatform/OpenPlatformClient.cs
```

> 当前用于新项目优先参考 `ty_sight`，因为它的命名与业务调用链更直接，且已经明确接在 `ApiClient -> Service` 这条线上。

---

## 已确认的实际处理逻辑

项目中的真实处理逻辑如下：

1. 平台完整响应为：

```json
{
  "code": 0,
  "msg": "成功",
  "data": "密文字符串"
}
```

2. 当业务调用要求 `decryptResponseData = true` 时，客户端只处理 `data` 字段。
3. 当 `settings.Version == "1.1"` 时：
   - 走 RSA 私钥解密。
4. 当 `settings.Version != "1.1"` 时：
   - 走 XXTea 解密。
5. 解密后的明文字符串一般是 JSON，再反序列化成 DTO。

也就是说，新项目里真正要复用的规则是：

```text
data 为密文字符串
-> decryptResponseData = true
-> version = 1.1
-> RsaDecryptor.Decrypt(encryptedText, settings.RsaPrivateKey)
-> Json 反序列化
```

---

## 一、远程库中的真实 RSA 解密类

以下代码整理自：

```text
wangliuhua1977/ty_sight
TylinkMonitorDemo.Infrastructure/Crypto/RsaDecryptor.cs
```

可直接作为新项目的基线实现使用。

```csharp
using System.Security.Cryptography;
using System.Text;

namespace YourNewProject.Infrastructure.Crypto;

public static class RsaDecryptor
{
    public static bool IsHexCipherText(string? cipherText)
    {
        return TryHex(cipherText, out _);
    }

    public static string Decrypt(string cipherText, string privateKey)
    {
        if (string.IsNullOrWhiteSpace(cipherText))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(privateKey))
        {
            throw new InvalidOperationException("RSA 私钥为空，无法解密 1.1 版本响应。");
        }

        var keyText = privateKey
            .Replace("-----BEGIN PRIVATE KEY-----", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-----END PRIVATE KEY-----", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-----BEGIN RSA PRIVATE KEY-----", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-----END RSA PRIVATE KEY-----", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();

        var cipherBytes = DecodeCipherBytes(cipherText);
        var keyBytes = Convert.FromBase64String(keyText);

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportPkcs8PrivateKey(keyBytes, out _);
        }
        catch (CryptographicException)
        {
            rsa.ImportRSAPrivateKey(keyBytes, out _);
        }

        var plainBytes = DecryptCipherBytes(rsa, cipherBytes);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private static byte[] DecodeCipherBytes(string cipherText)
    {
        return TryHex(cipherText, out var hexBytes)
            ? hexBytes
            : Convert.FromBase64String(cipherText.Trim());
    }

    private static byte[] DecryptCipherBytes(RSA rsa, byte[] cipherBytes)
    {
        if (cipherBytes.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var blockSize = rsa.KeySize / 8;
        if (blockSize <= 0)
        {
            throw new InvalidOperationException("RSA 密钥长度无效，无法解密响应。");
        }

        if (cipherBytes.Length % blockSize != 0)
        {
            throw new CryptographicException(
                $"The RSA ciphertext length {cipherBytes.Length} is not a multiple of the key block size {blockSize}.");
        }

        if (cipherBytes.Length == blockSize)
        {
            return rsa.Decrypt(cipherBytes, RSAEncryptionPadding.Pkcs1);
        }

        using var plainStream = new MemoryStream(cipherBytes.Length);
        for (var offset = 0; offset < cipherBytes.Length; offset += blockSize)
        {
            var plainBlock = rsa.Decrypt(cipherBytes.AsSpan(offset, blockSize), RSAEncryptionPadding.Pkcs1);
            plainStream.Write(plainBlock, 0, plainBlock.Length);
        }

        return plainStream.ToArray();
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
}
```

---

## 二、远程库中的实际调用分支

以下逻辑整理自：

```text
wangliuhua1977/ty_sight
TylinkMonitorDemo.Infrastructure/Clients/ApiClient.cs
```

它表明：

- `decryptResponseData == true`
- `data` 必须是字符串密文
- `version == 1.1` 时走 `RsaDecryptor.Decrypt(...)`
- 否则走 `XXTeaCryptor.Decrypt(...)`

建议新项目把这部分单独抽成统一响应解密器。

### 建议落地类：`OpenPlatformResponseDataResolver.cs`

```csharp
using System.Text.Json;
using YourNewProject.Infrastructure.Crypto;

namespace YourNewProject.Infrastructure.OpenPlatform;

public static class OpenPlatformResponseDataResolver
{
    public static TResponse? Resolve<TResponse>(
        JsonElement data,
        bool decryptResponseData,
        string version,
        string rsaPrivateKey,
        string appSecret,
        Func<string, string, string> xxTeaDecrypt,
        JsonSerializerOptions jsonOptions)
    {
        if (data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return default;
        }

        if (!decryptResponseData)
        {
            if (typeof(TResponse) == typeof(string) && data.ValueKind == JsonValueKind.String)
            {
                return (TResponse?)(object?)data.GetString();
            }

            return JsonSerializer.Deserialize<TResponse>(data.GetRawText(), jsonOptions);
        }

        if (data.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("当前响应 data 不是字符串密文，无法按加密响应处理。");
        }

        var encryptedText = data.GetString();
        if (string.IsNullOrWhiteSpace(encryptedText))
        {
            return default;
        }

        string decryptedJson;
        if (version.Equals("1.1", StringComparison.OrdinalIgnoreCase))
        {
            decryptedJson = RsaDecryptor.Decrypt(encryptedText, rsaPrivateKey);
        }
        else
        {
            decryptedJson = xxTeaDecrypt(encryptedText, appSecret);
        }

        if (typeof(TResponse) == typeof(string))
        {
            return (TResponse?)(object?)decryptedJson;
        }

        return JsonSerializer.Deserialize<TResponse>(decryptedJson, jsonOptions);
    }
}
```

---

## 三、业务调用样板

以下使用方式对应远程库中的业务层调用思路，整理自：

```text
wangliuhua1977/ty_sight
TylinkMonitorDemo.Services/StreamService.cs
```

其核心是调用 API 时显式传：

```csharp
// decryptResponseData: true
```

### 新项目最小使用样板

```csharp
using System.Text.Json;
using YourNewProject.Infrastructure.OpenPlatform;

var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNameCaseInsensitive = true
};

// responseJson 为平台原始返回：
// { "code":0, "msg":"成功", "data":"密文字符串" }
using var document = JsonDocument.Parse(responseJson);
var root = document.RootElement;

var code = root.GetProperty("code").GetInt32();
var msg = root.GetProperty("msg").GetString();

if (code != 0)
{
    throw new InvalidOperationException($"平台返回失败：code={code}, msg={msg}");
}

var data = root.GetProperty("data");

var dto = OpenPlatformResponseDataResolver.Resolve<StreamAddressDto>(
    data: data,
    decryptResponseData: true,
    version: settings.Version,
    rsaPrivateKey: settings.RsaPrivateKey,
    appSecret: settings.AppSecret,
    xxTeaDecrypt: (cipher, secret) => XxTeaCryptor.Decrypt(cipher, secret),
    jsonOptions: serializerOptions);
```

### 先只验证解密字符串时的样板

```csharp
var plainJson = OpenPlatformResponseDataResolver.Resolve<string>(
    data: data,
    decryptResponseData: true,
    version: settings.Version,
    rsaPrivateKey: settings.RsaPrivateKey,
    appSecret: settings.AppSecret,
    xxTeaDecrypt: (cipher, secret) => XxTeaCryptor.Decrypt(cipher, secret),
    jsonOptions: serializerOptions);

Console.WriteLine(plainJson);
```

---

## 四、新项目推荐文件结构

建议在 `tysl-ins` 中按下面方式组织：

```text
C:\tysl-ins\src\YourProject.Infrastructure\Crypto\RsaDecryptor.cs
C:\tysl-ins\src\YourProject.Infrastructure\OpenPlatform\OpenPlatformResponseDataResolver.cs
C:\tysl-ins\docs\open-platform\open-platform-rsa-decrypt-success-baseline.md
```

如果你后面还会接入：

- XXTea 请求加解密
- HMAC-SHA256 签名
- 统一 Token 缓存
- 统一 API Trace 日志

那么建议继续在 `Infrastructure/OpenPlatform` 下收口，不要散落到多个模块中。

---

## 五、与旧项目保持一致的关键点

### 1. 只解 `data` 字段

不要对整包响应 JSON 做 RSA 解密。

### 2. `version = 1.1` 走 RSA

这是旧项目实际调用链里的判断条件。

### 3. 密文兼容两种格式

当前解密类支持：

- 十六进制密文
- Base64 密文

### 4. 私钥兼容两种导入方式

当前解密类依次尝试：

- `ImportPkcs8PrivateKey`
- `ImportRSAPrivateKey`

### 5. 支持分段解密

当密文长度大于单块 RSA 长度时，会按块逐段解密再拼接。

---

## 六、当前可直接复用的最小组合

对于新项目，当前最小复制集合就是：

1. `RsaDecryptor.cs`
2. `OpenPlatformResponseDataResolver.cs`
3. 业务调用处显式传 `decryptResponseData = true`

如果后续你要继续把旧项目里的 XXTea、签名、请求组装、Token 缓存也统一整理进来，可以在本目录下继续追加文档：

- `open-platform-xxtea-and-signature-baseline.md`
- `open-platform-token-cache-baseline.md`
- `open-platform-api-client-baseline.md`

---

## 七、结论

本文件整理的不是“按官方文档重新写的示例”，而是来自远程仓库 `ty_sight` 中已经存在并接入调用链的真实代码基线。

对新项目而言，最核心的一句就是：

```text
接口返回 data 为密文字符串
-> decryptResponseData = true
-> version = 1.1
-> RsaDecryptor.Decrypt(encryptedText, settings.RsaPrivateKey)
-> 再反序列化为 DTO
```

这份基线适合直接作为 `tysl-ins` 的 Open Platform RSA 响应解密参考文档。
