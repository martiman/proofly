using System.Text;

namespace Proofly.Domain.Enums;

/// <summary>Converts enum members to contract wire names.</summary>
public static class WireNames
{
    public static string ToUpperSnake<T>(T value) where T : struct, Enum => Snake(value.ToString(), '_', upper: true);
    public static string ToLowerSnake<T>(T value) where T : struct, Enum => Snake(value.ToString(), '_', upper: false);

    /// <summary>Permission wire form, e.g. SafeguardingAdjustmentPropose -> safeguarding.adjustment.propose.</summary>
    public static string ToPermission(Permission p) => Snake(p.ToString(), '.', upper: false);

    private static string Snake(string name, char sep, bool upper)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) { sb.Append(sep); }
            sb.Append(upper ? char.ToUpperInvariant(name[i]) : char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}
