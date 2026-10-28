namespace GitSona.Core.Platform;

public enum ExpiryState { Ok, ExpiringSoon, Expired }

public sealed record ExpiryNotice(Account Account, ExpiryState State, int DaysLeft)
{
    public string Describe() => State switch
    {
        ExpiryState.Expired => $"{Account.Label}: token expired {-DaysLeft} day{(DaysLeft == -1 ? "" : "s")} ago",
        ExpiryState.ExpiringSoon when DaysLeft == 0 => $"{Account.Label}: token expires today",
        ExpiryState.ExpiringSoon => $"{Account.Label}: token expires in {DaysLeft} day{(DaysLeft == 1 ? "" : "s")}",
        _ => $"{Account.Label}: token valid for {DaysLeft} more days",
    };
}

public static class ExpiryChecker
{
    public const int DefaultWarnDays = 7;

    public static ExpiryNotice? Check(Account account, DateOnly today, int warnDays = DefaultWarnDays)
    {
        if (account.TokenExpiresOn is not { } expiry) return null;
        var left = expiry.DayNumber - today.DayNumber;
        var state = left < 0 ? ExpiryState.Expired : left <= warnDays ? ExpiryState.ExpiringSoon : ExpiryState.Ok;
        return new ExpiryNotice(account, state, left);
    }

    /// <summary>Accounts that are expired or expire within <paramref name="warnDays"/>, soonest first.</summary>
    public static IReadOnlyList<ExpiryNotice> Attention(IEnumerable<Account> accounts, DateOnly today, int warnDays = DefaultWarnDays) =>
        accounts.Select(a => Check(a, today, warnDays))
            .OfType<ExpiryNotice>()
            .Where(n => n.State != ExpiryState.Ok)
            .OrderBy(n => n.DaysLeft)
            .ToList();
}
