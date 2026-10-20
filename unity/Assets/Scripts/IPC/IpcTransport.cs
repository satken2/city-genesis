using System;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// UI IPC transport. Windows uses MMF; Web builds use jslib to communicate with UI in the same page.
/// Both carry identical Envelope JSON; IpcClient uses this without choosing an implementation.
/// </summary>
public interface IIpcTransport : IDisposable
{
    event Action<string> OnMessageReceived;
    void Send(string json);
    /// <summary>Dispatches received messages on the main thread (called by IpcClient.Update).</summary>
    void ProcessReceivedMessages();
}

public static class IpcTransports
{
    /// <summary>
    /// Opens this platform's transport; null on platforms without IPC.
    /// Viewer-only launches (ViewerSession) connect to an in-process peer without opening MMF.
    /// </summary>
    public static IIpcTransport Open()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        return new WebIpcTransport();
#elif UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        return ViewerSession.Enabled ? ViewerSession.OpenTransport() : new MmfIpcTransport();
#else
        return null;
#endif
    }
}

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
/// <summary>
/// MMF transport: MmfReader receives and MmfWriter sends.
/// Shared memory/synchronization names must match tauri src-tauri/src/ipc.rs.
/// </summary>
internal sealed class MmfIpcTransport : IIpcTransport
{
    private readonly MmfReader _reader;
    private readonly MmfWriter _writer;

    public event Action<string> OnMessageReceived;

    public MmfIpcTransport()
    {
        _reader = new MmfReader("genesis-ipc-ui-to-graphic", "genesis-ipc-mutex-utg", "genesis-ipc-event-utg");
        _reader.OnMessageReceived += message => OnMessageReceived?.Invoke(message);
        _reader.Start();
        _writer = new MmfWriter("genesis-ipc-graphic-to-ui", "genesis-ipc-mutex-gtu", "genesis-ipc-event-gtu");
    }

    public void Send(string json) => _writer.Send(json);

    public void ProcessReceivedMessages() => _reader.ProcessReceivedMessages();

    public void Dispose()
    {
        _reader.Dispose();
        _writer.Dispose();
    }
}

/// <summary>
/// Named mutex acquisition helper.
/// If the previous owner (e.g. crashed Unity) died without releasing the mutex,
/// WaitOne throws AbandonedMutexException, but ownership has already
/// been acquired, so warn and continue (can occur on reattachment after Tauri
/// automatically restarts Unity).
/// </summary>
internal static class IpcMutexUtil
{
    public static void WaitSafely(Mutex mutex)
    {
        try
        {
            mutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            Debug.LogWarning("IPC: 放棄されたMutexを引き継ぎました(前プロセスの異常終了)");
        }
    }
}

/// <summary>
/// MMF-based IPC reader.
/// Reads messages written by another process from a ring buffer.
/// CreateOrOpen allows either process to start first.
/// </summary>
public class MmfReader : IDisposable
{
    // Shared memory size. Must match CAPACITY in tauri src-tauri/src/ipc.rs
    // (CreateOrOpen uses the first process's size; mismatch causes one side to read out of bounds).
    // 8KB is insufficient for messages containing hundreds of polygons, such as bulk parcel previews.
    private const int CAPACITY = 1024 * 1024;
    private const int HEADER_SIZE = 8;
    private const int BUFFER_SIZE = CAPACITY - HEADER_SIZE;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _event;
    private Thread _readThread;
    private volatile bool _isRunning;
    private readonly ConcurrentQueue<string> _receiveQueue = new ConcurrentQueue<string>();

    public event Action<string> OnMessageReceived;

    public MmfReader(string mmfName, string mutexName, string eventName)
    {
        _mmf = MemoryMappedFile.CreateOrOpen(mmfName, CAPACITY, MemoryMappedFileAccess.ReadWrite);
        _view = _mmf.CreateViewAccessor();
        _mutex = new Mutex(false, mutexName);
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
    }

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;

        _readThread = new Thread(ReadLoop)
        {
            Name = "IPC-MmfReadThread",
            IsBackground = true
        };
        _readThread.Start();
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;
        try { _event.Set(); } catch { }

        if (_readThread != null && _readThread.IsAlive)
            _readThread.Join(2000);
    }

    public void ProcessReceivedMessages()
    {
        while (_receiveQueue.TryDequeue(out string message))
        {
            try
            {
                OnMessageReceived?.Invoke(message);
            }
            catch (Exception e)
            {
                Debug.LogError($"MmfReader: メッセージ処理エラー: {e.Message}");
            }
        }
    }

    private void ReadLoop()
    {
        while (_isRunning)
        {
            _event.WaitOne();
            while (_isRunning)
            {
                IpcMutexUtil.WaitSafely(_mutex);
                string message;
                try
                {
                    message = TryReadMessageLocked();
                }
                finally
                {
                    _mutex.ReleaseMutex();
                }

                if (message == null)
                    break;

                _receiveQueue.Enqueue(message);
            }
        }
    }

    /// <summary>
    /// Reads one message from the ring buffer.
    /// Caller must hold the mutex.
    /// null means no readable message.
    /// </summary>
    private string TryReadMessageLocked()
    {
        int writePos = _view.ReadInt32(0);
        int readPos = _view.ReadInt32(4);

        if (readPos == writePos)
            return null;

        // Wrap to the start if the length prefix does not fit at the end, or on a wrap marker (length=0).
        // Continue reading messages at the start within the same write notification
        // (stopping here would leave them unread until the next notification).
        if (readPos + 4 > BUFFER_SIZE || _view.ReadInt32(HEADER_SIZE + readPos) == 0)
        {
            readPos = 0;
            _view.Write(4, 0);
            if (readPos == writePos)
                return null;
        }

        int length = _view.ReadInt32(HEADER_SIZE + readPos);
        if (length <= 0 || length > BUFFER_SIZE - readPos - 4)
        {
            // Invalid length. Discard unread data rather than stopping the reader thread with an exception, keeping future communication alive.
            _view.Write(4, writePos);
            return null;
        }

        byte[] data = new byte[length];
        _view.ReadArray(HEADER_SIZE + readPos + 4, data, 0, length);
        _view.Write(4, readPos + 4 + length);

        return Encoding.UTF8.GetString(data);
    }

    public void Dispose()
    {
        Stop();
        _view?.Dispose();
        _mmf?.Dispose();
        _mutex?.Dispose();
        _event?.Dispose();
    }
}

/// <summary>
/// MMF-based IPC writer.
/// Writes ring-buffer messages and notifies the reader.
/// CreateOrOpen allows either process to start first.
/// Resets the buffer in the constructor.
/// </summary>
public class MmfWriter : IDisposable
{
    // Same as MmfReader.CAPACITY; also must match tauri src-tauri/src/ipc.rs CAPACITY.
    private const int CAPACITY = 1024 * 1024;
    private const int HEADER_SIZE = 8;
    private const int BUFFER_SIZE = CAPACITY - HEADER_SIZE;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _event;

    public MmfWriter(string mmfName, string mutexName, string eventName)
    {
        _mmf = MemoryMappedFile.CreateOrOpen(mmfName, CAPACITY, MemoryMappedFileAccess.ReadWrite);
        _view = _mmf.CreateViewAccessor();
        _mutex = new Mutex(false, mutexName);
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);

        // Reset the buffer (clear previous session remnants).
        IpcMutexUtil.WaitSafely(_mutex);
        try
        {
            _view.Write(0, 0); // writePos
            _view.Write(4, 0); // readPos
        }
        finally
        {
            _mutex.ReleaseMutex();
        }
    }

    public void Send(string jsonMessage)
    {
        byte[] data = Encoding.UTF8.GetBytes(jsonMessage);
        int messageSize = 4 + data.Length;

        if (messageSize > BUFFER_SIZE - 4)
            throw new ArgumentException($"Message too large: {messageSize} bytes");

        IpcMutexUtil.WaitSafely(_mutex);
        try
        {
            int writePos = _view.ReadInt32(0);
            int readPos = _view.ReadInt32(4);

            if (writePos >= readPos)
            {
                int atEnd = BUFFER_SIZE - writePos;
                int atStart = readPos;

                if (messageSize <= atEnd)
                {
                    WriteAt(writePos, data);
                    _view.Write(0, writePos + messageSize);
                }
                else if (messageSize < atStart)
                {
                    // Wrap: write a marker at the current position, then write from the start.
                    // Do not allow equality: filling exactly to ReadPos makes WritePos == ReadPos indistinguishable from empty.
                    if (writePos + 4 <= BUFFER_SIZE)
                        _view.Write(HEADER_SIZE + writePos, 0); // Wrap marker
                    WriteAt(0, data);
                    _view.Write(0, messageSize);
                }
                else
                {
                    throw new InvalidOperationException("MMF buffer full");
                }
            }
            else
            {
                int available = readPos - writePos - 4;
                if (messageSize > available)
                    throw new InvalidOperationException("MMF buffer full");

                WriteAt(writePos, data);
                _view.Write(0, writePos + messageSize);
            }
        }
        finally
        {
            _mutex.ReleaseMutex();
        }

        _event.Set();
    }

    private void WriteAt(int pos, byte[] data)
    {
        _view.Write(HEADER_SIZE + pos, data.Length);
        _view.WriteArray(HEADER_SIZE + pos + 4, data, 0, data.Length);
    }

    public void Dispose()
    {
        _view?.Dispose();
        _mmf?.Dispose();
        _mutex?.Dispose();
        _event?.Dispose();
    }
}
#endif
