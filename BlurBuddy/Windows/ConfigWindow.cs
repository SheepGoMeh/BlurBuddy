using System;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

using BlurBuddy.Capture;
using BlurBuddy.Tracking;

namespace BlurBuddy.Windows;

public class ConfigWindow(BlurBuddyConfiguration configuration, UiCapture uiCapture, FrameRenderer frameRenderer, PickerWindow picker)
	: Window("BlurBuddy", ImGuiWindowFlags.AlwaysAutoResize)
{
	private static readonly string[] SetNames = ["Scene only", "Scene and nameplates", "Scene and game UI"];
	private static readonly string[] ModeNames = ["Blurred (list shows)", "Shown (list blurs)"];
	private static readonly string[] StyleNames = ["Gaussian blur", "Pixelate", "Crystallize", "Diamond glass"];

	private string search = "";

	public override void Draw()
	{
		int set = (int)configuration.Set;
		if (ImGui.Combo("Stream shows", ref set, SetNames, SetNames.Length))
		{
			configuration.Set = (CaptureSet)set;
			configuration.Save();
		}

		if (configuration.Set != CaptureSet.None)
		{
			if (ImGui.Checkbox("Blur nameplates", ref configuration.BlurNameplates))
				configuration.Save();

			if (ImGui.Checkbox("Only the name line on nameplates", ref configuration.OnlyPlayerNames))
				configuration.Save();
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Blurs the line with the player name (and FC tag), keeps titles, icons, minion names and retainer labels");

			if (ImGui.SliderInt("Nameplate padding", ref configuration.NameplatePadding, 0, 16))
				configuration.Save();
		}

		if (configuration.Set == CaptureSet.Ui)
			this.DrawRules();

		if (configuration.Set != CaptureSet.None)
		{
			int style = (int)configuration.Style;
			if (ImGui.Combo("Style", ref style, StyleNames, StyleNames.Length))
			{
				configuration.Style = (BlurStyle)style;
				configuration.Save();
			}

			if (ImGui.SliderFloat("Strength", ref configuration.BlurStrength, 1.0f, 10.0f))
				configuration.Save();
		}

		ImGui.TextDisabled(uiCapture.Status);
		ImGui.TextDisabled(frameRenderer.ObsStatus);
		if (VkCapture.IsWine)
		{
			if (ImGui.Checkbox("Send to obs-vkcapture", ref configuration.VulkanCapture))
				configuration.Save();
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip("Linux OBS with obs-vkcapture (OBS_VKCAPTURE=1): captures the blurred frame instead of the game");
			if (configuration.VulkanCapture)
				ImGui.TextDisabled(frameRenderer.VulkanStatus ?? "Vulkan capture unavailable (needs DXVK), see /xllog");
		}

		if (uiCapture.Tracker.Status.Length > 0)
			ImGui.TextColored(new Vector4(1, 0.6f, 0.2f, 1), uiCapture.Tracker.Status);

		if (ImGui.Checkbox("Show debug", ref configuration.ShowDebug))
			configuration.Save();
		if (!configuration.ShowDebug)
			return;

		if (frameRenderer.PreviewSrv != 0)
		{
			float width = 480.0f;
			float height = width * frameRenderer.OutputHeight / frameRenderer.OutputWidth;
			ImGui.Image(new ImTextureID(frameRenderer.PreviewSrv), new Vector2(width, height));
		}

		ImDrawListPtr drawList = ImGui.GetForegroundDrawList();
		foreach ((ScreenRect rect, string name) in uiCapture.Tracker.LastRects)
		{
			drawList.AddRect(new Vector2(rect.Left, rect.Top), new Vector2(rect.Right, rect.Bottom), 0xFF00FFFF);
			drawList.AddText(new Vector2(rect.Left + 2, rect.Top + 1), 0xFF00FFFF, name);
		}
	}

	private void DrawRules()
	{
		int mode = configuration.BlurUnlisted ? 0 : 1;
		if (ImGui.Combo("Unlisted UI", ref mode, ModeNames, ModeNames.Length))
		{
			configuration.BlurUnlisted = mode == 0;
			configuration.Save();
		}

		if (ImGui.IsItemHovered())
			ImGui.SetTooltip("Blurred: new or unknown windows stay hidden until you list them. Shown: they leak until you list them.");

		UiRule? removed = null;
		if (ImGui.BeginTable("rules", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit))
		{
			foreach (UiRule rule in configuration.Rules)
			{
				ImGui.PushID(rule.GetHashCode());
				ImGui.TableNextColumn();
				ImGui.TextUnformatted(RuleLabel(rule));
				ImGui.TableNextColumn();
				if (ImGui.Checkbox("Blur", ref rule.Blur))
					configuration.Save();
				ImGui.TableNextColumn();
				if (ImGui.SmallButton("Remove"))
					removed = rule;
				ImGui.PopID();
			}

			ImGui.EndTable();
		}

		if (removed != null)
		{
			configuration.Rules.Remove(removed);
			configuration.Save();
		}

		if (ImGui.Button("Pick a part on screen"))
			picker.IsOpen = true;

		ImGui.InputTextWithHint("##search", "Add a module or known part", ref this.search, 64);
		if (ImGui.BeginChild("modules", new Vector2(360, 160), true))
			this.DrawAddList();
		ImGui.EndChild();
	}

	private void DrawAddList()
	{
		foreach (UiParts.Part part in UiParts.All)
			this.AddEntry(part.Addon, part.Id, $"{part.Addon} > {part.Label}");
		foreach (string name in uiCapture.Tracker.LoadedModules.Distinct().Order())
			this.AddEntry(name, "", name);
	}

	private void AddEntry(string addon, string part, string label)
	{
		if (this.search.Length > 0 && !label.Contains(this.search, StringComparison.OrdinalIgnoreCase))
			return;
		if (configuration.Rules.Exists(rule => rule.Addon == addon && rule.Part == part))
			return;
		if (!ImGui.Selectable(label))
			return;

		// Opposite of the current state
		AddonRules current = new();
		current.Resolve(configuration.Rules, configuration.BlurUnlisted, addon);
		configuration.Rules.Add(new UiRule { Addon = addon, Part = part, Blur = !current.Blur });
		configuration.Save();
	}

	private static string RuleLabel(UiRule rule)
	{
		if (rule.IsModule)
			return rule.Addon;
		if (rule.IsKnownPart)
			return $"{rule.Addon} > {UiParts.Find(rule.Addon, rule.Part)?.Label ?? rule.Part}";
		return $"{rule.Addon} > node {rule.Part}";
	}
}
