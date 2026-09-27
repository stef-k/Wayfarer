namespace Wayfarer.Middleware;

/// <summary>
/// Observes only host-generated 413 failures so MVC cannot obscure them as antiforgery/model errors.
/// Does not count, buffer, parse, or own the request stream; the host remains the size authority.
/// </summary>
internal sealed class UploadBodyStream(Stream inner) : Stream
{
    /// <summary>True only after the host rejected a body read for exceeding its request ceiling.</summary>
    internal bool SizeRejected { get; private set; }

    /// <inheritdoc />
    public override bool CanRead => inner.CanRead;
    /// <inheritdoc />
    public override bool CanSeek => inner.CanSeek;
    /// <inheritdoc />
    public override bool CanWrite => inner.CanWrite;
    /// <inheritdoc />
    public override long Length => inner.Length;
    /// <inheritdoc />
    public override long Position { get => inner.Position; set => inner.Position = value; }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        try { return inner.Read(buffer, offset, count); }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            SizeRejected = true;
            throw;
        }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try { return await inner.ReadAsync(buffer, cancellationToken); }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            SizeRejected = true;
            throw;
        }
    }

    /// <inheritdoc />
    public override void Flush() => inner.Flush();
    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    /// <inheritdoc />
    public override void SetLength(long value) => inner.SetLength(value);
    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
}
