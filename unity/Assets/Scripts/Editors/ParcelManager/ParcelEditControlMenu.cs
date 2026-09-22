using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 土地区画作成モード中の「作成モード切替」をUnity内で完結させるドライバ。
/// ParcelEditorManager と同じGameObjectに常駐し、作成モードがアクティブかをポーリングして自律動作する
/// (Ctrlラジアルメニュー・カーソルHUDの開閉は共通基底 <see cref="EditControlMenuBase"/> が担う)。
/// ONリング=現在の作成モード。
///
/// 作成モードの真実源は ParcelEditorManager.CurrentMode にあり、ここで切り替えると
/// `edit_parcel:set_mode` でUI(作成パネル)へ通知される(パネルのタイル選択も同じ状態を指す)。
/// 幅・奥行き・角度などの数値パラメータはパネル側が持つ。
/// </summary>
public class ParcelEditControlMenu : EditControlMenuBase
{
    private ParcelEditorManager manager;
    private List<RadialMenuItem> items;

    /// <summary>モードIDごとのアイコン(Resources/UIIcons、未配置ならnull=ラベル頭文字で代替表示)。</summary>
    private readonly Dictionary<string, Sprite> icons = new Dictionary<string, Sprite>();

    protected override bool IsSessionActive => manager != null && manager.IsActive;
    protected override List<RadialMenuItem> MenuItems => items;

    /// <summary>IpcStateCoordinator.ApplyStateEditParcel から呼ばれる初期化。</summary>
    public void Initialize(ParcelEditorManager editorManager)
    {
        manager = editorManager;

        items = new List<RadialMenuItem>();
        foreach (var info in ParcelEditorManager.Modes)
        {
            if (info.HiddenFromMenu) continue;

            icons[info.Id] = Utils.LoadUIIcon(info.IconName);

            // ループ変数をクロージャに閉じ込めるためローカルへ退避する
            ParcelEditorManager.ModeInfo mode = info;
            items.Add(new RadialMenuItem
            {
                Id = mode.Id,
                Label = mode.Label,
                GetIcon = () => icons.TryGetValue(mode.Id, out var sprite) ? sprite : null,
                GetState = () => manager != null && manager.CurrentMode == mode.Id,
                OnToggle = () => manager?.SetCreationMode(mode.Id),
            });
        }
    }

    /// <summary>
    /// カーソルHUDの表示内容。現在の作成モードのアイコンと、「モード名｜次の操作」のテキストを返す。
    ///
    /// 選択済みのときは「パネルで確定」を案内する(確定ボタンはTauri側にあり、
    /// 3Dビューだけを見ていると次に何をすればよいか分からないため)。
    /// </summary>
    protected override (List<CursorHudIcon> icons, string text) BuildHudContent()
    {
        hudIcons.Clear();
        if (manager == null) return (hudIcons, "");

        ParcelEditorManager.ModeInfo info = manager.CurrentModeInfo;
        hudIcons.Add(new CursorHudIcon(
            icons.TryGetValue(info.Id, out var sprite) ? sprite : null, true));

        string action = manager.HasSelection() ? "パネルで確定 (Escで選び直し)" : info.Hint;
        return (hudIcons, $"{info.Label}｜{action}");
    }
}
