use std::env;
use std::fs;
use std::path::PathBuf;

fn main() {
    let source = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap()).join("../../version.json");
    println!("cargo:rerun-if-changed={}", source.display());
    let version: serde_json::Value = serde_json::from_str(&fs::read_to_string(source).unwrap()).unwrap();
    assert_eq!(version["frontend_version"].as_str().unwrap(), env::var("CARGO_PKG_VERSION").unwrap(), "Run npm run sync:version");
    tauri_build::build();

    // Steam 版は、チケットの identity と認証済みの状態値をコアと揃える。正はコアの consts.py で、
    // 同期済みの src/constants.ts から読んで埋め込む(Rust 向けの同期先は持たない)。
    if env::var_os("CARGO_FEATURE_STEAM").is_some() {
        let source = PathBuf::from(env::var("CARGO_MANIFEST_DIR").unwrap()).join("../src/constants.ts");
        println!("cargo:rerun-if-changed={}", source.display());
        let constants = fs::read_to_string(&source).unwrap();
        for name in ["STEAM_TICKET_IDENTITY", "GAME_VERIFIED"] {
            println!("cargo:rustc-env=CG_{name}={}", shared_constant(&constants, name));
        }
    }
}

/// constants.ts の `export const NAME: type = value;` から値を取り出す(文字列は引用符を外す)。
fn shared_constant(constants: &str, name: &str) -> String {
    let prefix = format!("export const {name}:");
    let line = constants
        .lines()
        .find(|line| line.starts_with(&prefix))
        .unwrap_or_else(|| panic!("src/constants.ts に {name} がありません"));
    let (_, value) = line.split_once(" = ").expect("constants.ts の定数の形式が想定と違います");
    value.trim().trim_end_matches(';').trim_matches('"').to_string()
}
