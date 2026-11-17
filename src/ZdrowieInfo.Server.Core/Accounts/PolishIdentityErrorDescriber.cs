using Microsoft.AspNetCore.Identity;

namespace ZdrowieInfo.Server.Accounts;

/// <summary>Komunikaty Identity po polsku (interfejs jest tylko po polsku).</summary>
public sealed class PolishIdentityErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError DefaultError() => E(nameof(DefaultError), "Wystąpił nieznany błąd.");
    public override IdentityError ConcurrencyFailure() => E(nameof(ConcurrencyFailure), "Dane zostały zmienione w międzyczasie. Spróbuj ponownie.");
    public override IdentityError PasswordMismatch() => E(nameof(PasswordMismatch), "Nieprawidłowe hasło.");
    public override IdentityError InvalidToken() => E(nameof(InvalidToken), "Nieprawidłowy token.");
    public override IdentityError InvalidUserName(string? userName) => E(nameof(InvalidUserName), $"Login „{userName}” zawiera niedozwolone znaki.");
    public override IdentityError DuplicateUserName(string userName) => E(nameof(DuplicateUserName), $"Login „{userName}” jest już zajęty.");
    public override IdentityError PasswordTooShort(int length) => E(nameof(PasswordTooShort), $"Hasło musi mieć co najmniej {length} znaków.");
    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => E(nameof(PasswordRequiresUniqueChars), $"Hasło musi zawierać co najmniej {uniqueChars} różnych znaków.");
    public override IdentityError PasswordRequiresNonAlphanumeric() => E(nameof(PasswordRequiresNonAlphanumeric), "Hasło musi zawierać znak specjalny.");
    public override IdentityError PasswordRequiresDigit() => E(nameof(PasswordRequiresDigit), "Hasło musi zawierać cyfrę.");
    public override IdentityError PasswordRequiresLower() => E(nameof(PasswordRequiresLower), "Hasło musi zawierać małą literę.");
    public override IdentityError PasswordRequiresUpper() => E(nameof(PasswordRequiresUpper), "Hasło musi zawierać wielką literę.");
    public override IdentityError UserLockoutNotEnabled() => E(nameof(UserLockoutNotEnabled), "Blokada konta nie jest włączona.");
    public override IdentityError RecoveryCodeRedemptionFailed() => E(nameof(RecoveryCodeRedemptionFailed), "Nieprawidłowy kod odzyskiwania.");

    private static IdentityError E(string code, string description) => new() { Code = code, Description = description };
}
