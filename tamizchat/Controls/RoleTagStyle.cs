using System.Text.Json;
using System.Text.Json.Serialization;

namespace TamizChat.Controls;

/// <summary>How a role's tag is filled.</summary>
public enum TagPattern
{
    Solid,
    Gradient,
    Sunset,
    Stripes,
    Glass,
    Outline,
    Metal,
}

/// <summary>The tag's silhouette.</summary>
public enum TagShape
{
    Pill,
    Rounded,
    Square,
    Cut,
}

/// <summary>What the tag does while it sits there.</summary>
public enum TagAnimation
{
    None,
    Pulse,
    Shimmer,
    Glow,
    Rainbow,
    Float,
}

/// <summary>
/// The look of a role's tag.
///
/// This is what goes into the role's `tag_style` on the server, which stores it
/// as opaque text and never interprets it. That is deliberate: adding a new
/// pattern or animation is a client release, not a protocol change and a
/// migration.
///
/// Everything has a default, and an unknown value falls back rather than
/// throwing — a client that meets a style written by a newer one should show a
/// plain tag, not an error.
/// </summary>
public sealed class RoleTagStyle
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    [JsonPropertyName("bg")]
    public string Background { get; set; } = "#7289da";

    /// <summary>The second colour, for the patterns that use two.</summary>
    [JsonPropertyName("bg2")]
    public string BackgroundTwo { get; set; } = "#5b6eae";

    /// <summary>Empty means "pick black or white by contrast", which is usually right.</summary>
    [JsonPropertyName("fg")]
    public string Foreground { get; set; } = "";

    [JsonPropertyName("pattern")]
    public TagPattern Pattern { get; set; } = TagPattern.Solid;

    [JsonPropertyName("shape")]
    public TagShape Shape { get; set; } = TagShape.Pill;

    [JsonPropertyName("animation")]
    public TagAnimation Animation { get; set; } = TagAnimation.None;

    /// <summary>An optional Segoe Fluent glyph shown before the name.</summary>
    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "";

    [JsonPropertyName("bold")]
    public bool Bold { get; set; } = true;

    /// <summary>
    /// Reads a stored style. Anything unreadable comes back as the default built
    /// from the role's plain colour, so an old role with only `color` set still
    /// looks like itself.
    /// </summary>
    public static RoleTagStyle Parse(string? json, string fallbackColour)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<RoleTagStyle>(json, Options);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
                // Fall through to the colour-only default.
            }
        }

        var colour = string.IsNullOrWhiteSpace(fallbackColour) ? "#7289da" : fallbackColour;
        return new RoleTagStyle { Background = colour, BackgroundTwo = colour };
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public RoleTagStyle Clone() => Parse(ToJson(), Background);
}
