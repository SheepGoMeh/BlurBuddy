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
	private bool holding; // a fade addon is up without a fade running, or a login is pending: the screen is held black
	private bool picked; // a character was picked at character select
	private bool login; // and the lobby is gone: the login has begun
	private bool covered; // the game's own black holds the screen with its loading screen up

	/// <summary>
	/// The frame is the game's loading screen: its artwork and moogle are textures it draws itself, no node of NowLoading has
	/// them. A fade addon alone isn't enough, outside a loading screen the frame has the UI in it
	/// </summary>
	public bool Covered => this.covered;

	/// <summary>0 clear, 1 black</summary>
	public float Current()
	{
		AtkUnitBase* running = null;
		bool up = false;
		foreach (string name in FadeAddons)
		{
			AtkUnitBase* addon = Addon(name);
			if (!IsShown(addon))
				continue;

			up = true;
			if (*((byte*)addon + ActiveOffset) != 0)
				running = addon;
		}

		// A fade addon up without a fade running holds the screen black (a login's loading screen has no fade in front of
		// it), the fade out of it follows; the addons hide the frame their fade ends
		bool wasHolding = this.holding;
		this.holding = (up && running == null) || this.LoginPending(up);

		// Black stays while the loading screen is up, whatever fades run meanwhile
		bool loading = IsShown(Addon("NowLoading"));
		float fade = this.black ? 1 : 0;
		if (running != null)
		{
			// Only a fade after the loading screen comes out of black
			if (!this.fading)
				this.toBlack = !(this.black || wasHolding) || loading;
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
			fade = this.black || this.holding ? 1 : 0;
		}

		this.covered = up && running == null && loading;
		return this.black && loading ? 1 : fade;
	}

	/// <summary>
	/// Logging in from character select, black until a fade addon takes over. Picking a character hides the list, and a queue
	/// dialog (SelectOk) may or may not follow, so the login begins when the lobby is torn down and the world's HUD, which the
	/// lobby never has, loads
	/// </summary>
	private bool LoginPending(bool fadeUp)
	{
		AtkUnitBase* select = Addon("CharaSelect");
		if (select != null)
		{
			if (!select->IsVisible)
				this.picked = true;
			else if (!IsShown(Addon("SelectOk")) && !IsShown(Addon("SelectYesno")))
				this.picked = false; // back at the list, the login didn't go on

			this.login = false;
		}
		else if (this.picked && Addon("Hud") != null)
		{
			this.login = !fadeUp;
			if (fadeUp)
				this.picked = false;
		}
		else
		{
			this.login = false;
		}

		return this.login;
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
