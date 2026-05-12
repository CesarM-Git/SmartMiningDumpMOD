using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mafi;
using Mafi.Core;
using Mafi.Core.Entities;

namespace SmartMiningDumpMod;

/// <summary>
/// Manages per-MineTower "prefer dumping" toggle state.
/// State is persisted to a JSON file per save-game name so it survives game restarts.
/// </summary>
public sealed class DumpPreferenceManager
{
    public static DumpPreferenceManager Instance { get; set; }

    private readonly Dictionary<int, bool> m_toggles = new Dictionary<int, bool>();
    private readonly string m_savePath;

    public DumpPreferenceManager(string savePath)
    {
        m_savePath = savePath;
        Load();
    }

    public bool IsToggled(EntityId entityId)
    {
        return m_toggles.TryGetValue(entityId.Value, out bool val) && val;
    }

    public void SetToggle(EntityId entityId, bool value)
    {
        m_toggles[entityId.Value] = value;
        Save();
    }

    public bool Toggle(EntityId entityId)
    {
        bool current = IsToggled(entityId);
        bool next = !current;
        SetToggle(entityId, next);
        return next;
    }

    public int ToggledCount
    {
        get
        {
            int count = 0;
            foreach (var kv in m_toggles)
                if (kv.Value) count++;
            return count;
        }
    }

    /// <summary>
    /// Returns all entity IDs that have the toggle ON.
    /// Caller should not cache the result across ticks.
    /// </summary>
    public IEnumerable<int> AllToggledIds()
    {
        foreach (var kv in m_toggles)
            if (kv.Value)
                yield return kv.Key;
    }

    /// <summary>
    /// Remove toggle state for entities that no longer exist.
    /// Call periodically or on entity removal.
    /// </summary>
    public void CleanupRemovedEntity(EntityId entityId)
    {
        if (m_toggles.Remove(entityId.Value))
            Save();
    }

    // ── Persistence ─────────────────────────────────────────────────────

    private void Save()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            bool first = true;
            foreach (var kv in m_toggles)
            {
                if (!kv.Value) continue; // only persist ON entries
                if (!first) sb.AppendLine(",");
                sb.Append($"  \"{kv.Key}\": true");
                first = false;
            }
            sb.AppendLine();
            sb.AppendLine("}");
            File.WriteAllText(m_savePath, sb.ToString());
        }
        catch (Exception ex)
        {
            Log.Warning($"SmartMiningDumpMOD: Failed to save preferences: {ex.Message}");
        }
    }

    private void Load()
    {
        m_toggles.Clear();
        if (!File.Exists(m_savePath))
            return;

        try
        {
            string json = File.ReadAllText(m_savePath);
            // Minimal JSON parser: extract "key": true pairs
            // Avoids dependency on System.Text.Json (not available in net48 by default)
            int idx = 0;
            while (idx < json.Length)
            {
                int qStart = json.IndexOf('"', idx);
                if (qStart < 0) break;
                int qEnd = json.IndexOf('"', qStart + 1);
                if (qEnd < 0) break;

                string key = json.Substring(qStart + 1, qEnd - qStart - 1);

                int colonIdx = json.IndexOf(':', qEnd + 1);
                if (colonIdx < 0) break;

                // Find the value (true/false)
                int valStart = colonIdx + 1;
                while (valStart < json.Length && char.IsWhiteSpace(json[valStart])) valStart++;

                if (valStart < json.Length - 3 && json.Substring(valStart, 4) == "true")
                {
                    if (int.TryParse(key, out int entityIdValue))
                        m_toggles[entityIdValue] = true;
                    idx = valStart + 4;
                }
                else if (valStart < json.Length - 4 && json.Substring(valStart, 5) == "false")
                {
                    idx = valStart + 5;
                }
                else
                {
                    idx = valStart + 1;
                }
            }

            Log.Info($"SmartMiningDumpMOD: Loaded {m_toggles.Count} toggle(s) from {m_savePath}");
        }
        catch (Exception ex)
        {
            Log.Warning($"SmartMiningDumpMOD: Failed to load preferences: {ex.Message}");
        }
    }
}
