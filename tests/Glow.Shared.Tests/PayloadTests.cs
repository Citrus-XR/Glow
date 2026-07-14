using Glow.Shared;
using Xunit;

namespace Glow.Shared.Tests;

public class PayloadCodecTests
{
    static ReadOnlyMemory<byte> Round(Action<PayloadWriter> write)
    {
        var w = new PayloadWriter();
        write(w);
        return w.ToPayload();
    }

    [Fact]
    public void Primitives_RoundTrip()
    {
        var w = new PayloadWriter();
        w.PutBool(true);
        w.PutByte(0xAB);
        w.PutSByte(-42);
        w.PutShort(-30_000);
        w.PutUShort(60_000);
        w.PutInt(-int.MaxValue);
        w.PutUInt(uint.MaxValue);
        w.PutLong(long.MinValue);
        w.PutULong(ulong.MaxValue);
        w.PutFloat(3.14f);
        w.PutDouble(2.71828);
        w.PutString("日本語 🎮");
        w.PutBytes(new byte[] { 1, 2, 3, 4 });

        var r = new PayloadReader(w.ToPayload());
        Assert.True(r.GetBool());
        Assert.Equal(0xAB, r.GetByte());
        Assert.Equal(-42, r.GetSByte());
        Assert.Equal(-30_000, r.GetShort());
        Assert.Equal(60_000, r.GetUShort());
        Assert.Equal(-int.MaxValue, r.GetInt());
        Assert.Equal(uint.MaxValue, r.GetUInt());
        Assert.Equal(long.MinValue, r.GetLong());
        Assert.Equal(ulong.MaxValue, r.GetULong());
        Assert.Equal(3.14f, r.GetFloat());
        Assert.Equal(2.71828, r.GetDouble());
        Assert.Equal("日本語 🎮", r.GetString());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, r.GetBytes());
        Assert.True(r.EndOfData);
    }

    [Fact]
    public void UnityShapedTypes_RoundTrip()
    {
        var w = new PayloadWriter();
        w.PutVec2(1, 2);
        w.PutVec3(3, 4, 5);
        w.PutVec4(6, 7, 8, 9);
        w.PutQuat(0, 0, 0, 1);
        w.PutColor(1, 0.5f, 0.25f, 1);
        w.PutColor32(255, 128, 64, 32);

        var r = new PayloadReader(w.ToPayload());
        Assert.Equal((1f, 2f), r.GetVec2());
        Assert.Equal((3f, 4f, 5f), r.GetVec3());
        Assert.Equal((6f, 7f, 8f, 9f), r.GetVec4());
        Assert.Equal((0f, 0f, 0f, 1f), r.GetQuat());
        Assert.Equal((1f, 0.5f, 0.25f, 1f), r.GetColor());
        Assert.Equal(((byte)255, (byte)128, (byte)64, (byte)32), r.GetColor32());
    }

    [Fact]
    public void PrimitiveArrays_RoundTrip()
    {
        var w = new PayloadWriter();
        w.PutIntArray(new[] { 1, 2, 3, -1 });
        w.PutFloatArray(new[] { 1.5f, -2.5f });
        w.PutStringArray(new[] { "a", "", "hello" });

        var r = new PayloadReader(w.ToPayload());
        Assert.Equal(new[] { 1, 2, 3, -1 }, r.GetIntArray());
        Assert.Equal(new[] { 1.5f, -2.5f }, r.GetFloatArray());
        Assert.Equal(new[] { "a", "", "hello" }, r.GetStringArray());
    }

    [Fact]
    public void EmptyPayload_HasZeroLength()
    {
        var w = new PayloadWriter();
        Assert.Equal(0, w.Length);
        var payload = w.ToPayload();
        Assert.Equal(0, payload.Length);
    }
}
