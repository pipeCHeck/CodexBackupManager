using System;
using System.Text;
using CodexBackupManager.Codex.Inspection;
using Xunit;

namespace CodexBackupManager.Codex.Tests.Inspection;

/// <summary>
/// Phase 9_5a-T1 — <see cref="GlobalStateJson"/>이 JS <c>JSON.stringify</c> 형식을 바이트까지 같게 다시 만드는지(왕복). 입력은 손으로 쓴
/// stringify 형식 바이트다(직렬화기로 만든 바이트를 다시 넣는 동어반복 검사가 아니다).
/// </summary>
public sealed class GlobalStateJsonTests
{
    private static string RoundTrip(string json) => GlobalStateJson.Serialize(GlobalStateJson.Parse(json));

    [Theory]
    // 제어 문자: \b \f \n \r \t는 짧은 이스케이프, 나머지는 \u00xx 소문자
    [InlineData("{\"a\":\"x\\by\\fz\\n\\r\\t\\u0000\\u0001\\u001f\\u000b\"}")]
    // 짝 없는 서로게이트는 \udxxx 소문자, 짝이 맞는 이모지는 그대로
    [InlineData("{\"lone\":\"\\ud800\",\"low\":\"\\udfff\",\"pair\":\"😀\",\"mixed\":\"a\\ud83db😀\"}")]
    // 백슬래시 + 슬래시 원문 문자열(\\/), 따옴표, 백슬래시만
    [InlineData("{\"path\":\"C:\\\\Users\\\\a\\\\/b\",\"q\":\"\\\"\",\"bs\":\"\\\\\"}")]
    // 비ASCII(한글), DEL, U+2028/2029는 그대로
    [InlineData("{\"이름\":\"삼각형 3개\",\"del\":\"\u007f\",\"ls\":\"\u2028\u2029\",\"slash\":\"a/b\"}")]
    // 큰 정수, 음수, 지수·소수 원문 보존(재포맷하지 않는다)
    [InlineData("{\"big\":12345678901234567890123,\"ms\":1759300000000,\"neg\":-0,\"e\":1e+21,\"E\":1.5E-7,\"f\":0.1,\"g\":-12.5e3}")]
    // 빈 객체·배열, 중첩, 리터럴, 키 순서(정렬하지 않는다)
    [InlineData("{\"z\":{},\"a\":[],\"m\":[{},[],[1,[2,{\"k\":null}]]],\"t\":true,\"f\":false,\"n\":null}")]
    [InlineData("[]")]
    [InlineData("\"top\"")]
    public void 왕복하면_JS_stringify_형식_바이트가_같다(string json)
    {
        byte[] original = GlobalStateJson.StrictUtf8.GetBytes(json);
        byte[] again = GlobalStateJson.StrictUtf8.GetBytes(RoundTrip(json));
        Assert.Equal(original, again);
    }

    [Theory]
    [InlineData("{ \"a\": 1 }")]              // 공백(들여쓰기)
    [InlineData("{\"a\":\"\\/\"}")]           // stringify는 / 를 이스케이프하지 않는다
    [InlineData("{\"a\":\"\\u00E9\"}")]       // 비ASCII를 이스케이프하지 않는다(대문자 hex도 아님)
    [InlineData("{\"a\":\"\\u001F\"}")]       // 제어 문자 이스케이프는 소문자 hex
    [InlineData("{\"a\":\"\\u000a\"}")]       // \n은 짧은 이스케이프
    [InlineData("{\"a\":\"\\uD800\"}")]       // 짝 없는 서로게이트도 소문자
    public void stringify_형식이_아니면_다시_쓴_바이트가_다르다(string json)
        => Assert.NotEqual(json, RoundTrip(json));

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]          // 중복 키
    [InlineData("{\"a\":01}")]
    [InlineData("{\"a\":1.}")]
    [InlineData("{\"a\":\"\u0001\"}")]          // 이스케이프하지 않은 제어 문자
    [InlineData("{\"a\":tru}")]
    [InlineData("{\"a\":1}x")]
    [InlineData("{\"a\":\"\\x\"}")]
    public void 잘못된_JSON은_거부한다(string json)
        => Assert.Throws<FormatException>(() => GlobalStateJson.Parse(json));

    [Fact]
    public void 원문을_주면_바꾸지_않은_값은_원문_조각을_그대로_쓰고_바꾼_컨테이너만_다시_쓴다()
    {
        // 일부러 stringify 형식이 아닌 값(공백, \/)을 넣어 "원문 조각 재사용"과 "다시 쓰기"를 구별한다.
        const string json = "{\"keep\": [1, 2],\"esc\":\"a\\/b\",\"list\":[\"x\"]}";
        var root = (GlobalStateJsonObject)GlobalStateJson.Parse(json);
        var list = (GlobalStateJsonArray)root.Get("list")!;
        list.Items.Add(new GlobalStateJsonString("y"));
        list.MarkModified();
        root.MarkModified();

        string written = GlobalStateJson.Serialize(root, json);

        Assert.Equal("{\"keep\":[1, 2],\"esc\":\"a\\/b\",\"list\":[\"x\",\"y\"]}", written);
    }

    [Fact]
    public void 새_문자열은_JS_규칙으로_이스케이프한다()
    {
        var builder = new StringBuilder();
        GlobalStateJson.WriteString(builder, "C:\\a/b \"q\" \u0001\n한\ud800😀");
        Assert.Equal("\"C:\\\\a/b \\\"q\\\" \\u0001\\n한\\ud800😀\"", builder.ToString());
    }
}
