using System;
using System.Collections.Generic;

namespace BlurBuddy.Tracking;

/// <summary>Blur or show an addon, a known part or a picked node; nested rules win, unlisted follows BlurUnlisted</summary>
public sealed class UiRule
{
	public string Addon = "";
	public string Part = ""; // empty: whole addon, "@Id": known part, "12/3/7": NodeId path
	public bool Blur;

	public bool IsModule => this.Part.Length == 0;
	public bool IsKnownPart => this.Part.StartsWith('@');
}

/// <summary>Rules of one addon, resolved for its tree walk</summary>
public sealed class AddonRules
{
	public bool Blur;
	public bool HasRules;
	public readonly Dictionary<ulong, bool> Nodes = [];
	public readonly List<(string Part, bool Blur)> KnownParts = [];

	/// <summary>No part or node rules</summary>
	public bool IsUniform => this.Nodes.Count == 0 && this.KnownParts.Count == 0;

	public void Resolve(IReadOnlyList<UiRule> rules, bool blurUnlisted, string addon)
	{
		this.Blur = blurUnlisted;
		this.HasRules = false;
		this.Nodes.Clear();
		this.KnownParts.Clear();
		foreach (UiRule rule in rules)
		{
			if (rule.Addon != addon)
				continue;

			this.HasRules = true;
			if (rule.IsModule)
				this.Blur = rule.Blur;
			else if (rule.IsKnownPart)
				this.KnownParts.Add((rule.Part, rule.Blur));
			else if (NodePath.TryParse(rule.Part, out ulong hash))
				this.Nodes[hash] = rule.Blur;
		}
	}
}

/// <summary>FNV-1a over NodeIds from the root down, through component roots</summary>
public static class NodePath
{
	public const ulong Root = 14695981039346656037;

	public static ulong Append(ulong hash, uint nodeId) => (hash ^ nodeId) * 1099511628211;

	public static bool TryParse(string path, out ulong hash)
	{
		hash = Root;
		foreach (Range range in path.AsSpan().Split('/'))
		{
			if (!uint.TryParse(path.AsSpan()[range], out uint nodeId))
				return false;
			hash = Append(hash, nodeId);
		}

		return true;
	}

	public static string Format(ReadOnlySpan<uint> nodeIds) => string.Join('/', nodeIds.ToArray());
}
