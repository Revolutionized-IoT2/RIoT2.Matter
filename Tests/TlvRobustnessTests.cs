using System.Buffers;
using RIoT2.Matter.Tlv;
using Xunit;

namespace RIoT2.Matter.Tests;

public sealed class TlvRobustnessTests
{
    [Fact]
    public void CopierRejectsUnterminatedContainer()
    {
        var reader = new TlvReader([(byte)TlvElementType.Structure]);
        Assert.True(reader.Read());

        InvalidDataException? exception = null;
        try { TlvCopier.Capture(ref reader); }
        catch (InvalidDataException ex) { exception = ex; }
        Assert.NotNull(exception);
    }

    [Fact]
    public void SkipRejectsUnterminatedContainer()
    {
        var reader = new TlvReader([(byte)TlvElementType.Array]);
        Assert.True(reader.Read());

        InvalidDataException? exception = null;
        try { TlvCopier.Skip(ref reader); }
        catch (InvalidDataException ex) { exception = ex; }
        Assert.NotNull(exception);
    }

    [Fact]
    public void CopierRejectsExcessiveContainerDepth()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);
        for (var i = 0; i <= TlvCopier.MaxNestingDepth; i++)
        {
            writer.StartArray(i == 0 ? TlvTag.Anonymous : TlvTag.ContextSpecific(1));
        }

        for (var i = 0; i <= TlvCopier.MaxNestingDepth; i++)
        {
            writer.EndContainer();
        }

        var reader = new TlvReader(buffer.WrittenSpan);
        Assert.True(reader.Read());

        InvalidDataException? exception = null;
        try { TlvCopier.Capture(ref reader); }
        catch (InvalidDataException ex) { exception = ex; }
        Assert.NotNull(exception);
    }
}
