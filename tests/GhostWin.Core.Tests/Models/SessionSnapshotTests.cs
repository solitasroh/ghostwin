using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using GhostWin.Core.Models;
using Xunit;

namespace GhostWin.Core.Tests.Models;

/// <summary>
/// M-11 Session Restore — DTO 직렬화/역직렬화 unit tests.
/// Design §14.1 의 핵심 케이스 3 가지 (JSON round-trip, Reserved 보존, JsonPolymorphic 분기) + 보강 케이스.
/// </summary>
public class SessionSnapshotTests
{
    // ──────────────────────────────────────────────────────────
    // 공용 직렬화 옵션 — SessionSnapshotService 와 동일해야 한다 (Design §3.1).
    // 테스트는 Core 레이어이므로 Services 내부 static 멤버를 참조할 수 없음 → 복제 유지.
    // 옵션 drift 방지: SessionSnapshotService.JsonOpts 와 동등성 테스트는 Services.Tests 영역.
    // ──────────────────────────────────────────────────────────
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    // ── T-1: 단일 leaf round-trip ──────────────────────────────
    [Fact]
    [Trait("Category", "Unit")]
    public void Roundtrip_SingleLeaf_PreservesValues()
    {
        // Arrange
        var original = new SessionSnapshot(
            SchemaVersion: 1,
            SavedAt: new DateTimeOffset(2026, 4, 15, 10, 0, 0, TimeSpan.FromHours(9)),
            Workspaces:
            [
                new WorkspaceSnapshot(
                    Name: "ws-1",
                    Root: new PaneLeafSnapshot(
                        Cwd: @"C:\Users\test",
                        Title: "pwsh",
                        Reserved: null),
                    Reserved: null)
            ],
            ActiveWorkspaceIndex: 0,
            Reserved: null);

        // Act
        var json = JsonSerializer.Serialize(original, JsonOpts);
        var restored = JsonSerializer.Deserialize<SessionSnapshot>(json, JsonOpts);

        // Assert
        restored.Should().NotBeNull();
        restored!.SchemaVersion.Should().Be(1);
        restored.ActiveWorkspaceIndex.Should().Be(0);
        restored.Workspaces.Should().HaveCount(1);

        var ws = restored.Workspaces[0];
        ws.Name.Should().Be("ws-1");
        ws.Root.Should().BeOfType<PaneLeafSnapshot>();

        var leaf = (PaneLeafSnapshot)ws.Root;
        leaf.Cwd.Should().Be(@"C:\Users\test");
        leaf.Title.Should().Be("pwsh");
    }

    // ── T-2: 3 단계 중첩 split round-trip ──────────────────────
    [Fact]
    [Trait("Category", "Unit")]
    public void Roundtrip_NestedSplit_PreservesTreeStructure()
    {
        // Arrange: horizontal split → 좌측은 leaf, 우측은 vertical split → 좌 leaf + 우 leaf
        var original = new SessionSnapshot(
            SchemaVersion: 1,
            SavedAt: DateTimeOffset.Now,
            Workspaces:
            [
                new WorkspaceSnapshot(
                    Name: "nested",
                    Root: new PaneSplitSnapshot(
                        Orientation: SplitOrientation.Horizontal,
                        Ratio: 0.6,
                        Left: new PaneLeafSnapshot(Cwd: @"C:\a", Title: "a", Reserved: null),
                        Right: new PaneSplitSnapshot(
                            Orientation: SplitOrientation.Vertical,
                            Ratio: 0.3,
                            Left: new PaneLeafSnapshot(Cwd: @"C:\b", Title: "b", Reserved: null),
                            Right: new PaneLeafSnapshot(Cwd: @"C:\c", Title: "c", Reserved: null))),
                    Reserved: null)
            ],
            ActiveWorkspaceIndex: 0,
            Reserved: null);

        // Act
        var json = JsonSerializer.Serialize(original, JsonOpts);
        var restored = JsonSerializer.Deserialize<SessionSnapshot>(json, JsonOpts);

        // Assert
        restored.Should().NotBeNull();
        var root = restored!.Workspaces[0].Root;
        root.Should().BeOfType<PaneSplitSnapshot>();

        var topSplit = (PaneSplitSnapshot)root;
        topSplit.Orientation.Should().Be(SplitOrientation.Horizontal);
        topSplit.Ratio.Should().BeApproximately(0.6, 0.0001);

        topSplit.Left.Should().BeOfType<PaneLeafSnapshot>();
        ((PaneLeafSnapshot)topSplit.Left).Cwd.Should().Be(@"C:\a");

        topSplit.Right.Should().BeOfType<PaneSplitSnapshot>();
        var innerSplit = (PaneSplitSnapshot)topSplit.Right;
        innerSplit.Orientation.Should().Be(SplitOrientation.Vertical);
        innerSplit.Ratio.Should().BeApproximately(0.3, 0.0001);
        ((PaneLeafSnapshot)innerSplit.Left).Cwd.Should().Be(@"C:\b");
        ((PaneLeafSnapshot)innerSplit.Right).Cwd.Should().Be(@"C:\c");
    }

    // ── T-3: JsonPolymorphic 분기 — "type" 판별자 정확성 ───────
    [Fact]
    [Trait("Category", "Unit")]
    public void JsonPolymorphic_TypeDiscriminator_IsLeafOrSplit()
    {
        // Arrange
        var snap = new SessionSnapshot(
            SchemaVersion: 1,
            SavedAt: DateTimeOffset.Now,
            Workspaces:
            [
                new WorkspaceSnapshot(
                    Name: "poly",
                    Root: new PaneSplitSnapshot(
                        Orientation: SplitOrientation.Vertical,
                        Ratio: 0.5,
                        Left: new PaneLeafSnapshot(Cwd: null, Title: null, Reserved: null),
                        Right: new PaneLeafSnapshot(Cwd: null, Title: null, Reserved: null)),
                    Reserved: null)
            ],
            ActiveWorkspaceIndex: 0,
            Reserved: null);

        // Act
        var json = JsonSerializer.Serialize(snap, JsonOpts);
        using var doc = JsonDocument.Parse(json);

        // Assert — snake_case + type 판별자
        var root = doc.RootElement.GetProperty("workspaces")[0].GetProperty("root");
        root.GetProperty("type").GetString().Should().Be("split");
        root.GetProperty("left").GetProperty("type").GetString().Should().Be("leaf");
        root.GetProperty("right").GetProperty("type").GetString().Should().Be("leaf");
        root.GetProperty("orientation").GetString().Should().Be("Vertical");  // enum-as-string
    }

    // ── T-4: Reserved JsonObject round-trip (Phase 6-A 예약) ──
    [Fact]
    [Trait("Category", "Unit")]
    public void Reserved_JsonObject_RoundtripPreservesUnknownFields()
    {
        // Arrange — v2 가 기록했을 법한 agent 서브필드를 포함한 v1 파일
        var agentNode = new JsonObject
        {
            ["pending_notifications"] = new JsonArray(),
            ["unknown_v2_field"] = "preserve_me"
        };
        var original = new SessionSnapshot(
            SchemaVersion: 1,
            SavedAt: DateTimeOffset.Now,
            Workspaces: [],
            ActiveWorkspaceIndex: -1,
            Reserved: new JsonObject { ["agent"] = agentNode });

        // Act: save → load → save (round-trip 2 회)
        var firstJson = JsonSerializer.Serialize(original, JsonOpts);
        var reloaded = JsonSerializer.Deserialize<SessionSnapshot>(firstJson, JsonOpts);
        var secondJson = JsonSerializer.Serialize(reloaded, JsonOpts);

        // Assert — 최종 JSON 에서 unknown_v2_field 값 보존
        using var doc = JsonDocument.Parse(secondJson);
        var preserved = doc.RootElement
            .GetProperty("reserved")
            .GetProperty("agent")
            .GetProperty("unknown_v2_field")
            .GetString();
        preserved.Should().Be("preserve_me");
    }

    // ── T-5: snake_case 명명 검증 ──────────────────────────────
    [Fact]
    [Trait("Category", "Unit")]
    public void Serialization_UsesSnakeCaseProperties()
    {
        // Arrange
        var snap = new SessionSnapshot(
            SchemaVersion: 1,
            SavedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Workspaces: [],
            ActiveWorkspaceIndex: -1,
            Reserved: null);

        // Act
        var json = JsonSerializer.Serialize(snap, JsonOpts);

        // Assert — 모든 루트 프로퍼티는 snake_case
        json.Should().Contain("\"schema_version\":");
        json.Should().Contain("\"saved_at\":");
        json.Should().Contain("\"active_workspace_index\":");
        json.Should().Contain("\"workspaces\":");
        // PascalCase 노출 금지
        json.Should().NotContain("\"SchemaVersion\":");
        json.Should().NotContain("\"SavedAt\":");
    }

    // ── T-6: null Cwd/Title 은 JSON 에서 생략 (경량화) ──────────
    [Fact]
    [Trait("Category", "Unit")]
    public void Serialization_NullFields_AreOmitted()
    {
        // Arrange — 모든 선택 필드가 null
        var snap = new SessionSnapshot(
            SchemaVersion: 1,
            SavedAt: DateTimeOffset.Now,
            Workspaces:
            [
                new WorkspaceSnapshot(
                    Name: "lean",
                    Root: new PaneLeafSnapshot(Cwd: null, Title: null, Reserved: null),
                    Reserved: null)
            ],
            ActiveWorkspaceIndex: 0,
            Reserved: null);

        // Act
        var json = JsonSerializer.Serialize(snap, JsonOpts);

        // Assert — JsonIgnoreCondition.WhenWritingNull 동작
        json.Should().NotContain("\"cwd\":");
        json.Should().NotContain("\"title\":");
        json.Should().NotContain("\"reserved\":");
    }
}
