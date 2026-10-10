using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using Vortice.Vulkan;

using static Vortice.Vulkan.Vulkan;

namespace BlurBuddy.Capture;

/// <summary>
/// Proton (DXVK): obs-vkcapture captures pSwapchains[0] of each present, so the output goes through a hidden
/// window's swapchain on DXVK's device and rides as the first swapchain of the game's own present
/// </summary>
public sealed unsafe partial class VkCapture: IDisposable
{
	private static readonly Guid InteropDeviceId = new("e2ef5fa5-dc21-4af7-90c4-f67ef6a09323");
	private static readonly Guid InteropSurfaceId = new("5546cf8c-77e7-4341-b05d-8d4d5000e77d");

	private const VkResult NotWritten = (VkResult)int.MaxValue;

	private delegate VkResult QueuePresentDelegate(nint queue, VkPresentInfoKHR* info);

	[InlineArray(2)]
	private struct Two<T> where T: unmanaged
	{
		private T element;
	}

	private readonly object sync = new();
	private readonly nint interop; // IDXGIVkInteropDevice
	private readonly VkInstanceApi instanceApi;
	private readonly VkDeviceApi api;
	private readonly VkPhysicalDevice physicalDevice;
	private readonly VkQueue queue;
	private readonly VkCommandPool pool;
	private readonly nint window;
	private readonly VkSurfaceKHR surface;
	private readonly Hook<QueuePresentDelegate> presentHook;

	private VkSwapchainKHR swapchain;
	private VkPresentModeKHR presentMode;
	private VkExtent2D swapExtent;
	private VkImage[] images = [];
	private VkCommandBuffer[] commands = [];
	private VkFence[] fences = [];
	private VkSemaphore[] acquired = [];
	private VkSemaphore[] copied = [];
	private VkSemaphore spare;
	private volatile bool outOfDate;
	private uint windowWidth, windowHeight;

	// Output texture, set by the render thread
	private VkImage source;
	private VkImageLayout sourceLayout;
	private VkExtent3D sourceExtent;

	private bool logged;

	public static bool IsWine { get; } = NativeLibrary.TryLoad("ntdll.dll", out nint ntdll) && NativeLibrary.TryGetExport(ntdll, "wine_get_version", out _);

	public string Status { get; private set; } = "Waiting for the first frame";

	/// <summary>Framework thread, null without DXVK</summary>
	public static VkCapture? TryCreate()
	{
		if (!IsWine || Marshal.QueryInterface((nint)Device.Instance()->D3D11Forwarder, in InteropDeviceId, out nint interop) != 0)
			return null;

		try
		{
			return new VkCapture(interop);
		}
		catch (Exception e)
		{
			Service.PluginLog.Error(e, "Vulkan capture setup failed");
			Marshal.Release(interop);
			return null;
		}
	}

	private VkCapture(nint interop)
	{
		this.interop = interop;
		nint instance, physicalDevice, device, queue;
		uint family;
		void** vtable = *(void***)interop;
		((delegate* unmanaged[Stdcall]<nint, nint*, nint*, nint*, void>)vtable[3])(interop, &instance, &physicalDevice, &device);
		((delegate* unmanaged[Stdcall]<nint, nint*, uint*, void>)vtable[4])(interop, &queue, &family);

		// Same loader DXVK uses, so the handles and the hooked entry point are DXVK's own
		Check(vkInitialize("vulkan-1.dll"), "vkInitialize");
		this.instanceApi = new VkInstanceApi(new VkInstance(instance));
		this.api = new VkDeviceApi(this.instanceApi, new VkDevice(device));
		this.physicalDevice = new VkPhysicalDevice(physicalDevice);
		this.queue = new VkQueue(queue);

		// Never shown: obs-vkcapture copies the image inside the present call
		this.window = CreateWindowExW(0, "STATIC", null, WsPopup, 0, 0, 1, 1, 0, 0, GetModuleHandleW(null), 0);
		if (this.window == 0)
			throw new InvalidOperationException("CreateWindowEx failed");

		VkWin32SurfaceCreateInfoKHR surfaceInfo = new() { hinstance = GetModuleHandleW(null), hwnd = this.window };
		Check(this.instanceApi.vkCreateWin32SurfaceKHR(&surfaceInfo, out this.surface), "vkCreateWin32SurfaceKHR");
		Check(this.instanceApi.vkGetPhysicalDeviceSurfaceSupportKHR(this.physicalDevice, family, this.surface, out VkBool32 supported),
			"vkGetPhysicalDeviceSurfaceSupportKHR");
		if (!supported)
			throw new InvalidOperationException("DXVK's queue can't present to a window");

		Check(this.api.vkCreateCommandPool(VkCommandPoolCreateFlags.ResetCommandBuffer, family, out this.pool), "vkCreateCommandPool");
		Check(this.api.vkCreateSemaphore(out this.spare), "vkCreateSemaphore");

		this.presentHook = Service.GameInteropProvider.HookFromAddress<QueuePresentDelegate>(
			(nint)this.api.vkQueuePresentKHR_ptr.Value, this.PresentDetour);
		this.presentHook.Enable();
	}

	/// <summary>Framework thread: the window owns the swapchain size</summary>
	public void Update(uint width, uint height)
	{
		if (width == 0 || height == 0)
			return;

		bool resized = width != this.windowWidth || height != this.windowHeight;
		if (!resized && !this.outOfDate && this.swapchain.IsNotNull)
			return;

		if (resized)
		{
			SetWindowPos(this.window, 0, 0, 0, (int)width, (int)height, SwpNoMove | SwpNoZOrder | SwpNoActivate);
			this.windowWidth = width;
			this.windowHeight = height;
		}

		this.LockQueue(() =>
		{
			try
			{
				this.CreateSwapchain(width, height);
				this.outOfDate = false;
			}
			catch (Exception e)
			{
				this.DestroySwapchain();
				this.Status = $"Vulkan swapchain failed: {e.Message}";
				Service.PluginLog.Error(e, "Vulkan capture swapchain failed");
			}
		});
	}

	/// <summary>Render thread, null before the output is released</summary>
	public void SetSource(nint texture)
	{
		if (texture == 0)
		{
			this.LockQueue(() => this.source = default);
			return;
		}

		if (Marshal.QueryInterface(texture, in InteropSurfaceId, out nint surface) != 0)
			return;

		VkImage image;
		VkImageLayout layout;
		VkImageCreateInfo info = new();
		int result = ((delegate* unmanaged[Stdcall]<nint, VkImage*, VkImageLayout*, VkImageCreateInfo*, int>)(*(void***)surface)[4])(
			surface, &image, &layout, &info);
		Marshal.Release(surface);
		if (result < 0)
			return;

		lock (this.sync)
		{
			this.source = image;
			this.sourceLayout = layout;
			this.sourceExtent = info.extent;
		}
	}

	/// <summary>Submission thread with DXVK's queue lock held: DXVK presents inside it</summary>
	private VkResult PresentDetour(nint queue, VkPresentInfoKHR* info)
	{
		try
		{
			if (queue == this.queue.Handle && info->swapchainCount == 1 && this.TryPresent(info, out VkResult result))
				return result;
		}
		catch (Exception e)
		{
			if (!this.logged)
				Service.PluginLog.Error(e, "Vulkan capture present failed");
			this.logged = true;
		}

		return this.presentHook.Original(queue, info);
	}

	private bool TryPresent(VkPresentInfoKHR* info, out VkResult result)
	{
		result = VkResult.Success;

		// Every per swapchain extension struct needs a second entry, ours first
		Two<ulong> ids = default, ids2 = default;
		Two<VkFence> fences = default;
		Two<VkPresentModeKHR> modes = default;
		Two<VkPresentRegionKHR> regions = default;
		Two<VkPresentTimingInfoEXT> timings = default;
		VkPresentIdKHR presentId = default;
		VkPresentId2KHR presentId2 = default;
		VkSwapchainPresentFenceInfoKHR fenceInfo = default;
		VkSwapchainPresentModeInfoKHR modeInfo = default;
		VkPresentRegionsKHR regionInfo = default;
		VkPresentTimingsInfoEXT timingsInfo = default;
		VkBaseInStructure* head = null;
		VkBaseInStructure** link = &head;
		int seen = 0;

		for (VkBaseInStructure* next = (VkBaseInStructure*)info->pNext; next != null; next = next->pNext)
		{
			VkBaseInStructure* copy;
			int bit;
			switch (next->sType)
			{
				case VkStructureType.PresentIdKHR:
					presentId = *(VkPresentIdKHR*)next;
					ids[1] = presentId.pPresentIds == null ? 0 : presentId.pPresentIds[0];
					presentId.pPresentIds = (ulong*)&ids;
					copy = (VkBaseInStructure*)&presentId;
					bit = 1;
					break;
				case VkStructureType.PresentId2KHR:
					presentId2 = *(VkPresentId2KHR*)next;
					ids2[1] = presentId2.pPresentIds == null ? 0 : presentId2.pPresentIds[0];
					presentId2.pPresentIds = (ulong*)&ids2;
					copy = (VkBaseInStructure*)&presentId2;
					bit = 2;
					break;
				case VkStructureType.SwapchainPresentFenceInfoKHR:
					fenceInfo = *(VkSwapchainPresentFenceInfoKHR*)next;
					fences[1] = fenceInfo.pFences[0];
					fenceInfo.pFences = (VkFence*)&fences;
					copy = (VkBaseInStructure*)&fenceInfo;
					bit = 4;
					break;
				case VkStructureType.SwapchainPresentModeInfoKHR:
					modeInfo = *(VkSwapchainPresentModeInfoKHR*)next;
					modes[0] = this.presentMode;
					modes[1] = modeInfo.pPresentModes[0];
					modeInfo.pPresentModes = (VkPresentModeKHR*)&modes;
					copy = (VkBaseInStructure*)&modeInfo;
					bit = 8;
					break;
				case VkStructureType.PresentRegionsKHR:
					regionInfo = *(VkPresentRegionsKHR*)next;
					regions[1] = regionInfo.pRegions == null ? default : regionInfo.pRegions[0];
					regionInfo.pRegions = (VkPresentRegionKHR*)&regions;
					copy = (VkBaseInStructure*)&regionInfo;
					bit = 16;
					break;
				case VkStructureType.PresentTimingsInfoEXT:
					timingsInfo = *(VkPresentTimingsInfoEXT*)next;
					timings[0] = new VkPresentTimingInfoEXT();
					timings[1] = timingsInfo.pTimingInfos[0];
					timingsInfo.pTimingInfos = (VkPresentTimingInfoEXT*)&timings;
					copy = (VkBaseInStructure*)&timingsInfo;
					bit = 32;
					break;
				default:
					// Unknown layout, could be per swapchain: present alone, capture restarts
					if (!this.logged)
						Service.PluginLog.Warning($"Vulkan capture: unknown present struct {next->sType}, presenting without it");
					this.logged = true;
					return false;
			}

			if ((seen & bit) != 0 || ((uint*)copy)[4] != 1) // swapchainCount follows sType and pNext
				return false;
			seen |= bit;
			((uint*)copy)[4] = 2;
			*link = copy;
			link = &copy->pNext;
		}

		*link = null;

		lock (this.sync)
		{
			if (this.swapchain.IsNull || this.source.IsNull)
				return false;

			uint index;
			VkResult acquire = this.api.vkAcquireNextImageKHR(this.swapchain, 0, this.spare, VkFence.Null, &index);
			if (acquire is not (VkResult.Success or VkResult.SuboptimalKHR))
			{
				this.outOfDate |= acquire == VkResult.ErrorOutOfDateKHR;
				return false;
			}

			(this.spare, this.acquired[index]) = (this.acquired[index], this.spare);
			VkFence fence = this.fences[index];
			this.api.vkWaitForFences(1, &fence, true, ulong.MaxValue);
			this.api.vkResetFences(1, &fence);
			this.RecordCopy(index);

			VkSemaphore wait = this.acquired[index], signal = this.copied[index];
			VkCommandBuffer command = this.commands[index];
			VkPipelineStageFlags stage = VkPipelineStageFlags.Transfer;
			VkSubmitInfo submit = new()
			{
				waitSemaphoreCount = 1, pWaitSemaphores = &wait, pWaitDstStageMask = &stage,
				commandBufferCount = 1, pCommandBuffers = &command,
				signalSemaphoreCount = 1, pSignalSemaphores = &signal,
			};
			Check(this.api.vkQueueSubmit(this.queue, 1, &submit, fence), "vkQueueSubmit");

			int waitCount = (int)info->waitSemaphoreCount + 1;
			VkSemaphore* waits = stackalloc VkSemaphore[waitCount];
			for (int i = 0; i < waitCount - 1; i++)
				waits[i] = info->pWaitSemaphores[i];
			waits[waitCount - 1] = signal;

			Two<VkSwapchainKHR> swapchains = default;
			Two<uint> indices = default;
			Two<VkResult> results = default;
			swapchains[0] = this.swapchain;
			swapchains[1] = info->pSwapchains[0];
			indices[0] = index;
			indices[1] = info->pImageIndices[0];
			results[1] = NotWritten;

			VkPresentInfoKHR both = *info;
			both.pNext = head;
			both.waitSemaphoreCount = (uint)waitCount;
			both.pWaitSemaphores = waits;
			both.swapchainCount = 2;
			both.pSwapchains = (VkSwapchainKHR*)&swapchains;
			both.pImageIndices = (uint*)&indices;
			both.pResults = (VkResult*)&results;
			VkResult all = this.presentHook.Original(this.queue.Handle, &both);

			this.outOfDate |= results[0] is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR;
			// DXVK only learns about its own swapchain; device errors leave pResults alone
			result = results[1] == NotWritten ? all : results[1];
			if (info->pResults != null)
				info->pResults[0] = result;
			this.Status = results[0] >= 0 ? "Sending to obs-vkcapture" : $"Vulkan present failed: {results[0]}";
			return true;
		}
	}

	private void RecordCopy(uint index)
	{
		VkCommandBuffer command = this.commands[index];
		VkImage target = this.images[index];
		VkImageSubresourceRange range = new(VkImageAspectFlags.Color, 0, 1, 0, 1);
		this.api.vkResetCommandBuffer(command, 0);
		this.api.vkBeginCommandBuffer(command, VkCommandBufferUsageFlags.OneTimeSubmit);

		// DXVK leaves the image in its default layout between submissions, the blur's writes precede this in queue order
		VkImageMemoryBarrier* before = stackalloc VkImageMemoryBarrier[2];
		before[0] = new VkImageMemoryBarrier(this.source, range, VkAccessFlags.MemoryWrite, VkAccessFlags.TransferRead,
			this.sourceLayout, VkImageLayout.TransferSrcOptimal, VK_QUEUE_FAMILY_IGNORED, VK_QUEUE_FAMILY_IGNORED, null);
		before[1] = new VkImageMemoryBarrier(target, range, VkAccessFlags.None, VkAccessFlags.TransferWrite,
			VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal, VK_QUEUE_FAMILY_IGNORED, VK_QUEUE_FAMILY_IGNORED, null);
		this.api.vkCmdPipelineBarrier(command, VkPipelineStageFlags.AllCommands, VkPipelineStageFlags.Transfer, 0, 0, null, 0, null, 2, before);

		VkImageBlit blit = new()
		{
			srcSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
			dstSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
		};
		blit.srcOffsets[1] = new VkOffset3D((int)this.sourceExtent.width, (int)this.sourceExtent.height, 1);
		blit.dstOffsets[1] = new VkOffset3D((int)this.swapExtent.width, (int)this.swapExtent.height, 1);
		this.api.vkCmdBlitImage(command, this.source, VkImageLayout.TransferSrcOptimal, target, VkImageLayout.TransferDstOptimal, 1, &blit,
			VkFilter.Linear);

		VkImageMemoryBarrier* after = stackalloc VkImageMemoryBarrier[2];
		after[0] = new VkImageMemoryBarrier(this.source, range, VkAccessFlags.None, VkAccessFlags.MemoryRead | VkAccessFlags.MemoryWrite,
			VkImageLayout.TransferSrcOptimal, this.sourceLayout, VK_QUEUE_FAMILY_IGNORED, VK_QUEUE_FAMILY_IGNORED, null);
		after[1] = new VkImageMemoryBarrier(target, range, VkAccessFlags.TransferWrite, VkAccessFlags.None,
			VkImageLayout.TransferDstOptimal, VkImageLayout.PresentSrcKHR, VK_QUEUE_FAMILY_IGNORED, VK_QUEUE_FAMILY_IGNORED, null);
		this.api.vkCmdPipelineBarrier(command, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.AllCommands, 0, 0, null, 0, null, 2, after);
		this.api.vkEndCommandBuffer(command);
	}

	/// <summary>Queue locked and idle</summary>
	private void CreateSwapchain(uint width, uint height)
	{
		Check(this.instanceApi.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(this.physicalDevice, this.surface, out VkSurfaceCapabilitiesKHR caps),
			"vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
		VkExtent2D extent = caps.currentExtent.width == uint.MaxValue ? new VkExtent2D(width, height) : caps.currentExtent;
		if (extent.width == 0 || extent.height == 0)
		{
			this.DestroySwapchain();
			return;
		}

		// Hidden windows get no vblank: FIFO could wait forever inside the game's present
		uint modeCount;
		this.instanceApi.vkGetPhysicalDeviceSurfacePresentModesKHR(this.physicalDevice, this.surface, &modeCount, null);
		VkPresentModeKHR* modes = stackalloc VkPresentModeKHR[(int)modeCount];
		this.instanceApi.vkGetPhysicalDeviceSurfacePresentModesKHR(this.physicalDevice, this.surface, &modeCount, modes);
		VkPresentModeKHR? mode = null;
		for (int i = 0; i < modeCount; i++)
		{
			if (modes[i] == VkPresentModeKHR.Mailbox || (modes[i] == VkPresentModeKHR.Immediate && mode == null))
				mode = modes[i];
		}

		if (mode == null)
			throw new InvalidOperationException("No mailbox or immediate present mode");

		uint formatCount;
		this.instanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(this.physicalDevice, this.surface, &formatCount, null);
		if (formatCount == 0)
			throw new InvalidOperationException("No surface formats");
		VkSurfaceFormatKHR* formats = stackalloc VkSurfaceFormatKHR[(int)formatCount];
		this.instanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(this.physicalDevice, this.surface, &formatCount, formats);
		VkSurfaceFormatKHR format = formats[0];
		for (int i = 0; i < formatCount; i++)
		{
			if (formats[i].format is VkFormat.B8G8R8A8Unorm or VkFormat.R8G8B8A8Unorm)
				format = formats[i];
		}

		uint imageCount = Math.Max(caps.minImageCount + 1, 3);
		if (caps.maxImageCount != 0)
			imageCount = Math.Min(imageCount, caps.maxImageCount);

		VkSwapchainKHR old = this.swapchain;
		VkSwapchainCreateInfoKHR create = new()
		{
			surface = this.surface,
			minImageCount = imageCount,
			imageFormat = format.format,
			imageColorSpace = format.colorSpace,
			imageExtent = extent,
			imageArrayLayers = 1,
			imageUsage = VkImageUsageFlags.TransferDst,
			imageSharingMode = VkSharingMode.Exclusive,
			preTransform = caps.currentTransform,
			compositeAlpha = (caps.supportedCompositeAlpha & VkCompositeAlphaFlagsKHR.Opaque) != 0
				? VkCompositeAlphaFlagsKHR.Opaque
				: VkCompositeAlphaFlagsKHR.Inherit,
			presentMode = mode.Value,
			clipped = true,
			oldSwapchain = old,
		};
		VkSwapchainKHR created;
		VkResult result = this.api.vkCreateSwapchainKHR(&create, &created);
		this.DestroySwapchain();
		Check(result, "vkCreateSwapchainKHR");

		this.swapchain = created;
		this.presentMode = mode.Value;
		this.swapExtent = extent;
		uint count;
		this.api.vkGetSwapchainImagesKHR(created, &count, null);
		this.images = new VkImage[count];
		fixed (VkImage* images = this.images)
			this.api.vkGetSwapchainImagesKHR(created, &count, images);

		this.commands = new VkCommandBuffer[count];
		VkCommandBufferAllocateInfo allocate = new() { commandPool = this.pool, level = VkCommandBufferLevel.Primary, commandBufferCount = count };
		fixed (VkCommandBuffer* commands = this.commands)
			Check(this.api.vkAllocateCommandBuffers(&allocate, commands), "vkAllocateCommandBuffers");

		this.fences = new VkFence[count];
		this.acquired = new VkSemaphore[count];
		this.copied = new VkSemaphore[count];
		for (int i = 0; i < count; i++)
		{
			this.api.vkCreateFence(VkFenceCreateFlags.Signaled, out this.fences[i]);
			this.api.vkCreateSemaphore(out this.acquired[i]);
			this.api.vkCreateSemaphore(out this.copied[i]);
		}

		this.Status = $"Ready for obs-vkcapture ({extent.width}x{extent.height}, {mode.Value})";
	}

	/// <summary>Queue locked and idle</summary>
	private void DestroySwapchain()
	{
		foreach (VkFence fence in this.fences)
			this.api.vkDestroyFence(fence);
		foreach (VkSemaphore semaphore in this.acquired)
			this.api.vkDestroySemaphore(semaphore);
		foreach (VkSemaphore semaphore in this.copied)
			this.api.vkDestroySemaphore(semaphore);
		if (this.commands.Length > 0)
		{
			fixed (VkCommandBuffer* commands = this.commands)
				this.api.vkFreeCommandBuffers(this.pool, (uint)this.commands.Length, commands);
		}
		if (this.swapchain.IsNotNull)
			this.api.vkDestroySwapchainKHR(this.swapchain);

		this.swapchain = VkSwapchainKHR.Null;
		this.images = [];
		this.commands = [];
		this.fences = [];
		this.acquired = [];
		this.copied = [];
	}

	/// <summary>DXVK's queue lock first, then ours: the detour runs in that order</summary>
	private void LockQueue(Action action)
	{
		void** vtable = *(void***)this.interop;
		((delegate* unmanaged[Stdcall]<nint, void>)vtable[7])(this.interop);
		try
		{
			lock (this.sync)
			{
				this.api.vkQueueWaitIdle(this.queue);
				action();
			}
		}
		finally
		{
			((delegate* unmanaged[Stdcall]<nint, void>)vtable[8])(this.interop);
		}
	}

	private static void Check(VkResult result, string call)
	{
		if (result < 0)
			throw new InvalidOperationException($"{call}: {result}");
	}

	/// <summary>Framework thread, the window belongs to it</summary>
	public void Dispose()
	{
		// No present is in flight while DXVK's queue is locked
		this.presentHook.Dispose();
		this.LockQueue(() =>
		{
			this.DestroySwapchain();
			this.api.vkDestroySemaphore(this.spare);
			this.api.vkDestroyCommandPool(this.pool);
			this.instanceApi.vkDestroySurfaceKHR(this.surface);
		});
		DestroyWindow(this.window);
		Marshal.Release(this.interop);
	}

	private const uint WsPopup = 0x80000000;
	private const uint SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

	[LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
	private static partial nint CreateWindowExW(uint exStyle, string className, string? windowName, uint style, int x, int y, int width,
		int height, nint parent, nint menu, nint instance, nint param);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool DestroyWindow(nint window);

	[LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
	private static partial nint GetModuleHandleW(string? name);
}
