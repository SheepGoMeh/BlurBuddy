using System;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

using BlurBuddy.Tracking;

namespace BlurBuddy.Capture;

/// <summary>
/// Main thread: how black the stream goes around a loading screen. The game's fades (FadeMiddle, FadeBack) and its loading
/// screen are UI, so the scene sets never see them; the stream follows the fade timers, black from halfway through the fade
/// in, held while the loading screen is up, and out again halfway through the fade back.
/// </summary>
public sealed unsafe class LoadingFade
{
	private static readonly string[] FadeAddons = ["FadeMiddle", "FadeBack"];

	// AddonFadeMiddleBack (PerfBuddy TransitionLog): fade running, elapsed ms (float), duration ms
	private const int ActiveOffset = 0x349;
	private const int ElapsedOffset = 0x334;
	private const int DurationOffset = 0x338;

	private bool fading; // a fade is running
	private bool toBlack; // its direction: out of black when it started black
	private bool black; // a fade in finished, held until the fade back

	/// <summary>0 clear, 1 black</summary>
	public float Current()
	{
		AtkUnitBase* running = null;
		foreach (string name in FadeAddons)
		{
			AtkUnitBase* addon = Addon(name);
			if (IsShown(addon) && *((byte*)addon + ActiveOffset) != 0)
				running = addon;
		}

		// Black stays while the loading screen is up, whatever fades run meanwhile
		bool loading = IsShown(Addon("NowLoading"));
		float fade = this.black ? 1 : 0;
		if (running != null)
		{
			// Only a fade after the loading screen comes out of black
			if (!this.fading)
				this.toBlack = !this.black || loading;
			this.fading = true;
			float progress = Math.Clamp(
				*(float*)((byte*)running + ElapsedOffset) / Math.Max(1u, *(uint*)((byte*)running + DurationOffset)), 0, 1);
			fade = this.toBlack
				? Math.Max(fade, Math.Min(1, progress * 2))
				: Math.Min(1, (1 - progress) * 2);
		}
		else
		{
			// A finished fade stays where it went, until the next one
			if (this.fading)
				this.black = this.toBlack;
			this.fading = false;
			fade = this.black ? 1 : 0;
		}

		return this.black && loading ? 1 : fade;
	}

	/// <summary>
	/// The game's loading indicator: NowLoading is only that, a spinner (ui/uld/Loading_hr1.tex) under a root node that fades
	/// in; the root's box holds the spinner as it moves. Empty when it isn't up
	/// </summary>
	public static ScreenRect Indicator(float width, float height)
	{
		AtkUnitBase* addon = Addon("NowLoading");
		return IsShown(addon) && addon->RootNode != null ? ElementTracker.Bounds(addon->RootNode).Clamp(width, height) : ScreenRect.Empty;
	}

	private static AtkUnitBase* Addon(string name) => RaptureAtkUnitManager.Instance()->GetAddonByName(name);

	private static bool IsShown(AtkUnitBase* addon) => addon != null && addon->IsVisible;
}
