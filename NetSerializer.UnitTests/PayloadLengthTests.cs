using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace NetSerializer.UnitTests;

[TestFixture]
public sealed class PayloadLengthTests
{
    private sealed class FragmentedStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _seekable;
        public FragmentedStream(byte[] bytes, bool seekable) { _inner = new MemoryStream(bytes); _seekable = seekable; }
        public override bool CanRead => true;
        public override bool CanSeek => _seekable;
        public override bool CanWrite => false;
        public override long Length => _seekable ? _inner.Length : throw new NotSupportedException();
        public override long Position { get => _seekable ? _inner.Position : throw new NotSupportedException(); set { if (!_seekable) throw new NotSupportedException(); _inner.Position = value; } }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 3));
        public override int ReadByte() => _inner.ReadByte();
        public override long Seek(long offset, SeekOrigin origin) => _seekable ? _inner.Seek(offset, origin) : throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    private static byte[] Prefix(uint bytes, uint? chars = null, byte[] payload = null)
    {
        using var stream = new MemoryStream();
        Primitives.WritePrimitive(stream, bytes);
        if (chars.HasValue) Primitives.WritePrimitive(stream, chars.Value);
        if (payload != null) stream.Write(payload, 0, payload.Length);
        return stream.ToArray();
    }

    private static void AssertRejected(TestDelegate read)
    {
        Assert.That(Assert.Catch(read), Is.TypeOf<InvalidDataException>().Or.TypeOf<EndOfStreamException>());
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ValidStringsAndFollowingValueRoundTrip(bool seekable)
    {
        foreach (var original in new[] { null, "", "hello", "h\u00e9llo \u2622 \U0001f600", new string('x', 12000) })
        {
            using var encoded = new MemoryStream();
            Primitives.WritePrimitive(encoded, original);
            Primitives.WritePrimitive(encoded, 123u);
            using var stream = new FragmentedStream(encoded.ToArray(), seekable);
            Primitives.ReadPrimitive(stream, out string decoded);
            Primitives.ReadPrimitive(stream, out uint following);
            Assert.That(decoded, Is.EqualTo(original));
            Assert.That(following, Is.EqualTo(123u));
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ValidArraysAndFollowingValueRoundTrip(bool seekable)
    {
        foreach (var original in new[] { null, Array.Empty<byte>(), Enumerable.Range(0, 12000).Select(x => (byte)x).ToArray() })
        {
            using var encoded = new MemoryStream();
            Primitives.WritePrimitive(encoded, original);
            Primitives.WritePrimitive(encoded, 123u);
            using var stream = new FragmentedStream(encoded.ToArray(), seekable);
            Primitives.ReadPrimitive(stream, out byte[] decoded);
            Primitives.ReadPrimitive(stream, out uint following);
            Assert.That(decoded, Is.EqualTo(original));
            Assert.That(following, Is.EqualTo(123u));
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void InconsistentCharacterCountsAreRejected(bool seekable)
    {
        foreach (var chars in new[] { 0u, 1000000u, uint.MaxValue })
        {
            using var stream = new FragmentedStream(Prefix(3u, chars, new byte[] { 65, 65 }), seekable);
            Assert.Throws<InvalidDataException>(() => Primitives.ReadPrimitive(stream, out string _));
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void CharacterCountMustMatchDecodedPayload(bool seekable)
    {
        using var stream = new FragmentedStream(Prefix(4u, 2u, Encoding.UTF8.GetBytes("\u2622")), seekable);
        Assert.Throws<InvalidDataException>(() => Primitives.ReadPrimitive(stream, out string _));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void UnsupportedLengthsAreRejectedBeforeAllocation(bool seekable)
    {
        using var bytes = new FragmentedStream(Prefix(uint.MaxValue), seekable);
        Assert.Throws<InvalidDataException>(() => Primitives.ReadPrimitive(bytes, out byte[] _));
        using var text = new FragmentedStream(Prefix(uint.MaxValue, 1u), seekable);
        Assert.Throws<InvalidDataException>(() => Primitives.ReadPrimitive(text, out string _));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void RejectsAdvertisedAllocationWithoutPayload(bool seekable)
    {
        // Keep this reproduction small enough to run safely against the original reader.
        const uint claim = 4 * 1024 * 1024;
        using var warmup = new FragmentedStream(Prefix(4u, 4u), seekable);
        Assert.Throws<InvalidDataException>(() => Primitives.ReadPrimitive(warmup, out string _));
        using var text = new FragmentedStream(Prefix(claim + 1, claim, new byte[] { 65 }), seekable);
        var before = GC.GetAllocatedBytesForCurrentThread();
        AssertRejected(() => Primitives.ReadPrimitive(text, out string _));
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.LessThan(128 * 1024), "Allocated memory based on the forged string length");
        using var bytes = new FragmentedStream(Prefix(claim + 1, payload: new byte[] { 65 }), seekable);
        before = GC.GetAllocatedBytesForCurrentThread();
        AssertRejected(() => Primitives.ReadPrimitive(bytes, out byte[] _));
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.LessThan(128 * 1024), "Allocated memory based on the forged byte-array length");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ActualSerializerRejectsMalformedPayload(bool seekable)
    {
        var serializer = new Serializer(new[] { typeof(string), typeof(byte[]) });
        using var text = new FragmentedStream(Prefix(1000001u, 1000000u, new byte[] { 65 }), seekable);
        AssertRejected(() => serializer.DeserializeDirect(text, out string _));
        using var bytes = new FragmentedStream(Prefix(1000001u, payload: new byte[] { 65 }), seekable);
        AssertRejected(() => serializer.DeserializeDirect(bytes, out byte[] _));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void TruncatedPrefixesAreRejected(bool seekable)
    {
        using var text = new FragmentedStream(new byte[] { 0x80 }, seekable);
        Assert.Throws<EndOfStreamException>(() => Primitives.ReadPrimitive(text, out string _));
        using var bytes = new FragmentedStream(new byte[] { 0x80 }, seekable);
        Assert.Throws<EndOfStreamException>(() => Primitives.ReadPrimitive(bytes, out byte[] _));
    }
}
