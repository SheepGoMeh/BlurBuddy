using System;
using System.Threading;

using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using BlurBuddy.Capture;
using BlurBuddy.Rendering;
using BlurBuddy.Tracking;
using BlurBuddy.Windows;

namespace BlurBuddy;

public class BlurBuddyPlugin: IDalamudPlugin
{
	private const string CommandName = "/blurbuddy";

	private readonly BlurBuddyConfiguration configuration;
	private readonly NameplateLayer nameplateLayer;
	private readonly FrameRenderer frameRenderer;
	private readonly Blur blur;
	private readonly UiCapture uiCapture;
	private readonly WindowSystem windowSystem;
	private readonly ConfigWindow configWindow;
	private VkCapture? vkCapture;
	private bool vkTried;

	public BlurBuddyPlugin(IDalamudPluginInterface pluginInterface)
	{
		pluginInterface.Create<Service>();

		this.configuration = Service.PluginInterface.GetPluginConfig() as BlurBuddyConfiguration ?? new BlurBuddyConfiguration();
		if (!Enum.IsDefined(this.configuration.Style))
			this.configuration.Style = BlurStyle.Gaussian; // removed styles
		if (this.configuration.Version < 1)
		{
			// Target arrow covers what it points at
			this.configuration.Rules.Add(new UiRule { Addon = "_TargetCursor" });
			this.configuration.Version = 1;
		}
		this.nameplateLayer = new NameplateLayer();
		this.frameRenderer = new FrameRenderer(this.nameplateLayer);
		this.blur = new Blur(this.configuration);
		this.frameRenderer.Composite = this.blur.Run;
		this.uiCapture = new UiCapture(this.configuration, this.nameplateLayer);

		this.windowSystem = new WindowSystem("BlurBuddy");
		PickerWindow picker = new(this.configuration, this.uiCapture.Tracker);
		this.configWindow = new ConfigWindow(this.configuration, this.uiCapture, this.frameRenderer, picker);
		this.windowSystem.AddWindow(this.configWindow);
		this.windowSystem.AddWindow(picker);

		Service.PluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
		Service.PluginInterface.UiBuilder.OpenConfigUi += this.configWindow.Toggle;
		Service.CommandManager.AddHandler(
			CommandName,
			new CommandInfo((_, _) => this.configWindow.Toggle()) { HelpMessage = "Open the BlurBuddy settings." });
		Service.Framework.Update += this.OnFrameworkUpdate;
	}

	private void OnFrameworkUpdate(IFramework framework)
	{
		this.uiCapture.Update();
		if (this.configuration.VulkanCapture && !this.vkTried)
		{
			this.vkTried = true;
			this.vkCapture = VkCapture.TryCreate();
			this.frameRenderer.SetVulkan(this.vkCapture);
		}
		else if (!this.configuration.VulkanCapture && this.vkTried)
		{
			this.StopVulkan();
		}

		this.vkCapture?.Update(this.frameRenderer.OutputWidth, this.frameRenderer.OutputHeight);
	}

	private void StopVulkan()
	{
		this.frameRenderer.SetVulkan(null);
		this.vkCapture?.Dispose();
		this.vkCapture = null;
		this.vkTried = false;
	}

	protected virtual void Dispose(bool disposing)
	{
		if (!disposing)
		{
			return;
		}

		Service.Framework.Update -= this.OnFrameworkUpdate;
		Service.CommandManager.RemoveHandler(CommandName);
		Service.PluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
		Service.PluginInterface.UiBuilder.OpenConfigUi -= this.configWindow.Toggle;
		this.windowSystem.RemoveAllWindows();

		// Queued callbacks reference the renderer and the slots
		Service.Framework.RunOnFrameworkThread(() =>
		{
			this.uiCapture.Stop();
			this.nameplateLayer.Stop();
			this.StopVulkan(); // owns a window of this thread
		}).Wait();
		Thread.Sleep(200);
		this.frameRenderer.StopObs();
		this.uiCapture.Dispose();
		this.frameRenderer.Dispose();
		this.nameplateLayer.Dispose();
		this.blur.Dispose();
	}

	public void Dispose()
	{
		this.Dispose(true);
		GC.SuppressFinalize(this);
	}
}
