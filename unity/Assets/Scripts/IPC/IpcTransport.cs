using System;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// UI との IPC の送受信路。Windows は MMF、Web ビルドは同じページで動く UI と jslib で受け渡す。
/// 運ぶメッセージ(Envelope の JSON)はどちらも同じで、IpcClient は実装を選ばずにこれを通す。
/// </summary>
public interface IIpcTransport : IDisposable
{
    event Action<string> OnMessageReceived;
    void Send(string json);
    /// <summary>受信済みのメッセージをメインスレッドで配る(IpcClient.Update から呼ぶ)。</summary>
    void ProcessReceivedMessages();
}

public static class IpcTransports
{
    /// <summary>
    /// このプラットフォームの送受信路を開く。IPC の無いプラットフォームでは null。
    /// 観察専用の起動(ViewerSession)は MMF を開かず、同じプロセスの相手役とつなぐ。
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
/// MMF の送受信路。受信は MmfReader、送信は MmfWriter が受け持つ。
/// 共有メモリと同期オブジェクトの名前は tauri 側 src-tauri/src/ipc.rs と一致させること。
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
/// 名前付きMutexの取得ヘルパー。
/// 前の所有プロセス(クラッシュしたUnity等)がMutexを解放せずに死んだ場合、
/// WaitOne は AbandonedMutexException を投げるが、その時点で所有権自体は
/// 取得できているため、警告を出して続行してよい(Tauri による
/// Unity 自動再起動後の再アタッチで発生しうる)。
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
/// MMFベースのIPCリーダー。
/// 他プロセスが書き込んだメッセージをリングバッファから読み出す。
/// CreateOrOpenで共有メモリを開くため、どちらが先に起動しても動作する。
/// </summary>
public class MmfReader : IDisposable
{
    // 共有メモリのサイズ。tauri側 src-tauri/src/ipc.rs の CAPACITY と必ず一致させること
    // (CreateOrOpenは先に起動した側のサイズが採用されるため、食い違うと片側だけ範囲外を読む)。
    // 土地区画の一括作成プレビューのようにポリゴンを数百件送るメッセージがあるため8KBでは足りない。
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
    /// リングバッファから1メッセージを読み出す。
    /// 呼び出し元がMutexを保持している前提。
    /// nullの場合は読み出せるメッセージがない。
    /// </summary>
    private string TryReadMessageLocked()
    {
        int writePos = _view.ReadInt32(0);
        int readPos = _view.ReadInt32(4);

        if (readPos == writePos)
            return null;

        // 末尾にlengthプレフィクスが収まらない、またはラップマーカー(length=0)なら先頭へ戻る。
        // 先頭に書かれたメッセージは同じ書き込み通知の中で続けて読む
        // (ここで打ち切ると、次の書き込み通知が来るまで取り残される)。
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
            // 長さが壊れている。例外で読み取りスレッドを止めず、未読分を捨てて以降の通信を生かす。
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
/// MMFベースのIPCライター。
/// リングバッファにメッセージを書き込み、読み取り側に通知する。
/// CreateOrOpenで共有メモリを開くため、どちらが先に起動しても動作する。
/// コンストラクタでバッファをリセットする。
/// </summary>
public class MmfWriter : IDisposable
{
    // MmfReader.CAPACITY と同値。tauri側 src-tauri/src/ipc.rs の CAPACITY とも一致させること。
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

        // バッファをリセット（前回セッションの残骸を消去）
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
                    // ラップ: 現在位置にマーカーを書き、先頭から書く。
                    // ReadPos ちょうどまで埋めると WritePos == ReadPos(=空)と区別できないため、等号は許さない。
                    if (writePos + 4 <= BUFFER_SIZE)
                        _view.Write(HEADER_SIZE + writePos, 0); // ラップマーカー
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
