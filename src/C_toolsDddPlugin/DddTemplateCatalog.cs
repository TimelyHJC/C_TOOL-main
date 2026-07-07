using System.Collections.Generic;
using System.Linq;

namespace C_toolsDddPlugin;

internal enum DddPanelListKind
{
    Remarks = 0,
    Props = 1,
    Materials = 2
}

internal sealed class DddListTemplatePreset
{
    internal DddListTemplatePreset(
        DddPanelListKind kind,
        string name,
        string description,
        IReadOnlyList<string[]> rows)
    {
        Kind = kind;
        Name = name;
        Description = description;
        Rows = rows;
    }

    internal DddPanelListKind Kind { get; }

    internal string Name { get; }

    internal string Description { get; }

    internal IReadOnlyList<string[]> Rows { get; }

    public override string ToString() => Name;
}

internal static class DddTemplateCatalog
{
    private static readonly IReadOnlyList<DddListTemplatePreset> AllPresets =
    [
        new(
            DddPanelListKind.Remarks,
            "施工说明（通用）",
            "通用施工与节点说明。",
            [
                ["尺寸以现场复尺为准"],
                ["未尽事宜按现场条件调整"],
                ["详见节点大样"],
                ["安装完成后复核标高"]
            ]),
        new(
            DddPanelListKind.Remarks,
            "审核复核",
            "图纸会签与复核常用语。",
            [
                ["甲方确认"],
                ["设计复核"],
                ["尺寸待确认"],
                ["现场条件待确认"]
            ]),
        new(
            DddPanelListKind.Remarks,
            "安全与保护",
            "施工安全与成品保护提示。",
            [
                ["注意成品保护"],
                ["施工前请确认断电"],
                ["严禁踩踏"],
                ["需做防火封堵"]
            ]),
        new(
            DddPanelListKind.Remarks,
            "加工安装",
            "深化、放样与安装提醒。",
            [
                ["厂家深化后下单"],
                ["先放样后制作"],
                ["安装定位线复核"],
                ["拼缝方向统一"]
            ]),
        new(
            DddPanelListKind.Props,
            "报价占位",
            "道具清单常用报价占位项。",
            [
                ["主体道具", "￥0", "含安装"],
                ["辅材配件", "￥0", "按实计"],
                ["运输搬运", "￥0", "单列"],
                ["税费", "￥0", "如需开票"]
            ]),
        new(
            DddPanelListKind.Props,
            "制作状态",
            "道具制作进度与备注模板。",
            [
                ["主道具", "待确认", "尺寸待复核"],
                ["备用件", "待确认", "数量待确认"],
                ["安装件", "待确认", "现场配合"],
                ["收口件", "待确认", "跟随主体"]
            ]),
        new(
            DddPanelListKind.Props,
            "展示标签",
            "展示阶段常用道具标签。",
            [
                ["样品件", "暂定", "仅供确认"],
                ["正式件", "暂定", "以终稿为准"],
                ["替换件", "暂定", "现场备用"],
                ["返修件", "暂定", "待复检"]
            ]),
        new(
            DddPanelListKind.Materials,
            "板材常用",
            "板材与木作清单模板。",
            [
                ["夹板", "1220x2440", "E0"],
                ["密度板", "1220x2440", "喷漆基层"],
                ["饰面板", "按样板", "颜色待确认"],
                ["木方", "30x40", "基层"]
            ]),
        new(
            DddPanelListKind.Materials,
            "金属常用",
            "金属与收边常用模板。",
            [
                ["方管", "40x40x2.0", "黑铁"],
                ["扁钢", "40x4", "收边"],
                ["不锈钢板", "1.2mm", "拉丝"],
                ["圆钢", "Φ8", "连接件"]
            ]),
        new(
            DddPanelListKind.Materials,
            "软装与饰面",
            "软包、透光与饰面常用项。",
            [
                ["阻燃布", "门幅1500", "颜色待确认"],
                ["皮革", "按样板", "包覆"],
                ["亚克力", "5mm", "发光面"],
                ["PET板", "2mm", "图文贴面"]
            ])
    ];

    private static readonly IReadOnlyDictionary<DddPanelListKind, IReadOnlyList<DddListTemplatePreset>> PresetsByKind =
        AllPresets
            .GroupBy(static p => p.Kind)
            .ToDictionary(
                static g => g.Key,
                static g => (IReadOnlyList<DddListTemplatePreset>)g.ToList());

    internal static IReadOnlyList<DddListTemplatePreset> GetPresets(DddPanelListKind kind) =>
        PresetsByKind.TryGetValue(kind, out var list) ? list : [];
}
