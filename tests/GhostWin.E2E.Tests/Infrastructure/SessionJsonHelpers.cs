using System.Text.Json.Nodes;

namespace GhostWin.E2E.Tests.Infrastructure;

/// <summary>
/// session.json 파일 읽기/쓰기 헬퍼.
/// PS1 의 Find-FirstLeaf / session.json 파싱 로직을 C# 로 이식.
///
/// session.json 위치: %APPDATA%\GhostWin\session.json
/// 구조:
///   {
///     "workspaces": [
///       { "root": { "type": "leaf", "cwd": "...", "title": "..." } }
///       // 또는
///       { "root": { "type": "split", "left": {...}, "right": {...} } }
///     ]
///   }
/// </summary>
public static class SessionJsonHelpers
{
    /// <summary>session.json 전체 경로</summary>
    public static string SessionJsonPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GhostWin",
            "session.json");

    /// <summary>
    /// session.json 파일을 삭제한다 (테스트 Arrange 단계).
    /// 파일이 없으면 무시.
    /// </summary>
    public static void DeleteSessionJson()
    {
        if (File.Exists(SessionJsonPath))
            File.Delete(SessionJsonPath);
    }

    /// <summary>
    /// session.json 을 읽어 JsonNode 로 반환한다.
    /// 파일이 없거나 파싱 실패 시 예외 발생.
    /// </summary>
    public static JsonNode ReadSessionJson()
    {
        if (!File.Exists(SessionJsonPath))
            throw new FileNotFoundException(
                $"session.json 이 존재하지 않습니다. 경로: {SessionJsonPath}");

        var text = File.ReadAllText(SessionJsonPath);
        return JsonNode.Parse(text)
               ?? throw new InvalidDataException("session.json 파싱 실패: null 반환");
    }

    /// <summary>
    /// workspaces[0].root 에서 첫 번째 leaf 노드를 찾아 반환한다.
    /// PS1 의 Find-FirstLeaf 재귀 함수 이식.
    ///
    /// 노드 타입:
    ///   "leaf"  → { "type": "leaf",  "cwd": "...", "title": "..." }
    ///   "split" → { "type": "split", "left": {...}, "right": {...} }
    ///
    /// 탐색 순서: split → left 먼저 (PS1 과 동일)
    /// </summary>
    public static JsonNode? FindFirstLeaf(JsonNode? json)
    {
        var root = json?["workspaces"]?[0]?["root"];
        return FindFirstLeafNode(root);
    }

    private static JsonNode? FindFirstLeafNode(JsonNode? node)
    {
        if (node is null) return null;

        var type = node["type"]?.GetValue<string>();

        if (type == "leaf")
            return node;

        if (type == "split")
        {
            var left = FindFirstLeafNode(node["left"]);
            if (left is not null) return left;
            return FindFirstLeafNode(node["right"]);
        }

        // 알 수 없는 타입 — 자식 탐색 불가
        return null;
    }
}
