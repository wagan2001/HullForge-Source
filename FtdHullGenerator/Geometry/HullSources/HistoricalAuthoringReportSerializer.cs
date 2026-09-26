using System.Text.Json;
using FtdHullGenerator.Domain.Historical;

namespace FtdHullGenerator.Geometry.HullSources;

/// <summary>Serializes only the bounded H02 report DTO and stops before exceeding its byte cap.</summary>
public static class HistoricalAuthoringReportSerializer
{
    public const int DefaultMaxReportBytes = 1024 * 1024;

    public static byte[] Serialize(
        HistoricalAuthoringReport report,
        JsonSerializerOptions options,
        int maxBytes = DefaultMaxReportBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(options);
        if (maxBytes is < 1 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        cancellationToken.ThrowIfCancellationRequested();
        var effectiveOptions = new JsonSerializerOptions(options);
        effectiveOptions.Converters.Insert(0, new HistoricalAuthoringOutcomeJsonConverter());
        using var stream = new CappedMemoryStream(maxBytes, cancellationToken);
        JsonSerializer.SerializeAsync(stream, report, effectiveOptions, cancellationToken)
            .GetAwaiter().GetResult();
        cancellationToken.ThrowIfCancellationRequested();
        return stream.ToArray();
    }

    public static bool WriteAtomic(
        string path,
        HistoricalAuthoringReport report,
        JsonSerializerOptions options,
        out string? error,
        int maxBytes = DefaultMaxReportBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = Serialize(report, options, maxBytes, cancellationToken);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var temporary = Path.Combine(directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                const int chunkBytes = 64 * 1024;
                for (var offset = 0; offset < bytes.Length; offset += chunkBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stream.Write(bytes, offset, Math.Min(chunkBytes, bytes.Length - offset));
                }
                cancellationToken.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, overwrite: true);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class CappedMemoryStream : Stream
    {
        private readonly int _maxBytes;
        private readonly CancellationToken _cancellationToken;
        private readonly MemoryStream _inner = new();

        public CappedMemoryStream(int maxBytes, CancellationToken cancellationToken)
        {
            _maxBytes = maxBytes;
            _cancellationToken = cancellationToken;
        }

        public byte[] ToArray() => _inner.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _cancellationToken.ThrowIfCancellationRequested();

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            EnsureCapacity(count);
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            EnsureCapacity(buffer.Length);
            _inner.Write(buffer);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _cancellationToken.ThrowIfCancellationRequested();
            EnsureCapacity(buffer.Length);
            _inner.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        private void EnsureCapacity(int additionalBytes)
        {
            if (_inner.Length + additionalBytes > _maxBytes)
                throw new InvalidDataException(
                    $"Historical authoring report exceeds the {_maxBytes}-byte output limit.");
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
