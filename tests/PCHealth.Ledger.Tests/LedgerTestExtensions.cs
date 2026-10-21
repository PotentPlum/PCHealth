namespace PCHealth.Ledger.Tests;

internal static class LedgerTestExtensions
{
    /// <summary>Saves a transaction in a final state by stepping through the states the ledger requires.</summary>
    public static void Record(this TransactionLedger ledger, LedgerTransaction transaction)
    {
        ledger.Save(transaction with { State = TransactionState.Pending });
        if (transaction.State == TransactionState.Verified)
        {
            ledger.Save(transaction with { State = TransactionState.Applied });
        }

        ledger.Save(transaction);
    }
}
