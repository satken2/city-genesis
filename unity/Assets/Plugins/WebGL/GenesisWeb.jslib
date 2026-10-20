// Web ビルドで、同じページの Web シェル(tauri/web/shell)と受け渡す処理。
// IPC は Web シェルの __genesisGraphicBridge(tauri/web/shell/ipc.ts)と、MMF と同じ Envelope の JSON 文字列を受け渡す。
mergeInto(LibraryManager.library, {
  GenesisIpc_Open: function (callback) {
    globalThis.__genesisGraphicBridge.attach(function (json) {
      var size = lengthBytesUTF8(json) + 1;
      var buffer = _malloc(size);
      stringToUTF8(json, buffer, size);
      {{{ makeDynCall('vi', 'callback') }}}(buffer);
      _free(buffer);
    });
  },

  GenesisIpc_Send: function (json) {
    globalThis.__genesisGraphicBridge.receive(UTF8ToString(json));
  },

  GenesisIpc_Close: function () {
    globalThis.__genesisGraphicBridge.detach();
  },

  GenesisWeb_FocusCanvas: function () {
    // exe でウィンドウを前面に出す操作(WindowUtil.BringToForeground)に当たる。キー入力を 3D 画面へ向ける。
    Module.canvas.focus();
  },
});
