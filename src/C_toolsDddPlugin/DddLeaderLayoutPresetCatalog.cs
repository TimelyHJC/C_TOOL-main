using System.Collections.Generic;
using C_toolsPlugin;

namespace C_toolsDddPlugin;

internal sealed class DddLeaderLayoutPreset
{
    private readonly System.Action<MLeaderToolSettingsDto> _apply;

    internal DddLeaderLayoutPreset(
        string id,
        string name,
        string description,
        System.Action<MLeaderToolSettingsDto> apply)
    {
        Id = id;
        Name = name;
        Description = description;
        _apply = apply;
    }

    internal string Id { get; }

    internal string Name { get; }

    internal string Description { get; }

    internal void ApplyTo(MLeaderToolSettingsDto dto)
    {
        DddLeaderLayoutPresetCatalog.ResetLayoutFields(dto);
        dto.LayoutPresetId = Id;
        _apply(dto);
    }

    public override string ToString() => Name;
}

internal static class DddLeaderLayoutPresetCatalog
{
    private static readonly IReadOnlyList<DddLeaderLayoutPreset> Presets =
    [
        new(
            "follow-style",
            "跟随当前样式",
            "不做额外覆盖，完全沿用当前 CMLEADERSTYLE。",
            static _ => { }),
        new(
            "middle-standard",
            "中线标准",
            "中线连接，适合常规说明文字。",
            static dto =>
            {
                dto.TextAttachmentDirection = "AttachmentHorizontal";
                dto.LeftTextAttachmentType = "AttachmentMiddle";
                dto.RightTextAttachmentType = "AttachmentMiddle";
                dto.EnableLanding = true;
                dto.EnableDogleg = true;
                dto.DoglegLength = 10;
                dto.LandingGap = 2;
            }),
        new(
            "top-note",
            "顶线对齐",
            "文字顶部连接，适合贴边说明。",
            static dto =>
            {
                dto.TextAttachmentDirection = "AttachmentHorizontal";
                dto.LeftTextAttachmentType = "AttachmentTopOfTop";
                dto.RightTextAttachmentType = "AttachmentTopOfTop";
                dto.EnableLanding = true;
                dto.EnableDogleg = true;
                dto.DoglegLength = 12;
                dto.LandingGap = 2.5;
            }),
        new(
            "bottom-note",
            "底线对齐",
            "文字底线连接，适合清单与编号。",
            static dto =>
            {
                dto.TextAttachmentDirection = "AttachmentHorizontal";
                dto.LeftTextAttachmentType = "AttachmentBottomLine";
                dto.RightTextAttachmentType = "AttachmentBottomLine";
                dto.EnableLanding = true;
                dto.EnableDogleg = true;
                dto.DoglegLength = 10;
                dto.LandingGap = 1.5;
            }),
        new(
            "long-dogleg",
            "加长基线",
            "延长着陆线，适合大段说明或远距离引注。",
            static dto =>
            {
                dto.TextAttachmentDirection = "AttachmentHorizontal";
                dto.LeftTextAttachmentType = "AttachmentMiddleOfTop";
                dto.RightTextAttachmentType = "AttachmentMiddleOfTop";
                dto.EnableLanding = true;
                dto.EnableDogleg = true;
                dto.DoglegLength = 20;
                dto.LandingGap = 4;
            }),
        new(
            "direct-note",
            "直连无狗腿",
            "关闭着陆线与狗腿，适合简洁点注。",
            static dto =>
            {
                dto.TextAttachmentDirection = "AttachmentHorizontal";
                dto.LeftTextAttachmentType = "AttachmentMiddle";
                dto.RightTextAttachmentType = "AttachmentMiddle";
                dto.EnableLanding = false;
                dto.EnableDogleg = false;
            })
    ];

    internal static IReadOnlyList<DddLeaderLayoutPreset> List() => Presets;

    internal static DddLeaderLayoutPreset Default => Presets[0];

    internal static DddLeaderLayoutPreset? FindById(string? id)
    {
        var key = (id ?? "").Trim();
        if (key.Length == 0)
            return null;
        foreach (var preset in Presets)
        {
            if (string.Equals(preset.Id, key, System.StringComparison.OrdinalIgnoreCase))
                return preset;
        }

        return null;
    }

    internal static void ResetLayoutFields(MLeaderToolSettingsDto dto)
    {
        dto.LayoutPresetId = "";
        dto.TextAttachmentDirection = "";
        dto.LeftTextAttachmentType = "";
        dto.RightTextAttachmentType = "";
        dto.EnableLanding = null;
        dto.EnableDogleg = null;
        dto.DoglegLength = 0;
        dto.LandingGap = 0;
        dto.MaxLeaderSegmentsPoints = 0;
    }
}
