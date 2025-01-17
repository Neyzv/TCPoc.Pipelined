using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using TCPoc.Shared.Dispatcher;
using TCPoc.Shared.Framing;

namespace TCPoc.Shared.Transport;

public abstract class SocketListener<TMessage>
    : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly IMessageEncoder<TMessage> _messageEncoder;
    private readonly IMessageDecoder<TMessage> _messageDecoder;
    private readonly IMessageDispatcher<TMessage> _messageDispatcher;
    private readonly SemaphoreSlim _semaphore;

    private bool _disposed;

    protected readonly Socket _socket;

    /// <summary>
    /// Gets the endpoint of the connectection.
    /// </summary>
    private IPEndPoint EndPoint =>
        (IPEndPoint)_socket.RemoteEndPoint!;

    /// <summary>
    /// Gets the IP address of the connectection.
    /// </summary>
    public IPAddress Address =>
        EndPoint.Address;

    /// <summary>
    /// Gets the port of the connectection.
    /// </summary>
    public int Port =>
        EndPoint.Port;

    /// <summary>
    /// Gets the cancellation token associated with the connection.
    /// </summary>
    public CancellationToken CancellationToken =>
        _cts.Token;

    /// <summary>
    /// Gets a value indicating whether the connection is available.
    /// </summary>
    public bool IsConnected =>
        !_cts.IsCancellationRequested && _socket.Connected && !_disposed;

    public SocketListener(Socket socket,
        IMessageEncoder<TMessage> messageEncoder,
        IMessageDecoder<TMessage> messageDecoder,
        IMessageDispatcher<TMessage> messageDispatcher,
        CancellationTokenSource cts)
    {
        _socket = socket;
        _cts = cts;

        var stream = new NetworkStream(socket, true);
        _reader = PipeReader.Create(stream);
        _writer = PipeWriter.Create(stream);

        _messageEncoder = messageEncoder;
        _messageDecoder = messageDecoder;
        _messageDispatcher = messageDispatcher;

        _semaphore = new SemaphoreSlim(1);
    }

    /// <summary>
    /// Start listening for message from connection.
    /// </summary>
    protected async Task ListenAsync()
    {
        try
        {
            while (IsConnected)
            {
                var result = await _reader.ReadAsync(_cts.Token).ConfigureAwait(false);

                if (result.IsCanceled)
                    break;

                var buffer = result.Buffer;

                if (IsConnected && _messageDecoder.TryDecodeMessage(ref buffer, out var message))
                    await _messageDispatcher
                        .DispatchMessageAsync(this, message)
                        .ConfigureAwait(false);

                _reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        finally
        {
            await DisposeAsync()
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Send a <typeparamref name="TMessage"/> through the connection.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <exception cref="InvalidOperationException">Thrown if the connection have been broken or not initialized.</exception>
    public async ValueTask SendAsync(TMessage message)
    {
        await _semaphore.WaitAsync(_cts.Token).ConfigureAwait(false);

        try
        {
            if (!IsConnected)
                throw new InvalidOperationException("The session socket isn't connected.");

            await _writer
                .WriteAsync(_messageEncoder.EncodeMessage(message), _cts.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (!_cts.IsCancellationRequested)
            await _cts.CancelAsync().ConfigureAwait(false);

        await _reader.CompleteAsync().ConfigureAwait(false);
        await _writer.CompleteAsync().ConfigureAwait(false);

        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
            // ignored
        }
        finally
        {
            _socket.Close();

            _socket.Dispose();
            _cts.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}