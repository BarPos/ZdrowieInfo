namespace ZdrowieInfo.Server.ApiKeys;

/// <summary>Klucz API dla programów i agentów AI (tabela <c>api_keys</c>). W bazie tylko prefiks i hash sekretu.</summary>
public sealed class ApiKey
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public required string Prefix { get; set; }
    public required string SecretHash { get; set; }
    public List<string> Scopes { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public static class ApiScopes
{
    public const string ReportsWrite = "reports:write";
    public const string McpRead = "mcp:read";

    public static readonly IReadOnlyList<string> All = [ReportsWrite, McpRead];
}
