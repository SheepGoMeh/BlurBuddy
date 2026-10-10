using System;
using System.Runtime.InteropServices;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace BlurBuddy.Capture;

/// <summary>
/// Read only: where the frame is taken before and after the nameplates, nothing of the game's is written.
/// Nameplates are one bind of the final target with the display depth buffer, after the post effects and before the 2D UI
/// (view 30, keys 0xCE/0xCF..., ffxiv_rev nameplate-layer-handoff). At that bind the final target holds the scene; at the
/// 2D UI's bind (a callback queued in front of it) it holds the scene and the plates. With UpscaleBuddy (frame generation)
/// the plates go into its UI layer instead, which holds only them at that point; the final target keeps the scene.
/// </summary>
public sealed unsafe class PlateCapture: IDisposable
{
	private delegate void SetTargetDelegate(ImmediateContext* context, RenderCommandSetTarget* command);

	private static PlateCapture? instance;

	private readonly Hook<SetTargetDelegate> setTargetHook;

	// Main thread at the UI pass: the final target as the gate picks it (UiCapture.FinalTarget)
	private volatile nint finalTarget;
	// Per frame final and UI targets for the callback, a ring like FrameSlots: the render thread runs frames behind
	private const int FlushSlots = 4;
	private const int FlushArguments = 2;
	private readonly nint* flushTargets = (nint*)NativeMemory.AllocZeroed(FlushSlots * FlushArguments, (nuint)sizeof(nint));
	private int nextFlush;
	private volatile bool stopped;

	// Render thread
	private bool sceneTaken; // the scene was taken at this frame's plate bind

	public PlateCapture()
	{
		instance = this;
		this.setTargetHook = Service.GameInteropProvider.HookFromAddress<SetTargetDelegate>(
			(nint)ImmediateContext.MemberFunctionPointers.DoSetTargetCommand, this.SetTargetDetour);
		this.setTargetHook.Enable();
	}

	/// <summary>Render thread, at the plate bind: the final target holds the scene</summary>
	public Action<nint>? BeforePlates { get; set; }

	/// <summary>
	/// Render thread, before the 2D UI: final target, UI target (the final target, or another plugin's UI layer with the
	/// plates), and whether the scene was already taken at the plate bind
	/// </summary>
	public Action<nint, nint, bool>? AtUi { get; set; }

	/// <summary>Render thread: counts UI passes, a capture with the same value is of the frame taken at it</summary>
	public uint Frame { get; private set; }

	/// <summary>Main thread, AtkServer.Draw: this frame's final target, its depth bound bind is the plates'</summary>
	public void Prepare(KernelTexture* final) => this.finalTarget = this.stopped ? 0 : (nint)final;

	/// <summary>
	/// Main thread, at the 2D UI's bind (its target is RTM+0x570 during AtkServer.Draw): the argument of the
	/// <see cref="OnUiBegin"/> callback queued in front of it
	/// </summary>
	public nint FlushArgument(KernelTexture* final, KernelTexture* ui)
	{
		nint* targets = this.flushTargets + (this.nextFlush * FlushArguments);
		this.nextFlush = (this.nextFlush + 1) % FlushSlots;
		targets[0] = (nint)final;
		targets[1] = (nint)ui;
		return (nint)targets;
	}

	[UnmanagedCallersOnly]
	public static void OnUiBegin(nint targets)
	{
		try
		{
			instance?.Flush(((nint*)targets)[0], ((nint*)targets)[1]);
		}
		catch (Exception e)
		{
			Service.PluginLog.Error(e, "BlurBuddy plate capture failed");
		}
	}

	private void Flush(nint final, nint ui)
	{
		if (this.stopped || final == 0)
			return;

		this.Frame++;
		bool taken = this.sceneTaken;
		this.sceneTaken = false;
		this.AtUi?.Invoke(final, ui, taken);
	}

	/// <summary>Render thread: the first plate bind of the frame, before the game runs it</summary>
	private void SetTargetDetour(ImmediateContext* context, RenderCommandSetTarget* command)
	{
		nint final = this.finalTarget;
		if (!this.stopped && !this.sceneTaken && final != 0 && (nint)command->RenderTargets[0].Value == final && command->DepthBuffer != null)
		{
			try
			{
				this.BeforePlates?.Invoke(final);
				this.sceneTaken = true;
			}
			catch (Exception e)
			{
				Service.PluginLog.Error(e, "BlurBuddy scene capture failed");
			}
		}

		this.setTargetHook.Original(context, command);
	}

	/// <summary>Framework thread: nothing taken from here, queued callbacks do nothing; then wait for the render thread before Dispose</summary>
	public void Stop()
	{
		this.stopped = true;
		this.finalTarget = 0;
	}

	public void Dispose()
	{
		this.stopped = true;
		this.setTargetHook.Dispose();
		NativeMemory.Free(this.flushTargets);
		instance = null;
	}
}
