using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Tysl.Inspection.Desktop.Contracts.Configuration;
using Tysl.Inspection.Desktop.Infrastructure.OpenPlatform;

namespace Tysl.Inspection.Desktop.Tests;

public sealed class OpenPlatformClientTests
{
    [Fact]
    public async Task GetDevicePreviewUrlAsync_DecryptsRsaPayload_WhenDataIsHex()
    {
        using var rsa = RSA.Create(2048);
        var payloadJson = """{"url":"rtsp://demo/live/dev-hex","expireTime":"600"}""";
        var encryptedPayload = Convert.ToHexString(
            rsa.Encrypt(Encoding.UTF8.GetBytes(payloadJson), RSAEncryptionPadding.Pkcs1));
        using var client = CreateClient(
            rsa.ExportRSAPrivateKeyPem(),
            CreateHandler(
                CreateJsonResponse("""{"code":0,"msg":"成功","data":{"accessToken":"token","refreshToken":"refresh","expiresIn":3600}}"""),
                CreateJsonResponse($$"""{"code":0,"msg":"成功","data":"{{encryptedPayload}}"}""")));

        var result = await client.GetDevicePreviewUrlAsync("dev-hex", CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal("rtsp://demo/live/dev-hex", result.Payload!.Url);
        Assert.Equal("600", result.Payload.ExpireTime);
    }

    [Fact]
    public async Task GetDevicePreviewUrlAsync_DecryptsRsaPayload_WhenDataIsBase64()
    {
        using var rsa = RSA.Create(2048);
        var payloadJson = """{"url":"rtsp://demo/live/dev-001","expireTime":"600"}""";
        var encryptedPayload = Convert.ToBase64String(
            rsa.Encrypt(Encoding.UTF8.GetBytes(payloadJson), RSAEncryptionPadding.Pkcs1));
        using var client = CreateClient(
            rsa.ExportRSAPrivateKeyPem(),
            CreateHandler(
                CreateJsonResponse("""{"code":0,"msg":"成功","data":{"accessToken":"token","refreshToken":"refresh","expiresIn":3600}}"""),
                CreateJsonResponse($$"""{"code":0,"msg":"成功","data":"{{encryptedPayload}}"}""")));

        var result = await client.GetDevicePreviewUrlAsync("dev-001", CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal("rtsp://demo/live/dev-001", result.Payload!.Url);
        Assert.Equal("600", result.Payload.ExpireTime);
    }

    [Fact]
    public async Task GetDevicePreviewUrlAsync_DecryptsRsaPayload_WhenCipherTextIsChunked()
    {
        using var rsa = RSA.Create(2048);
        var payloadJson = $$"""{"url":"rtsp://demo/live/{{new string('a', 320)}}","expireTime":"1800"}""";
        var encryptedPayload = EncryptInBlocksToBase64(rsa, payloadJson);
        using var client = CreateClient(
            rsa.ExportRSAPrivateKeyPem(),
            CreateHandler(
                CreateJsonResponse("""{"code":0,"msg":"成功","data":{"accessToken":"token","refreshToken":"refresh","expiresIn":3600}}"""),
                CreateJsonResponse($$"""{"code":0,"msg":"成功","data":"{{encryptedPayload}}"}""")));

        var result = await client.GetDevicePreviewUrlAsync("dev-chunked", CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal($"rtsp://demo/live/{new string('a', 320)}", result.Payload!.Url);
        Assert.Equal("1800", result.Payload.ExpireTime);
    }

    [Fact]
    public async Task GetDevicePreviewUrlAsync_ReturnsDecryptFailure_WhenPayloadIsNotHexOrBase64()
    {
        using var client = CreateClient(
            CreatePrivateKeyPem(),
            CreateHandler(
                CreateJsonResponse("""{"code":0,"msg":"成功","data":{"accessToken":"token","refreshToken":"refresh","expiresIn":3600}}"""),
                CreateJsonResponse("""{"code":0,"msg":"成功","data":"not-base64!"}""")));

        var result = await client.GetDevicePreviewUrlAsync("dev-001", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("RTSP 响应解密失败", result.ErrorMessage);
    }

    [Fact]
    public async Task GetDevicePreviewUrlAsync_ReturnsJsonParseFailure_WhenDecryptedPayloadIsNotJson()
    {
        using var rsa = RSA.Create(2048);
        var encryptedPayload = Convert.ToBase64String(
            rsa.Encrypt(Encoding.UTF8.GetBytes("not-json"), RSAEncryptionPadding.Pkcs1));
        using var client = CreateClient(
            rsa.ExportRSAPrivateKeyPem(),
            CreateHandler(
                CreateJsonResponse("""{"code":0,"msg":"成功","data":{"accessToken":"token","refreshToken":"refresh","expiresIn":3600}}"""),
                CreateJsonResponse($$"""{"code":0,"msg":"成功","data":"{{encryptedPayload}}"}""")));

        var result = await client.GetDevicePreviewUrlAsync("dev-001", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("RTSP 解密后 JSON 解析失败", result.ErrorMessage);
    }

    [Fact]
    public async Task GetDevicePreviewUrlAsync_ReturnsMissingUrlFailure_WhenPayloadDoesNotContainUrl()
    {
        using var rsa = RSA.Create(2048);
        var payloadJson = """{"expireTime":"600"}""";
        var encryptedPayload = Convert.ToBase64String(
            rsa.Encrypt(Encoding.UTF8.GetBytes(payloadJson), RSAEncryptionPadding.Pkcs1));
        using var client = CreateClient(
            rsa.ExportRSAPrivateKeyPem(),
            CreateHandler(
                CreateJsonResponse("""{"code":0,"msg":"成功","data":{"accessToken":"token","refreshToken":"refresh","expiresIn":3600}}"""),
                CreateJsonResponse($$"""{"code":0,"msg":"成功","data":"{{encryptedPayload}}"}""")));

        var result = await client.GetDevicePreviewUrlAsync("dev-001", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("RTSP 返回缺少 url", result.ErrorMessage);
    }

    [Fact]
    public async Task GetDevicePreviewUrlAsync_ReturnsBusinessFailure_WhenPlatformCodeIsNotSuccess()
    {
        using var client = CreateClient(
            CreatePrivateKeyPem(),
            CreateHandler(
                CreateJsonResponse("""{"code":0,"msg":"成功","data":{"accessToken":"token","refreshToken":"refresh","expiresIn":3600}}"""),
                CreateJsonResponse("""{"code":1001,"msg":"设备不可预览","data":""}""")));

        var result = await client.GetDevicePreviewUrlAsync("dev-001", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("RTSP 接口业务失败", result.ErrorMessage);
    }

    [Fact]
    public async Task GetDevicePreviewUrlAsync_DecryptsRsaPayload_WhenPrivateKeyIsPkcs8Base64()
    {
        using var rsa = RSA.Create(2048);
        var payloadJson = """{"url":"rtsp://demo/live/dev-003","expireTime":"1200"}""";
        var encryptedPayload = Convert.ToBase64String(
            rsa.Encrypt(Encoding.UTF8.GetBytes(payloadJson), RSAEncryptionPadding.Pkcs1));
        var pkcs8Base64 = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
        using var client = CreateClient(
            pkcs8Base64,
            CreateHandler(
                CreateJsonResponse("""{"code":0,"msg":"成功","data":{"accessToken":"token","refreshToken":"refresh","expiresIn":3600}}"""),
                CreateJsonResponse($$"""{"code":0,"msg":"成功","data":"{{encryptedPayload}}"}""")));

        var result = await client.GetDevicePreviewUrlAsync("dev-003", CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal("rtsp://demo/live/dev-003", result.Payload!.Url);
        Assert.Equal("1200", result.Payload.ExpireTime);
    }

    [Fact]
    public async Task GetRegionListAsync_DecryptsRsaPayload_WhenDataIsEncryptedString()
    {
        using var rsa = RSA.Create(2048);
        var payloadJson = """[{"id":"r1","regionCode":"R-001","hasChildren":1,"havDevice":1,"name":"一级目录A","level":1,"regionGBId":"GB-001"}]""";
        var encryptedPayload = EncryptToBase64(rsa, payloadJson);
        using var client = CreateClient(
            rsa.ExportRSAPrivateKeyPem(),
            CreateHandler(
                CreateJsonResponse("""{"code":0,"msg":"成功","data":{"accessToken":"token","refreshToken":"refresh","expiresIn":3600}}"""),
                CreateJsonResponse($$"""{"code":0,"msg":"成功","data":"{{encryptedPayload}}"}""")));

        var result = await client.GetRegionListAsync(string.Empty, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        var region = Assert.Single(result.Payload!);
        Assert.Equal("r1", region.Id);
        Assert.Equal("R-001", region.RegionCode);
        Assert.Equal(1, region.HasChildren);
        Assert.Equal(1, region.HavDevice);
    }

    [Fact]
    public async Task GetRegionDevicePageAsync_ParsesPagedDevices_WhenPayloadIsEncryptedString()
    {
        using var rsa = RSA.Create(2048);
        var payloadJson = """{"list":[{"deviceCode":"d1","deviceName":"设备1"},{"deviceCode":"d2","deviceName":"设备2"}],"pageNo":1,"pageSize":2,"totalCount":3}""";
        var encryptedPayload = EncryptToBase64(rsa, payloadJson);
        using var client = CreateClient(
            rsa.ExportRSAPrivateKeyPem(),
            CreateHandler(
                CreateJsonResponse("""{"code":0,"msg":"成功","data":{"accessToken":"token","refreshToken":"refresh","expiresIn":3600}}"""),
                CreateJsonResponse($$"""{"code":0,"msg":"成功","data":"{{encryptedPayload}}"}""")));

        var result = await client.GetRegionDevicePageAsync("r1", 1, 50, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal(2, result.Payload!.Items.Count);
        Assert.Equal(1, result.Payload.PageNo);
        Assert.Equal(2, result.Payload.PageSize);
        Assert.Equal(3, result.Payload.TotalCount);
        Assert.Equal("d2", result.Payload.Items[1].DeviceCode);
    }

    [Fact]
    public async Task GetRegionDeviceCountsAsync_ParsesCountList_WhenPayloadIsEncryptedString()
    {
        using var rsa = RSA.Create(2048);
        var payloadJson = """[{"regionCode":"R-001","deviceCount":4,"onlineCount":3},{"regionCode":"R-002","deviceCount":0,"onlineCount":0}]""";
        var encryptedPayload = EncryptToBase64(rsa, payloadJson);
        using var client = CreateClient(
            rsa.ExportRSAPrivateKeyPem(),
            CreateHandler(
                CreateJsonResponse("""{"code":0,"msg":"成功","data":{"accessToken":"token","refreshToken":"refresh","expiresIn":3600}}"""),
                CreateJsonResponse($$"""{"code":0,"msg":"成功","data":"{{encryptedPayload}}"}""")));

        var result = await client.GetRegionDeviceCountsAsync(string.Empty, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal(2, result.Payload!.Count);
        Assert.Equal("R-001", result.Payload[0].RegionCode);
        Assert.Equal(4, result.Payload[0].DeviceCount);
        Assert.Equal(3, result.Payload[0].OnlineCount);
    }

    private static OpenPlatformClient CreateClient(string rsaPrivateKey, HttpMessageHandler handler)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "tysl-ins-tests", Guid.NewGuid().ToString("N"));
        var dataPath = Path.Combine(rootPath, "data");
        Directory.CreateDirectory(dataPath);

        var options = new TianyiOpenPlatformOptions
        {
            BaseUrl = "https://vcp.21cn.com",
            ApiVersion = "2.0",
            ClientType = 3,
            Version = "1.1",
            AppId = "app-id",
            AppSecret = "app-secret",
            RsaPrivateKey = rsaPrivateKey,
            EnterpriseUser = "enterprise-user"
        };
        var runtimePaths = new AppRuntimePaths(
            rootPath,
            Path.Combine(rootPath, "logs"),
            dataPath,
            Path.Combine(dataPath, "inspection.db"),
            Path.Combine(dataPath, "token-cache.json"));

        return new OpenPlatformClient(
            options,
            runtimePaths,
            NullLogger<OpenPlatformClient>.Instance,
            new HttpClient(handler));
    }

    private static HttpMessageHandler CreateHandler(params HttpResponseMessage[] responses)
    {
        return new StubHttpMessageHandler(responses);
    }

    private static HttpResponseMessage CreateJsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string CreatePrivateKeyPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }

    private static string EncryptToBase64(RSA rsa, string payloadJson)
    {
        return Convert.ToBase64String(
            rsa.Encrypt(Encoding.UTF8.GetBytes(payloadJson), RSAEncryptionPadding.Pkcs1));
    }

    private static string EncryptInBlocksToBase64(RSA rsa, string payloadJson)
    {
        var plainBytes = Encoding.UTF8.GetBytes(payloadJson);
        var maxPlainBlockSize = (rsa.KeySize / 8) - 11;
        using var cipherStream = new MemoryStream();
        for (var offset = 0; offset < plainBytes.Length; offset += maxPlainBlockSize)
        {
            var blockSize = Math.Min(maxPlainBlockSize, plainBytes.Length - offset);
            var cipherBlock = rsa.Encrypt(
                plainBytes.AsSpan(offset, blockSize).ToArray(),
                RSAEncryptionPadding.Pkcs1);
            cipherStream.Write(cipherBlock, 0, cipherBlock.Length);
        }

        return Convert.ToBase64String(cipherStream.ToArray());
    }

    private sealed class StubHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.NotEmpty(responses);
            return Task.FromResult(responses.Dequeue());
        }
    }
}
