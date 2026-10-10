using System;
using System.Runtime.InteropServices;

using BlurBuddy.Tracking;

namespace BlurBuddy.Capture;

/// <summary>Per frame arguments of the render callback</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FrameSlot
{
	public int RectCount;
	public int WholeFrame;
	public int PlateCount; // the first rects are nameplates, blurred only where the nameplate layer has coverage
	public int AfterUiBind; // queued in a UI pass whose 2D UI bind got the layer flush, which saved the scene
	public CaptureSet Set; // what the frame was tracked for: a set change reaches the render thread frames later
	public float Fade; // black over the stream, 0 to 1: the loading screen the scene sets don't capture
	public ScreenRect Indicator; // the game's loading indicator, kept from the final target over the black
	public nint Target; // Kernel::Texture the UI pass renders into, read when the capture is queued
	public fixed float Rects[FrameSlots.MaxRects * 4]; // left, top, right, bottom in pixels
}

/// <summary>Native ring of frame slots, the render thread runs at most a few frames behind</summary>
public sealed unsafe class FrameSlots: IDisposable
{
	public const int SlotCount = 4;
	public const int MaxRects = 512;

	private readonly FrameSlot* slots = (FrameSlot*)NativeMemory.AllocZeroed((nuint)(SlotCount * sizeof(FrameSlot)));
	private int next;

	public FrameSlot* Next()
	{
		FrameSlot* slot = this.slots + this.next;
		this.next = (this.next + 1) % SlotCount;
		slot->RectCount = 0;
		slot->WholeFrame = 0;
		slot->PlateCount = 0;
		slot->AfterUiBind = 0;
		slot->Fade = 0;
		slot->Indicator = ScreenRect.Empty;
		return slot;
	}

	/// <summary>False and whole frame blur when full</summary>
	public static bool TryAdd(FrameSlot* slot, float left, float top, float right, float bottom)
	{
		if (slot->RectCount >= MaxRects)
		{
			slot->WholeFrame = 1;
			return false;
		}

		float* rect = slot->Rects + (slot->RectCount * 4);
		rect[0] = left;
		rect[1] = top;
		rect[2] = right;
		rect[3] = bottom;
		slot->RectCount++;
		return true;
	}

	public void Dispose() => NativeMemory.Free(this.slots);
}
