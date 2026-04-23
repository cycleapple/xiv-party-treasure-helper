using System;
using System.Security.Cryptography;

namespace XivTreasureParty.Party;

public static class PartyCodeGenerator
{
    private const string CodeChars = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int CodeLength = 8;

    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[CodeLength];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[CodeLength];
        for (var i = 0; i < CodeLength; i++)
            chars[i] = CodeChars[bytes[i] % CodeChars.Length];
        return new string(chars);
    }

    public static bool IsValidFormat(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length != CodeLength) return false;
        foreach (var c in code.ToUpperInvariant())
        {
            if (!IsAllowedChar(c)) return false;
        }
        return true;
    }

    private static bool IsAllowedChar(char c)
    {
        if (c is >= 'A' and <= 'Z') return c is not ('I' or 'L' or 'O');
        if (c is >= '2' and <= '9') return true;
        return false;
    }
}
