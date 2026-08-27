using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Defines the bounded ownership contract for the location-import worker.</summary>
public sealed class BoundedLocationImportContractTests
{
    [Fact]
    public void ParserContract_StreamsLocationsInsteadOfReturningCompleteList()
    {
        var parse = typeof(ILocationDataParser).GetMethod("ParseAsync");

        Assert.NotNull(parse);
        Assert.Equal(typeof(IAsyncEnumerable<Location>), parse!.ReturnType);
        Assert.Contains(parse.GetParameters(), parameter =>
            parameter.ParameterType == typeof(CancellationToken));
    }

    [Fact]
    public void ImportWorker_ReceivesFactoryForPhaseScopedContexts()
    {
        var constructors = typeof(LocationImportService).GetConstructors(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);

        Assert.Contains(constructors.SelectMany(constructor => constructor.GetParameters()), parameter =>
            parameter.ParameterType == typeof(IDbContextFactory<ApplicationDbContext>));
    }

    [Fact]
    public void Deduplicator_KeyLookupAcceptsOnlyCurrentBatchKeys()
    {
        var filter = typeof(LocationImportDeduplicator).GetMethod(
            "FilterAsync",
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(filter);
        Assert.Contains(filter!.GetParameters(), parameter =>
            parameter.Name == "batchKeys" &&
            parameter.ParameterType == typeof(IReadOnlySet<Guid>));
    }

    [Fact]
    public void ImportWorker_DeclaresBoundedBatchSize()
    {
        var batchSize = typeof(LocationImportService).GetField(
            "BatchSize",
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(batchSize);
        Assert.Equal(50, batchSize!.GetRawConstantValue());
    }

    [Fact]
    public void RestartCursor_IsAppliedByStreamingWorkerRatherThanParserMaterialization()
    {
        var materializingHelper = typeof(LocationImportService).GetMethod(
            "GetLocationsToProcess",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);

        Assert.Null(materializingHelper);
    }

    [Fact]
    public async Task GoogleParser_FirstLocationIsAvailableBeforeUnreadHistory()
    {
        const string prefix = "{\"semanticSegments\":[{\"timelinePath\":[" +
            "{\"point\":\"40.1°, 22.2°\",\"time\":\"2026-08-25T10:00:00Z\"},";
        await using var stream = new ThrowAfterFirstReadStream(Encoding.UTF8.GetBytes(prefix));
        var parser = new GoogleTimelineJsonParser(NullLogger<GoogleTimelineJsonParser>.Instance);
        await using var enumerator = parser.ParseAsync(stream, "owner").GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(40.1, enumerator.Current.Coordinates.Y, 3);
        Assert.Equal(1, stream.ReadCount);
    }

    private sealed class ThrowAfterFirstReadStream(byte[] prefix) : Stream
    {
        private int position;
        internal int ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => prefix.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            ReadCount++;
            if (ReadCount > 1) throw new IOException("Parser read beyond the first available record.");
            prefix.CopyTo(buffer);
            position = prefix.Length;
            return prefix.Length;
        }
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
