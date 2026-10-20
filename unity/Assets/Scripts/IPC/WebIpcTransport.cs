#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOT;

/// <summary>
/// Web-build IPC transport. UI shares the page, so instead of MMF,
/// jslib (Plugins/WebGL/GenesisWebIpc.jslib) exchanges Envelope JSON with page JS.
/// The peer is the Web shell (tauri/web/shell/ipc.ts), which buffers messages sent before reception starts.
/// </summary>
internal sealed class WebIpcTransport : IIpcTransport
{
    private delegate void MessageCallback(IntPtr utf8Json);

    [DllImport("__Internal")] private static extern void GenesisIpc_Open(MessageCallback callback);
    [DllImport("__Internal")] private static extern void GenesisIpc_Send(string json);
    [DllImport("__Internal")] private static extern void GenesisIpc_Close();

    // JS can call only static functions, so keep the receive queue static as well (only one transport opens).
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
