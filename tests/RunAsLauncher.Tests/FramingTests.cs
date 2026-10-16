using System.Buffers.Binary;
using System.Text;
using RunAsLauncher.Ipc;
using RunAsLauncher.Policies;

namespace RunAsLauncher.Tests;

public sealed class FramingTests
{
    private static MemoryStream RawFrame(string json) => RawFrame(Encoding.UTF8.GetBytes(json));

    private static MemoryStream RawFrame(byte[] body, int? declaredLength = null)
    {
        var ms = new MemoryStream();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, declaredLength ?? body.Length);
        ms.Write(header);
        ms.Write(body);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task Request_RoundTrip()
    {
        var sent = new Request(1, RequestType.Run, "notepad-admin");
        using var ms = new MemoryStream();
        await Framing.WriteAsync(ms, sent);
        ms.Position = 0;

        var got = await Framing.ReadAsync<Request>(ms);
        Assert.Equal(sent, got);
        Assert.Equal(ms.Length, ms.Position);
    }

    [Fact]
    public async Task Response_RoundTrip_WithItems()
    {
        var items = new[] { new ItemView("cmd", "Prompt", @"C:\Windows\System32\cmd.exe", RunMode.Elevated, null) };
        var sent = new Response(1, ResultCode.Ok, null, items, 1234);
        using var ms = new MemoryStream();
        await Framing.WriteAsync(ms, sent);
        ms.Position = 0;

        var got = await Framing.ReadAsync<Response>(ms);
        Assert.Equal(ResultCode.Ok, got.Code);
        Assert.Equal(1234, got.ProcessId);
        Assert.Equal(items[0], Assert.Single(got.Items!));
    }

    [Fact]
    public async Task Wire_IsLittleEndianPrefix_CamelCaseJson_EnumsAsString()
    {
        using var ms = new MemoryStream();
        await Framing.WriteAsync(ms, new Request(1, RequestType.Ping, null));
        byte[] bytes = ms.ToArray();

        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        Assert.Equal(bytes.Length - 4, length);
        string json = Encoding.UTF8.GetString(bytes, 4, length);
        Assert.Contains("\"type\":\"ping\"", json);
        Assert.Contains("\"version\":1", json);
    }

    [Theory]
    [InlineData(Framing.MaxRequestBytes + 1)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Read_InvalidLengthPrefix_Throws(int declared)
    {
        using var ms = RawFrame([], declared);
        await Assert.ThrowsAsync<InvalidDataException>(() => Framing.ReadAsync<Request>(ms));
    }

    [Fact]
    public async Task Read_ResponseLimitIsLargerThanRequestLimit()
    {
        // 20 KiB declarado: acima do limite de requisição, abaixo do de resposta.
        using var req = RawFrame([], 20 * 1024);
        await Assert.ThrowsAsync<InvalidDataException>(() => Framing.ReadAsync<Request>(req));

        using var resp = RawFrame([], 20 * 1024);
        await Assert.ThrowsAsync<EndOfStreamException>(() => Framing.ReadAsync<Response>(resp));
    }

    [Fact]
    public async Task Write_OversizedResponse_Throws()
    {
        var big = new Response(1, ResultCode.Ok, new string('x', Framing.MaxResponseBytes), null, null);
        using var ms = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => Framing.WriteAsync(ms, big));
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public async Task Read_TruncatedBody_Throws()
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"version\":1,\"type\":\"ping\"}");
        using var ms = RawFrame(body[..10], body.Length);
        await Assert.ThrowsAsync<EndOfStreamException>(() => Framing.ReadAsync<Request>(ms));
    }

    [Fact]
    public async Task Read_TruncatedHeader_Throws()
    {
        using var ms = new MemoryStream([0x10, 0x00]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => Framing.ReadAsync<Request>(ms));
    }

    [Theory]
    [InlineData("{\"version\":1,\"type\":\"format\",\"itemId\":null}")]
    [InlineData("{\"version\":1,\"type\":99,\"itemId\":null}")]
    [InlineData("{\"version\":1,\"type\":\"Run2\"}")]
    public async Task Read_UnknownType_Throws(string json)
    {
        using var ms = RawFrame(json);
        await Assert.ThrowsAsync<InvalidDataException>(() => Framing.ReadAsync<Request>(ms));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public async Task Read_MalformedJson_Throws(string json)
    {
        using var ms = RawFrame(json);
        await Assert.ThrowsAsync<InvalidDataException>(() => Framing.ReadAsync<Request>(ms));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("Notepad")]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 41 caracteres
    public async Task Read_InvalidItemId_Throws(string itemId)
    {
        string json = $"{{\"version\":1,\"type\":\"run\",\"itemId\":\"{itemId}\"}}";
        using var ms = RawFrame(json);
        await Assert.ThrowsAsync<InvalidDataException>(() => Framing.ReadAsync<Request>(ms));
    }

    [Fact]
    public async Task Write_InvalidItemId_Throws()
    {
        using var ms = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(
            () => Framing.WriteAsync(ms, new Request(1, RequestType.Run, "BAD ID")));
    }
}
