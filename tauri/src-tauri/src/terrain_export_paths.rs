use std::path::{Path, PathBuf};

fn is_original(path: &Path) -> bool {
    let parts: Vec<_> = path
        .components()
        .map(|p| p.as_os_str().to_string_lossy().to_lowercase())
        .collect();
    parts
        .windows(2)
        .any(|pair| pair[0] == "terrains" && pair[1] == "original")
}

/// 原本は複製専用。親ディレクトリや既存ファイルのリンク先も確認してから書き込む。
pub fn checked_export_path(path: &Path) -> Result<PathBuf, String> {
    if is_original(path) {
        return Err(
            "オリジナル地形は編集禁止です。terrains フォルダへコピーとして保存してください。"
                .into(),
        );
    }
    let parent = path
        .parent()
        .filter(|p| !p.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    let parent = parent
        .canonicalize()
        .map_err(|e| format!("保存先を確認できません: {e}"))?;
    let name = path.file_name().ok_or("保存先のファイル名がありません")?;
    let resolved = parent.join(name);
    if is_original(&resolved) {
        return Err(
            "オリジナル地形は編集禁止です。terrains フォルダへコピーとして保存してください。"
                .into(),
        );
    }
    for extension in [
        "npy",
        "png",
        "water.json",
        "rivers.json",
        "resources.json",
        "props.json",
    ] {
        let target = resolved.with_extension(extension);
        if target.exists() {
            let actual = target
                .canonicalize()
                .map_err(|e| format!("保存先を確認できません: {e}"))?;
            if is_original(&actual) {
                return Err("オリジナル地形への書き込みはできません。".into());
            }
        }
    }
    if resolved.extension().and_then(|e| e.to_str()) != Some("npy") {
        return Err("地形の保存先は .npy ファイルを指定してください。".into());
    }
    Ok(resolved)
}

/// file_name はバックエンドの assets 基準。標高と付随データは同じ場所に置く。
pub fn registry_file_name(path: &Path) -> String {
    let name = path.file_name().unwrap_or_default().to_string_lossy();
    if path
        .parent()
        .and_then(Path::file_name)
        .is_some_and(|p| p.eq_ignore_ascii_case("terrains"))
    {
        format!("terrains/{name}")
    } else {
        name.into_owned()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn original_directory_is_rejected_before_writing() {
        assert!(checked_export_path(Path::new("assets/terrains/original/map.npy")).is_err());
        assert!(checked_export_path(Path::new("assets/TERRAINS/ORIGINAL/map.npy")).is_err());
        assert!(checked_export_path(Path::new("assets/terrains/original/../map.npy")).is_err());
    }

    #[test]
    fn game_copy_uses_assets_relative_registry_path() {
        assert_eq!(
            registry_file_name(Path::new("assets/terrains/map_yoron.npy")),
            "terrains/map_yoron.npy"
        );
        assert_eq!(
            registry_file_name(Path::new("export/map_yoron.npy")),
            "map_yoron.npy"
        );
    }

    #[test]
    fn parent_is_resolved_before_original_check() {
        let root =
            std::env::temp_dir().join(format!("terrain-export-paths-{}", std::process::id()));
        let original = root.join("terrains/original");
        std::fs::create_dir_all(&original).unwrap();
        let copy = checked_export_path(&root.join("terrains/map.npy")).unwrap();
        assert!(copy.is_absolute());
        assert!(!copy.exists());
        assert!(checked_export_path(&original.join("map.npy")).is_err());
        assert!(!original.join("map.npy").exists());
        std::fs::remove_dir(&original).unwrap();
        std::fs::remove_dir(root.join("terrains")).unwrap();
        std::fs::remove_dir(&root).unwrap();
    }
}
