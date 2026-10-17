using System.Text.Json;
using ZHS.Client;
using Xunit;

namespace ZHS.Client.Tests;

public sealed class NdjsonCodecTests
{
    [Fact]
    public void EncodeRequest_WithoutParams_MatchesWireShape()
    {
        var frame = NdjsonCodec.EncodeRequest(7, "session/list", null);

        Assert.Equal("""{"id":7,"method":"session/list"}""", frame);
    }

    [Fact]
    public void EncodeRequest_WithParams_UsesCamelCaseFields()
    {
        var frame = NdjsonCodec.EncodeRequest(8, "session/send", new { sessionId = "s1", content = "hi" });

        Assert.Equal("""{"id":8,"method":"session/send","params":{"sessionId":"s1","content":"hi"}}""", frame);
    }

    [Fact]
    public void DecodeFrames_ParsesMultipleFrames_AndKeepsPartialTail()
    {
        var input = "{\"id\":1,\"result\":{\"ok\":true}}\n{\"method\":\"startup/storageState\",\"params\":{}}\n{\"id\":2,";

        var decoded = NdjsonCodec.DecodeFrames(input);

        Assert.Equal(2, decoded.Frames.Count);
        Assert.Equal("{\"id\":2,", decoded.Rest);
    }

    [Fact]
    public void DecodeFrames_SkipsPollutedAndEmptyLines()
    {
        var input = "\n[warn] some child stderr noise\n{\"id\":1,\"result\":null}\nnot json at all\n";

        var decoded = NdjsonCodec.DecodeFrames(input);

        var frame = Assert.Single(decoded.Frames);
        Assert.Equal(1, frame.RootElement.GetProperty("id").GetInt64());
        Assert.Equal(string.Empty, decoded.Rest);
    }

    [Fact]
    public void DecodeFrames_ErrorEnvelopeParses()
    {
        var input = "{\"error\":{\"code\":-32601,\"message\":\"Method not found: bogus/method\"},\"id\":2}\n";

        var decoded = NdjsonCodec.DecodeFrames(input);

        var frame = Assert.Single(decoded.Frames);
        var error = frame.RootElement.GetProperty("error");
        Assert.Equal(-32601, error.GetProperty("code").GetInt32());
        Assert.Equal(2, frame.RootElement.GetProperty("id").GetInt64());
    }

    [Fact]
    public void DecodeFrames_EmptyInput_ReturnsNoFrames()
    {
        var decoded = NdjsonCodec.DecodeFrames(string.Empty);

        Assert.Empty(decoded.Frames);
        Assert.Equal(string.Empty, decoded.Rest);
    }
}
