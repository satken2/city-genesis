using System;
using UnityEngine;

/// <summary>
/// ラジアルメニューの1項目の定義。
/// 状態(ON/OFF・有効/無効・アイコン)は表示のたびに毎フレーム評価されるため、
/// 値ではなくFunc/Actionで渡す。特定の編集モードに依存しない純データであり、
/// 呼び出し側(道路編集など)が自分のマネージャの状態を閉じ込めて渡す。
/// </summary>
public class RadialMenuItem
{
    /// <summary>識別子(ログ・デバッグ用)</summary>
    public string Id;

    /// <summary>ホバー中にメニュー中央へ表示する短いラベル。ASCII推奨(日本語フォント未導入のため)</summary>
    public string Label;

    /// <summary>表示アイコン。直線/曲線トグルのように状態でアイコンが変わる項目に対応するためFuncで受ける</summary>
    public Func<Sprite> GetIcon;

    /// <summary>現在のON/OFF状態。ONの項目はアクセント色のリングで強調される</summary>
    public Func<bool> GetState;

    /// <summary>有効条件。null=常に有効。無効の項目はグレー表示になり、選択を確定しても何も起きない</summary>
    public Func<bool> IsEnabled;

    /// <summary>選択確定(トリガーキーを離した)時に実行されるトグル処理</summary>
    public Action OnToggle;

    /// <summary>有効判定(IsEnabled未指定なら常に有効)</summary>
    public bool Enabled => IsEnabled == null || IsEnabled();
}
