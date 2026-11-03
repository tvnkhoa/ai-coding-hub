using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiCodingHub.Core.Options;
using Microsoft.Extensions.Logging;

namespace AiCodingHub.App.Settings;

/// <summary>Persists user overrides into <c>%APPDATA%\ClaudeMiniAlert\settings.json</c>.</summary>
public interface IUserSettingsWriter
{
    void SaveWindow(WindowOptions window);
}

/// <summary>
/// Merges the given section into the existing user settings file (preserving other sections)
/// and writes atomically. This is the writable layer <c>IOptionsMonitor</c> reloads from.
/// </summary>
public sealed class UserSettingsWriter : IUserSettingsWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<UserSettingsWriter> _log;
    private readonly object _gate = new();

    public UserSettingsWriter(string path, ILogger<UserSettingsWriter> log)
    {
        _path = path;
        _log = log;
    }

    public void SaveWindow(WindowOptions window)
    {
        lock (_gate)
        {
            try
            {
                var root = Load();
                root[WindowOptions.SectionName] = JsonSerializer.SerializeToNode(window);
                WriteAtomic(root);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to persist window settings");
            }
        }
    }

    private JsonObject Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var text = File.ReadAllText(_path);
                if (!string.IsNullOrWhiteSpace(text) &&
                    JsonNode.Parse(text) is JsonObject existing)
                {
                    return existing;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read existing settings; starting fresh");
        }

        return new JsonObject();
    }

    private void WriteAtomic(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(WriteOptions));
        File.Move(tmp, _path, overwrite: true);
    }
}
