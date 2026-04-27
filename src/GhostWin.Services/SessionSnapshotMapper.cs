using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.Services;

/// <summary>
/// 런타임 서비스 상태 → DTO 변환기 (순수 정적, 상태 없음).
/// <para>
/// 변환 방향:
/// <see cref="IWorkspaceService.Workspaces"/> + <see cref="IPaneLayoutService.Root"/>
/// + <see cref="ISessionManager.Sessions"/> → <see cref="SessionSnapshot"/>.
/// </para>
/// <para>
/// 호출 스레드: <see cref="Collect"/> 는 반드시 UI 스레드에서 호출 (WorkspaceService /
/// PaneLayoutService 상태는 UI affinity). 직렬화/파일 I/O 는 호출자 책임.
/// </para>
/// </summary>
public static class SessionSnapshotMapper
{
    // ──────────────────────────────────────────────────────────
    // Snapshot 수집 (Design §12.1, Step 3)
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// 현재 앱 상태를 <see cref="SessionSnapshot"/> 으로 수집.
    /// <para>
    /// <see cref="IWorkspaceService.Workspaces"/> 순회 →
    /// 각 워크스페이스의 <see cref="IPaneLayoutService.Root"/> 를
    /// <see cref="ToPaneSnapshot"/> 으로 재귀 변환 →
    /// 활성 워크스페이스 인덱스 계산.
    /// </para>
    /// </summary>
    /// <param name="workspaces">워크스페이스 서비스 (UI 스레드에서만 안전).</param>
    /// <param name="sessions">세션 매니저 (leaf CWD/Title 조회).</param>
    /// <returns>완전한 v1 스냅샷 (활성 없을 시 <c>ActiveWorkspaceIndex = -1</c>).</returns>
    public static SessionSnapshot Collect(
        IWorkspaceService workspaces,
        ISessionManager sessions)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(sessions);

        var list = new List<WorkspaceSnapshot>(workspaces.Workspaces.Count);
        int activeIdx = -1;

        for (int i = 0; i < workspaces.Workspaces.Count; i++)
        {
            var ws = workspaces.Workspaces[i];
            var layout = workspaces.GetPaneLayout(ws.Id);
            if (layout?.Root == null) continue;   // 방어적 skip — 정상 경로 아님

            var rootSnap = ToPaneSnapshot(layout.Root, sessions);

            list.Add(new WorkspaceSnapshot(
                Name: ws.Name,
                Root: rootSnap,
                Reserved: null));

            if (ws.Id == workspaces.ActiveWorkspaceId)
                activeIdx = list.Count - 1;
        }

        return new SessionSnapshot(
            SchemaVersion: 1,
            SavedAt: DateTimeOffset.Now,
            Workspaces: list,
            ActiveWorkspaceIndex: activeIdx,
            Reserved: null);
    }

    /// <summary>
    /// PaneNode 트리 → PaneSnapshot 재귀 변환.
    /// Leaf: SessionInfo.Cwd/Title 을 조회해 PaneLeafSnapshot 생성 (Cwd 빈 문자열 → null 로 경량화).
    /// Split: orientation + ratio + 좌/우 자식 재귀.
    /// </summary>
    public static PaneSnapshot ToPaneSnapshot(
        IReadOnlyPaneNode node,
        ISessionManager sessions)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(sessions);

        if (node.IsLeaf)
        {
            string? cwd = null;
            string? title = null;
            if (node.SessionId is { } sid)
            {
                var info = sessions.Sessions.FirstOrDefault(s => s.Id == sid);
                cwd = string.IsNullOrEmpty(info?.Cwd) ? null : info.Cwd;
                title = string.IsNullOrEmpty(info?.Title) ? null : info.Title;
            }
            return new PaneLeafSnapshot(Cwd: cwd, Title: title, Reserved: null);
        }

        // Split/branch — Left/Right 는 null 불가 (PaneNode.Split 이후 항상 채워짐).
        if (node.Left == null || node.Right == null || node.SplitDirection == null)
            throw new InvalidOperationException(
                $"Corrupt branch node (Id={node.Id}): SplitDirection/Left/Right must be non-null");

        return new PaneSplitSnapshot(
            Orientation: node.SplitDirection.Value,
            Ratio: node.Ratio,
            Left: ToPaneSnapshot(node.Left, sessions),
            Right: ToPaneSnapshot(node.Right, sessions));
    }

    // ──────────────────────────────────────────────────────────
    // CWD 폴백 로직 (OQ-2 구현 — Design §8)
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// 저장된 CWD 가 현재 존재하는지 확인하고, 부재 시 상위 3 단계까지 탐색.
    /// 전부 실패하면 null (쉘 기본 CWD 사용).
    /// </summary>
    /// <param name="saved">저장 시점의 CWD. null 또는 빈 문자열이면 즉시 null 반환.</param>
    /// <returns>접근 가능한 디렉터리 경로 또는 null.</returns>
    public static string? ResolveCwd(string? saved)
    {
        if (string.IsNullOrEmpty(saved)) return null;

        var current = saved;
        for (int depth = 0; depth < 3; depth++)
        {
            try
            {
                if (Directory.Exists(current)) return current;
            }
            catch
            {
                // UnauthorizedAccessException 등 — 다음 상위로
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }

        return null;
    }
}
