using System;

namespace BlurBuddy.Capture;

/// <summary>
/// Nameplates sort first (small per nameplate keys), 2D UI from the depth layer keys up
/// </summary>
public static class UiSplit
{
	public const uint TwoDimensionalKey = 0x80000000;

	public static int FindSplit(ReadOnlySpan<uint> keys, ReadOnlySpan<bool> isDraw)
	{
		for (int i = 0; i < keys.Length; i++)
		{
			if (isDraw[i] && keys[i] >= TwoDimensionalKey)
				return i;
		}

		return keys.Length;
	}

	/// <summary>
	/// Kernel commands run by sort key, equal keys in queue order; nameplate draws use a lower key than 2D UI
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
