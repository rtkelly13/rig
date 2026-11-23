using System.Text.Json.Nodes;
using Rig.Core.Chrome.Extension;

namespace Rig.Core.Tests.Chrome.Extension;

public sealed class NativeFramesTests
{
    [Fact]
    public async Task A_frame_is_a_little_endian_length_then_the_json()
    {
        using var stream = new MemoryStream();

        await NativeFrames.WriteAsync(stream, new JsonObject { ["a"] = 1 }, TestContext.Current.CancellationToken);

        Assert.Equal([7, 0, 0, 0, .. "{\"a\":1}"u8.ToArray()], stream.ToArray());
    }

    [Fact]
    public async Task Frames_read_back_in_order_then_end_cleanly()
    {
        using var stream = new MemoryStream();
        await NativeFrames.WriteAsync(stream, new JsonObject { ["n"] = 1 }, TestContext.Current.CancellationToken);
        await NativeFrames.WriteAsync(stream, new JsonObject { ["n"] = "two" }, TestContext.Current.CancellationToken);
        stream.Position = 0;

        Assert.Equal(1, (int)(await NativeFrames.ReadAsync(stream, TestContext.Current.CancellationToken))!["n"]!);
        Assert.Equal("two", (string)(await NativeFrames.ReadAsync(stream, TestContext.Current.CancellationToken))!["n"]!);
        Assert.Null(await NativeFrames.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(new byte[] { 1, 0 })]
    [InlineData(new byte[] { 9, 0, 0, 0, (byte)'{' })]
    [InlineData(new byte[] { 2, 0, 0, 0, (byte)'[', (byte)']' })]
    [InlineData(new byte[] { 255, 255, 255, 255 })]
    public async Task A_cut_short_or_foreign_frame_is_an_error(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);

        await Assert.ThrowsAsync<NativeFrameException>(() => NativeFrames.ReadAsync(stream, TestContext.Current.CancellationToken));
    }
}
