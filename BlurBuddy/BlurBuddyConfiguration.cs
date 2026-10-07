using System.Collections.Generic;

using Dalamud.Configuration;

using BlurBuddy.Tracking;

namespace BlurBuddy;

public enum CaptureSet
{
	None,
	Nameplates,
	Ui,
}

public enum BlurStyle
{
	Gaussian,
	Pixelate,
	Crystallize,
	Diamond,
}

public class BlurBuddyConfiguration: IPluginConfiguration
{
	public int Version { get; set; }

	public CaptureSet Set = CaptureSet.Ui;
	public bool BlurNameplates = true;
	public bool OnlyPlayerNames;
	public bool BlurUnlisted = true; // true: rules list what to show (whitelist), false: what to blur (blacklist)
	public List<UiRule> Rules = [];
	public BlurStyle Style = BlurStyle.Gaussian;
	public float BlurStrength = 8.0f; // Gaussian sigma (half resolution px), pixelate / crystallize cell size / 4
	public int NameplatePadding = 4;
	public bool ShowDebug;

	public void Save() => Service.PluginInterface.SavePluginConfig(this);
}
