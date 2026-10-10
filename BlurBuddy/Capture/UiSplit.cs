namespace BlurBuddy.Capture;

/// <summary>
/// Where the capture sorts among the UI replay's commands
/// </summary>
public static class UiSplit
{
	/// <summary>
	/// Kernel commands run by sort key, equal keys in queue order
	/// </summary>
	/// <param name="anyBefore">Commands were queued in this replay before the capture</param>
	/// <param name="maxBefore">Highest key among them</param>
	/// <param name="previousMin">Lowest key of the previous frame's replay, uint.MaxValue if none</param>
	/// <param name="current">Context key at the capture</param>
	public static uint CaptureKey(bool anyBefore, uint maxBefore, uint previousMin, uint current)
	{
		if (anyBefore)
			return maxBefore;

		return previousMin is 0 or uint.MaxValue ? current : previousMin - 1;
	}
}
