namespace WalletPK.Identity.Domain.Users;

public enum UserStatus
{
    /// <summary>Can log in and transact.</summary>
    Active = 0,

    /// <summary>Temporarily locked, for example after too many wrong MPIN attempts.</summary>
    Locked = 1,

    /// <summary>Frozen by compliance (fraud review). Cannot log in.</summary>
    Frozen = 2,
}
