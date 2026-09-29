using System.Text.Json;

public class AlibabaProtocolTests
{
    [Fact]
    public void Sign_MatchesIndependentHmacSha256Vector()
    {
        var parameters = new Dictionary<string, string>
        {
            ["app_key"] = "123456",
            ["timestamp"] = "1517820392000",
            ["sign_method"] = "sha256",
            ["code"] = "abc",
            ["sign"] = "ignored"
        };

        // Vector computed with Node's crypto.createHmac over "/auth/token/create" + sorted k/v pairs.
        Assert.Equal(
            "DB7357BB7F1362C85060A8777A39E53C639BD3AAF0FDE47BF2A2F029409E7C38",
            IopSigner.Sign("/auth/token/create", parameters, "helloworld"));
    }

    [Fact]
    public void Parameters_FlattenNestedObjectsAsJson()
    {
        var result = IopParameters.From(new { product_id = 123L, product_info = new { basic_info = new { title = "x" } }, skip = (string?)null, flag = true });

        Assert.Equal("123", result["product_id"]);
        Assert.Equal("{\"basic_info\":{\"title\":\"x\"}}", result["product_info"]);
        Assert.Equal("true", result["flag"]);
        Assert.False(result.ContainsKey("skip"));
    }

    [Theory]
    [InlineData("{\"type\":\"ISV\",\"code\":\"IncompleteSignature\",\"message\":\"bad sign\",\"request_id\":\"r1\"}", false, "IncompleteSignature")]
    [InlineData("{\"code\":\"InvalidCode\",\"type\":\"ISP\",\"message\":\"Invalid authorization code\"}", false, "InvalidCode")]
    [InlineData("{\"result\":{\"success\":false,\"msg_code\":\"B_TITLE_NOT_FOUND\",\"message\":\"Title not found.\"},\"code\":\"0\"}", false, "B_TITLE_NOT_FOUND")]
    [InlineData("{\"success\":false,\"msg_code\":\"B_PRODUCT_PARAM_INVALID\",\"message\":\"bad\"}", false, "B_PRODUCT_PARAM_INVALID")]
    [InlineData("{\"result\":{\"error_code\":\"E1\",\"error_msg\":\"upload failed\"}}", false, "E1")]
    [InlineData("{\"result\":{\"data\":1601454338774,\"success\":true},\"code\":\"0\",\"request_id\":\"r2\"}", true, null)]
    [InlineData("{\"access_token\":\"t\",\"expires_in\":3600,\"code\":\"0\"}", true, null)]
    public void ResponseParser_DetectsGatewayAndBusinessErrors(string body, bool success, string? code)
    {
        var parsed = AlibabaResponseParser.Parse(200, body);

        Assert.Equal(success, parsed.Success);
        Assert.Equal(code, parsed.Code);
    }

    [Fact]
    public void ResponseParser_NonJsonIsFailure()
    {
        var parsed = AlibabaResponseParser.Parse(404, "<html>Not Found</html>");

        Assert.False(parsed.Success);
        Assert.Equal("HTTP404", parsed.Code);
    }

    [Fact]
    public void ExtractProductId_ReadsNumericResultData()
    {
        using var document = JsonDocument.Parse("{\"result\":{\"data\":1601454338774,\"success\":true}}");

        Assert.Equal("1601454338774", ProductOperations.ExtractProductId(document.RootElement.Clone()));
    }

    [Fact]
    public void TokenRecord_ReadsStringAndNumberExpiry()
    {
        using var document = JsonDocument.Parse("{\"access_token\":\"a\",\"refresh_token\":\"r\",\"expires_in\":3600,\"refresh_expires_in\":\"7200\",\"account\":\"seller@x.com\"}");
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        var token = AlibabaTokenRecord.FromResponse(document.RootElement, now);

        Assert.Equal(now.AddHours(1), token.AccessTokenExpiresAt);
        Assert.Equal(now.AddHours(2), token.RefreshTokenExpiresAt);
        Assert.Equal("seller@x.com", token.Account);
    }

    [Theory]
    [InlineData("3_500332_abc", "3_500332_abc")]
    [InlineData("https://erp.soford.cn/openapi/callback?code=3_500332_abc&state=xyz", "3_500332_abc")]
    [InlineData("https://erp.soford.cn/openapi/callback?state=xyz&code=a%2Bb", "a+b")]
    public void ExtractCode_AcceptsCodeOrCallbackUrl(string input, string expected)
    {
        Assert.Equal(expected, ApiEndpoints.ExtractCode(input));
    }

    [Fact]
    public void DotEnv_ParsesKeysAndSkipsHostSettings()
    {
        var values = DotEnv.Parse(["# comment", "ASPNETCORE_ENVIRONMENT=Production", "Alibaba__AppKey=\"500332\"", "Empty=", "﻿Cors__AllowedOrigins__0=https://x"]).ToDictionary(x => x.Key, x => x.Value);

        Assert.Equal("500332", values["Alibaba:AppKey"]);
        Assert.Equal("https://x", values["Cors:AllowedOrigins:0"]);
        Assert.False(values.ContainsKey("ASPNETCORE_ENVIRONMENT"));
    }
}
