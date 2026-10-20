using System;
using System.IO;
using UnityEngine;

/// <summary>
/// User directory (UrbanGenesis under the OS local-data directory), resolved with the same rule as core consts.py and Tauri version.rs.
/// Alongside saves, store settings retained across game updates, such as asset weights, and files exchanged with Tauri.
/// null in environments without a user directory, such as the browser build.
/// </summary>
public static class UserDataDir
{
    private const string FolderName = "UrbanGenesis";
    private static string root;
    private static bool resolved;

    public static string Root
    {
        get
        {
            if (!resolved)
            {
                root = Resolve();
                resolved = true;
            }
            return root;
        }
    }

    private static string Resolve()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string baseDir;
        switch (Application.platform)
        {
            case RuntimePlatform.WindowsPlayer:
            case RuntimePlatform.WindowsEditor:
                baseDir = Variable("LOCALAPPDATA") ?? Variable("APPDATA") ?? Path.Combine(home, "AppData", "Local");
                break;
            case RuntimePlatform.OSXPlayer:
            case RuntimePlatform.OSXEditor:
                baseDir = Path.Combine(home, "Library", "Application Support");
                break;
            case RuntimePlatform.LinuxPlayer:
            case RuntimePlatform.LinuxEditor:
                baseDir = Variable("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share");
                break;
            default:
                return null;
        }
        return Path.Combine(baseDir, FolderName);
    }

    private static string Variable(string name)
    {
        string value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
