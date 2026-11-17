namespace ZdrowieInfo.Server.Accounts;

public static class MfaClaims
{
    /// <summary>Claim w ciasteczku: "1", gdy użytkownik ma włączone TOTP. Bez niego aplikacja przekierowuje do konfiguracji MFA.</summary>
    public const string Enabled = "zi:mfa";
}
