namespace LocalWebcam.Mobile.Services;

/// <summary>
/// Raised when an unknown computer wants to pair (spec section 7). The UI
/// shows <see cref="ComputerName"/>/<see cref="Code"/> with Allow/Reject
/// buttons, and completes <see cref="Decision"/> with the user's choice.
/// </summary>
public sealed class PairingApprovalRequest(string computerName, string code)
{
    public string ComputerName { get; } = computerName;

    public string Code { get; } = code;

    public TaskCompletionSource<bool> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
