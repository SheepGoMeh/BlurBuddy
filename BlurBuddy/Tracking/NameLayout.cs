using System;

namespace BlurBuddy.Tracking;

/// <summary>
/// The nameplate text line that identifies a player: byte range, index and line count.
/// Titles, owners and retainer / mannequin names are wrapped in 《 》 on their own line; players' line holds the name and FC tag.
/// </summary>
public readonly record struct NameLayout(int Start, int End, int Index, int Count)
{
	private const byte NewLine = 0x0A;
	private static ReadOnlySpan<byte> Open => [0xE3, 0x80, 0x8A]; // 《
	private static ReadOnlySpan<byte> Close => [0xE3, 0x80, 0x8B]; // 》

	/// <summary>
	/// Players: the line that is not a 《title》. Labelled plates (retainers, mannequins, minions, pets): the 《name》 line.
	/// Null for single line plates or when no such line exists.
	/// </summary>
	public static NameLayout? IdentifyingLine(ReadOnlySpan<byte> text, bool ownedObject)
	{
		int count = text.Count(NewLine) + 1;
		if (count < 2)
			return null;

		int start = 0;
		for (int index = 0; index < count; index++)
		{
			int length = text[start..].IndexOf(NewLine);
			int end = length < 0 ? text.Length : start + length;
			if (IsBracketed(text[start..end]) == ownedObject)
				return new NameLayout(start, end, index, count);
			start = end + 1;
		}

		return null;
	}

	private static bool IsBracketed(ReadOnlySpan<byte> line)
	{
		line = line.Trim((byte)' ');
		return line.StartsWith(Open) && line.EndsWith(Close);
	}
}
