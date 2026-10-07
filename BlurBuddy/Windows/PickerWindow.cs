using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

using BlurBuddy.Tracking;

using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BlurBuddy.Windows;

/// <summary>Full screen and input eating: click a node to add a rule for it, wheel walks up its parents</summary>
public unsafe class PickerWindow(BlurBuddyConfiguration configuration, ElementTracker tracker)
	: Window("BlurBuddy picker", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings)
{
	private const uint Color = 0xFF00FFFF;

	private int level; // parents above the hovered node

	public override void OnOpen() => this.level = 0;

	public override void PreDraw()
	{
		ImGuiViewportPtr viewport = ImGui.GetMainViewport();
		this.Position = viewport.Pos;
		this.PositionCondition = ImGuiCond.Always;
		this.Size = viewport.Size;
		this.SizeCondition = ImGuiCond.Always;
	}

	public override void Draw()
	{
		if (ImGui.IsKeyPressed(ImGuiKey.Escape) || ImGui.IsMouseClicked(ImGuiMouseButton.Right))
		{
			this.IsOpen = false;
			return;
		}

		Vector2 mouse = ImGui.GetMousePos();
		ImDrawListPtr drawList = ImGui.GetForegroundDrawList();
		PickedNode? picked = tracker.Pick(mouse);
		if (picked == null)
		{
			drawList.AddText(mouse + new Vector2(16, 16), Color, "Nothing here (right click or Esc to cancel)");
			return;
		}

		float wheel = ImGui.GetIO().MouseWheel;
		this.level = Math.Clamp(this.level + (wheel > 0 ? 1 : wheel < 0 ? -1 : 0), 0, picked.Ids.Length - 1);
		int length = picked.Ids.Length - this.level;
		ScreenRect rect = ElementTracker.Bounds((AtkResNode*)picked.Nodes[length - 1]);
		string path = NodePath.Format(picked.Ids.AsSpan(0, length));

		drawList.AddRect(new Vector2(rect.Left, rect.Top), new Vector2(rect.Right, rect.Bottom), Color, 0, ImDrawFlags.None, 2);
		drawList.AddText(mouse + new Vector2(16, 16), Color, $"{picked.Addon} {path}\nWheel: parent / child, click: toggle, right click: cancel");

		if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left))
			return;

		// Opposite of what the addon does now
		AddonRules rules = new();
		rules.Resolve(configuration.Rules, configuration.BlurUnlisted, picked.Addon);
		configuration.Rules.RemoveAll(rule => rule.Addon == picked.Addon && rule.Part == path);
		configuration.Rules.Add(new UiRule { Addon = picked.Addon, Part = path, Blur = !rules.Blur });
		configuration.Save();
		this.IsOpen = false;
	}
}
