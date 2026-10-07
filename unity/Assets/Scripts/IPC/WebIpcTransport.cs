#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOT;

/// <summary>
/// Web ビルドの IPC の送受信路。UI は同じページで動くので、MMF の代わりに
/// jslib(Plugins/WebGL/GenesisWebIpc.jslib)でページの JS と Envelope の JSON を受け渡す。
/// 相手はページの Web シェル(tauri/web/shell/ipc.ts)で、受信を始める前に送られた分は向こうで溜めてある。
/// </summary>
internal sealed class WebIpcTransport : IIpcTransport
{
    private delegate void MessageCallback(IntPtr utf8Json);

    [DllImport("__Internal")] private static extern void GenesisIpc_Open(MessageCallback callback);
    [DllImport("__Internal")] private static extern void GenesisIpc_Send(string json);
    [DllImport("__Internal")] private static extern void GenesisIpc_Close();

    // JS からは静的な関数しか呼べないので、受信の待ち行列も静的に持つ(送受信路は1つだけ開く)。
    private static readonly Queue<string> Received = new Queue<string>();

    public event Action<string> OnMessageReceived;

    public WebIpcTransport()
    {
        GenesisIpc_Open(OnMessage);
    }

    [MonoPInvokeCallback(typeof(MessageCallback))]
    private static void OnMessage(IntPtr utf8Json)
    {
        Received.Enqueue(Marshal.PtrToStringUTF8(utf8Json));
    }

    public void Send(string json) => GenesisIpc_Send(json);

    public void ProcessReceivedMessages()
    {
        while (Received.Count > 0)
        {
            string message = Received.Dequeue();
            try
            {
                OnMessageReceived?.Invoke(message);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"WebIpcTransport: メッセージ処理エラー: {e.Message}");
            }
        }
    }

    public void Dispose()
    {
        GenesisIpc_Close();
        Received.Clear();
    }
}
#endif
