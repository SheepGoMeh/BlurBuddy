using System;
using System.Collections.Generic;

namespace BlurBuddy.Tracking;

public readonly record struct ScreenRect(float Left, float Top, float Right, float Bottom)
{
	public static ScreenRect Empty => new(0, 0, 0, 0);

	public bool IsEmpty => this.Right <= this.Left || this.Bottom <= this.Top;

	public float Area => this.IsEmpty ? 0 : (this.Right - this.Left) * (this.Bottom - this.Top);

	public ScreenRect Union(ScreenRect other)
	{
		if (this.IsEmpty)
			return other;
		if (other.IsEmpty)
			return this;
		return new ScreenRect(Math.Min(this.Left, other.Left), Math.Min(this.Top, other.Top), Math.Max(this.Right, other.Right),
			Math.Max(this.Bottom, other.Bottom));
	}

	public ScreenRect Inflate(float amount) => this.IsEmpty
		? this
		: new ScreenRect(this.Left - amount, this.Top - amount, this.Right + amount, this.Bottom + amount);

	public ScreenRect Clamp(float width, float height) => new(Math.Clamp(this.Left, 0, width), Math.Clamp(this.Top, 0, height),
		Math.Clamp(this.Right, 0, width), Math.Clamp(this.Bottom, 0, height));

	public ScreenRect Intersect(ScreenRect other) => new(Math.Max(this.Left, other.Left), Math.Max(this.Top, other.Top),
		Math.Min(this.Right, other.Right), Math.Min(this.Bottom, other.Bottom));

	public bool IsNear(ScreenRect other, float gap) =>
		this.Left <= other.Right + gap && other.Left <= this.Right + gap &&
		this.Top <= other.Bottom + gap && other.Top <= this.Bottom + gap;

	/// <summary>Joins rectangles closer than gap until none are, distant pieces stay separate</summary>
	// ponytail: quadratic per pass, fine for an addon's few dozen nodes
	public static List<ScreenRect> Merge(List<ScreenRect> rects, float gap)
	{
		List<ScreenRect> merged = [.. rects];
		bool joined = true;
		while (joined)
		{
			joined = false;
			for (int i = 0; i < merged.Count && !joined; i++)
			{
				for (int j = i + 1; j < merged.Count; j++)
				{
					if (!merged[i].IsNear(merged[j], gap))
						continue;

					merged[i] = merged[i].Union(merged[j]);
					merged.RemoveAt(j);
					joined = true;
					break;
				}
			}
		}

		return merged;
	}
}
