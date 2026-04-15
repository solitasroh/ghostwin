using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Messaging;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.Services;

public sealed class SettingsService : ISettingsService, IDisposable
{
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private readonly object _lock = new();

    public AppSettings Current { get; private set; } = new();
    public string SettingsFilePath { get; }

    public SettingsService()
    {
        SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GhostWin", "ghostwin.json");
    }

    public void Load()
    {
        if (!File.Exists(SettingsFilePath))
        {
            Current = new AppSettings();
            return;
        }

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            var settings = new AppSettings();

            // "app" 섹션 파싱 (AppSettings — sidebar, titlebar, window, terminal.font 등).
            // Terminal.Font 는 M-12 설정 UI 바인딩 + 엔진 UpdateCellMetrics 의 입력 소스.
            if (doc.RootElement.TryGetProperty("app", out var appElem))
            {
                settings = JsonSerializer.Deserialize<AppSettings>(
                    appElem.GetRawText(), JsonOptions) ?? new();
            }

            // "keybindings" 섹션
            if (doc.RootElement.TryGetProperty("keybindings", out var kbElem))
            {
                settings.Keybindings = JsonSerializer
                    .Deserialize<Dictionary<string, string>>(
                        kbElem.GetRawText(), JsonOptions) ?? [];
            }

            lock (_lock) { Current = settings; }
        }
        catch (JsonException ex)
        {
            // 문법 오류 시 기존 설정 유지
            Debug.WriteLine($"[SettingsService] JSON parse error: {ex.Message}");
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"[SettingsService] File read error: {ex.Message}");
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // 기존 JSON을 읽어서 app/keybindings 섹션만 업데이트
            JsonNode? root = null;
            if (File.Exists(SettingsFilePath))
            {
                try
                {
                    var existing = File.ReadAllText(SettingsFilePath);
                    root = System.Text.Json.Nodes.JsonNode.Parse(existing, documentOptions: new()
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    });
                }
                catch { /* 파싱 실패 시 새로 생성 */ }
            }

            root ??= new System.Text.Json.Nodes.JsonObject();
            var obj = root.AsObject();

            AppSettings snapshot;
            lock (_lock) { snapshot = Current; }

            obj["app"] = System.Text.Json.Nodes.JsonNode.Parse(
                JsonSerializer.Serialize(snapshot, JsonOptions));
            obj["keybindings"] = System.Text.Json.Nodes.JsonNode.Parse(
                JsonSerializer.Serialize(snapshot.Keybindings, JsonOptions));

            // app 노드에서 keybindings 중복 제거
            if (obj["app"] is System.Text.Json.Nodes.JsonObject appObj)
                appObj.Remove("keybindings");

            var output = obj.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            });

            File.WriteAllText(SettingsFilePath, output);
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"[SettingsService] Save error: {ex.Message}");
        }
    }

    public void StartWatching()
    {
        var dir = Path.GetDirectoryName(SettingsFilePath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        _watcher = new FileSystemWatcher(dir, "ghostwin.json")
        {
            NotifyFilter = NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileChanged;
    }

    public void StopWatching()
    {
        if (_watcher != null)
        {
            _watcher.Changed -= OnFileChanged;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    // UI 스레드에서 메시지 전파하기 위한 콜백 (App.xaml.cs에서 설정)
    public Action<AppSettings>? OnSettingsReloaded { get; set; }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // debounce 50ms (NFR-03: 설정 리로드 < 100ms)
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(_ =>
        {
            Load();
            OnSettingsReloaded?.Invoke(Current);
        }, null, 50, Timeout.Infinite);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public void Dispose()
    {
        StopWatching();
        _debounceTimer?.Dispose();
    }
}
