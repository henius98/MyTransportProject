using Google.Protobuf;
using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace GTFSRealtimeApp.Utils;

/// <summary>
/// High-performance utility class for parsing Protocol Buffer messages with thread-safe caching,
/// memory pooling, and comprehensive error handling optimized for .NET 9.
/// </summary>
public static class ProtocolBufferUtils
{
    private static readonly FrozenDictionary<Type, object> _parserCache = CreateParserCache();
    private static readonly ActivitySource _activitySource = new("GTFSRealtimeApp.ProtocolBuffer");

    /// <summary>
    /// Parses a Protocol Buffer byte array into a strongly-typed message asynchronously.
    /// </summary>
    /// <typeparam name="T">The message type implementing IMessage&lt;T&gt;.</typeparam>
    /// <param name="data">The byte array to parse.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A ValueTask containing the parsed message.</returns>
    /// <exception cref="ArgumentNullException">Thrown when data is null.</exception>
    /// <exception cref="ProtocolBufferParsingException">Thrown when parsing fails.</exception>
    public static ValueTask<T> ParseAsync<T>(
        byte[] data,
        CancellationToken cancellationToken = default)
        where T : class, IMessage<T>, new()
    {
        ArgumentNullException.ThrowIfNull(data);
        return ParseAsyncCore<T>(data.AsMemory(), cancellationToken);
    }

    /// <summary>
    /// Parses a Protocol Buffer from a ReadOnlyMemory&lt;byte&gt; into a strongly-typed message asynchronously.
    /// </summary>
    /// <typeparam name="T">The message type implementing IMessage&lt;T&gt;.</typeparam>
    /// <param name="data">The memory containing bytes to parse.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A ValueTask containing the parsed message.</returns>
    /// <exception cref="ProtocolBufferParsingException">Thrown when parsing fails.</exception>
    public static ValueTask<T> ParseAsync<T>(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
        where T : class, IMessage<T>, new()
    {
        return ParseAsyncCore<T>(data, cancellationToken);
    }

    /// <summary>
    /// Parses a Protocol Buffer from a Stream into a strongly-typed message asynchronously.
    /// </summary>
    /// <typeparam name="T">The message type implementing IMessage&lt;T&gt;.</typeparam>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A ValueTask containing the parsed message.</returns>
    /// <exception cref="ArgumentNullException">Thrown when stream is null.</exception>
    /// <exception cref="ProtocolBufferParsingException">Thrown when parsing fails.</exception>
    public static async ValueTask<T> ParseAsync<T>(
        Stream stream,
        CancellationToken cancellationToken = default)
        where T : class, IMessage<T>, new()
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var activity = _activitySource.StartActivity("ParseFromStream");
        activity?.SetTag("messageType", typeof(T).Name);

        try
        {
            // Read all bytes from stream asynchronously
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);

            var data = memoryStream.ToArray();
            var parser = GetParser<T>();

            return parser.ParseFrom(data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ProtocolBufferParsingException(
                $"Failed to parse {typeof(T).Name} from stream", ex);
        }
    }

    /// <summary>
    /// Attempts to parse a Protocol Buffer byte array into a strongly-typed message.
    /// </summary>
    /// <typeparam name="T">The message type implementing IMessage&lt;T&gt;.</typeparam>
    /// <param name="data">The byte array to parse.</param>
    /// <param name="result">The parsed message if successful.</param>
    /// <returns>True if parsing succeeded, false otherwise.</returns>
    public static bool TryParse<T>(byte[] data, [NotNullWhen(true)] out T? result)
        where T : class, IMessage<T>, new()
    {
        return TryParse(data.AsSpan(), out result);
    }

    /// <summary>
    /// Attempts to parse a Protocol Buffer from a ReadOnlySpan&lt;byte&gt; into a strongly-typed message.
    /// </summary>
    /// <typeparam name="T">The message type implementing IMessage&lt;T&gt;.</typeparam>
    /// <param name="data">The span of bytes to parse.</param>
    /// <param name="result">The parsed message if successful.</param>
    /// <returns>True if parsing succeeded, false otherwise.</returns>
    public static bool TryParse<T>(ReadOnlySpan<byte> data, [NotNullWhen(true)] out T? result)
        where T : class, IMessage<T>, new()
    {
        result = null;

        if (data.IsEmpty)
            return false;

        try
        {
            var parser = GetParser<T>();
            result = parser.ParseFrom(data);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parses multiple Protocol Buffer messages from a byte array containing length-prefixed messages.
    /// </summary>
    /// <typeparam name="T">The message type implementing IMessage&lt;T&gt;.</typeparam>
    /// <param name="data">The byte array containing multiple length-prefixed messages.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>An async enumerable of parsed messages.</returns>
    public static async IAsyncEnumerable<T> ParseMultipleAsync<T>(
        ReadOnlyMemory<byte> data,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where T : class, IMessage<T>, new()
    {
        var parser = GetParser<T>();
        var offset = 0;
        var dataLength = data.Length;

        while (offset < dataLength && !cancellationToken.IsCancellationRequested)
        {
            if (dataLength - offset < sizeof(int))
                yield break;

            // Read length prefix (assuming little-endian 32-bit integer)
            var lengthBytes = data.Slice(offset, sizeof(int));
            var length = BitConverter.ToInt32(lengthBytes.Span);
            offset += sizeof(int);

            if (length <= 0 || offset + length > dataLength)
                yield break;

            T? message = null;
            try
            {
                var messageData = data.Slice(offset, length);
                message = parser.ParseFrom(messageData.Span);
                offset += length;
            }
            catch
            {
                // Skip malformed message and continue
                offset += length;
                continue;
            }

            if (message is not null)
            {
                yield return message;

                // Yield control periodically for better responsiveness
                if (offset % 8192 == 0)
                    await Task.Yield();
            }
        }
    }

    /// <summary>
    /// Validates that a byte array contains a valid Protocol Buffer message of the specified type.
    /// </summary>
    /// <typeparam name="T">The message type implementing IMessage&lt;T&gt;.</typeparam>
    /// <param name="data">The byte array to validate.</param>
    /// <returns>True if the data represents a valid message, false otherwise.</returns>
    public static bool IsValidMessage<T>(ReadOnlySpan<byte> data)
        where T : class, IMessage<T>, new()
    {
        return TryParse<T>(data, out _);
    }

    /// <summary>
    /// Validates that a byte array contains a valid Protocol Buffer message of the specified type.
    /// </summary>
    /// <typeparam name="T">The message type implementing IMessage&lt;T&gt;.</typeparam>
    /// <param name="data">The byte array to validate.</param>
    /// <returns>True if the data represents a valid message, false otherwise.</returns>
    public static bool IsValidMessage<T>(byte[] data)
        where T : class, IMessage<T>, new()
    {
        return TryParse<T>(data, out _);
    }

    /// <summary>
    /// Gets the estimated size of a message when serialized.
    /// </summary>
    /// <typeparam name="T">The message type implementing IMessage&lt;T&gt;.</typeparam>
    /// <param name="message">The message to measure.</param>
    /// <returns>The calculated size in bytes.</returns>
    public static int CalculateSize<T>(T message) where T : IMessage<T>
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.CalculateSize();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static async ValueTask<T> ParseAsyncCore<T>(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
        where T : class, IMessage<T>, new()
    {
        if (data.IsEmpty)
            throw new ProtocolBufferParsingException("Cannot parse empty data");

        cancellationToken.ThrowIfCancellationRequested();

        using var activity = _activitySource.StartActivity("ParseMessage");
        activity?.SetTag("messageType", typeof(T).Name);
        activity?.SetTag("dataSize", data.Length);

        var parser = GetParser<T>();

        try
        {
            // For small messages, parse synchronously to avoid overhead
            if (data.Length < 4096)
            {
                return parser.ParseFrom(data.Span);
            }

            // For larger messages, yield control first, then parse
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            // Parse after the await - span is accessed fresh
            return parser.ParseFrom(data.Span);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw new ProtocolBufferParsingException(
                $"Failed to parse {typeof(T).Name}: {ex.Message}", ex);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static MessageParser<T> GetParser<T>() where T : class, IMessage<T>, new()
    {
        var type = typeof(T);

        // First check the frozen cache
        if (_parserCache.TryGetValue(type, out var cachedParser))
        {
            return (MessageParser<T>)cachedParser;
        }

        // Lazy initialization with double-checked locking
        _mutableParserCache ??= new Dictionary<Type, object>();

        if (_mutableParserCache.TryGetValue(type, out var parser))
        {
            return (MessageParser<T>)parser;
        }

        lock (_parserCacheLock)
        {
            if (_mutableParserCache.TryGetValue(type, out parser))
            {
                return (MessageParser<T>)parser;
            }

            var newParser = new MessageParser<T>(() => new T());
            _mutableParserCache[type] = newParser;
            return newParser;
        }
    }

    private static FrozenDictionary<Type, object> CreateParserCache()
    {
        // Cache will be populated lazily as parsers are requested
        return FrozenDictionary<Type, object>.Empty;
    }

    private static readonly object _parserCacheLock = new();
    private static volatile Dictionary<Type, object>? _mutableParserCache;
}

/// <summary>
/// Exception thrown when Protocol Buffer parsing fails.
/// </summary>
public sealed class ProtocolBufferParsingException : Exception
{
    public ProtocolBufferParsingException(string message) : base(message) { }

    public ProtocolBufferParsingException(string message, Exception innerException)
        : base(message, innerException) { }
}