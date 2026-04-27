using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GhostWin.Core.Models;

/// <summary>
/// 루트 DTO. session.json 의 최상위 구조.
/// SchemaVersion 은 향후 v2 (Phase 6-A) 과의 하위 호환 경로에서 사용.
/// </summary>
public sealed record SessionSnapshot(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("saved_at")]       DateTimeOffset SavedAt,
    [property: JsonPropertyName("workspaces")]     IReadOnlyList<WorkspaceSnapshot> Workspaces,
    [property: JsonPropertyName("active_workspace_index")] int ActiveWorkspaceIndex,
    [property: JsonPropertyName("reserved")]       JsonObject? Reserved
);

/// <summary>
/// 워크스페이스 하나의 상태 스냅샷. 이름과 Pane 트리를 보관.
/// </summary>
public sealed record WorkspaceSnapshot(
    [property: JsonPropertyName("name")]     string Name,
    [property: JsonPropertyName("root")]     PaneSnapshot Root,
    [property: JsonPropertyName("reserved")] JsonObject? Reserved
);

/// <summary>
/// Discriminated union 기저 타입. JSON 상에서 "type": "leaf" | "split" 태그 사용.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PaneLeafSnapshot),  "leaf")]
[JsonDerivedType(typeof(PaneSplitSnapshot), "split")]
public abstract record PaneSnapshot;

/// <summary>
/// 단말(터미널) Pane 스냅샷. CWD, 타이틀, Phase 6-A 예약 필드 보관.
/// </summary>
public sealed record PaneLeafSnapshot(
    [property: JsonPropertyName("cwd")]      string? Cwd,
    [property: JsonPropertyName("title")]    string? Title,
    [property: JsonPropertyName("reserved")] JsonObject? Reserved
) : PaneSnapshot;

/// <summary>
/// 분할(Split) 노드 스냅샷. 방향, 비율, 좌/우 자식 Pane 트리를 보관.
/// </summary>
public sealed record PaneSplitSnapshot(
    [property: JsonPropertyName("orientation")] SplitOrientation Orientation,
    [property: JsonPropertyName("ratio")]       double Ratio,
    [property: JsonPropertyName("left")]        PaneSnapshot Left,
    [property: JsonPropertyName("right")]       PaneSnapshot Right
) : PaneSnapshot;
