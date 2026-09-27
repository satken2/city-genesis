use std::env;
use std::fs;
use std::path::PathBuf;

fn main() {
    let source = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap()).join("../../version.json");
    println!("cargo:rerun-if-changed={}", source.display());
    let version: serde_json::Value = serde_json::from_str(&fs::read_to_string(source).unwrap()).unwrap();
    assert_eq!(version["frontend_version"].as_str().unwrap(), env::var("CARGO_PKG_VERSION").unwrap(), "Run npm run sync:version");
    tauri_build::build();

    // 実行ファイルと同じディレクトリに配置する設定ファイルを、
    // 開発時・本番ビルド時の両方で target/{profile} へ自動コピーする。
    // これにより、current_exe() の親ディレクトリから読み込むロジックが
    // cargo run / tauri dev / cargo build --release のいずれでも正しく動作する。
    let manifest_dir = env::var("CARGO_MANIFEST_DIR")
        .map(PathBuf::from)
        .expect("CARGO_MANIFEST_DIR が設定されていません");
    let profile = env::var("PROFILE").expect("PROFILE が設定されていません");

    let target_dir = env::var("CARGO_TARGET_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(|_| manifest_dir.join("target"));
    let target_profile_dir = target_dir.join(&profile);

    let source = manifest_dir.parent().unwrap().join("graphic-launch.toml");
    let dest = target_profile_dir.join("graphic-launch.toml");

    if source.exists() {
        // コピー先ディレクトリがまだ存在しない場合（特に初回ビルド時）は作成する。
        if let Some(parent) = dest.parent() {
            fs::create_dir_all(parent).expect("コピー先ディレクトリの作成に失敗しました");
        }
        fs::copy(&source, &dest).expect("graphic-launch.toml のコピーに失敗しました");
    }
}
